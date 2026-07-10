using System.Diagnostics;

namespace Tcfc.Tray;

/// <summary>
/// Autostart-on-logon via a scheduled task, not an HKCU Run entry: the exe
/// manifest requires elevation and a Run entry can't silently elevate at logon.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "ThinkCentreFanControl";

    /// <summary>True when the scheduled task exists (schtasks query exits 0).</summary>
    public static bool IsEnabled()
    {
        try
        {
            return RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;
        }
        catch
        {
            return false; // no task (or no schtasks) counts as "not enabled"
        }
    }

    /// <summary>Creates the logon task pointing at the running exe, elevated.</summary>
    public static void Enable()
    {
        string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
        int exitCode = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        if (exitCode != 0)
            throw new InvalidOperationException($"schtasks /Create failed (exit code {exitCode}).");
    }

    /// <summary>Deletes the logon task.</summary>
    public static void Disable()
    {
        int exitCode = RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        if (exitCode != 0)
            throw new InvalidOperationException($"schtasks /Delete failed (exit code {exitCode}).");
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
}
