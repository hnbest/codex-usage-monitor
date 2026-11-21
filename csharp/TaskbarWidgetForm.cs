using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace CodexUsageMonitor
{
    /// <summary>
    /// 显示在 Explorer 主任务栏空位中的实时额度条。
    /// </summary>
    public class TaskbarWidgetForm : Control
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int Width, Height; }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitsPerPixel;
            public uint Compression, ImageSize;
            public int XResolution, YResolution;
            public uint ColorsUsed, ImportantColors;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Blend
        {
            public byte Operation, Flags, Opacity, AlphaFormat;
        }

        private struct Placement
        {
            public IntPtr Taskbar;
            public Rectangle Bar;
            public Rectangle Tray;
            public Rectangle Widget;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ToolInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Window;
            public IntPtr Id;
            public RECT Bounds;
            public IntPtr Instance;
            public string Text;
            public IntPtr Data;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CommonControls
        {
            public int Size, Classes;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string cls, string title);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT point);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pquns);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDc, IntPtr destination,
            ref SIZE size, IntPtr sourceDc, ref POINT source, uint colorKey, ref Blend blend, uint flags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader header, uint colors,
            out IntPtr pixels, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("comctl32.dll")]
        private static extern bool InitCommonControlsEx(ref CommonControls controls);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, ref ToolInfo info);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        private const uint SWP_NOACTIVATE = 0x0010;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint ULW_ALPHA = 2;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYOUTRTL = 0x00400000;
        private const int WidgetWidth = 156;
        private const int PaddingBetweenItems = 4;
        private const int TTM_ADDTOOLW = 0x0432;
        private const int TTM_UPDATETIPTEXTW = 0x0439;
        private const int TTM_SETMAXTIPWIDTH = 0x0418;

        private readonly MainForm _owner;
        private readonly System.Windows.Forms.Timer _keepTimer;
        private readonly System.Windows.Forms.Timer _cycleTimer;
        private readonly NativeWindow _tip = new NativeWindow();
        private string _tipText = "Codex / AGY 用量 - 加载中…";

        private TaskbarDisplayMode _mode = TaskbarDisplayMode.Dual;
        private bool _showWeekly;
        private UsageData _data;
        private string _err;
        private string _errKind;
        private string _acctName = "";
        private string _cdxName;
        private UsageData _cdxData;
        private string _cdxErr;
        private string _cdxErrKind;
        private string _agyName;
        private UsageData _agyData;
        private string _agyErr;
        private string _agyErrKind;
        private bool _hover;
        private IntPtr _taskbar;
        private Rectangle _lastBar;
        private Rectangle _lastWidget;
        private Task<Placement> _measurement;
        private int _measurementStarted;
        private bool _widgetNeedsPaint = true;

        private readonly Font _fLbl = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Regular);
        private readonly Font _fVal = new Font("Microsoft YaHei UI", 8.0f, FontStyle.Bold);
        private readonly Font _fMsg = new Font("Microsoft YaHei UI", 8.0f, FontStyle.Regular);

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Parent = _taskbar;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public TaskbarWidgetForm(MainForm owner)
        {
            _owner = owner;

            Visible = false;
            TabStop = false;
            SetStyle(ControlStyles.Selectable, false);
            Size = new Size(WidgetWidth, 38);
            Cursor = Cursors.Hand;

            MouseEnter += delegate { _hover = true; _widgetNeedsPaint = true; };
            MouseLeave += delegate { _hover = false; _widgetNeedsPaint = true; };

            MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                    _owner.ToggleWindowFromOutside();
                else if (e.Button == MouseButtons.Right)
                    _owner.ShowContextMenuAt(Control.MousePosition);
            };

            _keepTimer = new System.Windows.Forms.Timer();
            _keepTimer.Interval = 400;
            _keepTimer.Tick += delegate { KeepOnTaskbar(); };

            // 双平台同屏时每 4 秒自动在 5h 与 周 之间切换显示（鼠标悬停时暂停）
            _cycleTimer = new System.Windows.Forms.Timer();
            _cycleTimer.Interval = 4000;
            _cycleTimer.Tick += delegate
            {
                if (_hover || !Visible) return;
                if (_mode == TaskbarDisplayMode.Dual && _agyName != null)
                {
                    _showWeekly = !_showWeekly;
                    _widgetNeedsPaint = true;
                }
            };
        }

        public void UpdateData(
            TaskbarDisplayMode mode,
            string acctName, UsageData data, string err, string errKind,
            string cdxName, UsageData cdxData, string cdxErr, string cdxErrKind,
            string agyName, UsageData agyData, string agyErr, string agyErrKind)
        {
            _mode = mode;
            _acctName = acctName ?? "";
            _data = data;
            _err = err;
            _errKind = errKind;
            _cdxName = cdxName;
            _cdxData = cdxData;
            _cdxErr = cdxErr;
            _cdxErrKind = cdxErrKind;
            _agyName = agyName;
            _agyData = agyData;
            _agyErr = agyErr;
            _agyErrKind = agyErrKind;

            _tipText = BuildTooltip();
            if (_tip.Handle != IntPtr.Zero)
                SetToolTipText(TTM_UPDATETIPTEXTW);
            _widgetNeedsPaint = true;
        }

        private string BuildTooltip()
        {
            if (_mode == TaskbarDisplayMode.Dual && _agyName != null)
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                if (_cdxData != null)
                {
                    sb.AppendFormat("[Codex] {0} ({1})\n  5h: 剩 {2}% · {3} 重置\n  周: 剩 {4}% · {5} 重置",
                        _cdxName ?? "",
                        (_cdxData.Plan ?? "").ToUpperInvariant(),
                        _cdxData.PRemaining.HasValue ? ((int)Math.Round(_cdxData.PRemaining.Value)).ToString() : "-",
                        FormatUtil.FmtResetShort(_cdxData.PResetAt),
                        _cdxData.SRemaining.HasValue ? ((int)Math.Round(_cdxData.SRemaining.Value)).ToString() : "-",
                        FormatUtil.FmtResetShort(_cdxData.SResetAt));
                }
                else if (_cdxName != null)
                {
                    sb.AppendFormat("[Codex] {0}: {1}", _cdxName, _cdxErrKind == "auth" ? "需重新登录" : (string.IsNullOrEmpty(_cdxErr) ? "加载中…" : _cdxErr));
                }
                else
                {
                    sb.Append("[Codex] 未登录");
                }

                sb.Append("\n");
                if (_agyData != null)
                {
                    sb.AppendFormat("[AGY] {0} ({1})", _agyName, (_agyData.Plan ?? "AGY").ToUpperInvariant());
                    if (_agyData.Groups != null && _agyData.Groups.Count > 0)
                    {
                        foreach (QuotaGroup g in _agyData.Groups)
                        {
                            sb.AppendFormat("\n  {0}:", g.DisplayName);
                            foreach (QuotaBucket b in g.Buckets)
                            {
                                sb.AppendFormat(" {0} {1}%", b.ShortLabel ?? "", b.RemainingPct.HasValue ? ((int)Math.Round(b.RemainingPct.Value)).ToString() : "-");
                            }
                        }
                    }
                    else if (_agyData.Buckets != null && _agyData.Buckets.Count > 0)
                    {
                        foreach (QuotaBucket b in _agyData.Buckets)
                        {
                            sb.AppendFormat("\n  {0}: 剩 {1}%{2}",
                                b.Label,
                                b.RemainingPct.HasValue ? ((int)Math.Round(b.RemainingPct.Value)).ToString() : "-",
                                b.ResetAt.HasValue ? (" · " + FormatUtil.FmtResetShort(b.ResetAt) + " 重置") : "");
                        }
                    }
                    else
                    {
                        sb.Append("\n  暂无模型额度数据");
                    }
                }
                else
                {
                    string st = _agyErrKind == "offline" ? "未运行" : (_agyErrKind == "auth" ? "需重新登录" : (string.IsNullOrEmpty(_agyErr) ? "加载中…" : _agyErr));
                    sb.AppendFormat("[AGY] {0}: {1}", _agyName, st);
                }
                sb.Append("\n左键展开详情 · 右键菜单");
                return sb.ToString();
            }

            if (_data != null)
            {
                if (_data.Provider == ProviderKind.Antigravity)
                {
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    sb.AppendFormat("[AGY] {0} [{1}]", _acctName, (_data.Plan ?? "AGY").ToUpperInvariant());
                    if (_data.Buckets != null)
                    {
                        foreach (QuotaBucket b in _data.Buckets)
                        {
                            sb.AppendFormat("\n{0}: 剩 {1}%{2}",
                                b.Label,
                                b.RemainingPct.HasValue ? ((int)Math.Round(b.RemainingPct.Value)).ToString() : "-",
                                b.ResetAt.HasValue ? (" · " + FormatUtil.FmtResetShort(b.ResetAt) + " 重置") : "");
                        }
                    }
                    sb.Append("\n左键展开详情 · 右键菜单");
                    return sb.ToString();
                }
                return string.Format(
                    "{0} [{1}]\n5小时: 剩 {2}% · {3} 重置\n每周: 剩 {4}% · {5} 重置\n左键展开详情 · 右键菜单",
                    _acctName,
                    (_data.Plan ?? "").ToUpperInvariant(),
                    _data.PRemaining.HasValue ? ((int)Math.Round(_data.PRemaining.Value)).ToString() : "-",
                    FormatUtil.FmtResetShort(_data.PResetAt),
                    _data.SRemaining.HasValue ? ((int)Math.Round(_data.SRemaining.Value)).ToString() : "-",
                    FormatUtil.FmtResetShort(_data.SResetAt));
            }
            if (_errKind == "auth")
                return _acctName + "\n会话已失效，点击打开窗口重新登录";
            if (_errKind == "offline")
                return _acctName + "\n未检测到运行中的 Antigravity";
            return string.IsNullOrEmpty(_err) ? "Codex / AGY 用量 - 加载中…" : (_acctName + "\n" + _err);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _widgetNeedsPaint = true;
            // Explorer 是原生父窗口，WinForms ToolTip 无法找到托管的顶层容器。
            var controls = new CommonControls { Size = Marshal.SizeOf(typeof(CommonControls)), Classes = 0x0004 };
            if (!InitCommonControlsEx(ref controls)) return;

            try
            {
                _tip.CreateHandle(new CreateParams
                {
                    ClassName = "tooltips_class32",
                    Parent = Handle,
                    Style = unchecked((int)0x80000003), // WS_POPUP | TTS_ALWAYSTIP | TTS_NOPREFIX
                    ExStyle = 0x00000008 // WS_EX_TOPMOST
                });
                SendMessage(_tip.Handle, TTM_SETMAXTIPWIDTH, IntPtr.Zero, new IntPtr(560));
                SetToolTipText(TTM_ADDTOOLW);
            }
            catch (Win32Exception) { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_tip.Handle != IntPtr.Zero)
                _tip.DestroyHandle();
            base.OnHandleDestroyed(e);
        }

        private void SetToolTipText(int message)
        {
            var info = new ToolInfo
            {
                Size = Marshal.SizeOf(typeof(ToolInfo)), // TTTOOLINFO_V2_SIZE 兼容两个常用的 comctl32 版本
                Flags = 0x0011, // TTF_IDISHWND | TTF_SUBCLASS
                Window = Handle,
                Id = Handle,
                Text = _tipText
            };
            SendMessage(_tip.Handle, message, IntPtr.Zero, ref info);
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                _keepTimer.Start();
                _cycleTimer.Start();
                KeepOnTaskbar();
            }
            else
            {
                _keepTimer.Stop();
                _cycleTimer.Stop();
                Hide();
            }
        }

        private static Rectangle GetVisibleBounds(IntPtr window)
        {
            RECT bounds;
            if (window == IntPtr.Zero || !IsWindowVisible(window) || !GetWindowRect(window, out bounds))
                return Rectangle.Empty;
            return Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        }

        private static Placement ReadShellBounds()
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            Rectangle bar = GetVisibleBounds(taskbar);
            if (bar.Width <= bar.Height || bar.Height < 28 ||
                (GetWindowLong(taskbar, GWL_EXSTYLE) & WS_EX_LAYOUTRTL) != 0)
                return default(Placement);

            Rectangle tray = GetVisibleBounds(FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null));
            if (tray.Width <= 0 || tray.Left <= bar.Left || !bar.IntersectsWith(tray))
                return default(Placement);

            return new Placement { Taskbar = taskbar, Bar = bar, Tray = tray };
        }

        private static Placement MeasureTaskbar(Placement placement)
        {
            try
            {
                var occupied = new List<Rectangle>();
                uint shellProcess;
                if (GetWindowThreadProcessId(placement.Taskbar, out shellProcess) == 0)
                    return placement;
                uint ourProcess = GetCurrentProcessId();
                bool hasTaskButtons = false;
                Rectangle legacyTaskList = Rectangle.Empty;
                int trayLeft = placement.Tray.Left;

                // 批量读取 UI Automation 属性，避免逐个查询跨进程控件。
                var cache = new CacheRequest { TreeFilter = Automation.RawViewCondition };
                cache.Add(AutomationElement.BoundingRectangleProperty);
                cache.Add(AutomationElement.IsOffscreenProperty);
                cache.Add(AutomationElement.ClassNameProperty);
                cache.Add(AutomationElement.AutomationIdProperty);
                cache.Add(AutomationElement.ProcessIdProperty);
                cache.Add(AutomationElement.ControlTypeProperty);

                AutomationElementCollection elements;
                using (cache.Activate())
                {
                    elements = AutomationElement.FromHandle(placement.Taskbar)
                        .FindAll(TreeScope.Descendants, Condition.TrueCondition);
                }
                foreach (AutomationElement element in elements)
                {
                    var info = element.Cached;
                    var bounds = info.BoundingRectangle;
                    if (info.IsOffscreen || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
                        info.ProcessId == ourProcess)
                        continue;
                    Rectangle rect = Rectangle.FromLTRB((int)Math.Floor(bounds.Left), (int)Math.Floor(bounds.Top),
                        (int)Math.Ceiling(bounds.Right), (int)Math.Ceiling(bounds.Bottom));
                    if (!placement.Bar.IntersectsWith(rect))
                        continue;

                    string className = info.ClassName;
                    if (className.StartsWith("SystemTray.", StringComparison.Ordinal))
                        trayLeft = Math.Min(trayLeft, rect.Left);
                    if (className == "MSTaskListWClass")
                        legacyTaskList = rect;
                    bool taskButton = className == "Taskbar.TaskListButtonAutomationPeer";
                    hasTaskButtons |= taskButton;

                    if (info.AutomationId == "WidgetsButton" || className == "ShellFeedsTaskbarViewWindow" ||
                        className == "DynamicContent2" || (info.ProcessId != 0 && info.ProcessId != shellProcess) ||
                        taskButton || info.ControlType == ControlType.Button ||
                        info.ControlType == ControlType.SplitButton || info.ControlType == ControlType.CheckBox ||
                        info.ControlType == ControlType.Edit || info.ControlType == ControlType.MenuItem ||
                        info.ControlType == ControlType.TabItem || info.ControlType == ControlType.ListItem)
                        occupied.Add(rect);
                }

                // 内部控件树不可读时交给原生托盘图标。
                if (hasTaskButtons || (!legacyTaskList.IsEmpty && occupied.Exists(legacyTaskList.Contains)))
                    placement.Widget = FindFreeSlot(placement.Bar, trayLeft, occupied);
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            return placement;
        }

        private static Rectangle FindFreeSlot(Rectangle bar, int trayLeft, List<Rectangle> occupied)
        {
            int height = Math.Min(38, Math.Max(28, bar.Height - 8));
            int top = bar.Top + (bar.Height - height) / 2;
            int right = trayLeft - PaddingBetweenItems;
            // 从通知区往左找最近的可用空位，逐个避开已有控件。
            occupied.Sort((a, b) => b.Right.CompareTo(a.Right));
            foreach (Rectangle item in occupied)
            {
                if (item.Left < right + PaddingBetweenItems &&
                    item.Right > right - WidgetWidth - PaddingBetweenItems)
                    right = item.Left - PaddingBetweenItems;
            }
            if (right - WidgetWidth < bar.Left + PaddingBetweenItems)
                return Rectangle.Empty;

            return new Rectangle(right - WidgetWidth, top, WidgetWidth, height);
        }


        private bool IsForegroundFullscreen()
        {
            try
            {
                // 使用 Windows 官方 API 判断是否处于真正全屏(D3D全屏游戏 / 全屏视频演示)
                // QUNS_BUSY = 2 (全屏非D3D应用如全屏视频), QUNS_RUNNING_D3D_FULL_SCREEN = 3 (全屏游戏)
                int state;
                if (SHQueryUserNotificationState(out state) == 0)
                {
                    if (state != 2 && state != 3) return false;
                }

                // 二次校验: 排除桌面(Progman/WorkerW)与任务栏自身, 防止点击桌面或任务栏时误隐藏
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero || (IsHandleCreated && fg == Handle) || (_owner.IsHandleCreated && fg == _owner.Handle)) return false;
                System.Text.StringBuilder cls = new System.Text.StringBuilder(64);
                GetClassName(fg, cls, cls.Capacity);
                string clsName = cls.ToString();
                if (clsName == "Progman" || clsName == "WorkerW"
                    || clsName == "Shell_TrayWnd" || clsName == "Shell_SecondaryTrayWnd"
                    || clsName == "NotifyIconOverflowWindow" || clsName == "Windows.UI.Core.CoreWindow")
                    return false;

                RECT r;
                if (!GetWindowRect(fg, out r)) return false;
                Rectangle scr = Screen.PrimaryScreen.Bounds;
                return r.Left <= scr.Left && r.Top <= scr.Top && r.Right >= scr.Right && r.Bottom >= scr.Bottom;
            }
            catch { return false; }
        }

        private void KeepOnTaskbar()
        {
            Placement current = ReadShellBounds();
            if (current.Taskbar == IntPtr.Zero || IsForegroundFullscreen())
            {
                Hide();
                return;
            }

            // 托盘图标的显隐会改变通知区宽度。等新位置测量完成前，保留仍未被覆盖的长条。
            if (_taskbar != current.Taskbar || _lastBar != current.Bar ||
                _lastWidget.Right > current.Tray.Left || !IsHandleCreated)
                Hide();

            if (_measurement != null)
            {
                bool expired = unchecked(Environment.TickCount - _measurementStarted) > 2000;
                if (expired) Hide();
                if (!_measurement.IsCompleted) return;

                Placement measured = _measurement.Status == TaskStatus.RanToCompletion
                    ? _measurement.Result : default(Placement);
                if (!expired && measured.Taskbar == current.Taskbar && measured.Bar == current.Bar &&
                    measured.Tray == current.Tray)
                {
                    if (measured.Widget.IsEmpty || !Screen.FromHandle(current.Taskbar).Bounds.Contains(measured.Widget))
                        Hide();
                    else
                    {
                        PlaceWidget(measured);
                        _lastBar = measured.Bar;
                        _lastWidget = measured.Widget;
                    }
                }
                else if (measured.Taskbar == IntPtr.Zero)
                    Hide();
            }

            _measurementStarted = Environment.TickCount;
            _measurement = Task.Run(() => MeasureTaskbar(current));
        }

        private void PlaceWidget(Placement placement)
        {
            var location = new POINT { X = placement.Widget.X, Y = placement.Widget.Y };
            if (!ScreenToClient(placement.Taskbar, ref location))
            {
                Hide();
                return;
            }

            try
            {
                if (_taskbar != placement.Taskbar)
                {
                    Hide();
                    _taskbar = placement.Taskbar;
                    if (IsHandleCreated) RecreateHandle();
                }
                IntPtr handle = Handle;
                Size size = placement.Widget.Size;
                _widgetNeedsPaint |= Size != size;
                if (GetParent(handle) != _taskbar ||
                    !SetWindowPos(handle, IntPtr.Zero, location.X, location.Y, size.Width, size.Height, SWP_NOACTIVATE) ||
                    (_widgetNeedsPaint && !PaintLayeredWidget(handle, size)))
                {
                    Hide();
                    return;
                }
                _widgetNeedsPaint = false;
                Show();
            }
            catch (Win32Exception)
            {
                Hide();
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) // WM_MOUSEACTIVATE
            {
                message.Result = new IntPtr(3); // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref message);
        }


        private bool PaintLayeredWidget(IntPtr handle, Size size)
        {
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;

            var header = new BitmapHeader
            {
                Size = (uint)Marshal.SizeOf(typeof(BitmapHeader)),
                Width = size.Width,
                Height = -size.Height,
                Planes = 1,
                BitsPerPixel = 32
            };
            IntPtr pixels;
            IntPtr dib = CreateDIBSection(dc, ref header, 0, out pixels, IntPtr.Zero, 0);
            IntPtr previous = IntPtr.Zero;
            try
            {
                if (dib == IntPtr.Zero || pixels == IntPtr.Zero) return false;
                previous = SelectObject(dc, dib);
                if (previous == IntPtr.Zero) return false;
                using (var bitmap = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, pixels))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    PaintWidget(graphics, size);
                }

                var dimensions = new SIZE { Width = size.Width, Height = size.Height };
                var source = new POINT();
                var blend = new Blend { Opacity = 255, AlphaFormat = 1 };
                return UpdateLayeredWindow(handle, IntPtr.Zero, IntPtr.Zero, ref dimensions,
                    dc, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (previous != IntPtr.Zero) SelectObject(dc, previous);
                if (dib != IntPtr.Zero) DeleteObject(dib);
                DeleteDC(dc);
            }
        }

        private static double? PickAgyWindowPct(UsageData agyData, bool weekly)
        {
            if (agyData == null) return null;
            string target = weekly ? "周" : "5h";
            double? min = null;
            if (agyData.Groups != null && agyData.Groups.Count > 0)
            {
                foreach (QuotaGroup g in agyData.Groups)
                {
                    foreach (QuotaBucket b in g.Buckets)
                    {
                        if (b.ShortLabel != target || !b.RemainingPct.HasValue) continue;
                        if (!min.HasValue || b.RemainingPct.Value < min.Value)
                            min = b.RemainingPct.Value;
                    }
                }
            }
            else if (agyData.Buckets != null)
            {
                foreach (QuotaBucket b in agyData.Buckets)
                {
                    if (b.ShortLabel != target || !b.RemainingPct.HasValue) continue;
                    if (!min.HasValue || b.RemainingPct.Value < min.Value)
                        min = b.RemainingPct.Value;
                }
            }
            return min.HasValue ? min : agyData.MinRemaining;
        }

        private void PaintWidget(Graphics g, Size size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            Rectangle client = new Rectangle(Point.Empty, size);
            Color bg = _hover ? ColorTranslator.FromHtml("#262934") : ColorTranslator.FromHtml("#1c1e26");
            Color bd = _hover ? Theme.Accent : ColorTranslator.FromHtml("#333746");
            using (GraphicsPath frame = Renderer.RoundedRect(new RectangleF(0.5f, 0.5f, size.Width - 1, size.Height - 1), 6))
            using (SolidBrush br = new SolidBrush(bg))
            using (Pen p = new Pen(bd, 1f))
            {
                g.FillPath(br, frame);
                g.DrawPath(p, frame);
            }

            if (_mode == TaskbarDisplayMode.Dual && _agyName != null)
            {
                int rowH = size.Height / 2;
                string winTag = _showWeekly ? "周" : "5h";
                double? cdxPct = _cdxData != null
                    ? (_showWeekly ? (_cdxData.SRemaining ?? _cdxData.PRemaining) : (_cdxData.PRemaining ?? _cdxData.SRemaining))
                    : null;
                string cdxFallback = _cdxName == null ? "未登" : (_cdxErrKind == "auth" ? "重登" : "--");
                Color cdxFbColor = _cdxErrKind == "auth" ? Theme.Red : Theme.Sub;
                DrawRow(g, "C·" + winTag, cdxPct, 2, rowH - 2, size.Width, cdxFallback, cdxFbColor);

                double? agyPct = PickAgyWindowPct(_agyData, _showWeekly);
                string agyFallback = _agyErrKind == "offline" ? "离线" : (_agyErrKind == "auth" ? "重登" : "--");
                Color agyFbColor = _agyErrKind == "auth" ? Theme.Red : Theme.Sub;
                DrawRow(g, "A·" + winTag, agyPct, rowH, size.Height - rowH - 2, size.Width, agyFallback, agyFbColor);
            }
            else if (_data != null)
            {
                int rowH = size.Height / 2;
                if (_data.Provider == ProviderKind.Antigravity && _data.Groups != null && _data.Groups.Count > 0)
                {
                    QuotaGroup g1 = _data.Groups[0];
                    DrawRow(g, g1.ShortName ?? "Gem", g1.MinRemaining, 2, rowH - 2, size.Width, "--", Theme.Sub);
                    if (_data.Groups.Count >= 2)
                    {
                        QuotaGroup g2 = _data.Groups[1];
                        DrawRow(g, g2.ShortName ?? "Cld", g2.MinRemaining, rowH, size.Height - rowH - 2, size.Width, "--", Theme.Sub);
                    }
                    else
                    {
                        DrawRow(g, "AGY", g1.MinRemaining, rowH, size.Height - rowH - 2, size.Width, "--", Theme.Sub);
                    }
                }
                else
                {
                    DrawRow(g, "5h", _data.PRemaining, 2, rowH - 2, size.Width, "--", Theme.Sub);
                    DrawRow(g, "周", _data.SRemaining, rowH, size.Height - rowH - 2, size.Width, "--", Theme.Sub);
                }
            }
            else if (_errKind == "auth")
            {
                using (SolidBrush br = new SolidBrush(Theme.Red))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("⚠ 需重新登录", _fMsg, br, client, sf);
            }
            else if (_errKind == "offline")
            {
                using (SolidBrush br = new SolidBrush(Theme.Sub))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("AGY 未运行", _fMsg, br, client, sf);
            }
            else
            {
                using (SolidBrush br = new SolidBrush(Theme.Sub))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("加载中…", _fMsg, br, client, sf);
            }
        }

        private void DrawRow(Graphics g, string label, double? pct, int y, int h, int width, string fallbackText, Color fallbackColor)
        {
            Color c = pct.HasValue ? Theme.ColorFor(pct) : fallbackColor;
            using (SolidBrush subBr = new SolidBrush(Theme.Sub))
            using (StringFormat sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString(label, _fLbl, subBr, new RectangleF(6, y, 42, h), sfLeft);
            }

            // 迷你进度条
            float barX = 48f;
            float barW = 48f;
            float barH = 5f;
            float barY = y + (h - barH) / 2f;
            using (GraphicsPath tr = Renderer.RoundedRect(new RectangleF(barX, barY, barW, barH), 2.5f))
            using (SolidBrush trBr = new SolidBrush(Theme.Track))
                g.FillPath(trBr, tr);

            if (pct.HasValue)
            {
                float p = (float)Math.Max(0.0, Math.Min(100.0, pct.Value));
                float fw = barW * p / 100f;
                if (fw >= 2.5f)
                {
                    using (GraphicsPath fg = Renderer.RoundedRect(new RectangleF(barX, barY, fw, barH), 2.5f))
                    using (SolidBrush fgBr = new SolidBrush(c))
                        g.FillPath(fgBr, fg);
                }
            }

            string val = pct.HasValue ? string.Format("{0}%", (int)Math.Round(pct.Value)) : fallbackText;
            using (SolidBrush valBr = new SolidBrush(c))
            using (StringFormat sfRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString(val, _fVal, valBr, new RectangleF(98, y, width - 104, h), sfRight);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _keepTimer.Stop();
                _keepTimer.Dispose();
                _cycleTimer.Stop();
                _cycleTimer.Dispose();
                _fLbl.Dispose();
                _fVal.Dispose();
                _fMsg.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
