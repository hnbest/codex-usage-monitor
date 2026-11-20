using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexUsageMonitor
{
    /// <summary>
    /// 固定贴附在 Windows 任务栏上的实时额度小组件 (类似 TrafficMonitor 任务栏模式)
    /// 无需点击悬浮窗, 随时在任务栏右下角直接看到 5h / 每周剩余百分比与进度条
    /// </summary>
    public class TaskbarWidgetForm : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string cls, string title);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("shell32.dll")]
        private static extern int SHQueryUserNotificationState(out int pquns);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private readonly MainForm _owner;
        private readonly System.Windows.Forms.Timer _keepTimer;
        private readonly ToolTip _tip;

        private UsageData _data;
        private string _err;
        private string _errKind;
        private string _acctName = "";
        private bool _hover;
        private bool _hiddenByFullscreen;

        private bool _dragging;
        private bool _dragMoved;
        private int _dragStartMouseX;
        private int _dragStartOffsetX;

        private readonly Font _fLbl = new Font("Microsoft YaHei UI", 7.8f, FontStyle.Regular);
        private readonly Font _fVal = new Font("Microsoft YaHei UI", 8.0f, FontStyle.Bold);
        private readonly Font _fMsg = new Font("Microsoft YaHei UI", 8.0f, FontStyle.Regular);

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        public TaskbarWidgetForm(MainForm owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(138, 38);
            BackColor = ColorTranslator.FromHtml("#1c1e26");
            Renderer.RoundControl(this, 6);
            Opacity = 0.96;
            Cursor = Cursors.Hand;

            _tip = new ToolTip
            {
                InitialDelay = 300,
                AutoPopDelay = 8000,
                ReshowDelay = 200
            };

            MouseEnter += delegate { _hover = true; Invalidate(); };
            MouseLeave += delegate { _hover = false; Invalidate(); };

            MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    _dragging = true;
                    _dragMoved = false;
                    _dragStartMouseX = Control.MousePosition.X;
                    _dragStartOffsetX = _owner.Config.taskbar_offset_x;
                }
            };

            MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_dragging && e.Button == MouseButtons.Left)
                {
                    int dx = Control.MousePosition.X - _dragStartMouseX;
                    if (Math.Abs(dx) >= 4) _dragMoved = true;
                    if (_dragMoved)
                    {
                        _owner.Config.taskbar_offset_x = _dragStartOffsetX + dx;
                        UpdatePosition();
                    }
                }
            };

            MouseUp += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && _dragging)
                {
                    _dragging = false;
                    if (_dragMoved)
                    {
                        CodexApi.SaveConfig(_owner.Config);
                    }
                    else
                    {
                        _owner.ToggleWindowFromOutside();
                    }
                }
                else if (e.Button == MouseButtons.Right)
                {
                    _owner.ShowContextMenuAt(Control.MousePosition);
                }
            };

            _keepTimer = new System.Windows.Forms.Timer();
            _keepTimer.Interval = 400;
            _keepTimer.Tick += delegate { KeepOnTaskbar(); };

            Load += delegate
            {
                NativeWin.ApplyRoundCorners(Handle);
                UpdatePosition();
                _keepTimer.Start();
            };
        }

        public void UpdateData(string acctName, UsageData data, string err, string errKind)
        {
            _acctName = acctName ?? "";
            _data = data;
            _err = err;
            _errKind = errKind;

            string tipText;
            if (_data != null)
            {
                tipText = string.Format(
                    "{0} [{1}]\n5小时: 剩 {2}% · {3} 重置\n每周: 剩 {4}% · {5} 重置\n左键展开详情 · 拖动可微调位置 · 右键菜单",
                    _acctName,
                    (_data.Plan ?? "").ToUpperInvariant(),
                    _data.PRemaining.HasValue ? ((int)Math.Round(_data.PRemaining.Value)).ToString() : "-",
                    FormatUtil.FmtResetShort(_data.PResetAt),
                    _data.SRemaining.HasValue ? ((int)Math.Round(_data.SRemaining.Value)).ToString() : "-",
                    FormatUtil.FmtResetShort(_data.SResetAt));
            }
            else if (_errKind == "auth")
            {
                tipText = _acctName + "\n会话已失效，点击打开窗口重新登录";
            }
            else
            {
                tipText = string.IsNullOrEmpty(_err) ? "Codex 用量 - 加载中…" : ("Codex 用量\n" + _err);
            }
            try { _tip.SetToolTip(this, tipText); } catch { }
            Invalidate();
        }

        public void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                _hiddenByFullscreen = false;
                if (!Visible) Show();
                UpdatePosition();
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                if (!_keepTimer.Enabled) _keepTimer.Start();
            }
            else
            {
                _keepTimer.Stop();
                if (Visible) Hide();
            }
        }

        public void UpdatePosition()
        {
            IntPtr trayWnd = FindWindow("Shell_TrayWnd", null);
            RECT tbRect = new RECT();
            bool hasTb = trayWnd != IntPtr.Zero && GetWindowRect(trayWnd, out tbRect)
                         && (tbRect.Right - tbRect.Left) > 100 && (tbRect.Bottom - tbRect.Top) > 20;

            Rectangle scr = Screen.PrimaryScreen.Bounds;
            Rectangle work = Screen.PrimaryScreen.WorkingArea;

            int tbLeft = hasTb ? tbRect.Left : scr.Left;
            int tbTop = hasTb ? tbRect.Top : work.Bottom;
            int tbRight = hasTb ? tbRect.Right : scr.Right;
            int tbBottom = hasTb ? tbRect.Bottom : scr.Bottom;
            int tbH = Math.Max(32, tbBottom - tbTop);

            int widgetH = Math.Max(28, Math.Min(38, tbH - 8));
            if (Height != widgetH) Height = widgetH;

            int anchorRight = tbRight - 220;
            if (hasTb)
            {
                IntPtr notifyWnd = FindWindowEx(trayWnd, IntPtr.Zero, "TrayNotifyWnd", null);
                RECT tnRect;
                if (notifyWnd != IntPtr.Zero && GetWindowRect(notifyWnd, out tnRect) && tnRect.Left > tbLeft + Width)
                {
                    anchorRight = tnRect.Left - 8;
                }
            }

            int x = anchorRight - Width + _owner.Config.taskbar_offset_x;
            int minX = tbLeft + 8;
            int maxX = Math.Max(minX, tbRight - Width - 8);
            x = Math.Min(Math.Max(minX, x), maxX);
            int y = tbTop + (tbH - Height) / 2;

            if (Left != x || Top != y)
                Location = new Point(x, y);
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
                if (fg == IntPtr.Zero || fg == Handle || fg == _owner.Handle) return false;
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
            if (!_owner.Config.show_in_taskbar) return;

            bool fs = IsForegroundFullscreen();
            if (fs)
            {
                if (Visible)
                {
                    _hiddenByFullscreen = true;
                    Hide();
                }
                return;
            }
            if (_hiddenByFullscreen && !Visible)
            {
                _hiddenByFullscreen = false;
                Show();
            }

            if (Visible)
            {
                if (!_dragging) UpdatePosition();
                SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color bg = _hover ? ColorTranslator.FromHtml("#262934") : ColorTranslator.FromHtml("#1c1e26");
            Color bd = _hover ? Theme.Accent : ColorTranslator.FromHtml("#333746");
            using (GraphicsPath frame = Renderer.RoundedRect(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 6))
            using (SolidBrush br = new SolidBrush(bg))
            using (Pen p = new Pen(bd, 1f))
            {
                g.FillPath(br, frame);
                g.DrawPath(p, frame);
            }

            if (_data != null)
            {
                int rowH = Height / 2;
                DrawRow(g, "5h", _data.PRemaining, 2, rowH - 2);
                DrawRow(g, "周", _data.SRemaining, rowH, Height - rowH - 2);
            }
            else if (_errKind == "auth")
            {
                using (SolidBrush br = new SolidBrush(Theme.Red))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("⚠ 需重新登录", _fMsg, br, ClientRectangle, sf);
            }
            else
            {
                using (SolidBrush br = new SolidBrush(Theme.Sub))
                using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString("Codex …", _fMsg, br, ClientRectangle, sf);
            }
        }

        private void DrawRow(Graphics g, string label, double? pct, int y, int h)
        {
            Color c = Theme.ColorFor(pct);
            using (SolidBrush subBr = new SolidBrush(Theme.Sub))
            using (StringFormat sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(label, _fLbl, subBr, new RectangleF(7, y, 22, h), sfLeft);
            }

            // 迷你进度条
            float barX = 28f;
            float barW = 46f;
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

            string val = pct.HasValue ? string.Format("{0}%", (int)Math.Round(pct.Value)) : "--";
            using (SolidBrush valBr = new SolidBrush(c))
            using (StringFormat sfRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString(val, _fVal, valBr, new RectangleF(76, y, Width - 82, h), sfRight);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _keepTimer.Stop();
                _keepTimer.Dispose();
                _tip.Dispose();
                _fLbl.Dispose();
                _fVal.Dispose();
                _fMsg.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
