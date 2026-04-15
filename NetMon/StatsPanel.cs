using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace NetMon;

/// <summary>DU Meter–style inline stats panel: Today / Month breakdown with optional limit bar.</summary>
public sealed class StatsPanel : Control
{
    private DayUsage _today = new();
    private DayUsage _month = new();
    private long     _limitBytes;

    // ── cached GDI (all allocated once) ──────────────────────────────────
    private readonly Font        _hdrFont  = new("Tahoma", 7.5f, FontStyle.Bold | FontStyle.Underline);
    private readonly Font        _lblFont  = new("Tahoma", 8f);
    private readonly Font        _valFont  = new("Tahoma", 8f, FontStyle.Bold);
    private readonly Font        _limFont  = new("Tahoma", 7.5f);
    private readonly SolidBrush  _darkBrush = new(Color.FromArgb(20,  40,  65));
    private readonly SolidBrush  _dimBrush  = new(Color.FromArgb(78, 108, 140));
    private readonly Pen         _rulePen   = new(Color.FromArgb(155, 198, 222), 1f);
    private readonly Pen         _sepPen    = new(Color.FromArgb(165, 202, 226), 1f);
    private readonly Pen         _barPen    = new(Color.FromArgb(148, 190, 216), 1f);

    private const float RowH = 18f;
    private const float Col0 = 10f;

    // ── constructor ───────────────────────────────────────────────────────

    public StatsPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint            |
                 ControlStyles.OptimizedDoubleBuffer, true);
    }

    // ── public API ────────────────────────────────────────────────────────

    public void Refresh(DayUsage today, DayUsage month, long limitBytes)
    {
        _today      = today;
        _month      = month;
        _limitBytes = limitBytes;
        Invalidate();
    }

    // ── paint ─────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int   w  = Width;
        float c1 = w * 0.41f, c2 = w * 0.68f;

        // Top rule
        g.DrawLine(_rulePen, 8, 2, w - 8, 2);

        float y = 7f;

        // Column headers
        g.DrawString("Today", _hdrFont, _dimBrush, c1, y);
        g.DrawString("Month", _hdrFont, _dimBrush, c2, y);
        y += RowH;

        long ts = _today.BytesSent, tr = _today.BytesReceived;
        long ms = _month.BytesSent, mr = _month.BytesReceived;

        DrawRow(g, "Sent:",     ts,    ms,    c1, c2, y); y += RowH;
        DrawRow(g, "Received:", tr,    mr,    c1, c2, y); y += RowH;
        g.DrawLine(_sepPen, Col0, y - 2f, w - Col0, y - 2f);
        DrawRow(g, "Total:",    ts+tr, ms+mr, c1, c2, y); y += RowH + 5f;

        // Monthly limit bar
        if (_limitBytes > 0)
        {
            long   used = ms + mr;
            double pct  = Math.Min(1.0, (double)used / _limitBytes);
            float  bx = Col0, bw = w - Col0 * 2f, bh = 10f;

            using (var bg = new SolidBrush(Color.FromArgb(190, 216, 236)))
                g.FillRectangle(bg, bx, y, bw, bh);

            if (pct > 0)
            {
                Color fc = pct >= .9 ? Color.FromArgb(210, 44, 30)
                         : pct >= .7 ? Color.FromArgb(216, 136, 0)
                         : Color.FromArgb(0, 155, 40);
                using var fill = new SolidBrush(fc);
                g.FillRectangle(fill, bx, y, (float)(bw * pct), bh);
            }

            g.DrawRectangle(_barPen, bx, y, bw - 1f, bh - 1f);
            y += bh + 3f;

            g.DrawString(
                $"{pct * 100:F1}%  of  {MainForm.FormatBytes(_limitBytes)}  monthly limit",
                _limFont, _dimBrush, Col0, y);
        }
    }

    private void DrawRow(Graphics g, string label, long today, long month,
                          float c1, float c2, float y)
    {
        g.DrawString(label,                       _lblFont, _darkBrush, Col0, y);
        g.DrawString(MainForm.FormatBytes(today), _valFont, _darkBrush, c1,   y);
        g.DrawString(MainForm.FormatBytes(month), _valFont, _darkBrush, c2,   y);
    }

    // ── cleanup ───────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hdrFont.Dispose();  _lblFont.Dispose();
            _valFont.Dispose();  _limFont.Dispose();
            _darkBrush.Dispose(); _dimBrush.Dispose();
            _rulePen.Dispose();  _sepPen.Dispose(); _barPen.Dispose();
        }
        base.Dispose(disposing);
    }
}
