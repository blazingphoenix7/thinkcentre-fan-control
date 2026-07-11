using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Tcfc.Core;

namespace Tcfc.Tray;

/// <summary>
/// Tray UI: live RPM tooltip and menu header, board-gated fan-mode presets,
/// autostart toggle. Runs monitoring-only when the EC driver is unavailable
/// or the board is not the verified one.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    // Shown in the tooltip and menu header. NotifyIcon.Text caps at 63 chars.
    private const string EcUnavailableText = "EC unavailable - run as admin / install PawnIO";

    private readonly EcReader? _ec;
    private readonly CpuTemps? _cpu;
    private readonly Icon _fanIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _header;
    private readonly ToolStripMenuItem[] _modeItems;
    private readonly ToolStripMenuItem _autostartItem;
    private DashboardForm? _dashboard;
    private int _ticks;
    private bool _trimmed;

    public TrayApp()
    {
        // Without PawnIO or elevation the tray still starts, just without readings.
        try
        {
            _ec = new EcReader();
        }
        catch (EcUnavailableException)
        {
            _ec = null;
        }

        // Same defensive story as the EC reader: no PawnIO/elevation just means
        // the dashboard's per-core grid draws itself as unavailable.
        try
        {
            _cpu = new CpuTemps();
        }
        catch (EcUnavailableException)
        {
            _cpu = null;
        }

        _header = new ToolStripMenuItem(_ec is null ? EcUnavailableText : "RPM -  |  hottest sensor - C")
        {
            Enabled = false,
        };

        // Same board gate as the CLI; a WMI hiccup counts as unsupported.
        ToolStripMenuItem modeSection;
        if (MachineGuard.IsSupportedBoard(TryReadBoardProduct()))
        {
            _modeItems = new[]
            {
                CreateModeItem(FanMode.Quiet),
                CreateModeItem(FanMode.Balanced),
                CreateModeItem(FanMode.Performance),
            };
            modeSection = new ToolStripMenuItem("Fan mode");
            modeSection.DropDownItems.AddRange(_modeItems);
            modeSection.DropDownOpening += (_, _) => RefreshModeChecks();
        }
        else
        {
            _modeItems = Array.Empty<ToolStripMenuItem>();
            modeSection = new ToolStripMenuItem("Fan control unavailable on this model (monitoring only)")
            {
                Enabled = false,
            };
        }

        _autostartItem = new ToolStripMenuItem("Start with Windows");
        _autostartItem.Click += (_, _) => ToggleAutostart();

        var openItem = new ToolStripMenuItem("Open");
        openItem.Click += (_, _) => ShowDashboard();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(openItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_header);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(modeSection);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(exitItem);
        // Re-query the scheduled task on every open: it can change behind our back.
        _menu.Opening += (_, _) => OnMenuOpening();

        _fanIcon = CreateFanIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _fanIcon,
            ContextMenuStrip = _menu,
            Text = _ec is null ? EcUnavailableText : "- RPM",
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowDashboard();

        // 2s is plenty for a tooltip nobody watches continuously, and it halves
        // the idle wakeups versus 1s.
        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => OnTick();
        if (_ec is not null)
        {
            UpdateTooltip(); // first reading now, not one interval from now
            _timer.Start();
        }
    }

    // The timer only refreshes the tooltip, which is one cheap RPM read (2 EC
    // bytes). The 15-read temperature block feeds the menu header, so it is read
    // only when the menu actually opens - see OnMenuOpening - not every tick.
    private void OnTick()
    {
        UpdateTooltip();

        // Hand the startup working set back to the OS once things have settled
        // (JIT, module init). One-shot, a few ticks in.
        if (!_trimmed && ++_ticks >= 4)
        {
            _trimmed = true;
            TrimWorkingSet();
        }
    }

    private void UpdateTooltip()
    {
        if (_ec is null)
            return;

        try
        {
            int rpm = _ec.Rpm(); // -1 = no reading (EC lock or handshake timeout)
            _notifyIcon.Text = rpm < 0 ? "- RPM" : $"{rpm} RPM";
        }
        catch
        {
            // keep the tray alive; next tick retries
        }
    }

    // Refreshes the menu header's RPM + hottest-sensor line and the autostart
    // check. Only runs when the menu is opening, since it reads the whole
    // temperature block on top of the RPM.
    private void OnMenuOpening()
    {
        _autostartItem.Checked = Autostart.IsEnabled();
        if (_ec is null)
            return;

        try
        {
            int rpm = _ec.Rpm();
            string rpmText = rpm < 0 ? "-" : rpm.ToString();
            // "hottest sensor", not "CPU": the sensor-to-component mapping is
            // unverified (docs/research/temp-labeling.md)
            int? hottest = TempSummary.Representative(_ec.Temps());
            _header.Text = $"RPM {rpmText}  |  hottest sensor {hottest?.ToString() ?? "-"} C";
        }
        catch
        {
            // leave the last header; the next open retries
        }
    }

    private ToolStripMenuItem CreateModeItem(FanMode mode)
    {
        // also disabled while the EC is unavailable (degraded state)
        var item = new ToolStripMenuItem(mode.ToString())
        {
            Tag = mode,
            Enabled = _ec is not null,
        };
        item.Click += (_, _) => SetMode(mode);
        return item;
    }

    private void SetMode(FanMode mode)
    {
        try
        {
            FanModes.Set(mode);
            RefreshModeChecks();
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(3000, "Fan mode not changed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void RefreshModeChecks()
    {
        FanMode? current;
        try
        {
            current = FanModes.Get();
        }
        catch
        {
            current = null; // WMI read failed - no check beats a stale one
        }

        foreach (ToolStripMenuItem item in _modeItems)
            item.Checked = current is not null && (FanMode)item.Tag! == current;
    }

    private static string? TryReadBoardProduct()
    {
        try
        {
            return Board.Product();
        }
        catch
        {
            return null; // unreadable board counts as unsupported
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            if (Autostart.IsEnabled())
            {
                Autostart.Disable();
                _autostartItem.Checked = false;
            }
            else
            {
                Autostart.Enable();
                _autostartItem.Checked = true;
            }
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(3000, "Autostart not changed", ex.Message, ToolTipIcon.Error);
        }
    }

    // Lazily creates the one dashboard instance, then (re)shows it. Reused
    // across opens so the RPM history chart keeps rolling in the background.
    private void ShowDashboard()
    {
        if (_dashboard is null)
        {
            _dashboard = new DashboardForm(_ec, _cpu);
            // When the window is closed back to the tray, hand its paint-time
            // working set back to the OS.
            _dashboard.VisibleChanged += (_, _) =>
            {
                if (_dashboard is { Visible: false })
                    TrimWorkingSet();
            };
        }
        if (!_dashboard.Visible)
            _dashboard.Show();
        _dashboard.Activate();
        _dashboard.BringToFront();
    }

    private void ExitApp()
    {
        _timer.Stop();
        _notifyIcon.Visible = false; // drop the icon before the loop ends
        Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _notifyIcon.Dispose();
            _menu.Dispose();
            _fanIcon.Dispose();
            _dashboard?.Dispose(); // real close, bypassing its close-to-tray FormClosing guard
            _cpu?.Dispose();
            _ec?.Dispose();
        }
        base.Dispose(disposing);
    }

    // Drawn at runtime, no image assets. Three blades so it can't be mistaken
    // for the four-arrow move cursor; dark disc + light rim reads on both light
    // and dark taskbars and survives the 16x16 downscale.
    private static Icon CreateFanIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var dark = Color.FromArgb(38, 46, 58);
            var light = Color.FromArgb(244, 246, 250);
            using var darkFill = new SolidBrush(dark);
            using var lightFill = new SolidBrush(light);
            using var rim = new Pen(light, 2f);

            // disc + rim
            g.FillEllipse(darkFill, 1f, 1f, 30f, 30f);
            g.DrawEllipse(rim, 2f, 2f, 28f, 28f);

            // three blades, 120 degrees apart
            g.TranslateTransform(16f, 16f);
            for (int i = 0; i < 3; i++)
            {
                g.FillEllipse(lightFill, -4f, -13.5f, 8f, 11f);
                g.RotateTransform(120f);
            }
            g.ResetTransform();

            // hub + axle dot
            g.FillEllipse(lightFill, 12f, 12f, 8f, 8f);
            g.FillEllipse(darkFill, 14.5f, 14.5f, 3f, 3f);
        }

        // GetHicon hands out an unmanaged handle; clone it and free the original.
        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var fromHandle = Icon.FromHandle(hIcon);
            return (Icon)fromHandle.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // Hand pages we no longer need back to the OS. Safe and cheap: trimmed pages
    // fault back in on demand, and this tray is idle almost all of the time.
    private static void TrimWorkingSet()
    {
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // best effort - trimming is an optimization, never a requirement
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
}
