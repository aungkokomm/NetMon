using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NetMon;

/// <summary>
/// Compact, borderless, always-on-top bandwidth widget.
///
/// Architecture:
///   • Speed bar is painted directly in OnPaint — no child HWND.
///   • Resize grip is a mouse-state-machine hot-zone — no child HWND.
///   • Controls remaining: GraphPanel, StatsPanel, TitleButton×2.
///   • Window geometry, colour, opacity persist across launches.
///   • Single-instance via named Mutex + broadcast WM_ShowNetMon.
///   • Optional global hotkey (Win+Shift+N) toggles visibility.
///   • Drag edge-snap within 12 px of screen work-area.
/// </summary>
public sealed class MainForm : Form
{
    // ── layout constants ─────────────────────────────────────────────────
    private const int BorderPad          = 2;
    private const int BarHeight          = 30;
    private const int StatsHeight        = 118;
    private const int DefaultWidth       = 300;
    private const int DefaultGraphHeight = 70;
    private const int CornerRadius       = 12;
    private const int ButtonSize         = 16;
    private const int GripSize           = 15;
    private const int SnapDistance       = 12;

    private static int PillHeight        => BorderPad + BarHeight + BorderPad;
    private static int DefaultFullHeight => BorderPad + DefaultGraphHeight + BarHeight + BorderPad;

    // ── DWM / Win32 named constants ───────────────────────────────────────
    private const int  DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int  DWMWA_SYSTEMBACKDROP_TYPE      = 38;     // Win11 22621+
    private const int  DWMWCP_ROUND                   = 2;
    private const int  DWMSBT_MAINWINDOW              = 2;      // Mica
    private const int  WM_HOTKEY                      = 0x0312;
    private const uint MOD_SHIFT                      = 0x0004;
    private const uint MOD_WIN                        = 0x0008;
    private const int  HOTKEY_ID                      = 0xB17F;
    private const int  VK_N                           = 0x4E;

    /// <summary>Cross-process signal — running instance shows itself when received.</summary>
    public static readonly uint WmShowNetMon = RegisterWindowMessage(
        "NetMon_ShowMe_{8F4E2A1B-C3D5-4E6F-A7B8-9C0D1E2F3A4B}");

    // ── state ─────────────────────────────────────────────────────────────
    private AppSettings _settings;
    private Color       _borderCol;
    private bool        _compact;
    private bool        _expanded;
    private int         _savedFullH;
    private int         _graphH = DefaultGraphHeight;
    private bool        _firstShow       = true;
    private bool        _hotkeyRegistered;

    // Cached today-usage to avoid double store lookup per speed tick
    private DayUsage _cachedToday   = new();
    private DateTime _cachedTodayAt = DateTime.MinValue;

    // ── speed data (painted directly in OnPaint) ──────────────────────────
    private long _dlBps, _ulBps;

    // ── controls ──────────────────────────────────────────────────────────
    private readonly GraphPanel   _graph;
    private readonly StatsPanel   _statsPanel;
    private readonly TitleButton  _btnCompact;
    private readonly TitleButton  _btnClose;

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

    // ── cached GDI for form-level painting (rebuilt on size/bg change) ────
    private LinearGradientBrush? _bgGradBrush;
    private GraphicsPath?        _outerPath;
    private GraphicsPath?        _highlightPath;
    private Size                 _lastPaintedSize;
    private bool                 _resizing;

    // ── constructor ───────────────────────────────────────────────────────

    public MainForm()
    {
        _settings    = AppSettings.Load();
        _store       = new UsageStore();
        _monitor     = new NetworkMonitor();
        _savedFullH  = DefaultFullHeight;
        _sb          = new SpeedBarPainter();

        // ─ form ──────────────────────────────────────────────────────────
        Text            = "NetMon";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        MinimumSize     = new Size(180, 50);
        DoubleBuffered  = true;
        KeyPreview      = true;

        // Smooth resize: full repaint, no flicker
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.ResizeRedraw, true);
        UpdateStyles();

        TopMost    = _settings.AlwaysOnTop;
        Opacity    = Math.Clamp(_settings.Opacity, 0.2, 1.0);
        BackColor  = _settings.BgColor;
        _borderCol = DarkenColor(_settings.BgColor, 0.22f);

        // App icon — visible in Alt+Tab and taskbar switcher
        var appIcon = LoadAppIcon();
        if (appIcon != null) Icon = appIcon;

        RestoreGeometry();

        // ─ graph ─────────────────────────────────────────────────────────
        _graph = new GraphPanel
        {
            Location  = new Point(BorderPad, BorderPad),
            Size      = new Size(DefaultWidth - BorderPad * 2, DefaultGraphHeight),
            FillAlpha = _settings.GraphFillAlpha
        };
        Controls.Add(_graph);

        // ─ stats panel (hidden) ───────────────────────────────────────────
        _statsPanel = new StatsPanel
        {
            Bounds    = new Rectangle(BorderPad, BorderPad + DefaultGraphHeight + BarHeight,
                                      DefaultWidth - BorderPad * 2, StatsHeight),
            BackColor = _settings.BgColor,
            Visible   = false
        };
        Controls.Add(_statsPanel);

