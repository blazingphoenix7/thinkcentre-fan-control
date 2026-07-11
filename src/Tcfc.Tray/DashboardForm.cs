using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using Tcfc.Core;

namespace Tcfc.Tray;

/// <summary>
/// The main dashboard window: a live, interactive rendering of the README
/// monitor card (<see cref="Tcfc.Capture.CardRenderer"/>) plus per-core CPU
/// temps, a fan-mode selector, and a restart banner for the BIOS-gated Full
/// Speed setting. Custom-painted, no child controls; clickable regions are
/// plain hit-tested rectangles recorded during <see cref="OnPaint"/>.
///
/// Ownership: does not own or dispose <c>ec</c> or <c>cpu</c>. The tray creates
/// both (alongside its own EcReader) and disposes them in its own Dispose;
/// this form only ever reads from whichever references it is given, and either
/// may be null when that hardware path is unavailable.
/// </summary>
internal sealed class DashboardForm : Form
{
    // ---- palette / fonts, copied from CardRenderer so the window matches the
    // approved card look exactly. ----
    private static readonly Color PageBg = Color.FromArgb(19, 21, 26);
    private static readonly Color PanelBg = Color.FromArgb(27, 30, 36);
    private static readonly Color PanelEdge = Color.FromArgb(44, 49, 60);
    private static readonly Color TextPrimary = Color.FromArgb(232, 236, 244);
    private static readonly Color TextMuted = Color.FromArgb(139, 147, 164);
    private static readonly Color TextFaint = Color.FromArgb(94, 101, 118);
    private static readonly Color Accent = Color.FromArgb(63, 214, 198);
    private static readonly Color AccentText = Color.FromArgb(10, 34, 30);
    private static readonly Color GridLine = Color.FromArgb(38, 43, 53);
    private static readonly Color PillEdge = Color.FromArgb(56, 63, 78);

    // Dashboard-only additions: the warm "this is the powerful/gated one" tone
    // (Full Speed pill, restart banner) and the two hot per-core temp bands.
    private static readonly Color WarnAccent = Color.FromArgb(230, 170, 70);
    private static readonly Color HotAccent = Color.FromArgb(232, 90, 90);

