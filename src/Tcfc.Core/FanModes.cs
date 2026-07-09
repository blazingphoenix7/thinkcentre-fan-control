using System.Management;
using System.Runtime.Versioning;

namespace Tcfc.Core;

/// <summary>
/// Reads and sets the firmware fan mode through the LENOVO_GAMEZONE_DATA WMI
/// class in root\wmi — the same GetSmartFanMode/SetSmartFanMode calls the
/// vendor's own utility makes.
/// </summary>
public static class FanModes
{
    /// <summary>
    /// Decodes a GetSupportThermalMode bitmask: mode m is supported when bit
    /// (int)m of the mask is set (mask 14 = 0b1110 → Quiet, Balanced,
    /// Performance). Pure logic; no WMI.
    /// </summary>
    public static FanMode[] SupportedFromMask(int mask)
    {
        var all = new[] { FanMode.Quiet, FanMode.Balanced, FanMode.Performance };
        return all.Where(m => ((mask >> (int)m) & 1) == 1).ToArray();
    }

    /// <summary>Reads the current fan mode (GetSmartFanMode).</summary>
    [SupportedOSPlatform("windows")]
    public static FanMode Get()
    {
        using var gameZone = GetGameZoneInstance();
        using var result = gameZone.InvokeMethod("GetSmartFanMode", (ManagementBaseObject?)null, null);
        return (FanMode)Convert.ToUInt32(result["Data"]);
    }

    /// <summary>Sets the fan mode (SetSmartFanMode).</summary>
    [SupportedOSPlatform("windows")]
    public static void Set(FanMode mode)
    {
        using var gameZone = GetGameZoneInstance();
        using var inParams = gameZone.GetMethodParameters("SetSmartFanMode");
        inParams["Data"] = (uint)mode;
        using var result = gameZone.InvokeMethod("SetSmartFanMode", inParams, null);
    }

    /// <summary>Queries which modes the firmware supports (GetSupportThermalMode).</summary>
    [SupportedOSPlatform("windows")]
    public static FanMode[] Supported()
    {
        using var gameZone = GetGameZoneInstance();
        using var result = gameZone.InvokeMethod("GetSupportThermalMode", (ManagementBaseObject?)null, null);
        return SupportedFromMask((int)Convert.ToUInt32(result["Data"]));
    }

    /// <summary>
    /// Returns the single LENOVO_GAMEZONE_DATA instance from root\wmi.
    /// Caller owns (and should dispose) the returned object.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static ManagementObject GetGameZoneInstance()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\wmi", "SELECT * FROM LENOVO_GAMEZONE_DATA");
            foreach (ManagementObject instance in searcher.Get())
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
