using System.Management;
using System.Runtime.Versioning;

namespace Tcfc.Core;

public static class Board
{
    /// <summary>Win32_BaseBoard.Product (e.g. "3376"), or null when WMI does not report one.</summary>
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
