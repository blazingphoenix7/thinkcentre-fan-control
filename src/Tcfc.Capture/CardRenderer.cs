using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using Tcfc.Core;

namespace Tcfc.Capture;

/// <summary>
/// Headless GDI+ renderer for the fan-control dashboard window, used to build
/// the README demo GIF. This draws exactly what DashboardForm draws - the same
/// "hardware map" blueprint: faint ruled ground, the header, the live-rpm hero
/// with the read-only handshake, the framed per-core temperature box, the mode
/// keys, the autostart toggle, and the drafting title block - ported constant
/// for constant, except every value is a plain argument instead of a live
/// EC/CPU/WMI reading, and there is no restart banner (that state is transient
/// and never holds long enough to be worth a GIF frame). Rendered at
/// <see cref="Scale"/> so the GIF is crisp.
/// </summary>
public sealed class CardRenderer
{
    // ---- palette: identical to DashboardForm's approved flat "blueprint" look.
    // Deep navy ground, pale cyan ink, one orange accent for anything live or
    // hot, amber for the warm middle band. No gradients. ----
    private static readonly Color Paper = Color.FromArgb(13, 33, 55);        // blueprint navy
    private static readonly Color Ink = Color.FromArgb(207, 227, 245);       // pale cyan base text
    private static readonly Color Muted = Color.FromArgb(140, 207, 227, 245);
    private static readonly Color Faint = Color.FromArgb(90, 207, 227, 245);
    private static readonly Color Rule = Color.FromArgb(77, 207, 227, 245);
    private static readonly Color GridLine = Color.FromArgb(15, 207, 227, 245); // the ~28px drawing grid
    private static readonly Color BoxLine = Color.FromArgb(64, 207, 227, 245);
    private static readonly Color Accent = Color.FromArgb(255, 87, 34);
    private static readonly Color Amber = Color.FromArgb(213, 148, 43);
    private static readonly Color White = Color.FromArgb(255, 255, 255);

