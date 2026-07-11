using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Tcfc.Core;

namespace Tcfc.Tray;

/// <summary>
/// The main dashboard window: a live, interactive control panel with per-core
/// CPU temps, a fan-mode selector, and a restart banner for the BIOS-gated
/// Full Speed setting. Custom-painted, no child controls; clickable regions
/// are plain hit-tested rectangles recorded during <see cref="OnPaint"/>.
///
/// Ownership: does not own or dispose <c>ec</c> or <c>cpu</c>. The tray creates
/// both (alongside its own EcReader) and disposes them in its own Dispose;
/// this form only ever reads from whichever references it is given, and either
/// may be null when that hardware path is unavailable.
/// </summary>
internal sealed class DashboardForm : Form
{
    // ---- palette: the approved flat "bone + orange" look. One accent, used
    // for anything live/active/hot; everything else is ink, muted or rule. ----
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

    // Bahnschrift for the hero figure and every instrument label; Cascadia
    // Mono for every live figure (temps, core ids, the stamp, the live tag).
    // Named static instances (not synthetic FontStyle.Bold) so the weight is
    // the font's real drawn weight, not GDI's embolden.
    private static readonly Font HeroFont = new("Bahnschrift SemiBold", 92f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font KickerFont = new("Bahnschrift SemiBold", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font StatusFont = new("Bahnschrift SemiBold", 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font HeroUnitFont = new("Bahnschrift", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font LabelFont = new("Bahnschrift SemiBold", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font ToggleFont = new("Bahnschrift SemiBold", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font BannerFont = new("Bahnschrift SemiBold", 12f, FontStyle.Regular, GraphicsUnit.Pixel);

    private static readonly Font CoreNumFont = new("Cascadia Mono", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font CoreNumHotFont = new("Cascadia Mono", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font CoreIdFont = new("Cascadia Mono", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font LiveTagFont = new("Cascadia Mono", 9f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font StampFont = new("Cascadia Mono", 9.5f, FontStyle.Regular, GraphicsUnit.Pixel);

    // Cached for the same reason as the fonts above: OnPaint runs repeatedly
    // while the window is visible, so these are built once instead of per frame.
    private static readonly SolidBrush InkBrush = new(Ink);
    private static readonly SolidBrush MutedBrush = new(Muted);
    private static readonly SolidBrush AccentBrush = new(Accent);
    private static readonly SolidBrush WhiteBrush = new(White);
    private static readonly SolidBrush BarCoolBrush = new(BarCool);
    private static readonly SolidBrush BarWarmBrush = new(BarWarm);
    private static readonly SolidBrush KeyInactiveBgBrush = new(KeyInactiveBg);
    private static readonly SolidBrush KeyInactiveTextBrush = new(KeyInactiveText);
    private static readonly SolidBrush RestartBannerFillBrush = new(Color.FromArgb(26, Accent));

    private static readonly Pen RulePen = new(Rule, 1f);
    private static readonly Pen KeyBorderPen = new(Rule, 1.5f);
    private static readonly Pen RestartBannerBorderPen = new(Accent, 1.25f);
    private static readonly Pen SparklinePen = new(Ink, 1.6f)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    // GDI+ has no letter-spacing primitive. Tracked (uppercase, wide-set)
    // labels are drawn one glyph at a time with a fixed advance added after
    // each one; GenericTypographic gives tight, predictable glyph metrics for
    // that. A lone space is never measured through the font - MeasureString on
    // a single space is unreliable across families - it gets a fixed fraction
    // of the em size instead.
    private static readonly StringFormat TightFormat = new(StringFormat.GenericTypographic);

    private const int FormWidth = 440;
    // Logical zoom at 96 DPI; the real paint-time scale is this times the
    // monitor's DPI ratio (see EffScale), so the window is a consistent
    // physical size on any display. Raise this to grow the whole dashboard
    // everywhere; 1 keeps the window at the approved ~440-logical-wide size.
    private const float UiScale = 1f;

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
    private const float UnavailableGraphHeight = 40f;
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

    private const float RestartBannerGap = 14f;
    private const float RestartBannerHeight = 52f;
    private const float BannerRadius = 10f;
    private const float BannerPadX = 16f;
    private const float BannerPillPadX = 14f;
    private const float BannerPillMarginY = 8f;

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
    private const float BannerTracking = 1.4f;

    private const int HistoryCapacity = 60;

    private const int MinFormHeight = 380;
    private const int MaxFormHeight = 650;

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private readonly EcReader? _ec;
    private readonly CpuTemps? _cpu;
    private readonly bool _cpuAvailable;
    private readonly int _coreCount; // sized from Environment.ProcessorCount, not a live PawnIO read
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Queue<int> _history = new();
    private int _baseHeight; // unscaled content height; ClientSize is this * EffScale (grows when the banner shows)

    private int _lastRpm = -1;
    private int[]? _lastCoreTemps;
    private int _tickCount; // gates the per-core temp read in DoTick to every other tick
    private int _tjmax;
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

    public DashboardForm(EcReader? ec, CpuTemps? cpu)
    {
        _ec = ec;
        _cpu = cpu;
        _cpuAvailable = _cpu is not null;
        _coreCount = _cpuAvailable ? Environment.ProcessorCount : 0;

        Text = "ThinkCentre Fan Control";
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
        y += HeaderH;
        y += HeroTopMargin + HeroBlockHeight + HeroBottomMargin;
        y += SectionTopMargin + SectionLabelHeight + SectionBottomMargin; // CORES / degC
        y += cpuAvailable ? GraphHeight : UnavailableGraphHeight;
        y += SectionTopMargin + SectionLabelHeight + SectionBottomMargin; // MODE / hint
        y += KeyHeight;
        if (withBanner)
            y += RestartBannerGap + RestartBannerHeight;
        y += FooterTopMargin + FooterRuleGap + FooterH;
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
            int useImmersiveDarkMode = 0; // flat paper theme now - force the light title bar, not dark
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
        _history.Enqueue(rpm);
        while (_history.Count > HistoryCapacity)
            _history.Dequeue();

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

        if (_cpu is not null)
        {
            try
            {
                _tjmax = _cpu.Tjmax();
            }
            catch
            {
                // leave _tjmax at its previous value (0 initially - suppressed in the draw)
            }
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

        float y = PadTop;
        y = DrawHeader(g, y);
        y += HeroTopMargin;
        y = DrawHero(g, y, _lastRpm, _history.ToArray());
        y += HeroBottomMargin;

        y += SectionTopMargin;
        DrawSectionLabel(g, y, "CORES", "°C");
        y += SectionLabelHeight + SectionBottomMargin;
        y = DrawCoreGraph(g, y);

        y += SectionTopMargin;
        DrawSectionLabel(g, y, "MODE", _restartPending ? "" : "· RESTART APPLIES FULL SPEED");
        y += SectionLabelHeight + SectionBottomMargin;
        y = DrawModeKeys(g, y);

        if (_restartPending)
        {
            y += RestartBannerGap;
            y = DrawRestartBanner(g, y);
        }
        else
        {
            _restartNowRect = RectangleF.Empty;
        }

        y += FooterTopMargin;
        DrawFooter(g, y);
    }

    // ---- drawing, top to bottom ----

    private float DrawHeader(Graphics g, float y)
    {
        DrawTracked(g, "FAN CONTROL", KickerFont, InkBrush, ContentLeft, y, KickerTracking);

        string statusLabel = _currentSelection.HasValue ? LabelFor(_currentSelection.Value) : "UNKNOWN";
        Brush statusBrush = _currentSelection.HasValue ? AccentBrush : MutedBrush;

        float statusW = MeasureTracked(g, statusLabel, StatusFont, StatusTracking);
        float statusX = ContentRight - statusW;
        float dotX = statusX - HeaderDotGap - HeaderDotSize;
        SizeF statusLine = g.MeasureString(statusLabel, StatusFont);
        float dotY = y + (statusLine.Height - HeaderDotSize) / 2f;

        g.FillEllipse(statusBrush, dotX, dotY, HeaderDotSize, HeaderDotSize);
        DrawTracked(g, statusLabel, StatusFont, statusBrush, statusX, y, StatusTracking);

        return y + HeaderH;
    }

    private static float DrawHero(Graphics g, float y, int rpm, int[] history)
    {
        bool available = rpm >= 0;
        string figure = available ? rpm.ToString(CultureInfo.InvariantCulture) : "-";
        Brush figureBrush = available ? InkBrush : MutedBrush;

        float figBottom = y + HeroBlockHeight;
        SizeF figSize = g.MeasureString(figure, HeroFont);
        g.DrawString(figure, HeroFont, figureBrush, ContentLeft, figBottom - figSize.Height);

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

    // A small flat sparkline of the RPM history queue - not the old axis-and-
    // grid chart, just a line and a dot. Gaps (failed reads, marked -1) are
    // skipped rather than breaking the line into runs; for a decoration this
    // small that reads better than a visible hole.
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

        // Keep a near-constant reading (full speed barely moves) as a calm,
        // centred, near-flat line instead of amplifying a few rpm of jitter into
        // a full-height square wave.
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

    private float DrawCoreGraph(Graphics g, float y)
    {
        if (_cpu is null)
        {
            DrawTracked(g, "PER-CORE TEMPS UNAVAILABLE", LabelFont, MutedBrush,
                ContentLeft, y + (UnavailableGraphHeight - SectionLabelHeight) / 2f, SectionTracking);
            return y + UnavailableGraphHeight;
        }

        float colWidth = (ContentWidth - CoreColumnGap * (_coreCount - 1)) / _coreCount;
        for (int i = 0; i < _coreCount; i++)
        {
            var col = new RectangleF(ContentLeft + i * (colWidth + CoreColumnGap), y, colWidth, GraphHeight);
            int tempC = _lastCoreTemps is not null && i < _lastCoreTemps.Length ? _lastCoreTemps[i] : -1;
            DrawCoreColumn(g, col, i, tempC);
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

    private float DrawModeKeys(Graphics g, float y)
    {
        FanSelection[] order = { FanSelection.Quiet, FanSelection.Balanced, FanSelection.Performance, FanSelection.FullSpeed };
        float keyWidth = (ContentWidth - KeyGap * (order.Length - 1)) / order.Length;
        float x = ContentLeft;

        foreach (FanSelection sel in order)
        {
            var rect = new RectangleF(x, y, keyWidth, KeyHeight);
            using var path = RoundedRect(rect, KeyRadius);
            bool active = _currentSelection == sel;
            if (active)
            {
                g.FillPath(AccentBrush, path);
            }
            else
            {
                g.FillPath(KeyInactiveBgBrush, path);
                g.DrawPath(KeyBorderPen, path);
            }

            string label = LabelFor(sel);
            Brush textBrush = active ? WhiteBrush : KeyInactiveTextBrush;
            SizeF size = g.MeasureString(label, LabelFont);
            g.DrawString(label, LabelFont, textBrush,
                x + (keyWidth - size.Width) / 2f,
                y + (KeyHeight - size.Height) / 2f);

            switch (sel)
            {
                case FanSelection.Quiet: _quietKeyRect = rect; break;
                case FanSelection.Balanced: _balancedKeyRect = rect; break;
                case FanSelection.Performance: _performanceKeyRect = rect; break;
                case FanSelection.FullSpeed: _fullSpeedKeyRect = rect; break;
            }

            x += keyWidth + KeyGap;
        }

        return y + KeyHeight;
    }

    private static string LabelFor(FanSelection sel)
        => sel == FanSelection.FullSpeed ? "FULL SPEED" : sel.ToString().ToUpperInvariant();

    private float DrawRestartBanner(Graphics g, float y)
    {
        var rect = new RectangleF(ContentLeft, y, ContentWidth, RestartBannerHeight);
        using var path = RoundedRect(rect, BannerRadius);
        g.FillPath(RestartBannerFillBrush, path);
        g.DrawPath(RestartBannerBorderPen, path);

        // The banner only ever shows while entering Full Speed (leaving is live).
        const string message = "RESTART TO APPLY FULL SPEED";
        SizeF lineSize = g.MeasureString(message, BannerFont);
        DrawTracked(g, message, BannerFont, InkBrush,
            rect.X + BannerPadX, rect.Y + (rect.Height - lineSize.Height) / 2f, BannerTracking);

        const string pillLabel = "RESTART NOW";
        SizeF pillTextSize = g.MeasureString(pillLabel, LabelFont);
        float pillW = pillTextSize.Width + BannerPillPadX * 2f;
        float pillH = RestartBannerHeight - BannerPillMarginY * 2f;
        var pillRect = new RectangleF(rect.Right - pillW - BannerPadX, rect.Y + BannerPillMarginY, pillW, pillH);
        using var pillPath = RoundedRect(pillRect, pillH / 2f);
        g.FillPath(AccentBrush, pillPath);
        g.DrawString(pillLabel, LabelFont, WhiteBrush,
            pillRect.X + (pillRect.Width - pillTextSize.Width) / 2f,
            pillRect.Y + (pillRect.Height - pillTextSize.Height) / 2f);

        _restartNowRect = pillRect;
        return y + RestartBannerHeight;
    }

    private void DrawFooter(Graphics g, float y)
    {
        g.DrawLine(RulePen, ContentLeft, y, ContentRight, y);
        float rowY = y + FooterRuleGap;
        float rowMid = rowY + FooterH / 2f;

        const float swW = 30f, swH = 17f;
        var swRect = new RectangleF(ContentLeft, rowMid - swH / 2f, swW, swH);
        DrawToggleSwitch(g, swRect, _autostartEnabled);

        const string toggleLabel = "START WITH WINDOWS";
        SizeF toggleSize = g.MeasureString(toggleLabel, ToggleFont);
        float toggleX = swRect.Right + ToggleGap;
        DrawTracked(g, toggleLabel, ToggleFont, KeyInactiveTextBrush, toggleX, rowMid - toggleSize.Height / 2f, ToggleTracking);
        float toggleW = MeasureTracked(g, toggleLabel, ToggleFont, ToggleTracking);
        _autostartToggleRect = new RectangleF(ContentLeft, rowY, toggleX + toggleW - ContentLeft, FooterH);

        string stamp = "M70t";
        if (_tjmax > 0)
            stamp += " · Tjmax " + _tjmax.ToString(CultureInfo.InvariantCulture);
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