    private static readonly Font TitleFont = new("Segoe UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font NumberFont = new("Segoe UI", 76f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font UnitFont = new("Segoe UI", 18f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font LineFont = new("Segoe UI", 16f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font PillFont = new("Segoe UI", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Font PillFontBold = new("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
    private static readonly Font SmallFont = new("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);

    // Cached for the same reason as the fonts above: OnPaint runs repeatedly
    // while the window is visible, so these are built once instead of per frame.
    private static readonly SolidBrush PanelBgBrush = new(PanelBg);
    private static readonly SolidBrush TextPrimaryBrush = new(TextPrimary);
    private static readonly SolidBrush TextMutedBrush = new(TextMuted);
    private static readonly SolidBrush TextFaintBrush = new(TextFaint);
    private static readonly SolidBrush AccentBrush = new(Accent);
    private static readonly SolidBrush AccentTextBrush = new(AccentText);
    private static readonly SolidBrush WarnAccentBrush = new(WarnAccent);
    private static readonly SolidBrush HotAccentBrush = new(HotAccent);
    private static readonly SolidBrush ChartUnderFillBrush = new(Color.FromArgb(26, Accent));
    private static readonly SolidBrush ChartHaloBrush = new(Color.FromArgb(70, Accent));
    private static readonly SolidBrush BannerFillBrush = new(Color.FromArgb(30, WarnAccent));

    private static readonly Pen PanelEdgePen = new(PanelEdge, 1f);
    private static readonly Pen GridLinePen = new(GridLine, 1f);
    private static readonly Pen PillEdgePen = new(PillEdge, 1f);
    private static readonly Pen ChartLinePen = new(Accent, 2.2f)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };
    private static readonly Pen FullSpeedEdgePen = new(WarnAccent, 1.4f);
    private static readonly Pen BannerEdgePen = new(Color.FromArgb(90, WarnAccent), 1f);

    private static readonly RectangleF ChartRect = new(320f, 88f, 396f, 200f);

    private const int FormWidth = 760;
    // Logical zoom at 96 DPI. The real paint-time scale is this times the monitor's
    // DPI ratio (see EffScale), so the window is a consistent physical size on any
    // display. Raise this to grow the whole dashboard everywhere.
    private const float UiScale = 1.3f;
    private const float ContentLeft = 44f;
    private const float ContentRight = FormWidth - ContentLeft;
    private const int HistoryCapacity = 60; // matches CardRenderer.HistorySlots

    private const int CoresPerRow = 5;
    private const float CoreCellWidth = 118f;
    private const float CoreCellHeight = 32f;
    private const float CoreCellGapX = 10f;
    private const float CoreCellGapY = 8f;

    private const float CpuSectionY = 328f;   // first Y below the RPM figure / chart row
    private const float LabelBlockHeight = 28f;
    private const float SectionGap = 20f;
    private const float FanPillHeight = 36f;
    private const float FullSpeedExtra = 6f;  // Full Speed pill is a touch taller - the emphasis option
    private const float RestartBannerHeight = 48f;
    private const float AutostartHeight = 32f;
    private const float BottomMargin = 30f;

    private const int MinFormHeight = 420;
    private const int MaxFormHeight = 900;

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    private readonly EcReader? _ec;
    private readonly CpuTemps? _cpu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Queue<int> _history = new();
    private readonly int _coreRows; // sized from Environment.ProcessorCount, not a live PawnIO read
    private int _baseHeight; // unscaled content height; ClientSize is this * UiScale (grows when the banner shows)

    private int _lastRpm = -1;
    private int[]? _lastCoreTemps;
    private int _tickCount; // gates the per-core temp read in DoTick to every other tick
    private int _tjmax;
    private FanSelection? _currentSelection; // null until the first successful WMI read
    private bool _biosFullSpeed;
    private bool _autostartEnabled;
    private bool _restartPending; // drives the banner and the window's extra height

    // Hit-test rectangles, recorded fresh on every OnPaint.
    private RectangleF _quietPillRect;
    private RectangleF _balancedPillRect;
    private RectangleF _performancePillRect;
    private RectangleF _fullSpeedPillRect;
    private RectangleF _restartNowRect;
    private RectangleF _autostartRect;

    public DashboardForm(EcReader? ec, CpuTemps? cpu)
    {
        _ec = ec;
        _cpu = cpu;

        int coreCount = _cpu is not null ? Environment.ProcessorCount : 0;
        _coreRows = Math.Max(1, (coreCount + CoresPerRow - 1) / CoresPerRow);

        Text = "ThinkCentre Fan Control";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = PageBg;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None; // we do all DPI scaling ourselves via EffScale
        _baseHeight = ComputeHeight(_coreRows, withBanner: false);
        ApplyClientSize();

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += DoTick;

        RefreshCachedState();
    }

    // Mirrors the OnPaint layout cursor. The restart-banner row is only counted
    // when the banner is actually showing, so there is no dead space under the
    // window in the common (no-restart-pending) state; DoTick regrows it if a
    // restart becomes pending.
    private static int ComputeHeight(int coreRows, bool withBanner)
    {
        float y = CpuSectionY;
        y += LabelBlockHeight;
        y += coreRows * (CoreCellHeight + CoreCellGapY);
        y += SectionGap;
        y += LabelBlockHeight;
        y += FanPillHeight + FullSpeedExtra;
        y += SectionGap;
        if (withBanner)
            y += RestartBannerHeight + SectionGap;
        y += AutostartHeight;
        y += BottomMargin;
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
            int useDark = 1;
            DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));
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
            _baseHeight = ComputeHeight(_coreRows, pending);
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

        g.Clear(PageBg);
        g.ScaleTransform(EffScale, EffScale); // draw in logical units; scaled by zoom * monitor DPI
        DrawPanel(g);
        DrawTitle(g);
        DrawRpmFigure(g, _lastRpm);
        DrawChart(g, _history.ToArray());

        float y = DrawCpuSection(g, CpuSectionY, _lastCoreTemps, _tjmax);
        y = DrawFanSection(g, y);

