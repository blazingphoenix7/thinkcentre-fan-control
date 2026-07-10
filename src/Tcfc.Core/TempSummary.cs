namespace Tcfc.Core;

/// <summary>
/// Pure logic that reduces the raw EC temperature block to one honest display
/// value. The sensor-to-component mapping of this block is unverified, so no
/// value is ever labelled "CPU" or similar — the app only ever claims
/// "hottest sensor". See <c>docs/research/temp-labeling.md</c>.
/// </summary>
public static class TempSummary
{
    /// <summary>
    /// A byte that cannot plausibly be a Celsius reading of a machine that is
    /// still running. Offset 0x26 was measured at 111 under load — real, and
    /// load-correlated, but not a plain degrees-C temperature.
    /// </summary>
    private const int MaxPlausibleC = 100;

    /// <summary>
    /// The hottest plausible sensor value: the maximum reading in
    /// (0, 100] °C. This skips 0 (unused sensor slots), -1 (timed-out EC
    /// reads), and implausibly high bytes that are not plain temperatures.
    /// Returns null when nothing qualifies.
    /// </summary>
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
