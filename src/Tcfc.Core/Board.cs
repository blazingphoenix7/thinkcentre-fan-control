using System.Management;
using System.Runtime.Versioning;

namespace Tcfc.Core;

/// <summary>
/// Board identity, used to gate fan-mode writes to the verified model
/// (see <see cref="MachineGuard.IsSupportedBoard"/>). Shared by the CLI and
/// the tray app.
/// </summary>
public static class Board
{
    /// <summary>
    /// The motherboard product string from Win32_BaseBoard (e.g. "3376"),
    /// or null when WMI does not report one.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? Product()
    {
        using var searcher = new ManagementObjectSearcher("SELECT Product FROM Win32_BaseBoard");
        using var results = searcher.Get();
        foreach (ManagementBaseObject board in results)
        {
            using (board)
            {
                if (board["Product"] is string product)
                    return product;
            }
        }
        return null;
    }
}
