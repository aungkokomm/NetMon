using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NetMon;

/// <summary>
/// Compact, borderless, always-on-top bandwidth widget.
///
/// Architecture changes vs previous version:
///   • SpeedBar is no longer a WinForms Control — it is painted directly inside
///     MainForm.OnPaint, eliminating one HWND and all its message overhead.
///   • ResizeGrip is no longer a WinForms Control — resize is handled through a
///     unified mouse state-machine in the form itself.
///   • Controls remaining: GraphPanel, StatsPanel, TitleButton×2. (Down from 7.)
///   • Window position and size are saved to settings on close and restored on start.
/// </summary>
public sealed class MainForm : Form
{
    // ── layout constants ─────────────────────────────────────────────────
    private const int BP       = 2;    // border inset (px)
    private const int BarH     = 30;   // speed-bar height
    private const int StatsH   = 118;  // stats panel height
    private const int DefW     = 300;  // default width
    private const int DefGrH   = 70;   // default graph height
    private const int CornerR  = 10;   // rounded-corner radius
    private const int BtnSz    = 15;   // title-button size
    private const int GripSz   = 15;   // bottom-right resize hot-zone

    private static int PillH    => BP + BarH + BP;
    private static int DefFullH => BP + DefGrH + BarH + BP;

    // ── state ─────────────────────────────────────────────────────────────
    private AppSettings _settings;
    private Color       _borderCol;
    private bool        _compact;
    private bool        _expanded;
    private int         _savedFullH;
    private int         _graphH = DefGrH;     // current computed graph height

    // ── speed data (painted directly in OnPaint) ──────────────────────────
    private long _dlBps, _ulBps;

    // ── controls ──────────────────────────────────────────────────────────
    private readonly GraphPanel       _graph;
    private readonly StatsPanel       _statsPanel;
    private readonly TitleButton      _btnCompact;
    private readonly TitleButton      _btnClose;

    // ── services ──────────────────────────────────────────────────────────
    private readonly SpeedBarPainter  _sb;
    private readonly NetworkMonitor   _monitor;
    private readonly UsageStore       _store;
    private readonly NotifyIcon       _tray;
    private readonly ContextMenuStrip _trayMenu;

    // ── mouse drag / resize state-machine ─────────────────────────────────
    private enum DragMode { None, Moving, Resizing }
    private DragMode _drag;
    private Point    _dragOrigin;
    private Point    _dragFormOrigin;
    private Size     _dragOrigSize;

    // ── cached GDI for form-level painting ────────────────────────────────
    private SolidBrush _bgBrush;

    // ── constructor ───────────────────────────────────────────────────────

