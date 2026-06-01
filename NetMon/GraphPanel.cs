using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NetMon;

/// <summary>
/// Scrolling dual-channel area graph.
///
/// Green fill + line = download.  Red fill + line = upload.
/// Left strip shows the current Y-axis scale (rotated).
/// Top-left shows today's running total via <see cref="TopInfo"/>.
///
/// Performance notes:
///   • PointF arrays are cached by sample count — zero heap allocation in steady state.
///   • Area fills use HighSpeed (no anti-alias); lines use AntiAlias.
///   • Invalidate() is skipped when the graph is idle.
/// </summary>
public sealed class GraphPanel : Control
{
    // ── config ────────────────────────────────────────────────────────────
    private const int   Samples  = 120;
    private const float MinScale = 2048f;
    private const int   YAxisW   = 26;   // left label strip

    // Fluent-leaning palette: blue download / green upload, lighter grid
    private static readonly Color BgColor   = Color.FromArgb(244, 249, 254);
    private static readonly Color YAxisBg   = Color.FromArgb(225, 238, 250);
    private static readonly Color GridColor = Color.FromArgb(190, 215, 235);
    private static readonly Color LabelCol  = Color.FromArgb( 60, 100, 145);
    // Convention: green = download, red = upload
    private static readonly Color DlLine    = Color.FromArgb(  0, 160,  50);
    private static readonly Color DlFill    = Color.FromArgb( 90,   0, 160,  50);
    private static readonly Color UlLine    = Color.FromArgb(210,  50,  35);
    private static readonly Color UlFill    = Color.FromArgb( 90, 210,  50,  35);

    // ── cached GDI ────────────────────────────────────────────────────────
    private readonly Pen        _dlPen;
    private readonly Pen        _ulPen;
    private readonly Pen        _gridPen;
    private readonly Pen        _axisPen;
    private readonly SolidBrush _dlFill;
    private readonly SolidBrush _ulFill;
    private readonly SolidBrush _dlBar      = new(DlLine);   // opaque bar fill
    private readonly SolidBrush _ulBar      = new(UlLine);   // opaque bar fill
    private readonly SolidBrush _yaBrush    = new(YAxisBg);
    private readonly SolidBrush _labelBrush = new(LabelCol);
    private readonly Font       _scaleFont  = new("Tahoma", 7f, FontStyle.Bold);
    private readonly Font       _infoFont   = new("Tahoma", 7f);

    // ── PointF buffer cache ───────────────────────────────────────────────
    private PointF[]? _lineBuf;
    private PointF[]? _fillBuf;
    private int       _cachedCount = -1;

    // ── data ──────────────────────────────────────────────────────────────
    private readonly long[] _dl = new long[Samples];
    private readonly long[] _ul = new long[Samples];
    private int   _head;           // next write position
    private float _scale = MinScale;
    private long  _prevDl, _prevUl;

    /// <summary>Small informational line painted in the graph top-left (e.g. "Today 12.4 MB").</summary>
    public string TopInfo { get; set; } = "";

    /// <summary>Alpha channel (0–255) for the area fills under the download / upload lines.</summary>
    public int FillAlpha
    {
        get => _dlFill.Color.A;
        set
        {
            int a = Math.Clamp(value, 20, 220);
            _dlFill.Color = Color.FromArgb(a, DlLine);
            _ulFill.Color = Color.FromArgb(a, UlLine);
            Invalidate();
        }
    }

    // ── constructor ───────────────────────────────────────────────────────

    public GraphPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint            |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = BgColor;

