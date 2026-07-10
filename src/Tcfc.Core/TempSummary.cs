namespace Tcfc.Core;

/// <summary>
/// Reduces the raw EC temp block to one display value. The sensor-to-component
/// mapping is unverified, so we only ever claim "hottest sensor", never "CPU"
/// (see docs/research/temp-labeling.md).
/// </summary>
public static class TempSummary
{
    // Offset 0x26 reads 111 under load - real and load-correlated, but not a
    // plain degrees-C temperature. Anything above this is filtered out.
    private const int MaxPlausibleC = 100;

    /// <summary>Hottest reading in (0, 100]; skips 0 (unused slots) and -1 (timed-out reads). Null when nothing qualifies.</summary>
    public static int? Representative(int[] temps)
    {
        int? hottest = null;
        foreach (int t in temps)
        {
            if (t > 0 && t <= MaxPlausibleC && (hottest is null || t > hottest.Value))
                hottest = t;
        }
        return hottest;
    }
}
