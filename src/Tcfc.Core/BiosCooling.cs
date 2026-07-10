using System.Management;
using System.Runtime.Versioning;

namespace Tcfc.Core;

/// <summary>
/// BIOS "Intelligent Cooling" fan mode via the Lenovo_BiosSetting,
/// Lenovo_SetBiosSetting and Lenovo_SaveBiosSettings WMI classes in root\wmi -
/// the vendor's supported interface for scripted BIOS changes. Reads and writes
/// exactly one setting, IntelligentCoolingPerformanceMode; nothing else in the
/// BIOS is ever touched.
/// </summary>
public static class BiosCooling
{
    public const string SettingName = "IntelligentCoolingPerformanceMode";
    public const string FullSpeedValue = "Full speed";
    public const string StockValue = "Performance Mode";

    /// <summary>
    /// Pulls the active value out of a Lenovo_BiosSetting.CurrentSetting string, e.g.
    /// "IntelligentCoolingPerformanceMode,Full speed;[Optional:Performance Mode,Balance Mode,Full speed]":
    /// the value sits between the first comma and the first semicolon (or end of
    /// string). Anything unparseable reads as not-full-speed rather than an error.
    /// </summary>
    public static bool ParseIsFullSpeed(string? currentSetting)
    {
        if (currentSetting is null)
            return false;
        int comma = currentSetting.IndexOf(',');
        if (comma < 0)
            return false;
        int semi = currentSetting.IndexOf(';', comma + 1);
        string value = semi < 0 ? currentSetting[(comma + 1)..] : currentSetting[(comma + 1)..semi];
        return value.Trim() == FullSpeedValue;
    }

    /// <summary>True iff the BIOS Intelligent Cooling mode is currently "Full speed".</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsFullSpeed()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\wmi", "SELECT CurrentSetting FROM Lenovo_BiosSetting");
            using var results = searcher.Get();
            foreach (ManagementBaseObject setting in results)
            {
                using (setting)
                {
                    if (setting["CurrentSetting"] is string current &&
                        current.StartsWith(SettingName, StringComparison.Ordinal))
                        return ParseIsFullSpeed(current);
                }
            }
        }
        catch (ManagementException ex)
        {
            throw new InvalidOperationException(
                "Lenovo_BiosSetting is not available in root\\wmi on this machine; " +
                "the BIOS-setting WMI interface is missing.", ex);
        }

        throw new InvalidOperationException(
            "No Lenovo_BiosSetting instance reports " + SettingName + "; " +
            "this BIOS does not expose the Intelligent Cooling mode.");
    }

    /// <summary>
    /// Switches the BIOS Intelligent Cooling mode to "Full speed" (on) or back to
    /// the stock "Performance Mode" (off), then commits it with SaveBiosSettings.
    /// Refuses off the verified board even if a caller forgot to gate.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void SetFullSpeed(bool on)
    {
        if (!MachineGuard.IsSupportedBoard(Board.Product()))
            throw new InvalidOperationException("BIOS fan control is only supported on the verified model.");

        Invoke("Lenovo_SetBiosSetting", "SetBiosSetting",
            SettingName + "," + (on ? FullSpeedValue : StockValue));
        Invoke("Lenovo_SaveBiosSettings", "SaveBiosSettings", "");
    }

    // Both setter classes take a single string arg named "parameter" and report
    // their status in an out property literally named "return"; anything other
    // than the exact string "Success" is a failure. No BIOS password is set on
    // the verified board, so SaveBiosSettings gets an empty parameter.
    [SupportedOSPlatform("windows")]
    private static void Invoke(string className, string methodName, string parameter)
    {
        using var instance = GetInstance(className);
        using var inParams = instance.GetMethodParameters(methodName);
        inParams["parameter"] = parameter;
        using var result = instance.InvokeMethod(methodName, inParams, null);
        var status = result["return"] as string;
        if (status != "Success")
            throw new InvalidOperationException(
                $"{className}.{methodName}(\"{parameter}\") failed: {status ?? "(no status)"}");
    }

    // Caller disposes the returned instance.
    [SupportedOSPlatform("windows")]
    private static ManagementObject GetInstance(string className)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\root\wmi", "SELECT * FROM " + className);
            using var results = searcher.Get();
            foreach (ManagementObject instance in results)
                return instance;
        }
        catch (ManagementException ex)
        {
            throw new InvalidOperationException(
                className + " is not available in root\\wmi on this machine; " +
                "the BIOS-setting WMI interface is missing.", ex);
        }

        throw new InvalidOperationException(
            "No " + className + " instance found in root\\wmi; " +
            "the BIOS-setting WMI interface is missing.");
    }
}
