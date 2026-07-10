using System.Runtime.Versioning;

namespace Tcfc.Core;

/// <summary>
/// The single fan setting the UI exposes. Quiet/Balanced/Performance are the
/// instant firmware SmartFanMode; FullSpeed is the BIOS Intelligent Cooling
/// "Full speed" setting, which only takes effect after a reboot - both when
/// entering it and when leaving it.
/// </summary>
public enum FanSelection
{
    Quiet,
    Balanced,
    Performance,
    FullSpeed,
}

/// <summary>
/// Unifies the machine's two independent fan mechanisms behind one selector:
/// SmartFanMode (instant but subtle) and the BIOS "Full speed" setting
/// (powerful but reboot-gated in both directions).
/// </summary>
public static class FanControl
{
    /// <summary>
    /// RPM above which the fan counts as actually running at full speed. The
    /// automatic curve peaks near 2800 rpm and true full speed sits near 3980,
    /// so 3500 cleanly separates the two.
    /// </summary>
    public const int FullSpeedRpm = 3500;

    /// <summary>The BIOS full-speed intent wins over whatever SmartFanMode reports.</summary>
    [SupportedOSPlatform("windows")]
    public static FanSelection GetCurrent()
    {
        if (BiosCooling.IsFullSpeed())
            return FanSelection.FullSpeed;
        return FromFanMode(FanModes.Get());
    }

    /// <summary>
    /// Applies a selection with as few BIOS writes as possible. Switching among
    /// Quiet/Balanced/Performance is an instant SmartFanMode write; entering
    /// FullSpeed arms the BIOS setting; leaving FullSpeed both applies the new
    /// SmartFanMode and clears the BIOS setting so the fan drops back after the
    /// next reboot. BIOS NVRAM already in the target state is never rewritten.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Set(FanSelection selection)
    {
        bool currentlyFull = BiosCooling.IsFullSpeed();
        if (selection == FanSelection.FullSpeed)
        {
            if (!currentlyFull)
                BiosCooling.SetFullSpeed(true);
        }
        else
        {
            FanModes.Set(ToFanMode(selection));
            if (currentlyFull)
                BiosCooling.SetFullSpeed(false);
        }
    }

    /// <summary>
    /// True when the BIOS intent disagrees with what the fan is actually doing:
    /// the BIOS wants full speed but the fan is not maxed yet (reboot to go
    /// loud), or the BIOS is back to stock but the fan is still maxed (reboot
    /// to go quiet). A failed RPM read (-1) correctly counts as not maxed.
    /// </summary>
    public static bool IsRestartPending(bool biosFullSpeed, int currentRpm)
        => biosFullSpeed != (currentRpm >= FullSpeedRpm);

    /// <summary>
    /// The SmartFanMode a selection maps to, by name - the enums' numeric
    /// values differ, because FanMode carries the raw WMI Data values.
    /// FullSpeed lives in the BIOS, not in SmartFanMode, so it has no mapping.
    /// </summary>
    public static FanMode ToFanMode(FanSelection selection) => selection switch
    {
        FanSelection.Quiet => FanMode.Quiet,
        FanSelection.Balanced => FanMode.Balanced,
        FanSelection.Performance => FanMode.Performance,
        _ => throw new ArgumentOutOfRangeException(nameof(selection), selection,
            "Only Quiet/Balanced/Performance map to a SmartFanMode."),
    };

    /// <summary>Inverse of <see cref="ToFanMode"/> for the three shared names.</summary>
    public static FanSelection FromFanMode(FanMode mode) => mode switch
    {
        FanMode.Quiet => FanSelection.Quiet,
        FanMode.Balanced => FanSelection.Balanced,
        FanMode.Performance => FanSelection.Performance,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown fan mode."),
    };
}