    // Same named font instances as DashboardForm (not synthetic FontStyle.Bold
    // on the base family) so the drawn weight matches the real window. Sizes are
    // the form's logical pixel sizes; ScaleTransform below blows them up by
    // Scale, the same way DashboardForm's EffScale does for a high-DPI monitor.
    private static readonly Font HeroFont = new("Bahnschrift SemiBold", 64f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font KickerFont = new("Bahnschrift SemiBold", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font HeroUnitFont = new("Bahnschrift", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font KeyFont = new("Bahnschrift SemiBold", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CellValueFont = new("Bahnschrift SemiBold", 10f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static readonly Font CaptionFont = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font ChipFont = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CoreNumFont = new("Cascadia Mono", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CoreNumHotFont = new("Cascadia Mono", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font CoreIdFont = new("Cascadia Mono", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CellLabelFont = new("Cascadia Mono", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font ToggleFont = new("Cascadia Mono", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);

    // Cached once for the same reason DashboardForm caches them: this renders
    // dozens of frames per run, not once.
    private static readonly SolidBrush InkBrush = new(Ink);
    private static readonly SolidBrush MutedBrush = new(Muted);
    private static readonly SolidBrush FaintBrush = new(Faint);
    private static readonly SolidBrush AccentBrush = new(Accent);
    private static readonly SolidBrush AmberBrush = new(Amber);
    private static readonly SolidBrush WhiteBrush = new(White);

    private static readonly Pen RulePen = new(Rule, 1f);
    private static readonly Pen GridPen = new(GridLine, 1f);
    private static readonly Pen BoxPen = new(BoxLine, 1f);
    private static readonly Pen KeyBorderPen = new(Faint, 1.25f);
    private static readonly Pen ChipBoxPen = new(Faint, 1f);

    // GDI+ has no letter-spacing primitive; tracked labels are drawn glyph by
    // glyph with a fixed advance, same as DashboardForm's DrawTracked.
    private static readonly StringFormat TightFormat = new(StringFormat.GenericTypographic);

    private const int FormWidth = 520;

    private const float PadX = 26f;
    private const float PadTop = 24f;
    private const float PadBottom = 22f;
    private const float ContentLeft = PadX;
    private const float ContentRight = FormWidth - PadX;
    private const float ContentWidth = ContentRight - ContentLeft;

    private const float GridStep = 28f; // spacing of the faint blueprint grid

    private const float HeaderH = 16f;
    private const float HeaderRuleGap = 12f;
    private const float HeaderToHero = 18f;

    private const float HeroCaptionH = 12f;
    private const float HeroCaptionGap = 8f;
    private const float HeroBlockH = 72f;
    private const float HeroToCores = 22f;

    private const float SectionCaptionH = 12f;
    private const float SectionCaptionGap = 8f;
    private const float CoreBoxH = 54f;
    private const float CoresToMode = 22f;

    private const float ModeCaptionGap = 10f;
    private const float KeyH = 38f;
    private const float KeyGap = 7f;
    private const float KeyRadius = 3f;

    private const float AutostartGap = 16f;
    private const float AutostartH = 20f;
    private const float ToggleGap = 9f;

    private const float TitleBlockGap = 16f;
    private const float TitleBlockH = 46f;
    private const float CellPadX = 12f;

    private const float KickerTracking = 2f;
    private const float CaptionTracking = 1.6f;
    private const float SectionTracking = 2.2f;
    private const float KeyTracking = 1.1f;
    private const float ToggleTracking = 1.1f;

    private const int WarmThreshold = 89;
    private const int HotThreshold = 93;

    private const int MinFormHeight = 380;
    private const int MaxFormHeight = 760;

    // The demo always shows autostart enabled - there is no argument on Render
    // for it, and "on" is the more informative state to show off the toggle's
    // filled/knob-right look.
    private const bool AutostartOnForDemo = true;

    /// <summary>Render scale. 2x the form's logical 520-wide layout, so the GIF is crisp.</summary>
    public const float Scale = 2f;

    // Unscaled logical content height, clamped the same way DashboardForm's
    // ComputeHeight clamps it. Drives both the grid extent and the bitmap size.
    private static readonly float BaseHeight = ComputeHeight();

    /// <summary>Rendered bitmap width in pixels: FormWidth (520) * Scale (2).</summary>
    public const int Width = 1040;

    /// <summary>Rendered bitmap height in pixels: the form's clamped logical height * Scale.</summary>
    public static readonly int Height = (int)MathF.Round(BaseHeight * Scale);

    /// <summary>
    /// Renders one frame. rpm and the coreTemps values are plain numbers -
    /// there is no "unavailable" state here (this tool has no hardware to fail
    /// to read from); coreTemps.Length sets the core-column count. activeModeIndex
    /// is 0=Quiet, 1=Balanced, 2=Performance, 3=Full Speed. board is the detected
    /// Win32_BaseBoard.Product string (null renders as N/A / UNSUPPORTED, which
    /// the demo never passes but the drawing keeps honest). Caller disposes the bitmap.
    /// </summary>
    public Bitmap Render(int rpm, int[] coreTemps, int activeModeIndex, string? board)
    {
        ArgumentNullException.ThrowIfNull(coreTemps);
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
            DrawBlueprintGrid(g);

            float y = PadTop;
            y = DrawHeader(g, y, board);
            y += HeaderToHero;
            y = DrawHero(g, y, rpm);
            y += HeroToCores;

            DrawSectionCaption(g, y, "CPU CORE TEMPERATURES · °C");
            y += SectionCaptionH + SectionCaptionGap;
            y = DrawCoreBox(g, y, coreTemps);
            y += CoresToMode;

            DrawSectionCaption(g, y, "MODE");
            y += SectionCaptionH + ModeCaptionGap;
            y = DrawModeKeys(g, y, selection);

            y += AutostartGap;
            y = DrawAutostartRow(g, y);
            y += TitleBlockGap;
            DrawTitleBlock(g, y, coreTemps, board);

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
        y += HeaderH + HeaderRuleGap;
        y += HeaderToHero;
        y += HeroCaptionH + HeroCaptionGap + HeroBlockH;
        y += HeroToCores;
        y += SectionCaptionH + SectionCaptionGap;                 // CPU CORE TEMPERATURES
        y += CoreBoxH;
        y += CoresToMode;
        y += SectionCaptionH + ModeCaptionGap;                    // MODE
        y += KeyH;
        y += AutostartGap + AutostartH;
        y += TitleBlockGap + TitleBlockH;
        y += PadBottom;
        return Math.Clamp(y, MinFormHeight, MaxFormHeight);
    }

    // ---- drawing, top to bottom - ported from DashboardForm's OnPaint helpers ----

    // The faint ruled ground that makes the window read as a drawing sheet.
    // Fixed-pitch lines only; a static backdrop that never changes between frames.
    private static void DrawBlueprintGrid(Graphics g)
    {
        for (float gx = GridStep; gx < FormWidth; gx += GridStep)
            g.DrawLine(GridPen, gx, 0f, gx, BaseHeight);
        for (float gy = GridStep; gy < BaseHeight; gy += GridStep)
            g.DrawLine(GridPen, 0f, gy, FormWidth, gy);
    }

    private static float DrawHeader(Graphics g, float y, string? board)
    {
        DrawTracked(g, "FAN CONTROL · HARDWARE MAP", KickerFont, InkBrush, ContentLeft, y, KickerTracking);

        // The detected board, right-aligned. null means WMI reported nothing, so
        // we say so rather than fake a value.
        string boardText = board is null ? "BOARD N/A" : "M70T · BOARD " + board;
        float boardW = MeasureTracked(g, boardText, CaptionFont, CaptionTracking);
        DrawTracked(g, boardText, CaptionFont, MutedBrush, ContentRight - boardW, y + 2f, CaptionTracking);

        float ruleY = y + HeaderH + HeaderRuleGap * 0.5f;
        g.DrawLine(RulePen, ContentLeft, ruleY, ContentRight, ruleY);
        return y + HeaderH + HeaderRuleGap;
    }

    private static float DrawHero(Graphics g, float y, int rpm)
    {
        DrawTracked(g, "LIVE FAN SPEED · READ OFF THE CHIP", CaptionFont, MutedBrush, ContentLeft, y, CaptionTracking);
        float figTop = y + HeroCaptionH + HeroCaptionGap;
        float figBottom = figTop + HeroBlockH;

        bool available = rpm >= 0;
        string figure = available ? rpm.ToString(CultureInfo.InvariantCulture) : "-";
        Brush figureBrush = available ? WhiteBrush : MutedBrush;
        SizeF figSize = g.MeasureString(figure, HeroFont);
        g.DrawString(figure, HeroFont, figureBrush, ContentLeft, figBottom - figSize.Height);

        // "RPM" unit sitting on the figure's baseline, just to its right.
        const string unit = "RPM";
        SizeF unitSize = g.MeasureString(unit, HeroUnitFont);
        DrawTracked(g, unit, HeroUnitFont, MutedBrush,
            ContentLeft + figSize.Width + 6f, figBottom - unitSize.Height - 6f, 2f);

        DrawHandshake(g, figBottom);
        return figBottom;
    }

    // The read-only handshake, bottom-aligned to the hero figure: a "FAN CHIP"
    // node, an arrow, and the reply port it hands a byte back on. Under it, the
    // one honest promise this tool makes about the EC: it only ever reads.
    private static void DrawHandshake(Graphics g, float figBottom)
    {
        const string readOnly = "READ-ONLY";
        SizeF roSize = g.MeasureString(readOnly, ChipFont);
        float roW = MeasureTracked(g, readOnly, ChipFont, 1f);
        DrawTracked(g, readOnly, ChipFont, FaintBrush, ContentRight - roW, figBottom - roSize.Height, 1f);

        const string node = "FAN CHIP";
        const string arrow = "▸";
        const string reply = "REPLY 0x62";
        float nodeTextW = g.MeasureString(node, ChipFont).Width;
        float boxW = nodeTextW + 16f;
        float boxH = g.MeasureString(node, ChipFont).Height + 8f;
        float arrowW = g.MeasureString(arrow, ChipFont).Width;
        float replyW = g.MeasureString(reply, ChipFont).Width;
        const float gap = 8f;
        float total = boxW + gap + arrowW + gap + replyW;

        float rowBottom = figBottom - roSize.Height - 8f;
        float rowTop = rowBottom - boxH;
        float x = ContentRight - total;

        var boxRect = new RectangleF(x, rowTop, boxW, boxH);
        g.DrawRectangle(ChipBoxPen, boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height);
        g.DrawString(node, ChipFont, InkBrush, x + 8f, rowTop + (boxH - g.MeasureString(node, ChipFont).Height) / 2f);
        x += boxW + gap;

        float midY = rowTop + (boxH - g.MeasureString(arrow, ChipFont).Height) / 2f;
        g.DrawString(arrow, ChipFont, AccentBrush, x, midY);
        x += arrowW + gap;
        g.DrawString(reply, ChipFont, MutedBrush, x, rowTop + (boxH - g.MeasureString(reply, ChipFont).Height) / 2f);
    }

    private static void DrawSectionCaption(Graphics g, float y, string text)
        => DrawTracked(g, text, CaptionFont, MutedBrush, ContentLeft, y, SectionTracking);

    // A framed instrument reading out the per-core temps: number over core id,
    // one column per logical processor, inside a drafting box. Cool reads pale,
    // warm reads amber, hot reads (>= HotThreshold) orange.
    private static float DrawCoreBox(Graphics g, float y, int[] coreTemps)
    {
        int coreCount = coreTemps.Length;
        var frame = new RectangleF(ContentLeft, y, ContentWidth, CoreBoxH);
        g.DrawRectangle(BoxPen, frame.X, frame.Y, frame.Width, frame.Height);

        float colWidth = ContentWidth / coreCount;
        for (int i = 0; i < coreCount; i++)
        {
            float colX = ContentLeft + i * colWidth;
            if (i > 0)
                g.DrawLine(BoxPen, colX, frame.Y + 8f, colX, frame.Bottom - 8f);

            int tempC = coreTemps[i];
            bool hasTemp = tempC >= 0;
            bool hot = hasTemp && tempC >= HotThreshold;
            bool warm = hasTemp && tempC >= WarmThreshold;

            string numLabel = hasTemp ? tempC.ToString(CultureInfo.InvariantCulture) : "-";
            Font numFont = hot ? CoreNumHotFont : CoreNumFont;
            Brush numBrush = hot ? AccentBrush : warm ? AmberBrush : hasTemp ? WhiteBrush : MutedBrush;
            SizeF numSize = g.MeasureString(numLabel, numFont);
            g.DrawString(numLabel, numFont, numBrush, colX + (colWidth - numSize.Width) / 2f, frame.Y + 10f);

            string idLabel = "C" + i.ToString(CultureInfo.InvariantCulture);
            SizeF idSize = g.MeasureString(idLabel, CoreIdFont);
            g.DrawString(idLabel, CoreIdFont, FaintBrush, colX + (colWidth - idSize.Width) / 2f, frame.Bottom - idSize.Height - 8f);
        }
        return y + CoreBoxH;
    }

    private static float DrawModeKeys(Graphics g, float y, FanSelection active)
    {
        FanSelection[] order = { FanSelection.Quiet, FanSelection.Balanced, FanSelection.Performance, FanSelection.FullSpeed };
        float keyWidth = (ContentWidth - KeyGap * (order.Length - 1)) / order.Length;
        float x = ContentLeft;

        foreach (FanSelection sel in order)
        {
            var rect = new RectangleF(x, y, keyWidth, KeyH);
            using var path = RoundedRect(rect, KeyRadius);
            bool activeKey = active == sel;
            if (activeKey)
            {
                g.FillPath(AccentBrush, path);
            }
            else
            {
                g.DrawPath(KeyBorderPen, path);
            }

            string label = LabelFor(sel);
            Brush textBrush = activeKey ? WhiteBrush : MutedBrush;
            float labelW = MeasureTracked(g, label, KeyFont, KeyTracking);
            SizeF size = g.MeasureString(label, KeyFont);
            DrawTracked(g, label, KeyFont, textBrush,
                x + (keyWidth - labelW) / 2f, y + (KeyH - size.Height) / 2f, KeyTracking);

            x += keyWidth + KeyGap;
        }

        return y + KeyH;
    }

    private static string LabelFor(FanSelection sel)
        => sel == FanSelection.FullSpeed ? "FULL SPEED" : sel.ToString().ToUpperInvariant();

    // A slim toggle row, blueprint-styled, kept as its own affordance so the
    // autostart control stays obviously a switch (the title block below is
    // read-only reference data). The demo always draws it on.
    private static float DrawAutostartRow(Graphics g, float y)
    {
        float rowMid = y + AutostartH / 2f;
        const float swW = 30f, swH = 17f;
        var swRect = new RectangleF(ContentLeft, rowMid - swH / 2f, swW, swH);
        DrawToggleSwitch(g, swRect, AutostartOnForDemo);

        const string toggleLabel = "START WITH WINDOWS";
        SizeF toggleSize = g.MeasureString(toggleLabel, ToggleFont);
        float toggleX = swRect.Right + ToggleGap;
        DrawTracked(g, toggleLabel, ToggleFont, MutedBrush, toggleX, rowMid - toggleSize.Height / 2f, ToggleTracking);
        return y + AutostartH;
    }

    // The drafting title block: a bordered strip of read-only reference cells,
    // the last carrying the detected board (LENOVO <product>).
    private static void DrawTitleBlock(Graphics g, float y, int[] coreTemps, string? board)
    {
        var frame = new RectangleF(ContentLeft, y, ContentWidth, TitleBlockH);
        g.DrawRectangle(BoxPen, frame.X, frame.Y, frame.Width, frame.Height);

        int hottestC = -1;
        foreach (int t in coreTemps)
            if (t > hottestC) hottestC = t;
        string hottest = hottestC >= 0 ? hottestC.ToString(CultureInfo.InvariantCulture) + " °C" : "N/A";
        string boardValue = board is null ? "UNSUPPORTED" : "LENOVO " + board;
        (string label, string value)[] cells =
        {
            ("READS", "RPM + " + coreTemps.Length.ToString(CultureInfo.InvariantCulture) + " CORES"),
            ("BOARD", boardValue),
            ("HOTTEST", hottest),
            ("DRIVER", "PAWNIO · SIGNED"),
        };

        float cellW = ContentWidth / cells.Length;
        for (int i = 0; i < cells.Length; i++)
        {
            float cellX = frame.X + i * cellW;
            if (i > 0)
                g.DrawLine(BoxPen, cellX, frame.Y, cellX, frame.Bottom);
            g.DrawString(cells[i].label, CellLabelFont, MutedBrush, cellX + CellPadX, frame.Y + 9f);
            g.DrawString(cells[i].value, CellValueFont, InkBrush, cellX + CellPadX, frame.Y + 23f);
        }
    }

    private static void DrawToggleSwitch(Graphics g, RectangleF rect, bool on)
    {
        using var path = RoundedRect(rect, rect.Height / 2f);
        if (on)
            g.FillPath(AccentBrush, path);
        else
            g.DrawPath(KeyBorderPen, path);

        const float knobPad = 2f;
        float knobD = rect.Height - knobPad * 2f;
        float knobX = on ? rect.Right - knobPad - knobD : rect.X + knobPad;
        g.FillEllipse(on ? WhiteBrush : MutedBrush, knobX, rect.Y + knobPad, knobD, knobD);
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
}
