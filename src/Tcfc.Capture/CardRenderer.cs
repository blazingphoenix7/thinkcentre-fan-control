using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Tcfc.Core;

namespace Tcfc.Capture;

/// <summary>
/// Draws the README monitor card with GDI+. Pure rendering: every value comes
/// from the caller, and unavailable readings draw as dashes or chart gaps.
/// </summary>
public sealed class CardRenderer
{
    public const int Width = 760;
    public const int Height = 420;

    /// <summary>Chart capacity in samples. A shorter history draws right-anchored, like a ticker filling up.</summary>
    public const int HistorySlots = 60;

    // Dark slate palette with a single teal accent.
    private static readonly Color PageBg = Color.FromArgb(19, 21, 26);
    private static readonly Color PanelBg = Color.FromArgb(27, 30, 36);      // #1b1e24
    private static readonly Color PanelEdge = Color.FromArgb(44, 49, 60);
    private static readonly Color TextPrimary = Color.FromArgb(232, 236, 244);
    private static readonly Color TextMuted = Color.FromArgb(139, 147, 164);
    private static readonly Color TextFaint = Color.FromArgb(94, 101, 118);
    private static readonly Color Accent = Color.FromArgb(63, 214, 198);
    private static readonly Color AccentText = Color.FromArgb(10, 34, 30);   // on-accent pill text
    private static readonly Color GridLine = Color.FromArgb(38, 43, 53);
    private static readonly Color PillEdge = Color.FromArgb(56, 63, 78);

