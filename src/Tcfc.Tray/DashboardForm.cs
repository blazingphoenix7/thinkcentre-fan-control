using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Tcfc.Core;

namespace Tcfc.Tray;

/// <summary>
/// The main dashboard window: a live "hardware map" control panel drawn as a
/// blueprint of the machine itself - live fan speed read off the EC, the
/// read-only handshake, per-core CPU temps, a fan-mode selector, a restart
/// banner for the BIOS-gated Full Speed setting, and a drafting-style title
/// block carrying the ACTUAL detected board number. Custom-painted, no child
/// controls; clickable regions are plain hit-tested rectangles recorded during
/// <see cref="OnPaint"/>.
///
/// Ownership: does not own or dispose <c>ec</c> or <c>cpu</c>. The tray creates
/// both (alongside its own EcReader) and disposes them in its own Dispose;
/// this form only ever reads from whichever references it is given, and either
/// may be null when that hardware path is unavailable. The board string is
/// read once by the tray (WMI is slow) and handed in - null means WMI did not
/// report a board.
/// </summary>
internal sealed class DashboardForm : Form
{
    // ---- palette: the approved flat "blueprint" look. Deep navy ground, pale
    // cyan ink, one orange accent for anything live/active/hot. No gradients. ----
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

    // Bahnschrift for the hero figure, keys and title-block values; Cascadia
    // Mono for every technical label and live figure (temps, core ids, chip
    // handshake, drafting captions). Named static instances (not synthetic
    // FontStyle.Bold) so the weight is the font's real drawn weight.
    private static readonly Font HeroFont = new("Bahnschrift SemiBold", 64f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font KickerFont = new("Bahnschrift SemiBold", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font HeroUnitFont = new("Bahnschrift", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font KeyFont = new("Bahnschrift SemiBold", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font BannerFont = new("Bahnschrift SemiBold", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CellValueFont = new("Bahnschrift SemiBold", 10f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static readonly Font CaptionFont = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font ChipFont = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CoreNumFont = new("Cascadia Mono", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CoreNumHotFont = new("Cascadia Mono", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font CoreIdFont = new("Cascadia Mono", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CellLabelFont = new("Cascadia Mono", 8f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font ToggleFont = new("Cascadia Mono", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);

    // Cached because OnPaint runs repeatedly while the window is visible; built
    // once instead of per frame.
    private static readonly SolidBrush InkBrush = new(Ink);
    private static readonly SolidBrush MutedBrush = new(Muted);
    private static readonly SolidBrush FaintBrush = new(Faint);
    private static readonly SolidBrush AccentBrush = new(Accent);
    private static readonly SolidBrush AmberBrush = new(Amber);
    private static readonly SolidBrush WhiteBrush = new(White);
    private static readonly SolidBrush RestartBannerFillBrush = new(Color.FromArgb(26, Accent));

    private static readonly Pen RulePen = new(Rule, 1f);
    private static readonly Pen GridPen = new(GridLine, 1f);
    private static readonly Pen BoxPen = new(BoxLine, 1f);
    private static readonly Pen KeyBorderPen = new(Faint, 1.25f);
    private static readonly Pen ChipBoxPen = new(Faint, 1f);
    private static readonly Pen RestartBannerBorderPen = new(Accent, 1.25f);

    // GDI+ has no letter-spacing primitive. Tracked (uppercase, wide-set)
    // labels are drawn one glyph at a time with a fixed advance added after
    // each one; GenericTypographic gives tight, predictable glyph metrics.
    private static readonly StringFormat TightFormat = new(StringFormat.GenericTypographic);

    private const int FormWidth = 520;
    // Overall size knob. Logical zoom at 96 DPI; the real paint-time scale is
    // this times the monitor's DPI ratio (see EffScale), so the whole window,
    // frame plus text and spacing together, scales as one and stays a
    // consistent physical size on any display.
    private const float UiScale = 1.15f;

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
    private const float UnavailableBoxH = 40f;
    private const float CoresToMode = 22f;

    private const float ModeCaptionGap = 10f;
    private const float KeyH = 38f;
    private const float KeyGap = 7f;
    private const float KeyRadius = 3f;

    private const float BannerGap = 14f;
    private const float BannerH = 48f;
    private const float BannerRadius = 4f;
    private const float BannerPadX = 16f;
    private const float BannerPillPadX = 14f;
    private const float BannerPillMarginY = 8f;

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
    private const float BannerTracking = 1.4f;
    private const float ToggleTracking = 1.1f;

    private const int WarmThreshold = 89;
    private const int HotThreshold = 93;

    private const int MinFormHeight = 380;
    private const int MaxFormHeight = 760;

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private readonly EcReader? _ec;
    private readonly CpuTemps? _cpu;
    private readonly bool _cpuAvailable;
    private readonly int _coreCount; // sized from Environment.ProcessorCount, not a live PawnIO read
    private readonly string? _board;  // the ACTUAL Win32_BaseBoard.Product, read once by the tray; null = unreported
    private readonly System.Windows.Forms.Timer _timer;
    private int _baseHeight; // unscaled content height; ClientSize is this * EffScale (grows when the banner shows)

    private int _lastRpm = -1;
    private int[]? _lastCoreTemps;
    private int _tickCount; // gates the per-core temp read in DoTick to every other tick
    private FanSelection? _currentSelection; // null until the first successful WMI read
    private bool _biosFullSpeed;
    private bool _autostartEnabled;
    private bool _restartPending; // drives the banner and the window's extra height

    // Hit-test rectangles, recorded fresh on every OnPaint.
    private RectangleF _quietKeyRect;
    private RectangleF _balancedKeyRect;
    private RectangleF _performanceKeyRect;
    private RectangleF _fullSpeedKeyRect;
    private RectangleF _restartNowRect;
    private RectangleF _autostartToggleRect;

    public DashboardForm(EcReader? ec, CpuTemps? cpu, string? board)
    {
        _ec = ec;
        _cpu = cpu;
        _board = board;
        _cpuAvailable = _cpu is not null;
        _coreCount = _cpuAvailable ? Environment.ProcessorCount : 0;

        Text = "ThinkCentre Fan Control";
        if (AppIcon.Load() is { } appIcon)
            Icon = appIcon;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Paper;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None; // we do all DPI scaling ourselves via EffScale
        _baseHeight = ComputeHeight(_cpuAvailable, withBanner: false);
        ApplyClientSize();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += DoTick;

        RefreshCachedState();
    }

    // Mirrors the OnPaint layout cursor below - keep the two in sync. The
    // restart-banner row is only counted when the banner is actually showing,
    // so there is no dead space under the window in the common (no-restart-
    // pending) state; DoTick regrows it if a restart becomes pending.
    private static int ComputeHeight(bool cpuAvailable, bool withBanner)
    {
        float y = PadTop;
        y += HeaderH + HeaderRuleGap;
        y += HeaderToHero;
        y += HeroCaptionH + HeroCaptionGap + HeroBlockH;
        y += HeroToCores;
        y += SectionCaptionH + SectionCaptionGap;                 // CPU CORE TEMPERATURES
        y += cpuAvailable ? CoreBoxH : UnavailableBoxH;
        y += CoresToMode;
        y += SectionCaptionH + ModeCaptionGap;                    // MODE
        y += KeyH;
        if (withBanner)
            y += BannerGap + BannerH;
        y += AutostartGap + AutostartH;
        y += TitleBlockGap + TitleBlockH;
        y += PadBottom;
        return (int)Math.Clamp(y, MinFormHeight, MaxFormHeight);
    }

    // Total device-pixel zoom: the logical UiScale times the monitor's DPI ratio,
    // so the window is the same physical size on a 4K/150% display as on a 1080p one.
    private float EffScale => UiScale * DeviceDpi / 96f;

    private void ApplyClientSize()
    {
        ClientSize = new Size(
            (int)MathF.Round(FormWidth * EffScale),
            (int)MathF.Round(_baseHeight * EffScale));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            int useImmersiveDarkMode = 1; // blueprint navy ground reads as a dark theme; ask for the dark title bar
            DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref useImmersiveDarkMode, sizeof(int));
        }
        catch
        {
            // best effort - older Windows builds don't support this attribute
        }

        // DeviceDpi is only reliable once the handle exists; size to the real DPI now.
        ApplyClientSize();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            RefreshCachedState();
            DoTick(this, EventArgs.Empty); // don't sit blank for a second on open
            _timer.Start();
        }
        else
        {
            _timer.Stop(); // no point painting a hidden window
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Close-to-tray: the user clicking X just hides the window. The
            // tray disposes this form for real when the app actually exits.
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    // Per-tick work is cheap hardware reads only - no WMI. EcReader.Rpm() and
    // CpuTemps.PerCore() are both direct register/MSR reads.
    private void DoTick(object? sender, EventArgs e)
    {
        int rpm;
        try
        {
            rpm = _ec?.Rpm() ?? -1;
        }
        catch
        {
            rpm = -1; // keep the window alive; next tick retries
        }
        _lastRpm = rpm;

        // PerCore() pins affinity across every core to take the reading, and
        // temps drift slowly, so it only runs on alternating ticks; the last
        // sample carries over untouched on the ticks in between.
        if (_cpu is not null && _tickCount % 2 == 0)
        {
            try
            {
                _lastCoreTemps = _cpu.PerCore();
            }
            catch
            {
                // keep the previous reading; next tick retries
            }
        }
        _tickCount++;

        // The restart banner shows only when the BIOS setting and the actual fan
        // disagree. Grow or shrink the window to fit it, so there is never a gap.
        bool pending = FanControl.IsRestartPending(_biosFullSpeed, _lastRpm);
        if (pending != _restartPending)
        {
            _restartPending = pending;
            _baseHeight = ComputeHeight(_cpuAvailable, pending);
            ApplyClientSize();
        }

        Invalidate();
    }

    // WMI reads are slow, so these are only ever refreshed on show and right
    // after a user Set action - never once per second.
    private void RefreshCachedState()
    {
        try
        {
            _currentSelection = FanControl.GetCurrent();
            _biosFullSpeed = _currentSelection == FanSelection.FullSpeed;
        }
        catch
        {
            // keep whatever was cached before; a stale reading beats a fake one
        }

        try
        {
            _autostartEnabled = Autostart.IsEnabled();
        }
        catch
        {
            _autostartEnabled = false;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        g.Clear(Paper);
        g.ScaleTransform(EffScale, EffScale); // draw in logical units; scaled by zoom * monitor DPI
        DrawBlueprintGrid(g);

        float y = PadTop;
        y = DrawHeader(g, y);
        y += HeaderToHero;
        y = DrawHero(g, y, _lastRpm);
        y += HeroToCores;

        DrawSectionCaption(g, y, "CPU CORE TEMPERATURES · °C");
        y += SectionCaptionH + SectionCaptionGap;
        y = DrawCoreBox(g, y);
        y += CoresToMode;

        DrawSectionCaption(g, y, "MODE");
        y += SectionCaptionH + ModeCaptionGap;
        y = DrawModeKeys(g, y);

        if (_restartPending)
        {
            y += BannerGap;
            y = DrawRestartBanner(g, y);
        }
        else
        {
            _restartNowRect = RectangleF.Empty;
        }

        y += AutostartGap;
        y = DrawAutostartRow(g, y);
        y += TitleBlockGap;
        DrawTitleBlock(g, y);
    }

    // ---- drawing, top to bottom ----

    // The faint ruled ground that makes the window read as a drawing sheet.
    // Fixed-pitch lines only; a static backdrop that never changes between frames.
    private void DrawBlueprintGrid(Graphics g)
    {
        for (float gx = GridStep; gx < FormWidth; gx += GridStep)
            g.DrawLine(GridPen, gx, 0f, gx, _baseHeight);
        for (float gy = GridStep; gy < _baseHeight; gy += GridStep)
            g.DrawLine(GridPen, 0f, gy, FormWidth, gy);
    }

    private float DrawHeader(Graphics g, float y)
    {
        DrawTracked(g, "FAN CONTROL · HARDWARE MAP", KickerFont, InkBrush, ContentLeft, y, KickerTracking);

        // The ACTUAL detected board, right-aligned. Board.Product() (WMI
        // Win32_BaseBoard.Product) was read once by the tray and handed in;
        // null means WMI reported nothing, so we say so rather than fake a value.
        string boardText = _board is null ? "BOARD N/A" : "M70T · BOARD " + _board;
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

    // A framed instrument reading out the per-core MSR temps: number over core
    // id, one column per logical processor, inside a drafting box. Cool reads
    // pale, warm reads amber, hot reads (>= HotThreshold) orange.
    private float DrawCoreBox(Graphics g, float y)
    {
        if (_cpu is null)
        {
            var box = new RectangleF(ContentLeft, y, ContentWidth, UnavailableBoxH);
            g.DrawRectangle(BoxPen, box.X, box.Y, box.Width, box.Height);
            const string msg = "PER-CORE TEMPS UNAVAILABLE";
            float w = MeasureTracked(g, msg, CaptionFont, SectionTracking);
            SizeF s = g.MeasureString(msg, CaptionFont);
            DrawTracked(g, msg, CaptionFont, MutedBrush,
                box.X + (box.Width - w) / 2f, box.Y + (box.Height - s.Height) / 2f, SectionTracking);
            return y + UnavailableBoxH;
        }

        var frame = new RectangleF(ContentLeft, y, ContentWidth, CoreBoxH);
        g.DrawRectangle(BoxPen, frame.X, frame.Y, frame.Width, frame.Height);

        float colWidth = ContentWidth / _coreCount;
        for (int i = 0; i < _coreCount; i++)
        {
            float colX = ContentLeft + i * colWidth;
            if (i > 0)
                g.DrawLine(BoxPen, colX, frame.Y + 8f, colX, frame.Bottom - 8f);

            int tempC = _lastCoreTemps is not null && i < _lastCoreTemps.Length ? _lastCoreTemps[i] : -1;
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

    private float DrawModeKeys(Graphics g, float y)
    {
        FanSelection[] order = { FanSelection.Quiet, FanSelection.Balanced, FanSelection.Performance, FanSelection.FullSpeed };
        float keyWidth = (ContentWidth - KeyGap * (order.Length - 1)) / order.Length;
        float x = ContentLeft;

        foreach (FanSelection sel in order)
        {
            var rect = new RectangleF(x, y, keyWidth, KeyH);
            using var path = RoundedRect(rect, KeyRadius);
            bool active = _currentSelection == sel;
            if (active)
            {
                g.FillPath(AccentBrush, path);
            }
            else
            {
                g.DrawPath(KeyBorderPen, path);
            }

            string label = LabelFor(sel);
            Brush textBrush = active ? WhiteBrush : MutedBrush;
            float labelW = MeasureTracked(g, label, KeyFont, KeyTracking);
            SizeF size = g.MeasureString(label, KeyFont);
            DrawTracked(g, label, KeyFont, textBrush,
                x + (keyWidth - labelW) / 2f, y + (KeyH - size.Height) / 2f, KeyTracking);

            switch (sel)
            {
                case FanSelection.Quiet: _quietKeyRect = rect; break;
                case FanSelection.Balanced: _balancedKeyRect = rect; break;
                case FanSelection.Performance: _performanceKeyRect = rect; break;
                case FanSelection.FullSpeed: _fullSpeedKeyRect = rect; break;
            }

            x += keyWidth + KeyGap;
        }

        return y + KeyH;
    }

    private static string LabelFor(FanSelection sel)
        => sel == FanSelection.FullSpeed ? "FULL SPEED" : sel.ToString().ToUpperInvariant();

    private float DrawRestartBanner(Graphics g, float y)
    {
        var rect = new RectangleF(ContentLeft, y, ContentWidth, BannerH);
        using var path = RoundedRect(rect, BannerRadius);
        g.FillPath(RestartBannerFillBrush, path);
        g.DrawPath(RestartBannerBorderPen, path);

        // The banner only ever shows while entering Full Speed (leaving is live).
        const string message = "RESTART TO APPLY FULL SPEED";
        SizeF lineSize = g.MeasureString(message, BannerFont);
        DrawTracked(g, message, BannerFont, InkBrush,
            rect.X + BannerPadX, rect.Y + (rect.Height - lineSize.Height) / 2f, BannerTracking);

        const string pillLabel = "RESTART NOW";
        SizeF pillTextSize = g.MeasureString(pillLabel, KeyFont);
        float pillW = pillTextSize.Width + BannerPillPadX * 2f;
        float pillH = BannerH - BannerPillMarginY * 2f;
        var pillRect = new RectangleF(rect.Right - pillW - BannerPadX, rect.Y + BannerPillMarginY, pillW, pillH);
        using var pillPath = RoundedRect(pillRect, 3f);
        g.FillPath(AccentBrush, pillPath);
        g.DrawString(pillLabel, KeyFont, WhiteBrush,
            pillRect.X + (pillRect.Width - pillTextSize.Width) / 2f,
            pillRect.Y + (pillRect.Height - pillTextSize.Height) / 2f);

        _restartNowRect = pillRect;
        return y + BannerH;
    }

    // A slim toggle row, blueprint-styled, kept as its own affordance so the
    // autostart control stays obviously a switch (the title block below is
    // read-only reference data).
    private float DrawAutostartRow(Graphics g, float y)
    {
        float rowMid = y + AutostartH / 2f;
        const float swW = 30f, swH = 17f;
        var swRect = new RectangleF(ContentLeft, rowMid - swH / 2f, swW, swH);
        DrawToggleSwitch(g, swRect, _autostartEnabled);

        const string toggleLabel = "START WITH WINDOWS";
        SizeF toggleSize = g.MeasureString(toggleLabel, ToggleFont);
        float toggleX = swRect.Right + ToggleGap;
        DrawTracked(g, toggleLabel, ToggleFont, MutedBrush, toggleX, rowMid - toggleSize.Height / 2f, ToggleTracking);
        float toggleW = MeasureTracked(g, toggleLabel, ToggleFont, ToggleTracking);
        _autostartToggleRect = new RectangleF(ContentLeft, y, toggleX + toggleW - ContentLeft, AutostartH);
        return y + AutostartH;
    }

    // The drafting title block: a bordered strip of read-only reference cells,
    // the last carrying the ACTUAL detected board (LENOVO <product>).
    private void DrawTitleBlock(Graphics g, float y)
    {
        var frame = new RectangleF(ContentLeft, y, ContentWidth, TitleBlockH);
        g.DrawRectangle(BoxPen, frame.X, frame.Y, frame.Width, frame.Height);

        int hottestC = -1;
        if (_lastCoreTemps is not null)
            foreach (int t in _lastCoreTemps)
                if (t > hottestC) hottestC = t;
        string hottest = hottestC >= 0 ? hottestC.ToString(CultureInfo.InvariantCulture) + " °C" : "N/A";
        string boardValue = _board is null ? "UNSUPPORTED" : "LENOVO " + _board;
        (string label, string value)[] cells =
        {
            ("READS", "RPM + " + _coreCount.ToString(CultureInfo.InvariantCulture) + " CORES"),
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

    // ---- text tracking + geometry helpers ----

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

    // ---- interaction ----

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        PointF pt = new(e.X / EffScale, e.Y / EffScale);

        if (_quietKeyRect.Contains(pt))
            ApplySelection(FanSelection.Quiet);
        else if (_balancedKeyRect.Contains(pt))
            ApplySelection(FanSelection.Balanced);
        else if (_performanceKeyRect.Contains(pt))
            ApplySelection(FanSelection.Performance);
        else if (_fullSpeedKeyRect.Contains(pt))
            ConfirmAndApplyFullSpeed();
        else if (!_restartNowRect.IsEmpty && _restartNowRect.Contains(pt))
            ConfirmAndRestart();
        else if (_autostartToggleRect.Contains(pt))
            ToggleAutostart();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointF pt = new(e.X / EffScale, e.Y / EffScale);
        bool hovering =
            _quietKeyRect.Contains(pt) ||
            _balancedKeyRect.Contains(pt) ||
            _performanceKeyRect.Contains(pt) ||
            _fullSpeedKeyRect.Contains(pt) ||
            (!_restartNowRect.IsEmpty && _restartNowRect.Contains(pt)) ||
            _autostartToggleRect.Contains(pt);
        Cursor = hovering ? Cursors.Hand : Cursors.Default;
    }

    private void ApplySelection(FanSelection selection)
    {
        try
        {
            FanControl.Set(selection);
            RefreshCachedState();
            Invalidate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Fan mode not changed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ConfirmAndApplyFullSpeed()
    {
        DialogResult result = MessageBox.Show(this,
            "Full Speed changes a BIOS setting and only takes effect after a restart. Continue?",
            "Full Speed", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result == DialogResult.Yes)
            ApplySelection(FanSelection.FullSpeed);
    }

    private void ConfirmAndRestart()
    {
        DialogResult result = MessageBox.Show(this,
            "Restart the computer now?",
            "Restart now", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/r /t 0",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Restart failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            if (Autostart.IsEnabled())
                Autostart.Disable();
            else
                Autostart.Enable();
            _autostartEnabled = Autostart.IsEnabled();
            Invalidate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Autostart not changed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
