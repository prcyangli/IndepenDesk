using System.Drawing.Drawing2D;

namespace IndepenDesk;

/// <summary>
/// Genel bakış (Mission Control benzeri): tüm monitörler ve masaüstleri kartlar halinde.
/// Pencereler tutamaçlı (⠿) kutucuklar olarak listelenir ve sürükle-bırak ile taşınır;
/// geçerli bırakma hedefleri sürükleme sırasında yeşil çerçeveyle vurgulanır.
/// </summary>
internal sealed class OverviewForm : Form
{
    private static OverviewForm? _open;
    public static bool IsOpen => _open != null;

    private readonly DesktopManager _mgr;
    private readonly ToolTip _tips = new() { InitialDelay = 400 };
    private readonly Dictionary<(float Size, FontStyle Style), Font> _ownedFonts = new();
    private readonly HashSet<ContextMenuStrip> _ownedMenus = new();

    // Renk paleti
    private static readonly Color BgColor = Color.FromArgb(23, 23, 27);
    private static readonly Color CardBg = Color.FromArgb(38, 38, 45);
    private static readonly Color CardBorder = Color.FromArgb(58, 58, 68);
    private static readonly Color Accent = Color.FromArgb(75, 141, 224);   // aktif masaüstü
    private static readonly Color DropAccent = Color.FromArgb(70, 170, 110); // bırakma hedefi
    private static readonly Color ChipBg = Color.FromArgb(53, 53, 61);
    private static readonly Color ChipHover = Color.FromArgb(72, 72, 84);
    private static readonly Color ChipBorder = Color.FromArgb(80, 80, 94);
    private static readonly Color TextDim = Color.FromArgb(150, 150, 162);
    // Keep cards wide enough for the DPI-scaled header, but short enough for
    // two wrapped rows per monitor (four rows total with two monitors).
    private static readonly Size DesktopCardDesignSize = new(400, 200);
    private const int AddCardDesignWidth = 100;
    private const int WindowChipDesignSpacing = 3;

    private readonly float _layoutScale;
    private readonly Size _desktopCardSize;
    private readonly int _addCardWidth;
    private readonly int _windowChipSpacing;
    private readonly int _windowChipHeight;
    private bool _keepOpenOnDeactivate;
    private bool _suppressManagerRefresh;
    private bool _dragInProgress;
    private bool _managerRefreshPending;
    private bool _resourcesDisposed;

    private sealed record WindowDrag(IntPtr Handle);
    private sealed record DesktopDrag(string Device, int LocalIndex);

    public static void Toggle(DesktopManager mgr)
    {
        if (_open != null) { _open.Close(); return; }
        var f = new OverviewForm(mgr);
        _open = f;
        f.FormClosed += (_, _) => _open = null;
        f.Show();
        f.Activate();
    }

    private OverviewForm(DesktopManager mgr)
    {
        _mgr = mgr;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = BgColor;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;

        Native.GetCursorPos(out var pt);
        var screen = Screen.AllScreens.FirstOrDefault(s => s.Bounds.Contains(pt.X, pt.Y)) ?? Screen.PrimaryScreen!;
        var b = screen.WorkingArea;
        Bounds = new Rectangle(b.Left + b.Width / 12, b.Top + b.Height / 12,
                               b.Width * 10 / 12, b.Height * 10 / 12);

        using (var graphics = CreateGraphics())
        using (var windowFont = CreateUiFont(8.8f))
        {
            // Fonts already follow PerMonitorV2. Scale fixed geometry more gently so
            // cards stay usable at 200-300% without becoming larger than the screen.
            float dpiScale = Math.Clamp(graphics.DpiX / 96f, 1f, 4f);
            _layoutScale = Math.Clamp(1f + (dpiScale - 1f) * 0.25f, 1f, 1.75f);
            _desktopCardSize = ScaleSize(DesktopCardDesignSize);
            _addCardWidth = ScalePx(AddCardDesignWidth);
            _windowChipSpacing = ScalePx(WindowChipDesignSpacing);
            _windowChipHeight = Math.Max(ScalePx(29),
                (int)Math.Ceiling(windowFont.GetHeight(graphics)) + ScalePx(5));
        }

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Deactivate += (_, _) =>
        {
            if (!_keepOpenOnDeactivate)
                Close();
        };
        _mgr.DesktopSwitched += OnDesktopSwitched;

        BuildUi();
    }