        if (_restartPending)
            y = DrawRestartBanner(g, y);
        else
            _restartNowRect = RectangleF.Empty;

        DrawAutostartSection(g, y);
    }

    private void DrawPanel(Graphics g)
    {
        var rect = new RectangleF(18f, 18f, FormWidth - 36f, _baseHeight - 36f);
        using var path = RoundedRect(rect, 16f);
        g.FillPath(PanelBgBrush, path);
        g.DrawPath(PanelEdgePen, path);
    }

    // ---- drawing: card-identical pieces (panel/title/RPM figure/chart) ----

    private void DrawTitle(Graphics g)
    {
        g.DrawString("ThinkCentre Fan Control · desktop", TitleFont, TextMutedBrush, 42f, 38f);
    }

    private static void DrawRpmFigure(Graphics g, int rpm)
    {
        bool available = rpm >= 0;
        string figure = available ? rpm.ToString(CultureInfo.InvariantCulture) : "-";

        SolidBrush figureBrush = available ? AccentBrush : TextMutedBrush;

        const float x = 36f;
        const float y = 64f;
        SizeF figureSize = g.MeasureString(figure, NumberFont);
        g.DrawString(figure, NumberFont, figureBrush, x, y);

        SizeF unitSize = g.MeasureString("RPM", UnitFont);
        g.DrawString("RPM", UnitFont, TextMutedBrush,
            x + figureSize.Width - 8f,
            y + figureSize.Height - unitSize.Height - 16f);
    }

    private static void DrawChart(Graphics g, IReadOnlyList<int> history)
    {
        RectangleF chart = ChartRect;

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

        int slots = Math.Max(HistoryCapacity, history.Count);
        float slotW = chart.Width / (slots - 1);
        g.DrawRectangle(GridLinePen, chart.X, chart.Y, chart.Width, chart.Height);
        for (int t = 1; t < 4; t++)
        {
            float gy = chart.Y + chart.Height * t / 4f;
            g.DrawLine(GridLinePen, chart.X, gy, chart.Right, gy);
        }
        for (int s = 10; ; s += 10)
        {
            float gx = chart.Right - s * slotW;
            if (gx <= chart.X + 1f)
                break;
            g.DrawLine(GridLinePen, gx, chart.Y, gx, chart.Bottom);
        }

        if (hasData)
        {
            g.DrawString(scaleHi.ToString(CultureInfo.InvariantCulture), SmallFont, TextFaintBrush, chart.X + 5f, chart.Y + 4f);
            g.DrawString(scaleLo.ToString(CultureInfo.InvariantCulture), SmallFont, TextFaintBrush, chart.X + 5f, chart.Bottom - 18f);
        }

        if (hasData)
        {
            float XFor(int index) => chart.Right - (history.Count - 1 - index) * slotW;
            float YFor(int value) =>
                chart.Bottom - (value - scaleLo) / (float)(scaleHi - scaleLo) * chart.Height;

            var run = new List<PointF>();
            PointF? newest = null;

            void Flush()
            {
                if (run.Count == 0)
                    return;
                if (run.Count == 1)
                {
                    g.FillEllipse(AccentBrush, run[0].X - 2f, run[0].Y - 2f, 4f, 4f);
                }
                else
                {
                    var area = new PointF[run.Count + 2];
                    run.CopyTo(area);
                    area[run.Count] = new PointF(run[run.Count - 1].X, chart.Bottom);
                    area[run.Count + 1] = new PointF(run[0].X, chart.Bottom);
                    g.FillPolygon(ChartUnderFillBrush, area);
                    g.DrawLines(ChartLinePen, run.ToArray());
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

            if (newest.HasValue && history[history.Count - 1] >= 0)
            {
                g.FillEllipse(ChartHaloBrush, newest.Value.X - 7f, newest.Value.Y - 7f, 14f, 14f);
                g.FillEllipse(AccentBrush, newest.Value.X - 3.5f, newest.Value.Y - 3.5f, 7f, 7f);
            }
        }

        g.DrawString("fan rpm · recent samples", SmallFont, TextFaintBrush, chart.X, chart.Bottom + 8f);
    }

    // ---- drawing: new dashboard rows ----

    private float DrawCpuSection(Graphics g, float y, int[]? coreTemps, int tjmax)
    {
        const string label = "cpu cores";
        g.DrawString(label, LineFont, TextMutedBrush, ContentLeft, y);

        if (_cpu is not null && tjmax > 0)
        {
            float labelW = g.MeasureString(label, LineFont).Width;
            g.DrawString($"· Tjmax {tjmax.ToString(CultureInfo.InvariantCulture)}°", SmallFont, TextFaintBrush,
                ContentLeft + labelW + 10f, y + 5f);
        }
        y += LabelBlockHeight;

        if (_cpu is null)
        {
            g.DrawString("per-core temps unavailable", SmallFont, TextFaintBrush, ContentLeft, y + 6f);
            return y + CoreCellHeight + CoreCellGapY;
        }

        int count = Environment.ProcessorCount;
        for (int i = 0; i < count; i++)
        {
            int row = i / CoresPerRow;
            int col = i % CoresPerRow;
            var rect = new RectangleF(
                ContentLeft + col * (CoreCellWidth + CoreCellGapX),
                y + row * (CoreCellHeight + CoreCellGapY),
                CoreCellWidth, CoreCellHeight);
            int tempC = coreTemps is not null && i < coreTemps.Length ? coreTemps[i] : -1;
            DrawCoreCell(g, rect, i, tempC);
        }

        return y + _coreRows * (CoreCellHeight + CoreCellGapY);
    }

    private static void DrawCoreCell(Graphics g, RectangleF rect, int coreIndex, int tempC)
    {
        using var path = RoundedRect(rect, 8f);
        g.DrawPath(PillEdgePen, path);

        string label = "C" + coreIndex.ToString(CultureInfo.InvariantCulture);
        string value = tempC < 0 ? "-" : tempC.ToString(CultureInfo.InvariantCulture) + "°";
        SolidBrush valueBrush = tempC < 0 ? TextFaintBrush : tempC <= 84 ? AccentBrush : tempC <= 96 ? WarnAccentBrush : HotAccentBrush;

        SizeF labelSize = g.MeasureString(label, SmallFont);
        SizeF valueSize = g.MeasureString(value, SmallFont);
        float totalW = labelSize.Width + 6f + valueSize.Width;
        float startX = rect.X + (rect.Width - totalW) / 2f;
        float textY = rect.Y + (rect.Height - labelSize.Height) / 2f;

        g.DrawString(label, SmallFont, TextMutedBrush, startX, textY);
        g.DrawString(value, SmallFont, valueBrush, startX + labelSize.Width + 6f, textY);
    }

    private float DrawFanSection(Graphics g, float y)
    {
        g.DrawString("fan mode", LineFont, TextMutedBrush, ContentLeft, y);
        y += LabelBlockHeight;

        float x = ContentLeft;

        foreach (FanSelection sel in new[]
                 {
                     FanSelection.Quiet, FanSelection.Balanced, FanSelection.Performance, FanSelection.FullSpeed,
                 })
        {
            bool isFullSpeed = sel == FanSelection.FullSpeed;
            string label = isFullSpeed ? "Full Speed" : sel.ToString();
            Font font = isFullSpeed ? PillFontBold : PillFont;
            float rowH = isFullSpeed ? FanPillHeight + FullSpeedExtra : FanPillHeight;
            float rowY = isFullSpeed ? y - FullSpeedExtra / 2f : y;

            SizeF size = g.MeasureString(label, font);
            float w = size.Width + 28f;

            var rect = new RectangleF(x, rowY, w, rowH);
            using var path = RoundedRect(rect, rowH / 2f);

            bool active = _currentSelection == sel;
            if (active)
                g.FillPath(isFullSpeed ? WarnAccentBrush : AccentBrush, path);
            else
                g.DrawPath(isFullSpeed ? FullSpeedEdgePen : PillEdgePen, path);

            Brush textBrush = active ? AccentTextBrush : TextMutedBrush;
            g.DrawString(label, font, textBrush,
                rect.X + (rect.Width - size.Width) / 2f,
                rect.Y + (rect.Height - size.Height) / 2f + 0.5f);

            switch (sel)
            {
                case FanSelection.Quiet: _quietPillRect = rect; break;
                case FanSelection.Balanced: _balancedPillRect = rect; break;
                case FanSelection.Performance: _performancePillRect = rect; break;
                case FanSelection.FullSpeed: _fullSpeedPillRect = rect; break;
            }

            x += w + 10f;
        }

        return y + FanPillHeight + FullSpeedExtra + SectionGap;
    }

    private float DrawRestartBanner(Graphics g, float y)
    {
        var rect = new RectangleF(ContentLeft, y, ContentRight - ContentLeft, RestartBannerHeight);
        using var path = RoundedRect(rect, 10f);
        g.FillPath(BannerFillBrush, path);
        g.DrawPath(BannerEdgePen, path);

        // The banner only ever shows while entering Full Speed (leaving is live).
        string message = "Full speed applies after a restart";
        SizeF textSize = g.MeasureString(message, LineFont);
        g.DrawString(message, LineFont, TextPrimaryBrush, rect.X + 16f, rect.Y + (rect.Height - textSize.Height) / 2f);

        const string pillLabel = "Restart now";
        SizeF pillSize = g.MeasureString(pillLabel, PillFont);
        float pillW = pillSize.Width + 28f;
        const float pillH = 30f;
        var pillRect = new RectangleF(rect.Right - pillW - 12f, rect.Y + (rect.Height - pillH) / 2f, pillW, pillH);
        using var pillPath = RoundedRect(pillRect, pillH / 2f);
        g.FillPath(WarnAccentBrush, pillPath);
        g.DrawString(pillLabel, PillFont, AccentTextBrush,
            pillRect.X + (pillRect.Width - pillSize.Width) / 2f,
            pillRect.Y + (pillRect.Height - pillSize.Height) / 2f + 0.5f);

        _restartNowRect = pillRect;
        return y + RestartBannerHeight + SectionGap;
    }

    private void DrawAutostartSection(Graphics g, float y)
    {
        const string label = "Start with Windows";
        SizeF size = g.MeasureString(label, PillFont);
        float w = size.Width + 46f;
        const float h = AutostartHeight;
        var rect = new RectangleF(ContentLeft, y, w, h);
        using var path = RoundedRect(rect, h / 2f);

        if (_autostartEnabled)
            g.FillPath(AccentBrush, path);
        else
            g.DrawPath(PillEdgePen, path);

        string mark = _autostartEnabled ? "✓" : "○";
        SolidBrush markBrush = _autostartEnabled ? AccentTextBrush : TextMutedBrush;
        g.DrawString(mark, PillFont, markBrush, rect.X + 14f, rect.Y + (rect.Height - size.Height) / 2f);

        SolidBrush textBrush = _autostartEnabled ? AccentTextBrush : TextMutedBrush;
        g.DrawString(label, PillFont, textBrush, rect.X + 34f, rect.Y + (rect.Height - size.Height) / 2f);

        _autostartRect = rect;
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

        if (_quietPillRect.Contains(pt))
            ApplySelection(FanSelection.Quiet);
        else if (_balancedPillRect.Contains(pt))
            ApplySelection(FanSelection.Balanced);
        else if (_performancePillRect.Contains(pt))
            ApplySelection(FanSelection.Performance);
        else if (_fullSpeedPillRect.Contains(pt))
            ConfirmAndApplyFullSpeed();
        else if (!_restartNowRect.IsEmpty && _restartNowRect.Contains(pt))
            ConfirmAndRestart();
        else if (_autostartRect.Contains(pt))
            ToggleAutostart();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        PointF pt = new(e.X / EffScale, e.Y / EffScale);
        bool hovering =
            _quietPillRect.Contains(pt) ||
            _balancedPillRect.Contains(pt) ||
            _performancePillRect.Contains(pt) ||
            _fullSpeedPillRect.Contains(pt) ||
            (!_restartNowRect.IsEmpty && _restartNowRect.Contains(pt)) ||
            _autostartRect.Contains(pt);
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
