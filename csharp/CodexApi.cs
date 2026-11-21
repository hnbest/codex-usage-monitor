using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace CodexUsageMonitor
{
    public static class CodexApi
    {
        public const string ApiUsage = "https://chatgpt.com/backend-api/wham/usage";
        private const string ApiResetCredits = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";
        public const string ApiRefresh = "https://auth.openai.com/oauth/token";
        public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
        private const string AutostartReg = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string AutostartName = "CodexUsageTray";

        private static readonly object StateLock = new object();
        private static readonly Dictionary<string, double> NoRefreshUntil = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> AuthMtimeSeen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public static AppConfig LoadConfig()
        {
            AppConfig cfg = new AppConfig();
            Dictionary<string, object> d = JsonUtil.ReadDict(AppPaths.ConfigPath);
            if (d == null) return cfg;
            double? a = JsonUtil.GetDouble(d, "alpha");
            if (a.HasValue) cfg.alpha = Math.Max(0.4, Math.Min(1.0, a.Value));
            switch (JsonUtil.GetStr(d, "font_size"))
            {
                case "small": cfg.font_size = WindowFontSize.Small; break;
                case "medium": cfg.font_size = WindowFontSize.Medium; break;
                case "large": cfg.font_size = WindowFontSize.Large; break;
                default:
                    if (JsonUtil.GetBool(d, "medium_font", false)) cfg.font_size = WindowFontSize.Medium;
                    break;
            }
            int? iv = JsonUtil.GetInt(d, "interval");
            if (iv.HasValue) cfg.interval = Math.Max(15, Math.Min(3600, iv.Value));
            cfg.window_x = JsonUtil.GetInt(d, "window_x");
            cfg.window_y = JsonUtil.GetInt(d, "window_y");
            string du = JsonUtil.GetStr(d, "display_uid");
            if (du != null) cfg.display_uid = du;
            string vm = JsonUtil.GetStr(d, "view_mode");
            if (vm == "all" || vm == "single") cfg.view_mode = vm;
            cfg.notify_low = JsonUtil.GetBool(d, "notify_low", true);
            int? nt = JsonUtil.GetInt(d, "notify_threshold");
            if (nt.HasValue) cfg.notify_threshold = nt.Value;
            cfg.show_in_taskbar = JsonUtil.GetBool(d, "show_in_taskbar", true);
            int? tx = JsonUtil.GetInt(d, "taskbar_offset_x");
            if (tx.HasValue) cfg.taskbar_offset_x = tx.Value;
            cfg.enable_antigravity = JsonUtil.GetBool(d, "enable_antigravity", true);
            string tm = JsonUtil.GetStr(d, "taskbar_mode");
            cfg.taskbar_mode = (tm == "follow") ? TaskbarDisplayMode.FollowCurrent : TaskbarDisplayMode.Dual;
            string cid = JsonUtil.GetStr(d, "agy_oauth_client_id");
            if (cid != null) cfg.agy_oauth_client_id = cid;
            string csec = JsonUtil.GetStr(d, "agy_oauth_client_secret");
            if (csec != null) cfg.agy_oauth_client_secret = csec;
            return cfg;
        }

        public static void SaveConfig(AppConfig cfg)
        {
            try
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["alpha"] = cfg.alpha;
                d["font_size"] = cfg.font_size.ToString().ToLowerInvariant();
                d["interval"] = cfg.interval;
                d["window_x"] = cfg.window_x;
                d["window_y"] = cfg.window_y;
                d["display_uid"] = cfg.display_uid ?? "";
                d["view_mode"] = cfg.view_mode ?? "single";
                d["notify_low"] = cfg.notify_low;
                d["notify_threshold"] = cfg.notify_threshold;
                d["show_in_taskbar"] = cfg.show_in_taskbar;
                d["taskbar_offset_x"] = cfg.taskbar_offset_x;
                d["enable_antigravity"] = cfg.enable_antigravity;
                d["taskbar_mode"] = cfg.taskbar_mode == TaskbarDisplayMode.FollowCurrent ? "follow" : "dual";
                if (!string.IsNullOrEmpty(cfg.agy_oauth_client_id)) d["agy_oauth_client_id"] = cfg.agy_oauth_client_id;
                if (!string.IsNullOrEmpty(cfg.agy_oauth_client_secret)) d["agy_oauth_client_secret"] = cfg.agy_oauth_client_secret;
                JsonUtil.WriteAtomic(AppPaths.ConfigPath, d);
            }
            catch (Exception ex)
            {
                Logger.Log("SaveConfig 失败: " + ex.Message);
            }
        }

        public static string CodexHome(string home)
        {
            if (!string.IsNullOrEmpty(home)) return home;
            string env = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        public static long AuthMtimeTicks(string home)
        {
            try
            {
                string p = Path.Combine(CodexHome(home), "auth.json");
                if (!File.Exists(p)) return 0;
                return File.GetLastWriteTimeUtc(p).Ticks;
            }
            catch { return 0; }
        }

        public static void ClearRefreshCooldown(string home)
        {
            string k = CodexHome(home);
            lock (StateLock)
            {
                NoRefreshUntil.Remove(k);
            }
        }

        public static bool IsInCooldownUnchanged(string home)
        {
            string k = CodexHome(home);
            long curMt = AuthMtimeTicks(home);
            lock (StateLock)
            {
                double until;
                long seen;
                if (NoRefreshUntil.TryGetValue(k, out until) && FormatUtil.NowUnix() < until)
                {
                    if (AuthMtimeSeen.TryGetValue(k, out seen) && curMt <= seen)
                        return true;
                }
            }
            return false;
        }

        public static Dictionary<string, object> ReadAuth(string home)
        {
            string p = Path.Combine(CodexHome(home), "auth.json");
            return JsonUtil.ReadDict(p);
        }

        public static Dictionary<string, object> JwtClaims(Dictionary<string, object> auth)
        {
            try
            {
                Dictionary<string, object> tokens = JsonUtil.GetDict(auth, "tokens");
                string idToken = JsonUtil.GetStr(tokens, "id_token");
                if (string.IsNullOrEmpty(idToken)) return new Dictionary<string, object>();
                string[] parts = idToken.Split('.');
                if (parts.Length < 2) return new Dictionary<string, object>();
                string payload = parts[1].Replace('-', '+').Replace('_', '/');
                int mod = payload.Length % 4;
                if (mod > 0) payload += new string('=', 4 - mod);
                byte[] bytes = Convert.FromBase64String(payload);
                string json = Encoding.UTF8.GetString(bytes);
                return JsonUtil.ParseDict(json) ?? new Dictionary<string, object>();
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        public static void AuthIdentity(string home, out string nick, out string uid)
        {
            string email;
            AuthIdentity(home, out nick, out uid, out email);
        }

        public static void AuthIdentity(string home, out string nick, out string uid, out string email)
        {
            nick = "";
            uid = "";
            email = "";
            Dictionary<string, object> auth = ReadAuth(home);
            if (auth == null) return;
            Dictionary<string, object> c = JwtClaims(auth);
            string name = JsonUtil.GetStr(c, "name");
            email = JsonUtil.GetStr(c, "email") ?? "";
            if (!string.IsNullOrEmpty(email)) nick = email;
            else if (!string.IsNullOrEmpty(name)) nick = name;

            string sub = JsonUtil.GetStr(c, "sub");
            if (!string.IsNullOrEmpty(sub)) uid = sub;
            else
            {
                Dictionary<string, object> tokens = JsonUtil.GetDict(auth, "tokens");
                uid = JsonUtil.GetStr(tokens, "account_id") ?? "";
            }
        }

        private static string Slug(string name)
        {
            string s = Regex.Replace((name ?? "").Trim(), @"[^0-9A-Za-z_\-\u4e00-\u9fa5]+", "_").Trim('_');
            if (s.Length > 32) s = s.Substring(0, 32);
            return string.IsNullOrEmpty(s) ? "account" : s;
        }

        private static bool IsPendingDir(string name)
        {
            return name != null && name.StartsWith("_pending_", StringComparison.OrdinalIgnoreCase);
        }

        private static string MetaPath(string home)
        {
            return Path.Combine(home, "meta.json");
        }

        public static Dictionary<string, object> ReadMeta(string home)
        {
            return JsonUtil.ReadDict(MetaPath(home)) ?? new Dictionary<string, object>();
        }

        public static void MarkAuthState(string home, bool ok)
        {
            if (string.IsNullOrEmpty(home)) return;
            string fullHome = Path.GetFullPath(home);
            string fullAccts = Path.GetFullPath(AppPaths.AccountsDir);
            if (!fullHome.StartsWith(fullAccts, StringComparison.OrdinalIgnoreCase)) return;

            Dictionary<string, object> meta = ReadMeta(home);
            if (ok)
            {
                if (meta.ContainsKey("needs_login"))
                {
                    meta.Remove("needs_login");
                    meta.Remove("bad_at");
                    try { JsonUtil.WriteAtomic(MetaPath(home), meta); Logger.Log("账号已恢复: " + home); }
                    catch (Exception ex) { Logger.Log("MarkAuthState 失败: " + ex.Message); }
                }
            }
            else if (!JsonUtil.GetBool(meta, "needs_login", false))
            {
                meta["needs_login"] = true;
                meta["bad_at"] = FormatUtil.NowUnix();
                try { JsonUtil.WriteAtomic(MetaPath(home), meta); }
                catch (Exception ex) { Logger.Log("MarkAuthState 失败: " + ex.Message); }
            }
        }

        public static string FindSnapshotDir(string uid)
        {
            if (string.IsNullOrEmpty(uid) || !Directory.Exists(AppPaths.AccountsDir)) return null;
            string[] dirs = Directory.GetDirectories(AppPaths.AccountsDir);
            Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
            foreach (string d in dirs)
            {
                string name = Path.GetFileName(d);
                if (IsPendingDir(name)) continue;
                string n, u;
                AuthIdentity(d, out n, out u);
                if (u == uid || JsonUtil.GetStr(ReadMeta(d), "uid") == uid)
                    return d;
            }
            return null;
        }

        private static string EnsureSnapshotDir(string nick, string uid)
        {
            string dest = FindSnapshotDir(uid);
            if (dest == null)
            {
                string baseName = Slug(!string.IsNullOrEmpty(nick) ? nick : (uid.Length > 8 ? uid.Substring(uid.Length - 8) : uid));
                dest = Path.Combine(AppPaths.AccountsDir, baseName);
                int n = 2;
                while (Directory.Exists(dest))
                {
                    string en, eu;
                    AuthIdentity(dest, out en, out eu);
                    if (string.IsNullOrEmpty(eu) || eu == uid) break;
                    dest = Path.Combine(AppPaths.AccountsDir, baseName + "_" + n);
                    n++;
                }
            }
            Directory.CreateDirectory(dest);
            return dest;
        }

        public static List<AccountInfo> ListAccounts()
        {
            List<AccountInfo> list = new List<AccountInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            string live = CodexHome(null);
            string nick, uid, email;
            AuthIdentity(live, out nick, out uid, out email);
            if (!string.IsNullOrEmpty(uid))
            {
                string snap = FindSnapshotDir(uid);
                string liveLabel = snap != null ? (JsonUtil.GetStr(ReadMeta(snap), "label") ?? "") : "";
                list.Add(new AccountInfo
                {
                    Nick = string.IsNullOrEmpty(nick) ? "当前登录" : nick,
                    Email = email,
                    Uid = uid,
                    Home = live,
                    Live = true,
                    Label = liveLabel,
                    AuthBad = false
                });
                seen.Add(uid);
            }

            if (Directory.Exists(AppPaths.AccountsDir))
            {
                string[] dirs = Directory.GetDirectories(AppPaths.AccountsDir);
                Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                foreach (string d in dirs)
                {
                    string name = Path.GetFileName(d);
                    if (IsPendingDir(name)) continue;
                    if (!File.Exists(Path.Combine(d, "auth.json"))) continue;
                    string n, u, em;
                    AuthIdentity(d, out n, out u, out em);
                    if (string.IsNullOrEmpty(u) || seen.Contains(u)) continue;
                    seen.Add(u);
                    Dictionary<string, object> m = ReadMeta(d);
                    list.Add(new AccountInfo
                    {
                        Nick = string.IsNullOrEmpty(n) ? name : n,
                        Email = em,
                        Uid = u,
                        Home = d,
                        Live = false,
                        Label = JsonUtil.GetStr(m, "label") ?? "",
                        AuthBad = JsonUtil.GetBool(m, "needs_login", false)
                    });
                }
            }
            return list;
        }

        public static bool SaveCurrentLogin(out string msg, out string uidOut)
        {
            uidOut = "";
            string live = CodexHome(null);
            string src = Path.Combine(live, "auth.json");
            string nick, uid;
            AuthIdentity(live, out nick, out uid);
            if (string.IsNullOrEmpty(uid) || !File.Exists(src))
            {
                msg = "当前没有检测到登录, 请先 codex login";
                return false;
            }
            string dest = EnsureSnapshotDir(nick, uid);
            JsonUtil.CopyAtomic(src, Path.Combine(dest, "auth.json"));
            Dictionary<string, object> meta = ReadMeta(dest);
            meta["nick"] = nick;
            meta["uid"] = uid;
            meta["saved_at"] = FormatUtil.NowUnix();
            meta.Remove("needs_login");
            meta.Remove("bad_at");
            JsonUtil.WriteAtomic(MetaPath(dest), meta);
            ClearRefreshCooldown(dest);
            string disp = JsonUtil.GetStr(meta, "label");
            if (string.IsNullOrEmpty(disp)) disp = nick;
            Logger.Log("已保存账号快照: " + nick + " -> " + dest);
            uidOut = uid;
            msg = "已保存 " + disp;
            return true;
        }

        public static bool SwitchCliAccount(string uid, out string msg)
        {
            if (string.IsNullOrEmpty(uid)) { msg = "无效账号"; return false; }
            string live = CodexHome(null);
            string liveAuth = Path.Combine(live, "auth.json");
            string curNick, curUid;
            AuthIdentity(live, out curNick, out curUid);
            if (curUid == uid) { msg = "当前 CLI 已是该账号"; return true; }

            string targetDir = FindSnapshotDir(uid);
            if (targetDir == null) { msg = "未找到该账号的快照目录"; return false; }
            string targetAuth = Path.Combine(targetDir, "auth.json");
            if (!File.Exists(targetAuth)) { msg = "该账号缺少 auth.json 凭证"; return false; }
            Dictionary<string, object> targetMeta = ReadMeta(targetDir);
            if (JsonUtil.GetBool(targetMeta, "needs_login", false))
            {
                msg = "该账号会话已失效, 请先重新登录";
                return false;
            }
            string tNick, tUid;
            AuthIdentity(targetDir, out tNick, out tUid);
            if (string.IsNullOrEmpty(tUid)) { msg = "目标账号凭证损坏, 请重新登录"; return false; }

            // 1. 先归档当前 CLI 最新凭证
            if (!string.IsNullOrEmpty(curUid) && File.Exists(liveAuth))
            {
                string curDest = EnsureSnapshotDir(curNick, curUid);
                JsonUtil.CopyAtomic(liveAuth, Path.Combine(curDest, "auth.json"));
                Dictionary<string, object> cMeta = ReadMeta(curDest);
                cMeta["nick"] = curNick;
                cMeta["uid"] = curUid;
                cMeta["saved_at"] = FormatUtil.NowUnix();
                cMeta.Remove("needs_login");
                cMeta.Remove("bad_at");
                JsonUtil.WriteAtomic(MetaPath(curDest), cMeta);
                ClearRefreshCooldown(curDest);
                Logger.Log("切号前已归档当前 CLI 账号: " + curNick + " -> " + curDest);
            }

            // 2. 原子切入目标账号
            JsonUtil.CopyAtomic(targetAuth, liveAuth);
            ClearRefreshCooldown(live);
            ClearRefreshCooldown(targetDir);
            string disp = JsonUtil.GetStr(targetMeta, "label");
            if (string.IsNullOrEmpty(disp)) disp = string.IsNullOrEmpty(tNick) ? "目标账号" : tNick;
            Logger.Log("CLI 账号已热切换: " + (string.IsNullOrEmpty(curNick) ? "(无)" : curNick) + " -> " + disp);
            msg = "已切为 CLI 账号: " + disp + " (新开终端生效)";
            return true;
        }

        public static string CreatePendingLoginDir()
        {
            Directory.CreateDirectory(AppPaths.AccountsDir);
            string d = Path.Combine(AppPaths.AccountsDir, "_pending_" + (long)(FormatUtil.NowUnix() * 1000));
            Directory.CreateDirectory(d);
            return d;
        }

        public static bool ImportPendingLogin(string pendingDir, out string nickOut, out string uidOut)
        {
            nickOut = "";
            uidOut = "";
            string src = Path.Combine(pendingDir, "auth.json");
            if (!File.Exists(src)) return false;
            string nick, uid;
            AuthIdentity(pendingDir, out nick, out uid);
            if (string.IsNullOrEmpty(uid)) return false;

            string dest = EnsureSnapshotDir(nick, uid);
            JsonUtil.CopyAtomic(src, Path.Combine(dest, "auth.json"));
            Dictionary<string, object> meta = ReadMeta(dest);
            meta["nick"] = nick;
            meta["uid"] = uid;
            meta["saved_at"] = FormatUtil.NowUnix();
            meta.Remove("needs_login");
            meta.Remove("bad_at");
            JsonUtil.WriteAtomic(MetaPath(dest), meta);
            ClearRefreshCooldown(dest);
            try { Directory.Delete(pendingDir, true); } catch { }
            Logger.Log("独立登录新账号已自动入库: " + nick + " -> " + dest);
            nickOut = nick;
            uidOut = uid;
            return true;
        }

        public static bool RenameAccount(string uid, string label)
        {
            string d = FindSnapshotDir(uid);
            if (d == null)
            {
                string ln, lu;
                AuthIdentity(CodexHome(null), out ln, out lu);
                if (lu == uid) d = EnsureSnapshotDir(ln, uid);
            }
            if (d == null) return false;
            Dictionary<string, object> meta = ReadMeta(d);
            meta["uid"] = uid;
            meta["label"] = (label ?? "").Trim();
            JsonUtil.WriteAtomic(MetaPath(d), meta);
            return true;
        }

        public static bool RemoveAccount(string uid)
        {
            string ln, lu;
            AuthIdentity(CodexHome(null), out ln, out lu);
            if (uid == lu) return false;
            string d = FindSnapshotDir(uid);
            if (d == null) return false;
            try { Directory.Delete(d, true); } catch { }
            ClearRefreshCooldown(d);
            return true;
        }

        private static string HttpDetail(WebException ex)
        {
            try
            {
                if (ex.Response == null) return "";
                using (Stream s = ex.Response.GetResponseStream())
                using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
                {
                    string body = sr.ReadToEnd();
                    Dictionary<string, object> d = JsonUtil.ParseDict(body);
                    Dictionary<string, object> e = JsonUtil.GetDict(d, "error");
                    if (e == null) return "";
                    return (JsonUtil.GetStr(e, "code") ?? "") + "|" + (JsonUtil.GetStr(e, "message") ?? "");
                }
            }
            catch { return ""; }
        }

        private static int HttpStatusCodeOf(WebException ex)
        {
            HttpWebResponse r = ex.Response as HttpWebResponse;
            return r != null ? (int)r.StatusCode : 0;
        }

        private static Dictionary<string, object> HttpJson(string url, Dictionary<string, string> headers, object bodyObj, int timeoutMs = 20000)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.UserAgent = "codex-usage-tray/2.0";
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> kv in headers)
                {
                    if (string.Equals(kv.Key, "User-Agent", StringComparison.OrdinalIgnoreCase))
                        req.UserAgent = kv.Value;
                    else
                        req.Headers[kv.Key] = kv.Value;
                }
            }
            if (bodyObj != null)
            {
                req.Method = "POST";
                req.ContentType = "application/json";
                byte[] data = Encoding.UTF8.GetBytes(JsonUtil.Serialize(bodyObj));
                req.ContentLength = data.Length;
                using (Stream s = req.GetRequestStream())
                    s.Write(data, 0, data.Length);
            }
            else
            {
                req.Method = "GET";
            }

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(s, Encoding.UTF8))
            {
                string txt = sr.ReadToEnd();
                return JsonUtil.ParseDict(txt) ?? new Dictionary<string, object>();
            }
        }

        private static Dictionary<string, string> UsageHeaders(Dictionary<string, object> auth)
        {
            Dictionary<string, object> tokens = JsonUtil.GetDict(auth, "tokens");
            return new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + JsonUtil.GetStr(tokens, "access_token") },
                { "chatgpt-account-id", JsonUtil.GetStr(tokens, "account_id") ?? "" }
            };
        }

        private static void RefreshToken(Dictionary<string, object> auth, string home)
        {
            Dictionary<string, object> tok = JsonUtil.GetDict(auth, "tokens");
            string rt = JsonUtil.GetStr(tok, "refresh_token");
            if (string.IsNullOrEmpty(rt))
                throw new AuthException("凭证里没有 refresh_token, 请重新登录");

            Dictionary<string, object> resp;
            try
            {
                Dictionary<string, object> payload = new Dictionary<string, object>();
                payload["client_id"] = ClientId;
                payload["grant_type"] = "refresh_token";
                payload["refresh_token"] = rt;
                resp = HttpJson(ApiRefresh, null, payload);
            }
            catch (WebException ex)
            {
                int code = HttpStatusCodeOf(ex);
                string detail = HttpDetail(ex);
                Logger.Log(string.Format("续期失败[{0}]: {1} {2}", home ?? "默认", code, detail));
                if (code == 400 || code == 401 || detail.IndexOf("invalidated", StringComparison.OrdinalIgnoreCase) >= 0
                    || detail.IndexOf("invalid_grant", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new AuthException("会话已失效, 请重新登录该账号");
                throw;
            }

            Dictionary<string, object> newTok = new Dictionary<string, object>(tok);
            string at = JsonUtil.GetStr(resp, "access_token");
            string it = JsonUtil.GetStr(resp, "id_token");
            string nrt = JsonUtil.GetStr(resp, "refresh_token");
            if (!string.IsNullOrEmpty(at)) newTok["access_token"] = at;
            if (!string.IsNullOrEmpty(it)) newTok["id_token"] = it;
            if (!string.IsNullOrEmpty(nrt)) newTok["refresh_token"] = nrt;
            auth["tokens"] = newTok;
            auth["last_refresh"] = FormatUtil.NowUnix();

            string path = Path.Combine(CodexHome(home), "auth.json");
            JsonUtil.WriteAtomic(path, auth);
            Logger.Log("token 已续期: " + (home ?? "默认"));
        }

        public static UsageData FetchUsage(string home)
        {
            Dictionary<string, object> auth = ReadAuth(home);
            Dictionary<string, object> tok = JsonUtil.GetDict(auth, "tokens");
            if (auth == null || tok == null || string.IsNullOrEmpty(JsonUtil.GetStr(tok, "access_token")))
                throw new AuthException("没有登录凭证, 请重新登录该账号");

            string key = CodexHome(home);
            long curMt = AuthMtimeTicks(home);
            lock (StateLock)
            {
                long prevMt;
                if (AuthMtimeSeen.TryGetValue(key, out prevMt) && curMt > prevMt)
                    NoRefreshUntil.Remove(key);
                AuthMtimeSeen[key] = curMt;
            }

            Func<Dictionary<string, object>> callApi = delegate
            {
                return HttpJson(ApiUsage, UsageHeaders(auth), null);
            };

            Dictionary<string, object> d;
            try
            {
                d = callApi();
            }
            catch (WebException ex)
            {
                int code = HttpStatusCodeOf(ex);
                if (code != 401 && code != 403) throw;
                string detail = HttpDetail(ex);
                Logger.Log(string.Format("额度接口 {0}[{1}]: {2}", code, home ?? "默认", detail));

                lock (StateLock)
                {
                    double until;
                    if (NoRefreshUntil.TryGetValue(key, out until) && FormatUtil.NowUnix() < until)
                        throw new AuthException("会话已失效, 需重新登录");
                }

                try
                {
                    RefreshToken(auth, home);
                    lock (StateLock) { AuthMtimeSeen[key] = AuthMtimeTicks(home); }
                }
                catch (AuthException)
                {
                    lock (StateLock) { NoRefreshUntil[key] = FormatUtil.NowUnix() + 300; }
                    throw new AuthException("会话已失效, 需重新登录");
                }
                catch (WebException)
                {
                    lock (StateLock) { NoRefreshUntil[key] = FormatUtil.NowUnix() + 300; }
                    throw new AuthException("会话已失效, 需重新登录");
                }

                try
                {
                    d = callApi();
                }
                catch (WebException ex2)
                {
                    int c2 = HttpStatusCodeOf(ex2);
                    if (c2 == 401 || c2 == 403)
                    {
                        lock (StateLock) { NoRefreshUntil[key] = FormatUtil.NowUnix() + 300; }
                        throw new AuthException("会话已失效, 需重新登录");
                    }
                    throw;
                }
            }

            lock (StateLock) { NoRefreshUntil.Remove(key); }

            Dictionary<string, object> rl = JsonUtil.GetDict(d, "rate_limit");
            Dictionary<string, object> pw = JsonUtil.GetDict(rl, "primary_window");
            Dictionary<string, object> sw = JsonUtil.GetDict(rl, "secondary_window");
            Dictionary<string, object> cr = JsonUtil.GetDict(d, "rate_limit_reset_credits");
            Dictionary<string, object> extraCr = JsonUtil.GetDict(d, "credits");

            double? subUntil = null;
            double? subStart = null;
            string nick = "";
            try
            {
                Dictionary<string, object> claims = JwtClaims(auth);
                string nm = JsonUtil.GetStr(claims, "name");
                string em = JsonUtil.GetStr(claims, "email");
                nick = !string.IsNullOrEmpty(nm) ? nm : (!string.IsNullOrEmpty(em) ? em.Split('@')[0] : "");
                Dictionary<string, object> oa = JsonUtil.GetDict(claims, "https://api.openai.com/auth");
                string su = JsonUtil.GetStr(oa, "chatgpt_subscription_active_until");
                string ss = JsonUtil.GetStr(oa, "chatgpt_subscription_active_start");
                DateTime dt;
                if (!string.IsNullOrEmpty(su) && DateTime.TryParse(su, null, System.Globalization.DateTimeStyles.RoundtripKind, out dt))
                    subUntil = (dt.ToUniversalTime() - FormatUtil.UnixEpoch).TotalSeconds;
                if (!string.IsNullOrEmpty(ss) && DateTime.TryParse(ss, null, System.Globalization.DateTimeStyles.RoundtripKind, out dt))
                    subStart = (dt.ToUniversalTime() - FormatUtil.UnixEpoch).TotalSeconds;
            }
            catch (Exception ex)
            {
                Logger.Log("解析 id_token 失败: " + ex.Message);
            }

            int? availableCount = JsonUtil.GetInt(cr, "available_count");
            List<ResetCredit> resetCredits = null;
            if (availableCount != 0)
            {
                try
                {
                    Dictionary<string, object> details = HttpJson(ApiResetCredits, UsageHeaders(auth), null, 5000);
                    resetCredits = ParseResetCredits(details);
                    if (resetCredits != null)
                        availableCount = JsonUtil.GetInt(details, "available_count") ?? availableCount;
                }
                catch (WebException ex)
                {
                    // 明细接口失败时保留此次用量汇总。
                    using (ex.Response)
                        Logger.Log("读取重置卡明细失败: " + HttpStatusCodeOf(ex) + " " + ex.Status);
                }
                catch (IOException)
                {
                    Logger.Log("读取重置卡明细失败: 响应读取中断");
                }
            }
            double now = FormatUtil.NowUnix();
            double? expiresAt = null;
            if (resetCredits != null)
            {
                ResetCredit next = resetCredits.Find(delegate(ResetCredit card) { return card.ExpiresAt > now; });
                if (next != null) expiresAt = next.ExpiresAt;
            }

            return new UsageData
            {
                Home = key,
                Plan = JsonUtil.GetStr(d, "plan_type") ?? "?",
                Nick = nick,
                SubUntil = subUntil,
                SubStart = subStart,
                PUsed = JsonUtil.GetDouble(pw, "used_percent"),
                PResetAt = JsonUtil.GetDouble(pw, "reset_at"),
                PLeft = JsonUtil.GetDouble(pw, "reset_after_seconds"),
                SUsed = JsonUtil.GetDouble(sw, "used_percent"),
                SResetAt = JsonUtil.GetDouble(sw, "reset_at"),
                SLeft = JsonUtil.GetDouble(sw, "reset_after_seconds"),
                Credits = availableCount,
                ApplicableCredits = JsonUtil.GetInt(cr, "applicable_available_count"),
                ResetCredits = resetCredits,
                CreditsExpireAt = expiresAt ?? ParseResetCreditExpireAt(cr),
                CreditsBalance = JsonUtil.GetStr(extraCr, "balance") ?? "0",
                CreditsUnlimited = JsonUtil.GetBool(extraCr, "unlimited", false),
                LimitReached = JsonUtil.GetBool(rl, "limit_reached", false),
                Ts = now
            };
        }

        private static List<ResetCredit> ParseResetCredits(Dictionary<string, object> response)
        {
            object list;
            if (response == null || !response.TryGetValue("credits", out list)) return null;
            System.Collections.IList rows = list as System.Collections.IList;
            if (rows == null) return null;

            List<ResetCredit> cards = new List<ResetCredit>();
            foreach (object item in rows)
            {
                Dictionary<string, object> row = item as Dictionary<string, object>;
                if (!string.Equals(JsonUtil.GetStr(row, "status"), "available", StringComparison.OrdinalIgnoreCase))
                    continue;

                object rawDate;
                double? expiresAt = row.TryGetValue("expires_at", out rawDate) ? ParseTimestampObj(rawDate) : null;
                cards.Add(new ResetCredit
                {
                    Title = JsonUtil.GetStr(row, "title"),
                    ExpiresAt = expiresAt > 0 && expiresAt < 253402300799d ? expiresAt : null
                });
            }
            cards.Sort(delegate(ResetCredit a, ResetCredit b)
            {
                return (a.ExpiresAt ?? double.MaxValue).CompareTo(b.ExpiresAt ?? double.MaxValue);
            });
            return cards;
        }

        private static double? ParseTimestampObj(object val)
        {
            if (val == null) return null;
            if (val is int || val is long || val is double || val is decimal || val is float)
            {
                double num = Convert.ToDouble(val);
                if (num > 1e12) num /= 1000.0; // 毫秒转秒
                return num > 0 ? (double?)num : null;
            }
            string s = Convert.ToString(val);
            if (string.IsNullOrEmpty(s)) return null;
            double d;
            if (double.TryParse(s, out d))
            {
                if (d > 1e12) d /= 1000.0;
                return d > 0 ? (double?)d : null;
            }
            DateTime dt;
            if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out dt))
                return (dt.ToUniversalTime() - FormatUtil.UnixEpoch).TotalSeconds;
            return null;
        }

        private static double? ParseResetCreditExpireAt(Dictionary<string, object> cr)
        {
            if (cr == null) return null;
            string[] keys = new string[] { "next_expiring_at", "earliest_expires_at", "expires_at", "expiration_timestamp", "expiry" };
            for (int i = 0; i < keys.Length; i++)
            {
                if (cr.ContainsKey(keys[i]))
                {
                    double? ts = ParseTimestampObj(cr[keys[i]]);
                    if (ts > 0 && ts < 253402300799d) return ts;
                }
            }
            return null;
        }

        public static bool AutostartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AutostartReg, false))
                {
                    return k != null && k.GetValue(AutostartName) != null;
                }
            }
            catch { return false; }
        }

        public static void SetAutostart(bool enable)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(AutostartReg, true))
                {
                    if (k == null) return;
                    if (enable)
                    {
                        string exe = System.Windows.Forms.Application.ExecutablePath;
                        k.SetValue(AutostartName, "\"" + exe + "\"", RegistryValueKind.String);
                    }
                    else
                    {
                        k.DeleteValue(AutostartName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("设置开机自启失败: " + ex.Message);
            }
        }

        private static string FindCodexAppUserModelId()
        {
            // 先从当前用户注册表查找已安装的 OpenAI.Codex 包 FamilyName (兼容未来版本)
            try
            {
                string regPath = @"Software\Classes\ActivatableClasses\Package";
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(regPath, false))
                {
                    if (rk != null)
                    {
                        foreach (string sub in rk.GetSubKeyNames())
                        {
                            if (sub.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase))
                            {
                                // PackageFullName 形如 OpenAI.Codex_26.1117.3838.0_x64__2p2nqsd0c76g0
                                int idx = sub.LastIndexOf("__", StringComparison.Ordinal);
                                if (idx > 0)
                                    return "OpenAI.Codex_" + sub.Substring(idx + 2) + "!App";
                            }
                        }
                    }
                }
            }
            catch { }

            // 回退检查 WindowsApps 目录或默认官方包名
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string wa = Path.Combine(pf, "WindowsApps");
                if (Directory.Exists(wa))
                {
                    foreach (string dir in Directory.GetDirectories(wa, "OpenAI.Codex_*"))
                    {
                        string name = Path.GetFileName(dir);
                        int idx = name.LastIndexOf("__", StringComparison.Ordinal);
                        if (idx > 0)
                            return "OpenAI.Codex_" + name.Substring(idx + 2) + "!App";
                    }
                }
            }
            catch { }

            return "OpenAI.Codex_2p2nqsd0c76g0!App";
        }

        public static bool RestartCodexApp(out string msg)
        {
            try
            {
                int myPid = Process.GetCurrentProcess().Id;
                List<Process> targets = new List<Process>();
                bool hasDesktopApp = false;

                foreach (Process p in Process.GetProcesses())
                {
                    if (p.Id == myPid) continue;
                    try
                    {
                        string name = p.ProcessName ?? "";
                        string exePath = "";
                        try { exePath = p.MainModule != null ? (p.MainModule.FileName ?? "") : ""; }
                        catch { }

                        bool isDesktopProc = exePath.IndexOf("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) >= 0;
                        bool isCodexBin = string.Equals(name, "codex", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "codex-windows-sandbox-service", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "codex-computer-use-swift", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(name, "codex-code-mode-host", StringComparison.OrdinalIgnoreCase);

                        if (isDesktopProc) hasDesktopApp = true;
                        if (isDesktopProc || isCodexBin)
                            targets.Add(p);
                    }
                    catch { }
                }

                // 1. 先尝试优雅关闭带主窗口的进程
                foreach (Process p in targets)
                {
                    try { if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); }
                    catch { }
                }
                if (targets.Count > 0) Thread.Sleep(500);

                // 2. 确保剩余进程全部结束
                foreach (Process p in targets)
                {
                    try { if (!p.HasExited) p.Kill(); }
                    catch { }
                }
                if (targets.Count > 0) Thread.Sleep(700);

                // 3. 重新启动 Codex 桌面端 (通过 shell:AppsFolder)
                string aumid = FindCodexAppUserModelId();
                if (hasDesktopApp || !string.IsNullOrEmpty(aumid))
                {
                    Process.Start("explorer.exe", "shell:AppsFolder\\" + aumid);
                    Logger.Log("已重启 Codex 桌面端: " + aumid + " (结束进程 " + targets.Count + " 个)");
                    msg = "已重启 Codex";
                    return true;
                }

                // 兜底: 若未安装桌面端则打开 CLI
                Process.Start(new ProcessStartInfo("cmd.exe", "/k codex") { UseShellExecute = false });
                Logger.Log("已重新启动 Codex CLI");
                msg = "已启动 Codex CLI";
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("重启 Codex 失败: " + ex.Message);
                msg = "重启失败: " + ex.Message;
                return false;
            }
        }

        public static void MigrateFromPythonIfNeeded(string pythonAccountsDir)
        {
            try
            {
                if (string.IsNullOrEmpty(pythonAccountsDir) || !Directory.Exists(pythonAccountsDir)) return;
                if (string.Equals(Path.GetFullPath(pythonAccountsDir), Path.GetFullPath(AppPaths.AccountsDir), StringComparison.OrdinalIgnoreCase))
                    return;
                if (Directory.Exists(AppPaths.AccountsDir) && Directory.GetDirectories(AppPaths.AccountsDir).Length > 0)
                    return;
                Directory.CreateDirectory(AppPaths.AccountsDir);
                foreach (string srcDir in Directory.GetDirectories(pythonAccountsDir))
                {
                    string name = Path.GetFileName(srcDir);
                    if (IsPendingDir(name)) continue;
                    string srcAuth = Path.Combine(srcDir, "auth.json");
                    if (!File.Exists(srcAuth)) continue;
                    string dstDir = Path.Combine(AppPaths.AccountsDir, name);
                    Directory.CreateDirectory(dstDir);
                    JsonUtil.CopyAtomic(srcAuth, Path.Combine(dstDir, "auth.json"));
                    string srcMeta = Path.Combine(srcDir, "meta.json");
                    if (File.Exists(srcMeta))
                        JsonUtil.CopyAtomic(srcMeta, Path.Combine(dstDir, "meta.json"));
                }
                Logger.Log("已从 Python 版导入账号快照: " + pythonAccountsDir);
            }
            catch (Exception ex)
            {
                Logger.Log("导入快照失败: " + ex.Message);
            }
        }
    }
}