    private void OnDesktopSwitched(SwitchInfo _)
    {
        if (_suppressManagerRefresh || IsDisposed || !IsHandleCreated) return;
        if (_dragInProgress)
        {
            _managerRefreshPending = true;
            return;
        }
        BeginInvoke((Action)(() =>
        {
            if (!IsDisposed && !_suppressManagerRefresh)
                BuildUi();
        }));
    }

    private void BuildUi()
    {
        // ToolTip keeps strong references to associated controls even after the
        // controls are disposed. A rebuild must detach the old tree first or an
        // overview left open while repeatedly moving windows grows indefinitely.
        _tips.RemoveAll();
        foreach (Control control in Controls.Cast<Control>().ToArray())
            control.Dispose();

        var root = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = ScalePadding(22, 16, 22, 16)
        };
        Controls.Add(root);

        var closeButton = new Button
        {
            Text = string.Empty,
            ForeColor = Color.White,
            BackColor = CardBg,
            FlatStyle = FlatStyle.Flat,
            TextAlign = ContentAlignment.MiddleCenter,
            Size = ScaleSize(new Size(72, 60)),
            Location = new Point(ClientSize.Width - ScalePx(86), ScalePx(12)),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Cursor = Cursors.Hand,
            TabStop = false,
            AccessibleName = L.T("help.close"),
            UseVisualStyleBackColor = false
        };
        closeButton.FlatAppearance.BorderSize = 1;
        closeButton.FlatAppearance.BorderColor = CardBorder;
        closeButton.FlatAppearance.MouseOverBackColor = ChipHover;
        closeButton.FlatAppearance.MouseDownBackColor = ChipBg;
        closeButton.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = Math.Min(closeButton.ClientSize.Width, closeButton.ClientSize.Height) * 0.3f;
            float right = closeButton.ClientSize.Width - inset;
            float bottom = closeButton.ClientSize.Height - inset;
            using var pen = new Pen(Color.White, Math.Max(2.5f, closeButton.DeviceDpi / 48f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawLine(pen, inset, inset, right, bottom);
            e.Graphics.DrawLine(pen, right, inset, inset, bottom);
        };
        closeButton.Click += (_, _) => Close();
        _tips.SetToolTip(closeButton, L.T("help.close"));
        Controls.Add(closeButton);
        closeButton.BringToFront();

        root.Controls.Add(new Label
        {
            Text = L.T("ov.title"),
            ForeColor = Color.White,
            Font = UiFont(15f, FontStyle.Bold),
            AutoSize = true,
            Margin = ScalePadding(4, 0, 0, 2)
        });
        root.Controls.Add(new Label
        {
            Text = L.T("ov.legend"),
            ForeColor = TextDim,
            Font = UiFont(9.5f),
            AutoSize = true,
            Margin = ScalePadding(4, 0, 0, 14)
        });

        foreach (var mon in _mgr.GetLayout())
        {
            root.Controls.Add(new Label
            {
                Text = "🖥  " + L.F("ov.monitor", mon.Ordinal),
                ForeColor = Color.White,
                Font = UiFont(12.5f, FontStyle.Bold),
                AutoSize = true,
                Margin = ScalePadding(4, 10, 0, 4)
            });

            var row = new BufferedPanel
            {
                AutoSize = false,
                AllowDrop = true,
                Padding = new Padding(ScalePx(4)),
                Margin = ScalePadding(0, 0, 0, 8),
                Width = Math.Max(_desktopCardSize.Width + ScalePx(22),
                    ClientSize.Width - root.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - ScalePx(8))
            };
            var flow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = false,
                Location = new Point(ScalePx(4), ScalePx(4))
            };
            row.Controls.Add(flow);

            bool rowHover = false;
            row.Paint += (_, e) =>
            {
                if (!rowHover) return;
                using var pen = new Pen(DropAccent, 2) { DashStyle = DashStyle.Dash };
                e.Graphics.DrawRectangle(pen, 1, 1, row.Width - 3, row.Height - 3);
            };
            row.DragEnter += (_, e) =>
            {
                if (e.Data?.GetData(typeof(DesktopDrag)) is DesktopDrag d &&
                    (d.Device == mon.Device || mon.Desktops.Count < DesktopManager.MaxDesktopsPerMonitor))
                {
                    e.Effect = DragDropEffects.Move;
                    rowHover = true;
                    row.Invalidate();
                }
            };
            row.DragLeave += (_, _) => { rowHover = false; row.Invalidate(); };
            row.DragDrop += (_, e) =>
            {
                rowHover = false;
                if (e.Data?.GetData(typeof(DesktopDrag)) is DesktopDrag d)
                    RunAndRefreshOverview(() =>
                        _mgr.MoveDesktop(d.Device, d.LocalIndex, mon.Device, int.MaxValue));
            };
            root.Controls.Add(row);

            foreach (var desk in mon.Desktops)
                flow.Controls.Add(BuildDesktopCard(mon, desk));

            if (mon.Desktops.Count < DesktopManager.MaxDesktopsPerMonitor)
                flow.Controls.Add(BuildAddCard(mon.Device));

            int flowWidth = row.ClientSize.Width - ScalePx(8);
            int usedWidth = 0;
            int lineCount = 1;
            foreach (Control item in flow.Controls)
            {
                int itemWidth = item.Width + item.Margin.Horizontal;
                if (usedWidth > 0 && usedWidth + itemWidth > flowWidth)
                {
                    lineCount++;
                    usedWidth = 0;
                }
                usedWidth += itemWidth;
            }
            row.Height = ScalePx(8) + lineCount * (_desktopCardSize.Height + ScalePx(14));
            flow.Size = new Size(flowWidth, row.ClientSize.Height - ScalePx(8));
        }
    }

    // ---------- masaüstü kartı ----------

    private Control BuildDesktopCard(MonitorEntry mon, DesktopEntry desk)
    {
        var card = new BufferedPanel
        {
            Size = _desktopCardSize,
            BackColor = CardBg,
            Margin = new Padding(ScalePx(7)),
            AllowDrop = true,
            Cursor = Cursors.Hand
        };

        System.Windows.Forms.Timer? singleClickTimer = null;
        void CancelPendingSingleClick()
        {
            singleClickTimer?.Stop();
            singleClickTimer?.Dispose();
            singleClickTimer = null;
        }
        void ActivateDesktopAndClose()
        {
            CancelPendingSingleClick();
            _mgr.SwitchTo(mon.Device, desk.LocalIndex);
            Close();
        }
        void QueueSingleClickSwitch()
        {
            if (singleClickTimer != null) return;
            singleClickTimer = new System.Windows.Forms.Timer
            {
                Interval = SystemInformation.DoubleClickTime
            };
            singleClickTimer.Tick += (_, _) =>
            {
                CancelPendingSingleClick();
                if (!card.IsDisposed)
                    RunAndRefreshOverview(() => _mgr.SwitchTo(mon.Device, desk.LocalIndex));
            };
            singleClickTimer.Start();
        }
        card.Disposed += (_, _) => CancelPendingSingleClick();

        bool dropHover = false;
        bool desktopDropHover = false;
        bool insertAfter = false;
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color border = dropHover ? DropAccent : desk.IsCurrent ? Accent : CardBorder;
            using var pen = new Pen(border, dropHover || desk.IsCurrent ? 2.5f : 1.5f);
            using var path = RoundedRect(new Rectangle(1, 1, card.Width - 3, card.Height - 3), 10);
            e.Graphics.DrawPath(pen, path);

            if (dropHover && desktopDropHover)
            {
                int x = insertAfter ? card.Width - 5 : 4;
                using var insertPen = new Pen(DropAccent, 4);
                e.Graphics.DrawLine(insertPen, x, 8, x, card.Height - 8);
            }
            else if (dropHover)
            {
                using var hint = CreateUiFont(11f, FontStyle.Bold);
                using var brush = new SolidBrush(DropAccent);
                using var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap,
                    Trimming = StringTrimming.EllipsisCharacter
                };
                int hintHeight = (int)Math.Ceiling(hint.GetHeight(e.Graphics)) + ScalePx(8);
                int horizontalInset = ScalePx(8);
                int bottomInset = ScalePx(14);
                e.Graphics.DrawString(L.T("ov.drop"), hint, brush,
                    new Rectangle(horizontalInset,
                        card.Height - hintHeight - bottomInset,
                        card.Width - horizontalInset * 2,
                        hintHeight), sf);
            }
        };

        // Başlık: kartı monitörler arası taşımak için sürükleme tutamacıdır
        var headerLbl = new Label
        {
            Text = L.F("ov.desktop", desk.LocalIndex + 1),
            ForeColor = Color.White,
            Font = UiFont(10.8f, FontStyle.Bold),
            // Move the label above the button top slightly: the font's internal
            // leading otherwise makes the visible glyphs look too low at 200% DPI.
            Location = new Point(ScalePx(10), ScalePx(3)),
            Size = new Size(card.Width - ScalePx(60), ScalePx(32)),
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.TopLeft,
            Cursor = Cursors.SizeAll,
            BackColor = Color.Transparent
        };
        _tips.SetToolTip(headerLbl, L.T("ov.tip.header"));
        Point? headerDragStart = null;
        bool headerWasDragged = false;
        headerLbl.MouseDown += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                headerWasDragged = false;
                headerDragStart = e.Location;
            }
        };
        headerLbl.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || headerDragStart is not Point start) return;
            var dragBounds = new Rectangle(
                start.X - SystemInformation.DragSize.Width / 2,
                start.Y - SystemInformation.DragSize.Height / 2,
                SystemInformation.DragSize.Width,
                SystemInformation.DragSize.Height);
            if (dragBounds.Contains(e.Location)) return;
            headerDragStart = null;
            headerWasDragged = true;
            StartDrag(headerLbl, new DesktopDrag(mon.Device, desk.LocalIndex));
        };
        headerLbl.MouseUp += (_, _) => headerDragStart = null;
        headerLbl.Click += (_, _) =>
        {
            if (!headerWasDragged)
                QueueSingleClickSwitch();
            headerWasDragged = false;
        };
        headerLbl.DoubleClick += (_, _) => ActivateDesktopAndClose();
        card.Controls.Add(headerLbl);

        var deleteButton = new Button
        {
            Text = string.Empty,
            Size = ScaleSize(new Size(36, 30)),
            Location = new Point(card.Width - ScalePx(46), ScalePx(6)),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            BackColor = ChipBg,
            FlatStyle = FlatStyle.Flat,
            Cursor = mon.Desktops.Count > 1 ? Cursors.Hand : Cursors.Default,
            Enabled = mon.Desktops.Count > 1,
            TabStop = false,
            AccessibleName = L.T("ov.tip.closeDesktop"),
            UseVisualStyleBackColor = false
        };
        deleteButton.FlatAppearance.BorderSize = 1;
        deleteButton.FlatAppearance.BorderColor = CardBorder;
        deleteButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(115, 52, 58);
        deleteButton.FlatAppearance.MouseDownBackColor = Color.FromArgb(145, 58, 65);
        deleteButton.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float inset = Math.Min(deleteButton.ClientSize.Width, deleteButton.ClientSize.Height) * 0.31f;
            float right = deleteButton.ClientSize.Width - inset;
            float bottom = deleteButton.ClientSize.Height - inset;
            Color color = deleteButton.Enabled ? Color.FromArgb(235, 190, 194) : TextDim;
            using var pen = new Pen(color, Math.Max(2f, deleteButton.DeviceDpi / 64f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            e.Graphics.DrawLine(pen, inset, inset, right, bottom);
            e.Graphics.DrawLine(pen, right, inset, inset, bottom);
        };
        deleteButton.Click += (_, _) =>
            RunAndRefreshOverview(() => _mgr.DeleteDesktop(mon.Device, desk.LocalIndex));
        _tips.SetToolTip(deleteButton, L.T("ov.tip.closeDesktop"));
        card.Controls.Add(deleteButton);
        deleteButton.BringToFront();

        if (desk.IsCurrent)
        {
            var badge = new Label
            {
                Text = L.T("ov.active"),
                ForeColor = Color.White,
                BackColor = Accent,
                Font = UiFont(8.3f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                AutoSize = true,
                Padding = ScalePadding(4, 1, 4, 1)
            };
            card.Controls.Add(badge);
            badge.Click += (_, _) => QueueSingleClickSwitch();
            badge.Location = new Point(deleteButton.Left - badge.PreferredSize.Width - ScalePx(6), ScalePx(8));
            headerLbl.Width = Math.Max(ScalePx(80), badge.Left - headerLbl.Left - ScalePx(4));
            badge.BringToFront();
        }
        else
        {
            headerLbl.Width = Math.Max(ScalePx(80), deleteButton.Left - headerLbl.Left - ScalePx(4));
        }

        int windowListTop = Math.Max(ScalePx(38),
            headerLbl.Top + headerLbl.PreferredSize.Height + ScalePx(4));
        var windowList = new FlowLayoutPanel
        {
            Location = new Point(ScalePx(10), windowListTop),
            Size = new Size(card.Width - ScalePx(20), card.Height - windowListTop - ScalePx(8)),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            AllowDrop = true,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        card.Controls.Add(windowList);

        int contentHeight = desk.Windows.Count * (_windowChipHeight + _windowChipSpacing);
        bool needsVerticalScroll = contentHeight > windowList.ClientSize.Height;
        int scrollbarWidth = needsVerticalScroll ? SystemInformation.VerticalScrollBarWidth : 0;
        int chipWidth = windowList.ClientSize.Width - scrollbarWidth - 2;
        foreach (var win in desk.Windows)
        {
            var chip = BuildWindowChip(win, mon.Device, desk.LocalIndex, Point.Empty, chipWidth);
            chip.Margin = new Padding(0, 0, 0, _windowChipSpacing);
            windowList.Controls.Add(chip);
        }

        if (desk.Windows.Count == 0)
        {
            var emptyLabel = new Label
            {
                Text = L.T("ov.empty"),
                ForeColor = TextDim,
                Font = UiFont(10.5f, FontStyle.Italic),
                AutoSize = true,
                BackColor = Color.Transparent
            };
            emptyLabel.Click += (_, _) => QueueSingleClickSwitch();
            windowList.Controls.Add(emptyLabel);
        }

        card.Click += (_, _) => QueueSingleClickSwitch();
        card.DoubleClick += (_, _) => ActivateDesktopAndClose();
        windowList.Click += (_, _) => QueueSingleClickSwitch();

        void OnDragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetData(typeof(WindowDrag)) is WindowDrag)
            {
                e.Effect = DragDropEffects.Move;
                dropHover = true;
                desktopDropHover = false;
                card.Invalidate();
            }
            else if (e.Data?.GetData(typeof(DesktopDrag)) is DesktopDrag d &&
                     (d.Device == mon.Device || mon.Desktops.Count < DesktopManager.MaxDesktopsPerMonitor))
            {
                e.Effect = DragDropEffects.Move;
                dropHover = true;
                desktopDropHover = true;
                UpdateInsertSide(e);
                card.Invalidate();
            }
        }
        void OnDragOver(object? sender, DragEventArgs e)
        {
            if (desktopDropHover && e.Data?.GetData(typeof(DesktopDrag)) is DesktopDrag)
            {
                e.Effect = DragDropEffects.Move;
                UpdateInsertSide(e);
            }
        }
        void UpdateInsertSide(DragEventArgs e)
        {
            bool next = card.PointToClient(new Point(e.X, e.Y)).X >= card.ClientSize.Width / 2;
            if (next == insertAfter) return;
            insertAfter = next;
            card.Invalidate();
        }
        void OnDragLeave(object? sender, EventArgs e)
        {
            dropHover = false;
            desktopDropHover = false;
            card.Invalidate();
        }
        void OnDragDrop(object? sender, DragEventArgs e)
        {
            dropHover = false;
            desktopDropHover = false;
            if (e.Data?.GetData(typeof(WindowDrag)) is WindowDrag w)
            {
                RunAndRefreshOverview(() =>
                    _mgr.MoveWindowToDesktop(w.Handle, mon.Device, desk.LocalIndex));
            }
            else if (e.Data?.GetData(typeof(DesktopDrag)) is DesktopDrag d)
            {
                int targetIndex = desk.LocalIndex + (insertAfter ? 1 : 0);
                RunAndRefreshOverview(() =>
                    _mgr.MoveDesktop(d.Device, d.LocalIndex, mon.Device, targetIndex));
            }
        }

        card.DragEnter += OnDragEnter;
        card.DragOver += OnDragOver;
        card.DragLeave += OnDragLeave;
        card.DragDrop += OnDragDrop;
        windowList.DragEnter += OnDragEnter;
        windowList.DragOver += OnDragOver;
        windowList.DragLeave += OnDragLeave;
        windowList.DragDrop += OnDragDrop;
        windowList.DoubleClick += (_, _) => ActivateDesktopAndClose();
        return card;
    }

    // ---------- pencere kutucuğu ----------

    private Control BuildWindowChip(
        WindowEntry win, string sourceDevice, int sourceLocal, Point location, int width = 316)
    {
        var chip = new BufferedPanel
        {
            Location = location,
            Size = new Size(width, _windowChipHeight),
            BackColor = ChipBg,
            Cursor = Cursors.SizeAll
        };
        chip.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(ChipBorder, 1);
            using var path = RoundedRect(new Rectangle(0, 0, chip.Width - 1, chip.Height - 1), 7);
            e.Graphics.DrawPath(pen, path);
        };

        var grip = new Label
        {
            Text = "⠿",
            ForeColor = TextDim,
            Font = UiFont(10f),
            Location = new Point(ScalePx(4), 0),
            Size = new Size(ScalePx(20), _windowChipHeight),
            TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false,
            BackColor = Color.Transparent,
            Cursor = Cursors.SizeAll
        };
        chip.Controls.Add(grip);

        int textX = ScalePx(28);
        PictureBox? iconBox = null;
        using var icon = Native.GetWindowSmallIcon(win.Handle);
        if (icon != null)
        {
            var iconImage = icon.ToBitmap();
            int iconSize = Math.Min(ScalePx(20), _windowChipHeight - ScalePx(8));
            iconBox = new PictureBox
            {
                Image = iconImage,
                Size = new Size(iconSize, iconSize),
                Location = new Point(ScalePx(28), (_windowChipHeight - iconSize) / 2),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
                Cursor = Cursors.SizeAll,
                TabStop = false
            };
            iconBox.Disposed += (_, _) => iconImage.Dispose();
            chip.Controls.Add(iconBox);
            textX = ScalePx(52);
        }

        var titleLbl = new Label
        {
            Text = win.Title,
            ForeColor = Color.FromArgb(225, 225, 235),
            Font = UiFont(8.8f),
            Location = new Point(textX, 0),
            Size = new Size(chip.Width - textX - ScalePx(6), chip.Height),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Cursor = Cursors.SizeAll
        };
        chip.Controls.Add(titleLbl);

        _tips.SetToolTip(chip, L.T("ov.tip.window"));
        _tips.SetToolTip(titleLbl, win.Title);

        void Hover(bool on) => chip.BackColor = on ? ChipHover : ChipBg;
        // Sürükleme, sistem sürükleme eşiği aşılınca başlar; aksi durumda hızlı
        // çift tıklama sürüklemeye dönüşür ve DoubleClick olayı asla oluşmaz.
        Point? chipDragStart = null;
        void OnDown(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                chipDragStart = e.Location;
        }
        void OnMove(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || chipDragStart is not Point start) return;
            var dragBounds = new Rectangle(
                start.X - SystemInformation.DragSize.Width / 2,
                start.Y - SystemInformation.DragSize.Height / 2,
                SystemInformation.DragSize.Width,
                SystemInformation.DragSize.Height);
            if (dragBounds.Contains(e.Location)) return;
            chipDragStart = null;
            StartDrag(chip, new WindowDrag(win.Handle));
        }
        void OnUp(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                chipDragStart = null;
            else if (e.Button == MouseButtons.Right)
                ShowWindowMenu(win, sourceDevice, sourceLocal, chip);
        }
        // Çift tık: pencerenin masaüstüne git, simge durumundaysa geri yükle ve odakla.
        void OnChipDoubleClick(object? s, EventArgs e)
        {
            chipDragStart = null;
            if (!Native.IsWindow(win.Handle)) return;
            _mgr.SwitchToWindow(win.Handle);
            Close();
        }

        var dragControls = new List<Control> { chip, grip, titleLbl };
        if (iconBox != null)
            dragControls.Add(iconBox);
        foreach (Control c in dragControls)
        {
            c.MouseEnter += (_, _) => Hover(true);
            c.MouseLeave += (_, _) => Hover(false);
            c.MouseDown += OnDown;
            c.MouseMove += OnMove;
            c.MouseUp += OnUp;
            c.DoubleClick += OnChipDoubleClick;
        }
        return chip;
    }

    /// <summary>Pencereye sağ tık: her monitörün her masaüstüne taşıma menüsü
    /// (görev çubuğu menüsü genişletilemediği için karşılığı burasıdır).</summary>
    private void ShowWindowMenu(WindowEntry win, string sourceDevice, int sourceLocal, Control anchor)
    {
        var menu = new ContextMenuStrip();
        _ownedMenus.Add(menu);
        menu.Closed += (_, _) =>
        {
            // Menus auto-dismiss on activation loss while an item's Click handler
            // is still running (e.g. a window shown by MoveWindowToDesktop steals
            // the foreground). Disposing here would leave WinForms' HandleItemClick
            // operating on a disposed menu (ObjectDisposedException). Defer the
            // disposal until the message loop is idle.
            if (IsDisposed || !IsHandleCreated)
            {
                _ownedMenus.Remove(menu);
                menu.Dispose();
                return;
            }
            BeginInvoke((Action)(() =>
            {
                _ownedMenus.Remove(menu);
                menu.Dispose();
            }));
        };
        foreach (var mon in _mgr.GetLayout())
        {
            foreach (var desk in mon.Desktops)
            {
                string label = L.F("ov.menu.move", mon.Ordinal, desk.LocalIndex + 1) +
                               (desk.IsCurrent ? L.T("ov.menu.activeSuffix") : "");
                var (device, local) = (mon.Device, desk.LocalIndex);
                menu.Items.Add(label, null, (_, _) =>
                {
                    RunAndRefreshOverview(() =>
                        _mgr.MoveWindowToDesktop(win.Handle, device, local));
                });
            }
            // Mirror of dropping a window onto the "+" card: same manager method,
            // same "create at the end without switching" behavior; hidden when full.
            if (mon.Desktops.Count < DesktopManager.MaxDesktopsPerMonitor)
            {
                string newDesktopDevice = mon.Device;
                menu.Items.Add(L.F("ov.menu.moveNew", mon.Ordinal), null, (_, _) =>
                {
                    RunAndRefreshOverview(() =>
                        _mgr.CreateDesktopAndMoveWindow(win.Handle, newDesktopDevice));
                });
            }
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("ov.menu.goto"), null, (_, _) =>
        {
            _mgr.SwitchTo(sourceDevice, sourceLocal);
            Close();
            if (Native.IsWindow(win.Handle) && !Native.IsHungAppWindow(win.Handle))
            {
                if (!Native.IsWindowVisible(win.Handle))
                    Native.ShowWindow(win.Handle, Native.SW_SHOWNA);
                Native.SetForegroundWindow(win.Handle);
            }
        });
        try
        {
            menu.Show(anchor, new Point(0, anchor.Height));
        }
        catch
        {
            _ownedMenus.Remove(menu);
            menu.Dispose();
            throw;
        }
    }

    // ---------- yeni masaüstü kartı ----------

    private Control BuildAddCard(string device)
    {
        var card = new BufferedPanel
        {
            Size = new Size(_addCardWidth, _desktopCardSize.Height),
            BackColor = BgColor,
            Margin = new Padding(ScalePx(7)),
            AllowDrop = true,
            Cursor = Cursors.Hand
        };
        bool hover = false;
        bool dropHover = false;
        card.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color highlight = dropHover ? DropAccent : Accent;
            using var pen = new Pen(hover || dropHover ? highlight : CardBorder, dropHover ? 2.5f : 1.5f)
            {
                DashStyle = DashStyle.Dash
            };
            using var path = RoundedRect(new Rectangle(1, 1, card.Width - 3, card.Height - 3), 10);
            e.Graphics.DrawPath(pen, path);
            using var font = CreateUiFont(24f);
            using var brush = new SolidBrush(hover || dropHover ? highlight : TextDim);
            using var sf = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            e.Graphics.DrawString("+", font, brush, card.ClientRectangle, sf);
        };
        _tips.SetToolTip(card, L.T("ov.tip.add"));
        card.MouseEnter += (_, _) => { hover = true; card.Invalidate(); };
        card.MouseLeave += (_, _) => { hover = false; card.Invalidate(); };
        card.DragEnter += (_, e) =>
        {
            if (e.Data?.GetData(typeof(WindowDrag)) is WindowDrag ||
                e.Data?.GetData(typeof(DesktopDrag)) is DesktopDrag)
            {
                e.Effect = DragDropEffects.Move;
                dropHover = true;
                card.Invalidate();
            }
        };
        card.DragLeave += (_, _) =>
        {
            dropHover = false;
            card.Invalidate();
        };
        card.DragDrop += (_, e) =>
        {
            dropHover = false;
            if (e.Data?.GetData(typeof(WindowDrag)) is WindowDrag window)
            {
                RunAndRefreshOverview(() =>
                    _mgr.CreateDesktopAndMoveWindow(window.Handle, device));
            }
            else if (e.Data?.GetData(typeof(DesktopDrag)) is DesktopDrag desktop)
            {
                RunAndRefreshOverview(() =>
                    _mgr.MoveDesktop(desktop.Device, desktop.LocalIndex, device, int.MaxValue));
            }
        };
        card.Click += (_, _) => RunAndRefreshOverview(() => _mgr.CreateDesktop(device));
        return card;
    }

    private void RunAndRefreshOverview(Action action)
    {
        _keepOpenOnDeactivate = true;
        _suppressManagerRefresh = true;
        try
        {
            action();
            BuildUi();
        }
        catch (Exception ex)
        {
            AppLog.Error(nameof(RunAndRefreshOverview), ex);
        }
        finally
        {
            _suppressManagerRefresh = false;
        }

        if (!IsDisposed && IsHandleCreated)
            BeginInvoke((Action)(() =>
        {
            if (IsDisposed) return;
            Activate();
            _keepOpenOnDeactivate = false;
        }));
    }

    private int ScalePx(int value) => (int)Math.Round(value * _layoutScale);

    private Size ScaleSize(Size size) => new(ScalePx(size.Width), ScalePx(size.Height));

    private Padding ScalePadding(int left, int top, int right, int bottom) =>
        new(ScalePx(left), ScalePx(top), ScalePx(right), ScalePx(bottom));

    private void StartDrag(Control source, object payload)
    {
        _dragInProgress = true;
        try
        {
            source.DoDragDrop(new DataObject(payload), DragDropEffects.Move);
        }
        finally
        {
            _dragInProgress = false;
            if (_managerRefreshPending && !IsDisposed && IsHandleCreated)
            {
                _managerRefreshPending = false;
                BeginInvoke((Action)(() =>
                {
                    if (!IsDisposed)
                        BuildUi();
                }));
            }
        }
    }

    private Font UiFont(float size, FontStyle style = FontStyle.Regular)
    {
        var key = (size, style);
        if (!_ownedFonts.TryGetValue(key, out var font))
        {
            font = CreateUiFont(size, style);
            _ownedFonts[key] = font;
        }
        return font;
    }

    private static Font CreateUiFont(float size, FontStyle style = FontStyle.Regular)
    {
        using Font? systemFont = SystemFonts.MessageBoxFont;
        string familyName = systemFont?.Name ?? FontFamily.GenericSansSerif.Name;
        return new Font(familyName, size, style, GraphicsUnit.Point);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            _mgr.DesktopSwitched -= OnDesktopSwitched;
            foreach (ContextMenuStrip menu in _ownedMenus.ToList())
                menu.Dispose();
            _ownedMenus.Clear();
            _tips.Dispose();
        }
        base.Dispose(disposing);
        if (disposing && _resourcesDisposed)
        {
            foreach (Font font in _ownedFonts.Values)
                font.Dispose();
            _ownedFonts.Clear();
        }
    }

    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
    }
}
