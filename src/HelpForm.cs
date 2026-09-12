using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace IndepenDesk;

/// <summary>
/// "Nasıl kullanılır" penceresi: kısayollar, touchpad kurulum adımları ve
/// hareketleri gösteren döngülü animasyon (4 parmak sola/sağa/yukarı kaydırma).
/// </summary>
internal sealed class HelpForm : Form
{
    private static HelpForm? _open;
    private readonly float _layoutScale;
    private readonly int _contentWidth;
    private readonly Font _baseFont;
    private readonly Font _headerFont;
    private bool _resourcesDisposed;

    public static void ShowHelp()
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new HelpForm();
        _open.FormClosed += (_, _) => _open = null;
        _open.Show();
    }

    private HelpForm()
    {
        Text = L.T("help.title");
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(24, 24, 28);
        AutoScaleMode = AutoScaleMode.None;
        AutoScroll = true;
        string uiFontFamily = GetUiFontFamilyName();
        _baseFont = new Font(uiFontFamily, 9.5f);
        _headerFont = new Font(uiFontFamily, 11.5f, FontStyle.Bold);
        Font = _baseFont;

        var screen = Screen.FromPoint(Cursor.Position);
        using (var graphics = CreateGraphics())
            _layoutScale = Math.Clamp(graphics.DpiX / 96f, 1f, 4f);
        _contentWidth = ScalePx(672);

        int desiredWidth = ScalePx(720);
        int desiredHeight = ScalePx(640);
        ClientSize = new Size(
            Math.Min(desiredWidth, Math.Max(480, screen.WorkingArea.Width - ScalePx(32))),
            Math.Min(desiredHeight, Math.Max(420, screen.WorkingArea.Height - ScalePx(32))));
        Location = new Point(
            screen.WorkingArea.Left + (screen.WorkingArea.Width - Width) / 2,
            screen.WorkingArea.Top + (screen.WorkingArea.Height - Height) / 2);

        int y = ScalePx(18);
        y = AddHeader(L.T("help.shortcuts.header"), y);
        y = AddBody(L.T("help.shortcuts.body").Replace("&&", "&", StringComparison.Ordinal), y);

        y = AddHeader(L.T("help.demo.header"), y);
        var demo = new GesturePanel
        {
            Location = new Point(ScalePx(24), y),
            Size = new Size(_contentWidth, ScalePx(190))
        };
        Controls.Add(demo);
        y += ScalePx(196);

        y = AddHeader(L.T("help.touchpad.header"), y);
        y = AddBody(L.T("help.touchpad.body").Replace("&&", "&", StringComparison.Ordinal), y);

        var openBtn = new Button
        {
            Text = L.T("help.touchpad.open"),
            Location = new Point(ScalePx(24), y + ScalePx(4)),
            Size = new Size(ScalePx(260), ScalePx(34)),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 110, 180)
        };
        openBtn.FlatAppearance.BorderSize = 0;
        openBtn.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:devices-touchpad")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLog.Error("Open touchpad settings", ex);
                MessageBox.Show(this, ex.Message, "IndepenDesk",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        Controls.Add(openBtn);

        var closeBtn = new Button
        {
            Text = L.T("help.close"),
            Location = new Point(ScalePx(596), y + ScalePx(4)),
            Size = new Size(ScalePx(100), ScalePx(34)),
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(60, 60, 70)
        };
        closeBtn.FlatAppearance.BorderSize = 0;
        closeBtn.Click += (_, _) => Close();
        Controls.Add(closeBtn);

        AutoScrollMinSize = new Size(0, y + ScalePx(54));
    }

    private int AddHeader(string text, int y)
    {
        Controls.Add(new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(120, 170, 235),
            Font = _headerFont,
            Location = new Point(ScalePx(22), y),
            AutoSize = true
        });
        return y + ScalePx(30);
    }

    private int AddBody(string text, int y)
    {
        var lbl = new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(215, 215, 225),
            Location = new Point(ScalePx(24), y),
            MaximumSize = new Size(_contentWidth, 0),
            AutoSize = true
        };
        Controls.Add(lbl);
        return y + lbl.GetPreferredSize(new Size(_contentWidth, 0)).Height + ScalePx(14);
    }

    private int ScalePx(int value) => (int)Math.Round(value * _layoutScale);

    private static string GetUiFontFamilyName()
    {
        using Font? systemFont = SystemFonts.MessageBoxFont;
        return systemFont?.Name ?? FontFamily.GenericSansSerif.Name;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _headerFont.Dispose();
            _baseFont.Dispose();
        }
    }

    /// <summary>Touchpad hareketlerini döngü halinde canlandıran panel:
    /// 4 parmak sağa → masaüstü kayar; sola → geri; yukarı → genel bakış ızgarası.</summary>
    private sealed class GesturePanel : Panel
    {
        private const double MotionDurationMs = 1333.333;
        private const double PhaseDurationMs = 1866.667;

        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
        private readonly Stopwatch _clock = new();
        private readonly GraphicsPath _padPath =
            GraphicsExtensions.CreateRoundedRectanglePath(new Rectangle(30, 30, 180, 130), 16);
        private readonly GraphicsPath _monitorPath =
            GraphicsExtensions.CreateRoundedRectanglePath(new Rectangle(280, 22, 220, 132), 8);
        private readonly SolidBrush _padBrush = new(Color.FromArgb(48, 48, 58));
        private readonly Pen _padPen = new(Color.FromArgb(90, 90, 105), 2);
        private readonly SolidBrush _fingerBrush = new(Color.FromArgb(130, 185, 245));
        private readonly SolidBrush _trailBrush = new(Color.FromArgb(60, 130, 185, 245));
        private readonly Pen _arrowPen = new(Color.FromArgb(130, 185, 245), 3)
        {
            EndCap = LineCap.ArrowAnchor
        };
        private readonly Pen _monitorPen = new(Color.FromArgb(120, 120, 135), 2);
        private readonly SolidBrush _overviewCardBrush = new(Color.FromArgb(64, 96, 138));
        private readonly SolidBrush _desktopOneBrush = new(Color.FromArgb(64, 96, 138));
        private readonly SolidBrush _desktopTwoBrush = new(Color.FromArgb(76, 128, 96));
        private readonly SolidBrush _windowBrush = new(Color.FromArgb(190, 205, 225));
        private readonly Font _captionFont = new(GetUiFontFamilyName(), 12.7f, GraphicsUnit.Pixel);
        private readonly SolidBrush _captionBrush = new(Color.FromArgb(200, 200, 212));
        private readonly string _swipeUpCaption = L.T("help.demo.swipeUp");
        private readonly string _swipeLrCaption = L.T("help.demo.swipeLR");
        private readonly StringFormat _captionFormat = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };
        private double _t; // 0..1 faz ilerlemesi
        private int _phase; // 0: sağa, 1: sola, 2: yukarı
        private bool _drawingResourcesDisposed;

        public GesturePanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(30, 30, 36);
            _timer.Tick += (_, _) =>
            {
                double elapsed = _clock.Elapsed.TotalMilliseconds;
                long cycle = (long)(elapsed / PhaseDurationMs);
                _phase = (int)(cycle % 3);
                _t = elapsed % PhaseDurationMs / MotionDurationMs;
                Invalidate();
            };
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (_drawingResourcesDisposed) return;
            if (Visible)
            {
                _clock.Start();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
                _clock.Stop();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_drawingResourcesDisposed)
            {
                _drawingResourcesDisposed = true;
                _timer.Dispose();
                _clock.Stop();
                _padPath.Dispose();
                _monitorPath.Dispose();
                _padBrush.Dispose();
                _padPen.Dispose();
                _fingerBrush.Dispose();
                _trailBrush.Dispose();
                _arrowPen.Dispose();
                _monitorPen.Dispose();
                _overviewCardBrush.Dispose();
                _desktopOneBrush.Dispose();
                _desktopTwoBrush.Dispose();
                _windowBrush.Dispose();
                _captionFont.Dispose();
                _captionBrush.Dispose();
                _captionFormat.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const float designWidth = 672f;
            const float designHeight = 190f;
            float canvasScale = Math.Min(Width / designWidth, Height / designHeight);
            var state = g.Save();
            g.ScaleTransform(canvasScale, canvasScale);
            double p = Math.Min(1.0, _t);
            double ease = 1 - Math.Pow(1 - p, 3);

            // Sol: touchpad ve parmaklar
            var pad = new Rectangle(30, 30, 180, 130);
            g.FillPath(_padBrush, _padPath);
            g.DrawPath(_padPen, _padPath);

            // 4 parmak noktası: faza göre hareket yönü
            double dx = 0, dy = 0;
            switch (_phase)
            {
                case 0: dx = ease * 70; break;
                case 1: dx = -ease * 70; break;
                case 2: dy = -ease * 60; break;
            }
            for (int i = 0; i < 4; i++)
            {
                int fx = pad.Left + 45 + i * 26;
                int fy = pad.Top + 70;
                g.FillEllipse(_trailBrush, (int)(fx + dx * 0.55) - 8, (int)(fy + dy * 0.55) - 8, 16, 16);
                g.FillEllipse(_fingerBrush, (int)(fx + dx) - 8, (int)(fy + dy) - 8, 16, 16);
            }

            // Yön oku
            int cx = pad.Left + 90, cy = pad.Top - 8;
            switch (_phase)
            {
                case 0: g.DrawLine(_arrowPen, cx - 30, cy, cx + 30, cy); break;
                case 1: g.DrawLine(_arrowPen, cx + 30, cy, cx - 30, cy); break;
                case 2: g.DrawLine(_arrowPen, pad.Right, pad.Top + 95, pad.Right, pad.Top + 35); break;
            }

            // Sağ: mini monitör ve etki
            var mon = new Rectangle(280, 22, 220, 132);
            g.DrawPath(_monitorPen, _monitorPath);
            g.DrawLine(_monitorPen, mon.Left + 85, mon.Bottom + 8, mon.Right - 85, mon.Bottom + 8);
            var inner = Rectangle.Inflate(mon, -6, -6);
            g.SetClip(inner);

            if (_phase == 2)
            {
                // Genel bakış: ızgara halinde küçülen kartlar
                for (int i = 0; i < 4; i++)
                {
                    int gx = inner.Left + 12 + (i % 2) * 100;
                    int gy = inner.Top + 12 + (i / 2) * 58;
                    int w = (int)(84 * (0.55 + 0.45 * (1 - ease)));
                    int h = (int)(46 * (0.55 + 0.45 * (1 - ease)));
                    g.FillRoundedRectangleCompat(_overviewCardBrush,
                        new Rectangle(gx, gy, Math.Max(w, 46), Math.Max(h, 25)), 6);
                }
            }
            else
            {
                // Masaüstü kayması: iki renkli sahne yana kayar
                int shift = (int)(ease * inner.Width) * (_phase == 0 ? -1 : 1);
                g.FillRectangle(_desktopOneBrush, inner.Left + shift, inner.Top, inner.Width, inner.Height);
                g.FillRectangle(_desktopTwoBrush, inner.Left + shift + (_phase == 0 ? inner.Width : -inner.Width),
                    inner.Top, inner.Width, inner.Height);
                g.FillRoundedRectangleCompat(_windowBrush,
                    new Rectangle(inner.Left + shift + 20, inner.Top + 22, 74, 50), 4);
            }
            g.ResetClip();

            // Alt yazı
            string caption = _phase == 2 ? _swipeUpCaption : _swipeLrCaption;
            g.DrawString(caption, _captionFont, _captionBrush,
                new RectangleF(10, 162, designWidth - 20, 24), _captionFormat);
            g.Restore(state);
        }
    }
}

/// <summary>GDI+ için yuvarlatılmış dikdörtgen yardımcıları.</summary>
internal static class GraphicsExtensions
{
    public static void FillRoundedRectangleCompat(this Graphics g, Brush brush, Rectangle r, int radius)
    {
        using var path = CreateRoundedRectanglePath(r, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRectangleCompat(this Graphics g, Pen pen, Rectangle r, int radius)
    {
        using var path = CreateRoundedRectanglePath(r, radius);
        g.DrawPath(pen, path);
    }

    public static GraphicsPath CreateRoundedRectanglePath(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