    private static readonly Font TitleFont = new("Segoe UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font NumberFont = new("Segoe UI", 76f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font UnitFont = new("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font LineFont = new("Segoe UI", 16f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font LineValueFont = new("Segoe UI", 16f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font PillFont = new("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font SmallFont = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static readonly RectangleF ChartRect = new(320f, 88f, 396f, 240f);

    /// <summary>rpm &lt; 0 / null tempC / null mode draw as dashes; negative history samples become chart gaps. Caller disposes the bitmap.</summary>
    public Bitmap Render(int rpm, int? tempC, FanMode? mode, IReadOnlyList<int> history)
    {
        var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        try
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.Clear(PageBg);
            DrawPanel(g);
            DrawTitle(g);
            DrawRpmFigure(g, rpm);
            DrawTempLine(g, tempC);
            DrawChart(g, history);
            DrawModePills(g, mode);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    private static void DrawPanel(Graphics g)
    {
        var rect = new RectangleF(18f, 18f, Width - 36f, Height - 36f);
        using var path = RoundedRect(rect, 16f);
        using var fill = new SolidBrush(PanelBg);
        using var edge = new Pen(PanelEdge, 1f);
        g.FillPath(fill, path);
        g.DrawPath(edge, path);
    }

    private static void DrawTitle(Graphics g)
    {
        using var muted = new SolidBrush(TextMuted);
        g.DrawString("ThinkCentre Fan Control · desktop", TitleFont, muted, 42f, 38f);
    }

    private static void DrawRpmFigure(Graphics g, int rpm)
    {
        bool available = rpm >= 0;
        string figure = available ? rpm.ToString(CultureInfo.InvariantCulture) : "-";

        using var figureBrush = new SolidBrush(available ? Accent : TextMuted);
        using var unitBrush = new SolidBrush(TextMuted);

        const float x = 36f;
        const float y = 64f;
        SizeF figureSize = g.MeasureString(figure, NumberFont);
        g.DrawString(figure, NumberFont, figureBrush, x, y);

        SizeF unitSize = g.MeasureString("RPM", UnitFont);
        g.DrawString("RPM", UnitFont, unitBrush,
            x + figureSize.Width - 8f,
            y + figureSize.Height - unitSize.Height - 16f);
    }

    private static void DrawTempLine(Graphics g, int? tempC)
    {
        const float x = 44f;
        const float y = 196f;
        string label = "hottest sensor";
        string value = tempC.HasValue
            ? tempC.Value.ToString(CultureInfo.InvariantCulture) + " °C"
            : "-";

        using var muted = new SolidBrush(TextMuted);
        using var primary = new SolidBrush(TextPrimary);
        g.DrawString(label, LineFont, muted, x, y);
        float labelWidth = g.MeasureString(label, LineFont).Width;
        g.DrawString(value, LineValueFont, primary, x + labelWidth + 6f, y);
    }

    private static void DrawChart(Graphics g, IReadOnlyList<int> history)
    {
        RectangleF chart = ChartRect;

        // Scale from the valid (non-negative) samples only.
        int lo = int.MaxValue, hi = int.MinValue;
        for (int i = 0; i < history.Count; i++)
        {
            int v = history[i];
            if (v < 0)
                continue;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        bool hasData = hi >= lo;

        int scaleLo = 0, scaleHi = 1;
        if (hasData)
        {
            float pad = Math.Max(25f, (hi - lo) * 0.2f);
            scaleLo = (int)Math.Floor(Math.Max(0f, lo - pad) / 50f) * 50;
            scaleHi = (int)Math.Ceiling((hi + pad) / 50f) * 50;
            if (scaleHi <= scaleLo)
                scaleHi = scaleLo + 50;
        }

        // Grid: frame, three inner horizontal lines, a vertical every 10 slots.
        int slots = Math.Max(HistorySlots, history.Count);
        float slotW = chart.Width / (slots - 1);
        using (var grid = new Pen(GridLine, 1f))
        {
            g.DrawRectangle(grid, chart.X, chart.Y, chart.Width, chart.Height);
            for (int t = 1; t < 4; t++)
            {
                float y = chart.Y + chart.Height * t / 4f;
                g.DrawLine(grid, chart.X, y, chart.Right, y);
            }
            for (int s = 10; ; s += 10)
            {
                float x = chart.Right - s * slotW;
                if (x <= chart.X + 1f)
                    break;
                g.DrawLine(grid, x, chart.Y, x, chart.Bottom);
            }
        }

        // scale labels on the top and bottom grid lines
        if (hasData)
        {
            using var faint = new SolidBrush(TextFaint);
            g.DrawString(scaleHi.ToString(CultureInfo.InvariantCulture), SmallFont, faint, chart.X + 5f, chart.Y + 4f);
            g.DrawString(scaleLo.ToString(CultureInfo.InvariantCulture), SmallFont, faint, chart.X + 5f, chart.Bottom - 18f);
        }

        // Series: right-anchored polyline; unavailable samples (-1) are gaps.
        if (hasData)
        {
            float XFor(int index) => chart.Right - (history.Count - 1 - index) * slotW;
            float YFor(int value) =>
                chart.Bottom - (value - scaleLo) / (float)(scaleHi - scaleLo) * chart.Height;

            using var linePen = new Pen(Accent, 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            using var underFill = new SolidBrush(Color.FromArgb(26, Accent));
            using var dotFill = new SolidBrush(Accent);

            var run = new List<PointF>();
            PointF? newest = null;

            void Flush()
            {
                if (run.Count == 0)
                    return;
                if (run.Count == 1)
                {
                    g.FillEllipse(dotFill, run[0].X - 2f, run[0].Y - 2f, 4f, 4f);
                }
                else
                {
                    var area = new PointF[run.Count + 2];
                    run.CopyTo(area);
                    area[run.Count] = new PointF(run[run.Count - 1].X, chart.Bottom);
                    area[run.Count + 1] = new PointF(run[0].X, chart.Bottom);
                    g.FillPolygon(underFill, area);
                    g.DrawLines(linePen, run.ToArray());
                }
                newest = run[run.Count - 1];
                run.Clear();
            }

            for (int i = 0; i < history.Count; i++)
            {
                if (history[i] < 0)
                {
                    Flush();
                    continue;
                }
                run.Add(new PointF(XFor(i), YFor(history[i])));
            }
            Flush();

            // Marker on the newest sample.
            if (newest.HasValue && history[history.Count - 1] >= 0)
            {
                using var halo = new SolidBrush(Color.FromArgb(70, Accent));
                g.FillEllipse(halo, newest.Value.X - 7f, newest.Value.Y - 7f, 14f, 14f);
                g.FillEllipse(dotFill, newest.Value.X - 3.5f, newest.Value.Y - 3.5f, 7f, 7f);
            }
        }

        using var caption = new SolidBrush(TextFaint);
        g.DrawString("fan rpm · recent samples", SmallFont, caption, chart.X, chart.Bottom + 8f);
    }

    private static void DrawModePills(Graphics g, FanMode? mode)
    {
        using (var faint = new SolidBrush(TextFaint))
        {
            g.DrawString("firmware fan mode", SmallFont, faint, 44f, 322f);
        }

        float x = 44f;
        const float y = 344f;
        const float h = 30f;

        using var activeFill = new SolidBrush(Accent);
        using var activeText = new SolidBrush(AccentText);
        using var idleEdge = new Pen(PillEdge, 1f);
        using var idleText = new SolidBrush(TextMuted);

        foreach (FanMode m in new[] { FanMode.Quiet, FanMode.Balanced, FanMode.Performance })
        {
            string label = m.ToString();
            SizeF size = g.MeasureString(label, PillFont);
            float w = size.Width + 24f;

            using var pill = RoundedRect(new RectangleF(x, y, w, h), h / 2f);
            bool active = mode == m;
            if (active)
                g.FillPath(activeFill, pill);
            else
                g.DrawPath(idleEdge, pill);

            g.DrawString(label, PillFont, active ? activeText : idleText,
                x + (w - size.Width) / 2f,
                y + (h - size.Height) / 2f + 0.5f);

            x += w + 10f;
        }
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
        path.CloseFigure();
        return path;
    }
}
