# -*- coding: utf-8 -*-
"""
Codex Usage Monitor — 在任务栏托盘 + 浮动窗口显示 Codex 的 5 小时 / 每周额度余量
数据来源: https://chatgpt.com/backend-api/wham/usage (与 Codex 官方客户端同一接口)
凭证: ~/.codex/auth.json (OAuth token, 过期自动用 refresh_token 续期)

启动: pythonw codex_tray.py  (无控制台窗口)
"""

import base64
import http.client
import json
import math
import os
import re
import shutil
import subprocess
import sys
import time
import threading
import traceback
import urllib.request
import urllib.error
from datetime import datetime, timedelta
from functools import lru_cache
from pathlib import Path

import windows_ui

# ----------------------------------------------------------------------------
# 配置
# ----------------------------------------------------------------------------
SCRIPT_DIR = Path(__file__).resolve().parent


def project_data_dir(script_dir):
    if script_dir.name.lower() == "python" and (script_dir.parent / "csharp").is_dir():
        return script_dir.parent
    return script_dir


DATA_DIR = project_data_dir(SCRIPT_DIR)
if getattr(sys, "frozen", False):
    exe_dir = Path(sys.executable).resolve().parent
    if (exe_dir / "codex_tray.py").is_file():
        DATA_DIR = project_data_dir(exe_dir)
    elif (exe_dir / "accounts").is_dir():
        DATA_DIR = exe_dir
    else:
        DATA_DIR = Path(os.environ["LOCALAPPDATA"]) / "CodexUsageMonitor"
    DATA_DIR.mkdir(parents=True, exist_ok=True)
CONFIG_PATH = os.path.join(DATA_DIR, "config.json")
LOG_PATH = os.path.join(DATA_DIR, "codex_tray.log")
ACCOUNTS_DIR = os.path.join(DATA_DIR, "accounts")
AUTOSTART_REG = r"Software\Microsoft\Windows\CurrentVersion\Run"
AUTOSTART_NAME = "CodexUsageTray"

POLL_INTERVAL = 60          # 秒, 默认刷新间隔 (可在设置面板调整)
API_USAGE = "https://chatgpt.com/backend-api/wham/usage"
API_RESET_CREDITS = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits"
API_REFRESH = "https://auth.openai.com/oauth/token"
CLIENT_ID = "app_EMoamEEZ73f0CkXaXp7hrann"   # Codex CLI 官方 client_id

# 配色 (深色卡片)
C_BG = "#17181d"
C_CARD = "#202128"
C_BORDER = "#2e3039"
C_TEXT = "#eceef3"
C_SUB = "#8e9099"
C_ACCENT = "#7cb8ff"
C_GREEN = "#3ddc84"
C_YELLOW = "#f5c542"
C_RED = "#ff5b5b"
C_TRACK = "#3a3b44"

ICON_FONT = r"C:\Windows\Fonts\arialbd.ttf"
FONT_SCALES = {"small": 1.0, "medium": 1.2, "large": 1.4}
USAGE_URL = "https://chatgpt.com/codex/settings/usage"
def version_label():
    state = _read_json(os.path.join(SCRIPT_DIR, "python-version.json"), {})
    return "版本 " + state.get("version", "0.0.1")

DEFAULT_CONFIG = {
    "alpha": 0.80,           # 窗口整体透明度 0.4-1.0
    "interval": 60,          # 采集间隔秒 (默认 60s)
    "window_x": None,        # None = 右下角自动定位
    "window_y": None,
    "display_uid": "",       # 当前显示哪个账号 (空 = 当前登录的那个)
    "view_mode": "single",   # single = 单账号双环详情 | all = 多账号总览
    "notify_low": True,      # 低额度时是否发送系统通知
    "notify_threshold": 20,  # 低额度提醒阈值 (%)
    "show_in_taskbar": True,
    "taskbar_offset_x": 0,
}

LOG_MAX_BYTES = 512 * 1024   # 日志最大 512KB, 超出后轮转到 .log.1
_LOG_LOCK = threading.Lock()


def log(msg):
    try:
        with _LOG_LOCK:
            if os.path.isfile(LOG_PATH) and os.path.getsize(LOG_PATH) >= LOG_MAX_BYTES:
                bak = LOG_PATH + ".1"
                try:
                    os.replace(LOG_PATH, bak)
                except OSError:
                    pass
            with open(LOG_PATH, "a", encoding="utf-8") as f:
                f.write("[%s] %s\n" % (datetime.now().strftime("%m-%d %H:%M:%S"), msg))
    except Exception:
        pass


def _read_json(path, default=None):
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {} if default is None else default


def _write_json_atomic(path, data, indent=None):
    tmp = "%s.%d.tmp" % (path, threading.get_ident())
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=indent)
    os.replace(tmp, path)


class AuthError(RuntimeError):
    """登录凭证失效 (被服务端作废 / 无 refresh_token) — 只能重新登录恢复"""


# 续期失败过的账号, 5 分钟内不再重复尝试 (避免把 token 链反复打废 + 刷 401)
_NO_REFRESH_UNTIL = {}
_AUTH_MTIME_SEEN = {}


def auth_mtime(home=None):
    """返回指定账号 auth.json 的修改时间, 不存在返回 0.0"""
    try:
        return os.path.getmtime(os.path.join(codex_home(home), "auth.json"))
    except OSError:
        return 0.0


def clear_refresh_cooldown(home=None):
    """清除某账号的续期冷却期 (例如检测到重新登录写入了新的 auth.json)"""
    _NO_REFRESH_UNTIL.pop(codex_home(home), None)


def http_detail(err):
    """读 HTTPError 里的服务端原因, 如 token_revoked / refresh_token_invalidated"""
    try:
        body = err.read().decode("utf-8", "replace")
        d = json.loads(body)
        e = d.get("error") or {}
        return "%s|%s" % (e.get("code") or "", e.get("message") or "")
    except Exception:
        return ""


def load_config():
    cfg = dict(DEFAULT_CONFIG)
    data = _read_json(CONFIG_PATH, {})
    if isinstance(data, dict):
        cfg.update(data)
    return cfg


def save_config(cfg):
    try:
        _write_json_atomic(CONFIG_PATH, cfg, indent=2)
    except Exception as e:
        log("save_config: %s" % e)


# ----------------------------------------------------------------------------
# 数据获取
# ----------------------------------------------------------------------------
def codex_home(home=None):
    """指定账号的配置目录; 不传则默认账号 (CODEX_HOME 或 ~/.codex)"""
    if home:
        return home
    return os.environ.get("CODEX_HOME") or os.path.join(os.path.expanduser("~"), ".codex")


def read_auth(home=None):
    path = os.path.join(codex_home(home), "auth.json")
    if not os.path.exists(path):
        return None
    return _read_json(path, None)


def jwt_claims(auth):
    """解 id_token 里的 claim, 失败返回 {}"""
    try:
        payload = auth["tokens"]["id_token"].split(".")[1]
        payload += "=" * (-len(payload) % 4)
        return json.loads(base64.urlsafe_b64decode(payload))
    except Exception:
        return {}


def auth_identity(home=None):
    """返回 (显示标识/邮箱, 账号唯一标识) — 优先使用完整邮箱"""
    auth = read_auth(home)
    if not auth:
        return "", ""
    c = jwt_claims(auth)
    nick = c.get("email") or c.get("name") or ""
    uid = c.get("sub") or auth.get("tokens", {}).get("account_id") or ""
    return nick, uid


def _slug(name):
    s = re.sub(r"[^0-9A-Za-z_\-\u4e00-\u9fa5]+", "_", (name or "").strip()).strip("_")
    return s[:32] or "account"


def _meta_path(home):
    return os.path.join(home, "meta.json")


def read_meta(home):
    data = _read_json(_meta_path(home), {})
    return data if isinstance(data, dict) else {}


def mark_auth_state(home, ok):
    """记录快照的登录状态: 凭证被作废时写 needs_login, 恢复后自动清掉

    只对 accounts/ 下的快照写文件, 不动 ~/.codex
    """
    if not home or not os.path.abspath(home).startswith(os.path.abspath(ACCOUNTS_DIR)):
        return
    meta = read_meta(home)
    if ok:
        if meta.pop("needs_login", None) is not None:
            meta.pop("bad_at", None)
            try:
                _write_json_atomic(_meta_path(home), meta)
                log("账号已恢复: %s" % home)
            except Exception as e:
                log("mark_auth_state 写入失败: %s" % e)
    elif not meta.get("needs_login"):
        meta["needs_login"] = True
        meta["bad_at"] = time.time()
        try:
            _write_json_atomic(_meta_path(home), meta)
        except Exception as e:
            log("mark_auth_state 写入失败: %s" % e)


def _is_pending_dir(name):
    return name.startswith("_pending_")


def _find_snapshot_dir(uid):
    if not uid or not os.path.isdir(ACCOUNTS_DIR):
        return None
    for name in sorted(os.listdir(ACCOUNTS_DIR)):
        if _is_pending_dir(name):
            continue
        d = os.path.join(ACCOUNTS_DIR, name)
        if not os.path.isdir(d):
            continue
        if auth_identity(d)[1] == uid or read_meta(d).get("uid") == uid:
            return d
    return None


def _ensure_snapshot_dir(nick, uid):
    dest = _find_snapshot_dir(uid)
    if dest is None:
        base = _slug(nick or uid[-8:])
        dest = os.path.join(ACCOUNTS_DIR, base)
        n = 2
        while os.path.isdir(dest) and auth_identity(dest)[1] not in ("", uid):
            dest = os.path.join(ACCOUNTS_DIR, "%s_%d" % (base, n))
            n += 1
    os.makedirs(dest, exist_ok=True)
    return dest


def _copy_file_atomic(src, dst):
    """原子复制文件, 避免写半截损坏 auth.json"""
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    tmp = "%s.%d.tmp" % (dst, threading.get_ident())
    shutil.copy2(src, tmp)
    os.replace(tmp, dst)


def list_accounts():
    """当前登录账号 + 已保存的账号快照

    返回 [{'nick','uid','home','live','label','auth_bad'}], 当前登录的排在最前
    """
    out, seen = [], set()
    live = codex_home()
    nick, uid = auth_identity(live)
    if uid:
        snap = _find_snapshot_dir(uid)
        live_label = read_meta(snap).get("label", "") if snap else ""
        out.append({"nick": nick or "当前登录", "uid": uid, "home": live,
                    "live": True, "label": live_label, "auth_bad": False})
        seen.add(uid)
    if os.path.isdir(ACCOUNTS_DIR):
        for name in sorted(os.listdir(ACCOUNTS_DIR)):
            if _is_pending_dir(name):
                continue
            home = os.path.join(ACCOUNTS_DIR, name)
            if not os.path.isfile(os.path.join(home, "auth.json")):
                continue
            n, u = auth_identity(home)
            if not u or u in seen:          # 与当前登录是同一个账号 → 不重复列出 (避免双份同时刷新)
                continue
            seen.add(u)
            m = read_meta(home)
            out.append({"nick": n or name, "uid": u, "home": home, "live": False,
                        "label": m.get("label", ""), "auth_bad": bool(m.get("needs_login"))})
    return out


def save_current_login():
    """把当前 ~/.codex 的登录存成独立快照, 返回 (ok, msg, uid)"""
    live = codex_home()
    src = os.path.join(live, "auth.json")
    nick, uid = auth_identity(live)
    if not uid or not os.path.isfile(src):
        return False, "当前没有检测到登录, 请先 codex login", ""
    dest = _ensure_snapshot_dir(nick, uid)
    _copy_file_atomic(src, os.path.join(dest, "auth.json"))
    meta = read_meta(dest)
    meta.update({"nick": nick, "uid": uid, "saved_at": time.time()})
    meta.pop("needs_login", None)          # 重新同步即视为凭证已刷新
    meta.pop("bad_at", None)
    _write_json_atomic(_meta_path(dest), meta)
    clear_refresh_cooldown(dest)
    log("已保存账号快照: %s -> %s" % (nick, dest))
    return True, "已保存 %s" % (meta.get("label") or nick), uid


def switch_cli_account(uid):
    """热切换 Codex CLI 当前登录账号 (替代 codex logout, 保留原账号授权不吊销)

    步骤:
    1. 将当前 ~/.codex/auth.json (含最新续期 token) 原子归档回原账号快照目录
    2. 将目标账号快照的 auth.json 原子切入 ~/.codex/auth.json
    3. 后续监控只读写活跃目录 (~/.codex 对应新账号, accounts/<旧> 对应旧账号), 绝不双份并发刷新
    返回 (ok, msg)
    """
    if not uid:
        return False, "无效账号"
    live = codex_home()
    live_auth = os.path.join(live, "auth.json")
    cur_nick, cur_uid = auth_identity(live)
    if cur_uid == uid:
        return True, "当前 CLI 已是该账号"

    target_dir = _find_snapshot_dir(uid)
    if not target_dir:
        return False, "未找到该账号的快照目录"
    target_auth = os.path.join(target_dir, "auth.json")
    if not os.path.isfile(target_auth):
        return False, "该账号缺少 auth.json 凭证"
    target_meta = read_meta(target_dir)
    if target_meta.get("needs_login"):
        return False, "该账号会话已失效, 请先重新登录"
    t_nick, t_uid = auth_identity(target_dir)
    if not t_uid:
        return False, "目标账号凭证损坏, 请重新登录"

    # 1. 先归档当前 CLI 账号最新凭证
    if cur_uid and os.path.isfile(live_auth):
        cur_dest = _ensure_snapshot_dir(cur_nick, cur_uid)
        _copy_file_atomic(live_auth, os.path.join(cur_dest, "auth.json"))
        c_meta = read_meta(cur_dest)
        c_meta.update({"nick": cur_nick, "uid": cur_uid, "saved_at": time.time()})
        c_meta.pop("needs_login", None)
        c_meta.pop("bad_at", None)
        _write_json_atomic(_meta_path(cur_dest), c_meta)
        clear_refresh_cooldown(cur_dest)
        log("切号前已归档当前 CLI 账号: %s -> %s" % (cur_nick, cur_dest))

    # 2. 原子切入目标账号
    _copy_file_atomic(target_auth, live_auth)
    clear_refresh_cooldown(live)
    clear_refresh_cooldown(target_dir)
    disp = target_meta.get("label") or t_nick or "目标账号"
    log("CLI 账号已热切换: %s -> %s" % (cur_nick or "(无)", disp))
    return True, "已切为 CLI 账号: %s (新开终端生效)" % disp


def create_pending_login_dir():
    """创建一个独立的临时 CODEX_HOME 目录用于登录新账号, 不影响 ~/.codex"""
    os.makedirs(ACCOUNTS_DIR, exist_ok=True)
    d = os.path.join(ACCOUNTS_DIR, "_pending_%d" % int(time.time() * 1000))
    os.makedirs(d, exist_ok=True)
    return d


