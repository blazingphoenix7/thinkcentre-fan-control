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
/// Headless GDI+ renderer for the fan-control dashboard window, used to build
/// the README demo GIF. This draws exactly what DashboardForm draws - same
/// palette, fonts, and layout math, ported constant for constant - except every
/// value is a plain argument instead of a live EC/CPU/WMI reading, and there is
/// no restart banner (that state is transient and never holds long enough to be
/// worth a GIF frame). Rendered at <see cref="Scale"/> so the GIF is crisp.
/// </summary>
public sealed class CardRenderer
{
    // ---- palette: identical to DashboardForm's approved flat "bone + orange" look. ----
    private static readonly Color Paper = Color.FromArgb(234, 230, 219);
    private static readonly Color Ink = Color.FromArgb(24, 21, 16);
    private static readonly Color Muted = Color.FromArgb(140, 133, 122);
    private static readonly Color Rule = Color.FromArgb(211, 205, 191);
    private static readonly Color Accent = Color.FromArgb(255, 87, 34);
    private static readonly Color BarCool = Color.FromArgb(127, 138, 144);
    private static readonly Color BarWarm = Color.FromArgb(213, 148, 43);
    private static readonly Color KeyInactiveBg = Color.FromArgb(226, 221, 209);
    private static readonly Color KeyInactiveText = Color.FromArgb(111, 105, 93);
    private static readonly Color White = Color.FromArgb(255, 255, 255);

