using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CodexUsageMonitor
{
    /// <summary>
    /// 自绘抗锯齿圆角胶囊按钮 / 切换芯片
    /// </summary>
    internal class RoundChip : Control
    {
        private bool _active;
        private bool _hover;
        private bool _primary;
        private bool _danger;
        private int _radius = 5;

        public Color NormalBg = Theme.Card;
        public Color HoverBg = Theme.CardHover;
        public Color ActiveBg = Theme.Accent;
        public Color ActiveHoverBg = ColorTranslator.FromHtml("#96c7ff");
        public Color NormalFg = Theme.Sub;
        public Color HoverFg = Theme.Text;
        public Color ActiveFg = ColorTranslator.FromHtml("#12141a");

        public bool Active
        {
            get { return _active; }
            set { if (_active != value) { _active = value; Invalidate(); } }
        }

        public bool Primary
        {
            get { return _primary; }
            set { _primary = value; Invalidate(); }
        }

        public bool Danger
        {
            get { return _danger; }
            set { _danger = value; Invalidate(); }
        }

        public int Radius
        {
            get { return _radius; }
            set { _radius = value; Invalidate(); }
        }

        public RoundChip(string text, Font font, int width, int height)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Text = text;
            Font = font;
            Size = new Size(width, height);
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hover = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = false;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Color bg, fg;
            Color? border = null;
            if (_danger)
            {
                bg = _hover ? ColorTranslator.FromHtml("#55282e") : ColorTranslator.FromHtml("#3c2024");
                fg = Theme.Red;
                border = ColorTranslator.FromHtml("#6a323a");
            }
            else if (_active || _primary)
            {
                bg = _hover ? ActiveHoverBg : ActiveBg;
                fg = ActiveFg;
            }
            else
            {
                bg = _hover ? HoverBg : NormalBg;
                fg = _hover ? HoverFg : NormalFg;
                border = ColorTranslator.FromHtml("#313440");
            }

            float penW = Math.Max(1f, NativeWin.DpiScale * 0.6f);
            RectangleF rect = new RectangleF(penW * 0.5f, penW * 0.5f, Width - penW - 0.5f, Height - penW - 0.5f);
            using (GraphicsPath gp = Renderer.RoundedRect(rect, _radius))
            {
                using (SolidBrush br = new SolidBrush(bg))
                    g.FillPath(br, gp);
                if (border.HasValue)
                {
                    using (Pen pen = new Pen(border.Value, penW))
                        g.DrawPath(pen, gp);
                }
            }

            using (SolidBrush textBr = new SolidBrush(fg))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            {
                g.DrawString(Text, Font, textBr, new RectangleF(0, 0, Width, Height), sf);
            }
        }
    }

    public class SettingsForm : Form
    {
        private const int FormWidth = 416;
        private const int PadX = 14;
        private const int ContentW = FormWidth - 2 - PadX * 2; // 386

        private readonly MainForm _owner;
        private Panel _inner;
        private Panel _body;
        private Panel _acctBox;
        private Label _lblAcct;
        private Label _lblStatus;
        private Label _lblAlphaVal;
        private TextBox _txtCustomSec;
        private RoundChip _btnRestartCodex;

        private readonly Dictionary<int, RoundChip> _alphaChips = new Dictionary<int, RoundChip>();
        private readonly Dictionary<int, RoundChip> _taskbarChips = new Dictionary<int, RoundChip>();
        private readonly Dictionary<int, RoundChip> _autostartChips = new Dictionary<int, RoundChip>();
        private readonly Dictionary<int, RoundChip> _agyEnableChips = new Dictionary<int, RoundChip>();
        private readonly Dictionary<int, RoundChip> _tbModeChips = new Dictionary<int, RoundChip>();
        private readonly Dictionary<int, RoundChip> _notifyChips = new Dictionary<int, RoundChip>();
        private readonly Dictionary<int, RoundChip> _intervalChips = new Dictionary<int, RoundChip>();
        private Point? _dragStart;

        private Font _fTitle;
        private Font _fSec;
        private Font _fSub;
        private Font _fChip;
        private Font _fTiny;
        private Font _fPill;

        // 布局锚点面板，便于多账号高度动态变化时自动重排下方区域
        private Panel _lowerPanel;

        public SettingsForm(MainForm owner)
        {
            _owner = owner;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            Opacity = 0.98;
            BackColor = Theme.Border;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
            };

            AutoScaleMode = AutoScaleMode.None;
            AutoScroll = true;
            ApplyFontSize();
            Load += delegate { NativeWin.ApplyRoundCorners(Handle); };
        }

        private int UiPx(int value) { return _owner.UiPx(value); }
        private Point UiPoint(int x, int y) { return _owner.UiPoint(x, y); }
        private Size UiSize(int width, int height) { return _owner.UiSize(width, height); }

        private Font CreateFont(float size, FontStyle style)
        {
            return new Font("Microsoft YaHei UI", size * _owner.FontScale, style);
        }

        private void DisposeFonts()
        {
            if (_fTitle == null) return;
            _fTitle.Dispose();
            _fSec.Dispose();
            _fSub.Dispose();
            _fChip.Dispose();
            _fTiny.Dispose();
            _fPill.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing) DisposeFonts();
        }

        internal void ApplyFontSize(float previousScale = 0f)
        {
            string status = _lblStatus == null ? "" : _lblStatus.Text;
            Color statusColor = _lblStatus == null ? Theme.Sub : _lblStatus.ForeColor;
            string customSeconds = _txtCustomSec == null ? null : _txtCustomSec.Text;
            int scrollY = previousScale > 0f
                ? (int)Math.Round(-AutoScrollPosition.Y * _owner.WindowScale / previousScale)
                : 0;

            SuspendLayout();
            try
            {
                AutoScrollPosition = Point.Empty;
                if (_inner != null) _inner.Dispose();
                DisposeFonts();
                _alphaChips.Clear();
                _taskbarChips.Clear();
                _autostartChips.Clear();
                _agyEnableChips.Clear();
                _tbModeChips.Clear();
                _notifyChips.Clear();
                _intervalChips.Clear();
                _fTitle = CreateFont(10f, FontStyle.Bold);
                _fSec = CreateFont(8.2f, FontStyle.Bold);
                _fSub = CreateFont(8.4f, FontStyle.Regular);
                _fChip = CreateFont(8.2f, FontStyle.Regular);
                _fTiny = CreateFont(7.6f, FontStyle.Regular);
                _fPill = CreateFont(7.4f, FontStyle.Bold);
                BuildUi();
                if (customSeconds != null) _txtCustomSec.Text = customSeconds;
                SetStatus(status, statusColor);
                RenderAccounts();
                Refit();
            }
            finally
            {
                ResumeLayout(true);
            }
            AutoScrollPosition = new Point(0, scrollY);
        }

        private void BuildUi()
        {
            Width = UiPx(FormWidth);

            _inner = new Panel
            {
                BackColor = Theme.Bg,
                Location = UiPoint(1, 1),
                Width = UiPx(FormWidth - 2)
            };
            Controls.Add(_inner);

            // ---- 顶部标题栏 ----
            Panel head = new Panel
            {
                Location = UiPoint(0, 0),
                Size = UiSize(FormWidth - 2, 38),
                BackColor = Theme.Bg
            };
            Label title = new Label
            {
                Text = "设置",
                ForeColor = Theme.Text,
                Font = _fTitle,
                AutoSize = true,
                Location = UiPoint(PadX, 10)
            };
            Label closeBtn = new Label
            {
                Text = "✕",
                ForeColor = Theme.Sub,
                Font = _fSub,
                Size = UiSize(24, 24),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = UiPoint(FormWidth - 2 - PadX - 20, 7),
                Cursor = Cursors.Hand
            };
            closeBtn.Click += delegate { Close(); };
            closeBtn.MouseEnter += delegate { closeBtn.ForeColor = Theme.Red; };
            closeBtn.MouseLeave += delegate { closeBtn.ForeColor = Theme.Sub; };
            head.Controls.Add(title);
            head.Controls.Add(closeBtn);
            head.Controls.Add(new Panel { BackColor = Theme.Border, Location = UiPoint(PadX, 37), Size = UiSize(ContentW, 1) });
            BindDrag(head);
            BindDrag(title);
            _inner.Controls.Add(head);

            _body = new Panel
            {
                Location = UiPoint(0, 38),
                Width = UiPx(FormWidth - 2),
                BackColor = Theme.Bg
            };
            _inner.Controls.Add(_body);

            // ---- 账号管理标题 ----
            _lblAcct = new Label
            {
                Text = "多账号",
                ForeColor = Theme.Sub,
                Font = _fSec,
                AutoSize = true,
                Location = UiPoint(PadX, 10)
            };
            _body.Controls.Add(_lblAcct);

            // 账号卡片容器（高度随账号数动态计算）
            _acctBox = new Panel
            {
                Location = UiPoint(PadX, 30),
                Width = UiPx(ContentW),
                BackColor = Theme.Bg
            };
            _body.Controls.Add(_acctBox);

            // ---- 下半部分固定排版容器 ----
            _lowerPanel = new Panel
            {
                Location = UiPoint(0, 40),
                Width = UiPx(FormWidth - 2),
                BackColor = Theme.Bg
            };
            _body.Controls.Add(_lowerPanel);

            int y = 0;

            // 状态提示行
            _lblStatus = new Label
            {
                Text = "",
                ForeColor = Theme.Sub,
                Font = _fTiny,
                Location = UiPoint(PadX, y),
                Size = UiSize(ContentW, 16)
            };
            _lowerPanel.Controls.Add(_lblStatus);
            y += 20;

            // 添加新账号 + 同步当前CLI + (切号后1分钟内显示)重启Codex
            RoundChip bAdd = new RoundChip("+ 添加 Codex 账号", _fChip, UiPx(134), UiPx(28)) { Radius = UiPx(5), Primary = true, Location = UiPoint(PadX, y) };
            bAdd.Click += delegate
            {
                string msg;
                bool ok = _owner.LaunchNewLogin(out msg);
                SetStatus(msg, ok ? Theme.Accent : Theme.Red);
            };
            RoundChip bAddAgy = new RoundChip("+ 添加 AGY 云端账号", _fChip, UiPx(146), UiPx(28))
            {
                Radius = UiPx(5),
                Primary = true,
                ActiveBg = ColorTranslator.FromHtml("#9c7cff"),
                ActiveHoverBg = ColorTranslator.FromHtml("#b399ff"),
                ActiveFg = ColorTranslator.FromHtml("#12101a"),
                Location = UiPoint(PadX + 140, y)
            };
            bAddAgy.Click += delegate
            {
                string msg;
                bool ok = _owner.LaunchAgyCloudLogin(out msg);
                SetStatus(msg, ok ? ColorTranslator.FromHtml("#c5b3ff") : Theme.Red);
            };
            RoundChip bSync = new RoundChip("同步 CLI", _fChip, UiPx(74), UiPx(28)) { Radius = UiPx(5), Location = UiPoint(PadX + 292, y) };
            bSync.NormalFg = Theme.Text;
            bSync.Click += delegate
            {
                string msg;
                bool ok = _owner.SyncCurrentLogin(out msg);
                SetStatus(msg, ok ? Theme.Green : Theme.Red);
                if (ok) { RenderAccounts(); Refit(); }
            };
            _lowerPanel.Controls.Add(bAdd);
            _lowerPanel.Controls.Add(bAddAgy);
            _lowerPanel.Controls.Add(bSync);
            y += 34;

            _btnRestartCodex = new RoundChip("↻ 重启Codex", _fPill, UiPx(120), UiPx(24))
            {
                Radius = UiPx(5),
                Primary = true,
                ActiveBg = Theme.Yellow,
                ActiveHoverBg = ColorTranslator.FromHtml("#ffe07d"),
                ActiveFg = ColorTranslator.FromHtml("#1a1506"),
                Location = UiPoint(PadX, y),
                Visible = false
            };
            _btnRestartCodex.Click += delegate
            {
                string msg;
                if (_owner.PromptRestartCodex(this, out msg) && !string.IsNullOrEmpty(msg))
                    SetStatus(msg, Theme.Accent);
            };
            _lowerPanel.Controls.Add(_btnRestartCodex);

            Label tip = new Label
            {
                Text = "提示：Codex 切号点『切为CLI』；AGY 自动读本机，也可点上方添加多账号",
                ForeColor = Theme.Muted,
                Font = _fTiny,
                AutoSize = true,
                Location = UiPoint(PadX, y + 3)
            };
            _lowerPanel.Controls.Add(tip);
            y += 24;

            // 分隔线 1
            _lowerPanel.Controls.Add(new Panel { BackColor = Theme.Border, Location = UiPoint(PadX, y), Size = UiSize(ContentW, 1) });
            y += 12;

            // ---- 窗口透明度 ----
            _lowerPanel.Controls.Add(new Label { Text = "窗口透明度", ForeColor = Theme.Sub, Font = _fSec, AutoSize = true, Location = UiPoint(PadX, y) });
            int fontX = PadX + 290;
            _lowerPanel.Controls.Add(new Label { Text = "字体大小", ForeColor = Theme.Sub, Font = _fSec, AutoSize = true, Location = UiPoint(fontX, y) });
            const int chipW = 62;
            const int chipGap = 8;
            int[] alphas = new int[] { 100, 80, 60, 40 };
            int curAlpha = (int)Math.Round(_owner.Config.alpha * 100.0);
            _lblAlphaVal = new Label
            {
                Text = curAlpha + "%",
                ForeColor = Theme.Accent,
                Font = _fSec,
                Size = UiSize(46, 16),
                TextAlign = ContentAlignment.MiddleRight,
                Location = UiPoint(PadX + alphas.Length * (chipW + chipGap) - chipGap - 46, y)
            };
            _lowerPanel.Controls.Add(_lblAlphaVal);
            y += 22;

            for (int i = 0; i < alphas.Length; i++)
            {
                int val = alphas[i];
                RoundChip chip = new RoundChip(val + "%", _fChip, UiPx(chipW), UiPx(26))
                {
                    Radius = UiPx(5),
                    Location = UiPoint(PadX + i * (chipW + chipGap), y),
                    Active = (val == curAlpha)
                };
                chip.Click += delegate
                {
                    _lblAlphaVal.Text = val + "%";
                    HighlightChip(_alphaChips, val);
                    _owner.SetAlpha(val / 100.0);
                };
                _alphaChips[val] = chip;
                _lowerPanel.Controls.Add(chip);
            }

            string[] fontLabels = { "小", "中", "大" };
            WindowFontSize[] fontSizes = { WindowFontSize.Small, WindowFontSize.Medium, WindowFontSize.Large };
            for (int i = 0; i < fontSizes.Length; i++)
            {
                WindowFontSize size = fontSizes[i];
                RoundChip chip = new RoundChip(fontLabels[i], _fChip, UiPx(28), UiPx(26))
                {
                    Radius = UiPx(5),
                    Location = UiPoint(fontX + i * 33, y),
                    Active = size == _owner.Config.font_size
                };
                chip.Click += delegate { _owner.SetFontSize(size); };
                _lowerPanel.Controls.Add(chip);
            }
            y += 38;

            // ---- 任务栏常驻显示 & 开机自启（并排双列，绿开红关） ----
            int col2X = PadX + 200;
            _lowerPanel.Controls.Add(new Label { Text = "任务栏常驻显示", ForeColor = Theme.Sub, Font = _fSec, AutoSize = true, Location = UiPoint(PadX, y) });
            _lowerPanel.Controls.Add(new Label { Text = "开机自动启动", ForeColor = Theme.Sub, Font = _fSec, AutoSize = true, Location = UiPoint(col2X, y) });
            y += 22;

            Color greenHover = ColorTranslator.FromHtml("#5ae698");
            Color greenFg = ColorTranslator.FromHtml("#0d1c14");
            Color redHover = ColorTranslator.FromHtml("#ff7676");

            int curTb = _owner.Config.show_in_taskbar ? 1 : 0;
            int[] binVals = new int[] { 1, 0 };
            string[] tbTxts = new string[] { "显示", "关闭" };
            for (int i = 0; i < 2; i++)
            {
                int tv = binVals[i];
                RoundChip chip = new RoundChip(tbTxts[i], _fChip, UiPx(64), UiPx(26))
                {
                    Radius = UiPx(5),
                    Location = UiPoint(PadX + i * 72, y),
                    Active = (tv == curTb),
                    ActiveBg = tv == 1 ? Theme.Green : Theme.Red,
                    ActiveHoverBg = tv == 1 ? greenHover : redHover,
                    ActiveFg = tv == 1 ? greenFg : Color.White
                };
                chip.Click += delegate
                {
                    HighlightChip(_taskbarChips, tv);
                    _owner.SetShowInTaskbar(tv == 1);
                };
                _taskbarChips[tv] = chip;
                _lowerPanel.Controls.Add(chip);
            }

            int curAuto = CodexApi.AutostartEnabled() ? 1 : 0;
            string[] autoTxts = new string[] { "开启", "关闭" };
            for (int i = 0; i < 2; i++)
            {
                int av = binVals[i];
                RoundChip chip = new RoundChip(autoTxts[i], _fChip, UiPx(64), UiPx(26))
                {
                    Radius = UiPx(5),
                    Location = UiPoint(col2X + i * 72, y),
                    Active = (av == curAuto),
                    ActiveBg = av == 1 ? Theme.Green : Theme.Red,
                    ActiveHoverBg = av == 1 ? greenHover : redHover,
                    ActiveFg = av == 1 ? greenFg : Color.White
                };
                chip.Click += delegate
                {
                    HighlightChip(_autostartChips, av);
                    CodexApi.SetAutostart(av == 1);
                };
                _autostartChips[av] = chip;
                _lowerPanel.Controls.Add(chip);
            }
            y += 38;

            // ---- Antigravity 监控 & 任务栏模式（并排双列） ----
            _lowerPanel.Controls.Add(new Label { Text = "Antigravity 监控", ForeColor = Theme.Sub, Font = _fSec, AutoSize = true, Location = UiPoint(PadX, y) });
            _lowerPanel.Controls.Add(new Label { Text = "任务栏展示模式", ForeColor = Theme.Sub, Font = _fSec, AutoSize = true, Location = UiPoint(col2X, y) });
            y += 22;

            int curAgy = _owner.Config.enable_antigravity ? 1 : 0;
            for (int i = 0; i < 2; i++)
            {
                int agv = binVals[i];
                RoundChip chip = new RoundChip(autoTxts[i], _fChip, UiPx(64), UiPx(26))
                {
                    Radius = UiPx(5),
                    Location = UiPoint(PadX + i * 72, y),
                    Active = (agv == curAgy),
                    ActiveBg = agv == 1 ? Theme.Green : Theme.Red,
                    ActiveHoverBg = agv == 1 ? greenHover : redHover,
                    ActiveFg = agv == 1 ? greenFg : Color.White
                };
                chip.Click += delegate
                {
                    HighlightChip(_agyEnableChips, agv);
                    _owner.SetEnableAntigravity(agv == 1);
                };
                _agyEnableChips[agv] = chip;
                _lowerPanel.Controls.Add(chip);
            }

            int curMode = _owner.Config.taskbar_mode == TaskbarDisplayMode.Dual ? 0 : 1;
            string[] modeTxts = new string[] { "双平台同屏", "跟随当前" };
            for (int i = 0; i < 2; i++)
            {
                int mv = i;
                RoundChip chip = new RoundChip(modeTxts[i], _fChip, UiPx(82), UiPx(26))
                {
                    Radius = UiPx(5),
                    Location = UiPoint(col2X + i * 88, y),
                    Active = (mv == curMode)
                };
                chip.Click += delegate
                {
                    HighlightChip(_tbModeChips, mv);
                    _owner.SetTaskbarMode(mv == 0 ? TaskbarDisplayMode.Dual : TaskbarDisplayMode.FollowCurrent);
                };
                _tbModeChips[mv] = chip;
                _lowerPanel.Controls.Add(chip);
            }
            y += 38;

            // ---- 低额度提醒 ----
            _lowerPanel.Controls.Add(new Label { Text = "低额度系统通知", ForeColor = Theme.Sub, Font = _fSec, AutoSize = true, Location = UiPoint(PadX, y) });
            y += 22;

            int curNotify = _owner.Config.notify_low ? _owner.Config.notify_threshold : 0;
            int[] nVals = new int[] { 0, 10, 20, 30 };
            string[] nTxts = new string[] { "关闭", "≤10%", "≤20%", "≤30%" };
            for (int i = 0; i < nVals.Length; i++)
            {
                int nv = nVals[i];
                RoundChip chip = new RoundChip(nTxts[i], _fChip, UiPx(66), UiPx(26))
                {
                    Radius = UiPx(5),
                    Location = UiPoint(PadX + i * 74, y),
                    Active = (nv == curNotify)
                };
                if (nv == 0)
                {
                    chip.ActiveBg = Theme.Red;
                    chip.ActiveHoverBg = redHover;
                    chip.ActiveFg = Color.White;
                }
                chip.Click += delegate
                {
                    HighlightChip(_notifyChips, nv);
                    _owner.SetNotifyLow(nv > 0, nv > 0 ? nv : _owner.Config.notify_threshold);
                };
                _notifyChips[nv] = chip;
                _lowerPanel.Controls.Add(chip);
            }
            y += 38;

            // 分隔线 2
            _lowerPanel.Controls.Add(new Panel { BackColor = Theme.Border, Location = UiPoint(PadX, y), Size = UiSize(ContentW, 1) });
            y += 12;

            // ---- 采集频率 ----
            _lowerPanel.Controls.Add(new Label { Text = "采集频率", ForeColor = Theme.Sub, Font = _fSec, AutoSize = true, Location = UiPoint(PadX, y) });
            y += 22;

            int curIv = _owner.Config.interval;
            int[] ivs = new int[] { 15, 30, 60, 120, 300 };
            for (int i = 0; i < ivs.Length; i++)
            {
                int s = ivs[i];
                RoundChip chip = new RoundChip(s + "s", _fChip, UiPx(58), UiPx(26))
                {
                    Radius = UiPx(5),
                    Location = UiPoint(PadX + i * 66, y),
                    Active = (s == curIv)
                };
                chip.Click += delegate
                {
                    HighlightChip(_intervalChips, s);
                    _owner.SetInterval(s);
                };
                _intervalChips[s] = chip;
                _lowerPanel.Controls.Add(chip);
            }
            y += 35;

            // 自定义(秒) — 暗色细边框包裹无框 TextBox + 圆角应用按钮
            _lowerPanel.Controls.Add(new Label
            {
                Text = "自定义(秒)",
                ForeColor = Theme.Sub,
                Font = _fSub,
                AutoSize = true,
                Location = UiPoint(PadX, y + 4)
            });

            Panel inputWrap = new Panel
            {
                BackColor = ColorTranslator.FromHtml("#313440"),
                Location = UiPoint(PadX + 74, y),
                Size = UiSize(68, 25)
            };
            Panel inputInner = new Panel
            {
                BackColor = Theme.Card,
                Location = UiPoint(1, 1),
                Size = UiSize(66, 23)
            };
            _txtCustomSec = new TextBox
            {
                Location = UiPoint(6, 3),
                Width = UiPx(54),
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
                Font = _fSub,
                Text = _intervalChips.ContainsKey(curIv) ? "" : curIv.ToString()
            };
            Action applyCustom = delegate
            {
                int val;
                if (!int.TryParse(_txtCustomSec.Text.Trim(), out val))
                {
                    SetStatus("请输入 15~3600 之间的整数秒", Theme.Red);
                    return;
                }
                val = Math.Max(15, Math.Min(3600, val));
                _txtCustomSec.Text = val.ToString();
                HighlightChip(_intervalChips, val);
                _owner.SetInterval(val);
                SetStatus("采集间隔已设为 " + val + "s", Theme.Green);
            };
            _txtCustomSec.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    applyCustom();
                }
            };
            Renderer.RoundControl(inputWrap, UiPx(6));
            Renderer.RoundControl(inputInner, UiPx(5));
            inputInner.Controls.Add(_txtCustomSec);
            inputWrap.Controls.Add(inputInner);
            _lowerPanel.Controls.Add(inputWrap);

            RoundChip btnApply = new RoundChip("应用", _fTiny, UiPx(48), UiPx(25))
            {
                Radius = UiPx(5),
                Location = UiPoint(PadX + 148, y)
            };
            btnApply.NormalFg = Theme.Text;
            btnApply.Click += delegate { applyCustom(); };
            _lowerPanel.Controls.Add(btnApply);

            _lowerPanel.Controls.Add(new Label
            {
                Text = "范围 15~3600s",
                ForeColor = Theme.Muted,
                Font = _fTiny,
                AutoSize = true,
                Location = UiPoint(PadX + 204, y + 5)
            });
            y += 36;

            // 底部脚注
            _lowerPanel.Controls.Add(new Label
            {
                Text = "所有修改即时生效并自动保存 · 按 Esc 关闭",
                ForeColor = Theme.Muted,
                Font = _fTiny,
                AutoSize = true,
                Location = UiPoint(PadX, y)
            });
            y += 20;

            _lowerPanel.Controls.Add(new Label
            {
                Text = Program.VersionLabel,
                ForeColor = Theme.Sub,
                Font = _fTiny,
                AutoSize = true,
                Location = UiPoint(PadX, y)
            });
            y += 24;

            _lowerPanel.Height = UiPx(y);
        }

        private void HighlightChip(Dictionary<int, RoundChip> map, int activeVal)
        {
            foreach (KeyValuePair<int, RoundChip> kv in map)
                kv.Value.Active = (kv.Key == activeVal);
        }

        private void SetStatus(string text, Color color)
        {
            _lblStatus.Text = text ?? "";
            _lblStatus.ForeColor = color;
        }

        public void ShowExternalStatus(string text, Color color)
        {
            SetStatus(text, color);
        }

        public void UpdateRestartButton()
        {
            if (_btnRestartCodex == null || _btnRestartCodex.IsDisposed) return;
            int left = _owner.RestartPromptSecondsLeft;
            if (left > 0)
            {
                _btnRestartCodex.Text = string.Format("重启Codex({0}s)", left);
                _btnRestartCodex.Visible = true;
                _btnRestartCodex.Invalidate();
            }
            else
            {
                _btnRestartCodex.Visible = false;
            }
        }

        public void RenderAccounts()
        {
            HighlightChip(_taskbarChips, _owner.Config.show_in_taskbar ? 1 : 0);
            HighlightChip(_autostartChips, CodexApi.AutostartEnabled() ? 1 : 0);
            HighlightChip(_agyEnableChips, _owner.Config.enable_antigravity ? 1 : 0);
            HighlightChip(_tbModeChips, _owner.Config.taskbar_mode == TaskbarDisplayMode.Dual ? 0 : 1);
            UpdateRestartButton();

            _acctBox.SuspendLayout();
            while (_acctBox.Controls.Count > 0)
                _acctBox.Controls[0].Dispose();
            List<AccountInfo> accts = _owner.Accounts;
            _lblAcct.Text = string.Format("多账号 ({0})", accts.Count);

            int y = 0;
            int cardH = 36;
            foreach (AccountInfo a in accts)
            {
                AccountInfo acct = a;
                bool cur = _owner.ViewAccount != null && acct.Uid == _owner.ViewAccount.Uid;

                bool isAgy = acct.Provider == ProviderKind.Antigravity;
                // 卡片外框（当前 CLI 账号带淡淡绿色边框，AGY 本机紫框，当前查看账号带蓝色边框）
                Color borderCol = acct.Live
                    ? ColorTranslator.FromHtml("#2a6f4c")
                    : (cur ? ColorTranslator.FromHtml("#3c5b82") : (isAgy ? ColorTranslator.FromHtml("#4b3e6e") : Theme.Border));
                Panel cardWrap = new Panel
                {
                    BackColor = borderCol,
                    Location = UiPoint(0, y),
                    Size = UiSize(ContentW, cardH)
                };
                Panel card = new Panel
                {
                    BackColor = Theme.Card,
                    Location = UiPoint(1, 1),
                    Size = UiSize(ContentW - 2, cardH - 2)
                };
                Renderer.RoundControl(cardWrap, UiPx(9));
                Renderer.RoundControl(card, UiPx(8));
                cardWrap.Controls.Add(card);

                // 右侧操作区采用绝对 X 坐标从右向左精确定位，顺序工整不错乱
                int rightX = ContentW - 2 - 8;

                if (isAgy && acct.IsAgyLocal)
                {
                    Label localTag = new Label
                    {
                        Text = "● 本机自动",
                        ForeColor = ColorTranslator.FromHtml("#c5b3ff"),
                        BackColor = ColorTranslator.FromHtml("#2d2445"),
                        Font = _fPill,
                        Size = UiSize(78, 20),
                        TextAlign = ContentAlignment.MiddleCenter,
                        Location = UiPoint(rightX - 78, 7)
                    };
                    Renderer.RoundControl(localTag, UiPx(5));
                    card.Controls.Add(localTag);
                    rightX -= 84;
                }
                else if (isAgy)
                {
                    // 云端 AGY 账号：删除 ✕ + 重命名 ✎ + 状态/重新登录
                    Label xr = new Label
                    {
                        Text = "✕",
                        ForeColor = Theme.Sub,
                        Font = _fTiny,
                        Size = UiSize(20, 22),
                        TextAlign = ContentAlignment.MiddleCenter,
                        Location = UiPoint(rightX - 20, 6),
                        Cursor = Cursors.Hand
                    };
                    xr.MouseEnter += delegate { xr.ForeColor = Theme.Red; };
                    xr.MouseLeave += delegate { xr.ForeColor = Theme.Sub; };
                    xr.Click += delegate
                    {
                        if (_owner.RemoveAccountByUid(acct.Uid))
                        {
                            SetStatus("已删除 Antigravity 账号", Theme.Sub);
                            RenderAccounts();
                            Refit();
                        }
                    };
                    card.Controls.Add(xr);
                    rightX -= 22;

                    Label ed = new Label
                    {
                        Text = "✎",
                        ForeColor = Theme.Sub,
                        Font = _fTiny,
                        Size = UiSize(20, 22),
                        TextAlign = ContentAlignment.MiddleCenter,
                        Location = UiPoint(rightX - 20, 6),
                        Cursor = Cursors.Hand
                    };
                    ed.MouseEnter += delegate { ed.ForeColor = Theme.Accent; };
                    ed.MouseLeave += delegate { ed.ForeColor = Theme.Sub; };
                    ed.Click += delegate { PromptRename(acct); };
                    card.Controls.Add(ed);
                    rightX -= 24;

                    if (acct.AuthBad)
                    {
                        RoundChip rl = new RoundChip("重新登录", _fPill, UiPx(68), UiPx(22))
                        {
                            Radius = UiPx(5),
                            Danger = true,
                            BackColor = Theme.Card,
                            Location = UiPoint(rightX - 68, 6)
                        };
                        rl.Click += delegate
                        {
                            string msg;
                            bool ok = _owner.LaunchRelogin(acct, out msg);
                            SetStatus(msg, ok ? Theme.Accent : Theme.Red);
                        };
                        card.Controls.Add(rl);
                        rightX -= 74;
                    }
                    else
                    {
                        Label cloudTag = new Label
                        {
                            Text = "☁ 云端",
                            ForeColor = Theme.Accent,
                            BackColor = ColorTranslator.FromHtml("#222b3d"),
                            Font = _fPill,
                            Size = UiSize(54, 20),
                            TextAlign = ContentAlignment.MiddleCenter,
                            Location = UiPoint(rightX - 54, 7)
                        };
                        Renderer.RoundControl(cloudTag, UiPx(5));
                        card.Controls.Add(cloudTag);
                        rightX -= 60;
                    }
                }
                else if (!acct.Live)
                {
                    // 最右侧：删除 ✕
                    Label xr = new Label
                    {
                        Text = "✕",
                        ForeColor = Theme.Sub,
                        Font = _fTiny,
                        Size = UiSize(20, 22),
                        TextAlign = ContentAlignment.MiddleCenter,
                        Location = UiPoint(rightX - 20, 6),
                        Cursor = Cursors.Hand
                    };
                    xr.MouseEnter += delegate { xr.ForeColor = Theme.Red; };
                    xr.MouseLeave += delegate { xr.ForeColor = Theme.Sub; };
                    xr.Click += delegate
                    {
                        if (_owner.RemoveAccountByUid(acct.Uid))
                        {
                            SetStatus("已删除账号快照", Theme.Sub);
                            RenderAccounts();
                            Refit();
                        }
                        else SetStatus("当前 CLI 账号无法删除", Theme.Red);
                    };
                    card.Controls.Add(xr);
                    rightX -= 22;

                    // 次右侧：重命名 ✎
                    Label ed = new Label
                    {
                        Text = "✎",
                        ForeColor = Theme.Sub,
                        Font = _fTiny,
                        Size = UiSize(20, 22),
                        TextAlign = ContentAlignment.MiddleCenter,
                        Location = UiPoint(rightX - 20, 6),
                        Cursor = Cursors.Hand
                    };
                    ed.MouseEnter += delegate { ed.ForeColor = Theme.Accent; };
                    ed.MouseLeave += delegate { ed.ForeColor = Theme.Sub; };
                    ed.Click += delegate { PromptRename(acct); };
                    card.Controls.Add(ed);
                    rightX -= 24;

                    // 主操作按钮：切为CLI 或 重新登录
                    if (acct.AuthBad)
                    {
                        RoundChip rl = new RoundChip("重新登录", _fPill, UiPx(68), UiPx(22))
                        {
                            Radius = UiPx(5),
                            Danger = true,
                            BackColor = Theme.Card,
                            Location = UiPoint(rightX - 68, 6)
                        };
                        rl.Click += delegate
                        {
                            string msg;
                            bool ok = _owner.LaunchRelogin(acct, out msg);
                            SetStatus(msg, ok ? Theme.Accent : Theme.Red);
                        };
                        card.Controls.Add(rl);
                        rightX -= 74;
                    }
                    else
                    {
                        RoundChip sw = new RoundChip("切为CLI", _fPill, UiPx(64), UiPx(22))
                        {
                            Radius = UiPx(5),
                            BackColor = Theme.Card,
                            NormalBg = ColorTranslator.FromHtml("#282c3a"),
                            NormalFg = Theme.Accent,
                            HoverBg = Theme.Accent,
                            HoverFg = ColorTranslator.FromHtml("#12141a"),
                            Location = UiPoint(rightX - 64, 6)
                        };
                        sw.Click += delegate
                        {
                            string msg;
                            bool ok = _owner.SwitchCliByUid(acct.Uid, out msg);
                            SetStatus(msg, ok ? Theme.Green : Theme.Red);
                            RenderAccounts();
                            Refit();
                        };
                        card.Controls.Add(sw);
                        rightX -= 70;
                    }
                }
                else
                {
                    if (acct.AuthBad)
                    {
                        RoundChip rl = new RoundChip("重新登录", _fPill, UiPx(68), UiPx(22))
                        {
                            Radius = UiPx(5),
                            Danger = true,
                            BackColor = Theme.Card,
                            Location = UiPoint(rightX - 68, 6)
                        };
                        rl.Click += delegate
                        {
                            string msg;
                            bool ok = _owner.LaunchRelogin(acct, out msg);
                            SetStatus(msg, ok ? Theme.Accent : Theme.Red);
                        };
                        card.Controls.Add(rl);
                        rightX -= 74;
                    }
                    else
                    {
                        Label cliTag = new Label
                        {
                            Text = "● CLI当前",
                            ForeColor = Theme.Green,
                            BackColor = ColorTranslator.FromHtml("#1d3328"),
                            Font = _fPill,
                            Size = UiSize(78, 20),
                            TextAlign = ContentAlignment.MiddleCenter,
                            Location = UiPoint(rightX - 78, 7)
                        };
                        Renderer.RoundControl(cliTag, UiPx(5));
                        card.Controls.Add(cliTag);
                        rightX -= 84;
                    }
                }

                // 左侧：选中指示 + 平台前缀 + 邮箱（点击切换主窗查看）
                int maxUnits = Math.Max(12, (rightX - 52) / 7);
                Label nameLbl = new Label
                {
                    Text = string.Format("{0} [{1}] {2}", cur ? "✓" : "○", acct.ProviderTag, FormatUtil.Ellipsize(acct.DisplayName, maxUnits)),
                    ForeColor = cur ? Theme.Accent : Theme.Text,
                    BackColor = Theme.Card,
                    Font = _fSub,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Location = UiPoint(10, 6),
                    Size = UiSize(Math.Max(80, rightX - 14), 22),
                    Cursor = Cursors.Hand
                };
                nameLbl.Click += delegate
                {
                    _owner.SetDisplayUid(acct.Uid, false);
                    RenderAccounts();
                };
                nameLbl.MouseEnter += delegate { if (!cur) nameLbl.ForeColor = ColorTranslator.FromHtml("#a8d0ff"); };
                nameLbl.MouseLeave += delegate { if (!cur) nameLbl.ForeColor = Theme.Text; };
                card.Controls.Add(nameLbl);

                _acctBox.Controls.Add(cardWrap);
                y += cardH + 6;
            }

            _acctBox.Height = UiPx(y);
            _acctBox.ResumeLayout(true);
            _lowerPanel.Top = _acctBox.Bottom + UiPx(4);
            _body.Height = _lowerPanel.Bottom;
        }

        private void PromptRename(AccountInfo acct)
        {
            TopMost = false;
            try
            {
                using (Form dlg = new Form())
                {
                    dlg.Text = "命名账号";
                    dlg.AutoScaleMode = AutoScaleMode.None;
                    dlg.Font = _fSub;
                    dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.MaximizeBox = false;
                    dlg.MinimizeBox = false;
                    dlg.ClientSize = UiSize(280, 108);
                    dlg.BackColor = Theme.Bg;
                    dlg.ForeColor = Theme.Text;

                    Label lb = new Label { Text = "显示名称（留空恢复默认邮箱）:", Left = UiPx(14), Top = UiPx(12), AutoSize = true, ForeColor = Theme.Sub, Font = _fSub };
                    Panel input = new Panel { Location = UiPoint(14, 36), Size = UiSize(252, 26), BackColor = Theme.Card };
                    TextBox tb = new TextBox { Left = UiPx(6), Top = UiPx(4), Width = UiPx(240), BorderStyle = BorderStyle.None, Text = string.IsNullOrEmpty(acct.Label) ? acct.DisplayName : acct.Label, BackColor = Theme.Card, ForeColor = Theme.Text, Font = _fSub };
                    Button okBtn = new Button { Text = "确定", DialogResult = DialogResult.OK, Left = UiPx(190), Top = UiPx(70), Size = UiSize(76, 26), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, BackColor = Theme.Accent, ForeColor = ColorTranslator.FromHtml("#12141a"), Font = _fSub };
                    okBtn.FlatAppearance.BorderSize = 0;
                    Renderer.RoundControl(input, UiPx(6));
                    Renderer.RoundControl(okBtn, UiPx(6));
                    input.Controls.Add(tb);
                    dlg.Controls.Add(lb);
                    dlg.Controls.Add(input);
                    dlg.Controls.Add(okBtn);
                    dlg.AcceptButton = okBtn;
                    if (dlg.ShowDialog(this) == DialogResult.OK)
                    {
                        _owner.RenameAccountByUid(acct.Uid, tb.Text.Trim());
                        SetStatus("已更新显示名称", Theme.Green);
                        RenderAccounts();
                        Refit();
                    }
                }
            }
            finally
            {
                TopMost = true;
            }
        }

        public void Refit()
        {
            if (_inner == null || _body == null) return;
            _inner.Height = _body.Bottom + UiPx(2);
            Rectangle area = Screen.FromControl(_owner).WorkingArea;
            _owner.FitPopup(this, _inner, area);
            int pad = _owner.DpiPx(8);
            int x = Math.Min(Math.Max(area.Left + pad, _owner.Left - _owner.DpiPx(12)), Math.Max(area.Left + pad, area.Right - Width - pad));
            int y = _owner.Top - Height - pad;
            if (y < area.Top + pad)
                y = Math.Min(area.Bottom - Height - pad, _owner.Bottom + pad);
            if (y < area.Top + pad)
                y = area.Top + Math.Max(pad, (area.Height - Height) / 2);
            Location = new Point(x, y);
        }

        private void BindDrag(Control c)
        {
            c.MouseDown += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) _dragStart = Control.MousePosition;
            };
            c.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (_dragStart.HasValue && e.Button == MouseButtons.Left)
                {
                    Point cur = Control.MousePosition;
                    Location = new Point(Left + cur.X - _dragStart.Value.X, Top + cur.Y - _dragStart.Value.Y);
                    _dragStart = cur;
                }
            };
            c.MouseUp += delegate { _dragStart = null; };
        }
    }
}