def import_pending_login(pending_dir):
    """检查临时目录是否已完成登录; 完成后自动归档入库并返回 (nick, uid, dest)"""
    src = os.path.join(pending_dir, "auth.json")
    if not os.path.isfile(src):
        return None
    nick, uid = auth_identity(pending_dir)
    if not uid:
        return None
    # 若与当前 ~/.codex 是同一账号, 只更新当前快照并清理冷却
    dest = _ensure_snapshot_dir(nick, uid)
    _copy_file_atomic(src, os.path.join(dest, "auth.json"))
    meta = read_meta(dest)
    meta.update({"nick": nick, "uid": uid, "saved_at": time.time()})
    meta.pop("needs_login", None)
    meta.pop("bad_at", None)
    _write_json_atomic(_meta_path(dest), meta)
    clear_refresh_cooldown(dest)
    try:
        shutil.rmtree(pending_dir, ignore_errors=True)
    except Exception:
        pass
    log("独立登录新账号已自动入库: %s -> %s" % (nick, dest))
    return nick, uid, dest


def rename_account(uid, label):
    """给账号改显示名 (仅本地标记, 支持已保存快照及当前登录账号)"""
    d = _find_snapshot_dir(uid)
    if d is None:
        live_nick, live_uid = auth_identity(codex_home())
        if live_uid == uid:
            d = _ensure_snapshot_dir(live_nick, uid)
    if not d:
        return False
    meta = read_meta(d)
    meta["uid"] = uid
    meta["label"] = label.strip()
    _write_json_atomic(_meta_path(d), meta)
    return True


def remove_account(uid):
    """删除账号快照 (当前登录的不能删, 只能删已保存的快照目录)"""
    live_uid = auth_identity(codex_home())[1]
    if uid == live_uid:
        return False
    d = _find_snapshot_dir(uid)
    if not d:
        return False
    shutil.rmtree(d, ignore_errors=True)
    clear_refresh_cooldown(d)
    return True


def http_json(url, headers=None, data=None, timeout=20):
    req = urllib.request.Request(url, headers=headers or {})
    body = None
    if data is not None:
        body = json.dumps(data).encode()
        req.add_header("Content-Type", "application/json")
    with urllib.request.urlopen(req, body, timeout=timeout) as r:
        return json.loads(r.read().decode())


def refresh_token(auth, home=None):
    """用 refresh_token 换新的 access_token, 并原子写回该账号的 auth.json"""
    tok = auth.get("tokens", {})
    rt = tok.get("refresh_token")
    if not rt:
        raise AuthError("凭证里没有 refresh_token, 请重新登录")
    try:
        resp = http_json(API_REFRESH, data={
            "client_id": CLIENT_ID,
            "grant_type": "refresh_token",
            "refresh_token": rt,
        })
    except urllib.error.HTTPError as e:
        detail = http_detail(e)
        log("续期失败[%s]: %s %s" % (home or "默认", e.code, detail))
        if e.code in (400, 401) or "invalidated" in detail or "invalid_grant" in detail:
            # refresh_token 已被服务端作废 (会话结束), 只能重新登录
            raise AuthError("会话已失效, 请重新登录该账号")
        raise
    new_tok = dict(tok)
    if resp.get("access_token"):
        new_tok["access_token"] = resp["access_token"]
    if resp.get("id_token"):
        new_tok["id_token"] = resp["id_token"]
    if resp.get("refresh_token"):
        new_tok["refresh_token"] = resp["refresh_token"]
    auth["tokens"] = new_tok
    auth["last_refresh"] = time.time()
    # 原子写回, 避免和 Codex CLI 并发写坏文件
    path = os.path.join(codex_home(home), "auth.json")
    tmp = path + ".tray.tmp"
    with open(tmp, "w", encoding="utf-8") as f:
        json.dump(auth, f, ensure_ascii=False)
    os.replace(tmp, path)
    log("token 已续期: %s" % home)


def _credit_timestamp(value):
    if value is None or isinstance(value, bool):
        return None
    try:
        stamp = float(value)
        if stamp > 1e12:
            stamp /= 1000
    except (ValueError, TypeError):
        if not isinstance(value, str):
            return None
        try:
            stamp = datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()
        except (ValueError, OverflowError, OSError):
            return None
    return stamp if 0 < stamp < 253402300799 else None


def _credit_count(value):
    try:
        return int(value) if value is not None and not isinstance(value, bool) else None
    except (ValueError, TypeError, OverflowError):
        return None


def _reset_credit_data(summary, extra, rate_limit, headers):
    summary = summary if isinstance(summary, dict) else {}
    extra = extra if isinstance(extra, dict) else {}
    available = _credit_count(summary.get("available_count"))
    cards = None
    if available != 0:
        try:
            detail = http_json(API_RESET_CREDITS, headers=headers, timeout=5)
            rows = detail.get("credits") if isinstance(detail, dict) else None
            if isinstance(rows, list):
                cards = [{
                    "title": " ".join(str(row.get("title") or "重置卡").split()),
                    "expires_at": _credit_timestamp(row.get("expires_at")),
                } for row in rows if isinstance(row, dict) and str(row.get("status", "")).lower() == "available"]
                cards.sort(key=lambda card: card["expires_at"] or math.inf)
                detail_count = _credit_count(detail.get("available_count"))
                if detail_count is not None:
                    available = detail_count
        except (OSError, ValueError, http.client.HTTPException) as exc:
            log("读取重置卡明细失败: %s" % type(exc).__name__)

    now = time.time()
    expires_at = next((card["expires_at"] for card in cards or ()
                       if (card["expires_at"] or 0) > now), None)
    if expires_at is None:
        for key in ("next_expiring_at", "earliest_expires_at", "expires_at", "expiration_timestamp", "expiry"):
            expires_at = _credit_timestamp(summary.get(key))
            if expires_at is not None:
                break
    return {
        "credits": available,
        "applicable_credits": _credit_count(summary.get("applicable_available_count")),
        "reset_cards": cards,
        "credits_expire_at": expires_at,
        "credits_balance": "无限" if extra.get("unlimited") is True else extra.get("balance") or "0",
        "limit_reached": rate_limit.get("limit_reached") is True,
    }


def fetch_usage(home=None):
    """返回指定账号的规范化数据 dict; 失败抛异常 (凭证失效抛 AuthError)"""
    auth = read_auth(home)
    if not auth or not auth.get("tokens", {}).get("access_token"):
        raise AuthError("没有登录凭证, 请重新登录该账号")

    def usage_headers():
        tok = auth["tokens"]
        return {
            "Authorization": "Bearer " + tok["access_token"],
            "chatgpt-account-id": tok.get("account_id", ""),
            "User-Agent": "codex-usage-tray/1.0",
        }

    key = codex_home(home)
    cur_mtime = auth_mtime(home)
    prev_mtime = _AUTH_MTIME_SEEN.get(key)
    if prev_mtime is not None and cur_mtime > prev_mtime:
        # 检测到 auth.json 被重新写入(如刚完成 codex login), 立即解除冷却
        _NO_REFRESH_UNTIL.pop(key, None)
    _AUTH_MTIME_SEEN[key] = cur_mtime

    try:
        d = http_json(API_USAGE, headers=usage_headers())
    except urllib.error.HTTPError as e:
        if e.code not in (401, 403):
            raise
        detail = http_detail(e)
        log("额度接口 %s[%s]: %s" % (e.code, home or "默认", detail))
        if time.time() < _NO_REFRESH_UNTIL.get(key, 0):
            raise AuthError("会话已失效, 需重新登录")
        try:
            refresh_token(auth, home)          # 续期后重试一次
            _AUTH_MTIME_SEEN[key] = auth_mtime(home)
        except AuthError:
            _NO_REFRESH_UNTIL[key] = time.time() + 300
            raise AuthError("会话已失效, 需重新登录")
        except urllib.error.HTTPError:
            _NO_REFRESH_UNTIL[key] = time.time() + 300
            raise AuthError("会话已失效, 需重新登录")
        try:
            d = http_json(API_USAGE, headers=usage_headers())
        except urllib.error.HTTPError as e2:
            if e2.code in (401, 403):
                _NO_REFRESH_UNTIL[key] = time.time() + 300
                raise AuthError("会话已失效, 需重新登录")
            raise
    _NO_REFRESH_UNTIL.pop(key, None)

    rl = d.get("rate_limit") or {}
    pw = rl.get("primary_window") or {}
    sw = rl.get("secondary_window") or {}
    credit_data = _reset_credit_data(
        d.get("rate_limit_reset_credits") or {}, d.get("credits") or {}, rl, usage_headers())

    # 订阅信息从 id_token 的 claim 里取 (含本期到期时间)
    sub_until = sub_start = None
    nick = ""
    try:
        claims = jwt_claims(auth)
        nick = claims.get("name") or (claims.get("email") or "").split("@")[0]
        oa = claims.get("https://api.openai.com/auth", {})
        for key, k in (("chatgpt_subscription_active_until", "sub_until"),
                       ("chatgpt_subscription_active_start", "sub_start")):
            v = oa.get(key)
            if v:
                epoch = datetime.fromisoformat(v.replace("Z", "+00:00")).timestamp()
                if k == "sub_until":
                    sub_until = epoch
                else:
                    sub_start = epoch
    except Exception as e:
        log("解析 id_token 失败: %s" % e)

    return {
        "home": codex_home(home),
        "plan": d.get("plan_type", "?"),
        "nick": nick,
        "sub_until": sub_until,
        "sub_start": sub_start,
        "p_used": pw.get("used_percent"),
        "p_reset_at": pw.get("reset_at"),
        "p_left": pw.get("reset_after_seconds"),
        "s_used": sw.get("used_percent"),
        "s_reset_at": sw.get("reset_at"),
        "s_left": sw.get("reset_after_seconds"),
        **credit_data,
        "ts": time.time(),
    }


def fmt_duration(sec):
    if sec is None:
        return "--"
    sec = max(0, int(sec))
    d, rem = divmod(sec, 86400)
    h, rem = divmod(rem, 3600)
    m, _ = divmod(rem, 60)
    if d > 0:
        return "%dd%dh%dm" % (d, h, m)
    if h > 0:
        return "%dh%dm" % (h, m)
    return "%dm" % m


def fmt_reset(reset_at):
    """精确到期时间: 今天 19:26:04 / 明天 03:12:40 / 11月25日 14:22:33"""
    if not reset_at:
        return "--"
    dt = datetime.fromtimestamp(reset_at)
    now = datetime.now()
    if dt.date() == now.date():
        day = "今天"
    elif dt.date() == (now + timedelta(days=1)).date():
        day = "明天"
    else:
        day = "%d月%d日" % (dt.month, dt.day)
    return "%s %02d:%02d:%02d" % (day, dt.hour, dt.minute, dt.second)


def fmt_reset_short(reset_at):
    """紧凑格式: 19:24 / 明天 03:12 / 11/25 18:15"""
    if not reset_at:
        return "--"
    dt = datetime.fromtimestamp(reset_at)
    now = datetime.now()
    if dt.date() == now.date():
        return dt.strftime("%H:%M")
    if dt.date() == (now + timedelta(days=1)).date():
        return dt.strftime("明天 %H:%M")
    return dt.strftime("%m/%d %H:%M")


@lru_cache(maxsize=16)
def _ring_font(size):
    from PIL import ImageFont
    for path in (r"C:\Windows\Fonts\msyhbd.ttc", ICON_FONT):
        try:
            return ImageFont.truetype(path, size)
        except OSError:
            pass
    return ImageFont.load_default()


def render_ring_image(pct, color, size=88, scale=4, ring_width=8, time_ratio=None, countdown="", phase=0):
    """抗锯齿环形进度图: 4x 超采样绘制后 LANCZOS 缩小

    pct: 剩余百分比 0-100, None 表示未知
    """
    from PIL import Image, ImageDraw

    S = size * scale
    w = ring_width * scale
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = w / 2 + 2 * scale
    box = (pad, pad, S - pad, S - pad)
    cx = cy = S / 2
    radius = (S - 2 * pad) / 2
    # PIL 的 arc/ellipse 描边贴着包围盒向内生长, 弧线中线半径 = radius - w/2
    r_mid = radius - w / 2

    if time_ratio is not None:
        inner = radius - w - scale
        inner_box = (cx - inner, cy - inner, cx + inner, cy + inner)
        mask = Image.new("L", (S, S))
        ImageDraw.Draw(mask).ellipse(inner_box, fill=255)
        liquid = Image.new("RGBA", (S, S), "#313944")
        water = ImageDraw.Draw(liquid)
        ratio = max(0, min(1, time_ratio))
        level = cy + inner * (1 - 2 * ratio)
        if ratio == 1:
            water.rectangle((0, 0, S, S), fill="#52677f")
        elif ratio > 0:
            amplitude = 1.5 * scale * math.sin(math.pi * ratio)
            wave = [(x, level + amplitude * math.sin(x / S * math.tau - phase))
                    for x in range(0, S + 1, scale)]
            water.polygon(wave + [(S, S), (0, S)], fill="#52677f")
        img.paste(liquid, (0, 0), mask)

    # 轨道
    d.ellipse(box, outline=C_TRACK, width=int(w))

    if pct is not None:
        p = max(0.0, min(100.0, float(pct)))
        if p >= 99.9:
            d.ellipse(box, outline=color, width=int(w))
        elif p > 0:
            start, end = -90.0, -90.0 + 360.0 * p / 100.0
            d.arc(box, start, end, fill=color, width=int(w))
            # 两端补圆头(与弧线中线对齐, 不外凸)
            for ang in (start, end):
                a = math.radians(ang)
                x = cx + r_mid * math.cos(a)
                y = cy + r_mid * math.sin(a)
                d.ellipse([x - w / 2, y - w / 2, x + w / 2, y + w / 2], fill=color)

    # 中心百分比文字 (同样超采样绘制)
    txt = "?" if pct is None else "%d%%" % int(pct)
    fg = C_SUB if pct is None else color
    number_font = _ring_font(round(size * scale * 0.19))
    d.text((cx, cy - (S * 0.10 if countdown else 0)), txt, font=number_font, fill=fg, anchor="mm")
    if countdown:
        d.text((cx, cy + S * 0.16), countdown, font=_ring_font(round(size * scale * 0.12)),
               fill="#d8e7f9", anchor="mm")

    return img.resize((size, size), Image.LANCZOS)


def render_settings_icon(color, size=15, scale=4):
    """绘制"滑杆"设置图标: 3 条横线 + 圆钮, 4x 超采样抗锯齿"""
    from PIL import Image, ImageDraw
    S = size * scale
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    line_w = max(1, int(1.2 * scale))
    r = 1.45 * scale

    rows = [(0.20, 0.32), (0.50, 0.66), (0.80, 0.44)]   # (y比例, 圆钮x比例)
    for yp, xp in rows:
        y = S * yp
        x = S * xp
        d.line([(S * 0.10, y), (S * 0.90, y)], fill=color, width=line_w)
        d.ellipse([x - r, y - r, x + r, y + r], fill=color)
        # 圆钮中心挖空, 形成调节钮的镂空感
        d.ellipse([x - r * 0.45, y - r * 0.45, x + r * 0.45, y + r * 0.45], fill=(0, 0, 0, 0))
    return img.resize((size, size), Image.LANCZOS)


