# Codex Usage Monitor

Windows 系统托盘 + 置顶透明浮动卡片工具，实时监控 OpenAI Codex 的 **5 小时窗口** 与 **每周窗口** 剩余额度、重置倒计时与订阅到期时间。

## 主要功能

- **实时双环形仪表**：4x 超采样抗锯齿绘制 5 小时 / 每周剩余百分比，按余量自动切换绿 / 黄 / 红状态色。
- **系统托盘动态图标**：托盘图标实时显示 5 小时剩余百分比，底部进度条展示每周余量，悬停可查看完整重置时间。
- **多账号总览与后台全量轮询**：后台自动轮询所有健康账号；支持点击标题栏 `总览` / `详情` 按钮一键切换「单账号双环详情」与「多账号总览列表」，账号切换弹窗与系统托盘悬停均实时展示所有账号余量。
- **独立隔离登录 & 一键热切换 CLI 账号**：
  - 点击「添加新账号（独立隔离）」会在临时 `CODEX_HOME` 中运行 `codex login`，不影响当前 `~/.codex` 登录，完成后自动归档入库。
  - 在多账号总览、设置面板或右键菜单中点击 `切为CLI`，会自动把当前 `~/.codex/auth.json` 归档保存再原子切入目标账号——**无需执行 `codex logout`，不会吊销原账号授权，也免去反复浏览器登录**。
- **凭证自动续期与秒级恢复**：Access Token 过期时自动使用 `refresh_token` 原子写回续期；若会话失效，主界面底栏一键拉起重新登录，登录完成后自动检测 `auth.json` 变更并立即恢复。
- **低额度系统通知**：支持自定义阈值（`≤10%` / `≤20%` / `≤30%` / 关闭），跌破阈值时在当前重置周期内发送一次 Windows 通知提醒。
- **丰富交互与持久化设置**：
  - 拖动窗口任意空白/环形区即可移动位置，自动记忆并防越界。
  - 右键主窗口可快速刷新、切换账号、打开设置、复位窗口位置、隐藏到托盘或退出。
  - 点击底栏右侧频率标签（如 `每 60s`）可立即手动刷新。
  - 支持透明度（`40%` ~ `100%`）、采集间隔（`15s` ~ `3600s`）与 Windows 开机自启。

## 环境要求

- Windows 10 / 11（Win11 自动启用 DWM 原生圆角）
- Python 3.9+（需带 `tkinter`）
- 已安装并登录过 Codex CLI（凭证位于 `~/.codex/auth.json`）

## 安装与启动

本项目提供 **C# 原生单文件版** 和 **Python 版**。两版支持额度监控、多账号切换、重置卡详情和到期提醒、三档窗口字号、任务栏常驻小窗及版本号显示，共用同一套多账号切换协议与互斥锁。

### 方式一：C# 原生单文件 `.exe`（推荐，可直接放桌面）
1. 一键编译（使用 Windows 自带 `.NET Framework 4.x csc.exe`，无需额外安装 SDK）：
   ```powershell
   powershell -ExecutionPolicy Bypass -File csharp\build.ps1 -CopyToDesktop
   ```
2. 编译产物位于 `csharp\dist\CodexUsageMonitor-<版本号>.exe`，例如 `CodexUsageMonitor-0.0.1.exe`。`-CopyToDesktop` 会将同名文件复制到桌面。
3. 版本从 `0.0.1` 开始。C# 源码、图标或打包脚本有变化时，下次成功打包会将末位加 1，同一套文件重复打包沿用原版本。设置底部、任务栏小窗及托盘图标的右键菜单均显示当前运行版本。
4. 单独放桌面运行时，配置、多账号快照和日志自动存放在 `%LOCALAPPDATA%\CodexUsageMonitor\`，不会弄乱桌面。

### 方式二：Python 版

源码和启动器位于 `python\`，与 `csharp\` 平级。以下命令从项目根目录运行。

1. 安装 Python 依赖：
   ```bash
   python -m pip install -r python\requirements.txt
   ```
2. 启动方式：
   - **双击无窗口启动**：直接双击 [`python\启动监控.vbs`](python/启动监控.vbs)
   - **命令行启动**：
     ```bash
     pythonw python\codex_tray.py
     ```
   - 调试模式下可在项目根目录创建空文件 `debug.on`，崩溃堆栈将写入 `tray_err.txt`。
3. 打包为 Windows 单文件 EXE：
   ```powershell
   python -m pip install pyinstaller
   python python\build_python.py
   ```
   产物位于 `python\dist\CodexUsageMonitor-Python-<版本号>.exe`。Python 版从 `0.0.1` 独立计版，源码或打包输入变化后，下次成功打包自动将末位加 1。直接运行脚本时显示最近一次打包的版本。
4. 在项目内运行 Python 脚本时，继续读取项目根目录的配置和账号快照。单独运行打包版时，默认使用 `%LOCALAPPDATA%\CodexUsageMonitor\`。

## 多账号使用流程

1. **添加新账号**：打开设置（或右键主窗口）→ 点击 `添加新账号(独立隔离)`，在弹出的终端完成 `codex login`，监控程序会在 2 秒内自动归档入库，**不覆盖当前 CLI 登录**。
2. **同时观察多账号**：点击主窗口右上角 `总览` 按钮（或右键/托盘菜单），即可同屏查看所有账号的 5 小时 / 每周余量进度条；点击任意账号昵称可切回双环详情。
3. **一键切换 CLI 账号**：在总览卡片、设置面板或右键菜单点击 `切为CLI`，即可无损热切换当前 Codex CLI 登录账号。**切号时请勿执行 `codex logout`**，否则 OpenAI 会主动吊销原账号会话。

## 配置文件说明

程序首次运行会在同目录生成 `config.json`（已列入 `.gitignore`）：

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `alpha` | `0.6` | 浮动窗口透明度（`0.4` ~ `1.0`） |
| `interval` | `60` | 自动刷新间隔秒数（`15` ~ `3600`） |
| `window_x` / `window_y` | `null` | 窗口左上角坐标，`null` 表示自动定位到右下角 |
| `display_uid` | `""` | 单账号详情模式下当前选中的账号 UID |
| `view_mode` | `"single"` | 主窗口视图模式：`single`（双环详情）或 `all`（多账号总览） |
| `notify_low` | `true` | 是否开启低额度系统通知 |
| `notify_threshold` | `20` | 低额度提醒阈值百分比 |

## 安全提示

- `accounts/` 目录和 `~/.codex/auth.json` 包含 OAuth Token，请勿提交到任何代码仓库或分享给他人。
- 日志文件 `codex_tray.log` 超过 512KB 会自动轮转至 `codex_tray.log.1`，不会无限增长。