        // ─ title buttons ─────────────────────────────────────────────────
        _btnCompact = new TitleButton("−", Color.FromArgb(70, 130, 200));
        _btnClose   = new TitleButton("×", Color.FromArgb(210, 50,  30));
        Controls.Add(_btnCompact);
        Controls.Add(_btnClose);

        var tips = new ToolTip { InitialDelay = 400, ShowAlways = true };
        tips.SetToolTip(_btnCompact, "Compact / pill mode  (double-click to expand)");
        tips.SetToolTip(_btnClose,   "Hide to tray  (right-click tray icon for menu)");

        // ─ mouse wiring on children ──────────────────────────────────────
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
            Icon             = appIcon ?? BuildTrayIcon(),
            Text             = "NetMon",
            Visible          = true,
            ContextMenuStrip = _trayMenu
        };
        _tray.DoubleClick += (_, _) => ToggleWindow();

        // ─ monitor ───────────────────────────────────────────────────────
        _monitor.SpeedUpdated  += OnSpeedUpdated;
        _monitor.UsageRecorded += (_, e) =>
        {
            _store.Add(e.down, e.up);
            _cachedTodayAt = DateTime.MinValue;   // invalidate cache
        };

        FormClosing += OnFormClosing;

        UpdateLayout();
    }

    // ── start-minimized handling ─────────────────────────────────────────

    protected override void SetVisibleCore(bool value)
    {
        if (_firstShow && value && _settings.StartMinimized)
        {
            _firstShow = false;
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
            return;
        }
        _firstShow = false;
        base.SetVisibleCore(value);
    }

    // ── geometry persistence ──────────────────────────────────────────────

    private void RestoreGeometry()
    {
        int w = _settings.WinW > 0 ? Math.Max(_settings.WinW, 180) : DefaultWidth;
        int h = _settings.WinH > 0 ? Math.Max(_settings.WinH, PillHeight) : DefaultFullHeight;
        Size = new Size(w, h);

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

    private Point SnapToEdges(Point p, Size s)
    {
        var wa = Screen.GetWorkingArea(new Rectangle(p, s));
        int x  = p.X, y = p.Y;
        if (Math.Abs(x - wa.Left)              < SnapDistance) x = wa.Left;
        if (Math.Abs((x + s.Width) - wa.Right) < SnapDistance) x = wa.Right  - s.Width;
        if (Math.Abs(y - wa.Top)               < SnapDistance) y = wa.Top;
        if (Math.Abs((y + s.Height) - wa.Bottom)< SnapDistance) y = wa.Bottom - s.Height;
        return new Point(x, y);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _settings.WinX = Location.X;
        _settings.WinY = Location.Y;
        _settings.WinW = Width;
        _settings.WinH = _compact ? _savedFullH : (_expanded ? Height - StatsHeight : Height);
        _settings.Save();

        UnregisterGlobalHotkey();

        _tray.Visible = false;
        _tray.Dispose();
        _monitor.Dispose();
        _store.Dispose();
        _sb.Dispose();
        _bgGradBrush?.Dispose();
        _outerPath?.Dispose();
        _highlightPath?.Dispose();
    }

    // ── speed updates ─────────────────────────────────────────────────────

    private void OnSpeedUpdated(object? _, SpeedSample s)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => OnSpeedUpdated(_, s)); return; }

        string tip = $"NetMon\n↓ {FormatSpeed(s.DownloadBps)}\n↑ {FormatSpeed(s.UploadBps)}";
        if (tip.Length > 63) tip = tip[..63];
        if (_tray.Text != tip) _tray.Text = tip;

        if (!Visible) return;

        // Refresh cached "today" at most once per 2 s
        if ((DateTime.UtcNow - _cachedTodayAt).TotalSeconds > 2)
        {
            _cachedToday   = _store.GetToday();
            _cachedTodayAt = DateTime.UtcNow;
        }

        if (!_compact)
        {
            _graph.AddSample(s.DownloadBps, s.UploadBps);
            var topInfo = $"Today: {FormatBytes(_cachedToday.BytesReceived + _cachedToday.BytesSent)}";
            if (_graph.TopInfo != topInfo) _graph.TopInfo = topInfo;
        }

        bool changed = _dlBps != s.DownloadBps || _ulBps != s.UploadBps;
        _dlBps = s.DownloadBps;
        _ulBps = s.UploadBps;
        if (changed) Invalidate(SpeedBarRect);

        if (_statsPanel.Visible)
            _statsPanel.Refresh(_cachedToday, _store.GetThisMonth(),
                                _settings.MonthlyLimitBytes);
    }

    // ── mouse handling ────────────────────────────────────────────────────

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
        var fp = PointToClient(Cursor.Position);

        if (!_compact && GripRect.Contains(fp))
        {
            _drag         = DragMode.Resizing;
            _dragOrigin   = Cursor.Position;
            _dragOrigSize = Size;
            Capture       = true;
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
        Cursor = !_compact && GripRect.Contains(fp) ? Cursors.SizeNWSE : Cursors.Default;

        switch (_drag)
        {
            case DragMode.Moving when e.Button == MouseButtons.Left:
                var target = new Point(
                    _dragFormOrigin.X + Cursor.Position.X - _dragOrigin.X,
                    _dragFormOrigin.Y + Cursor.Position.Y - _dragOrigin.Y);
                Location = SnapToEdges(target, Size);
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
        if (_compact) ToggleCompact();
        else          ToggleExpand();
    }

    // ── keyboard shortcuts ────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        if (e.KeyCode == Keys.Escape)
        {
            ToggleWindow();
            e.Handled = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.H)
        {
            ToggleCompact();
            e.Handled = true;
            return;
        }

        // Arrow nudge (1 px), Shift+Arrow nudges 10 px
        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
        {
            int step = e.Shift ? 10 : 1;
            int dx = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
            int dy = e.KeyCode == Keys.Up   ? -step : e.KeyCode == Keys.Down  ? step : 0;
            Location = ClampToWorkArea(new Point(Location.X + dx, Location.Y + dy), Size);
            e.Handled = true;
        }
    }

    // ── compact toggle ────────────────────────────────────────────────────

    private void ToggleCompact()
    {
        _compact = !_compact;

        if (_compact)
        {
            _savedFullH = _expanded ? Height - StatsHeight : Height;
            if (_expanded) { _statsPanel.Visible = false; _expanded = false; }

            _graph.Visible     = false;
            _btnClose.Visible  = false;
            _btnCompact.Size   = new Size(ButtonSize, ButtonSize);
            _btnCompact.Symbol = "□";
            MinimumSize        = new Size(120, PillHeight);
            Height             = PillHeight;
        }
        else
        {
            _graph.Visible     = true;
            _btnClose.Visible  = true;
            _btnCompact.Symbol = "−";
            MinimumSize        = new Size(180, 50);
            Height             = _savedFullH > 0 ? _savedFullH : DefaultFullHeight;
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
            Height             += StatsHeight;
        }
        else
        {
            _statsPanel.Visible = false;
            _expanded           = false;
            Height             -= StatsHeight;
        }
    }

    // ── layout ────────────────────────────────────────────────────────────

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        InvalidateGdiCache();
        if (!IsHandleCreated) return;
        if (!_compact)
            _savedFullH = _expanded ? Height - StatsHeight : Height;
        UpdateLayout();
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        _resizing = true;
        base.OnResizeBegin(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        _resizing = false;
        base.OnResizeEnd(e);
        InvalidateGdiCache();
        Invalidate();
    }

    // Rebuild gradient + paths on size or bg-color change
    private void EnsureGdiCache()
    {
        if (_bgGradBrush != null && Size == _lastPaintedSize) return;
        _bgGradBrush?.Dispose();
        _outerPath?.Dispose();
        _highlightPath?.Dispose();

        var rect = new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height));
        var top    = LightenColor(_settings.BgColor, 0.10f);
        var bottom = DarkenColor (_settings.BgColor, 0.08f);
        _bgGradBrush   = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical);
        _outerPath     = RoundedRect(0.5f, 0.5f, Width - 1.5f, Height - 1.5f, CornerRadius);
        _highlightPath = RoundedRect(2f,   2f,   Width - 5f,   Height - 5f,   CornerRadius - 1.5f);
        _lastPaintedSize = Size;
    }

    private void InvalidateGdiCache()
    {
        _bgGradBrush?.Dispose();   _bgGradBrush   = null;
        _outerPath?.Dispose();     _outerPath     = null;
        _highlightPath?.Dispose(); _highlightPath = null;
    }

    private void UpdateLayout()
    {
        int w = Width, h = Height;

        if (_compact)
        {
            _btnCompact.Location = new Point(w - ButtonSize - 4, (h - ButtonSize) / 2);
            _btnCompact.BringToFront();
        }
        else
        {
            int statsH = _statsPanel?.Visible == true ? StatsHeight : 0;
            _graphH = Math.Max(20, h - BarHeight - statsH - BorderPad * 2);

            _graph.Bounds = new Rectangle(BorderPad, BorderPad, w - BorderPad * 2, _graphH);
            if (_statsPanel != null)
                _statsPanel.Bounds = new Rectangle(BorderPad, BorderPad + _graphH + BarHeight,
                                                    w - BorderPad * 2, StatsHeight);

            _btnClose.Location   = new Point(w - BorderPad - ButtonSize - 2,        BorderPad + 2);
            _btnCompact.Location = new Point(w - BorderPad - ButtonSize * 2 - 5,    BorderPad + 2);
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
        : new Rectangle(0, BorderPad + _graphH, Width, BarHeight);

    private Rectangle GripRect =>
        new Rectangle(Width - GripSize - 1, Height - GripSize - 1, GripSize, GripSize);

    // ── paint ─────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        EnsureGdiCache();
        var g   = e.Graphics;
        var sbr = SpeedBarRect;

        // Cheap path while live-resizing — no AA, no ClearType
        bool fast = _resizing;
        g.SmoothingMode     = fast ? SmoothingMode.None         : SmoothingMode.AntiAlias;
        g.TextRenderingHint = fast ? TextRenderingHint.SystemDefault
                                   : TextRenderingHint.ClearTypeGridFit;

        g.FillRectangle(_bgGradBrush!, sbr);
        _sb.Paint(g, sbr, _dlBps, _ulBps, fast);

        if (!_compact)
        {
            using (var sep = new Pen(Color.FromArgb(130, _borderCol), 1f))
                g.DrawLine(sep, BorderPad, sbr.Top, Width - BorderPad - 1, sbr.Top);

            using (var div = new Pen(Color.FromArgb(100, _borderCol), 1f))
                g.DrawLine(div, Width / 2, sbr.Top + 5, Width / 2, sbr.Bottom - 5);

            if (_statsPanel?.Visible == true)
                using (var sep = new Pen(Color.FromArgb(130, _borderCol), 1f))
                    g.DrawLine(sep, BorderPad, _statsPanel.Top, Width - BorderPad - 1, _statsPanel.Top);

            if (!_expanded && !fast)
                PaintGripDots(g);
        }

        // Border + highlight — skip in fast path (avoid GDI churn)
        if (!fast)
        {
            using var hlPen = new Pen(Color.FromArgb(100, 255, 255, 255), 1f);
            g.DrawPath(hlPen, _highlightPath!);
            using var pen   = new Pen(_borderCol, 2.5f);
            g.DrawPath(pen, _outerPath!);
        }
        else
        {
            using var pen = new Pen(_borderCol, 2f);
            g.DrawPath(pen, _outerPath!);
        }
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
        if (_settings.HotKeyEnabled) RegisterGlobalHotkey();
    }

    // ── global hotkey + WndProc ──────────────────────────────────────────

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
        {
            ToggleWindow();
            return;
        }
        if (m.Msg == (int)WmShowNetMon)
        {
            if (!Visible) Show();
            BringToFront();
            Activate();
            return;
        }
        base.WndProc(ref m);
    }

    private void RegisterGlobalHotkey()
    {
        if (_hotkeyRegistered || !IsHandleCreated) return;
        try
        {
            if (RegisterHotKey(Handle, HOTKEY_ID, MOD_WIN | MOD_SHIFT, VK_N))
                _hotkeyRegistered = true;
            else
                Debug.WriteLine("RegisterHotKey failed — another app may own Win+Shift+N");
        }
        catch (Exception ex) { Debug.WriteLine($"RegisterGlobalHotkey: {ex.Message}"); }
    }

    private void UnregisterGlobalHotkey()
    {
        if (!_hotkeyRegistered) return;
        try { UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
        _hotkeyRegistered = false;
    }

    // ── region + DWM ─────────────────────────────────────────────────────

    private void UpdateRegion()
    {
        using var path = RoundedRect(0, 0, Width, Height, CornerRadius);
        Region = new Region(path);
    }

    private void TryDwmRound()
    {
        try
        {
            int v = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, 4);
        }
        catch { }
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    [DllImport("dwmapi.dll", PreserveSig = false)]
    private static extern void DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int v, int sz);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr h);

    // ── app icon loader (embedded resource) ──────────────────────────────

    private static Icon? LoadAppIcon()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("NetMon.ico");
            if (stream == null) return null;
            return new Icon(stream);
        }
        catch (Exception ex) { Debug.WriteLine($"LoadAppIcon: {ex.Message}"); return null; }
    }

    // ── dialog helper: prevents modals from hiding behind TopMost widget ─

    private DialogResult ShowDialogSafe(Form dlg)
    {
        bool wasTopMost = TopMost;
        if (wasTopMost) TopMost = false;
        dlg.ShowInTaskbar = false;
        dlg.TopMost       = wasTopMost;   // match widget TopMost state
        try
        {
            return dlg.ShowDialog(this);
        }
        finally
        {
            if (wasTopMost) TopMost = true;
        }
    }

    /// <summary>Overload for the built-in common dialogs (ColorDialog etc.) which have no TopMost property.</summary>
    private DialogResult ShowDialogSafe(CommonDialog dlg, IWin32Window owner)
    {
        bool wasTopMost = TopMost;
        if (wasTopMost) TopMost = false;
        try { return dlg.ShowDialog(owner); }
        finally { if (wasTopMost) TopMost = true; }
    }

    // ── background ────────────────────────────────────────────────────────

    private void ApplyBackground(Color c)
    {
        BackColor  = c;
        _borderCol = DarkenColor(c, 0.22f);
        if (_statsPanel != null) _statsPanel.BackColor = c;
        InvalidateGdiCache();
        Invalidate();
    }

    private static Color DarkenColor(Color c, float f) =>
        Color.FromArgb((int)(c.R * (1 - f)), (int)(c.G * (1 - f)), (int)(c.B * (1 - f)));

    private static Color LightenColor(Color c, float f) =>
        Color.FromArgb(
            (int)Math.Min(255, c.R + (255 - c.R) * f),
            (int)Math.Min(255, c.G + (255 - c.G) * f),
            (int)Math.Min(255, c.B + (255 - c.B) * f));

    // ── tray / window toggle ──────────────────────────────────────────────

    private void ToggleWindow()
    {
        if (Visible) Hide();
        else { Show(); BringToFront(); Activate(); }
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

        var miStartMin = new ToolStripMenuItem("Start Minimized to Tray")
            { Checked = _settings.StartMinimized, CheckOnClick = true };
        miStartMin.CheckedChanged += (_, _) =>
        {
            _settings.StartMinimized = miStartMin.Checked;
            _settings.Save();
        };

        var miHotkey = new ToolStripMenuItem("Global Hotkey (Win+Shift+N)")
            { Checked = _settings.HotKeyEnabled, CheckOnClick = true };
        miHotkey.CheckedChanged += (_, _) =>
        {
            _settings.HotKeyEnabled = miHotkey.Checked;
            _settings.Save();
            if (miHotkey.Checked) RegisterGlobalHotkey();
            else                  UnregisterGlobalHotkey();
        };

        var miBg = new ToolStripMenuItem("Change Background…");
        miBg.Click += (_, _) => ShowBgDialog();

        var miTrans = new ToolStripMenuItem("Set Transparency…");
        miTrans.Click += (_, _) => ShowTransparencyDialog();

        var miLimit = new ToolStripMenuItem("Monthly Limit…");
        miLimit.Click += (_, _) => ShowLimitDialog();

        var miUsage = new ToolStripMenuItem("View Usage…");
        miUsage.Click += (_, _) => { using var f = new UsageForm(_store); ShowDialogSafe(f); };

        var miAbout = new ToolStripMenuItem("About NetMon…");
        miAbout.Click += (_, _) => ShowAboutDialog();

        var miExit = new ToolStripMenuItem("Exit");
        miExit.Click += (_, _) => BeginInvoke(Application.Exit);

        menu.Items.AddRange(new ToolStripItem[]
        {
            miToggle,
            new ToolStripSeparator(),
            miTop, miStartup, miStartMin, miHotkey, miBg, miTrans,
            new ToolStripSeparator(),
            miLimit, miUsage,
            new ToolStripSeparator(),
            miAbout, miExit
        });

        menu.Opening += (_, _) =>
        {
            miToggle.Text     = Visible ? "Hide Window" : "Show Window";
            miStartup.Checked = AppSettings.IsStartupEnabled();
        };
        return menu;
    }

    // ── dialogs ───────────────────────────────────────────────────────────

    private void ShowBgDialog()
    {
        using var frm = new Form
        {
            Text            = "Background – NetMon",
            ClientSize      = new Size(340, 164),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.CenterParent,
            MaximizeBox     = false, MinimizeBox = false,
            ShowInTaskbar   = false,
            Font            = new Font("Segoe UI", 9f),
            BackColor       = Color.White
        };
        var _bgIcon = LoadAppIcon();
        if (_bgIcon != null) frm.Icon = _bgIcon;

        var lbl = new Label
        {
            Text = "Pick a preset or choose a custom colour:",
            AutoSize = true, Location = new Point(12, 12)
        };

        Color[] presets =
        {
            Color.FromArgb(108, 182, 216),   // default cyan-blue
            Color.FromArgb( 55, 125, 185),   // steel blue
            Color.FromArgb( 42,  63,  95),   // navy
            Color.FromArgb( 36,  36,  40),   // dark slate
            Color.FromArgb(180, 100, 160),   // orchid
            Color.FromArgb(200, 120,  60),   // amber
            Color.FromArgb( 85, 160, 100),   // olive green
            Color.FromArgb(230, 230, 230)    // light gray
        };

        int sx = 12, sy = 40, sw = 34, sh = 34, gap = 4;
        Color chosen = _settings.BgColor;
        var swatches = new List<Panel>();

        for (int i = 0; i < presets.Length; i++)
        {
            var p = new Panel
            {
                BackColor = presets[i],
                Size      = new Size(sw, sh),
                Location  = new Point(sx + i * (sw + gap), sy),
                Cursor    = Cursors.Hand,
                BorderStyle = BorderStyle.FixedSingle,
                Tag       = presets[i]
            };
            int iLocal = i;
            p.Click += (_, _) => { chosen = presets[iLocal]; frm.DialogResult = DialogResult.OK; frm.Close(); };
            swatches.Add(p);
            frm.Controls.Add(p);
        }

        var btnMore = new Button
        {
            Text     = "More…",
            Bounds   = new Rectangle(12, 94, 100, 28),
            FlatStyle = FlatStyle.System
        };
        btnMore.Click += (_, _) =>
        {
            using var dlg = new ColorDialog
                { Color = _settings.BgColor, AnyColor = true, FullOpen = true };
            // Owner = frm which already matches widget's TopMost state
            if (dlg.ShowDialog(frm) == DialogResult.OK)
            {
                chosen = dlg.Color;
                frm.DialogResult = DialogResult.OK;
                frm.Close();
            }
        };

        var btnCancel = new Button
        {
            Text = "Cancel", Bounds = new Rectangle(230, 94, 80, 28),
            DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.System
        };

        frm.Controls.Add(lbl);
        frm.Controls.Add(btnMore);
        frm.Controls.Add(btnCancel);
        frm.CancelButton = btnCancel;

        if (ShowDialogSafe(frm) == DialogResult.OK)
        {
            _settings.BgColor = chosen;
            _settings.Save();
            ApplyBackground(chosen);
        }
    }

    private void ShowTransparencyDialog()
    {
        const int W = 380, H = 170;
        const int Pad = 16, BtnW = 92, BtnH = 32, BtnGap = 8;

        using var frm = new Form
        {
            Text            = "Transparency – NetMon",
            ClientSize      = new Size(W, H),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.CenterParent,
            MaximizeBox     = false, MinimizeBox = false,
            ShowInTaskbar   = false,
            Font            = new Font("Segoe UI", 9.5f),
            BackColor       = Color.White
        };
        var appIcon = LoadAppIcon();
        if (appIcon != null) frm.Icon = appIcon;

        var lbl = new Label
        {
            Text     = "Window opacity",
            AutoSize = true,
            Location = new Point(Pad, 14),
            Font     = new Font("Segoe UI Semibold", 10f)
        };
        var lblPct = new Label
        {
            Text      = $"{(int)(Opacity * 100)}%",
            AutoSize  = true,
            Font      = new Font("Segoe UI Semibold", 11f),
            ForeColor = Color.FromArgb(35, 105, 182)
        };
        // Right-align the percentage label
        lblPct.Location = new Point(W - Pad - 60, 13);

        var tb = new TrackBar
        {
            Minimum       = 20, Maximum = 100, TickFrequency = 10,
            Value         = (int)(Opacity * 100),
            Bounds        = new Rectangle(Pad, 44, W - Pad * 2, 40)
        };

        double prev = Opacity;
        tb.ValueChanged += (_, _) =>
        {
            Opacity       = tb.Value / 100.0;
            lblPct.Text   = $"{tb.Value}%";
        };

        // Footer button row — Apply (left), then OK / Cancel (right)
        int bY = H - BtnH - 16;

        var apply = new Button
        {
            Text      = "Apply",
            Bounds    = new Rectangle(Pad, bY, BtnW, BtnH),
            FlatStyle = FlatStyle.System
        };
        apply.Click += (_, _) =>
        {
            _settings.Opacity = tb.Value / 100.0;
            _settings.Save();
            prev = _settings.Opacity;    // committed — Cancel now restores this
        };

        var cancel = new Button
        {
            Text         = "Cancel",
            Bounds       = new Rectangle(W - Pad - BtnW, bY, BtnW, BtnH),
            DialogResult = DialogResult.Cancel,
            FlatStyle    = FlatStyle.System
        };
        var ok = new Button
        {
            Text         = "OK",
            Bounds       = new Rectangle(W - Pad - BtnW * 2 - BtnGap, bY, BtnW, BtnH),
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.System
        };

        frm.AcceptButton = ok; frm.CancelButton = cancel;
        frm.Controls.AddRange(new Control[] { lbl, lblPct, tb, apply, ok, cancel });

        if (ShowDialogSafe(frm) == DialogResult.OK)
        {
            _settings.Opacity = tb.Value / 100.0;
            _settings.Save();
        }
        else Opacity = prev;
    }

    private void ShowLimitDialog()
    {
        const int W = 420, H = 200;
        const int Pad = 18, BtnW = 100, BtnH = 32, BtnGap = 8;

        using var frm = new Form
        {
            Text            = "Monthly Limit – NetMon",
            ClientSize      = new Size(W, H),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.CenterParent,
            MaximizeBox     = false, MinimizeBox = false,
            ShowInTaskbar   = false,
            Font            = new Font("Segoe UI", 9.5f),
            BackColor       = Color.White
        };
        var appIcon = LoadAppIcon();
        if (appIcon != null) frm.Icon = appIcon;

        var hdr = new Label
        {
            Text     = "Monthly data limit",
            Font     = new Font("Segoe UI Semibold", 11.5f),
            AutoSize = true,
            Location = new Point(Pad, 16)
        };
        var sub = new Label
        {
            Text      = "Sets the monthly data cap shown in the stats panel. " +
                        "Enter 0 to disable.",
            Bounds    = new Rectangle(Pad, 44, W - Pad * 2, 36),
            ForeColor = Color.FromArgb(100, 100, 100)
        };
        var num = new NumericUpDown
        {
            Bounds        = new Rectangle(Pad, 92, 150, 30),
            DecimalPlaces = 1, Increment = 10,
            Maximum       = 100_000, Minimum = 0,
            Value         = (decimal)_settings.MonthlyLimitGB,
            Font          = new Font("Segoe UI", 11f),
            TextAlign     = HorizontalAlignment.Right
        };
        var unit = new Label
        {
            Text      = "GB  /  month",
            AutoSize  = true,
            Location  = new Point(Pad + 158, 98),
            ForeColor = Color.FromArgb(100, 100, 100)
        };

        int bY = H - BtnH - 18;
        var ok = new Button
        {
            Text         = "Save",
            Bounds       = new Rectangle(W - Pad - BtnW * 2 - BtnGap, bY, BtnW, BtnH),
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.System
        };
        var cancel = new Button
        {
            Text         = "Cancel",
            Bounds       = new Rectangle(W - Pad - BtnW, bY, BtnW, BtnH),
            DialogResult = DialogResult.Cancel,
            FlatStyle    = FlatStyle.System
        };

        frm.AcceptButton = ok; frm.CancelButton = cancel;
        frm.Controls.AddRange(new Control[] { hdr, sub, num, unit, ok, cancel });

        if (ShowDialogSafe(frm) == DialogResult.OK)
        {
            _settings.MonthlyLimitGB = (double)num.Value;
            _settings.Save();
        }
    }

    private void ShowAboutDialog()
    {
        var asmName    = Assembly.GetExecutingAssembly().GetName();
        string version = asmName.Version?.ToString(3) ?? "1.0";

        const int W = 480, H = 360;
        const int Pad = 22, BtnW = 130, BtnH = 34, BtnGap = 10;
        const int HdrH = 120;          // tall enough for 18pt + 10pt stacked

        using var frm = new Form
        {
            Text            = "About NetMon",
            ClientSize      = new Size(W, H),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition   = FormStartPosition.CenterParent,
            MaximizeBox     = false, MinimizeBox = false,
            ShowInTaskbar   = false,
            Font            = new Font("Segoe UI", 9.5f),
            BackColor       = Color.White
        };
        var appIcon = LoadAppIcon();
        if (appIcon != null) frm.Icon = appIcon;

        // ── Blue header (owner-drawn, with icon + title + version) ─────
        var header = new Panel
        {
            Bounds    = new Rectangle(0, 0, W, HdrH),
            BackColor = Color.FromArgb(35, 105, 182)
        };
        header.Paint += (_, pe) =>
        {
            var g = pe.Graphics;
            using var grad = new LinearGradientBrush(
                header.ClientRectangle,
                Color.FromArgb( 55, 135, 215),
                Color.FromArgb( 20,  80, 150),
                LinearGradientMode.Vertical);
            g.FillRectangle(grad, header.ClientRectangle);

            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int textX = Pad;
            if (appIcon != null)
            {
                using var ib = appIcon.ToBitmap();
                g.DrawImage(ib, Pad, (HdrH - 72) / 2, 72, 72);
                textX = Pad + 72 + 18;
            }

            using var titleFont = new Font("Segoe UI Semibold", 20f);
            using var verFont   = new Font("Segoe UI",           10.5f);
            var titleSize = g.MeasureString("NetMon", titleFont);
            var verSize   = g.MeasureString($"Version {version}", verFont);

            float stackHeight = titleSize.Height + verSize.Height + 2;
            float topY        = (HdrH - stackHeight) / 2f;

            using var titleBrush = new SolidBrush(Color.White);
            using var verBrush   = new SolidBrush(Color.FromArgb(215, 235, 255));
            g.DrawString("NetMon",             titleFont, titleBrush, textX, topY);
            g.DrawString($"Version {version}", verFont,   verBrush,   textX, topY + titleSize.Height + 2);
        };

        // ── Body text ──────────────────────────────────────────────────
        var desc = new Label
        {
            Text =
                "A lightweight, always-visible network bandwidth monitor.\n" +
                "Inspired by DU Meter. Free and open-source — forever.",
            Bounds    = new Rectangle(Pad, HdrH + 22, W - Pad * 2, 42),
            ForeColor = Color.FromArgb(40, 40, 40)
        };

        var linkLbl = new Label
        {
            Text      = "Project home",
            AutoSize  = true,
            Location  = new Point(Pad, HdrH + 76),
            ForeColor = Color.FromArgb(100, 100, 100)
        };
        var link = new LinkLabel
        {
            Text             = "github.com/aungkokomm/NetMon",
            AutoSize         = true,
            Location         = new Point(Pad + 88, HdrH + 76),
            LinkColor        = Color.FromArgb(35, 105, 182),
            ActiveLinkColor  = Color.FromArgb(210, 50, 30),
            VisitedLinkColor = Color.FromArgb(35, 105, 182),
            LinkBehavior     = LinkBehavior.HoverUnderline
        };
        link.LinkClicked += (_, _) => OpenUrl("https://github.com/aungkokomm/NetMon");

        var copy = new Label
        {
            Text      = "© 2026 aungkokomm · MIT License",
            AutoSize  = true,
            Location  = new Point(Pad, HdrH + 104),
            ForeColor = Color.FromArgb(130, 130, 130),
            Font      = new Font("Segoe UI", 8.5f)
        };

        // ── Footer with real buttons ───────────────────────────────────
        const int FooterH = 62;
        var footer = new Panel
        {
            Bounds    = new Rectangle(0, H - FooterH, W, FooterH),
            BackColor = Color.FromArgb(245, 248, 252)
        };
        footer.Paint += (_, pe) =>
        {
            using var pen = new Pen(Color.FromArgb(215, 225, 235), 1);
            pe.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        int bY = (FooterH - BtnH) / 2;

        var okBtn = new Button
        {
            Text         = "Close",
            Bounds       = new Rectangle(W - Pad - BtnW, bY, BtnW, BtnH),
            DialogResult = DialogResult.OK,
            FlatStyle    = FlatStyle.System,
            Font         = new Font("Segoe UI", 9.5f)
        };
        var ghBtn = new Button
        {
            Text         = "Visit GitHub",
            Bounds       = new Rectangle(W - Pad - BtnW * 2 - BtnGap, bY, BtnW, BtnH),
            FlatStyle    = FlatStyle.System,
            Font         = new Font("Segoe UI", 9.5f)
        };
        ghBtn.Click += (_, _) => OpenUrl("https://github.com/aungkokomm/NetMon");

        footer.Controls.Add(okBtn);
        footer.Controls.Add(ghBtn);

        frm.Controls.Add(header);
        frm.Controls.Add(desc);
        frm.Controls.Add(linkLbl);
        frm.Controls.Add(link);
        frm.Controls.Add(copy);
        frm.Controls.Add(footer);

        frm.AcceptButton = okBtn;
        frm.CancelButton = okBtn;

        ShowDialogSafe(frm);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"OpenUrl: {ex.Message}"); }
    }

    // ── tray icon (HiDPI) ────────────────────────────────────────────────

    private static Icon BuildTrayIcon()
    {
        const int S = 32;
        using var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using var g   = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bgPath  = RoundedRect(2, 2, S - 4, S - 4, 6);
        using var bgBrush = new SolidBrush(Color.FromArgb(35, 105, 182));
        g.FillPath(bgBrush, bgPath);

        PointF[] pts =
        {
            new( 4, 24), new( 8, 18), new(12, 22),
            new(16, 12), new(20, 16), new(24, 10), new(28, 14)
        };
        using var lp = new Pen(Color.FromArgb(235, 255, 255, 255), 2.8f)
            { LineJoin = LineJoin.Round };
        g.DrawLines(lp, pts);

        using var dot = new SolidBrush(Color.FromArgb(95, 215, 70));
        g.FillEllipse(dot, 24, 4, 5, 5);

        var hIcon = bmp.GetHicon();
        var icon  = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

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

    // ── public format helpers (used by StatsPanel / UsageForm) ────────────

    public static string FormatSpeed(long bps)
    {
        // Right-padded numeric field stops digit-jitter when paired with mono font
        long bits = bps * 8;
        return bits switch
        {
            < 1_000L         => $"{bits,5} bps",
            < 1_000_000L     => $"{bits / 1_000.0,6:F1} kbps",
            < 1_000_000_000L => $"{bits / 1_000_000.0,6:F2} Mbps",
            _                => $"{bits / 1_000_000_000.0,6:F2} Gbps"
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
        // Monospaced fonts so digits stay aligned as values tick
        private readonly Font       _font      = CreateMono(9.5f, FontStyle.Bold);
        private readonly Font       _fontSmall = CreateMono(8f,   FontStyle.Bold);
        // Convention: green = download, red = upload
        private readonly SolidBrush _dlBrush   = new(Color.FromArgb(  0, 160,  50));
        private readonly SolidBrush _ulBrush   = new(Color.FromArgb(210,  50,  35));
        private readonly SolidBrush _white     = new(Color.White);

        private static Font CreateMono(float sz, FontStyle st)
        {
            foreach (var name in new[] { "Cascadia Mono", "Consolas", "Lucida Console" })
            {
                try
                {
                    var f = new Font(name, sz, st);
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                        return f;
                    f.Dispose();
                }
                catch { /* try next */ }
            }
            return new Font(FontFamily.GenericMonospace, sz, st);
        }

        public void Paint(Graphics g, Rectangle r, long dlBps, long ulBps, bool fast = false)
        {
            g.SmoothingMode     = fast ? SmoothingMode.None : SmoothingMode.AntiAlias;
            g.TextRenderingHint = fast ? TextRenderingHint.SystemDefault
                                       : TextRenderingHint.ClearTypeGridFit;

            int half = r.Width / 2;
            PaintHalf(g, _dlBrush, FormatSpeed(dlBps), isDown: true,
                      r.X,        r.Y, half,           r.Height);
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
            float tw = Math.Max(0, sw - (tx - sx) - 4);

            // Auto-shrink if the primary font overflows
            Font useFont = _font;
            var meas = g.MeasureString(text, _font);
            if (meas.Width > tw) useFont = _fontSmall;

            using var sf = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming      = StringTrimming.EllipsisCharacter,
                FormatFlags   = StringFormatFlags.NoWrap
            };
            g.DrawString(text, useFont, brush, new RectangleF(tx, sy, tw, sh), sf);
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
            _font.Dispose(); _fontSmall.Dispose();
            _dlBrush.Dispose(); _ulBrush.Dispose(); _white.Dispose();
        }
    }

    // ── nested: title button (needs HWND for hover tracking) ─────────────

    private sealed class TitleButton : Control
    {
        private bool   _hov;
        private string _sym;
        private readonly Color _hoverCol;
        private readonly Font  _font = new("Tahoma", 9f, FontStyle.Bold);

        public string Symbol { get => _sym; set { _sym = value; Invalidate(); } }

        public TitleButton(string sym, Color hoverCol)
        {
            _sym      = sym;
            _hoverCol = hoverCol;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint            |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.SupportsTransparentBackColor, true);
            Size      = new Size(ButtonSize, ButtonSize);
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
                // Rounded-rect hover bg — Fluent style
                using var hp = RoundedRect(0.5f, 0.5f, Width - 1f, Height - 1f, 4f);
                using var hb = new SolidBrush(Color.FromArgb(220, _hoverCol));
                g.FillPath(hb, hp);
            }

            using var sb = new SolidBrush(
                _hov ? Color.White : Color.FromArgb(190, 245, 250, 255));
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