def color_for(remaining_pct):
    if remaining_pct is None:
        return C_SUB
    if remaining_pct >= 50:
        return C_GREEN
    if remaining_pct >= 20:
        return C_YELLOW
    return C_RED


# ----------------------------------------------------------------------------
# 托盘图标 (pystray)
# ----------------------------------------------------------------------------
def render_icon(p_remaining, s_remaining):
    """64x64 托盘图标: 4x 超采样绘制后缩小, 边缘无锯齿"""
    from PIL import Image, ImageDraw, ImageFont
    size, scale = 64, 4
    S = size * scale
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    dr = ImageDraw.Draw(img)
    # 深色圆角底
    dr.rounded_rectangle([2 * scale, 2 * scale, S - 2 * scale, S - 2 * scale],
                         radius=12 * scale, fill=(30, 31, 36, 255))
    color = color_for(p_remaining)
    rgb = tuple(int(color[i:i + 2], 16) for i in (1, 3, 5))

    try:
        font = ImageFont.truetype(ICON_FONT, 30 * scale)
    except Exception:
        font = ImageFont.load_default()
    text = "?" if p_remaining is None else str(int(p_remaining))
    bbox = dr.textbbox((0, 0), text, font=font)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    dr.text(((S - tw) / 2 - bbox[0], (S - th) / 2 - bbox[1] - 4 * scale),
            text, font=font, fill=rgb + (255,))

    # 底部周剩余进度条
    if s_remaining is not None:
        bar_y, bar_h, margin = S - 12 * scale, 6 * scale, 10 * scale
        dr.rounded_rectangle([margin, bar_y, S - margin, bar_y + bar_h],
                             radius=3 * scale, fill=(58, 59, 68, 255))
        w = int((S - 2 * margin) * max(0, min(100, s_remaining)) / 100)
        if w > 6 * scale:
            srgb = tuple(int(color_for(s_remaining)[i:i + 2], 16) for i in (1, 3, 5))
            dr.rounded_rectangle([margin, bar_y, margin + w, bar_y + bar_h],
                                 radius=3 * scale, fill=srgb + (255,))
    return img.resize((size, size), Image.LANCZOS)


def tooltip_multi(items):
    """items = [(显示名, data, err), ...]  多账号悬停提示"""
    if not items:
        return "Codex 用量\n⚠ 未配置账号"
    if len(items) == 1:
        name, d, err = items[0]
        if not d:
            return "Codex 用量\n⚠ %s" % (err or "加载中...")
        t = "Codex (%s)\n5小时: 剩 %s%% · %s后重置\n       到期: %s\n本周: 剩 %s%% · %s后重置\n       到期: %s" % (
            d["plan"].upper(),
            "-" if d["p_used"] is None else int(100 - d["p_used"]),
            fmt_duration(d["p_left"]),
            fmt_reset(d["p_reset_at"]),
            "-" if d["s_used"] is None else int(100 - d["s_used"]),
            fmt_duration(d["s_left"]),
            fmt_reset(d["s_reset_at"]),
        )
        if d.get("credits"):
            t += "\n重置机会: %d 个" % d["credits"]
        if d.get("sub_until"):
            t += "\n订阅到期: %s" % fmt_reset(d["sub_until"])
        if err:
            t += "\n⚠ %s" % err
        return t

    parts = ["Codex 用量 · %d 个账号" % len(items)]
    for name, d, err in items:
        parts.append("──────────")
        if d:
            parts.append("%s [%s]" % (name, d["plan"].upper()))
            parts.append("5小时 剩 %s%%  到 %s" % (
                "-" if d["p_used"] is None else int(100 - d["p_used"]),
                fmt_reset_short(d["p_reset_at"])))
            parts.append("本周  剩 %s%%  到 %s" % (
                "-" if d["s_used"] is None else int(100 - d["s_used"]),
                fmt_reset_short(d["s_reset_at"])))
            if d.get("sub_until"):
                parts.append("订阅到 %s" % fmt_reset_short(d["sub_until"]))
        else:
            parts.append("%s ⚠ %s" % (name, err or "加载中..."))
    return "\n".join(parts)


def tooltip_text(data, err=None):
    """兼容旧调用: 单账号"""
    return tooltip_multi([("Codex", data, err)])


# ----------------------------------------------------------------------------
# 开机自启 (注册表 HKCU Run)
# ----------------------------------------------------------------------------
def autostart_enabled():
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, AUTOSTART_REG) as k:
            winreg.QueryValueEx(k, AUTOSTART_NAME)
            return True
    except OSError:
        return False


def set_autostart(enable):
    import winreg
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, AUTOSTART_REG, 0, winreg.KEY_SET_VALUE) as k:
        if enable:
            if getattr(sys, "frozen", False):
                cmd = subprocess.list2cmdline([sys.executable])
            else:
                pyw = Path(sys.executable).with_name("pythonw.exe")
                cmd = subprocess.list2cmdline([str(pyw), os.path.abspath(__file__)])
            winreg.SetValueEx(k, AUTOSTART_NAME, 0, winreg.REG_SZ, cmd)
        else:
            try:
                winreg.DeleteValue(k, AUTOSTART_NAME)
            except OSError:
                pass


