using System.Drawing.Drawing2D;

namespace IndepenDesk;

/// <summary>Masaüstü geçişinde ilgili monitörün ortasında kısa süre görünen gösterge.</summary>
internal sealed class OsdForm : Form
{
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 950 };
    private string _subtitle = "";

    public OsdForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.85;
        Size = new Size(250, 104);
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }

    public void ShowSwitch(SwitchInfo info)
    {
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == info.Device) ?? Screen.PrimaryScreen;
        if (screen == null) return;
        _subtitle = L.F("osd.desktop", info.LocalIndex + 1);

        using (var font = new Font("Segoe UI", 16f, FontStyle.Bold))
        {
            var measured = TextRenderer.MeasureText(_subtitle, font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int maxWidth = Math.Max(250, screen.WorkingArea.Width - 32);
            Width = Math.Min(maxWidth, Math.Max(250, measured.Width + 48));
            Height = Math.Max(104, measured.Height + 36);
        }

        var b = screen.Bounds;
        Location = new Point(b.Left + (b.Width - Width) / 2, b.Top + (b.Height - Height) / 2);
        if (!Visible) Show();
        Invalidate();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 14);
        using var bg = new SolidBrush(Color.FromArgb(30, 30, 34));
        e.Graphics.FillPath(bg, path);
        using var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        };
        using var titleFont = new Font("Segoe UI", 16f, FontStyle.Bold);
        e.Graphics.DrawString(_subtitle, titleFont, Brushes.White,
            ClientRectangle, sf);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hideTimer.Dispose();
        base.Dispose(disposing);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
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
