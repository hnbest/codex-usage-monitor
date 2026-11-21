using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexUsageMonitor
{
    public static class NativeWin
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(int awareness);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        public static void SetTopMost(IntPtr hwnd)
        {
            try
            {
                // HWND_TOPMOST = -1, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW
                SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
            }
            catch { }
        }

        public static void EnableDpiAwareness()
        {
            try { SetProcessDpiAwareness(1); }
            catch
            {
                try { SetProcessDPIAware(); } catch { }
            }
        }

        public static void ApplyRoundCorners(IntPtr hwnd)
        {
            try
            {
                int pref = 2; // DWMWCP_ROUND (Win11)
                DwmSetWindowAttribute(hwnd, 33, ref pref, Marshal.SizeOf(pref));
            }
            catch { }
        }
    }

    public static class Renderer
    {
        internal static void RoundControl(Control control, float radius)
        {
            EventHandler update = delegate
            {
                if (control.ClientSize.Width <= 0 || control.ClientSize.Height <= 0) return;
                using (GraphicsPath path = RoundedRect(control.ClientRectangle, radius))
                    control.Region = new Region(path);
            };
            control.SizeChanged += update;
            update(control, EventArgs.Empty);
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            GraphicsPath gp = new GraphicsPath();
            float d = radius * 2f;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0.1f)
            {
                gp.AddRectangle(r);
                return gp;
            }
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        public static void DrawRing(Graphics g, Rectangle bounds, double? pct, Color color, float ringWidth, double? timePct, string timeText, float wavePhase, float textScale = 1f)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            float pad = ringWidth / 2f + 2f;
            RectangleF box = new RectangleF(
                bounds.X + pad, bounds.Y + pad,
                bounds.Width - pad * 2f, bounds.Height - pad * 2f);

            // 1. 环内油箱动态液位填充 (双层错相正弦波浪, 表示距离重置的剩余时间比例)
            float innerGap = ringWidth / 2f + 2.5f;
            RectangleF innerCircle = new RectangleF(
                box.X + innerGap, box.Y + innerGap,
                box.Width - innerGap * 2f, box.Height - innerGap * 2f);

            if (innerCircle.Width > 8f && innerCircle.Height > 8f)
            {
                GraphicsState gs = g.Save();
                using (GraphicsPath clipPath = new GraphicsPath())
                {
                    clipPath.AddEllipse(innerCircle);
                    g.SetClip(clipPath);

                    // 淡玻璃内腔底
                    using (SolidBrush cavityBr = new SolidBrush(Color.FromArgb(14, 255, 255, 255)))
                        g.FillEllipse(cavityBr, innerCircle);

                    if (timePct.HasValue)
                    {
                        float tp = (float)Math.Max(0.0, Math.Min(100.0, timePct.Value));
                        if (tp > 0.5f)
                        {
                            float fillH = innerCircle.Height * (tp / 100f);
                            float yLevel = innerCircle.Bottom - fillH;

                            // 接近满或空时自动衰减波幅, 避免贴边穿帮
                            float edgeFactor = Math.Min(1f, Math.Min(tp, 100f - tp) / 10f);
                            float amp1 = 2.6f * edgeFactor;
                            float amp2 = 2.0f * edgeFactor;

                            int steps = 16;
                            float x0 = innerCircle.Left - 2f;
                            float x1 = innerCircle.Right + 2f;
                            float span = x1 - x0;

                            // 后层浅波 (反向/错相, 营造立体水体层次)
                            if (amp2 > 0.1f)
                            {
                                PointF[] backWave = new PointF[steps + 1];
                                for (int i = 0; i <= steps; i++)
                                {
                                    float t = (float)i / steps;
                                    float px = x0 + span * t;
                                    float py = yLevel - 0.6f + (float)Math.Sin(t * Math.PI * 2.0 - wavePhase * 0.85 + 1.8) * amp2;
                                    backWave[i] = new PointF(px, py);
                                }
                                using (GraphicsPath backPath = new GraphicsPath())
                                {
                                    backPath.AddCurve(backWave, 0.5f);
                                    backPath.AddLine(x1, backWave[steps].Y, x1, innerCircle.Bottom + 2f);
                                    backPath.AddLine(x1, innerCircle.Bottom + 2f, x0, innerCircle.Bottom + 2f);
                                    backPath.CloseFigure();
                                    using (SolidBrush backBr = new SolidBrush(Color.FromArgb(44, 96, 165, 250)))
                                        g.FillPath(backBr, backPath);
                                }
                            }

                            // 前层主波
                            PointF[] frontWave = new PointF[steps + 1];
                            for (int i = 0; i <= steps; i++)
                            {
                                float t = (float)i / steps;
                                float px = x0 + span * t;
                                float py = yLevel + (float)Math.Sin(t * Math.PI * 2.0 + wavePhase) * amp1;
                                frontWave[i] = new PointF(px, py);
                            }
                            using (GraphicsPath liquidPath = new GraphicsPath())
                            {
                                liquidPath.AddCurve(frontWave, 0.5f);
                                liquidPath.AddLine(x1, frontWave[steps].Y, x1, innerCircle.Bottom + 2f);
                                liquidPath.AddLine(x1, innerCircle.Bottom + 2f, x0, innerCircle.Bottom + 2f);
                                liquidPath.CloseFigure();

                                RectangleF gradRect = new RectangleF(
                                    innerCircle.Left, Math.Max(innerCircle.Top, yLevel - 5f),
                                    innerCircle.Width, Math.Max(4f, innerCircle.Bottom - yLevel + 8f));
                                using (LinearGradientBrush lgb = new LinearGradientBrush(
                                    gradRect,
                                    Color.FromArgb(94, 124, 184, 255),
                                    Color.FromArgb(48, 64, 126, 222),
                                    LinearGradientMode.Vertical))
                                {
                                    g.FillPath(lgb, liquidPath);
                                }

                                // 流动液面高光线
                                if (tp < 98.5f)
                                {
                                    using (Pen crestPen = new Pen(Color.FromArgb(160, 178, 220, 255), 1.3f))
                                        g.DrawCurve(crestPen, frontWave, 0.5f);
                                }
                            }
                        }
                    }
                }
                g.Restore(gs);
            }

            // 2. 外环轨道与剩余额度进度弧
            using (Pen trackPen = new Pen(Theme.Track, ringWidth))
            {
                g.DrawEllipse(trackPen, box);
            }

            if (pct.HasValue)
            {
                float p = (float)Math.Max(0.0, Math.Min(100.0, pct.Value));
                if (p >= 99.9f)
                {
                    using (Pen arcPen = new Pen(color, ringWidth))
                        g.DrawEllipse(arcPen, box);
                }
                else if (p > 0f)
                {
                    using (Pen arcPen = new Pen(color, ringWidth))
                    {
                        arcPen.StartCap = LineCap.Round;
                        arcPen.EndCap = LineCap.Round;
                        g.DrawArc(arcPen, box, -90f, 360f * p / 100f);
                    }
                }
            }

            // 3. 中心文字: 额度百分比 + 下方剩余时间小字
            string txt = pct.HasValue ? string.Format("{0}%", (int)Math.Round(pct.Value)) : "?";
            Color fg = pct.HasValue ? color : Theme.Sub;
            bool hasTime = !string.IsNullOrEmpty(timeText);
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                if (hasTime)
                {
                    using (Font fBig = new Font("Microsoft YaHei UI", 13.0f * textScale, FontStyle.Bold, GraphicsUnit.Point))
                    using (SolidBrush br = new SolidBrush(fg))
                    {
                        g.DrawString(txt, fBig, br, new RectangleF(bounds.X, bounds.Y + bounds.Height * 0.21f, bounds.Width, bounds.Height * 0.36f), sf);
                    }
                    using (Font fSmall = new Font("Microsoft YaHei UI", 7.6f * textScale, FontStyle.Bold, GraphicsUnit.Point))
                    using (SolidBrush brT = new SolidBrush(ColorTranslator.FromHtml("#d2e2ff")))
                    {
                        g.DrawString(timeText, fSmall, brT, new RectangleF(bounds.X, bounds.Y + bounds.Height * 0.56f, bounds.Width, bounds.Height * 0.24f), sf);
                    }
                }
                else
                {
                    using (Font f = new Font("Microsoft YaHei UI", 13.5f * textScale, FontStyle.Bold, GraphicsUnit.Point))
                    using (SolidBrush br = new SolidBrush(fg))
                    {
                        g.DrawString(txt, f, br, new RectangleF(bounds.X, bounds.Y + 1, bounds.Width, bounds.Height), sf);
                    }
                }
            }
        }

        public static Bitmap RenderSettingsIcon(Color color, int size)
        {
            Bitmap bmp = new Bitmap(size, size);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float lineW = Math.Max(1.2f, size * 0.085f);
                float r = size * 0.11f;
                float[,] rows = new float[,] { { 0.20f, 0.32f }, { 0.50f, 0.66f }, { 0.80f, 0.44f } };
                using (Pen pen = new Pen(color, lineW))
                using (SolidBrush br = new SolidBrush(color))
                using (SolidBrush bg = new SolidBrush(Theme.Bg))
                {
                    for (int i = 0; i < 3; i++)
                    {
                        float y = size * rows[i, 0];
                        float x = size * rows[i, 1];
                        g.DrawLine(pen, size * 0.10f, y, size * 0.90f, y);
                        g.FillEllipse(br, x - r, y - r, r * 2f, r * 2f);
                        g.FillEllipse(bg, x - r * 0.45f, y - r * 0.45f, r * 0.9f, r * 0.9f);
                    }
                }
            }
            return bmp;
        }

        public static Icon RenderTrayIcon(double? pRemaining, double? sRemaining)
        {
            int S = 64;
            using (Bitmap bmp = new Bitmap(S, S))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);

                using (GraphicsPath bgPath = RoundedRect(new RectangleF(1, 1, S - 2, S - 2), 12))
                using (SolidBrush bgBr = new SolidBrush(Color.FromArgb(255, 30, 31, 36)))
                {
                    g.FillPath(bgBr, bgPath);
                }

                using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                })
                {
                    float rowHeight = S / 2f;
                    DrawTrayProgress(g, pRemaining, new RectangleF(0, 0, S, rowHeight), sf);
                    DrawTrayProgress(g, sRemaining, new RectangleF(0, rowHeight, S, rowHeight), sf);
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon tmp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)tmp.Clone();
                    }
                }
                finally
                {
                    NativeWin.DestroyIcon(hIcon);
                }
            }
        }

        private static void DrawTrayProgress(Graphics g, double? remaining, RectangleF bounds, StringFormat format)
        {
            RectangleF track = RectangleF.Inflate(bounds, -1f, -3f);
            double? percent = remaining.HasValue && !double.IsNaN(remaining.Value)
                ? (double?)Math.Max(0.0, Math.Min(100.0, remaining.Value))
                : null;
            using (GraphicsPath trackPath = RoundedRect(track, 6f))
            using (SolidBrush trackBrush = new SolidBrush(Theme.Track))
            {
                g.FillPath(trackBrush, trackPath);
                if (percent.GetValueOrDefault() > 0.0)
                {
                    float fillWidth = track.Width * (float)(percent.Value / 100.0);
                    GraphicsState state = g.Save();
                    g.SetClip(new RectangleF(track.X, track.Y, fillWidth, track.Height), CombineMode.Intersect);
                    using (SolidBrush fillBrush = new SolidBrush(Color.FromArgb(128, Theme.ColorFor(percent))))
                    {
                        g.FillPath(fillBrush, trackPath);
                    }
                    g.Restore(state);
                }
            }

            string text = percent.HasValue ? ((int)Math.Round(percent.Value)).ToString() : "?";
            float fontSize = text.Length >= 3 ? 30f : 36f;
            using (Font font = new Font("Arial", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush brush = new SolidBrush(Theme.Text))
            {
                g.DrawString(text, font, brush, bounds, format);
            }
        }
    }

    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            if (e.Item.Selected && e.Item.Enabled)
            {
                SmoothingMode smoothing = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath path = Renderer.RoundedRect(r, 5))
                using (SolidBrush br = new SolidBrush(Theme.Accent))
                    e.Graphics.FillPath(br, path);
                e.Graphics.SmoothingMode = smoothing;
            }
            else
            {
                using (SolidBrush br = new SolidBrush(Theme.Card))
                    e.Graphics.FillRectangle(br, new Rectangle(Point.Empty, e.Item.Size));
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Selected && e.Item.Enabled)
                e.TextColor = ColorTranslator.FromHtml("#12141a");
            else if (!e.Item.Enabled)
                e.TextColor = e.Item.ForeColor != SystemColors.ControlText ? e.Item.ForeColor : Theme.Green;
            else
                e.TextColor = Theme.Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen p = new Pen(Theme.Border))
                e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
            public override Color MenuBorder { get { return Theme.Border; } }
            public override Color MenuItemBorder { get { return Theme.Card; } }
        }
    }

    internal sealed class PeriodBadge : Label
    {
        private readonly Color _fill;

        public PeriodBadge(Color fill)
        {
            _fill = fill;
            BackColor = Theme.Bg;
            Size = new Size(28, 21);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            SmoothingMode previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF bounds = new RectangleF(0.5f, 0.5f, ClientSize.Width - 1, ClientSize.Height - 1);
            using (GraphicsPath path = Renderer.RoundedRect(bounds, 5))
            using (SolidBrush brush = new SolidBrush(_fill))
                e.Graphics.FillPath(brush, path);
            e.Graphics.SmoothingMode = previous;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (string.IsNullOrEmpty(Text)) return;

            float emSize = Font.SizeInPoints * e.Graphics.DpiY / 72f;
            using (GraphicsPath glyphs = new GraphicsPath())
            using (StringFormat format = StringFormat.GenericTypographic)
            using (Matrix offset = new Matrix())
            using (SolidBrush brush = new SolidBrush(ForeColor))
            {
                glyphs.AddString(Text, Font.FontFamily, (int)Font.Style, emSize, PointF.Empty, format);
                RectangleF ink = glyphs.GetBounds();
                offset.Translate((ClientSize.Width - ink.Width) / 2f - ink.Left,
                                 (ClientSize.Height - ink.Height) / 2f - ink.Top);
                glyphs.Transform(offset);
                SmoothingMode previous = e.Graphics.SmoothingMode;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.FillPath(brush, glyphs);
                e.Graphics.SmoothingMode = previous;
            }
        }
    }

    public class RingControl : Control
    {
        public float TextScale { get; set; }
        private double? _pct;
        private Color _color = Theme.Track;
        private double? _timePct;
        private string _timeText;
        private float _wavePhase;

        public RingControl()
        {
            TextScale = 1f;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Bg;
            Size = new Size(98, 98);
        }

        public bool HasAnimatedLiquid
        {
            get { return _timePct.HasValue && _timePct.Value > 0.5 && _timePct.Value < 99.5; }
        }

        public void SetInitialPhase(float phase)
        {
            _wavePhase = phase;
        }

        public void StepWave(float delta)
        {
            if (!HasAnimatedLiquid) return;
            _wavePhase += delta;
            if (_wavePhase > (float)(Math.PI * 200.0)) _wavePhase -= (float)(Math.PI * 200.0);
            Invalidate();
        }

        public void SetValue(double? pct, Color color, double? timePct, string timeText)
        {
            _pct = pct;
            _color = color;
            _timePct = timePct;
            _timeText = timeText;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Renderer.DrawRing(e.Graphics, ClientRectangle, _pct, _color, 8f * TextScale, _timePct, _timeText, _wavePhase, TextScale);
        }
    }

    public class MiniBarControl : Control
    {
        private double? _pct;
        private Color _color = Theme.Track;

        public MiniBarControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Theme.Card;
            Size = new Size(138, 7);
        }

        public void SetValue(double? pct, Color color)
        {
            _pct = pct;
            _color = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath bg = Renderer.RoundedRect(new RectangleF(0, 0, Width - 1, Height - 1), 3f))
            using (SolidBrush br = new SolidBrush(Theme.Track))
                e.Graphics.FillPath(br, bg);

            if (_pct.HasValue)
            {
                float p = (float)Math.Max(0.0, Math.Min(100.0, _pct.Value));
                float w = (Width - 1) * p / 100f;
                if (w >= 3f)
                {
                    using (GraphicsPath fg = Renderer.RoundedRect(new RectangleF(0, 0, w, Height - 1), 3f))
                    using (SolidBrush br = new SolidBrush(_color))
                        e.Graphics.FillPath(br, fg);
                }
            }
        }
    }
}