        _dlPen   = new Pen(DlLine, 1.6f) { LineJoin = LineJoin.Round };
        _ulPen   = new Pen(UlLine, 1.6f) { LineJoin = LineJoin.Round };
        _gridPen = new Pen(GridColor, 1f) { DashStyle = DashStyle.Dot };
        _axisPen = new Pen(GridColor, 1f);
        _dlFill  = new SolidBrush(DlFill);
        _ulFill  = new SolidBrush(UlFill);
    }

    // ── public API ────────────────────────────────────────────────────────

    /// <summary>Push one sample.  Only triggers a repaint when there is something new to show.</summary>
    public void AddSample(long dlBps, long ulBps)
    {
        _dl[_head] = dlBps;
        _ul[_head] = ulBps;
        _head = (_head + 1) % Samples;

        // Adaptive Y-axis: snap up instantly, decay slowly
        long  peak   = 0;
        for (int i = 0; i < Samples; i++)
            peak = Math.Max(peak, Math.Max(_dl[i], _ul[i]));
        float target  = Math.Max(MinScale, NiceCeil(peak));
        float prev    = _scale;
        _scale = _scale < target ? target : _scale * 0.96f + target * 0.04f;

        bool traffic = dlBps != 0 || ulBps != 0 || _prevDl != 0 || _prevUl != 0;
        bool rescale = Math.Abs(_scale - prev) > 0.5f;
        _prevDl = dlBps; _prevUl = ulBps;

        if (traffic || rescale) Invalidate();
    }

    // ── paint ─────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BgColor);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int w = Width, h = Height;
        int gx = YAxisW, gw = Math.Max(1, w - gx);

        // ── Y-axis strip ─────────────────────────────────────────────────
        g.FillRectangle(_yaBrush, 0, 0, gx, h);
        g.DrawLine(_axisPen, gx, 0, gx, h);

        // Rotated scale label (e.g. "8.5M")
        var saved = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TranslateTransform(gx - 4, h / 2f);
        g.RotateTransform(-90f);
        using var sf = new StringFormat
            { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
        g.DrawString(FormatScale(_scale), _scaleFont, _labelBrush, 0f, 0f, sf);
        g.Restore(saved);

        // ── Grid ──────────────────────────────────────────────────────────
        g.SmoothingMode = SmoothingMode.None;
        int cols = Math.Max(2, gw / 40);
        for (int i = 1; i < cols; i++)
        {
            float x = gx + gw * i / (float)cols;
            g.DrawLine(_gridPen, x, 0, x, h);
        }
        g.DrawLine(_gridPen, gx, h / 2f, w, h / 2f);

        // ── Vertical bars (DU-Meter style) ────────────────────────────────
        // One contiguous bar per pixel column — no gaps. When the graph is
        // wider than the sample buffer, each column maps back to the nearest
        // sample so the bars touch edge-to-edge like DU Meter.
        if (gw >= 2)
        {
            g.SmoothingMode = SmoothingMode.None;
            float hScale = _scale > 0 ? (h - 2) / _scale : 0f;
            int   baseY  = h - 1;
            int   pxCols = gw;                          // draw every pixel column

            for (int px = 0; px < pxCols; px++)
            {
                // Map this pixel column to a sample (oldest → newest, L → R)
                int s   = (int)((long)px * (Samples - 1) / (pxCols - 1));
                int idx = (_head - Samples + s + Samples) % Samples;
                int x   = gx + px;

                long dl = _dl[idx], ul = _ul[idx];
                // Larger value drawn first (behind), smaller on top, so both
                // colours remain visible in the same column.
                if (dl >= ul)
                {
                    DrawBar(g, _dlBar, x, baseY, dl, hScale);
                    DrawBar(g, _ulBar, x, baseY, ul, hScale);
                }
                else
                {
                    DrawBar(g, _ulBar, x, baseY, ul, hScale);
                    DrawBar(g, _dlBar, x, baseY, dl, hScale);
                }
            }
        }
    }

    private static void DrawBar(Graphics g, SolidBrush brush, int x, int baseY,
                                 long value, float hScale)
    {
        if (value <= 0) return;
        int top = (int)Math.Clamp(baseY - value * hScale, 1f, baseY);
        int barH = baseY - top + 1;
        if (barH > 0) g.FillRectangle(brush, x, top, 1, barH);
    }

    // ── buffer helpers ────────────────────────────────────────────────────

    private void EnsureBuffers(int count)
    {
        if (count == _cachedCount) return;
        _lineBuf     = new PointF[count];
        _fillBuf     = new PointF[count + 2];
        _cachedCount = count;
    }

    private void FillArea(Graphics g, long[] samples, SolidBrush fill,
                          int count, int gx, int gw, int h)
    {
        BuildLinePoints(samples, count, gx, gw, h, _fillBuf!);
        _fillBuf![count]     = new PointF(_fillBuf[count - 1].X, h - 1);
        _fillBuf![count + 1] = new PointF(_fillBuf[0].X, h - 1);
        g.FillPolygon(fill, _fillBuf);
    }

    private void DrawLine(Graphics g, long[] samples, Pen pen,
                          int count, int gx, int gw, int h)
    {
        BuildLinePoints(samples, count, gx, gw, h, _lineBuf!);
        g.DrawLines(pen, _lineBuf!);
    }

    private void BuildLinePoints(long[] samples, int count,
                                  int gx, int gw, int h, PointF[] buf)
    {
        float xStep  = (float)(gw - 1) / (count - 1);
        float hScale = _scale > 0 ? (h - 2) / _scale : 0f;
        for (int i = 0; i < count; i++)
        {
            int idx = (_head - count + i + Samples) % Samples;
            buf[i]  = new PointF(
                gx + i * xStep,
                Math.Clamp(h - 1 - samples[idx] * hScale, 1f, h - 1f));
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static string FormatScale(float scale)
    {
        long bits = (long)(scale * 8);
        return bits switch
        {
            < 1_000L         => $"{bits}b",
            < 1_000_000L     => $"{bits / 1_000.0:F0}k",
            < 1_000_000_000L => $"{bits / 1_000_000.0:F1}M",
            _                => $"{bits / 1_000_000_000.0:F1}G"
        };
    }

    private static float NiceCeil(long v)
    {
        float val = Math.Max(MinScale, v);
        float u   = 512f;
        while (u < val) u *= 2f;
        return u;
    }

    // ── cleanup ───────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dlPen.Dispose();   _ulPen.Dispose();
            _gridPen.Dispose(); _axisPen.Dispose();
            _dlFill.Dispose();  _ulFill.Dispose();
            _dlBar.Dispose();   _ulBar.Dispose();
            _yaBrush.Dispose(); _labelBrush.Dispose();
            _scaleFont.Dispose(); _infoFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
