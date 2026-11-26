using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CodexUsageMonitor
{
    public class MainForm : Form
    {
        private const int WindowWidth = 364;

        private readonly object _lock = new object();
        private readonly Dictionary<string, FetchResult> _results = new Dictionary<string, FetchResult>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double?> _notifiedLow = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _reloginWatch = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _pendingLogins = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly ManualResetEventSlim _wake = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);
        private EventWaitHandle _showEvent;

        private bool _liveAuthBad;
        private bool _footClickable;
        private bool _footIsRestart;
        private DateTime _restartPromptUntil = DateTime.MinValue;
        private System.Windows.Forms.Timer _restartTimer;
        private System.Windows.Forms.Timer _waveTimer;
        private Point? _dragStart;
        private Icon _currentTrayIcon;
        private NotifyIcon _tray;
        private System.Windows.Forms.Timer _trayBalloonTimer;
        private TaskbarWidgetForm _taskbarWidget;
        private SettingsForm _settingsWin;
        private Form _switcherWin;
        private Form _creditsWin;
        private ContextMenuStrip _activeMenu;

        // UI 控件
        private Panel _inner;
        private Panel _cardsHost;
        private Label _lblBrand;
        private Label _lblNick;
        private Label _lblPlan;
        private Label _lblCredits;
        private Label _lblMode;
        private Label _lblFoot;
        private Label _lblHint;
        private Label _lblCreditWarn;
        private Button _btnCardCredits;
        private Label _lblCardPlan;
        private PictureBox _gearBtn;
        private Bitmap _gearDim;
        private Bitmap _gearHot;

        // 单账号双环控件 (Codex)
        private RingControl _ringP;
        private RingControl _ringS;
        private Label _lblTimeP;
        private Label _lblTimeS;

        // 单账号模型池容器 (Antigravity)
        private Panel _agyBucketsPanel;
        private Label _lblAgySource;
        private Label _lblAgyPlan;
        private string _lastAgyRenderKey = "";

        // 多账号总览卡片控件
        private class OverviewWidgets
        {
            public Panel CardWrap;
            public Panel Card;
            public Panel AgyHost;
            public string LastAgyKey;
            public Label Plan;
            public Label TagP;
            public MiniBarControl BarP;
            public Label ValP;
            public Label RstP;
            public Label TagS;
            public MiniBarControl BarS;
            public Label ValS;
            public Label RstS;
        }
        private readonly Dictionary<string, OverviewWidgets> _overviewCards = new Dictionary<string, OverviewWidgets>(StringComparer.OrdinalIgnoreCase);

        private sealed class WindowFonts
        {
            public readonly Font Title, Name, Sub, Tiny, Pill;
            public readonly float Scale;

            public WindowFonts(float scale)
            {
                Scale = scale;
                Title = new Font("Microsoft YaHei UI", 10.5f * scale, FontStyle.Bold);
                Name = new Font("Microsoft YaHei UI", 9f * scale, FontStyle.Bold);
                Sub = new Font("Microsoft YaHei UI", 8.2f * scale, FontStyle.Regular);
                Tiny = new Font("Microsoft YaHei UI", 7.8f * scale, FontStyle.Regular);
                Pill = new Font("Microsoft YaHei UI", 7.2f * scale, FontStyle.Bold);
            }
        }

        private readonly WindowFonts _smallFonts = new WindowFonts(1f);
        private readonly WindowFonts _mediumFonts = new WindowFonts(1.2f);
        private readonly WindowFonts _largeFonts = new WindowFonts(1.4f);
        private WindowFonts Fonts
        {
            get
            {
                switch (Config.font_size)
                {
                    case WindowFontSize.Medium: return _mediumFonts;
                    case WindowFontSize.Large: return _largeFonts;
                    default: return _smallFonts;
                }
            }
        }
        internal float DpiScale { get { return NativeWin.DpiScale; } }
        internal float FontScale { get { return Fonts.Scale; } }
        internal float WindowScale { get { return DpiScale * FontScale; } }

        internal int DpiPx(int value) { return (int)Math.Round(value * DpiScale); }
        internal int UiPx(int value) { return (int)Math.Round(value * WindowScale); }
        internal Point UiPoint(int x, int y) { return new Point(UiPx(x), UiPx(y)); }
        internal Size UiSize(int width, int height) { return new Size(UiPx(width), UiPx(height)); }
        private Padding UiPadding(int left, int top, int right, int bottom)
        {
            return new Padding(UiPx(left), UiPx(top), UiPx(right), UiPx(bottom));
        }

        public AppConfig Config { get; private set; }
        public List<AccountInfo> Accounts { get; private set; }
        public AccountInfo ViewAccount { get; private set; }
        public int RestartPromptSecondsLeft
        {
            get
            {
                if (_restartPromptUntil == DateTime.MinValue) return 0;
                int sec = (int)Math.Ceiling((_restartPromptUntil - DateTime.UtcNow).TotalSeconds);
                return sec > 0 ? sec : 0;
            }
        }

        public MainForm()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            Config = CodexApi.LoadConfig();
            Accounts = new List<AccountInfo>();
            ReloadAccounts();

            Text = "Codex 用量";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Border;
            Opacity = Math.Max(0.4, Math.Min(1.0, Config.alpha));
            Width = UiPx(WindowWidth);
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int x = Config.window_x ?? (area.Right - Width - DpiPx(16));
            int y = Config.window_y ?? (area.Top + Math.Max(0, (area.Height - UiPx(210)) / 2));
            Location = new Point(x, y);

            BuildWindow();
            InitTray();
            _restartTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _restartTimer.Tick += delegate { OnRestartTimerTick(); };
            _waveTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _waveTimer.Tick += delegate
            {
                if (!Visible || IsOverviewMode()) return;
                if (_ringP != null && !_ringP.IsDisposed) _ringP.StepWave(0.14f);
                if (_ringS != null && !_ringS.IsDisposed) _ringS.StepWave(0.11f);
            };
            _waveTimer.Start();
            RenderAccount();
            if (!Config.window_y.HasValue)
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int cy = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
                Location = new Point(Left, cy);
            }
            ClampToScreen();

            Load += delegate
            {
                NativeWin.ApplyRoundCorners(Handle);
                ClampToScreen();
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                Logger.Log(string.Format("窗口就绪: Loc=({0},{1}) Size=({2}x{3}) DPI={4:0.##}x Scale={5:0.##}x Vis={6} Opacity={7} WA=({8},{9},{10}x{11})",
                    Left, Top, Width, Height, DpiScale, WindowScale, Visible, Opacity, wa.Left, wa.Top, wa.Width, wa.Height));
                _taskbarWidget = new TaskbarWidgetForm(this);
                _taskbarWidget.VisibleChanged += delegate { UpdateTrayVisibility(); };
                _taskbarWidget.SetEnabled(Config.show_in_taskbar);
                ApplyState();
                Thread t = new Thread(PollLoop);
                t.IsBackground = true;
                t.Start();
                StartShowEventWatcher();
            };

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    ToggleWindow();
                }
            };
        }

        private void RunUi(Action act)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                if (InvokeRequired) BeginInvoke(act);
                else act();
            }
            catch { }
        }

        // ---------------- 账号管理 ----------------
        private void ReloadAccounts()
        {
            List<AccountInfo> list = CodexApi.ListAccounts();
            foreach (AccountInfo a in list)
            {
                if (a.Live) a.AuthBad = _liveAuthBad;
            }
            List<AccountInfo> agyList = AntigravityApi.ListAccounts(Config.enable_antigravity);
            list.AddRange(agyList);

            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (AccountInfo a in list)
            {
                string key = a.ProviderTag + ":" + a.DisplayName;
                int cnt = seen.ContainsKey(key) ? seen[key] + 1 : 1;
                seen[key] = cnt;
                if (cnt > 1) a.Label = string.Format("{0} ({1})", a.DisplayName, cnt);
            }
            Accounts = list;
            string uid = Config.display_uid ?? "";
            ViewAccount = null;
            foreach (AccountInfo a in Accounts)
            {
                if (!string.IsNullOrEmpty(uid) && a.Uid == uid)
                {
                    ViewAccount = a;
                    break;
                }
            }
            if (ViewAccount == null && Accounts.Count > 0) ViewAccount = Accounts[0];
            Logger.Log(string.Format("账号: {0} 个, 当前显示 {1}", Accounts.Count, ViewAccount != null ? ViewAccount.DisplayName : "(无)"));
        }

        private string LiveAccountName()
        {
            foreach (AccountInfo a in Accounts)
            {
                if (a.Provider == ProviderKind.Codex && a.Live) return a.DisplayName;
            }
            return "未登录";
        }

        private bool IsOverviewMode()
        {
            return Config.view_mode == "all" && Accounts.Count > 1;
        }

        public void ToggleViewMode()
        {
            if (Accounts.Count < 2) Config.view_mode = "single";
            else Config.view_mode = IsOverviewMode() ? "single" : "all";
            CodexApi.SaveConfig(Config);
            RenderAccount();
            ApplyState();
            KickPoll();
        }

        public void SetDisplayUid(string uid, bool switchToSingle)
        {
            Config.display_uid = uid ?? "";
            if (switchToSingle) Config.view_mode = "single";
            CodexApi.SaveConfig(Config);
            ReloadAccounts();
            RenderAccount();
            ApplyState();
            KickPoll();
        }

        public bool SwitchCliByUid(string uid, out string msg)
        {
            Dictionary<string, FetchResult> uidCache = new Dictionary<string, FetchResult>(StringComparer.Ordinal);
            lock (_lock)
            {
                foreach (AccountInfo a in Accounts)
                {
                    FetchResult fr;
                    if (_results.TryGetValue(a.Home, out fr))
                        uidCache[a.Uid] = fr;
                }
            }

            bool ok = CodexApi.SwitchCliAccount(uid, out msg);
            if (ok)
            {
                _liveAuthBad = false;
                ReloadAccounts();
                lock (_lock)
                {
                    foreach (AccountInfo a in Accounts)
                    {
                        FetchResult fr;
                        if (uidCache.TryGetValue(a.Uid, out fr))
                            _results[a.Home] = fr;
                    }
                }
                StartRestartPromptWindow();
                RenderAccount();
                ApplyState();
                KickPoll();
            }
            else
            {
                _lblFoot.Text = "⚠ " + msg;
                _lblFoot.ForeColor = Theme.Red;
            }
            return ok;
        }

        private void StartRestartPromptWindow()
        {
            _restartPromptUntil = DateTime.UtcNow.AddSeconds(60);
            if (_restartTimer != null && !_restartTimer.Enabled)
                _restartTimer.Start();
            if (_settingsWin != null && !_settingsWin.IsDisposed)
                _settingsWin.UpdateRestartButton();
        }

        private void ClearRestartPromptWindow()
        {
            _restartPromptUntil = DateTime.MinValue;
            if (_restartTimer != null && _restartTimer.Enabled)
                _restartTimer.Stop();
            if (_settingsWin != null && !_settingsWin.IsDisposed)
                _settingsWin.UpdateRestartButton();
        }

        private void OnRestartTimerTick()
        {
            int left = RestartPromptSecondsLeft;
            if (left <= 0)
            {
                ClearRestartPromptWindow();
                ApplyState();
                return;
            }
            UpdateFootRestartPrompt(left);
            if (_settingsWin != null && !_settingsWin.IsDisposed)
                _settingsWin.UpdateRestartButton();
        }

        private void UpdateFootRestartPrompt(int leftSec)
        {
            _footClickable = true;
            _footIsRestart = true;
            _lblFoot.Cursor = Cursors.Hand;
            _lblFoot.Text = string.Format("↻ 切号成功 · 点此重启 Codex ({0}s)", leftSec);
            _lblFoot.ForeColor = Theme.Yellow;
        }

        public bool PromptRestartCodex(IWin32Window ownerWin, out string msg)
        {
            msg = "";
            bool mainTop = TopMost;
            bool setTop = _settingsWin != null && !_settingsWin.IsDisposed && _settingsWin.TopMost;
            TopMost = false;
            if (_settingsWin != null && !_settingsWin.IsDisposed) _settingsWin.TopMost = false;
            DialogResult dr;
            try
            {
                dr = MessageBox.Show(
                    ownerWin ?? this,
                    "重启会中断 Codex 中正在运行的任务。\r\n\r\n点击“确定”立即重启，点击“取消”不重启。",
                    "重启 Codex",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
            }
            finally
            {
                TopMost = mainTop;
                if (_settingsWin != null && !_settingsWin.IsDisposed) _settingsWin.TopMost = setTop;
            }

            if (dr != DialogResult.OK)
                return false;

            ClearRestartPromptWindow();
            _footClickable = false;
            _footIsRestart = false;
            _lblFoot.Cursor = Cursors.Default;
            _lblFoot.Text = "↻ 正在重启 Codex…";
            _lblFoot.ForeColor = Theme.Accent;
            msg = "正在重启 Codex…";

            Thread t = new Thread((ThreadStart)delegate
            {
                string rmsg;
                bool ok = CodexApi.RestartCodexApp(out rmsg);
                RunUi(delegate
                {
                    _lblFoot.Text = (ok ? "✓ " : "⚠ ") + rmsg;
                    _lblFoot.ForeColor = ok ? Theme.Green : Theme.Red;
                    if (_settingsWin != null && !_settingsWin.IsDisposed)
                        _settingsWin.ShowExternalStatus(rmsg, ok ? Theme.Green : Theme.Red);
                });
            });
            t.IsBackground = true;
            t.Start();
            return true;
        }

        public bool SyncCurrentLogin(out string msg)
        {
            string uid;
            bool ok = CodexApi.SaveCurrentLogin(out msg, out uid);
            if (ok)
            {
                _liveAuthBad = false;
                Config.display_uid = uid;
                CodexApi.SaveConfig(Config);
                ReloadAccounts();
                RenderAccount();
                ApplyState();
                KickPoll();
            }
            return ok;
        }

        public bool LaunchRelogin(AccountInfo acct, out string msg)
        {
            if (acct == null) { msg = "没有可重新登录的账号"; return false; }
            if (acct.Provider == ProviderKind.Antigravity)
            {
                if (acct.IsAgyLocal)
                {
                    KickPoll();
                    msg = "请在 Antigravity 客户端中保持登录，监控将自动识别";
                    return true;
                }
                return LaunchAgyCloudLogin(out msg);
            }
            try
            {
                Directory.CreateDirectory(acct.Home);
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/k codex login");
                psi.WorkingDirectory = acct.Home;
                psi.UseShellExecute = false;
                if (!acct.Live) psi.EnvironmentVariables["CODEX_HOME"] = acct.Home;
                Process.Start(psi);
                CodexApi.ClearRefreshCooldown(acct.Home);
                lock (_lock) { _reloginWatch[acct.Home] = CodexApi.AuthMtimeTicks(acct.Home); }
                KickPoll();
                msg = "已打开 " + acct.DisplayName + " 的登录窗口, 完成后自动恢复";
                Logger.Log("已拉起重新登录窗口: " + acct.DisplayName);
                return true;
            }
            catch (Exception ex)
            {
                msg = "打开失败: " + ex.Message;
                return false;
            }
        }

        public bool LaunchAgyCloudLogin(out string msg)
        {
            return AntigravityApi.StartCloudOAuthLogin(Config, delegate(bool ok, string resultMsg)
            {
                RunUi(delegate
                {
                    if (ok)
                    {
                        if (!Config.enable_antigravity)
                        {
                            Config.enable_antigravity = true;
                            CodexApi.SaveConfig(Config);
                        }
                        ReloadAccounts();
                        RenderAccount();
                        ApplyState();
                        KickPoll();
                        if (_settingsWin != null && !_settingsWin.IsDisposed)
                        {
                            _settingsWin.RenderAccounts();
                            _settingsWin.Refit();
                            _settingsWin.ShowExternalStatus(resultMsg, Theme.Green);
                        }
                        try { ShowTrayBalloon("Antigravity 账号就绪", resultMsg, ToolTipIcon.Info); }
                        catch { }
                    }
                    else
                    {
                        if (_settingsWin != null && !_settingsWin.IsDisposed)
                            _settingsWin.ShowExternalStatus(resultMsg, Theme.Red);
                    }
                });
            }, out msg);
        }

        public bool LaunchNewLogin(out string msg)
        {
            try
            {
                string pdir = CodexApi.CreatePendingLoginDir();
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/k codex login");
                psi.WorkingDirectory = pdir;
                psi.UseShellExecute = false;
                psi.EnvironmentVariables["CODEX_HOME"] = pdir;
                Process.Start(psi);
                lock (_lock) { _pendingLogins[pdir] = FormatUtil.NowUnix(); }
                KickPoll();
                msg = "已打开独立登录窗口(不影响当前CLI), 完成后自动加入";
                Logger.Log("已拉起独立新账号登录窗口: " + pdir);
                return true;
            }
            catch (Exception ex)
            {
                msg = "打开失败: " + ex.Message;
                return false;
            }
        }

        public bool RemoveAccountByUid(string uid)
        {
            bool ok = (!string.IsNullOrEmpty(uid) && uid.StartsWith("agy:", StringComparison.OrdinalIgnoreCase))
                ? AntigravityApi.RemoveAccount(uid)
                : CodexApi.RemoveAccount(uid);
            if (!ok) return false;
            if (Config.display_uid == uid)
            {
                Config.display_uid = "";
                CodexApi.SaveConfig(Config);
            }
            ReloadAccounts();
            RenderAccount();
            ApplyState();
            KickPoll();
            return true;
        }

        public void RenameAccountByUid(string uid, string label)
        {
            if (!string.IsNullOrEmpty(uid) && uid.StartsWith("agy:", StringComparison.OrdinalIgnoreCase))
                AntigravityApi.RenameAccount(uid, label);
            else
                CodexApi.RenameAccount(uid, label);
            ReloadAccounts();
            RenderAccount();
            ApplyState();
        }

        public void SetAlpha(double val)
        {
            val = Math.Max(0.4, Math.Min(1.0, Math.Round(val, 2)));
            Config.alpha = val;
            CodexApi.SaveConfig(Config);
            Opacity = val;
        }

        public void SetInterval(int sec)
        {
            sec = Math.Max(15, Math.Min(3600, sec));
            Config.interval = sec;
            CodexApi.SaveConfig(Config);
            _lblHint.Text = string.Format("每 {0}s", sec);
            _lblHint.ForeColor = Theme.Muted;
            KickPoll();
        }

        public void SetFontSize(WindowFontSize size)
        {
            if (Config.font_size == size) return;

            float previousScale = WindowScale;
            Config.font_size = size;
            SuspendLayout();
            try
            {
                _inner.Dispose();
                _gearDim.Dispose();
                _gearHot.Dispose();
                Width = UiPx(WindowWidth);
                BuildWindow();
                RenderAccount();
                ApplyState();
            }
            finally
            {
                ResumeLayout(true);
            }
            if (_settingsWin != null && !_settingsWin.IsDisposed)
                _settingsWin.ApplyFontSize(previousScale);
            if (_creditsWin != null && !_creditsWin.IsDisposed && ViewAccount != null)
                RenderCreditsDetail(_creditsWin);
            CodexApi.SaveConfig(Config);
        }

        public void SetNotifyLow(bool enabled, int threshold)
        {
            Config.notify_low = enabled;
            Config.notify_threshold = threshold;
            CodexApi.SaveConfig(Config);
            lock (_lock) { _notifiedLow.Clear(); }
        }

        public void SetShowInTaskbar(bool show)
        {
            Config.show_in_taskbar = show;
            CodexApi.SaveConfig(Config);
            if (_taskbarWidget != null && !_taskbarWidget.IsDisposed)
                _taskbarWidget.SetEnabled(show);
        }

        public void SetEnableAntigravity(bool enabled)
        {
            if (Config.enable_antigravity == enabled) return;
            Config.enable_antigravity = enabled;
            CodexApi.SaveConfig(Config);
            ReloadAccounts();
            RenderAccount();
            ApplyState();
            KickPoll();
            if (_settingsWin != null && !_settingsWin.IsDisposed)
            {
                _settingsWin.RenderAccounts();
                _settingsWin.Refit();
            }
        }

        public void SetTaskbarMode(TaskbarDisplayMode mode)
        {
            if (Config.taskbar_mode == mode) return;
            Config.taskbar_mode = mode;
            CodexApi.SaveConfig(Config);
            ApplyState();
        }

        public void ToggleWindowFromOutside()
        {
            ToggleWindow();
        }

        public void ShowContextMenuAt(Point screenPos)
        {
            ShowContextMenu(screenPos);
        }

        private void ManualRefresh()
        {
            foreach (AccountInfo a in Accounts)
                CodexApi.ClearRefreshCooldown(a.Home);
            _lblHint.Text = "刷新中…";
            _lblHint.ForeColor = Theme.Accent;
            KickPoll();
        }

        private void ResetPosition()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int x = Math.Max(area.Left, area.Right - Width - DpiPx(16));
            int y = area.Top + Math.Max(0, (area.Height - Height) / 2);
            Location = new Point(x, y);
            Config.window_x = x;
            Config.window_y = y;
            Config.taskbar_offset_x = 0;
            CodexApi.SaveConfig(Config);
            if (!Visible) Show();
            Activate();
        }

        // ---------------- 窗口构建 ----------------
        private void BuildWindow()
        {
            _inner = new Panel
            {
                BackColor = Theme.Bg,
                Location = UiPoint(1, 1),
                Width = UiPx(WindowWidth - 2)
            };
            Controls.Add(_inner);

            // ---- 标题栏 ----
            Panel bar = new Panel
            {
                BackColor = Theme.Bg,
                Location = UiPoint(0, 0),
                Size = UiSize(WindowWidth - 2, 36),
                Padding = UiPadding(12, 8, 12, 0)
            };

            // 右侧操作区优先 Dock Right，永不被昵称挤压
            FlowLayoutPanel rightBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.RightToLeft,
                BackColor = Theme.Bg,
                Padding = UiPadding(0, 2, 0, 0)
            };

            Label xb = new Label
            {
                Text = "✕",
                ForeColor = Theme.Sub,
                Font = Fonts.Sub,
                AutoSize = true,
                Cursor = Cursors.Hand,
                Margin = UiPadding(6, 1, 0, 0)
            };
            xb.Click += delegate { ToggleWindow(); };
            xb.MouseEnter += delegate { xb.ForeColor = Theme.Red; };
            xb.MouseLeave += delegate { xb.ForeColor = Theme.Sub; };
            xb.Tag = "nodrag";

            _gearDim = Renderer.RenderSettingsIcon(Theme.Sub, UiPx(16));
            _gearHot = Renderer.RenderSettingsIcon(Theme.Text, UiPx(16));
            _gearBtn = new PictureBox
            {
                Image = _gearDim,
                Size = UiSize(16, 16),
                SizeMode = PictureBoxSizeMode.CenterImage,
                BackColor = Theme.Bg,
                Cursor = Cursors.Hand,
                Margin = UiPadding(6, 2, 0, 0),
                Tag = "nodrag"
            };
            _gearBtn.Click += delegate { OpenSettings(); };
            _gearBtn.MouseEnter += delegate { _gearBtn.Image = _gearHot; };
            _gearBtn.MouseLeave += delegate { _gearBtn.Image = _gearDim; };

            _lblMode = new Label
            {
                Text = "总览",
                BackColor = Theme.Card,
                ForeColor = Theme.Sub,
                Font = Fonts.Pill,
                AutoSize = true,
                Padding = UiPadding(5, 2, 5, 2),
                Margin = UiPadding(4, 1, 0, 0),
                Cursor = Cursors.Hand,
                Visible = false,
                Tag = "nodrag"
            };
            _lblMode.Click += delegate { ToggleViewMode(); };
            _lblMode.MouseEnter += delegate { _lblMode.ForeColor = Theme.Text; _lblMode.BackColor = Theme.CardHover; };
            _lblMode.MouseLeave += delegate { _lblMode.ForeColor = IsOverviewMode() ? Theme.Accent : Theme.Sub; _lblMode.BackColor = Theme.Card; };
            Renderer.RoundControl(_lblMode, UiPx(5));

            rightBar.Controls.Add(xb);
            rightBar.Controls.Add(_gearBtn);
            rightBar.Controls.Add(_lblMode);

            // 左侧标题 + 昵称 + 徽标
            FlowLayoutPanel leftBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                BackColor = Theme.Bg,
                Padding = new Padding(0)
            };

            _lblBrand = new Label
            {
                Text = "Codex",
                ForeColor = Theme.Text,
                Font = Fonts.Title,
                AutoSize = true,
                Margin = UiPadding(0, 0, 4, 0)
            };

            _lblNick = new Label
            {
                Text = "",
                ForeColor = Theme.Accent,
                Font = Fonts.Sub,
                AutoSize = true,
                Cursor = Cursors.Hand,
                Margin = UiPadding(2, 3, 4, 0),
                Tag = "nodrag"
            };
            _lblNick.Click += delegate { OpenSwitcher(); };
            _lblNick.MouseEnter += delegate { _lblNick.ForeColor = ColorTranslator.FromHtml("#a8d0ff"); };
            _lblNick.MouseLeave += delegate { _lblNick.ForeColor = Theme.Accent; };

            _lblPlan = new Label
            {
                Text = "",
                BackColor = Theme.Card,
                ForeColor = Theme.Sub,
                Font = Fonts.Pill,
                AutoSize = true,
                Padding = UiPadding(5, 1, 5, 1),
                Margin = UiPadding(2, 3, 2, 0),
                Visible = false,
                Tag = "nodrag"
            };

            _lblCredits = new Label
            {
                Text = "",
                BackColor = Theme.Card,
                ForeColor = Theme.Accent,
                Font = Fonts.Pill,
                AutoSize = true,
                Padding = UiPadding(4, 1, 4, 1),
                Margin = UiPadding(2, 3, 0, 0),
                Visible = false,
                Tag = "nodrag"
            };

            Renderer.RoundControl(_lblPlan, UiPx(5));
            Renderer.RoundControl(_lblCredits, UiPx(5));
            leftBar.Controls.Add(_lblBrand);
            leftBar.Controls.Add(_lblNick);
            leftBar.Controls.Add(_lblPlan);
            leftBar.Controls.Add(_lblCredits);

            bar.Controls.Add(leftBar);
            bar.Controls.Add(rightBar);
            _inner.Controls.Add(bar);

            // ---- 内容区 ----
            _cardsHost = new Panel
            {
                BackColor = Theme.Bg,
                Location = UiPoint(0, 36),
                Width = UiPx(WindowWidth - 2)
            };
            _inner.Controls.Add(_cardsHost);

            // ---- 分隔线 + 底部栏 ----
            Panel sep = new Panel { Name = "sep", BackColor = Theme.Border, Height = Math.Max(1, DpiPx(1)), Width = UiPx(WindowWidth - 30), Left = UiPx(14) };
            _inner.Controls.Add(sep);

            Panel foot = new Panel { Name = "foot", BackColor = Theme.Bg, Height = UiPx(26), Width = UiPx(WindowWidth - 2), Padding = UiPadding(14, 4, 14, 4) };
            _lblFoot = new Label
            {
                Text = "",
                ForeColor = Theme.Sub,
                Font = Fonts.Tiny,
                AutoSize = true,
                Dock = DockStyle.Left,
                Tag = "nodrag"
            };
            _lblFoot.Click += delegate
            {
                if (_footClickable && _footIsRestart)
                {
                    string rmsg;
                    PromptRestartCodex(this, out rmsg);
                }
                else if (_footClickable && ViewAccount != null)
                {
                    string msg;
                    bool ok = LaunchRelogin(ViewAccount, out msg);
                    _lblFoot.Text = ok ? msg : ("⚠ " + msg);
                    _lblFoot.ForeColor = ok ? Theme.Accent : Theme.Red;
                }
            };
            _lblFoot.MouseEnter += delegate
            {
                if (!_footClickable) return;
                _lblFoot.ForeColor = _footIsRestart ? ColorTranslator.FromHtml("#ffe07d") : ColorTranslator.FromHtml("#ff8585");
            };
            _lblFoot.MouseLeave += delegate
            {
                if (!_footClickable) return;
                _lblFoot.ForeColor = _footIsRestart ? Theme.Yellow : Theme.Red;
            };

            _lblHint = new Label
            {
                Text = string.Format("每 {0}s", Config.interval),
                ForeColor = Theme.Muted,
                Font = Fonts.Tiny,
                AutoSize = true,
                Dock = DockStyle.Right,
                Cursor = Cursors.Hand,
                Tag = "nodrag"
            };
            _lblHint.Click += delegate { ManualRefresh(); };
            _lblHint.MouseEnter += delegate { _lblHint.ForeColor = Theme.Text; };
            _lblHint.MouseLeave += delegate { _lblHint.ForeColor = Theme.Muted; };

            foot.Controls.Add(_lblFoot);
            foot.Controls.Add(_lblHint);
            _inner.Controls.Add(foot);

            // 订阅时间正下方: 重置卡 2 天内即将过期提示行 (平时隐藏)
            _lblCreditWarn = new Label
            {
                Name = "creditWarn",
                Text = "",
                BackColor = Theme.Bg,
                ForeColor = Theme.Red,
                Font = Fonts.Tiny,
                AutoSize = false,
                Size = UiSize(WindowWidth - 30, 18),
                Left = UiPx(14),
                Visible = false,
                Tag = "nodrag"
            };
            _inner.Controls.Add(_lblCreditWarn);

            BindInteractions(this);
        }

        private void SetBadges(string planText, int? credits)
        {
            if (!string.IsNullOrEmpty(planText))
            {
                _lblPlan.Text = planText;
                _lblPlan.Visible = true;
            }
            else
            {
                _lblPlan.Text = "";
                _lblPlan.Visible = false;
            }

            if (credits.HasValue && credits.Value > 0)
            {
                _lblCredits.Text = "⚡" + credits.Value;
                _lblCredits.Visible = true;
            }
            else
            {
                _lblCredits.Text = "";
                _lblCredits.Visible = false;
            }
        }

        private void RenderAccount()
        {
            _cardsHost.SuspendLayout();
            while (_cardsHost.Controls.Count > 0)
                _cardsHost.Controls[0].Dispose();
            _ringP = null;
            _ringS = null;
            _lblTimeP = null;
            _lblTimeS = null;
            _btnCardCredits = null;
            _lblCardPlan = null;
            _agyBucketsPanel = null;
            _lblAgySource = null;
            _lblAgyPlan = null;
            _lastAgyRenderKey = "";
            _overviewCards.Clear();

            if (Accounts.Count > 1)
            {
                bool isAll = IsOverviewMode();
                _lblMode.Text = isAll ? "详情" : "总览";
                _lblMode.ForeColor = isAll ? Theme.Accent : Theme.Sub;
                _lblMode.Visible = true;
            }
            else
            {
                _lblMode.Visible = false;
            }

            if (ViewAccount == null)
            {
                Label empty = new Label
                {
                    Text = "未检测到 Codex 登录\r\n请先执行 codex login",
                    ForeColor = Theme.Sub,
                    Font = Fonts.Sub,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Size = UiSize(WindowWidth - 2, 92),
                    Location = UiPoint(0, 8)
                };
                _cardsHost.Controls.Add(empty);
                _cardsHost.Height = UiPx(104);
                _lblNick.Text = "未登录";
                _lblNick.Cursor = Cursors.Default;
                SetBadges("", null);
                _cardsHost.ResumeLayout(true);
                BindInteractions(_cardsHost);
                FitHeight();
                return;
            }

            if (IsOverviewMode())
            {
                if (_lblBrand != null) { _lblBrand.Text = "总览"; _lblBrand.ForeColor = Theme.Text; }
                _lblNick.Text = string.Format("{0} 个账号", Accounts.Count);
                _lblNick.Cursor = Cursors.Hand;
                SetBadges("", null);

                int y = 6;
                int cardW = WindowWidth - 26;
                foreach (AccountInfo a in Accounts)
                {
                    AccountInfo acct = a;
                    bool isAgy = acct.Provider == ProviderKind.Antigravity;
                    Color borderCol = acct.Live ? Theme.Accent : (isAgy ? ColorTranslator.FromHtml("#4b3e6e") : Theme.Border);
                    Panel cardWrap = new Panel
                    {
                        BackColor = borderCol,
                        Location = UiPoint(12, y),
                        Size = UiSize(cardW, 74)
                    };
                    Panel card = new Panel
                    {
                        BackColor = Theme.Card,
                        Location = UiPoint(1, 1),
                        Size = UiSize(cardW - 2, 72)
                    };
                    Renderer.RoundControl(cardWrap, UiPx(9));
                    Renderer.RoundControl(card, UiPx(8));
                    cardWrap.Controls.Add(card);

                    // 顶部行：右侧先 Dock 操作按钮，左侧平台徽标 + 昵称 + 套餐
                    Panel top = new Panel { Dock = DockStyle.Top, Height = UiPx(24), BackColor = Theme.Card, Padding = UiPadding(8, 5, 8, 0) };
                    if (isAgy)
                    {
                        if (acct.IsAgyLocal)
                        {
                            top.Controls.Add(new Label { Text = "● 本机自动", ForeColor = ColorTranslator.FromHtml("#b39ddb"), Font = Fonts.Pill, AutoSize = true, Dock = DockStyle.Right, Padding = UiPadding(4, 2, 0, 0) });
                        }
                        else if (acct.AuthBad)
                        {
                            Label btn = new Label { Text = "重新登录", BackColor = ColorTranslator.FromHtml("#3a2024"), ForeColor = Theme.Red, Font = Fonts.Pill, AutoSize = true, Dock = DockStyle.Right, Padding = UiPadding(5, 2, 5, 2), Cursor = Cursors.Hand, Tag = "nodrag" };
                            btn.Click += delegate { string m; LaunchRelogin(acct, out m); };
                            Renderer.RoundControl(btn, UiPx(5));
                            top.Controls.Add(btn);
                        }
                        else
                        {
                            top.Controls.Add(new Label { Text = "☁ 云端", ForeColor = Theme.Accent, Font = Fonts.Pill, AutoSize = true, Dock = DockStyle.Right, Padding = UiPadding(4, 2, 0, 0) });
                        }
                    }
                    else if (acct.Live)
                    {
                        top.Controls.Add(new Label { Text = "● CLI当前", ForeColor = Theme.Green, Font = Fonts.Pill, AutoSize = true, Dock = DockStyle.Right, Padding = UiPadding(4, 2, 0, 0) });
                    }
                    else if (acct.AuthBad)
                    {
                        Label btn = new Label { Text = "重新登录", BackColor = ColorTranslator.FromHtml("#3a2024"), ForeColor = Theme.Red, Font = Fonts.Pill, AutoSize = true, Dock = DockStyle.Right, Padding = UiPadding(5, 2, 5, 2), Cursor = Cursors.Hand, Tag = "nodrag" };
                        btn.Click += delegate { string m; LaunchRelogin(acct, out m); };
                        Renderer.RoundControl(btn, UiPx(5));
                        top.Controls.Add(btn);
                    }
                    else
                    {
                        Label btn = new Label { Text = "切为CLI", BackColor = ColorTranslator.FromHtml("#2a2d3a"), ForeColor = Theme.Accent, Font = Fonts.Pill, AutoSize = true, Dock = DockStyle.Right, Padding = UiPadding(5, 2, 5, 2), Cursor = Cursors.Hand, Tag = "nodrag" };
                        btn.MouseEnter += delegate { btn.BackColor = Theme.Accent; btn.ForeColor = ColorTranslator.FromHtml("#12141a"); };
                        btn.MouseLeave += delegate { btn.BackColor = ColorTranslator.FromHtml("#2a2d3a"); btn.ForeColor = Theme.Accent; };
                        btn.Click += delegate { string m; SwitchCliByUid(acct.Uid, out m); };
                        Renderer.RoundControl(btn, UiPx(5));
                        top.Controls.Add(btn);
                    }

                    FlowLayoutPanel leftInfo = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Card, WrapContents = false };
                    Label provLbl = new Label
                    {
                        Text = acct.ProviderTag,
                        BackColor = isAgy ? ColorTranslator.FromHtml("#3b2d59") : ColorTranslator.FromHtml("#223854"),
                        ForeColor = isAgy ? ColorTranslator.FromHtml("#d2beff") : Theme.Accent,
                        Font = Fonts.Pill,
                        AutoSize = true,
                        Padding = UiPadding(3, 1, 3, 1),
                        Margin = UiPadding(0, 1, 4, 0)
                    };
                    Renderer.RoundControl(provLbl, UiPx(4));
                    Label nameLbl = new Label { Text = FormatUtil.Ellipsize(acct.DisplayName, 13), ForeColor = Theme.Text, Font = Fonts.Name, AutoSize = true, Cursor = Cursors.Hand, Tag = "nodrag" };
                    nameLbl.Click += delegate { SetDisplayUid(acct.Uid, true); };
                    nameLbl.MouseEnter += delegate { nameLbl.ForeColor = Theme.Accent; };
                    nameLbl.MouseLeave += delegate { nameLbl.ForeColor = Theme.Text; };
                    Label planLbl = new Label { Text = "", BackColor = ColorTranslator.FromHtml("#2b2d37"), ForeColor = Theme.Sub, Font = Fonts.Pill, AutoSize = true, Padding = UiPadding(4, 1, 4, 1), Margin = UiPadding(4, 1, 0, 0), Visible = false };
                    Renderer.RoundControl(planLbl, UiPx(5));
                    leftInfo.Controls.Add(provLbl);
                    leftInfo.Controls.Add(nameLbl);
                    leftInfo.Controls.Add(planLbl);
                    top.Controls.Add(leftInfo);
                    card.Controls.Add(top);

                    if (isAgy)
                    {
                        Panel agyHost = new Panel
                        {
                            BackColor = Theme.Card,
                            Location = UiPoint(0, 26),
                            Size = UiSize(cardW - 2, 42)
                        };
                        card.Controls.Add(agyHost);
                        _cardsHost.Controls.Add(cardWrap);
                        _overviewCards[acct.Home] = new OverviewWidgets
                        {
                            CardWrap = cardWrap,
                            Card = card,
                            AgyHost = agyHost,
                            LastAgyKey = "",
                            Plan = planLbl
                        };
                        y += 80;
                    }
                    else
                    {
                        // 行 1
                        Label tP = new Label { Text = "5h", ForeColor = Theme.Sub, Font = Fonts.Pill, Location = UiPoint(8, 29), Size = UiSize(30, 16) };
                        MiniBarControl barP = new MiniBarControl { Location = UiPoint(40, 33), Size = UiSize(136, 7) };
                        Label valP = new Label { Text = "--", ForeColor = Theme.Sub, Font = Fonts.Pill, Location = UiPoint(182, 29), AutoSize = true };
                        Label rstP = new Label { Text = "", ForeColor = Theme.Muted, Font = Fonts.Pill, Location = UiPoint(230, 29), Size = UiSize(cardW - 240, 16), TextAlign = ContentAlignment.TopRight };

                        // 行 2
                        Label tS = new Label { Text = "周", ForeColor = Theme.Sub, Font = Fonts.Pill, Location = UiPoint(8, 49), Size = UiSize(32, 16) };
                        MiniBarControl barS = new MiniBarControl { Location = UiPoint(40, 53), Size = UiSize(136, 7) };
                        Label valS = new Label { Text = "--", ForeColor = Theme.Sub, Font = Fonts.Pill, Location = UiPoint(182, 49), AutoSize = true };
                        Label rstS = new Label { Text = "", ForeColor = Theme.Muted, Font = Fonts.Pill, Location = UiPoint(230, 49), Size = UiSize(cardW - 240, 16), TextAlign = ContentAlignment.TopRight };

                        card.Controls.Add(tP); card.Controls.Add(barP); card.Controls.Add(valP); card.Controls.Add(rstP);
                        card.Controls.Add(tS); card.Controls.Add(barS); card.Controls.Add(valS); card.Controls.Add(rstS);

                        _cardsHost.Controls.Add(cardWrap);
                        _overviewCards[acct.Home] = new OverviewWidgets
                        {
                            CardWrap = cardWrap,
                            Card = card,
                            Plan = planLbl,
                            TagP = tP, BarP = barP, ValP = valP, RstP = rstP,
                            TagS = tS, BarS = barS, ValS = valS, RstS = rstS
                        };
                        y += 80;
                    }
                }
                _cardsHost.Height = UiPx(y + 2);
            }
            else if (ViewAccount.Provider == ProviderKind.Antigravity)
            {
                if (_lblBrand != null) { _lblBrand.Text = "AGY"; _lblBrand.ForeColor = ColorTranslator.FromHtml("#c5b3ff"); }
                string nick = FormatUtil.Ellipsize(ViewAccount.DisplayName, 17);
                if (Accounts.Count > 1) nick += " ▾";
                _lblNick.Text = nick;
                _lblNick.Cursor = Accounts.Count > 1 ? Cursors.Hand : Cursors.Default;
                SetBadges("", null);

                _agyBucketsPanel = new Panel
                {
                    BackColor = Theme.Bg,
                    Location = UiPoint(12, 4),
                    Size = UiSize(WindowWidth - 26, 86)
                };
                _cardsHost.Controls.Add(_agyBucketsPanel);

                Panel metaRow = new Panel
                {
                    Name = "agyMetaRow",
                    BackColor = Theme.Bg,
                    Location = UiPoint(14, 96),
                    Size = UiSize(WindowWidth - 30, 26)
                };
                _lblAgySource = new Label
                {
                    Text = ViewAccount.IsAgyLocal ? "● 本机自动读取" : "☁ 云端 Google 账号",
                    BackColor = Theme.Card,
                    ForeColor = ViewAccount.IsAgyLocal ? ColorTranslator.FromHtml("#c5b3ff") : Theme.Accent,
                    Font = Fonts.Pill,
                    AutoSize = true,
                    Padding = UiPadding(6, 2, 6, 2),
                    Location = UiPoint(0, 2)
                };
                _lblAgyPlan = new Label
                {
                    Text = "",
                    BackColor = Theme.Card,
                    ForeColor = Theme.Sub,
                    Font = Fonts.Pill,
                    AutoSize = true,
                    Padding = UiPadding(6, 2, 6, 2),
                    Dock = DockStyle.Right,
                    Visible = false
                };
                Renderer.RoundControl(_lblAgySource, UiPx(6));
                Renderer.RoundControl(_lblAgyPlan, UiPx(5));
                metaRow.Controls.Add(_lblAgySource);
                metaRow.Controls.Add(_lblAgyPlan);
                _cardsHost.Controls.Add(metaRow);
                _cardsHost.Height = metaRow.Bottom + UiPx(2);
            }
            else
            {
                if (_lblBrand != null) { _lblBrand.Text = "Codex"; _lblBrand.ForeColor = Theme.Text; }
                string nick = FormatUtil.Ellipsize(ViewAccount.DisplayName, 17);
                if (Accounts.Count > 1) nick += " ▾";
                _lblNick.Text = nick;
                _lblNick.Cursor = Accounts.Count > 1 ? Cursors.Hand : Cursors.Default;
                SetBadges("", null);

                int half = (WindowWidth - 2) / 2;
                int ringSize = 98;
                _ringP = new RingControl { Location = UiPoint((half - ringSize) / 2, 4), LayoutScale = WindowScale, TextScale = FontScale, Size = UiSize(ringSize, ringSize) };
                _ringS = new RingControl { Location = UiPoint(half + (half - ringSize) / 2, 4), LayoutScale = WindowScale, TextScale = FontScale, Size = UiSize(ringSize, ringSize) };
                _ringS.SetInitialPhase(2.1f);

                Label capP = new PeriodBadge(ColorTranslator.FromHtml("#234C78"))
                {
                    Text = "5h",
                    ForeColor = Theme.Text,
                    Font = Fonts.Pill,
                    Size = UiSize(28, 21),
                    Location = UiPoint(14, 6)
                };
                Label capS = new PeriodBadge(ColorTranslator.FromHtml("#573C78"))
                {
                    Text = "周",
                    ForeColor = Theme.Text,
                    Font = Fonts.Pill,
                    Size = UiSize(28, 21),
                    Location = UiPoint(half + 14, 6)
                };

                _lblTimeP = new Label { Text = "", ForeColor = Theme.Sub, Font = Fonts.Tiny, TextAlign = ContentAlignment.MiddleCenter, Location = UiPoint(2, 106), Size = UiSize(half - 4, 18) };
                _lblTimeS = new Label { Text = "", ForeColor = Theme.Sub, Font = Fonts.Tiny, TextAlign = ContentAlignment.MiddleCenter, Location = UiPoint(half + 2, 106), Size = UiSize(half - 4, 18) };

                // 重置卡张数 + 套餐徽标行 (位于双环下方、分隔线上方, 永远不被长邮箱挤压)
                Panel metaRow = new Panel
                {
                    BackColor = Theme.Bg,
                    Location = UiPoint(14, 128),
                    Size = UiSize(WindowWidth - 30, 28)
                };
                _btnCardCredits = new Button
                {
                    Text = "⚡ 重置卡 -- ›",
                    AccessibleName = "查看重置卡详情",
                    BackColor = Theme.Card,
                    ForeColor = Theme.Sub,
                    Font = Fonts.Pill,
                    FlatStyle = FlatStyle.Flat,
                    UseVisualStyleBackColor = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = UiPadding(6, 0, 6, 0),
                    Location = UiPoint(0, 1),
                    Cursor = Cursors.Hand,
                    Tag = "nodrag"
                };
                _btnCardCredits.FlatAppearance.BorderSize = 0;
                _btnCardCredits.FlatAppearance.MouseOverBackColor = Theme.CardHover;
                _btnCardCredits.Click += delegate { OpenCreditsDetail(); };
                _lblCardPlan = new Label
                {
                    Text = "",
                    BackColor = Theme.Card,
                    ForeColor = Theme.Sub,
                    Font = Fonts.Pill,
                    AutoSize = true,
                    Padding = UiPadding(6, 2, 6, 2),
                    Dock = DockStyle.Right,
                    Visible = false
                };
                Renderer.RoundControl(_btnCardCredits, UiPx(7));
                Renderer.RoundControl(_lblCardPlan, UiPx(5));
                metaRow.Controls.Add(_btnCardCredits);
                metaRow.Controls.Add(_lblCardPlan);

                _cardsHost.Controls.Add(_ringP);
                _cardsHost.Controls.Add(_ringS);
                _cardsHost.Controls.Add(capP);
                _cardsHost.Controls.Add(capS);
                _cardsHost.Controls.Add(_lblTimeP);
                _cardsHost.Controls.Add(_lblTimeS);
                _cardsHost.Controls.Add(metaRow);
                _cardsHost.Height = metaRow.Bottom + UiPx(2);
            }

            _cardsHost.ResumeLayout(true);
            BindInteractions(_cardsHost);
            FitHeight();
        }

        private void FitHeight()
        {
            Control sep = _inner.Controls["sep"];
            Control foot = _inner.Controls["foot"];
            int y = _cardsHost.Bottom + UiPx(4);
            sep.Top = y;
            foot.Top = y + UiPx(2);
            int bottom = foot.Bottom;
            if (_lblCreditWarn != null && _lblCreditWarn.Visible)
            {
                _lblCreditWarn.Top = foot.Bottom;
                bottom = _lblCreditWarn.Bottom + UiPx(3);
            }
            int totalInnerH = bottom + UiPx(4);
            _inner.Height = totalInnerH;
            Height = totalInnerH + UiPx(2);
            ClampToScreen();
        }

        private void ClampToScreen()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int nx = Math.Min(Math.Max(area.Left, Left), Math.Max(area.Left, area.Right - Width));
            int ny = Math.Min(Math.Max(area.Top, Top), Math.Max(area.Top, area.Bottom - Height));
            if (nx != Left || ny != Top)
            {
                Location = new Point(nx, ny);
                Config.window_x = nx;
                Config.window_y = ny;
                CodexApi.SaveConfig(Config);
            }
        }

        // ---------------- 拖动与右键菜单 ----------------
        private void BindInteractions(Control root)
        {
            root.MouseUp -= OnRightClick;
            root.MouseUp += OnRightClick;
            if (!object.Equals(root.Tag, "nodrag"))
            {
                root.MouseDown -= OnDragDown;
                root.MouseMove -= OnDragMove;
                root.MouseUp -= OnDragUp;
                root.MouseDown += OnDragDown;
                root.MouseMove += OnDragMove;
                root.MouseUp += OnDragUp;
            }
            foreach (Control ch in root.Controls)
                BindInteractions(ch);
        }

        private void OnDragDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _dragStart = Control.MousePosition;
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (_dragStart.HasValue && e.Button == MouseButtons.Left)
            {
                Point cur = Control.MousePosition;
                Location = new Point(Left + cur.X - _dragStart.Value.X, Top + cur.Y - _dragStart.Value.Y);
                _dragStart = cur;
            }
        }

        private void OnDragUp(object sender, MouseEventArgs e)
        {
            if (_dragStart.HasValue)
            {
                _dragStart = null;
                Config.window_x = Left;
                Config.window_y = Top;
                CodexApi.SaveConfig(Config);
            }
        }

        private void OnRightClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
                ShowContextMenu(Control.MousePosition);
        }

        private ContextMenuStrip BuildContextMenu(bool forTray)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowImageMargin = false;
            menu.BackColor = Theme.Card;
            menu.ForeColor = Theme.Text;
            menu.Font = _smallFonts.Sub;
            Renderer.RoundControl(menu, DpiPx(8));

            ToolStripMenuItem cliHead = new ToolStripMenuItem("● 当前 CLI: " + LiveAccountName());
            cliHead.Enabled = false;
            cliHead.ForeColor = Theme.Green;
            menu.Items.Add(cliHead);
            menu.Items.Add(new ToolStripSeparator());

            if (forTray)
            {
                ToolStripMenuItem miToggle = new ToolStripMenuItem("显示/隐藏浮动窗口");
                miToggle.Click += delegate { ToggleWindow(); };
                menu.Items.Add(miToggle);
            }

            ToolStripMenuItem miRefresh = new ToolStripMenuItem("立即刷新");
            miRefresh.Click += delegate { ManualRefresh(); };
            menu.Items.Add(miRefresh);

            if (Accounts.Count > 1)
            {
                ToolStripMenuItem miMode = new ToolStripMenuItem(IsOverviewMode() ? "切换到单账号详情" : "切换到多账号总览");
                miMode.Click += delegate { ToggleViewMode(); };
                menu.Items.Add(miMode);

                ToolStripMenuItem subView = new ToolStripMenuItem("查看账号详情");
                subView.DropDown.Opened += delegate { NativeWin.SetTopMost(subView.DropDown.Handle); };
                ToolStripMenuItem subCli = new ToolStripMenuItem("切换为 CLI 账号");
                subCli.DropDown.Opened += delegate { NativeWin.SetTopMost(subCli.DropDown.Handle); };
                Renderer.RoundControl(subView.DropDown, DpiPx(8));
                Renderer.RoundControl(subCli.DropDown, DpiPx(8));
                foreach (AccountInfo a in Accounts)
                {
                    AccountInfo acct = a;
                    bool cur = ViewAccount != null && acct.Uid == ViewAccount.Uid;
                    string vSuffix = acct.AuthBad ? " (需重新登录)" : (acct.Live ? " (CLI当前)" : (acct.IsAgyLocal ? " (本机)" : ""));
                    ToolStripMenuItem iv = new ToolStripMenuItem(string.Format("{0} [{1}] {2}{3}", cur ? "✓" : "  ", acct.ProviderTag, acct.DisplayName, vSuffix));
                    iv.Click += delegate { SetDisplayUid(acct.Uid, true); };
                    subView.DropDownItems.Add(iv);

                    if (acct.Provider == ProviderKind.Codex)
                    {
                        string cSuffix = acct.Live ? " (当前)" : (acct.AuthBad ? " (需先重登)" : "");
                        ToolStripMenuItem ic = new ToolStripMenuItem(string.Format("{0} {1}{2}", acct.Live ? "●" : "  ", acct.DisplayName, cSuffix));
                        ic.Click += delegate { string m; SwitchCliByUid(acct.Uid, out m); };
                        subCli.DropDownItems.Add(ic);
                    }
                }
                menu.Items.Add(subView);
                if (subCli.DropDownItems.Count > 0)
                    menu.Items.Add(subCli);
            }

            if (ViewAccount != null && !forTray && !ViewAccount.IsAgyLocal)
            {
                ToolStripMenuItem miRelogin = new ToolStripMenuItem("重新登录当前查看账号");
                miRelogin.Click += delegate { string m; LaunchRelogin(ViewAccount, out m); };
                menu.Items.Add(miRelogin);
            }

            ToolStripMenuItem miAdd = new ToolStripMenuItem("添加 Codex 账号 (独立隔离)");
            miAdd.Click += delegate { string m; LaunchNewLogin(out m); };
            menu.Items.Add(miAdd);

            ToolStripMenuItem miAddAgy = new ToolStripMenuItem("添加 Antigravity 云端账号 (Google)");
            miAddAgy.Click += delegate { string m; LaunchAgyCloudLogin(out m); };
            menu.Items.Add(miAddAgy);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miSet = new ToolStripMenuItem("设置…");
            miSet.Click += delegate { OpenSettings(); };
            menu.Items.Add(miSet);

            ToolStripMenuItem miReset = new ToolStripMenuItem("复位窗口位置");
            miReset.Click += delegate { ResetPosition(); };
            menu.Items.Add(miReset);

            ToolStripMenuItem miTaskbar = new ToolStripMenuItem("任务栏常驻显示");
            miTaskbar.Checked = Config.show_in_taskbar;
            miTaskbar.Click += delegate { SetShowInTaskbar(!Config.show_in_taskbar); };
            menu.Items.Add(miTaskbar);

            ToolStripMenuItem miDual = new ToolStripMenuItem("任务栏双平台同屏 (CDX + AGY)");
            miDual.Checked = Config.taskbar_mode == TaskbarDisplayMode.Dual;
            miDual.Click += delegate
            {
                SetTaskbarMode(Config.taskbar_mode == TaskbarDisplayMode.Dual
                    ? TaskbarDisplayMode.FollowCurrent
                    : TaskbarDisplayMode.Dual);
            };
            menu.Items.Add(miDual);

            if (forTray)
            {
                ToolStripMenuItem miAuto = new ToolStripMenuItem("开机自启");
                miAuto.Checked = CodexApi.AutostartEnabled();
                miAuto.Click += delegate { CodexApi.SetAutostart(!CodexApi.AutostartEnabled()); };
                menu.Items.Add(miAuto);
            }
            else
            {
                ToolStripMenuItem miHide = new ToolStripMenuItem("隐藏到托盘");
                miHide.Click += delegate { ToggleWindow(); };
                menu.Items.Add(miHide);
            }

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem miQuit = new ToolStripMenuItem("退出");
            miQuit.Click += delegate { QuitApp(); };
            menu.Items.Add(miQuit);
            menu.Items.Add(new ToolStripMenuItem(Program.VersionLabel) { Enabled = false, ForeColor = Theme.Sub });
            return menu;
        }

        private void ShowContextMenu(Point screenPos)
        {
            if (_activeMenu != null)
            {
                try { _activeMenu.Close(); _activeMenu.Dispose(); } catch { }
                _activeMenu = null;
            }
            ContextMenuStrip menu = BuildContextMenu(false);
            _activeMenu = menu;
            menu.Closed += delegate
            {
                if (_activeMenu == menu) _activeMenu = null;
            };
            // 先创建并激活菜单窗口为前台置顶窗口, 确保压过普通置顶窗口且点击外部自动关闭
            menu.Show(screenPos);
            NativeWin.SetTopMost(menu.Handle);
            NativeWin.SetForegroundWindow(menu.Handle);
            menu.Focus();
        }

        // ---------------- 账号切换弹窗 ----------------
        private void OpenSwitcher()
        {
            if (Accounts.Count < 2) return;
            if (_switcherWin != null && !_switcherWin.IsDisposed)
            {
                _switcherWin.Close();
                return;
            }

            Form w = new Form();
            _switcherWin = w;
            w.AutoScaleMode = AutoScaleMode.None;
            w.AutoScroll = true;
            w.FormBorderStyle = FormBorderStyle.None;
            w.TopMost = true;
            w.ShowInTaskbar = false;
            w.Opacity = 0.98;
            w.BackColor = Theme.Border;
            w.StartPosition = FormStartPosition.Manual;
            w.KeyPreview = true;
            w.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) w.Close(); };
            w.Deactivate += delegate { try { if (!w.IsDisposed) w.Close(); } catch { } };

            Panel inner = new Panel { BackColor = Theme.Bg, Location = UiPoint(1, 1), Width = UiPx(314) };
            w.Controls.Add(inner);

            Label head = new Label { Text = "点击查看详情（实时余量）", ForeColor = Theme.Sub, Font = Fonts.Tiny, Location = UiPoint(10, 7), AutoSize = true };
            inner.Controls.Add(head);

            int y = 26;
            foreach (AccountInfo a in Accounts)
            {
                AccountInfo acct = a;
                bool cur = ViewAccount != null && acct.Uid == ViewAccount.Uid;
                Color baseBg = cur ? Theme.Card : Theme.Bg;
                Panel row = new Panel { BackColor = baseBg, Location = UiPoint(6, y), Size = UiSize(302, 26), Cursor = Cursors.Hand };

                string cliMark = acct.Live ? " ·CLI" : (acct.IsAgyLocal ? " ·本机" : "");
                Label lbl = new Label
                {
                    Text = string.Format("{0} [{1}] {2}{3}", cur ? "✓" : "○", acct.ProviderTag, FormatUtil.Ellipsize(acct.DisplayName, 16), cliMark),
                    ForeColor = cur ? Theme.Accent : Theme.Text,
                    BackColor = baseBg,
                    Font = Fonts.Sub,
                    AutoSize = true,
                    Location = UiPoint(6, 5),
                    Cursor = Cursors.Hand
                };

                FetchResult fr = null;
                lock (_lock) { _results.TryGetValue(acct.Home, out fr); }
                string tagTxt;
                Color tagFg;
                if (acct.AuthBad) { tagTxt = "需重新登录"; tagFg = Theme.Red; }
                else if (fr != null && fr.ErrorKind == "permission") { tagTxt = "云端权限受限"; tagFg = Theme.Red; }
                else if (fr != null && fr.ErrorKind == "offline") { tagTxt = "未运行"; tagFg = Theme.Muted; }
                else if (fr != null && fr.Data != null)
                {
                    if (acct.Provider == ProviderKind.Antigravity)
                    {
                        if (fr.Data.Groups != null && fr.Data.Groups.Count >= 2)
                        {
                            QuotaGroup g1 = fr.Data.Groups[0];
                            QuotaGroup g2 = fr.Data.Groups[1];
                            tagTxt = string.Format("{0} {1}% · {2} {3}%",
                                g1.ShortName ?? "Gem",
                                g1.MinRemaining.HasValue ? ((int)Math.Round(g1.MinRemaining.Value)).ToString() : "-",
                                g2.ShortName ?? "Cld",
                                g2.MinRemaining.HasValue ? ((int)Math.Round(g2.MinRemaining.Value)).ToString() : "-");
                        }
                        else if (fr.Data.MinRemaining.HasValue)
                        {
                            tagTxt = string.Format("剩 {0}%", (int)Math.Round(fr.Data.MinRemaining.Value));
                        }
                        else
                        {
                            tagTxt = "已连接";
                        }
                        tagFg = Theme.ColorFor(fr.Data.MinRemaining);
                    }
                    else
                    {
                        double? pr = fr.Data.PRemaining;
                        double? sr = fr.Data.SRemaining;
                        tagTxt = string.Format("5h {0}% · 周 {1}%",
                            pr.HasValue ? ((int)Math.Round(pr.Value)).ToString() : "-",
                            sr.HasValue ? ((int)Math.Round(sr.Value)).ToString() : "-");
                        tagFg = Theme.ColorFor(fr.Data.MinRemaining);
                    }
                }
                else { tagTxt = "加载中…"; tagFg = Theme.Muted; }

                Label tag = new Label
                {
                    Text = tagTxt,
                    ForeColor = tagFg,
                    BackColor = baseBg,
                    Font = Fonts.Tiny,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleRight,
                    Size = UiSize(116, 20),
                    Location = UiPoint(180, 3),
                    Cursor = Cursors.Hand
                };

                EventHandler pick = delegate
                {
                    w.Close();
                    SetDisplayUid(acct.Uid, true);
                };
                EventHandler enter = delegate { row.BackColor = Theme.CardHover; lbl.BackColor = Theme.CardHover; tag.BackColor = Theme.CardHover; };
                EventHandler leave = delegate { row.BackColor = baseBg; lbl.BackColor = baseBg; tag.BackColor = baseBg; };
                row.Click += pick; lbl.Click += pick; tag.Click += pick;
                row.MouseEnter += enter; lbl.MouseEnter += enter; tag.MouseEnter += enter;
                row.MouseLeave += leave; lbl.MouseLeave += leave; tag.MouseLeave += leave;

                Renderer.RoundControl(row, UiPx(6));
                row.Controls.Add(lbl);
                row.Controls.Add(tag);
                inner.Controls.Add(row);
                y += 28;
            }

            inner.Height = UiPx(y + 6);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            FitPopup(w, inner, area);
            int pad = DpiPx(8);
            int sx = Math.Min(Math.Max(area.Left + pad, Left + UiPx(10)), Math.Max(area.Left + pad, area.Right - w.Width - pad));
            int sy = Top + UiPx(38);
            if (sy + w.Height > area.Bottom - pad) sy = Math.Max(area.Top + pad, Top - w.Height - DpiPx(4));
            w.Location = new Point(sx, sy);
            w.Load += delegate { NativeWin.ApplyRoundCorners(w.Handle); };
            w.Show(this);
            w.Activate();
        }

        private void OpenCreditsDetail()
        {
            if (_creditsWin != null && !_creditsWin.IsDisposed)
            {
                _creditsWin.Activate();
                return;
            }
            if (ViewAccount == null) return;

            Form w = new Form();
            _creditsWin = w;
            w.FormBorderStyle = FormBorderStyle.None;
            w.TopMost = true;
            w.ShowInTaskbar = false;
            w.Opacity = 0.98;
            w.BackColor = Theme.Border;
            w.StartPosition = FormStartPosition.Manual;
            w.AutoScaleMode = AutoScaleMode.None;
            w.AutoScroll = true;
            w.KeyPreview = true;
            w.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) w.Close(); };
            w.Deactivate += delegate { try { if (!w.IsDisposed) w.Close(); } catch { } };

            RenderCreditsDetail(w);
            w.Load += delegate { NativeWin.ApplyRoundCorners(w.Handle); };
            w.Show(this);
            NativeWin.SetTopMost(w.Handle);
            w.Activate();
        }

        private void RenderCreditsDetail(Form w)
        {
            w.SuspendLayout();
            try
            {
                w.AutoScrollPosition = Point.Empty;
                if (w.Controls.Count > 0) w.Controls[0].Dispose();
                BuildCreditsDetail(w);
            }
            finally
            {
                w.ResumeLayout(true);
            }
        }

        private void BuildCreditsDetail(Form w)
        {
            FetchResult fr = null;
            lock (_lock) { _results.TryGetValue(ViewAccount.Home, out fr); }
            UsageData d = fr != null ? fr.Data : null;

            Panel inner = new Panel { BackColor = Theme.Bg, Location = UiPoint(1, 1), Width = UiPx(302) };
            w.Controls.Add(inner);

            // 标题栏
            Label title = new Label
            {
                Text = "⚡ 重置卡详情",
                ForeColor = Theme.Accent,
                Font = Fonts.Name,
                AutoSize = true,
                Location = UiPoint(12, 9)
            };
            Label closeBtn = new Label
            {
                Text = "✕",
                ForeColor = Theme.Sub,
                Font = Fonts.Sub,
                Size = UiSize(20, 20),
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(inner.Width - UiPx(28), UiPx(7)),
                Cursor = Cursors.Hand
            };
            closeBtn.Click += delegate { w.Close(); };
            closeBtn.MouseEnter += delegate { closeBtn.ForeColor = Theme.Red; };
            closeBtn.MouseLeave += delegate { closeBtn.ForeColor = Theme.Sub; };
            inner.Controls.Add(title);
            inner.Controls.Add(closeBtn);
            inner.Controls.Add(new Panel { BackColor = Theme.Border, Location = UiPoint(12, 32), Size = new Size(inner.Width - UiPx(24), UiPx(1)) });

            int y = 40;
            Action<string, string, Color> addRow = delegate(string k, string v, Color vc)
            {
                inner.Controls.Add(new Label
                {
                    Text = k,
                    ForeColor = Theme.Sub,
                    Font = Fonts.Tiny,
                    AutoSize = true,
                    Location = UiPoint(12, y)
                });
                inner.Controls.Add(new Label
                {
                    Text = v,
                    ForeColor = vc,
                    Font = Fonts.Sub,
                    AutoSize = false,
                    TextAlign = ContentAlignment.MiddleRight,
                    Size = UiSize(182, 18),
                    Location = new Point(inner.Width - UiPx(194), UiPx(y - 1))
                });
                y += 23;
            };

            addRow("所属账号", FormatUtil.Ellipsize(ViewAccount.DisplayName, 22), Theme.Text);
            if (d != null)
            {
                string avail = d.Credits.HasValue ? d.Credits.Value + " 张" : "接口未返回";
                string applicable = d.ApplicableCredits.HasValue ? d.ApplicableCredits.Value + " 张" : "接口未返回";
                if (d.ApplicableCredits == 0)
                    applicable += d.LimitReached ? "（当前不可用）" : "（未触及限流）";
                addRow("可用重置卡", avail, d.Credits > 0 ? Theme.Accent : Theme.Sub);
                addRow("当前可立即使用", applicable, d.ApplicableCredits > 0 ? Theme.Green : Theme.Sub);

                if (d.ResetCredits != null && d.ResetCredits.Count > 0)
                {
                    AddResetCreditRows(inner, d.ResetCredits, ref y);
                }
                else if (d.Credits == 0 || (d.ResetCredits != null && !d.Credits.HasValue))
                {
                    addRow("重置卡明细", "暂无可用重置卡", Theme.Muted);
                }
                else if (d.CreditsExpireAt.HasValue)
                {
                    double leftSec = d.CreditsExpireAt.Value - FormatUtil.NowUnix();
                    string expiresAt = FormatUtil.FromUnixLocal(d.CreditsExpireAt.Value).ToString("yyyy-MM-dd HH:mm");
                    Color expiresColor = leftSec <= 86400.0 ? Theme.Red : (leftSec <= 2.0 * 86400.0 ? Theme.Yellow : Theme.Text);
                    string status = "已过期";
                    if (leftSec > 0)
                        status = leftSec < 60 ? "剩余不到 1 分钟" : "剩余 " + FormatUtil.FmtDurationCompact(leftSec);
                    addRow("过期时间（本地）", expiresAt, expiresColor);
                    addRow("到期状态", status, expiresColor);
                }
                else
                {
                    addRow("过期时间", "暂未取得重置卡明细", Theme.Muted);
                }

                string balTxt = d.CreditsUnlimited ? "无限" : (string.IsNullOrEmpty(d.CreditsBalance) ? "0" : d.CreditsBalance);
                addRow("额外额度余额", balTxt, Theme.Sub);
            }
            else
            {
                addRow("状态", "暂无数据，请稍后刷新", Theme.Muted);
            }

            inner.Controls.Add(new Panel { BackColor = Theme.Border, Location = UiPoint(12, y + 2), Size = new Size(inner.Width - UiPx(24), UiPx(1)) });
            y += 10;

            inner.Controls.Add(new Label
            {
                Text = "说明：触及 5h/每周速率限制时，可使用重置卡立即重置当前窗口额度。",
                ForeColor = Theme.Muted,
                Font = Fonts.Tiny,
                AutoSize = false,
                Size = new Size(inner.Width - UiPx(24), UiPx(34)),
                Location = UiPoint(12, y)
            });
            y += 38;

            Label linkBtn = new Label
            {
                Text = "在浏览器打开 Codex 用量页 ↗",
                BackColor = Theme.Card,
                ForeColor = Theme.Accent,
                Font = Fonts.Tiny,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(inner.Width - UiPx(24), UiPx(26)),
                Location = UiPoint(12, y),
                Cursor = Cursors.Hand
            };
            linkBtn.MouseEnter += delegate { linkBtn.BackColor = Theme.Accent; linkBtn.ForeColor = ColorTranslator.FromHtml("#12141a"); };
            linkBtn.MouseLeave += delegate { linkBtn.BackColor = Theme.Card; linkBtn.ForeColor = Theme.Accent; };
            linkBtn.Click += delegate
            {
                try { Process.Start("https://chatgpt.com/codex/settings/usage"); } catch { }
                w.Close();
            };
            Renderer.RoundControl(linkBtn, UiPx(7));
            inner.Controls.Add(linkBtn);
            y += 36;

            inner.Height = UiPx(y);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            FitPopup(w, inner, area);
            int pad = DpiPx(8);
            int sx = Math.Min(Math.Max(area.Left + pad, Left + UiPx(12)), Math.Max(area.Left + pad, area.Right - w.Width - pad));
            int sy = Bottom + UiPx(6);
            if (sy + w.Height > area.Bottom - pad)
                sy = Math.Max(area.Top + pad, Top - w.Height - UiPx(6));
            w.Location = new Point(sx, sy);
        }

        private void AddResetCreditRows(Panel inner, List<ResetCredit> cards, ref int y)
        {
            const int rowHeight = 42;
            const int visibleRows = 4;
            int listWidth = inner.Width - UiPx(24);
            int rowsShown = Math.Min(cards.Count, visibleRows);
            Panel list = new Panel
            {
                AutoScroll = true,
                Location = UiPoint(12, y),
                Size = new Size(listWidth, UiPx(rowsShown * rowHeight))
            };
            int rowWidth = listWidth - (cards.Count > visibleRows ? SystemInformation.VerticalScrollBarWidth : 0);
            double now = FormatUtil.NowUnix();
            for (int i = 0; i < cards.Count; i++)
            {
                ResetCredit card = cards[i];
                string title = string.IsNullOrWhiteSpace(card.Title) ? "重置卡" : card.Title.Replace('\r', ' ').Replace('\n', ' ');
                string expiresText = "接口未返回过期时间";
                Color expiresColor = Theme.Muted;
                if (card.ExpiresAt.HasValue)
                {
                    double remaining = card.ExpiresAt.Value - now;
                    expiresText = FormatUtil.FromUnixLocal(card.ExpiresAt.Value).ToString("yyyy-MM-dd HH:mm") + " 到期";
                    if (remaining <= 0) expiresText += "（已过期）";
                    expiresColor = remaining <= 86400.0 ? Theme.Red : (remaining <= 172800.0 ? Theme.Yellow : Theme.Sub);
                }

                list.Controls.Add(new Label
                {
                    Text = title,
                    Font = Fonts.Sub,
                    ForeColor = Theme.Text,
                    UseMnemonic = false,
                    AutoEllipsis = true,
                    Location = UiPoint(0, i * rowHeight),
                    Size = new Size(rowWidth, UiPx(18))
                });
                list.Controls.Add(new Label
                {
                    Text = expiresText,
                    Font = Fonts.Tiny,
                    ForeColor = expiresColor,
                    Location = UiPoint(0, i * rowHeight + 19),
                    Size = new Size(rowWidth, UiPx(18))
                });
            }
            inner.Controls.Add(list);
            y += rowsShown * rowHeight + 4;
        }

        internal void FitPopup(Form popup, Control content, Rectangle workingArea)
        {
            int margin = DpiPx(16);
            int maxHeight = Math.Max(DpiPx(120), workingArea.Height - margin);
            int maxWidth = Math.Max(DpiPx(160), workingArea.Width - margin);
            int height = content.Height + UiPx(2);
            int width = content.Width + UiPx(2);
            if (height > maxHeight) width += SystemInformation.VerticalScrollBarWidth;
            if (width > maxWidth) height += SystemInformation.HorizontalScrollBarHeight;
            popup.Size = new Size(Math.Min(width, maxWidth), Math.Min(height, maxHeight));
        }

        private void OpenSettings()
        {
            if (_settingsWin != null && !_settingsWin.IsDisposed)
            {
                _settingsWin.RenderAccounts();
                _settingsWin.Refit();
                _settingsWin.Activate();
                return;
            }
            _settingsWin = new SettingsForm(this);
            _settingsWin.Show(this);
        }

        // ---------------- 托盘 ----------------
        private void InitTray()
        {
            _tray = new NotifyIcon();
            _currentTrayIcon = Renderer.RenderTrayIcon(null, null);
            _tray.Icon = _currentTrayIcon;
            _tray.Text = "Codex 用量 - 加载中...";
            _tray.Visible = true;
            _trayBalloonTimer = new System.Windows.Forms.Timer { Interval = 10000 };
            _trayBalloonTimer.Tick += delegate
            {
                _trayBalloonTimer.Stop();
                UpdateTrayVisibility();
            };
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleWindow();
                else if (e.Button == MouseButtons.Right)
                {
                    ContextMenuStrip m = BuildContextMenu(true);
                    _tray.ContextMenuStrip = m;
                    // 触发托盘菜单弹出
                    try
                    {
                        System.Reflection.MethodInfo mi = typeof(NotifyIcon).GetMethod("ShowContextMenu",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        if (mi != null) mi.Invoke(_tray, null);
                    }
                    catch { m.Show(Control.MousePosition); }
                }
            };
        }

        private void UpdateTrayVisibility()
        {
            if (!_stop.IsSet)
                _tray.Visible = _trayBalloonTimer.Enabled || _taskbarWidget == null || !_taskbarWidget.Visible;
        }

        private void ShowTrayBalloon(string title, string message, ToolTipIcon icon)
        {
            // Windows 的气泡需要已注册的通知区域图标。
            _trayBalloonTimer.Stop();
            _trayBalloonTimer.Start();
            UpdateTrayVisibility();
            _tray.ShowBalloonTip(5000, title, message, icon);
        }

        private void ToggleWindow()
        {
            if (Visible) Hide();
            else { Show(); Activate(); }
        }

        private void QuitApp()
        {
            _stop.Set();
            _wake.Set();
            try
            {
                if (_showEvent != null) { _showEvent.Set(); _showEvent.Dispose(); _showEvent = null; }
                if (_waveTimer != null) { _waveTimer.Stop(); _waveTimer.Dispose(); }
                if (_restartTimer != null) { _restartTimer.Stop(); _restartTimer.Dispose(); }
                if (_trayBalloonTimer != null) _trayBalloonTimer.Dispose();
                if (_taskbarWidget != null && !_taskbarWidget.IsDisposed) _taskbarWidget.Dispose();
                if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
                if (_currentTrayIcon != null) _currentTrayIcon.Dispose();
            }
            catch { }
            Application.Exit();
        }

        // ---------------- 状态刷新 ----------------
        private void CheckLowQuotaNotify(AccountInfo acct, UsageData d)
        {
            if (acct == null || d == null || !Config.notify_low) return;
            int threshold = Config.notify_threshold;
            if (threshold <= 0) return;

            if (acct.Provider == ProviderKind.Antigravity && d.Buckets != null)
            {
                foreach (QuotaBucket b in d.Buckets)
                {
                    if (!b.RemainingPct.HasValue) continue;
                    double used = 100.0 - b.RemainingPct.Value;
                    CheckSingleWindowNotify(acct, "agy_" + b.Id, b.Label, used, b.ResetAt, b.ResetLeft, threshold);
                }
                return;
            }
            CheckSingleWindowNotify(acct, "p", "5小时额度", d.PUsed, d.PResetAt, d.PLeft, threshold);
            CheckSingleWindowNotify(acct, "s", "每周额度", d.SUsed, d.SResetAt, d.SLeft, threshold);
        }

        private void CheckSingleWindowNotify(AccountInfo acct, string key, string label, double? used, double? resetAt, double? left, int threshold)
        {
            if (!used.HasValue) return;
            int rem = (int)Math.Round(100.0 - used.Value);
            string nk = acct.Home + "|" + key;
            bool shouldNotify = false;
            lock (_lock)
            {
                if (rem <= threshold)
                {
                    double? prev;
                    if (!_notifiedLow.TryGetValue(nk, out prev) || prev != resetAt)
                    {
                        _notifiedLow[nk] = resetAt;
                        shouldNotify = true;
                    }
                }
                else if (rem >= threshold + 5)
                {
                    _notifiedLow.Remove(nk);
                }
            }
            if (shouldNotify)
            {
                string title = acct.Provider == ProviderKind.Antigravity ? "Antigravity 额度偏低" : "Codex 额度偏低";
                string msg = string.Format("{0} {1}仅剩 {2}%{3}",
                    acct.DisplayName, label, rem,
                    left.HasValue ? ("（" + FormatUtil.FmtDuration(left) + "后重置）") : "");
                RunUi(delegate
                {
                    try
                    {
                        ShowTrayBalloon(title, msg, ToolTipIcon.Warning);
                        Logger.Log("已发送低额度通知: " + msg);
                    }
                    catch { }
                });
            }
        }

        private void ApplyState()
        {
            Dictionary<string, FetchResult> snap;
            lock (_lock) { snap = new Dictionary<string, FetchResult>(_results, StringComparer.OrdinalIgnoreCase); }

            FetchResult curFr = null;
            if (ViewAccount != null) snap.TryGetValue(ViewAccount.Home, out curFr);
            UsageData d = curFr != null ? curFr.Data : null;
            string err = curFr != null ? curFr.Error : null;
            string errKind = curFr != null ? curFr.ErrorKind : null;

            _footClickable = false;
            _footIsRestart = false;
            _lblFoot.Cursor = Cursors.Default;

            if (IsOverviewMode() && _overviewCards.Count > 0)
            {
                if (_lblCreditWarn != null && _lblCreditWarn.Visible)
                {
                    _lblCreditWarn.Visible = false;
                    FitHeight();
                }
                double latestTs = 0;
                bool overviewRelayout = false;
                foreach (AccountInfo a in Accounts)
                {
                    OverviewWidgets w;
                    if (!_overviewCards.TryGetValue(a.Home, out w)) continue;
                    FetchResult afr;
                    snap.TryGetValue(a.Home, out afr);
                    UsageData ad = afr != null ? afr.Data : null;
                    string aerr = afr != null ? afr.Error : null;
                    string akind = afr != null ? afr.ErrorKind : null;

                    if (a.Provider == ProviderKind.Antigravity && w.AgyHost != null)
                    {
                        if (ad != null && ad.Ts > latestTs) latestTs = ad.Ts;
                        string planTxt = ad != null ? (ad.Plan ?? "").ToUpperInvariant() : "";
                        w.Plan.Text = planTxt;
                        w.Plan.Visible = !string.IsNullOrEmpty(planTxt);
                        if (RenderOverviewAgyCard(w, a, ad, aerr, akind))
                            overviewRelayout = true;
                        continue;
                    }

                    if (ad != null)
                    {
                        if (ad.Ts > latestTs) latestTs = ad.Ts;
                        string planTxt = (ad.Plan ?? "").ToUpperInvariant();
                        w.Plan.Text = planTxt;
                        w.Plan.Visible = !string.IsNullOrEmpty(planTxt);
                        w.TagP.Text = "5h";
                        w.TagS.Text = "周";
                        double? pr = ad.PRemaining;
                        double? sr = ad.SRemaining;
                        Color pc = Theme.ColorFor(pr);
                        Color sc = Theme.ColorFor(sr);
                        w.BarP.SetValue(pr, pc);
                        w.BarS.SetValue(sr, sc);
                        w.ValP.Text = pr.HasValue ? ((int)Math.Round(pr.Value) + "%") : "--";
                        w.ValP.ForeColor = pc;
                        w.ValS.Text = sr.HasValue ? ((int)Math.Round(sr.Value) + "%") : "--";
                        w.ValS.ForeColor = sc;
                        w.RstP.Text = FormatUtil.FmtResetShort(ad.PResetAt);
                        w.RstS.Text = FormatUtil.FmtResetShort(ad.SResetAt);
                    }
                    else
                    {
                        w.Plan.Visible = false;
                        w.BarP.SetValue(null, Theme.Track);
                        w.BarS.SetValue(null, Theme.Track);
                        bool isBad = akind == "auth" || a.AuthBad;
                        bool isOffline = akind == "offline";
                        w.ValP.Text = isBad ? "需重新登录" : (isOffline ? "未运行" : (!string.IsNullOrEmpty(aerr) ? "获取失败" : "加载中…"));
                        w.ValP.ForeColor = isBad ? Theme.Red : Theme.Sub;
                        w.ValS.Text = "--";
                        w.ValS.ForeColor = Theme.Sub;
                        w.RstP.Text = "";
                        w.RstS.Text = "";
                    }
                }
                if (overviewRelayout)
                {
                    ReflowOverviewCards();
                }
                if (latestTs > 0)
                {
                    _lblFoot.Text = "点账号看详情 · 更新 " + FormatUtil.FromUnixLocal(latestTs).ToString("HH:mm:ss");
                    _lblFoot.ForeColor = Theme.Sub;
                }
                else
                {
                    _lblFoot.Text = "正在获取多账号数据…";
                    _lblFoot.ForeColor = Theme.Sub;
                }
            }
            else
            {
                bool showCreditWarn = false;
                if (d != null && _ringP != null)
                {
                    SetBadges("", null);
                    if (_lblCardPlan != null)
                    {
                        string pt = (d.Plan ?? "").ToUpperInvariant();
                        _lblCardPlan.Text = pt;
                        _lblCardPlan.Visible = !string.IsNullOrEmpty(pt);
                    }
                    if (_btnCardCredits != null)
                    {
                        _btnCardCredits.Text = d.Credits.HasValue ? string.Format("⚡ 重置卡 {0} 张 ›", d.Credits.Value) : "⚡ 重置卡 -- ›";
                        _btnCardCredits.ForeColor = d.Credits > 0 ? Theme.Accent : Theme.Sub;
                    }

                    // 检查是否有 2 天(48小时)内即将过期的重置卡
                    if (d.Credits.HasValue && d.Credits.Value > 0 && d.CreditsExpireAt.HasValue)
                    {
                        double remainSec = d.CreditsExpireAt.Value - FormatUtil.NowUnix();
                        if (remainSec > 0 && remainSec <= 2.0 * 86400.0)
                        {
                            showCreditWarn = true;
                            if (_lblCreditWarn != null)
                            {
                                _lblCreditWarn.Text = string.Format("⚠ 重置卡即将过期 · {0} 到期", FormatUtil.FmtResetShort(d.CreditsExpireAt));
                                _lblCreditWarn.ForeColor = remainSec <= 86400.0 ? Theme.Red : Theme.Yellow;
                            }
                        }
                    }

                    double? pr = d.PRemaining;
                    double? sr = d.SRemaining;
                    Color pc = Theme.ColorFor(pr);
                    Color sc = Theme.ColorFor(sr);

                    // 剩余时间油箱液位比例: 5h 窗口总长 18000s, 每周窗口总长 604800s
                    double? pTimePct = d.PLeft.HasValue ? (double?)Math.Max(0.0, Math.Min(100.0, d.PLeft.Value / 18000.0 * 100.0)) : null;
                    double? sTimePct = d.SLeft.HasValue ? (double?)Math.Max(0.0, Math.Min(100.0, d.SLeft.Value / 604800.0 * 100.0)) : null;
                    string pTimeTxt = FormatUtil.FmtDurationCompact(d.PLeft);
                    string sTimeTxt = FormatUtil.FmtDurationCompact(d.SLeft);

                    _ringP.SetValue(pr, pc, pTimePct, pTimeTxt);
                    _ringS.SetValue(sr, sc, sTimePct, sTimeTxt);
                    _lblTimeP.Text = FormatUtil.FmtResetShort(d.PResetAt) + " 重置";
                    _lblTimeP.ForeColor = (pr.HasValue && pr.Value < 20) ? pc : Theme.Sub;
                    _lblTimeS.Text = FormatUtil.FmtResetShort(d.SResetAt) + " 重置";
                    _lblTimeS.ForeColor = (sr.HasValue && sr.Value < 20) ? sc : Theme.Sub;
                }
                else if (_ringP != null)
                {
                    SetBadges("", null);
                    if (_lblCardPlan != null) _lblCardPlan.Visible = false;
                    if (_btnCardCredits != null)
                    {
                        _btnCardCredits.Text = "⚡ 重置卡 -- ›";
                        _btnCardCredits.ForeColor = Theme.Sub;
                    }
                    bool isBad = errKind == "auth" || (ViewAccount != null && ViewAccount.AuthBad);
                    string ph = isBad ? "-- 待重新登录" : (!string.IsNullOrEmpty(err) ? "-- 获取失败" : "加载中…");
                    _ringP.SetValue(null, Theme.Track, null, "");
                    _ringS.SetValue(null, Theme.Track, null, "");
                    _lblTimeP.Text = ph; _lblTimeP.ForeColor = Theme.Sub;
                    _lblTimeS.Text = ph; _lblTimeS.ForeColor = Theme.Sub;
                }

                else if (_agyBucketsPanel != null)
                {
                    SetBadges("", null);
                    RenderAgyBuckets(d, err, errKind);
                }

                if (_lblCreditWarn != null && _lblCreditWarn.Visible != showCreditWarn)
                {
                    _lblCreditWarn.Visible = showCreditWarn;
                    FitHeight();
                }

                if (d != null)
                {
                    string stamp = FormatUtil.FromUnixLocal(d.Ts).ToString("HH:mm:ss");
                    if (errKind == "snapshot")
                    {
                        _lblFoot.Text = FormatUtil.FmtQuotaSnapshot(d.Ts);
                        _lblFoot.ForeColor = Theme.Yellow;
                    }
                    else if (!string.IsNullOrEmpty(err))
                    {
                        _lblFoot.Text = "⚠ 刷新失败 · 上次 " + stamp;
                        _lblFoot.ForeColor = Theme.Yellow;
                    }
                    else if (d.SubUntil.HasValue)
                    {
                        double leftDays = (d.SubUntil.Value - FormatUtil.NowUnix()) / 86400.0;
                        string txt = "订阅 " + FormatUtil.FmtResetShort(d.SubUntil) + "到期";
                        if (leftDays < 1.0) txt += "(今天)";
                        _lblFoot.Text = txt + " · 更新 " + stamp;
                        _lblFoot.ForeColor = leftDays < 3.0 ? Theme.Red : Theme.Sub;
                    }
                    else
                    {
                        _lblFoot.Text = "更新 " + stamp;
                        _lblFoot.ForeColor = Theme.Sub;
                    }
                }
                else
                {
                    bool isBad = errKind == "auth" || (ViewAccount != null && ViewAccount.AuthBad);
                    if (isBad && ViewAccount != null)
                    {
                        _footClickable = true;
                        _lblFoot.Cursor = Cursors.Hand;
                        _lblFoot.Text = "⚠ 会话已失效 · 点此重新登录";
                        _lblFoot.ForeColor = Theme.Red;
                    }
                    else if (errKind == "offline")
                    {
                        _lblFoot.Text = "● " + (!string.IsNullOrEmpty(err) ? err : "Antigravity 未运行");
                        _lblFoot.ForeColor = Theme.Sub;
                    }
                    else
                    {
                        _lblFoot.Text = !string.IsNullOrEmpty(err) ? ("⚠ " + err) : "正在获取数据…";
                        _lblFoot.ForeColor = !string.IsNullOrEmpty(err) ? Theme.Red : Theme.Sub;
                    }
                }
            }

            int restartLeft = RestartPromptSecondsLeft;
            if (restartLeft > 0)
            {
                UpdateFootRestartPrompt(restartLeft);
            }

            _lblHint.Text = string.Format("每 {0}s", Config.interval);
            _lblHint.ForeColor = Theme.Muted;

            // 同步刷新任务栏常驻实时条 (支持双平台同屏 + 跟随当前)
            if (_taskbarWidget != null && !_taskbarWidget.IsDisposed)
            {
                AccountInfo cdxAcct = PickPlatformAccount(ProviderKind.Codex, snap);
                AccountInfo agyAcct = Config.enable_antigravity ? PickPlatformAccount(ProviderKind.Antigravity, snap) : null;
                FetchResult cdxFr = null;
                FetchResult agyFr = null;
                if (cdxAcct != null) snap.TryGetValue(cdxAcct.Home, out cdxFr);
                if (agyAcct != null) snap.TryGetValue(agyAcct.Home, out agyFr);

                _taskbarWidget.UpdateData(
                    Config.taskbar_mode,
                    ViewAccount != null ? ViewAccount.DisplayName : "", d, err, errKind,
                    cdxAcct != null ? cdxAcct.DisplayName : null,
                    cdxFr != null ? cdxFr.Data : null,
                    cdxFr != null ? cdxFr.Error : null,
                    cdxFr != null ? cdxFr.ErrorKind : (cdxAcct != null && cdxAcct.AuthBad ? "auth" : null),
                    agyAcct != null ? agyAcct.DisplayName : null,
                    agyFr != null ? agyFr.Data : null,
                    agyFr != null ? agyFr.Error : null,
                    agyFr != null ? agyFr.ErrorKind : (agyAcct != null && agyAcct.AuthBad ? "auth" : null));
            }

            // 更新托盘图标与悬停提示 (NotifyIcon.Text 上限 63 字符)
            try
            {
                double? pr = d != null ? d.PRemaining : null;
                double? sr = d != null ? d.SRemaining : null;
                Icon oldIcon = _currentTrayIcon;
                _currentTrayIcon = Renderer.RenderTrayIcon(pr, sr);
                _tray.Icon = _currentTrayIcon;
                if (oldIcon != null) oldIcon.Dispose();

                string tip = BuildTrayTooltip(snap, d, err);
                if (tip.Length > 63) tip = tip.Substring(0, 62) + "…";
                _tray.Text = tip;
            }
            catch { }
        }

        private bool RenderOverviewAgyCard(OverviewWidgets w, AccountInfo a, UsageData ad, string aerr, string akind)
        {
            StringBuilder ks = new StringBuilder();
            if (ad != null && ad.Groups != null && ad.Groups.Count > 0)
            {
                ks.Append("OK|");
                if (akind == "snapshot") ks.Append("snapshot|").Append(ad.Ts).Append("|");
                foreach (QuotaGroup g in ad.Groups)
                {
                    ks.Append(g.DisplayName).Append("[");
                    foreach (QuotaBucket b in g.Buckets)
                        ks.Append(b.ShortLabel).Append(":").Append(b.RemainingPct.HasValue ? ((int)Math.Round(b.RemainingPct.Value)).ToString() : "-").Append(":").Append(FormatUtil.FmtResetShort(b.ResetAt)).Append(";");
                    ks.Append("]");
                }
            }
            else
            {
                ks.Append("ST|").Append(akind ?? "").Append("|").Append(a.AuthBad ? "1" : "0").Append("|").Append(aerr ?? "");
            }
            string key = ks.ToString();
            if (key == w.LastAgyKey) return false;
            w.LastAgyKey = key;

            int cardW = WindowWidth - 26;
            int innerW = cardW - 2;
            Panel host = w.AgyHost;
            host.SuspendLayout();
            while (host.Controls.Count > 0)
                host.Controls[0].Dispose();

            int ry = 2;
            if (ad != null && ad.Groups != null && ad.Groups.Count > 0)
            {
                if (akind == "snapshot")
                {
                    host.Controls.Add(new Label
                    {
                        Text = FormatUtil.FmtQuotaSnapshot(ad.Ts),
                        ForeColor = Theme.Yellow,
                        Font = Fonts.Pill,
                        Location = UiPoint(8, ry),
                        Size = UiSize(innerW - 16, 16)
                    });
                    ry += 18;
                }
                for (int gi = 0; gi < ad.Groups.Count; gi++)
                {
                    QuotaGroup g = ad.Groups[gi];
                    if (gi > 0)
                    {
                        host.Controls.Add(new Panel
                        {
                            BackColor = Theme.Border,
                            Location = UiPoint(8, ry + 1),
                            Size = UiSize(innerW - 16, 1)
                        });
                        ry += 5;
                    }

                    Label grpLbl = new Label
                    {
                        Text = g.DisplayName,
                        ForeColor = ColorTranslator.FromHtml("#c5b3ff"),
                        Font = Fonts.Pill,
                        AutoSize = false,
                        AutoEllipsis = true,
                        UseMnemonic = false,
                        Location = UiPoint(8, ry),
                        Size = UiSize(innerW - 16, 15)
                    };
                    host.Controls.Add(grpLbl);
                    ry += 16;

                    foreach (QuotaBucket b in g.Buckets)
                    {
                        Color c = Theme.ColorFor(b.RemainingPct);
                        Label tLbl = new Label
                        {
                            Text = b.ShortLabel ?? "限额",
                            ForeColor = Theme.Sub,
                            Font = Fonts.Pill,
                            Location = UiPoint(8, ry),
                            Size = UiSize(30, 16)
                        };
                        MiniBarControl bar = new MiniBarControl
                        {
                            Location = UiPoint(40, ry + 4),
                            Size = UiSize(134, 7)
                        };
                        bar.SetValue(b.RemainingPct, c);
                        Label valLbl = new Label
                        {
                            Text = b.RemainingPct.HasValue ? ((int)Math.Round(b.RemainingPct.Value) + "%") : "--",
                            ForeColor = c,
                            Font = Fonts.Pill,
                            Location = UiPoint(180, ry),
                            AutoSize = true
                        };
                        Label rstLbl = new Label
                        {
                            Text = b.ResetAt.HasValue ? FormatUtil.FmtResetShort(b.ResetAt) : "",
                            ForeColor = Theme.Muted,
                            Font = Fonts.Pill,
                            Location = UiPoint(228, ry),
                            Size = UiSize(cardW - 238, 16),
                            TextAlign = ContentAlignment.TopRight
                        };
                        host.Controls.Add(tLbl);
                        host.Controls.Add(bar);
                        host.Controls.Add(valLbl);
                        host.Controls.Add(rstLbl);
                        ry += 19;
                    }
                }
                ry += 3;
            }
            else
            {
                bool isBad = akind == "auth" || a.AuthBad;
                bool isOffline = akind == "offline";
                string st = isBad ? "需重新登录" : (isOffline ? "Antigravity 未运行" : (!string.IsNullOrEmpty(aerr) ? "获取失败" : "加载中…"));
                host.Controls.Add(new Label
                {
                    Text = st,
                    ForeColor = isBad ? Theme.Red : Theme.Sub,
                    Font = Fonts.Tiny,
                    Location = UiPoint(8, 10),
                    Size = UiSize(innerW - 16, 20)
                });
                ry = 36;
            }

            host.Height = UiPx(ry);
            int newCardH = 26 + ry + 2;
            w.Card.Size = UiSize(cardW - 2, newCardH - 2);
            w.CardWrap.Size = UiSize(cardW, newCardH);
            Renderer.RoundControl(w.CardWrap, UiPx(9));
            Renderer.RoundControl(w.Card, UiPx(8));
            host.ResumeLayout(true);
            BindInteractions(host);
            return true;
        }

        private void ReflowOverviewCards()
        {
            int yPx = UiPx(6);
            int gapPx = UiPx(6);
            foreach (AccountInfo a in Accounts)
            {
                OverviewWidgets w;
                if (!_overviewCards.TryGetValue(a.Home, out w) || w.CardWrap == null) continue;
                w.CardWrap.Top = yPx;
                yPx = w.CardWrap.Bottom + gapPx;
            }
            _cardsHost.Height = yPx + UiPx(2);
            FitHeight();
        }

        private AccountInfo PickPlatformAccount(ProviderKind provider, Dictionary<string, FetchResult> snap)
        {
            if (ViewAccount != null && ViewAccount.Provider == provider)
                return ViewAccount;
            AccountInfo first = null;
            AccountInfo withData = null;
            foreach (AccountInfo a in Accounts)
            {
                if (a.Provider != provider) continue;
                if (first == null) first = a;
                if (provider == ProviderKind.Codex && a.Live) return a;
                FetchResult fr;
                if (withData == null && snap.TryGetValue(a.Home, out fr) && fr != null && fr.Data != null)
                    withData = a;
            }
            return withData ?? first;
        }

        private void RenderAgyBuckets(UsageData d, string err, string errKind)
        {
            if (_agyBucketsPanel == null || _agyBucketsPanel.IsDisposed) return;

            StringBuilder keySb = new StringBuilder();
            if (d != null)
            {
                keySb.Append("OK|").Append(d.Plan ?? "").Append("|").Append(d.CreditsBalance ?? "").Append("|");
                if (d.Buckets != null)
                {
                    foreach (QuotaBucket b in d.Buckets)
                        keySb.Append(b.Id).Append(":").Append(b.RemainingPct.HasValue ? ((int)Math.Round(b.RemainingPct.Value)).ToString() : "-").Append(":").Append(FormatUtil.FmtResetShort(b.ResetAt)).Append(";");
                }
            }
            else
            {
                keySb.Append("ERR|").Append(errKind ?? "").Append("|").Append(err ?? "");
            }

            if (_lblAgyPlan != null)
            {
                string pt = d != null ? (d.Plan ?? "").ToUpperInvariant() : "";
                _lblAgyPlan.Text = pt;
                _lblAgyPlan.Visible = !string.IsNullOrEmpty(pt);
            }
            if (_lblAgySource != null && ViewAccount != null)
            {
                _lblAgySource.Text = ViewAccount.IsAgyLocal
                    ? (errKind == "snapshot" ? "● 本机额度快照" : "● 本机自动读取")
                    : "☁ 云端 Google 账号";
            }

            string newKey = keySb.ToString();
            if (newKey == _lastAgyRenderKey) return;
            _lastAgyRenderKey = newKey;

            _agyBucketsPanel.SuspendLayout();
            while (_agyBucketsPanel.Controls.Count > 0)
                _agyBucketsPanel.Controls[0].Dispose();

            int panelW = WindowWidth - 26;
            _agyBucketsPanel.AutoScroll = false;
            int y = 2;
            if (d != null && d.Groups != null && d.Groups.Count > 0)
            {
                foreach (QuotaGroup g in d.Groups)
                {
                    int rows = Math.Max(1, g.Buckets.Count);
                    int cardH = 26 + rows * 22 + 4;
                    Panel cardWrap = new Panel
                    {
                        BackColor = Theme.Border,
                        Location = UiPoint(0, y),
                        Size = UiSize(panelW, cardH)
                    };
                    Panel card = new Panel
                    {
                        BackColor = Theme.Card,
                        Location = UiPoint(1, 1),
                        Size = UiSize(panelW - 2, cardH - 2)
                    };
                    Renderer.RoundControl(cardWrap, UiPx(8));
                    Renderer.RoundControl(card, UiPx(7));
                    cardWrap.Controls.Add(card);

                    Label grpLbl = new Label
                    {
                        Text = g.DisplayName,
                        ForeColor = Theme.Text,
                        Font = Fonts.Name,
                        AutoSize = false,
                        AutoEllipsis = true,
                        UseMnemonic = false,
                        Location = UiPoint(9, 5),
                        Size = UiSize(panelW - 20, 18)
                    };
                    card.Controls.Add(grpLbl);

                    int ry = 26;
                    foreach (QuotaBucket b in g.Buckets)
                    {
                        Color c = Theme.ColorFor(b.RemainingPct);
                        Label wLbl = new Label
                        {
                            Text = b.ShortLabel ?? "限额",
                            ForeColor = Theme.Sub,
                            Font = Fonts.Pill,
                            AutoSize = false,
                            Location = UiPoint(9, ry),
                            Size = UiSize(30, 16)
                        };
                        MiniBarControl bar = new MiniBarControl
                        {
                            Location = UiPoint(40, ry + 4),
                            Size = UiSize(128, 7)
                        };
                        bar.SetValue(b.RemainingPct, c);
                        Label valLbl = new Label
                        {
                            Text = b.RemainingPct.HasValue ? string.Format("{0}%", (int)Math.Round(b.RemainingPct.Value)) : "--",
                            ForeColor = c,
                            Font = Fonts.Pill,
                            AutoSize = false,
                            TextAlign = ContentAlignment.MiddleRight,
                            Size = UiSize(38, 16),
                            Location = UiPoint(172, ry)
                        };
                        string rstTxt = b.ResetAt.HasValue ? (FormatUtil.FmtResetShort(b.ResetAt) + " 重置") : "";
                        Label rstLbl = new Label
                        {
                            Text = rstTxt,
                            ForeColor = (b.RemainingPct.HasValue && b.RemainingPct.Value < 20) ? c : Theme.Muted,
                            Font = Fonts.Tiny,
                            AutoSize = false,
                            TextAlign = ContentAlignment.MiddleRight,
                            Size = UiSize(panelW - 220, 16),
                            Location = UiPoint(212, ry)
                        };
                        card.Controls.Add(wLbl);
                        card.Controls.Add(bar);
                        card.Controls.Add(valLbl);
                        card.Controls.Add(rstLbl);
                        ry += 22;
                    }

                    _agyBucketsPanel.Controls.Add(cardWrap);
                    y += cardH + 6;
                }
            }
            else
            {
                bool isBad = errKind == "auth" || (ViewAccount != null && ViewAccount.AuthBad);
                bool isOffline = errKind == "offline";
                string msg = isBad
                    ? "Google 授权已失效\r\n点击底栏重新登录"
                    : (isOffline
                        ? (err ?? "未检测到运行中的 Antigravity") + "\r\n打开并登录后将自动读取"
                        : (!string.IsNullOrEmpty(err) ? err : "正在获取 Antigravity 额度…"));
                Label ph = new Label
                {
                    Text = msg,
                    ForeColor = isBad ? Theme.Red : Theme.Sub,
                    Font = Fonts.Sub,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Location = UiPoint(0, 6),
                    Size = UiSize(panelW, 74)
                };
                _agyBucketsPanel.Controls.Add(ph);
                y = 84;
            }

            _agyBucketsPanel.Height = UiPx(y);
            Control metaRow = _cardsHost.Controls["agyMetaRow"];
            if (metaRow != null)
            {
                metaRow.Top = _agyBucketsPanel.Bottom + UiPx(4);
                _cardsHost.Height = metaRow.Bottom + UiPx(2);
            }
            else
            {
                _cardsHost.Height = _agyBucketsPanel.Bottom + UiPx(4);
            }
            _agyBucketsPanel.ResumeLayout(true);
            BindInteractions(_agyBucketsPanel);
            FitHeight();
        }

        private string BuildTrayTooltip(Dictionary<string, FetchResult> snap, UsageData d, string err)
        {
            if (Accounts.Count > 1)
            {
                StringBuilder sb = new StringBuilder();
                foreach (AccountInfo a in Accounts)
                {
                    FetchResult afr;
                    snap.TryGetValue(a.Home, out afr);
                    UsageData ad = afr != null ? afr.Data : null;
                    if (sb.Length > 0) sb.Append("\n");
                    string nm = FormatUtil.Ellipsize(a.DisplayName, 8);
                    if (ad != null)
                    {
                        if (a.Provider == ProviderKind.Antigravity)
                        {
                            sb.AppendFormat("[AGY]{0}: 剩{1}%",
                                nm,
                                ad.MinRemaining.HasValue ? ((int)Math.Round(ad.MinRemaining.Value)).ToString() : "-");
                            if (afr.ErrorKind == "snapshot") sb.Append(" (快照)");
                        }
                        else
                        {
                            sb.AppendFormat("{0}: 5h {1}% 周 {2}%",
                                nm,
                                ad.PRemaining.HasValue ? ((int)Math.Round(ad.PRemaining.Value)).ToString() : "-",
                                ad.SRemaining.HasValue ? ((int)Math.Round(ad.SRemaining.Value)).ToString() : "-");
                        }
                    }
                    else
                    {
                        string st = (afr != null && afr.ErrorKind == "offline") ? "离线" : (a.AuthBad ? "需重登" : "加载中");
                        sb.AppendFormat("{0}: {1}", nm, st);
                    }
                }
                return sb.ToString();
            }
            if (d != null)
            {
                return string.Format("Codex ({0})\n5h: 剩{1}% · 周: 剩{2}%",
                    (d.Plan ?? "").ToUpperInvariant(),
                    d.PRemaining.HasValue ? ((int)Math.Round(d.PRemaining.Value)).ToString() : "-",
                    d.SRemaining.HasValue ? ((int)Math.Round(d.SRemaining.Value)).ToString() : "-");
            }
            return "Codex 用量\n" + (!string.IsNullOrEmpty(err) ? err : "加载中...");
        }

        // ---------------- 后台轮询 ----------------
        private void StartShowEventWatcher()
        {
            try
            {
                _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "CodexUsageTray_ShowEvent");
                Thread wt = new Thread((ThreadStart)delegate
                {
                    while (!_stop.IsSet && _showEvent != null)
                    {
                        try
                        {
                            if (!_showEvent.WaitOne(1000)) continue;
                            if (_stop.IsSet) break;
                            RunUi(delegate
                            {
                                if (!Visible) Show();
                                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                                ClampToScreen();
                                TopMost = false;
                                TopMost = true;
                                BringToFront();
                                Activate();
                                NativeWin.SetForegroundWindow(Handle);
                                Logger.Log(string.Format("收到唤醒信号: Loc=({0},{1}) Size=({2}x{3}) Vis={4}", Left, Top, Width, Height, Visible));
                            });
                        }
                        catch { break; }
                    }
                });
                wt.IsBackground = true;
                wt.Start();
            }
            catch { }
        }

        private void KickPoll()
        {
            _wake.Set();
        }

        private bool CheckPendingLogins()
        {
            List<KeyValuePair<string, double>> list;
            lock (_lock)
            {
                if (_pendingLogins.Count == 0) return false;
                list = new List<KeyValuePair<string, double>>(_pendingLogins);
            }
            bool imported = false;
            double now = FormatUtil.NowUnix();
            foreach (KeyValuePair<string, double> kv in list)
            {
                string nick, uid;
                if (CodexApi.ImportPendingLogin(kv.Key, out nick, out uid))
                {
                    lock (_lock) { _pendingLogins.Remove(kv.Key); }
                    imported = true;
                    string addedName = nick;
                    RunUi(delegate
                    {
                        try { ShowTrayBalloon("Codex 新账号就绪", "已自动添加账号: " + addedName, ToolTipIcon.Info); }
                        catch { }
                    });
                }
                else if (now - kv.Value > 1800)
                {
                    lock (_lock) { _pendingLogins.Remove(kv.Key); }
                    try { Directory.Delete(kv.Key, true); } catch { }
                }
            }
            if (imported)
            {
                RunUi(delegate
                {
                    ReloadAccounts();
                    RenderAccount();
                    ApplyState();
                    if (_settingsWin != null && !_settingsWin.IsDisposed) { _settingsWin.RenderAccounts(); _settingsWin.Refit(); }
                });
            }
            return imported;
        }

        private void WaitForNextPoll(List<AccountInfo> accts)
        {
            int intervalMs = Math.Max(15, Config.interval) * 1000;
            Dictionary<string, long> watches = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            bool hasPending;
            lock (_lock)
            {
                hasPending = _pendingLogins.Count > 0;
                foreach (AccountInfo a in accts)
                {
                    if (a.Provider != ProviderKind.Codex) continue;
                    long mt;
                    if (_reloginWatch.TryGetValue(a.Home, out mt)) watches[a.Home] = mt;
                    else if (a.AuthBad) watches[a.Home] = CodexApi.AuthMtimeTicks(a.Home);
                }
            }

            if (watches.Count == 0 && !hasPending)
            {
                _wake.Wait(intervalMs);
                _wake.Reset();
                return;
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(intervalMs);
            while (!_stop.IsSet && DateTime.UtcNow < deadline)
            {
                int waitMs = Math.Min(2000, Math.Max(200, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                if (_wake.Wait(waitMs))
                {
                    _wake.Reset();
                    return;
                }
                if (CheckPendingLogins()) return;
                foreach (KeyValuePair<string, long> kv in watches)
                {
                    if (CodexApi.AuthMtimeTicks(kv.Key) > kv.Value)
                    {
                        Logger.Log("检测到 auth.json 已更新, 立即刷新: " + kv.Key);
                        CodexApi.ClearRefreshCooldown(kv.Key);
                        lock (_lock) { _reloginWatch.Remove(kv.Key); }
                        return;
                    }
                }
            }
        }

        private void PollLoop()
        {
            while (!_stop.IsSet)
            {
                CheckPendingLogins();
                List<AccountInfo> accts = new List<AccountInfo>(Accounts);
                AccountInfo curView = ViewAccount;
                if (curView != null)
                {
                    accts.Sort(delegate(AccountInfo a, AccountInfo b)
                    {
                        int pa = a.Uid == curView.Uid ? 0 : 1;
                        int pb = b.Uid == curView.Uid ? 0 : 1;
                        return pa.CompareTo(pb);
                    });
                }

                bool needRerender = false;
                foreach (AccountInfo acct in accts)
                {
                    if (_stop.IsSet) return;
                    if (acct.Provider == ProviderKind.Codex && curView != null && acct.Uid != curView.Uid && CodexApi.IsInCooldownUnchanged(acct.Home))
                        continue;

                    FetchResult res = new FetchResult();
                    try
                    {
                        UsageData data = acct.Provider == ProviderKind.Antigravity
                            ? AntigravityApi.FetchUsage(acct, Config)
                            : CodexApi.FetchUsage(acct.Home);
                        res.Data = data;
                        if (acct.Provider == ProviderKind.Antigravity)
                            AntigravityApi.MarkAuthState(acct.Home, true);
                        else
                            CodexApi.MarkAuthState(acct.Home, true);
                        lock (_lock) { _reloginWatch.Remove(acct.Home); }
                        if (acct.AuthBad) needRerender = true;
                        if (acct.Live && _liveAuthBad) { _liveAuthBad = false; needRerender = true; }
                        if (acct.IsAgyLocal && data != null && !string.IsNullOrEmpty(data.Nick) && string.IsNullOrEmpty(acct.Email))
                            needRerender = true;
                        CheckLowQuotaNotify(acct, data);
                    }
                    catch (OfflineException ex)
                    {
                        if (acct.IsAgyLocal) res.Data = AntigravityApi.ReadLocalQuotaSnapshot();
                        res.Error = ex.Message;
                        res.ErrorKind = res.Data != null ? "snapshot" : "offline";
                    }
                    catch (AgyCloudPermissionException ex)
                    {
                        res.Error = ex.Message;
                        res.ErrorKind = "permission";
                        if (acct.AuthBad)
                        {
                            AntigravityApi.MarkAuthState(acct.Home, true);
                            needRerender = true;
                        }
                        Logger.Log("AGY 云端额度: " + ex.Message);
                    }
                    catch (AuthException ex)
                    {
                        res.Data = null;
                        res.Error = ex.Message;
                        res.ErrorKind = "auth";
                        if (acct.Provider == ProviderKind.Antigravity)
                            AntigravityApi.MarkAuthState(acct.Home, false);
                        else
                            CodexApi.MarkAuthState(acct.Home, false);
                        if (acct.Live) _liveAuthBad = true;
                        needRerender = true;
                        Logger.Log("fetch[" + acct.DisplayName + "]: AuthException: " + ex.Message);
                    }
                    catch (Exception ex)
                    {
                        UsageData old = null;
                        lock (_lock)
                        {
                            FetchResult prev;
                            if (_results.TryGetValue(acct.Home, out prev) && prev != null) old = prev.Data;
                        }
                        res.Data = old;
                        res.Error = ex.Message.Length > 80 ? ex.Message.Substring(0, 80) : ex.Message;
                        res.ErrorKind = "net";
                        Logger.Log("fetch[" + acct.DisplayName + "]: " + ex.Message);
                    }

                    lock (_lock) { _results[acct.Home] = res; }
                }

                if (_stop.IsSet) return;
                if (needRerender)
                {
                    RunUi(delegate
                    {
                        ReloadAccounts();
                        RenderAccount();
                        ApplyState();
                        if (_settingsWin != null && !_settingsWin.IsDisposed) _settingsWin.RenderAccounts();
                    });
                }
                else
                {
                    RunUi(ApplyState);
                }

                WaitForNextPoll(new List<AccountInfo>(Accounts));
            }
        }
    }
}
