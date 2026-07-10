using System.Management;
using System.Runtime.Versioning;

namespace Tcfc.Core;

/// <summary>
/// Firmware fan mode via the LENOVO_GAMEZONE_DATA WMI class in root\wmi - the
/// same Get/SetSmartFanMode calls the vendor's own utility makes.
/// </summary>
public static class FanModes
{
    /// <summary>Mode m is supported when bit (int)m of the mask is set (mask 14 = 0b1110 = all three).</summary>
    public static FanMode[] SupportedFromMask(int mask)
    {
        var all = new[] { FanMode.Quiet, FanMode.Balanced, FanMode.Performance };
        return all.Where(m => ((mask >> (int)m) & 1) == 1).ToArray();
    }

    [SupportedOSPlatform("windows")]
    public static FanMode Get()
    {
        using var gameZone = GetGameZoneInstance();
        using var result = gameZone.InvokeMethod("GetSmartFanMode", (ManagementBaseObject?)null, null);
        return (FanMode)Convert.ToUInt32(result["Data"]);
    }

    /// <summary>Sets the fan mode. Refuses off the verified board even if a caller forgot to gate.</summary>
    [SupportedOSPlatform("windows")]
    public static void Set(FanMode mode)
    {
        if (!MachineGuard.IsSupportedBoard(Board.Product()))
            throw new InvalidOperationException("Fan mode control is only supported on the verified model.");

        using var gameZone = GetGameZoneInstance();
        using var inParams = gameZone.GetMethodParameters("SetSmartFanMode");
        inParams["Data"] = (uint)mode;
        using var result = gameZone.InvokeMethod("SetSmartFanMode", inParams, null);
    }

    [SupportedOSPlatform("windows")]
    public static FanMode[] Supported()
    {
        using var gameZone = GetGameZoneInstance();
        using var result = gameZone.InvokeMethod("GetSupportThermalMode", (ManagementBaseObject?)null, null);
        return SupportedFromMask((int)Convert.ToUInt32(result["Data"]));
    }

    // Caller disposes the returned instance.
    [SupportedOSPlatform("windows")]
    private static ManagementObject GetGameZoneInstance()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\wmi", "SELECT * FROM LENOVO_GAMEZONE_DATA");
            using var results = searcher.Get();
            foreach (ManagementObject instance in results)
                return instance;
        }
        catch (ManagementException ex)
        {
            throw new InvalidOperationException(
                "LENOVO_GAMEZONE_DATA is not available in root\\wmi on this machine; " +
                "the firmware fan-mode WMI interface is missing.", ex);
        }

        throw new InvalidOperationException(
            "No LENOVO_GAMEZONE_DATA instance found in root\\wmi; " +
            "the firmware fan-mode WMI interface is missing.");
    }
}