# ----------------------------------------------------------------------------
# 主程序
# ----------------------------------------------------------------------------
class App:
    def __init__(self):
        import tkinter as tk
        self.cfg = load_config()
        changed = False
        for k, v in DEFAULT_CONFIG.items():
            if k not in self.cfg:
                self.cfg[k] = v
                changed = True
        if changed:
            save_config(self.cfg)
        self.results = {}          # home -> (data, err, err_kind)  err_kind: None | "auth" | "net"
        self.accounts = []         # 当前登录 + 已保存快照
        self.view = None           # 当前显示的账号
        self.card = None           # 单账号卡片控件引用
        self.overview_cards = {}   # home -> 多账号总览卡片控件引用
        self._lock = threading.Lock()
        self._ring_state = {}
        self._wave_phase = 0
        self._animate_rings = windows_ui.animate_windows()
        self._restart_until = 0
        self._restarting = False
        self._taskbar_win = None
        self._taskbar_quota = (None, None)
        self._taskbar_setting = None
        self._stop_event = threading.Event()
        self._notified_low = {}    # (home, win_key) -> reset_at (去重低额度通知)
        self._live_auth_bad = False
        self._relogin_watch = {}   # home -> baseline mtime (等待重新登录自动恢复)
        self._pending_logins = {}  # pending_dir -> start_ts (等待独立登录新账号自动入库)
        self._foot_clickable = False
        self._reload_accounts()

        # 高 DPI 清晰
        try:
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except Exception:
            pass

        self.root = tk.Tk()
        log("启动: Tk 已创建")
        self.root.title("Codex 用量")
        self.root.overrideredirect(True)          # 无边框
        self.root.attributes("-topmost", True)    # 置顶
        self.root.attributes("-alpha", float(self.cfg.get("alpha") or DEFAULT_CONFIG["alpha"]))
        self._build_window()
        log("启动: 窗口已构建 %s" % (self.root.geometry(),))
        self._round_corners()

        # 托盘 (所有 Tk 操作统一通过 root.after 派发到主线程, 保证线程安全)
        import pystray
        from pystray import MenuItem, Menu
        self.tray = pystray.Icon(
            "CodexUsage",
            render_icon(None, None),
            "Codex 用量\n加载中...",
            menu=Menu(
                MenuItem(lambda item: "当前 CLI: %s" % self._live_account_name(), None, enabled=False),
                Menu.SEPARATOR,
                MenuItem("显示/隐藏浮动窗口", lambda *_: self._run_ui(self.toggle_window), default=True),
                MenuItem("切换单/多账号视图", lambda *_: self._run_ui(self._toggle_view_mode)),
                MenuItem("设置…", lambda *_: self._run_ui(self._open_settings)),
                MenuItem("立即刷新", lambda *_: self._run_ui(self._manual_refresh)),
                MenuItem("复位窗口位置", lambda *_: self._run_ui(self._reset_position)),
                MenuItem("任务栏常驻显示", lambda *_: self._run_ui(self._toggle_taskbar),
                         checked=lambda item: self.cfg["show_in_taskbar"]),
                MenuItem("开机自启", lambda *_: self._run_ui(self._toggle_autostart), checked=lambda item: autostart_enabled()),
                Menu.SEPARATOR,
                MenuItem("退出", lambda *_: self._run_ui(self._quit)),
                MenuItem(version_label(), None, enabled=False),
            ),
        )
        threading.Thread(target=self.tray.run, daemon=True).start()
        log("启动: 托盘已创建")

        # 轮询线程
        self._wake = threading.Event()
        threading.Thread(target=self._poll_loop, daemon=True).start()

        self.root.protocol("WM_DELETE_WINDOW", lambda: self.toggle_window())
        self.root.after(100, self._apply_state)
        self.root.after(100, self._refresh_taskbar)
        self.root.after(100, self._animate)
        self.root.after(1000, self._refresh_restart)
        log("启动: 进入 mainloop, 屏幕 %dx%d" % (self.root.winfo_screenwidth(), self.root.winfo_screenheight()))
        self.root.mainloop()

    def _run_ui(self, fn):
        """从非主线程安全派发回调到 Tk 主线程"""
        try:
            self.root.after(0, fn)
        except Exception:
            pass

    # ---------------- 账号管理 ----------------
    def _reload_accounts(self):
        self.accounts = list_accounts()
        # 同步当前登录账号的运行时失效标记
        for a in self.accounts:
            if a.get("live"):
                a["auth_bad"] = bool(self._live_auth_bad)
        # 同名账号(不同账号但昵称一样)自动加序号, 方便区分
        seen = {}
        for a in self.accounts:
            key = self.display_name(a)
            seen[key] = seen.get(key, 0) + 1
            if seen[key] > 1:
                a["label"] = "%s (%d)" % (key, seen[key])
        uid = self.cfg.get("display_uid") or ""
        self.view = None
        for a in self.accounts:
            if uid and a["uid"] == uid:
                self.view = a
                break
        if self.view is None and self.accounts:
            self.view = self.accounts[0]
        log("账号: %d 个, 当前显示 %s" % (
            len(self.accounts), self.display_name(self.view) if self.view else "(无)"))

    @staticmethod
    def display_name(acct):
        if not acct:
            return ""
        return acct.get("label") or acct.get("nick") or "账号"

    def _live_account_name(self):
        """返回当前 Codex CLI 登录账号的显示昵称"""
        for a in self.accounts:
            if a.get("live"):
                return self.display_name(a)
        return "未登录"

    def _is_overview_mode(self):
        return self.cfg.get("view_mode") == "all" and len(self.accounts) > 1

    def _toggle_view_mode(self, *_):
        if len(self.accounts) < 2:
            self.cfg["view_mode"] = "single"
        else:
            self.cfg["view_mode"] = "single" if self._is_overview_mode() else "all"
        save_config(self.cfg)
        self._render_account()
        self._apply_state()
        self._kick_poll()

    def _set_display(self, uid, switch_to_single=False):
        self.cfg["display_uid"] = uid
        if switch_to_single:
            self.cfg["view_mode"] = "single"
        save_config(self.cfg)
        self._reload_accounts()
        self._render_account()
        self._apply_state()
        self._kick_poll()

    def _switch_cli(self, uid):
        """一键热切换 Codex CLI 当前登录账号 (不执行 codex logout, 保留原账号授权)"""
        # 记录切换前 uid -> result 缓存, 切换后按新 home 还原避免界面闪烁
        uid_cache = {}
        with self._lock:
            for a in self.accounts:
                if a["home"] in self.results:
                    uid_cache[a["uid"]] = self.results[a["home"]]

        ok, msg = switch_cli_account(uid)
        if ok:
            self._restart_until = time.monotonic() + 60
            self._live_auth_bad = False
            self._reload_accounts()
            with self._lock:
                for a in self.accounts:
                    if a["uid"] in uid_cache:
                        self.results[a["home"]] = uid_cache[a["uid"]]
            self._render_account()
            self._apply_state()
            self._kick_poll()
            try:
                self.tray.update_menu()
            except Exception:
                pass
            try:
                self._refresh_restart_status()
            except Exception:
                pass
        else:
            try:
                self.lbl_foot.config(text="⚠ %s" % msg, fg=C_RED)
            except Exception:
                pass
        return ok, msg

    def _restart_seconds(self):
        return max(0, math.ceil(self._restart_until - time.monotonic()))

    def _refresh_restart_status(self):
        left = self._restart_seconds()
        button = getattr(self, "_restart_button", None)
        if button is not None and button.winfo_exists():
            if left:
                button.config(text="↻ 重启 Codex（%ds）" % left)
                if not button.winfo_manager():
                    button.pack(side="left", padx=(5, 0))
            else:
                button.pack_forget()
        if left and not self._foot_clickable:
            self.lbl_foot.config(text="✓ 已切换 CLI · 重启 Codex（%ds）" % left,
                                 fg=C_ACCENT, cursor="hand2")
        elif self.lbl_foot.cget("text").startswith("✓ 已切换 CLI"):
            self._apply_state()

    def _refresh_restart(self):
        self._refresh_restart_status()
        self.root.after(1000, self._refresh_restart)

    def _prompt_restart(self, parent=None):
        from tkinter import messagebox
        if self._restarting or not self._restart_seconds():
            return
        parent = parent or self.root
        if not messagebox.askyesno("重启 Codex", "重启 Codex 桌面应用以使用刚切换的账号？",
                                   icon="question", parent=parent):
            return
        self._restarting = True
        self._restart_until = 0
        self._refresh_restart_status()

        def restart():
            ok, message = windows_ui.restart_codex()

            def finish():
                self._restarting = False
                if not ok:
                    self._restart_until = time.monotonic() + 60
                    messagebox.showerror("重启 Codex", message, parent=self.root)
                self.lbl_foot.config(text=message, fg=C_GREEN if ok else C_RED)
            self._run_ui(finish)

        threading.Thread(target=restart, daemon=True).start()

    def _sync_login(self):
        """把当前 codex login 的账号存成快照, 并把显示切到它"""
        ok, msg, uid = save_current_login()
        if ok:
            self._live_auth_bad = False
            self.cfg["display_uid"] = uid
            save_config(self.cfg)
            self._reload_accounts()
            self._render_account()
            self._apply_state()
            self._kick_poll()
        return ok, msg

    def _launch_relogin(self, acct):
        """在独立控制台窗口为指定账号执行 codex login, 并监视 auth.json 变更以自动恢复"""
        if not acct:
            return False, "没有可重新登录的账号"
        home = acct["home"]
        nick = self.display_name(acct)
        try:
            os.makedirs(home, exist_ok=True)
            env = os.environ.copy()
            if not acct.get("live"):
                env["CODEX_HOME"] = home
            # 不使用 shell=True 拼接命令, 避免昵称含特殊字符导致命令异常
            creationflags = getattr(subprocess, "CREATE_NEW_CONSOLE", 0)
            subprocess.Popen(["cmd", "/k", "codex", "login"],
                             env=env, cwd=home, creationflags=creationflags)
            clear_refresh_cooldown(home)
            self._relogin_watch[home] = auth_mtime(home)
            self._kick_poll()
            log("已拉起重新登录窗口: %s (%s)" % (nick, home))
            return True, "已打开 %s 的登录窗口, 登录完成后自动恢复" % nick
        except Exception as ex:
            log("打开重新登录窗口失败: %s" % ex)
            return False, "打开失败: %s" % ex

    def _launch_new_login(self):
        """在独立隔离的 CODEX_HOME 目录打开终端登录新账号 (不覆盖 ~/.codex, 完成后自动入库)"""
        try:
            pending_dir = create_pending_login_dir()
            env = os.environ.copy()
            env["CODEX_HOME"] = pending_dir
            creationflags = getattr(subprocess, "CREATE_NEW_CONSOLE", 0)
            subprocess.Popen(["cmd", "/k", "codex", "login"],
                             env=env, cwd=pending_dir, creationflags=creationflags)
            self._pending_logins[pending_dir] = time.time()
            self._kick_poll()
            log("已拉起独立新账号登录窗口: %s" % pending_dir)
            return True, "已打开独立登录窗口(不影响当前CLI), 完成后自动加入"
        except Exception as ex:
            log("打开独立登录窗口失败: %s" % ex)
            return False, "打开失败: %s" % ex

    def _manual_refresh(self, *_):
        """手动立即刷新全部账号"""
        for a in self.accounts:
            clear_refresh_cooldown(a["home"])
        try:
            self.lbl_hint.config(text="刷新中…", fg=C_ACCENT)
        except Exception:
            pass
        self._kick_poll()

    def _reset_position(self, *_):
        """将浮动窗口复位到屏幕右侧垂直居中位置"""
        sw = self.root.winfo_screenwidth()
        sh = self.root.winfo_screenheight()
        self.root.update_idletasks()
        h = max(120, self.inner.winfo_reqheight())
        x = max(0, sw - self._W - 16)
        y = max(0, (sh - h) // 2)
        self._pos = (x, y)
        self.cfg["window_x"], self.cfg["window_y"] = x, y
        save_config(self.cfg)
        self.root.geometry("%dx%d+%d+%d" % (self._W, h, x, y))
        if self.root.state() == "withdrawn":
            self.root.deiconify()
        self.root.lift()
        log("窗口位置已复位: (%d,%d)" % (x, y))

    @staticmethod
    def _ellipsize(text, max_units=11):
        """按视觉宽度截断过长昵称 (中文计 2, 英文/数字计 1), 避免挤压右侧按钮"""
        if not text:
            return ""
        units = 0
        out = []
        for ch in text:
            w = 2 if ord(ch) > 127 else 1
            if units + w > max_units:
                return "".join(out) + "…"
            out.append(ch)
            units += w
        return text

    # ---------------- 浮动窗口 ----------------
    def _scale_px(self, size):
        selected = self.cfg.get("font_size") or ("medium" if self.cfg.get("medium_font") else "small")
        return max(1, round(size * FONT_SCALES.get(selected, 1)))

    def _set_font_size(self, size, custom_seconds, status, scroll):
        if self.cfg.get("font_size", "medium" if self.cfg.get("medium_font") else "small") == size:
            return
        self.cfg["font_size"] = size
        save_config(self.cfg)
        self._set_win.destroy()
        credits = getattr(self, "_credits_win", None)
        had_credits = credits is not None and credits.winfo_exists()
        if had_credits:
            credits.destroy()
        self._build_window()
        self._apply_state()
        self._open_settings(custom_seconds, status, scroll)
        if had_credits:
            self._open_credit_detail()
            self._set_win.lift()

    def _build_window(self):
        import tkinter as tk
        from tkinter import font as tkfont
        r = self.root
        if hasattr(self, "inner"):
            self.inner.destroy()
        r.configure(bg=C_BORDER)                  # 外层=1px 边框色
        self._W = self._scale_px(364)
        sw, sh = r.winfo_screenwidth(), r.winfo_screenheight()
        x, y = getattr(self, "_pos", (self.cfg.get("window_x"), self.cfg.get("window_y")))
        if x is None:
            x = sw - self._W - 16
        if y is None:
            y = max(0, (sh - 240) // 2)
        self._pos = (int(x), int(y))
        r.geometry("%dx%d+%d+%d" % (self._W, 300, self._pos[0], self._pos[1]))

        self.f_title = tkfont.Font(family="微软雅黑", size=self._scale_px(10.5), weight="bold")
        self.f_name = tkfont.Font(family="微软雅黑", size=self._scale_px(9), weight="bold")
        self.f_num = tkfont.Font(family="微软雅黑", size=self._scale_px(14), weight="bold")
        self.f_sub = tkfont.Font(family="微软雅黑", size=self._scale_px(8.2))
        self.f_tiny = tkfont.Font(family="微软雅黑", size=self._scale_px(7.8))
        self.f_pill = tkfont.Font(family="微软雅黑", size=self._scale_px(7.2), weight="bold")
        f_title, f_sub, f_tiny, f_pill = self.f_title, self.f_sub, self.f_tiny, self.f_pill

        inner = tk.Frame(r, bg=C_BG)
        inner.pack(fill="both", expand=True, padx=1, pady=1)
        self.inner = inner

        # ---- 标题栏 (先 pack 右侧操作区, 保证「总览 / 设置 / 关闭」永不被长昵称挤压) ----
        bar = tk.Frame(inner, bg=C_BG)
        bar.pack(fill="x", padx=14, pady=(11, 2))
        self.bar = bar

        right_bar = tk.Frame(bar, bg=C_BG)
        right_bar.pack(side="right", padx=(6, 0))
        self.right_bar = right_bar

        xb = tk.Label(right_bar, text="✕", bg=C_BG, fg=C_SUB, font=f_sub, cursor="hand2")
        xb.pack(side="right", pady=(2, 0))
        xb.bind("<Button-1>", lambda e: self.toggle_window())
        xb.bind("<Enter>", lambda e: xb.config(fg=C_RED))
        xb.bind("<Leave>", lambda e: xb.config(fg=C_SUB))

        # 齿轮设置按钮 (PIL 绘制图标, 悬停变亮)
        from PIL import ImageTk
        def hex2rgb(hx, a=255):
            return (int(hx[1:3], 16), int(hx[3:5], 16), int(hx[5:7], 16), a)
        img_dim = ImageTk.PhotoImage(render_settings_icon(hex2rgb(C_SUB), size=self._scale_px(15)))
        img_hot = ImageTk.PhotoImage(render_settings_icon(hex2rgb(C_TEXT), size=self._scale_px(15)))
        gear = tk.Label(right_bar, bg=C_BG, image=img_dim, cursor="hand2", bd=0)
        gear.image_dim, gear.image_hot = img_dim, img_hot   # 防 GC
        gear.pack(side="right", padx=(0, 9), pady=(2, 0))
        gear.bind("<Button-1>", lambda e: self._open_settings())
        gear.bind("<Enter>", lambda e: gear.config(image=img_hot))
        gear.bind("<Leave>", lambda e: gear.config(image=img_dim))

        # 单账号/多账号总览切换按钮 (多账号时显示)
        self.lbl_mode = tk.Label(right_bar, text="总览", bg=C_CARD, fg=C_SUB, font=f_pill,
                                 padx=6, pady=1, cursor="hand2")
        self.lbl_mode.bind("<Button-1>", lambda e: self._toggle_view_mode())
        self.lbl_mode.bind("<Enter>", lambda e: self.lbl_mode.config(fg=C_TEXT, bg="#2c2e39"))
        self.lbl_mode.bind("<Leave>", lambda e: self.lbl_mode.config(
            fg=C_ACCENT if self._is_overview_mode() else C_SUB, bg=C_CARD))

        # 左侧标题 + 昵称 + 徽标
        left_bar = tk.Frame(bar, bg=C_BG)
        left_bar.pack(side="left", fill="x", expand=True)
        tk.Label(left_bar, text="Codex", bg=C_BG, fg=C_TEXT, font=f_title).pack(side="left")
        # 昵称 (多账号时可点击切换)
        self.lbl_nick = tk.Label(left_bar, text="", bg=C_BG, fg=C_ACCENT, font=f_sub, cursor="hand2")
        self.lbl_nick.pack(side="left", padx=(6, 0), pady=(2, 0))
        self.lbl_nick.bind("<Button-1>", lambda e: self._open_switcher())
        self.lbl_nick.bind("<Enter>", lambda e: self.lbl_nick.config(fg="#a8d0ff"))
        self.lbl_nick.bind("<Leave>", lambda e: self.lbl_nick.config(fg=C_ACCENT))

        # ---- 仪表区 (单账号双环 或 多账号总览) ----
        self.cards_host = tk.Frame(inner, bg=C_BG)
        self.cards_host.pack(fill="both", expand=True)

        # ---- 分隔线 ----
        sep = tk.Frame(inner, bg=C_BORDER, height=1)
        sep.pack(fill="x", padx=14, pady=(6, 0))

        # ---- 底部信息 ----
        foot = tk.Frame(inner, bg=C_BG)
        foot.pack(fill="x", padx=14, pady=(5, 9))
        self.lbl_foot = tk.Label(foot, text="", bg=C_BG, fg=C_SUB, font=f_tiny, anchor="w")
        self.lbl_foot.pack(side="left")
        self.lbl_foot.bind("<Button-1>", self._on_foot_click)
        self.lbl_foot.bind("<Enter>", lambda e: self._foot_clickable and self.lbl_foot.config(fg="#ff8585"))
        self.lbl_foot.bind("<Leave>", lambda e: self._foot_clickable and self.lbl_foot.config(fg=C_RED))

        self.lbl_hint = tk.Label(foot, text="", bg=C_BG, fg="#6b6d76", font=f_tiny, anchor="e", cursor="hand2")
        self.lbl_hint.pack(side="right")
        self.lbl_hint.bind("<Button-1>", self._manual_refresh)
        self.lbl_hint.bind("<Enter>", lambda e: self.lbl_hint.config(fg=C_TEXT))
        self.lbl_hint.bind("<Leave>", lambda e: self.lbl_hint.config(fg="#6b6d76"))

        # 拖动 & 右键菜单 (绑定所有子控件)
        self._drag = None
        gear._no_drag = True          # 可点击控件不参与拖动, 避免误存坐标
        xb._no_drag = True
        self.lbl_mode._no_drag = True
        self.lbl_nick._no_drag = True
        self.lbl_hint._no_drag = True
        self.lbl_foot._no_drag = True
        self._bind_drag(r)

        self._render_account()
        self.root.after(300, self._clamp_to_screen)

    def _set_badges(self, plan_text, credits=None):
        if not self.card:
            return
        self.lbl_credits.config(text="⚡ 重置卡 %s ›" % ("--" if credits is None else "%d 张" % credits))
        if plan_text:
            self.lbl_plan.config(text=plan_text)
            if not self.lbl_plan.winfo_manager():
                self.lbl_plan.pack(side="right")
        else:
            self.lbl_plan.config(text="")
            if self.lbl_plan.winfo_manager():
                self.lbl_plan.pack_forget()

    def _on_foot_click(self, e=None):
        if self._foot_clickable and self.view:
            ok, msg = self._launch_relogin(self.view)
            self.lbl_foot.config(text=msg if ok else ("⚠ %s" % msg),
                                 fg=C_ACCENT if ok else C_RED)
        elif self._restart_seconds():
            self._prompt_restart()
        elif e is not None:
            self._on_drag_start(e)

    def _show_context_menu(self, e):
        """主窗口右键菜单"""
        import tkinter as tk
        menu = tk.Menu(self.root, tearoff=0, bg=C_CARD, fg=C_TEXT,
                       activebackground=C_ACCENT, activeforeground="#12141a",
                       disabledforeground=C_GREEN, bd=0, relief="flat")
        menu.add_command(label="● 当前 CLI: %s" % self._live_account_name(), state="disabled")
        menu.add_separator()
        menu.add_command(label="立即刷新", command=self._manual_refresh)
        if len(self.accounts) > 1:
            mode_lbl = "切换到单账号详情" if self._is_overview_mode() else "切换到多账号总览"
            menu.add_command(label=mode_lbl, command=self._toggle_view_mode)

            sub_view = tk.Menu(menu, tearoff=0, bg=C_CARD, fg=C_TEXT,
                               activebackground=C_ACCENT, activeforeground="#12141a",
                               bd=0, relief="flat")
            for a in self.accounts:
                cur = self.view and a["uid"] == self.view["uid"]
                suffix = " (需重新登录)" if a.get("auth_bad") else (" (CLI当前)" if a["live"] else "")
                label = "%s %s%s" % ("✓" if cur else "  ", self.display_name(a), suffix)
                sub_view.add_command(label=label, command=lambda uid=a["uid"]: self._set_display(uid, True))
            menu.add_cascade(label="查看账号详情", menu=sub_view)

            sub_cli = tk.Menu(menu, tearoff=0, bg=C_CARD, fg=C_TEXT,
                              activebackground=C_ACCENT, activeforeground="#12141a",
                              bd=0, relief="flat")
            for a in self.accounts:
                suffix = " (当前)" if a["live"] else (" (需先重登)" if a.get("auth_bad") else "")
                label = "%s %s%s" % ("●" if a["live"] else "  ", self.display_name(a), suffix)
                sub_cli.add_command(label=label, command=lambda uid=a["uid"]: self._switch_cli(uid))
            menu.add_cascade(label="切换为 CLI 账号", menu=sub_cli)

        if self.view:
            menu.add_command(label="重新登录当前查看账号", command=lambda: self._launch_relogin(self.view))
        menu.add_command(label="添加新账号 (独立隔离)", command=self._launch_new_login)
        menu.add_separator()
        menu.add_command(label="设置…", command=self._open_settings)
        menu.add_command(label="复位窗口位置", command=self._reset_position)
        menu.add_command(label="任务栏常驻显示 " + ("✓" if self.cfg["show_in_taskbar"] else ""),
                         command=self._toggle_taskbar)
        menu.add_command(label="隐藏到托盘", command=self.toggle_window)
        menu.add_separator()
        menu.add_command(label="退出", command=self._quit)
        menu.add_command(label=version_label(), state="disabled", foreground=C_SUB)
        try:
            menu.tk_popup(e.x_root, e.y_root)
        finally:
            menu.grab_release()

    def _draw_mini_bar(self, canvas, pct, color):
        """在 Canvas 上绘制紧凑进度条"""
        canvas.delete("all")
        w = int(float(canvas.cget("width")))
        h = int(float(canvas.cget("height")))
        canvas.create_rectangle(0, 0, w, h, fill=C_TRACK, outline="")
        if pct is not None:
            fill_w = int(w * max(0.0, min(100.0, float(pct))) / 100.0)
            if fill_w > 0:
                canvas.create_rectangle(0, 0, fill_w, h, fill=color, outline="")

    def _render_account(self):
        """根据 view_mode 渲染单账号双环详情或多账号总览"""
        import tkinter as tk
        for w in self.cards_host.winfo_children():
            w.destroy()
        self.card = None
        self.overview_cards = {}
        self._ring_state.clear()

        # 控制标题栏「总览/详情」切换按钮显隐
        if len(self.accounts) > 1:
            is_all = self._is_overview_mode()
            self.lbl_mode.config(text="详情" if is_all else "总览",
                                 fg=C_ACCENT if is_all else C_SUB)
            if not self.lbl_mode.winfo_manager():
                self.lbl_mode.pack(side="right", padx=(0, 8), pady=(2, 0))
        else:
            if self.lbl_mode.winfo_manager():
                self.lbl_mode.pack_forget()

        if not self.view:
            tk.Label(self.cards_host, text="未检测到 Codex 登录\n先执行 codex login",
                     bg=C_BG, fg=C_SUB, font=self.f_sub, justify="center").pack(pady=26)
            self.lbl_nick.config(text="未登录", cursor="arrow")
            self._set_badges("", None)
            self._bind_drag(self.cards_host)
            self._fit_height()
            return

        # ---- 多账号总览模式 ----
        if self._is_overview_mode():
            self.lbl_nick.config(text="%d 个账号" % len(self.accounts), cursor="hand2")
            self._set_badges("", None)
            wrap = tk.Frame(self.cards_host, bg=C_BG)
            wrap.pack(fill="x", padx=12, pady=(6, 2))
            for a in self.accounts:
                home = a["home"]
                uid = a["uid"]
                card = tk.Frame(wrap, bg=C_CARD, highlightthickness=1,
                                highlightbackground=C_ACCENT if a["live"] else C_BORDER)
                card.pack(fill="x", pady=(0, 6))

                top = tk.Frame(card, bg=C_CARD)
                top.pack(fill="x", padx=9, pady=(6, 3))

                # 先 pack 右侧状态/一键切为 CLI / 重新登录按钮, 避免长昵称挤压
                if a["live"]:
                    act_lbl = tk.Label(top, text="● CLI当前", bg=C_CARD, fg=C_GREEN, font=self.f_pill)
                    act_lbl.pack(side="right", padx=(6, 0))
                elif a.get("auth_bad"):
                    act_lbl = tk.Label(top, text="重新登录", bg="#3a2024", fg=C_RED,
                                       font=self.f_pill, padx=6, pady=1, cursor="hand2")
                    act_lbl.pack(side="right", padx=(6, 0))
                    act_lbl._no_drag = True
                    act_lbl.bind("<Button-1>", lambda e, acct=a: self._launch_relogin(acct))
                    act_lbl.bind("<Enter>", lambda e, lb=act_lbl: lb.config(bg="#4e282e"))
                    act_lbl.bind("<Leave>", lambda e, lb=act_lbl: lb.config(bg="#3a2024"))
                else:
                    act_lbl = tk.Label(top, text="切为CLI", bg="#2a2d3a", fg=C_ACCENT,
                                       font=self.f_pill, padx=6, pady=1, cursor="hand2")
                    act_lbl.pack(side="right", padx=(6, 0))
                    act_lbl._no_drag = True
                    act_lbl.bind("<Button-1>", lambda e, u=uid: self._switch_cli(u))
                    act_lbl.bind("<Enter>", lambda e, lb=act_lbl: lb.config(bg=C_ACCENT, fg="#12141a"))
                    act_lbl.bind("<Leave>", lambda e, lb=act_lbl: lb.config(bg="#2a2d3a", fg=C_ACCENT))

                name_lbl = tk.Label(top, text=self._ellipsize(self.display_name(a), 14), bg=C_CARD, fg=C_TEXT,
                                    font=self.f_name, cursor="hand2")
                name_lbl.pack(side="left")
                name_lbl._no_drag = True
                name_lbl.bind("<Button-1>", lambda e, u=uid: self._set_display(u, True))
                name_lbl.bind("<Enter>", lambda e, lb=name_lbl: lb.config(fg=C_ACCENT))
                name_lbl.bind("<Leave>", lambda e, lb=name_lbl: lb.config(fg=C_TEXT))

                plan_lbl = tk.Label(top, text="", bg="#2b2d37", fg=C_SUB, font=self.f_pill, padx=4, pady=0)

                # 进度条行: 5h 与 周
                bars = tk.Frame(card, bg=C_CARD)
                bars.pack(fill="x", padx=9, pady=(0, 6))

                row_p = tk.Frame(bars, bg=C_CARD)
                row_p.pack(fill="x", pady=(1, 2))
                tk.Label(row_p, text="5h", bg=C_CARD, fg=C_SUB, font=self.f_pill, width=3, anchor="w").pack(side="left")
                bar_p = tk.Canvas(row_p, width=140, height=6, bg=C_CARD, bd=0, highlightthickness=0)
                bar_p.pack(side="left", padx=(2, 6))
                val_p = tk.Label(row_p, text="--", bg=C_CARD, fg=C_SUB, font=self.f_pill, anchor="w")
                val_p.pack(side="left")
                rst_p = tk.Label(row_p, text="", bg=C_CARD, fg="#6b6d76", font=self.f_pill, anchor="e")
                rst_p.pack(side="right")

                row_s = tk.Frame(bars, bg=C_CARD)
                row_s.pack(fill="x")
                tk.Label(row_s, text="周", bg=C_CARD, fg=C_SUB, font=self.f_pill, width=3, anchor="w").pack(side="left")
                bar_s = tk.Canvas(row_s, width=140, height=6, bg=C_CARD, bd=0, highlightthickness=0)
                bar_s.pack(side="left", padx=(2, 6))
                val_s = tk.Label(row_s, text="--", bg=C_CARD, fg=C_SUB, font=self.f_pill, anchor="w")
                val_s.pack(side="left")
                rst_s = tk.Label(row_s, text="", bg=C_CARD, fg="#6b6d76", font=self.f_pill, anchor="e")
                rst_s.pack(side="right")

                self.overview_cards[home] = {
                    "plan": plan_lbl,
                    "bar_p": bar_p, "val_p": val_p, "rst_p": rst_p,
                    "bar_s": bar_s, "val_s": val_s, "rst_s": rst_s,
                }

            self._bind_drag(self.cards_host)
            self._fit_height()
            return

        # ---- 单账号双环详情模式 ----
        size = self._scale_px(96)
        # 昵称: 多账号时带 ▾ 提示可切换, 过长自动省略避免挤压右侧按钮
        name = self._ellipsize(self.display_name(self.view), 10)
        if len(self.accounts) > 1:
            name += " ▾"
        self.lbl_nick.config(text=name,
                             cursor="hand2" if len(self.accounts) > 1 else "arrow")
        rings = tk.Frame(self.cards_host, bg=C_BG)
        rings.pack(fill="x", padx=6, pady=(4, 0))
        cvs, tls = [], []
        for title, background in (("5h", "#395b78"), ("周", "#5d507b")):
            col = tk.Frame(rings, bg=C_BG)
            col.pack(side="left", expand=True, padx=4)
            cv = tk.Canvas(col, width=size, height=size, bg=C_BG, bd=0, highlightthickness=0)
            cv.pack()
            tk.Label(col, text=title, bg=background, fg=C_TEXT, font=self.f_pill,
                     padx=self._scale_px(6), pady=1).place(relx=0, y=0)
            tl = tk.Label(col, text="", bg=C_BG, fg=C_SUB, font=self.f_tiny)
            tl.pack()
            cvs.append(cv)
            tls.append(tl)

        self.card = {"cv_p": cvs[0], "t_p": tls[0], "cv_s": cvs[1], "t_s": tls[1]}
        badges = tk.Frame(self.cards_host, bg=C_BG)
        badges.pack(fill="x", padx=self._scale_px(14), pady=(self._scale_px(4), 0))
        self.lbl_credits = tk.Label(badges, bg=C_CARD, fg=C_ACCENT, font=self.f_pill,
                                    padx=self._scale_px(6), pady=2, cursor="hand2", takefocus=True)
        self.lbl_credits._no_drag = True
        self.lbl_credits.pack(side="left")
        self.lbl_credits.bind("<Button-1>", lambda e: self._open_credit_detail())
        self.lbl_credits.bind("<Return>", lambda e: self._open_credit_detail())
        self.lbl_plan = tk.Label(badges, bg=C_CARD, fg=C_SUB, font=self.f_pill,
                                 padx=self._scale_px(6), pady=2)
        self.lbl_credit_warn = tk.Label(self.cards_host, bg=C_BG, fg=C_YELLOW, font=self.f_tiny)
        self._set_badges("", None)
        # 重新为动态创建的卡片控件绑定拖动与右键菜单
        self._bind_drag(self.cards_host)
        self._fit_height()

    def _fit_height(self):
        """窗口高度跟随内容自适应, 超出屏幕则上移"""
        try:
            self.root.update_idletasks()
            h = max(120, self.inner.winfo_reqheight())
            x, y = self._pos
            sh = self.root.winfo_screenheight()
            if y + h > sh - 40:
                y = max(0, sh - h - 48)
            self._pos = (x, y)
            self.root.geometry("%dx%d+%d+%d" % (self._W, h, x, y))
        except Exception as e:
            log("fit_height 失败: %s" % e)

    def _open_switcher(self):
        """点击昵称: 弹出账号切换列表 (显示实时余量)"""
        if len(self.accounts) < 2:
            return
        import tkinter as tk
        from tkinter import font as tkfont

        old = getattr(self, "_switch_win", None)
        if old is not None and old.winfo_exists():
            old.destroy()
            return

        w = tk.Toplevel(self.root)
        self._switch_win = w
        w.overrideredirect(True)
        w.attributes("-topmost", True)
        w.attributes("-alpha", 0.98)
        w.configure(bg=C_BORDER)
        inner = tk.Frame(w, bg=C_BG)
        inner.pack(fill="both", expand=True, padx=1, pady=1)
        f_sub = tkfont.Font(family="微软雅黑", size=8)
        f_tiny = tkfont.Font(family="微软雅黑", size=7)

        tk.Label(inner, text="点击查看详情（实时余量）", bg=C_BG, fg=C_SUB,
                 font=f_tiny).pack(anchor="w", padx=10, pady=(7, 3))

        for a in self.accounts:
            cur = self.view and a["uid"] == self.view["uid"]
            base_bg = C_CARD if cur else C_BG
            row = tk.Frame(inner, bg=base_bg)
            row.pack(fill="x", padx=6, pady=1)
            prefix = "✓" if cur else "○"
            cli_mark = " ·CLI" if a["live"] else ""
            lbl = tk.Label(row, text="%s %s%s" % (prefix, self.display_name(a), cli_mark),
                           bg=base_bg, fg=C_ACCENT if cur else C_TEXT,
                           font=f_sub, anchor="w", padx=4, pady=3, cursor="hand2")
            lbl.pack(side="left")

            with self._lock:
                entry = self.results.get(a["home"])
            d = entry[0] if entry else None
            if a.get("auth_bad"):
                tag_txt, tag_fg = "需重新登录", C_RED
            elif d:
                pr = None if d["p_used"] is None else int(100 - d["p_used"])
                sr = None if d["s_used"] is None else int(100 - d["s_used"])
                tag_txt = "5h %s%% · 周 %s%%" % ("-" if pr is None else pr, "-" if sr is None else sr)
                min_rem = min([x for x in (pr, sr) if x is not None], default=None)
                tag_fg = color_for(min_rem)
            else:
                tag_txt, tag_fg = "加载中…", "#6b6d76"

            tag = tk.Label(row, text=tag_txt, bg=base_bg, fg=tag_fg,
                           font=f_tiny, padx=6, cursor="hand2")
            tag.pack(side="right")

            def pick(e, uid=a["uid"]):
                w.destroy()
                self._set_display(uid, switch_to_single=True)

            def on_enter(e, r=row, l=lbl, t=tag):
                for wd in (r, l, t):
                    wd.config(bg="#2a2c36")

            def on_leave(e, r=row, l=lbl, t=tag, bg=base_bg):
                for wd in (r, l, t):
                    wd.config(bg=bg)

            for wd in (row, lbl, tag):
                wd.bind("<Button-1>", pick)
                wd.bind("<Enter>", on_enter)
                wd.bind("<Leave>", on_leave)

        w.update_idletasks()
        H = inner.winfo_reqheight() + 4
        W = 264
        x = min(self.root.winfo_x() + 10, self.root.winfo_screenwidth() - W - 8)
        y = self.root.winfo_y() + 40
        if y + H > self.root.winfo_screenheight() - 40:
            y = max(0, self.root.winfo_y() - H - 4)
        w.geometry("%dx%d+%d+%d" % (W, H, x, y))
        self._round_corners_of(w)
        w.lift()
        w.focus_force()
        w.bind("<Escape>", lambda e: w.destroy())
        w.bind("<FocusOut>", lambda e: w.after(120, lambda: (
            w.destroy() if w.winfo_exists() and w.focus_get() is None else None
        )))
        log("切换列表: %d 个账号" % len(self.accounts))

    def _clamp_to_screen(self):
        """窗口若超出屏幕(DPI 缩放下常见), 拉回可视区域"""
        try:
            self.root.update_idletasks()
            sw = self.root.winfo_screenwidth()
            sh = self.root.winfo_screenheight()
            w = self.root.winfo_width()
            h = self.root.winfo_height()
            x, y = self.root.winfo_x(), self.root.winfo_y()
            nx = min(max(0, x), max(0, sw - w))
            ny = min(max(0, y), max(0, sh - h - 48))
            if (nx, ny) != (x, y):
                self.root.geometry("+%d+%d" % (nx, ny))
                self._pos = (nx, ny)
                self.cfg["window_x"], self.cfg["window_y"] = nx, ny
                save_config(self.cfg)
                log("窗口位置修正: (%d,%d)->(%d,%d)" % (x, y, nx, ny))
        except Exception as e:
            log("clamp 失败: %s" % e)

    def _round_corners(self):
        """Win11 原生圆角 (DWM), 旧系统自动忽略"""
        try:
            import ctypes
            self.root.update_idletasks()
            hwnd = ctypes.windll.user32.GetParent(self.root.winfo_id())
            pref = ctypes.c_int(2)          # DWMWCP_ROUND
            ctypes.windll.dwmapi.DwmSetWindowAttribute(
                hwnd, 33, ctypes.byref(pref), ctypes.sizeof(pref))
        except Exception as e:
            log("圆角设置失败(可忽略): %s" % e)

    def _bind_drag(self, widget):
        widget.bind("<Button-3>", self._show_context_menu)
        if not getattr(widget, "_no_drag", False):
            widget.bind("<Button-1>", self._on_drag_start)
            widget.bind("<B1-Motion>", self._on_drag_motion)
            widget.bind("<ButtonRelease-1>", self._on_drag_end)
        for child in widget.winfo_children():
            self._bind_drag(child)

    def _draw_ring(self, canvas, pct, color, time_ratio=None, countdown=""):
        self._ring_state[canvas] = (pct, color, time_ratio, countdown)
        self._paint_ring(canvas, pct, color, time_ratio, countdown)

    def _paint_ring(self, canvas, pct, color, time_ratio, countdown):
        """环形进度: 用 PIL 4 倍超采样绘制再 LANCZOS 缩放, 消除锯齿"""
        from PIL import ImageTk
        size = int(float(canvas.cget("width")))
        img = render_ring_image(pct, color, size=size, ring_width=self._scale_px(8),
                                time_ratio=time_ratio, countdown=countdown, phase=self._wave_phase)
        if hasattr(canvas, "_ring_photo"):
            canvas._ring_photo.paste(img)
        else:
            canvas._ring_photo = ImageTk.PhotoImage(img)
            canvas.create_image(size / 2, size / 2, image=canvas._ring_photo)

    def _animate(self):
        if self._animate_rings and self.root.state() != "withdrawn":
            self._wave_phase = (self._wave_phase + 0.22) % math.tau
            for canvas, state in self._ring_state.items():
                if state[2] is not None and 0 < state[2] < 1:
                    self._paint_ring(canvas, *state)
        self.root.after(70 if self._animate_rings else 1000, self._animate)

    def _on_drag_start(self, e):
        self._drag = (e.x, e.y)

    def _on_drag_motion(self, e):
        if self._drag:
            x = self.root.winfo_x() + e.x - self._drag[0]
            y = self.root.winfo_y() + e.y - self._drag[1]
            self.root.geometry("+%d+%d" % (x, y))

    def _on_drag_end(self, e):
        self._drag = None
        self.cfg["window_x"], self.cfg["window_y"] = self.root.winfo_x(), self.root.winfo_y()
        self._pos = (self.root.winfo_x(), self.root.winfo_y())
        save_config(self.cfg)

    # ---------------- 状态更新 ----------------
    def _check_low_quota_notify(self, acct, d):
        """额度低于阈值时发送一次 Windows 托盘系统通知 (同一重置周期内不重复提醒)"""
        if not acct or not d or not self.cfg.get("notify_low", True):
            return
        threshold = int(self.cfg.get("notify_threshold") or 20)
        if threshold <= 0:
            return
        home = acct["home"]
        name = self.display_name(acct)
        checks = (
            ("p", "5小时额度", d.get("p_used"), d.get("p_reset_at"), d.get("p_left")),
            ("s", "每周额度", d.get("s_used"), d.get("s_reset_at"), d.get("s_left")),
        )
        for key, label, used, reset_at, left in checks:
            if used is None:
                continue
            rem = int(round(100 - used))
            nk = (home, key)
            if rem <= threshold:
                if self._notified_low.get(nk) != reset_at:
                    self._notified_low[nk] = reset_at
                    msg = "%s %s仅剩 %d%%（%s后重置）" % (name, label, rem, fmt_duration(left))
                    try:
                        self.tray.notify(msg, "Codex 额度偏低")
                        log("已发送低额度通知: %s" % msg)
                    except Exception as e:
                        log("发送通知失败: %s" % e)
            elif rem >= threshold + 5:
                self._notified_low.pop(nk, None)

    def _apply_state(self):
        home = self.view["home"] if self.view else None
        with self._lock:
            snap_results = dict(self.results)
        entry = snap_results.get(home) if home else None
        if entry:
            d, err, err_kind = entry
        else:
            d, err, err_kind = None, None, None

        self._foot_clickable = False
        self.lbl_foot.config(cursor="arrow")

        # ---- 多账号总览视图刷新 ----
        if self._is_overview_mode() and self.overview_cards:
            latest_ts = 0
            for a in self.accounts:
                ah = a["home"]
                w = self.overview_cards.get(ah)
                if not w:
                    continue
                ae = snap_results.get(ah)
                ad, aerr, akind = ae if ae else (None, None, None)
                if ad:
                    latest_ts = max(latest_ts, ad.get("ts") or 0)
                    plan_txt = (ad.get("plan") or "").upper()
                    if plan_txt:
                        w["plan"].config(text=plan_txt)
                        if not w["plan"].winfo_manager():
                            w["plan"].pack(side="left", padx=(6, 0))
                    pr = None if ad["p_used"] is None else int(round(100 - ad["p_used"]))
                    sr = None if ad["s_used"] is None else int(round(100 - ad["s_used"]))
                    pc, sc = color_for(pr), color_for(sr)
                    self._draw_mini_bar(w["bar_p"], pr, pc)
                    self._draw_mini_bar(w["bar_s"], sr, sc)
                    w["val_p"].config(text="--" if pr is None else "%d%%" % pr, fg=pc)
                    w["val_s"].config(text="--" if sr is None else "%d%%" % sr, fg=sc)
                    w["rst_p"].config(text=fmt_reset_short(ad.get("p_reset_at")))
                    w["rst_s"].config(text=fmt_reset_short(ad.get("s_reset_at")))
                else:
                    if w["plan"].winfo_manager():
                        w["plan"].pack_forget()
                    self._draw_mini_bar(w["bar_p"], None, C_TRACK)
                    self._draw_mini_bar(w["bar_s"], None, C_TRACK)
                    is_bad = (akind == "auth") or bool(a.get("auth_bad"))
                    txt = "需重新登录" if is_bad else ("获取失败" if aerr else "加载中…")
                    fg = C_RED if is_bad else C_SUB
                    w["val_p"].config(text=txt, fg=fg)
                    w["val_s"].config(text="--", fg=C_SUB)
                    w["rst_p"].config(text="")
                    w["rst_s"].config(text="")

            if latest_ts:
                stamp = datetime.fromtimestamp(latest_ts).strftime("%H:%M:%S")
                self.lbl_foot.config(text="点账号看详情 · 更新 %s" % stamp, fg=C_SUB)
            else:
                self.lbl_foot.config(text="正在获取多账号数据…", fg=C_SUB)
        else:
            # ---- 单账号双环详情视图刷新 ----
            if d and self.card:
                self._set_badges(d["plan"].upper(), d.get("credits"))
                pr = None if d["p_used"] is None else 100 - d["p_used"]
                sr = None if d["s_used"] is None else 100 - d["s_used"]
                for cv, tlabel, rem, left, reset_at, duration in (
                    (self.card["cv_p"], self.card["t_p"], pr, d["p_left"], d["p_reset_at"], 18000),
                    (self.card["cv_s"], self.card["t_s"], sr, d["s_left"], d["s_reset_at"], 604800),
                ):
                    color = color_for(rem)
                    ratio = left / duration if left is not None else None
                    countdown = fmt_duration(left) if left is not None else ""
                    if left is not None and left >= 86400:
                        countdown = "%dd%dh" % (left // 86400, left % 86400 // 3600)
                    self._draw_ring(cv, rem, color, ratio, countdown)
                    tlabel.config(text="%s 重置" % fmt_reset_short(reset_at),
                                  fg=color if rem is not None and rem < 20 else C_SUB)
            elif self.card:
                self._set_badges("", None)
                is_auth_bad = (err_kind == "auth") or bool(self.view and self.view.get("auth_bad"))
                placeholder = "-- 待重新登录" if is_auth_bad else ("-- 获取失败" if err else "加载中…")
                for key, tkey in (("cv_p", "t_p"), ("cv_s", "t_s")):
                    self._draw_ring(self.card[key], None, C_TRACK)
                    self.card[tkey].config(text=placeholder, fg=C_SUB)

            # 底部: 订阅到期 + 更新时间 / 错误或一键重新登录
            if d:
                stamp = datetime.fromtimestamp(d["ts"]).strftime("%H:%M:%S")
                if err:
                    self.lbl_foot.config(text="⚠ 刷新失败 · 上次 %s" % stamp, fg=C_YELLOW)
                elif d.get("sub_until"):
                    left_days = (d["sub_until"] - time.time()) / 86400.0
                    txt = "订阅 %s到期" % fmt_reset_short(d["sub_until"])
                    if left_days < 1:
                        txt += "（今天）"
                    self.lbl_foot.config(text="%s · 更新 %s" % (txt, stamp),
                                         fg=C_RED if left_days < 3 else C_SUB)
                else:
                    self.lbl_foot.config(text="更新 %s" % stamp, fg=C_SUB)
            else:
                is_auth_bad = (err_kind == "auth") or bool(self.view and self.view.get("auth_bad"))
                if is_auth_bad and self.view:
                    self._foot_clickable = True
                    self.lbl_foot.config(text="⚠ 会话已失效 · 点此重新登录", fg=C_RED, cursor="hand2")
                else:
                    self.lbl_foot.config(text=("⚠ %s" % err) if err else "正在获取数据…",
                                         fg=C_RED if err else C_SUB)

        if self.card:
            expires = d.get("credits_expire_at") if d else None
            remaining = expires - time.time() if expires else None
            warn = self.lbl_credit_warn
            if remaining is not None and 0 < remaining <= 172800:
                warn.config(text="⚠ 重置卡即将过期 · %s 到期" % fmt_reset_short(expires),
                            fg=C_RED if remaining <= 86400 else C_YELLOW)
                if not warn.winfo_manager():
                    warn.pack(fill="x", pady=(3, 0))
                    self._fit_height()
            elif warn.winfo_manager():
                warn.pack_forget()
                self._fit_height()

        self.lbl_hint.config(text="每 %ds" % int(self.cfg.get("interval") or POLL_INTERVAL), fg="#6b6d76")
        self._refresh_restart_status()

        # 托盘: 图标反映当前选中账号, 悬停文本展示全部账号实时余量
        pr = None if not d or d["p_used"] is None else 100 - d["p_used"]
        sr = None if not d or d["s_used"] is None else 100 - d["s_used"]
        self._taskbar_quota = (pr, sr)
        self._draw_taskbar()
        try:
            self.tray.icon = render_icon(pr, sr)
            if len(self.accounts) > 1:
                items = []
                for a in self.accounts:
                    ae = snap_results.get(a["home"])
                    ad, aerr = (ae[0], ae[1]) if ae else (None, None)
                    nm = self.display_name(a) + (" [CLI]" if a["live"] else "")
                    items.append((nm, ad, aerr))
                self.tray.title = tooltip_multi(items)
            else:
                self.tray.title = tooltip_text(d, err)
        except Exception:
            pass

    # ---------------- 轮询 ----------------
    def _kick_poll(self):
        self._wake.set()

    def _check_pending_logins(self):
        """检查是否有独立隔离登录的新账号已完成授权, 完成后自动归档入库"""
        if not self._pending_logins:
            return False
        imported = False
        now = time.time()
        for pdir, start_ts in list(self._pending_logins.items()):
            res = import_pending_login(pdir)
            if res:
                nick, uid, dest = res
                self._pending_logins.pop(pdir, None)
                imported = True
                try:
                    self.tray.notify("已自动添加账号: %s" % nick, "Codex 新账号就绪")
                except Exception:
                    pass
            elif now - start_ts > 1800:
                # 超过 30 分钟未登录则清理空临时目录
                self._pending_logins.pop(pdir, None)
                shutil.rmtree(pdir, ignore_errors=True)
        if imported:
            self._reload_accounts()
            self._run_ui(self._render_account)
            self._run_ui(self._apply_state)
        return imported

    def _wait_for_next_poll(self, accounts):
        """等待下一次轮询; 若有账号失效/等待重登/正在添加新账号, 每 2 秒检查一次文件变化"""
        interval = max(15, int(self.cfg.get("interval") or POLL_INTERVAL))
        watch_homes = {}
        for a in accounts:
            h = a["home"]
            if h in self._relogin_watch or a.get("auth_bad"):
                watch_homes[h] = self._relogin_watch.get(h, auth_mtime(h))

        if not watch_homes and not self._pending_logins:
            self._wake.wait(interval)
            self._wake.clear()
            return

        deadline = time.time() + interval
        while not self._stop_event.is_set() and time.time() < deadline:
            if self._wake.wait(min(2.0, max(0.2, deadline - time.time()))):
                self._wake.clear()
                return
            if self._check_pending_logins():
                return
            for h, baseline in list(watch_homes.items()):
                if auth_mtime(h) > baseline:
                    log("检测到 auth.json 已更新, 立即刷新: %s" % h)
                    clear_refresh_cooldown(h)
                    self._relogin_watch.pop(h, None)
                    return

    def _poll_loop(self):
        while not self._stop_event.is_set():
            self._check_pending_logins()
            accts = list(self.accounts)
            # 优先轮询当前选中的账号, 再轮询其余后台账号
            if self.view:
                accts.sort(key=lambda a: 0 if a["uid"] == self.view["uid"] else 1)

            need_rerender = False
            for acct in accts:
                if self._stop_event.is_set():
                    return
                home = acct["home"]
                # 后台账号若处于冷却期且 auth.json 未变化, 跳过重复请求避免刷 401
                if (self.view and acct["uid"] != self.view["uid"]
                        and time.time() < _NO_REFRESH_UNTIL.get(home, 0)
                        and auth_mtime(home) <= _AUTH_MTIME_SEEN.get(home, 0)):
                    continue
                try:
                    data = fetch_usage(home)
                    res = (data, None, None)
                    mark_auth_state(home, True)
                    self._relogin_watch.pop(home, None)
                    if acct.get("auth_bad"):
                        need_rerender = True
                    if acct.get("live") and self._live_auth_bad:
                        self._live_auth_bad = False
                        need_rerender = True
                    self._check_low_quota_notify(acct, data)
                except Exception as e:
                    with self._lock:
                        prev = self.results.get(home)
                        old = prev[0] if prev else None
                    if isinstance(e, AuthError):
                        res = (None, str(e), "auth")  # 旧数据已不可信, 直接清空
                        mark_auth_state(home, False)
                        if acct.get("live"):
                            self._live_auth_bad = True
                        need_rerender = True
                    else:
                        res = (old, str(e)[:80], "net")
                    log("fetch[%s]: %s" % (self.display_name(acct),
                                           traceback.format_exc().splitlines()[-1]))
                with self._lock:
                    self.results[home] = res

            if need_rerender:
                self._reload_accounts()
                self._run_ui(self._render_account)
            if self._stop_event.is_set():
                return
            self._run_ui(self._apply_state)
            self._wait_for_next_poll(list(self.accounts))

    # ---------------- 菜单动作 ----------------
    def toggle_window(self, *_):
        if self.root.state() == "withdrawn":
            self.root.deiconify()
            self.root.lift()
        else:
            self.root.withdraw()
        # 不持久化显示状态: 避免误点 ✕ 后下次启动窗口直接消失

    def _toggle_taskbar(self):
        self.cfg["show_in_taskbar"] = not self.cfg["show_in_taskbar"]
        if self._taskbar_setting is not None:
            self._taskbar_setting.set(self.cfg["show_in_taskbar"])
        save_config(self.cfg)
        self._position_taskbar()
        self.tray.update_menu()

    def _refresh_taskbar(self):
        self._position_taskbar()
        self.root.after(700, self._refresh_taskbar)

    def _position_taskbar(self):
        win = self._taskbar_win
        if not self.cfg["show_in_taskbar"]:
            if win is not None:
                win.withdraw()
            return

        bar, icons = windows_ui.taskbar_rects()
        width = 162
        if bar is None or bar.right - bar.left < width + 16 or windows_ui.fullscreen_active():
            if win is not None:
                win.withdraw()
            return

        height = max(26, min(36, bar.bottom - bar.top - 6))
        anchor = icons.left - 8 if icons and icons.left >= bar.left + width else bar.right - 220
        min_x = bar.left + 8
        max_x = bar.right - width - 8
        self._taskbar_x_limits = (min_x, max_x)
        x = min(max(min_x, anchor - width + self.cfg["taskbar_offset_x"]), max_x)
        y = bar.top + (bar.bottom - bar.top - height) // 2

        if win is None:
            import tkinter as tk
            win = self._taskbar_win = tk.Toplevel(self.root)
            win.withdraw()
            win.overrideredirect(True)
            win.attributes("-topmost", True)
            win.attributes("-toolwindow", True)
            self._taskbar_canvas = tk.Canvas(win, bg=C_BG, bd=0, highlightthickness=0,
                                             cursor="hand2", width=width, height=height)
            self._taskbar_canvas.pack(fill="both", expand=True)
            self._taskbar_canvas.bind("<Configure>", lambda e: self._draw_taskbar())
            self._taskbar_canvas.bind("<Button-3>", self._show_context_menu)

            def start_drag(event):
                self._taskbar_drag = (event.x_root, win.winfo_x(), win.winfo_y(),
                                      self.cfg["taskbar_offset_x"])

            def drag(event):
                origin, win_x, win_y, _ = self._taskbar_drag
                if abs(event.x_root - origin) > 3:
                    left, right = self._taskbar_x_limits
                    win.geometry("%+d%+d" % (min(max(left, win_x + event.x_root - origin), right), win_y))

            def finish_drag(event):
                origin, win_x, _, offset = self._taskbar_drag
                self._taskbar_drag = None
                if abs(event.x_root - origin) > 3:
                    self.cfg["taskbar_offset_x"] = offset + win.winfo_x() - win_x
                    save_config(self.cfg)
                    self._position_taskbar()
                else:
                    self.toggle_window()

            self._taskbar_drag = None
            self._taskbar_canvas.bind("<Button-1>", start_drag)
            self._taskbar_canvas.bind("<B1-Motion>", drag)
            self._taskbar_canvas.bind("<ButtonRelease-1>", finish_drag)

        if self._taskbar_drag is None:
            win.geometry("%dx%d%+d%+d" % (width, height, x, y))
        if win.state() == "withdrawn":
            win.deiconify()
            self._round_corners_of(win)
        windows_ui.keep_topmost(win.winfo_id())

    def _draw_taskbar(self):
        if self._taskbar_win is None:
            return
        canvas = self._taskbar_canvas
        canvas.delete("all")
        height = max(26, canvas.winfo_height())
        for i, (name, quota) in enumerate(zip(("5h", "周"), self._taskbar_quota)):
            y = round(height * (i + 0.5) / 2)
            color = color_for(quota)
            canvas.create_text(9, y, text=name, fill=C_TEXT, font=("微软雅黑", 8, "bold"), anchor="w")
            canvas.create_rectangle(33, y - 2, 107, y + 2, fill=C_TRACK, outline="")
            if quota is not None and quota > 0:
                canvas.create_rectangle(33, y - 2, 33 + min(100, quota) * .74, y + 2,
                                        fill=color, outline="")
            canvas.create_text(151, y, text="--" if quota is None else "%d%%" % round(quota),
                               anchor="e", fill=color, font=("微软雅黑", 8, "bold"))

    def _set_alpha(self, value, persist=True):
        """设置窗口透明度 0.4-1.0"""
        value = max(0.4, min(1.0, round(float(value), 2)))
        self.cfg["alpha"] = value
        if persist:
            save_config(self.cfg)
        try:
            self.root.attributes("-alpha", value)
        except Exception as e:
            log("设置透明度失败: %s" % e)

    def _set_interval(self, sec):
        sec = max(15, min(3600, int(sec)))
        self.cfg["interval"] = sec
        save_config(self.cfg)
        try:
            self.lbl_hint.config(text="每 %ds" % sec, fg="#6b6d76")
        except Exception:
            pass
        self._kick_poll()          # 立即按新频率重排
        log("采集间隔改为 %ds" % sec)

    def _set_notify_low(self, enabled, threshold=None):
        self.cfg["notify_low"] = bool(enabled)
        if threshold is not None:
            self.cfg["notify_threshold"] = int(threshold)
        save_config(self.cfg)
        self._notified_low.clear()
        log("低额度提醒改为: enabled=%s threshold=%s" % (
            self.cfg["notify_low"], self.cfg["notify_threshold"]))

    def _popup_frame(self, window, width, below=False):
        import tkinter as tk

        window.overrideredirect(True)
        window.attributes("-topmost", True)
        window.attributes("-alpha", 0.98)
        window.configure(bg=C_BORDER)
        window.bind("<Escape>", lambda e: window.destroy())
        viewport = tk.Frame(window, bg=C_BG)
        viewport.pack(fill="both", expand=True, padx=1, pady=1)
        canvas = tk.Canvas(viewport, bg=C_BG, highlightthickness=0)
        scrollbar = tk.Scrollbar(viewport, command=canvas.yview)
        canvas.config(yscrollcommand=scrollbar.set)
        canvas.pack(side="left", fill="both", expand=True)
        inner = tk.Frame(canvas, bg=C_BG)
        item = canvas.create_window(0, 0, window=inner, anchor="nw", width=width - 2)
        canvas.bind("<Configure>", lambda e: canvas.itemconfigure(item, width=e.width))

        def wheel(event):
            if scrollbar.winfo_manager():
                canvas.yview_scroll(-int(event.delta / 120), "units")

        window.bind("<MouseWheel>", wheel, add="+")

        def refit():
            window.update_idletasks()
            left, top, right, bottom = windows_ui.work_area()
            height = inner.winfo_reqheight() + 2
            max_height = max(160, bottom - top - 16)
            full_width = width
            if height > max_height:
                scrollbar.pack(side="right", fill="y")
                full_width += scrollbar.winfo_reqwidth()
            else:
                scrollbar.pack_forget()
                canvas.yview_moveto(0)
            canvas.config(scrollregion=(0, 0, width - 2, height - 2))
            height = min(height, max_height)
            full_width = min(full_width, right - left - 16)
            root_x, root_y = self._pos
            root_bottom = root_y + self.root.winfo_height()
            x = min(max(left + 8, root_x + (12 if below else -12)), right - full_width - 8)
            y = root_bottom + 8 if below else root_y - height - 8
            if y < top + 8 or y + height > bottom - 8:
                y = root_y - height - 8 if below else root_bottom + 8
            y = min(max(top + 8, y), bottom - height - 8)
            window.geometry("%dx%d%+d%+d" % (full_width, height, x, y))

        return inner, canvas, refit

    def _open_credit_detail(self):
        import tkinter as tk
        import webbrowser

        if not self.view:
            return
        window = getattr(self, "_credits_win", None)
        if window is not None and window.winfo_exists():
            window.lift()
            return
        window = self._credits_win = tk.Toplevel(self.root)
        inner, _, refit = self._popup_frame(window, self._scale_px(304), below=True)
        pad = self._scale_px(12)
        header = tk.Frame(inner, bg=C_BG)
        header.pack(fill="x", padx=pad, pady=(pad, 5))
        tk.Label(header, text="⚡ 重置卡详情", bg=C_BG, fg=C_ACCENT, font=self.f_name).pack(side="left")
        close = tk.Label(header, text="✕", bg=C_BG, fg=C_SUB, font=self.f_sub, cursor="hand2")
        close.pack(side="right")
        close.bind("<Button-1>", lambda e: window.destroy())

        def separator():
            tk.Frame(inner, bg=C_BORDER, height=1).pack(fill="x", padx=pad, pady=(4, 6))

        def row(title, value, color=C_TEXT):
            frame = tk.Frame(inner, bg=C_BG)
            frame.pack(fill="x", padx=pad, pady=(0, self._scale_px(4)))
            tk.Label(frame, text=title, bg=C_BG, fg=C_SUB, font=self.f_tiny).pack(side="left")
            tk.Label(frame, text=value, bg=C_BG, fg=color, font=self.f_sub, anchor="e").pack(side="right")

        def count(value):
            return "%d 张" % value if value is not None else "接口未返回"

        def date(seconds):
            return datetime.fromtimestamp(seconds).strftime("%Y-%m-%d %H:%M") if seconds else "未返回到期时间"

        with self._lock:
            data = self.results.get(self.view["home"], (None, None, None))[0]
        separator()
        row("所属账号", self._ellipsize(self.display_name(self.view), 24))
        if not data:
            row("状态", "暂无数据，请稍后刷新", C_SUB)
        else:
            row("可用重置卡", count(data["credits"]), C_ACCENT)
            available = count(data["applicable_credits"])
            if data["applicable_credits"] == 0:
                available += "（当前不可用）" if data["limit_reached"] else "（未触及限流）"
            row("当前可立即使用", available, C_GREEN if (data["applicable_credits"] or 0) > 0 else C_SUB)
            if data["reset_cards"]:
                for card in data["reset_cards"]:
                    expiry = card["expires_at"]
                    remaining = expiry - time.time() if expiry else math.inf
                    tk.Label(inner, text=card["title"], bg=C_BG, fg=C_TEXT, font=self.f_sub,
                             wraplength=self._scale_px(270), justify="left").pack(anchor="w", padx=pad)
                    suffix = "（已过期）" if remaining <= 0 else (" 到期" if expiry else "")
                    color = C_RED if remaining <= 86400 else C_YELLOW if remaining <= 172800 else C_SUB
                    tk.Label(inner, text=date(expiry) + suffix, bg=C_BG, fg=color,
                             font=self.f_tiny).pack(anchor="w", padx=pad, pady=(0, self._scale_px(6)))
            elif data["credits"] == 0 or data["reset_cards"] == []:
                row("重置卡明细", "暂无可用重置卡", C_SUB)
            elif data["credits_expire_at"]:
                row("过期时间（本地）", date(data["credits_expire_at"]))
            else:
                row("过期时间", "暂未取得重置卡明细", C_SUB)
            row("额外额度余额", data["credits_balance"], C_SUB)

        separator()
        tk.Label(inner, text="触及 5h 或每周速率限制时，可用重置卡重置当前窗口额度。",
                 bg=C_BG, fg=C_SUB, font=self.f_tiny, wraplength=self._scale_px(270),
                 justify="left").pack(anchor="w", padx=pad)
        link = tk.Label(inner, text="在浏览器打开 Codex 用量页 ↗", bg=C_CARD, fg=C_ACCENT,
                        font=self.f_tiny, cursor="hand2", takefocus=True, pady=self._scale_px(4))
        link.pack(fill="x", padx=pad, pady=(pad, pad))
        link.bind("<Button-1>", lambda e: webbrowser.open(USAGE_URL))
        link.bind("<Return>", lambda e: webbrowser.open(USAGE_URL))
        refit()
        self._round_corners_of(window)
        window.focus_force()

    # ---------------- 设置面板 ----------------
    def _open_settings(self, custom_seconds=None, previous_status=None, scroll=0):
        import tkinter as tk
        from tkinter import font as tkfont

        win = getattr(self, "_set_win", None)
        if win is not None and win.winfo_exists():
            win.lift()
            win.focus_force()
            return

        w = tk.Toplevel(self.root)
        self._set_win = w
        inner, scroller, refit = self._popup_frame(w, self._scale_px(416))

        f_bold = tkfont.Font(family="微软雅黑", size=self._scale_px(10), weight="bold")
        f_sub = tkfont.Font(family="微软雅黑", size=self._scale_px(8.4))
        f_tiny = tkfont.Font(family="微软雅黑", size=self._scale_px(7.6))

        # 标题 + 关闭
        head = tk.Frame(inner, bg=C_BG)
        head.pack(fill="x", padx=12, pady=(9, 0))
        tk.Label(head, text="设置", bg=C_BG, fg=C_TEXT, font=f_bold).pack(side="left")
        xb = tk.Label(head, text="✕", bg=C_BG, fg=C_SUB, font=f_sub, cursor="hand2")
        xb.pack(side="right")
        xb.bind("<Button-1>", lambda e: w.destroy())
        xb.bind("<Enter>", lambda e: xb.config(fg=C_RED))
        xb.bind("<Leave>", lambda e: xb.config(fg=C_SUB))

        # ---- 多账号 ----
        rowA = tk.Frame(inner, bg=C_BG)
        rowA.pack(fill="x", padx=12, pady=(10, 0))
        lbl_acct = tk.Label(rowA, text="多账号 (%d)" % len(self.accounts),
                            bg=C_BG, fg=C_SUB, font=f_sub)
        lbl_acct.pack(side="left")

        acct_box = tk.Frame(inner, bg=C_BG)
        acct_box.pack(fill="x", padx=12, pady=(3, 0))

        status = tk.Label(inner, text="", bg=C_BG, fg=C_SUB, font=f_tiny, anchor="w")
        if previous_status:
            status.config(text=previous_status[0], fg=previous_status[1])
        status.pack(fill="x", padx=12, pady=(3, 0))

        def render_accts():
            for ch in acct_box.winfo_children():
                ch.destroy()
            lbl_acct.config(text="多账号 (%d)" % len(self.accounts))
            for a in self.accounts:
                cur = self.view and a["uid"] == self.view["uid"]
                row = tk.Frame(acct_box, bg=C_BG)
                row.pack(fill="x", pady=(1, 0))
                # 点昵称 = 切换显示
                name = tk.Label(row, text=("%s %s" % ("✓" if cur else "○",
                                                      self._ellipsize(self.display_name(a), 29))),
                                bg=C_BG, fg=C_ACCENT if cur else C_TEXT, font=f_sub,
                                anchor="w", cursor="hand2")
                name.pack(side="left")
                if a.get("auth_bad"):
                    tk.Label(row, text="需重新登录", bg=C_BG, fg=C_RED,
                             font=f_tiny).pack(side="left", padx=(6, 0))
                else:
                    tk.Label(row, text=("CLI当前" if a["live"] else "已保存"),
                             bg=C_BG, fg=C_GREEN if a["live"] else "#6b6d76",
                             font=f_tiny).pack(side="left", padx=(6, 0))
                if not a["live"]:
                    xr = tk.Label(row, text="✕", bg=C_BG, fg=C_SUB, font=f_tiny, cursor="hand2")
                    xr.pack(side="right")
                    xr.bind("<Enter>", lambda e, lb=xr: lb.config(fg=C_RED))
                    xr.bind("<Leave>", lambda e, lb=xr: lb.config(fg=C_SUB))
                    xr.bind("<Button-1>", lambda e, uid=a["uid"]: do_remove(uid))
                    ed = tk.Label(row, text="✎", bg=C_BG, fg=C_SUB, font=f_tiny, cursor="hand2")
                    ed.pack(side="right", padx=(0, 8))
                    ed.bind("<Enter>", lambda e, lb=ed: lb.config(fg=C_ACCENT))
                    ed.bind("<Leave>", lambda e, lb=ed: lb.config(fg=C_SUB))
                    ed.bind("<Button-1>", lambda e, uid=a["uid"], cur=a.get("label") or a["nick"]: do_rename(uid, cur))
                    if not a.get("auth_bad"):
                        sw = tk.Label(row, text="切为CLI", bg=C_CARD, fg=C_ACCENT,
                                      font=f_tiny, padx=5, pady=1, cursor="hand2")
                        sw.pack(side="right", padx=(0, 8))
                        sw.bind("<Enter>", lambda e, lb=sw: lb.config(bg=C_ACCENT, fg="#12141a"))
                        sw.bind("<Leave>", lambda e, lb=sw: lb.config(bg=C_CARD, fg=C_ACCENT))
                        sw.bind("<Button-1>", lambda e, uid=a["uid"]: do_switch_cli(uid))
                if a.get("auth_bad"):
                    rl = tk.Label(row, text="重新登录", bg=C_BG, fg=C_RED,
                                  font=f_tiny, cursor="hand2")
                    rl.pack(side="right", padx=(0, 8))
                    rl.bind("<Enter>", lambda e, lb=rl: lb.config(fg="#ff8585"))
                    rl.bind("<Leave>", lambda e, lb=rl: lb.config(fg=C_RED))
                    rl.bind("<Button-1>", lambda e, acct=a: do_relogin(acct))
                name.bind("<Button-1>", lambda e, uid=a["uid"]: (
                    self._set_display(uid), render_accts(), refit()))

        def do_switch_cli(uid):
            ok, msg = self._switch_cli(uid)
            status.config(text=msg, fg=C_GREEN if ok else C_RED)
            render_accts()
            refit()

        def do_remove(uid):
            ok = remove_account(uid)
            if not ok:
                status.config(text="当前登录的账号无法删除", fg=C_RED)
                return
            if self.cfg.get("display_uid") == uid:
                self.cfg["display_uid"] = ""
                save_config(self.cfg)
            self._reload_accounts()
            self._render_account()
            self._apply_state()
            self._kick_poll()
            status.config(text="已删除", fg=C_SUB)
            render_accts()
            refit()

        def do_rename(uid, cur):
            from tkinter import simpledialog
            w.attributes("-topmost", False)
            try:
                name = simpledialog.askstring("命名账号", "显示名称（留空恢复昵称）:",
                                              initialvalue=cur, parent=w)
            finally:
                w.attributes("-topmost", True)
            if name is None:
                return
            rename_account(uid, name.strip())
            self._reload_accounts()
            self._render_account()
            self._apply_state()
            status.config(text="已重命名", fg=C_GREEN)
            render_accts()
            refit()

        def do_sync(e=None):
            ok, msg = self._sync_login()
            status.config(text=msg, fg=C_GREEN if ok else C_RED)
            if ok:
                render_accts()
                refit()

        def do_login(e=None):
            ok, msg = self._launch_new_login()
            status.config(text=msg, fg=C_SUB if ok else C_RED)

        def do_relogin(acct):
            ok, msg = self._launch_relogin(acct)
            status.config(text=msg, fg=C_SUB if ok else C_RED)

        btns = tk.Frame(inner, bg=C_BG)
        btns.pack(fill="x", padx=12, pady=(5, 0))
        b1 = tk.Label(btns, text="添加新账号(独立隔离)", bg=C_ACCENT, fg="#12141a", font=f_tiny,
                      padx=8, pady=3, cursor="hand2")
        b1.pack(side="left")
        b1.bind("<Button-1>", do_login)
        b2 = tk.Label(btns, text="同步当前CLI", bg=C_CARD, fg=C_TEXT, font=f_tiny,
                      padx=8, pady=3, cursor="hand2")
        b2.pack(side="left", padx=(5, 0))
        b2.bind("<Button-1>", do_sync)
        self._restart_button = tk.Label(btns, bg=C_YELLOW, fg=C_BG, font=f_tiny,
                                        padx=6, pady=3, cursor="hand2")
        self._restart_button.bind("<Button-1>", lambda e: self._prompt_restart(w))
        self._refresh_restart_status()

        tip = tk.Label(inner, text="切号请点『切为CLI』，切勿执行 codex logout 以免授权失效",
                       bg=C_BG, fg="#6b6d76", font=f_tiny, anchor="w")
        tip.pack(fill="x", padx=12, pady=(3, 0))

        # ---- 透明度 ----
        row1 = tk.Frame(inner, bg=C_BG)
        row1.pack(fill="x", padx=12, pady=(12, 0))
        tk.Label(row1, text="窗口透明度", bg=C_BG, fg=C_SUB, font=f_sub).pack(side="left")
        val_lbl = tk.Label(row1, text="%d%%" % round(float(self.cfg["alpha"]) * 100),
                           bg=C_BG, fg=C_ACCENT, font=f_sub)
        val_lbl.pack(side="right")

        cur_a = round(float(self.cfg["alpha"]) * 100)
        a_chips = tk.Frame(inner, bg=C_BG)
        a_chips.pack(fill="x", padx=12, pady=(4, 0))
        a_lbls = {}

        def pick_alpha(p):
            val_lbl.config(text="%d%%" % p)
            for v, lbl in a_lbls.items():
                lbl.config(bg=C_ACCENT if v == p else C_CARD,
                           fg="#12141a" if v == p else C_SUB)
            self._set_alpha(p / 100.0, persist=True)

        for p in (100, 80, 60, 40):
            lbl = tk.Label(a_chips, text="%d%%" % p, bg=C_CARD, fg=C_SUB, font=f_sub,
                           padx=7, pady=3, cursor="hand2")
            lbl.pack(side="left", padx=(0, 5))
            lbl.bind("<Button-1>", lambda e, v=p: pick_alpha(v))
            a_lbls[p] = lbl
        if cur_a in a_lbls:
            a_lbls[cur_a].config(bg=C_ACCENT, fg="#12141a")

        tk.Label(a_chips, text="字体大小", bg=C_BG, fg=C_SUB, font=f_sub).pack(
            side="left", padx=(self._scale_px(12), 4))
        selected = self.cfg.get("font_size", "medium" if self.cfg.get("medium_font") else "small")
        for size, text in zip(FONT_SCALES, ("小", "中", "大")):
            chip = tk.Label(a_chips, text=text, bg=C_ACCENT if size == selected else C_CARD,
                            fg=C_BG if size == selected else C_SUB, font=f_sub,
                            padx=self._scale_px(6), pady=3, cursor="hand2", takefocus=True)
            chip.pack(side="left", padx=(0, 5))

            def change_size(event, size=size):
                self._set_font_size(size, ent2.get(), (status.cget("text"), status.cget("fg")),
                                    scroller.yview()[0])

            chip.bind("<Button-1>", change_size)
            chip.bind("<Return>", change_size)

        switches = tk.Frame(inner, bg=C_BG)
        switches.pack(fill="x", padx=12, pady=(10, 0))
        taskbar = self._taskbar_setting = tk.BooleanVar(w, value=self.cfg["show_in_taskbar"])
        autostart = tk.BooleanVar(w, value=autostart_enabled())

        def clear_taskbar_setting(event):
            if event.widget is w:
                self._taskbar_setting = None

        w.bind("<Destroy>", clear_taskbar_setting)

        def toggle_autostart():
            try:
                set_autostart(autostart.get())
                self.tray.update_menu()
            except OSError:
                autostart.set(autostart_enabled())
                status.config(text="无法保存开机自启设置", fg=C_RED)

        for text, variable, command in (
            ("任务栏常驻显示", taskbar, self._toggle_taskbar),
            ("开机自启", autostart, toggle_autostart),
        ):
            tk.Checkbutton(switches, text=text, variable=variable, command=command, bg=C_BG,
                           activebackground=C_BG, selectcolor=C_CARD, fg=C_SUB,
                           activeforeground=C_TEXT, font=f_sub).pack(side="left", padx=(0, 12))

        # ---- 低额度提醒 ----
        rowN = tk.Frame(inner, bg=C_BG)
        rowN.pack(fill="x", padx=12, pady=(10, 0))
        tk.Label(rowN, text="低额度提醒", bg=C_BG, fg=C_SUB, font=f_sub).pack(side="left")

        n_chips = tk.Frame(inner, bg=C_BG)
        n_chips.pack(fill="x", padx=12, pady=(4, 0))
        n_lbls = {}
        cur_n = int(self.cfg.get("notify_threshold") or 20) if self.cfg.get("notify_low", True) else 0

        def pick_notify(val):
            for v, lbl in n_lbls.items():
                lbl.config(bg=C_ACCENT if v == val else C_CARD,
                           fg="#12141a" if v == val else C_SUB)
            if val == 0:
                self._set_notify_low(False)
            else:
                self._set_notify_low(True, val)

        for val, text in ((0, "关闭"), (10, "≤10%"), (20, "≤20%"), (30, "≤30%")):
            lbl = tk.Label(n_chips, text=text, bg=C_CARD, fg=C_SUB, font=f_sub,
                           padx=7, pady=3, cursor="hand2")
            lbl.pack(side="left", padx=(0, 5))
            lbl.bind("<Button-1>", lambda e, v=val: pick_notify(v))
            n_lbls[val] = lbl
        if cur_n in n_lbls:
            n_lbls[cur_n].config(bg=C_ACCENT, fg="#12141a")

        # ---- 采集频率 ----
        row2 = tk.Frame(inner, bg=C_BG)
        row2.pack(fill="x", padx=12, pady=(10, 0))
        tk.Label(row2, text="采集频率", bg=C_BG, fg=C_SUB, font=f_sub).pack(side="left")

        cur = int(self.cfg.get("interval") or 60)
        var = tk.IntVar(value=cur)
        chips = tk.Frame(inner, bg=C_BG)
        chips.pack(fill="x", padx=12, pady=(4, 0))
        chip_lbls = {}

        def pick(sec):
            var.set(sec)
            for s, lbl in chip_lbls.items():
                lbl.config(bg=C_ACCENT if s == sec else C_CARD,
                           fg="#12141a" if s == sec else C_SUB)
            self._set_interval(sec)

        for sec in (15, 30, 60, 120, 300):
            lbl = tk.Label(chips, text="%ds" % sec, bg=C_CARD, fg=C_SUB, font=f_sub,
                           padx=7, pady=3, cursor="hand2")
            lbl.pack(side="left", padx=(0, 5))
            lbl.bind("<Button-1>", lambda e, s=sec: pick(s))
            chip_lbls[sec] = lbl
        # 初始化高亮
        for s, lbl in chip_lbls.items():
            if s == cur:
                lbl.config(bg=C_ACCENT, fg="#12141a")

        # 自定义输入
        row3 = tk.Frame(inner, bg=C_BG)
        row3.pack(fill="x", padx=12, pady=(10, 0))
        tk.Label(row3, text="自定义(秒)", bg=C_BG, fg=C_SUB, font=f_sub).pack(side="left")
        ent2 = tk.Entry(row3, width=7, bg=C_CARD, fg=C_TEXT, insertbackground=C_TEXT,
                        relief="flat", font=f_sub)
        ent2.pack(side="left", padx=(6, 0))
        if cur not in chip_lbls:
            ent2.insert(0, str(cur))

        def apply_custom(e=None):
            raw = ent2.get().strip()
            try:
                sec = int(raw)
            except ValueError:
                status.config(text="请输入 15~3600 之间的整数秒", fg=C_RED)
                return
            sec = max(15, min(3600, sec))
            ent2.delete(0, "end")
            ent2.insert(0, str(sec))
            var.set(sec)
            for s, lbl in chip_lbls.items():
                lbl.config(bg=C_ACCENT if s == sec else C_CARD,
                           fg="#12141a" if s == sec else C_SUB)
            self._set_interval(sec)
            status.config(text="采集间隔已设为 %ds" % sec, fg=C_GREEN)

        ent2.bind("<Return>", apply_custom)
        if custom_seconds is not None:
            ent2.delete(0, "end")
            ent2.insert(0, custom_seconds)
        tk.Label(row3, text="回车生效", bg=C_BG, fg="#6b6d76", font=f_sub).pack(side="left", padx=(6, 0))

        tk.Label(inner, text="修改即时生效, 自动保存", bg=C_BG, fg="#6b6d76",
                 font=f_sub).pack(anchor="w", padx=12, pady=(12, 2))
        tk.Label(inner, text=version_label(), bg=C_BG, fg=C_SUB,
                 font=f_tiny).pack(anchor="w", padx=12, pady=(0, 8))

        # 拖动
        drag = {}
        def ds(e): drag["x"], drag["y"] = e.x, e.y
        def dm(e):
            if drag:
                w.geometry("+%d+%d" % (w.winfo_x() + e.x - drag["x"], w.winfo_y() + e.y - drag["y"]))
        for wid in (head,):
            wid.bind("<Button-1>", ds)
            wid.bind("<B1-Motion>", dm)

        render_accts()
        refit()
        scroller.yview_moveto(scroll)
        self._round_corners_of(w)
        w.bind("<Escape>", lambda e: w.destroy())
        w.focus_force()

    def _round_corners_of(self, win):
        try:
            import ctypes
            win.update_idletasks()
            hwnd = ctypes.windll.user32.GetParent(win.winfo_id())
            pref = ctypes.c_int(2)
            ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 33, ctypes.byref(pref), ctypes.sizeof(pref))
        except Exception:
            pass

    def _toggle_autostart(self):
        set_autostart(not autostart_enabled())

    def _quit(self):
        self._stop_event.set()
        self._wake.set()
        try:
            self.tray.stop()
        except Exception:
            pass
        try:
            self.root.destroy()
        except Exception:
            pass


if __name__ == "__main__":
    # 调试开关: 目录下存在 debug.on 时把 stderr / 崩溃栈写进 tray_err.txt
    if os.path.exists(os.path.join(DATA_DIR, "debug.on")):
        try:
            import faulthandler
            _ef = open(os.path.join(DATA_DIR, "tray_err.txt"), "a", buffering=1,
                       encoding="utf-8")
            sys.stderr = _ef
            sys.stdout = _ef
            faulthandler.enable(_ef)
        except Exception:
            pass
    # 单实例保护: 已在运行则直接退出 (183 = Windows ERROR_ALREADY_EXISTS)
    try:
        import ctypes
        mutex = ctypes.windll.kernel32.CreateMutexW(None, False, "CodexUsageTray_Mutex")
        if ctypes.windll.kernel32.GetLastError() == 183:
            sys.exit(0)
    except Exception:
        pass
    try:
        App()
    except Exception:
        log("FATAL: %s" % traceback.format_exc())
        raise
