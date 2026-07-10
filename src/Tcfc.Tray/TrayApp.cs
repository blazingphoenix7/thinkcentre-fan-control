using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Tcfc.Core;

namespace Tcfc.Tray;

/// <summary>
/// The tray face of the tool: live fan RPM in the icon tooltip, a live
/// "RPM | hottest sensor" header in the context menu, board-gated fan-mode
/// presets, an autostart toggle, and exit.
/// Fails toward loud-and-safe: when the EC driver is unavailable the app
/// still runs (clearly labelled, mode changes disabled); on anything but the
/// verified board the mode menu is replaced by an explanation and the app
/// stays monitoring-only.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ThinkCentreFanControl";

    // Shown in both the tooltip and the menu header while degraded. Keep it
    // short: NotifyIcon.Text is hard-capped (63 chars classic).
    private const string EcUnavailableText = "EC unavailable - run as admin / install PawnIO";

    private readonly EcReader? _ec;
    private readonly Icon _fanIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _header;
    private readonly ToolStripMenuItem[] _modeItems;
    private readonly ToolStripMenuItem _autostartItem;

    public TrayApp()
    {
        // EC access is best effort: without PawnIO or elevation the tray
        // still starts, it just says why there are no readings.
        try
        {
            _ec = new EcReader();
        }
        catch (EcUnavailableException)
        {
            _ec = null;
        }

        _header = new ToolStripMenuItem(_ec is null ? EcUnavailableText : "RPM -  |  hottest sensor - C")
        {
            Enabled = false,
        };

        // Fan-mode writes happen only on the verified board; anywhere else
        // the menu says so and the app is monitoring-only (same gate as the
        // CLI). A WMI hiccup reads as unsupported, i.e. fails toward safe.
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

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_header);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(modeSection);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(exitItem);
        // Re-read the Run value on every open: it can change behind our back.
        _menu.Opening += (_, _) => _autostartItem.Checked = IsAutostartEnabled();

        _fanIcon = CreateFanIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _fanIcon,
            ContextMenuStrip = _menu,
            Text = _ec is null ? EcUnavailableText : "- RPM",
            Visible = true,
        };

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => UpdateReadings();
        if (_ec is not null)
        {
            UpdateReadings(); // first reading now, not one second from now
            _timer.Start();
        }
    }

    /// <summary>
    /// One monitoring beat (UI thread; EC reads are fast and synchronous):
    /// refreshes the tooltip RPM and the menu header line. A transient bad
    /// read keeps the previous values and is retried on the next tick.
    /// </summary>
    private void UpdateReadings()
    {
        if (_ec is null)
            return;

        try
        {
            int rpm = _ec.Rpm();

            // "Hottest sensor", deliberately not "CPU"/"temp": the EC block's
            // sensor-to-component mapping is unverified, and TempSummary
            // filters bytes that cannot be plain temperatures (see
            // docs/research/temp-labeling.md).
            int? hottest = TempSummary.Representative(_ec.Temps());

            _notifyIcon.Text = $"{rpm} RPM";
            _header.Text = $"RPM {rpm}  |  hottest sensor {hottest?.ToString() ?? "-"} C";
        }
        catch
        {
            // Keep the tray alive through a read hiccup; next tick retries.
        }
    }

    private ToolStripMenuItem CreateModeItem(FanMode mode)
    {
        // Mode items exist only on the verified board, and are additionally
        // disabled while the EC is unavailable (degraded monitoring state).
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

    /// <summary>Puts the check mark on the mode the firmware reports now.</summary>
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

    private static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key.GetValue(RunValueName) is null)
            {
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(RunValueName, $"\"{exePath}\"");
                _autostartItem.Checked = true;
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                _autostartItem.Checked = false;
            }
        }
        catch (Exception ex)
        {
            _notifyIcon.ShowBalloonTip(3000, "Autostart not changed", ex.Message, ToolTipIcon.Error);
        }
    }

    private void ExitApp()
    {
        _timer.Stop();
        _notifyIcon.Visible = false; // drop the icon before the loop ends
        Dispose();                   // EcReader, NotifyIcon, menu, icon
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
            _ec?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Draws the tray icon at runtime (no image assets): a solid dark disc
    /// with a light rim carrying a bold three-blade propeller. Three blades at
    /// 120° cannot be mistaken for the four-arrow "move" cursor, the dark disc
    /// keeps it visible on light taskbars and the light rim/blades on dark
    /// ones, and the shapes are chunky enough to survive the 16x16 downscale.
    /// </summary>
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

            // Badge: dark disc with a light rim just inside its edge.
            g.FillEllipse(darkFill, 1f, 1f, 30f, 30f);
            g.DrawEllipse(rim, 2f, 2f, 28f, 28f);

            // Three light blades at 120° radiating from the hub.
            g.TranslateTransform(16f, 16f);
            for (int i = 0; i < 3; i++)
            {
                g.FillEllipse(lightFill, -4f, -13.5f, 8f, 11f);
                g.RotateTransform(120f);
            }
            g.ResetTransform();

            // Hub with a dark axle dot.
            g.FillEllipse(lightFill, 12f, 12f, 8f, 8f);
            g.FillEllipse(darkFill, 14.5f, 14.5f, 3f, 3f);
        }

        // GetHicon hands out an unmanaged handle; clone into a managed icon
        // and release the handle so nothing leaks.
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
}