    // Same named font instances as DashboardForm (not synthetic FontStyle.Bold
    // on the base family) so the drawn weight matches the real window. Sizes
    // are the form's logical pixel sizes; ScaleTransform below blows them up
    // by Scale, the same way DashboardForm's EffScale does for a high-DPI monitor.
    private static readonly Font HeroFont = new("Bahnschrift SemiBold", 92f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font KickerFont = new("Bahnschrift SemiBold", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font StatusFont = new("Bahnschrift SemiBold", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font HeroUnitFont = new("Bahnschrift", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font LabelFont = new("Bahnschrift SemiBold", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font ToggleFont = new("Bahnschrift SemiBold", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static readonly Font CoreNumFont = new("Cascadia Mono", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CoreNumHotFont = new("Cascadia Mono", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font CoreIdFont = new("Cascadia Mono", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font LiveTagFont = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font StampFont = new("Cascadia Mono", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);

    // Cached once for the same reason DashboardForm caches them: this renders
    // dozens of frames per run, not once.
    private static readonly SolidBrush InkBrush = new(Ink);
    private static readonly SolidBrush MutedBrush = new(Muted);
    private static readonly SolidBrush AccentBrush = new(Accent);
    private static readonly SolidBrush WhiteBrush = new(White);
    private static readonly SolidBrush BarCoolBrush = new(BarCool);
    private static readonly SolidBrush BarWarmBrush = new(BarWarm);
    private static readonly SolidBrush KeyInactiveBgBrush = new(KeyInactiveBg);
    private static readonly SolidBrush KeyInactiveTextBrush = new(KeyInactiveText);

    private static readonly Pen RulePen = new(Rule, 1f);
    private static readonly Pen KeyBorderPen = new(Rule, 1.5f);
    private static readonly Pen SparklinePen = new(Ink, 1.6f)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    // GDI+ has no letter-spacing primitive; tracked labels are drawn glyph by
    // glyph with a fixed advance, same as DashboardForm's DrawTracked.
    private static readonly StringFormat TightFormat = new(StringFormat.GenericTypographic);

    /// <summary>Render scale. 2x the form's logical 440-wide layout, so the GIF is crisp.</summary>
    public const float Scale = 2f;

    private const int FormWidth = 440;

    private const float PadX = 26f;
    private const float PadTop = 26f;
    private const float PadBottom = 22f;
    private const float ContentLeft = PadX;
    private const float ContentRight = FormWidth - PadX;
    private const float ContentWidth = ContentRight - ContentLeft;

    private const float HeaderH = 16f;
    private const float HeaderDotSize = 9f;
    private const float HeaderDotGap = 7f;

    private const float HeroTopMargin = 14f;
    private const float HeroBlockHeight = 104f;
    private const float HeroBottomMargin = 8f;
    private const float HeroRightPad = 6f;
    private const float SparkWidth = 96f;
    private const float SparkHeight = 30f;
    private const float SparkToLiveGap = 3f;
    private const float UnitToSparkGap = 6f;
    private const float SparkDotRadius = 2.6f;

    private const float SectionTopMargin = 20f;
    private const float SectionLabelHeight = 14f;
    private const float SectionBottomMargin = 12f;

    private const float GraphHeight = 138f;
    private const float CoreColumnGap = 6f;
    private const float CoreBarMaxWidth = 24f;
    private const float CoreBarRadius = 3f;
    private const float CoreNumGap = 7f;
    private const float CoreIdGap = 9f;
    private const float BarMinTemp = 40f;
    private const float BarTempSpan = 60f;
    private const int WarmThreshold = 89;
    private const int HotThreshold = 93;

    private const float KeyHeight = 40f;
    private const float KeyGap = 7f;
    private const float KeyRadius = 9f;

    private const float FooterTopMargin = 18f;
    private const float FooterRuleGap = 16f;
    private const float FooterH = 30f;
    private const float ToggleGap = 9f;

    private const float KickerTracking = 2.5f;
    private const float StatusTracking = 1.38f;
    private const float UnitTracking = 4.08f;
    private const float SectionTracking = 2.52f;
    private const float ToggleTracking = 1.1f;
    private const float LiveTracking = 1.26f;

    private const int MinFormHeight = 380;
    private const int MaxFormHeight = 650;

    // The demo always shows autostart enabled - there's no ninth argument on
    // Render for it, and "on" is the more informative state to show off the
    // toggle's filled/knob-right look.
    private const bool AutostartOnForDemo = true;

    /// <summary>Rendered bitmap width in pixels: FormWidth (440) * Scale (2).</summary>
    public const int Width = 880;

    /// <summary>Rendered bitmap height in pixels: the form's clamped logical height * Scale.</summary>
    public static readonly int Height = (int)MathF.Round(ComputeHeight() * Scale);

    /// <summary>
    /// Renders one frame. rpm and tempC values are plain numbers - there is no
    /// "unavailable" state here (this tool has no hardware to fail to read
    /// from); coreTemps.Length sets the core-column count. activeModeIndex is
    /// 0=Quiet, 1=Balanced, 2=Performance, 3=Full Speed. Caller disposes the bitmap.
    /// </summary>
    public Bitmap Render(int rpm, int[] coreTemps, int activeModeIndex, IReadOnlyList<int> rpmHistory, int tjmax)
    {
        ArgumentNullException.ThrowIfNull(coreTemps);
        ArgumentNullException.ThrowIfNull(rpmHistory);
        if (coreTemps.Length == 0)
            throw new ArgumentException("At least one core temperature is required.", nameof(coreTemps));
        if (activeModeIndex is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(activeModeIndex), activeModeIndex,
                "Expected 0-3 (Quiet/Balanced/Performance/FullSpeed).");
        var selection = (FanSelection)activeModeIndex;

        var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            g.Clear(Paper);
            g.ScaleTransform(Scale, Scale); // draw in the same logical units DashboardForm does

            float y = PadTop;
            y = DrawHeader(g, y, selection);
            y += HeroTopMargin;
            y = DrawHero(g, y, rpm, rpmHistory);
            y += HeroBottomMargin;

            y += SectionTopMargin;
            DrawSectionLabel(g, y, "CORES", "°C");
            y += SectionLabelHeight + SectionBottomMargin;
            y = DrawCoreGraph(g, y, coreTemps);

            y += SectionTopMargin;
            DrawSectionLabel(g, y, "MODE", "· RESTART APPLIES FULL SPEED");
            y += SectionLabelHeight + SectionBottomMargin;
            y = DrawModeKeys(g, y, selection);

            y += FooterTopMargin;
            DrawFooter(g, y, tjmax);

            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
    }

    // Mirrors DashboardForm.ComputeHeight with cpuAvailable always true and
    // withBanner always false (this renderer never draws the restart banner).
    private static float ComputeHeight()
    {
        float y = PadTop;
        y += HeaderH;
        y += HeroTopMargin + HeroBlockHeight + HeroBottomMargin;
        y += SectionTopMargin + SectionLabelHeight + SectionBottomMargin; // CORES / degC
        y += GraphHeight;
        y += SectionTopMargin + SectionLabelHeight + SectionBottomMargin; // MODE / hint
        y += KeyHeight;
        y += FooterTopMargin + FooterRuleGap + FooterH;
        y += PadBottom;
        return Math.Clamp(y, MinFormHeight, MaxFormHeight);
    }

    // ---- drawing, top to bottom - ported from DashboardForm's OnPaint helpers ----

    private static float DrawHeader(Graphics g, float y, FanSelection selection)
    {
        DrawTracked(g, "FAN CONTROL", KickerFont, InkBrush, ContentLeft, y, KickerTracking);

        string statusLabel = LabelFor(selection);
        Brush statusBrush = AccentBrush; // always a known selection - no "UNKNOWN" state here

        float statusW = MeasureTracked(g, statusLabel, StatusFont, StatusTracking);
        float statusX = ContentRight - statusW;
        float dotX = statusX - HeaderDotGap - HeaderDotSize;
        SizeF statusLine = g.MeasureString(statusLabel, StatusFont);
        float dotY = y + (statusLine.Height - HeaderDotSize) / 2f;

        g.FillEllipse(statusBrush, dotX, dotY, HeaderDotSize, HeaderDotSize);
        DrawTracked(g, statusLabel, StatusFont, statusBrush, statusX, y, StatusTracking);

        return y + HeaderH;
    }

    private static float DrawHero(Graphics g, float y, int rpm, IReadOnlyList<int> history)
    {
        string figure = rpm.ToString(CultureInfo.InvariantCulture);

        float figBottom = y + HeroBlockHeight;
        SizeF figSize = g.MeasureString(figure, HeroFont);
        g.DrawString(figure, HeroFont, InkBrush, ContentLeft, figBottom - figSize.Height);

        // Right-hand stack (unit label / sparkline / "live" tag), bottom-anchored
        // against the hero figure the same way the approved layout is.
        float stackBottom = figBottom - HeroRightPad;

        const string liveTag = "live";
        SizeF liveSize = g.MeasureString(liveTag, LiveTagFont);
        float liveW = MeasureTracked(g, liveTag, LiveTagFont, LiveTracking);
        float liveY = stackBottom - liveSize.Height;
        DrawTracked(g, liveTag, LiveTagFont, MutedBrush, ContentRight - liveW, liveY, LiveTracking);

        float sparkBottom = liveY - SparkToLiveGap;
        float sparkTop = sparkBottom - SparkHeight;
        var sparkRect = new RectangleF(ContentRight - SparkWidth, sparkTop, SparkWidth, SparkHeight);
        DrawSparkline(g, history, sparkRect);

        const string unitLabel = "RPM";
        SizeF unitSize = g.MeasureString(unitLabel, HeroUnitFont);
        float unitW = MeasureTracked(g, unitLabel, HeroUnitFont, UnitTracking);
        float unitY = sparkTop - UnitToSparkGap - unitSize.Height;
        DrawTracked(g, unitLabel, HeroUnitFont, MutedBrush, ContentRight - unitW, unitY, UnitTracking);

        return figBottom;
    }

    // A small flat sparkline of the rpm history - gaps (negative samples)
    // are skipped rather than breaking the line into runs.
    private static void DrawSparkline(Graphics g, IReadOnlyList<int> history, RectangleF rect)
    {
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (int v in history)
        {
            if (v < 0)
                continue;
            if (v < lo) lo = v;
            if (v > hi) hi = v;
        }
        if (hi < lo)
            return; // no samples yet - an empty sparkline beats a fake one

        // Keep a near-constant reading as a calm, centred, near-flat line
        // instead of amplifying a few rpm of jitter into a square wave.
        const int minSpan = 400;
        if (hi - lo < minSpan)
        {
            int mid = (lo + hi) / 2;
            lo = mid - minSpan / 2;
            hi = mid + minSpan / 2;
        }

        int n = history.Count;
        var points = new List<PointF>(n);
        for (int i = 0; i < n; i++)
        {
            if (history[i] < 0)
                continue;
            float x = rect.X + (n <= 1 ? 0f : rect.Width * i / (n - 1));
            float py = rect.Bottom - (history[i] - lo) / (float)(hi - lo) * rect.Height;
            points.Add(new PointF(x, py));
        }

        if (points.Count >= 2)
            g.DrawLines(SparklinePen, points.ToArray());
        if (points.Count >= 1)
        {
            PointF last = points[^1];
            g.FillEllipse(AccentBrush, last.X - SparkDotRadius, last.Y - SparkDotRadius, SparkDotRadius * 2f, SparkDotRadius * 2f);
        }
    }

    private static void DrawSectionLabel(Graphics g, float y, string left, string right)
    {
        DrawTracked(g, left, LabelFont, MutedBrush, ContentLeft, y, SectionTracking);
        if (right.Length == 0)
            return;
        float w = MeasureTracked(g, right, LabelFont, SectionTracking);
        DrawTracked(g, right, LabelFont, MutedBrush, ContentRight - w, y, SectionTracking);
    }

    private static float DrawCoreGraph(Graphics g, float y, int[] coreTemps)
    {
        int coreCount = coreTemps.Length;
        float colWidth = (ContentWidth - CoreColumnGap * (coreCount - 1)) / coreCount;
        for (int i = 0; i < coreCount; i++)
        {
            var col = new RectangleF(ContentLeft + i * (colWidth + CoreColumnGap), y, colWidth, GraphHeight);
            DrawCoreColumn(g, col, i, coreTemps[i]);
        }
        return y + GraphHeight;
    }

    // Temp number on top, heat-mapped bar in the middle, core id underneath.
    // All columns share the same bottom edge, so the bars are bottom-aligned
    // on one baseline and the ids line up in a row regardless of bar height.
    private static void DrawCoreColumn(Graphics g, RectangleF col, int coreIndex, int tempC)
    {
        bool hasTemp = tempC >= 0;
        bool hot = hasTemp && tempC >= HotThreshold;

        // core id pinned to the shared bottom baseline
        string idLabel = "C" + coreIndex.ToString(CultureInfo.InvariantCulture);
        SizeF idSize = g.MeasureString(idLabel, CoreIdFont);
        float idY = col.Bottom - idSize.Height;
        g.DrawString(idLabel, CoreIdFont, MutedBrush, col.X + (col.Width - idSize.Width) / 2f, idY);

        // temperature pinned to the top, so the numbers read as one aligned row
        string numLabel = hasTemp ? tempC.ToString(CultureInfo.InvariantCulture) : "-";
        Font numFont = hot ? CoreNumHotFont : CoreNumFont;
        Brush numBrush = hot ? AccentBrush : hasTemp ? InkBrush : MutedBrush;
        SizeF numSize = g.MeasureString(numLabel, numFont);
        float numY = col.Y;
        g.DrawString(numLabel, numFont, numBrush, col.X + (col.Width - numSize.Width) / 2f, numY);

        // bar fills the framed area between the number row and the id baseline;
        // its height is the core's fraction of that frame, so cool reads short
        float barBottom = idY - CoreIdGap;
        float barAreaTop = numY + numSize.Height + CoreNumGap;
        float barArea = barBottom - barAreaTop;
        float frac = hasTemp ? Math.Clamp((tempC - BarMinTemp) / BarTempSpan, 0f, 1f) : 0f;
        float barHeight = frac * barArea;

        if (barHeight > 0.5f && barArea > 0f)
        {
            float barWidth = Math.Min(col.Width, CoreBarMaxWidth);
            float barX = col.X + (col.Width - barWidth) / 2f;
            Brush barBrush = !hasTemp ? MutedBrush : HeatBrush(tempC);
            using var barPath = TopRoundedRect(new RectangleF(barX, barBottom - barHeight, barWidth, barHeight), CoreBarRadius);
            g.FillPath(barBrush, barPath);
        }
    }

    private static Brush HeatBrush(int tempC)
        => tempC >= HotThreshold ? AccentBrush : tempC >= WarmThreshold ? BarWarmBrush : BarCoolBrush;

    private static float DrawModeKeys(Graphics g, float y, FanSelection active)
    {
        FanSelection[] order = { FanSelection.Quiet, FanSelection.Balanced, FanSelection.Performance, FanSelection.FullSpeed };
        float keyWidth = (ContentWidth - KeyGap * (order.Length - 1)) / order.Length;
        float x = ContentLeft;

        foreach (FanSelection sel in order)
        {
            var rect = new RectangleF(x, y, keyWidth, KeyHeight);
            using var path = RoundedRect(rect, KeyRadius);
            bool activeKey = active == sel;
            if (activeKey)
            {
                g.FillPath(AccentBrush, path);
            }
            else
            {
                g.FillPath(KeyInactiveBgBrush, path);
                g.DrawPath(KeyBorderPen, path);
            }

            string label = LabelFor(sel);
            Brush textBrush = activeKey ? WhiteBrush : KeyInactiveTextBrush;
            SizeF size = g.MeasureString(label, LabelFont);
            g.DrawString(label, LabelFont, textBrush,
                x + (keyWidth - size.Width) / 2f,
                y + (KeyHeight - size.Height) / 2f);

            x += keyWidth + KeyGap;
        }

        return y + KeyHeight;
    }

    private static string LabelFor(FanSelection sel)
        => sel == FanSelection.FullSpeed ? "FULL SPEED" : sel.ToString().ToUpperInvariant();

    private static void DrawFooter(Graphics g, float y, int tjmax)
    {
        g.DrawLine(RulePen, ContentLeft, y, ContentRight, y);
        float rowY = y + FooterRuleGap;
        float rowMid = rowY + FooterH / 2f;

        const float swW = 30f, swH = 17f;
        var swRect = new RectangleF(ContentLeft, rowMid - swH / 2f, swW, swH);
        DrawToggleSwitch(g, swRect, AutostartOnForDemo);

        const string toggleLabel = "START WITH WINDOWS";
        SizeF toggleSize = g.MeasureString(toggleLabel, ToggleFont);
        float toggleX = swRect.Right + ToggleGap;
        DrawTracked(g, toggleLabel, ToggleFont, KeyInactiveTextBrush, toggleX, rowMid - toggleSize.Height / 2f, ToggleTracking);

        string stamp = "M70t";
        if (tjmax > 0)
            stamp += " · Tjmax " + tjmax.ToString(CultureInfo.InvariantCulture);
        SizeF stampSize = g.MeasureString(stamp, StampFont);
        g.DrawString(stamp, StampFont, MutedBrush, ContentRight - stampSize.Width, rowMid - stampSize.Height / 2f);
    }

    private static void DrawToggleSwitch(Graphics g, RectangleF rect, bool on)
    {
        using var path = RoundedRect(rect, rect.Height / 2f);
        g.FillPath(on ? AccentBrush : KeyInactiveBgBrush, path);

        const float knobPad = 2f;
        float knobD = rect.Height - knobPad * 2f;
        float knobX = on ? rect.Right - knobPad - knobD : rect.X + knobPad;
        g.FillEllipse(WhiteBrush, knobX, rect.Y + knobPad, knobD, knobD);
    }

    // ---- text tracking + geometry helpers - identical to DashboardForm's ----

    private static float CharWidth(Graphics g, char c, Font font)
        => c == ' ' ? font.Size * 0.28f : g.MeasureString(c.ToString(), font, int.MaxValue, TightFormat).Width;

    private static float MeasureTracked(Graphics g, string text, Font font, float tracking)
    {
        if (text.Length == 0)
            return 0f;
        float w = 0f;
        foreach (char c in text)
            w += CharWidth(g, c, font);
        return w + tracking * (text.Length - 1);
    }

    private static void DrawTracked(Graphics g, string text, Font font, Brush brush, float x, float y, float tracking)
    {
        float cx = x;
        foreach (char c in text)
        {
            if (c != ' ')
                g.DrawString(c.ToString(), font, brush, cx, y, TightFormat);
            cx += CharWidth(g, c, font) + tracking;
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

    // Rounded top corners, square bottom - the core-graph bar shape.
    private static GraphicsPath TopRoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2f;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
        path.AddLine(rect.Right, rect.Bottom, rect.X, rect.Bottom);
        path.CloseFigure();
        return path;
    }
}
