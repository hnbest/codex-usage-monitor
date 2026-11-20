"""Windows 桌面集成。"""
import ctypes
import subprocess
from ctypes import wintypes


def _user32():
    return ctypes.windll.user32


def animate_windows():
    enabled = wintypes.BOOL()
    return bool(_user32().SystemParametersInfoW(0x1042, 0, ctypes.byref(enabled), 0) and enabled.value)


def work_area():
    rect = wintypes.RECT()
    if _user32().SystemParametersInfoW(0x0030, 0, ctypes.byref(rect), 0):
        return rect.left, rect.top, rect.right, rect.bottom
    return 0, 0, _user32().GetSystemMetrics(0), _user32().GetSystemMetrics(1)


def taskbar_rects():
    user32 = _user32()
    find_window = user32.FindWindowW
    find_window.argtypes = (wintypes.LPCWSTR, wintypes.LPCWSTR)
    find_window.restype = wintypes.HWND
    find_child = user32.FindWindowExW
    find_child.argtypes = (wintypes.HWND, wintypes.HWND, wintypes.LPCWSTR, wintypes.LPCWSTR)
    find_child.restype = wintypes.HWND
    get_rect = user32.GetWindowRect
    get_rect.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.RECT))
    get_rect.restype = wintypes.BOOL
    tray = find_window("Shell_TrayWnd", None)
    bar, icons = wintypes.RECT(), wintypes.RECT()
    if not tray or not get_rect(tray, ctypes.byref(bar)):
        return None, None
    child = find_child(tray, None, "TrayNotifyWnd", None)
    if not child or not get_rect(child, ctypes.byref(icons)):
        icons = None
    return bar, icons


def fullscreen_active():
    state = ctypes.c_int()
    return ctypes.windll.shell32.SHQueryUserNotificationState(ctypes.byref(state)) == 0 and state.value in (2, 3)


def keep_topmost(child_window):
    user32 = _user32()
    get_ancestor = user32.GetAncestor
    get_ancestor.argtypes = (wintypes.HWND, wintypes.UINT)
    get_ancestor.restype = wintypes.HWND
    set_window_pos = user32.SetWindowPos
    set_window_pos.argtypes = (wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int,
                               ctypes.c_int, ctypes.c_int, wintypes.UINT)
    window = get_ancestor(child_window, 2)
    if window:
        set_window_pos(window, ctypes.c_void_p(-1), 0, 0, 0, 0, 0x0013)


def restart_codex():
    """只重启已安装的 Codex 桌面应用，不结束独立 CLI 会话。"""
    script = r"""
$ErrorActionPreference = 'Stop'
$app = Get-StartApps | Where-Object AppID -like 'OpenAI.Codex_*!App' | Select-Object -First 1
if (-not $app) { exit 3 }
$targets = Get-Process | Where-Object { $_.Path -like '*\WindowsApps\OpenAI.Codex_*\*' }
foreach ($target in $targets) {
    if ($target.MainWindowHandle -ne 0) { $target.CloseMainWindow() | Out-Null }
}
if ($targets) { Start-Sleep -Milliseconds 750 }
foreach ($target in $targets) {
    if (-not $target.HasExited) {
        Stop-Process -InputObject $target -ErrorAction Stop
        $target.WaitForExit(2000) | Out-Null
    }
}
Start-Process explorer.exe -ArgumentList ('shell:AppsFolder\' + $app.AppID)
"""
    try:
        result = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
            creationflags=subprocess.CREATE_NO_WINDOW, timeout=30, capture_output=True)
    except (OSError, subprocess.TimeoutExpired):
        return False, "启动 Codex 桌面应用超时或失败"
    if result.returncode == 3:
        return False, "未找到 Codex 桌面应用"
    if result.returncode:
        return False, "无法重启 Codex 桌面应用"
    return True, "已请求重启 Codex 桌面应用"
