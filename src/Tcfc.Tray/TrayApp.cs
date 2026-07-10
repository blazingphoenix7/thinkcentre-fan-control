using System.Diagnostics;
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
    // A scheduled task, not an HKCU Run entry: the exe manifest requires
    // elevation and a Run entry can't silently elevate at logon.
    private const string AutostartTaskName = "ThinkCentreFanControl";

    // Shown in the tooltip and menu header. NotifyIcon.Text caps at 63 chars.
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
        // Without PawnIO or elevation the tray still starts, just without readings.
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

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApp();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_header);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(modeSection);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(exitItem);
        // Re-query the scheduled task on every open: it can change behind our back.
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

    private void UpdateReadings()
    {
        if (_ec is null)
            return;

        try
        {
            // -1 = no reading this tick (EC lock or handshake timeout)
            int rpm = _ec.Rpm();
            string rpmText = rpm < 0 ? "-" : rpm.ToString();

            // "hottest sensor", not "CPU": the sensor-to-component mapping is
            // unverified (docs/research/temp-labeling.md)
            int? hottest = TempSummary.Representative(_ec.Temps());

            _notifyIcon.Text = $"{rpmText} RPM";
            _header.Text = $"RPM {rpmText}  |  hottest sensor {hottest?.ToString() ?? "-"} C";
        }
        catch
        {
            // keep the tray alive; next tick retries
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

    // Runs schtasks.exe hidden (no console window flash); returns its exit code.
    private static int RunSchtasks(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("schtasks.exe could not be started.");
        process.WaitForExit();
        return process.ExitCode;
    }

    private static bool IsAutostartEnabled()
    {
        try
        {
            return RunSchtasks($"/Query /TN \"{AutostartTaskName}\"") == 0;
        }
        catch
        {
            return false; // no task (or no schtasks) counts as "not enabled"
        }
    }

    private void ToggleAutostart()
    {
        try
        {
            if (IsAutostartEnabled())
            {
                int exitCode = RunSchtasks($"/Delete /TN \"{AutostartTaskName}\" /F");
                if (exitCode != 0)
                    throw new InvalidOperationException($"schtasks /Delete failed (exit code {exitCode}).");
                _autostartItem.Checked = false;
            }
            else
            {
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                int exitCode = RunSchtasks(
                    $"/Create /TN \"{AutostartTaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
                if (exitCode != 0)
                    throw new InvalidOperationException($"schtasks /Create failed (exit code {exitCode}).");
                _autostartItem.Checked = true;
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
}