    public MainForm()
    {
        _settings    = AppSettings.Load();
        _store       = new UsageStore();
        _monitor     = new NetworkMonitor();
        _savedFullH  = DefFullH;
        _sb          = new SpeedBarPainter();
        _bgBrush     = new SolidBrush(_settings.BgColor);

        // ─ form ──────────────────────────────────────────────────────────
        Text            = "NetMon";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        MinimumSize     = new Size(180, 50);
        DoubleBuffered  = true;

        TopMost    = _settings.AlwaysOnTop;
        Opacity    = Math.Clamp(_settings.Opacity, 0.2, 1.0);
        BackColor  = _settings.BgColor;
        _borderCol = DarkenColor(_settings.BgColor, 0.22f);

        // Restore saved geometry or place bottom-right of primary screen
        RestoreGeometry();

        // ─ graph ─────────────────────────────────────────────────────────
        _graph = new GraphPanel
        {
            Location = new Point(BP, BP),
            Size     = new Size(DefW - BP * 2, DefGrH)
        };
        Controls.Add(_graph);

        // ─ stats panel (hidden) ───────────────────────────────────────────
        _statsPanel = new StatsPanel
        {
            Bounds    = new Rectangle(BP, BP + DefGrH + BarH, DefW - BP * 2, StatsH),
            BackColor = _settings.BgColor,
            Visible   = false
        };
        Controls.Add(_statsPanel);

        // ─ title buttons ─────────────────────────────────────────────────
        _btnCompact = new TitleButton("−", Color.FromArgb(70, 130, 200));
        _btnClose   = new TitleButton("×", Color.FromArgb(210, 50,  30));
        Controls.Add(_btnCompact);
        Controls.Add(_btnClose);

        // Tooltips
        var tips = new ToolTip { InitialDelay = 400, ShowAlways = true };
        tips.SetToolTip(_btnCompact, "Compact / pill mode  (double-click to expand)");
        tips.SetToolTip(_btnClose,   "Hide to tray  (right-click tray icon for menu)");

        // ─ mouse wiring ──────────────────────────────────────────────────
        // Attach unified handlers to child controls so drag and resize work
        // regardless of which control is under the cursor.
        foreach (var c in new Control[] { _graph, _statsPanel })
        {
            c.MouseDown        += (_, e) => HandleMouseDown(e);
            c.MouseMove        += (_, e) => HandleMouseMove(e);
            c.MouseUp          += (_, e) => HandleMouseUp(e);
            c.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) HandleDoubleClick(); };
        }

        _btnCompact.Click += (_, _) => ToggleCompact();
        _btnClose.Click   += (_, _) => ToggleWindow();

        // ─ tray ──────────────────────────────────────────────────────────
        _trayMenu = BuildTrayMenu();
        _tray     = new NotifyIcon
        {
            Icon             = BuildTrayIcon(),
            Text             = "NetMon",
            Visible          = true,
            ContextMenuStrip = _trayMenu
        };
        _tray.DoubleClick += (_, _) => ToggleWindow();

        // ─ monitor ───────────────────────────────────────────────────────
        _monitor.SpeedUpdated  += OnSpeedUpdated;
        _monitor.UsageRecorded += (_, e) => _store.Add(e.down, e.up);

        // ─ cleanup ───────────────────────────────────────────────────────
        FormClosing += OnFormClosing;

        UpdateLayout();
    }

    // ── geometry persistence ──────────────────────────────────────────────

    private void RestoreGeometry()
    {
        // Size
        int w = _settings.WinW > 0 ? Math.Max(_settings.WinW, 180) : DefW;
        int h = _settings.WinH > 0 ? Math.Max(_settings.WinH, PillH) : DefFullH;
        Size = new Size(w, h);

        // Position
        if (_settings.WinX != int.MinValue)
        {
            Location = ClampToWorkArea(new Point(_settings.WinX, _settings.WinY), Size);
        }
        else
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
            Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
        }
    }

    private static Point ClampToWorkArea(Point p, Size s)
    {
        var wa = Screen.GetWorkingArea(p);
        return new Point(
            Math.Clamp(p.X, wa.Left, Math.Max(wa.Left, wa.Right  - s.Width)),
            Math.Clamp(p.Y, wa.Top,  Math.Max(wa.Top,  wa.Bottom - s.Height)));
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Persist geometry
        _settings.WinX = Location.X;
        _settings.WinY = Location.Y;
        _settings.WinW = Width;
        _settings.WinH = _compact ? _savedFullH : (_expanded ? Height - StatsH : Height);
        _settings.Save();

        _tray.Visible = false;
        _tray.Dispose();
        _monitor.Dispose();
        _sb.Dispose();
        _bgBrush.Dispose();
    }

    // ── speed updates ─────────────────────────────────────────────────────

    private void OnSpeedUpdated(object? _, SpeedSample s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnSpeedUpdated(_, s)); return; }

        // Tray tooltip (always, even when hidden — cheap string op)
        string tip = $"NetMon\n↓ {FormatSpeed(s.DownloadBps)}\n↑ {FormatSpeed(s.UploadBps)}";
        if (tip.Length > 63) tip = tip[..63];
        if (_tray.Text != tip) _tray.Text = tip;

        if (!Visible) return;

        // Graph (only in full mode)
        if (!_compact)
        {
            _graph.AddSample(s.DownloadBps, s.UploadBps);

            // Today's total as overlay in graph
            var today   = _store.GetToday();
            var topInfo = $"Today: {FormatBytes(today.BytesReceived + today.BytesSent)}";
            if (_graph.TopInfo != topInfo) _graph.TopInfo = topInfo;
        }

        // Speed bar (invalidate only that rectangle — avoids full form repaint)
        bool changed = _dlBps != s.DownloadBps || _ulBps != s.UploadBps;
        _dlBps = s.DownloadBps;
        _ulBps = s.UploadBps;
        if (changed) Invalidate(SpeedBarRect);

        // Stats panel
        if (_statsPanel.Visible)
            _statsPanel.Refresh(_store.GetToday(), _store.GetThisMonth(),
                                _settings.MonthlyLimitBytes);
    }

    // ── mouse handling (unified — form + children) ────────────────────────

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); HandleMouseDown(e); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); HandleMouseMove(e); }
    protected override void OnMouseUp  (MouseEventArgs e) { base.OnMouseUp(e);   HandleMouseUp(e);   }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) HandleDoubleClick();
    }

    private void HandleMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var fp = PointToClient(Cursor.Position);       // form-space regardless of source control

        if (!_compact && GripRect.Contains(fp))
        {
            _drag        = DragMode.Resizing;
            _dragOrigin  = Cursor.Position;
            _dragOrigSize = Size;
            Capture      = true;                       // keep receiving moves outside form
        }
        else
        {
            _drag           = DragMode.Moving;
            _dragOrigin     = Cursor.Position;
            _dragFormOrigin = Location;
        }
    }

    private void HandleMouseMove(MouseEventArgs e)
    {
        var fp = PointToClient(Cursor.Position);

        // Cursor hint for resize zone
        Cursor = !_compact && GripRect.Contains(fp) ? Cursors.SizeNWSE : Cursors.Default;

        switch (_drag)
        {
            case DragMode.Moving when e.Button == MouseButtons.Left:
                Location = new Point(
                    _dragFormOrigin.X + Cursor.Position.X - _dragOrigin.X,
                    _dragFormOrigin.Y + Cursor.Position.Y - _dragOrigin.Y);
                break;

            case DragMode.Resizing when e.Button == MouseButtons.Left:
                var d = new Size(Cursor.Position.X - _dragOrigin.X,
                                 Cursor.Position.Y - _dragOrigin.Y);
                Size = new Size(
                    Math.Max(MinimumSize.Width,  _dragOrigSize.Width  + d.Width),
                    Math.Max(MinimumSize.Height, _dragOrigSize.Height + d.Height));
                break;
        }
    }

    private void HandleMouseUp(MouseEventArgs e)
    {
        if (_drag == DragMode.Resizing) { _drag = DragMode.None; Capture = false; }
        else _drag = DragMode.None;

        if (e.Button == MouseButtons.Right)
            _trayMenu?.Show(Cursor.Position);
    }

    private void HandleDoubleClick()
    {
        if (_compact) ToggleCompact();   // escape pill mode
        else          ToggleExpand();    // show / hide stats
    }

    // ── compact toggle ────────────────────────────────────────────────────

    private void ToggleCompact()
    {
        _compact = !_compact;

        if (_compact)
        {
            _savedFullH = _expanded ? Height - StatsH : Height;
            if (_expanded) { _statsPanel.Visible = false; _expanded = false; }

            _graph.Visible         = false;
            _btnClose.Visible      = false;
            _btnCompact.Size       = new Size(BtnSz, BtnSz);
            _btnCompact.Symbol     = "□";   // expand indicator
            MinimumSize            = new Size(120, PillH);
            Height                 = PillH;
        }
        else
        {
            _graph.Visible         = true;
            _btnClose.Visible      = true;
            _btnCompact.Symbol     = "−";   // compact indicator
            MinimumSize            = new Size(180, 50);
            Height                 = _savedFullH > 0 ? _savedFullH : DefFullH;
        }

        UpdateLayout();
    }

    // ── stats expand / collapse ───────────────────────────────────────────

    private void ToggleExpand()
    {
        if (_compact) return;

        if (!_expanded)
        {
            _statsPanel.Refresh(_store.GetToday(), _store.GetThisMonth(),
                                _settings.MonthlyLimitBytes);
            _statsPanel.Visible = true;
            _expanded           = true;
            Height             += StatsH;
        }
        else
        {
            _statsPanel.Visible = false;
            _expanded           = false;
            Height             -= StatsH;
        }
    }

    // ── layout ────────────────────────────────────────────────────────────

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated) return;
        if (!_compact)
            _savedFullH = _expanded ? Height - StatsH : Height;
        UpdateLayout();
    }

    private void UpdateLayout()
    {
        int w = Width, h = Height;

        if (_compact)
        {
            // Pill: speed bar fills entire form
            // Compact button floated right-centre of pill
            _btnCompact.Location = new Point(w - BtnSz - 4, (h - BtnSz) / 2);
            _btnCompact.BringToFront();
        }
        else
        {
            int statsH = _statsPanel?.Visible == true ? StatsH : 0;
            _graphH = Math.Max(20, h - BarH - statsH - BP * 2);

            _graph.Bounds      = new Rectangle(BP, BP, w - BP * 2, _graphH);
            if (_statsPanel != null)
                _statsPanel.Bounds = new Rectangle(BP, BP + _graphH + BarH, w - BP * 2, StatsH);

            // Title buttons: top-right of graph area (on top of graph)
            _btnClose.Location   = new Point(w - BP - BtnSz - 2,     BP + 2);
            _btnCompact.Location = new Point(w - BP - BtnSz * 2 - 5, BP + 2);
            _btnClose.BringToFront();
            _btnCompact.BringToFront();
        }

        if (IsHandleCreated)
        {
            UpdateRegion();
            Invalidate();
        }
    }

    // ── rects (computed from current state) ───────────────────────────────

    private Rectangle SpeedBarRect => _compact
        ? new Rectangle(0, 0, Width, Height)
        : new Rectangle(0, BP + _graphH, Width, BarH);

    private Rectangle GripRect =>
        new Rectangle(Width - GripSz - 1, Height - GripSz - 1, GripSz, GripSz);

    // ── paint ─────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g   = e.Graphics;
        var sbr = SpeedBarRect;

        // ── Speed bar background ──────────────────────────────────────────
        g.FillRectangle(_bgBrush, sbr);

        // ── Speed bar content ─────────────────────────────────────────────
        _sb.Paint(g, sbr, _dlBps, _ulBps);

        if (!_compact)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Separator: graph / speed bar
            using (var sep = new Pen(Color.FromArgb(130, _borderCol), 1f))
                g.DrawLine(sep, BP, sbr.Top, Width - BP - 1, sbr.Top);

            // Vertical divider in speed bar
            using (var div = new Pen(Color.FromArgb(100, _borderCol), 1f))
                g.DrawLine(div, Width / 2, sbr.Top + 5, Width / 2, sbr.Bottom - 5);

            // Separator: speed bar / stats
            if (_statsPanel?.Visible == true)
                using (var sep = new Pen(Color.FromArgb(130, _borderCol), 1f))
                    g.DrawLine(sep, BP, _statsPanel.Top, Width - BP - 1, _statsPanel.Top);

            // Resize grip dots (only visible when stats not covering the corner)
            if (!_expanded)
                PaintGripDots(g);
        }

        // ── DU Meter double-border ────────────────────────────────────────
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var hlPath = RoundedRect(2f, 2f, Width - 5f, Height - 5f, CornerR - 1.5f))
        using (var hlPen  = new Pen(Color.FromArgb(100, 255, 255, 255), 1f))
            g.DrawPath(hlPen, hlPath);

        using (var path = RoundedRect(0.5f, 0.5f, Width - 1.5f, Height - 1.5f, CornerR))
        using (var pen  = new Pen(_borderCol, 2.5f))
            g.DrawPath(pen, path);
    }

    private void PaintGripDots(Graphics g)
    {
        using var b = new SolidBrush(Color.FromArgb(80, _borderCol));
        int bx = Width - 3, by = Height - 3;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j <= i; j++)
                g.FillRectangle(b, bx - (i - j) * 4 - 2, by - j * 4 - 2, 2, 2);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateLayout();
        TryDwmRound();
    }

    // ── region + DWM ─────────────────────────────────────────────────────

    private void UpdateRegion()
    {
        using var path = RoundedRect(0, 0, Width, Height, CornerR);
        Region = new Region(path);
    }

    private void TryDwmRound()
    {
        try { int v = 2; DwmSetWindowAttribute(Handle, 33, ref v, 4); }
        catch { }
    }

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int v, int sz);

    // ── background ────────────────────────────────────────────────────────

    private void ApplyBackground(Color c)
    {
        _bgBrush.Dispose();
        _bgBrush   = new SolidBrush(c);
        BackColor  = c;
        _borderCol = DarkenColor(c, 0.22f);
        if (_statsPanel != null) _statsPanel.BackColor = c;
        Invalidate();
    }

    private static Color DarkenColor(Color c, float f) =>
        Color.FromArgb((int)(c.R * (1 - f)), (int)(c.G * (1 - f)), (int)(c.B * (1 - f)));

    // ── tray / window toggle ──────────────────────────────────────────────

    private void ToggleWindow()
    {
        if (Visible) Hide();
        else { Show(); BringToFront(); }
    }

    // ── context menu ─────────────────────────────────────────────────────

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Tahoma", 9f) };

        var miToggle = new ToolStripMenuItem("Hide Window");
        miToggle.Click += (_, _) => ToggleWindow();

        var miTop = new ToolStripMenuItem("Always on Top")
            { Checked = _settings.AlwaysOnTop, CheckOnClick = true };
        miTop.CheckedChanged += (_, _) =>
        {
            TopMost = miTop.Checked;
            _settings.AlwaysOnTop = miTop.Checked;
            _settings.Save();
        };

        var miStartup = new ToolStripMenuItem("Start with Windows")
            { Checked = AppSettings.IsStartupEnabled(), CheckOnClick = true };
        miStartup.CheckedChanged += (_, _) => _settings.SetStartup(miStartup.Checked);

        var miBg = new ToolStripMenuItem("Change Background…");
        miBg.Click += (_, _) =>
        {
            using var dlg = new ColorDialog
                { Color = _settings.BgColor, AnyColor = true, FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                _settings.BgColor = dlg.Color;
                _settings.Save();
                ApplyBackground(dlg.Color);
            }
        };

        var miTrans = new ToolStripMenuItem("Set Transparency…");
        miTrans.Click += (_, _) => ShowTransparencyDialog();

        var miLimit = new ToolStripMenuItem("Monthly Limit…");
        miLimit.Click += (_, _) => ShowLimitDialog();

        var miUsage = new ToolStripMenuItem("View Usage…");
        miUsage.Click += (_, _) => new UsageForm(_store).ShowDialog(this);

        var miExit = new ToolStripMenuItem("Exit");
        miExit.Click += (_, _) => BeginInvoke(Application.Exit);

        menu.Items.AddRange(new ToolStripItem[]
        {
            miToggle,
            new ToolStripSeparator(),
            miTop, miStartup, miBg, miTrans,
            new ToolStripSeparator(),
            miLimit, miUsage,
            new ToolStripSeparator(),
            miExit
        });

        menu.Opening += (_, _) =>
        {
            miToggle.Text   = Visible ? "Hide Window" : "Show Window";
            miStartup.Checked = AppSettings.IsStartupEnabled();
        };
        return menu;
    }

    // ── dialogs ───────────────────────────────────────────────────────────

    private void ShowTransparencyDialog()
    {
        using var frm = new Form
        {
            Text = "Transparency – NetMon", Size = new Size(300, 130),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false
        };
        var lbl    = new Label  { Text = "Window opacity:", AutoSize = true,
                                  Location = new Point(12, 14),
                                  Font = new Font("Tahoma", 8.5f) };
        var lblPct = new Label  { Text = $"{(int)(Opacity * 100)}%", AutoSize = true,
                                  Location = new Point(232, 14),
                                  Font = new Font("Tahoma", 8.5f) };
        var tb = new TrackBar
        {
            Minimum = 20, Maximum = 100, TickFrequency = 10,
            Value   = (int)(Opacity * 100),
            Bounds  = new Rectangle(12, 34, 266, 30)
        };
        var ok     = new Button { Text = "OK",     DialogResult = DialogResult.OK,
                                  Bounds = new Rectangle(100, 74, 80, 28),
                                  Font = new Font("Tahoma", 8.5f) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel,
                                  Bounds = new Rectangle(192, 74, 80, 28),
                                  Font = new Font("Tahoma", 8.5f) };

        double prev = Opacity;
        tb.ValueChanged += (_, _) => { Opacity = tb.Value / 100.0; lblPct.Text = $"{tb.Value}%"; };

        frm.AcceptButton = ok; frm.CancelButton = cancel;
        frm.Controls.AddRange(new Control[] { lbl, lblPct, tb, ok, cancel });

        if (frm.ShowDialog(this) == DialogResult.OK)
        {
            _settings.Opacity = tb.Value / 100.0;
            _settings.Save();
        }
        else Opacity = prev;
    }

    private void ShowLimitDialog()
    {
        using var frm = new Form
        {
            Text = "Monthly Limit – NetMon", Size = new Size(310, 140),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.CenterParent,
            MaximizeBox = false, MinimizeBox = false
        };
        var lbl = new Label { Text = "Monthly data limit in GB  (0 = disabled):",
                              AutoSize = false, Bounds = new Rectangle(12, 14, 280, 20),
                              Font = new Font("Tahoma", 8.5f) };
        var num = new NumericUpDown
        {
            Bounds = new Rectangle(12, 40, 140, 26),
            DecimalPlaces = 1, Increment = 10,
            Maximum = 100_000, Minimum = 0,
            Value   = (decimal)_settings.MonthlyLimitGB,
            Font    = new Font("Tahoma", 9f)
        };
        var ok     = new Button { Text = "OK",     Bounds = new Rectangle(115, 76, 80, 28),
                                  DialogResult = DialogResult.OK,     Font = num.Font };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(204, 76, 80, 28),
                                  DialogResult = DialogResult.Cancel, Font = num.Font };
        frm.AcceptButton = ok; frm.CancelButton = cancel;
        frm.Controls.AddRange(new Control[] { lbl, num, ok, cancel });

        if (frm.ShowDialog(this) == DialogResult.OK)
        {
            _settings.MonthlyLimitGB = (double)num.Value;
            _settings.Save();
        }
    }

    // ── tray icon ────────────────────────────────────────────────────────

    private static Icon BuildTrayIcon()
    {
        using var bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bgPath  = RoundedRect(1, 1, 14, 14, 3);
        using var bgBrush = new SolidBrush(Color.FromArgb(35, 105, 182));
        g.FillPath(bgBrush, bgPath);

        PointF[] pts =
        {
            new(2, 12), new(4, 9), new(6, 11),
            new(8,  6), new(10, 8), new(12, 5), new(14, 7)
        };
        using var lp = new Pen(Color.FromArgb(235, 255, 255, 255), 1.4f)
            { LineJoin = LineJoin.Round };
        g.DrawLines(lp, pts);

        using var dot = new SolidBrush(Color.FromArgb(95, 215, 70));
        g.FillEllipse(dot, 12, 2, 3, 3);

        var hIcon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    // ── GDI path helper ───────────────────────────────────────────────────

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x,         y,         r*2, r*2, 180, 90);
        p.AddArc(x+w-r*2,   y,         r*2, r*2, 270, 90);
        p.AddArc(x+w-r*2,   y+h-r*2,   r*2, r*2,   0, 90);
        p.AddArc(x,         y+h-r*2,   r*2, r*2,  90, 90);
        p.CloseFigure();
        return p;
    }

    // ── public format helpers (used by StatsPanel) ────────────────────────

    public static string FormatSpeed(long bps)
    {
        long bits = bps * 8;
        return bits switch
        {
            < 1_000L         => $"{bits} bps",
            < 1_000_000L     => $"{bits / 1_000.0:F1} kbps",
            < 1_000_000_000L => $"{bits / 1_000_000.0:F2} Mbps",
            _                => $"{bits / 1_000_000_000.0:F2} Gbps"
        };
    }

    public static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1_024L             => $"{bytes} B",
            < 1_048_576L         => $"{bytes / 1_024.0:F1} KB",
            < 1_073_741_824L     => $"{bytes / 1_048_576.0:F1} MB",
            < 1_099_511_627_776L => $"{bytes / 1_073_741_824.0:F2} GB",
            _                    => $"{bytes / 1_099_511_627_776.0:F2} TB"
        };
    }

    // ── nested: speed bar painter (NOT a Control — no HWND) ───────────────

    private sealed class SpeedBarPainter : IDisposable
    {
        private readonly Font       _font    = new("Tahoma", 9.5f);
        private readonly SolidBrush _dlBrush = new(Color.FromArgb(0,   155,  40));
        private readonly SolidBrush _ulBrush = new(Color.FromArgb(205,  45,  10));
        private readonly SolidBrush _white   = new(Color.White);

        public void Paint(Graphics g, Rectangle r, long dlBps, long ulBps)
        {
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int half = r.Width / 2;
            PaintHalf(g, _dlBrush, FormatSpeed(dlBps), isDown: true,
                      r.X,        r.Y, half,          r.Height);
            PaintHalf(g, _ulBrush, FormatSpeed(ulBps), isDown: false,
                      r.X + half, r.Y, r.Width - half, r.Height);
        }

        private void PaintHalf(Graphics g, SolidBrush brush, string text,
                                bool isDown, int sx, int sy, int sw, int sh)
        {
            int cy   = sy + sh / 2;
            int bdgH = Math.Min(sh - 4, 20);
            int bdgW = bdgH + 2;
            int bdgX = sx + 7;
            int bdgY = cy - bdgH / 2;

            using var path = BadgePath(bdgX, bdgY, bdgW, bdgH, 3);
            g.FillPath(brush, path);

            DrawArrow(g, _white, bdgX + bdgW / 2, cy, isDown);

            float tx = bdgX + bdgW + 6;
            float tw = sw - (tx - sx) - 4;
            using var sf = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(text, _font, brush,
                new RectangleF(tx, sy, Math.Max(0, tw), sh), sf);
        }

        private static GraphicsPath BadgePath(int x, int y, int w, int h, int r)
        {
            var p = new GraphicsPath();
            p.AddArc(x,       y,       r*2, r*2, 180, 90);
            p.AddArc(x+w-r*2, y,       r*2, r*2, 270, 90);
            p.AddArc(x+w-r*2, y+h-r*2, r*2, r*2,   0, 90);
            p.AddArc(x,       y+h-r*2, r*2, r*2,  90, 90);
            p.CloseFigure();
            return p;
        }

        private static void DrawArrow(Graphics g, Brush brush, int cx, int cy, bool down)
        {
            const int sw = 2, sh = 5, hw = 5, hh = 4;
            PointF[] pts = down
                ? new PointF[]
                {
                    new(cx-sw, cy-sh), new(cx+sw, cy-sh),
                    new(cx+sw, cy),    new(cx+hw, cy),
                    new(cx,    cy+hh), new(cx-hw, cy),
                    new(cx-sw, cy)
                }
                : new PointF[]
                {
                    new(cx-sw, cy+sh), new(cx+sw, cy+sh),
                    new(cx+sw, cy),    new(cx+hw, cy),
                    new(cx,    cy-hh), new(cx-hw, cy),
                    new(cx-sw, cy)
                };
            g.FillPolygon(brush, pts);
        }

        public void Dispose()
        {
            _font.Dispose(); _dlBrush.Dispose();
            _ulBrush.Dispose(); _white.Dispose();
        }
    }

    // ── nested: title button (needs HWND for hover tracking) ─────────────

    private sealed class TitleButton : Control
    {
        private bool   _hov;
        private string _sym;
        private readonly Color  _hoverCol;
        private readonly Font   _font = new("Tahoma", 9f, FontStyle.Bold);

        public string Symbol { get => _sym; set { _sym = value; Invalidate(); } }

        public TitleButton(string sym, Color hoverCol)
        {
            _sym      = sym;
            _hoverCol = hoverCol;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint            |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size      = new Size(BtnSz, BtnSz);
            BackColor = Color.Transparent;
            Cursor    = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hov = true;  Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { _hov = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? BackColor);
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            if (_hov)
            {
                using var hb = new SolidBrush(Color.FromArgb(200, _hoverCol));
                g.FillEllipse(hb, 1, 1, Width - 2, Height - 2);
            }

            using var sb = new SolidBrush(
                _hov ? Color.White : Color.FromArgb(160, 35, 78, 128));
            using var sf = new StringFormat
                { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(_sym, _font, sb, new RectangleF(0, 0, Width, Height), sf);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _font.Dispose();
            base.Dispose(disposing);
        }
    }
}
