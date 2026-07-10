namespace Tcfc.Core;

/// <summary>
/// Pure-logic guards for the target machine: fan tach decoding and board gating.
/// </summary>
public static class MachineGuard
{
    /// <summary>
    /// Decodes fan RPM from the EC tach register pair: byte 0x00 is the high
    /// byte, 0x01 the low byte — a 16-bit big-endian value.
    /// </summary>
    public static int RpmFromBytes(int hi, int lo) => ((hi & 0xFF) << 8) | (lo & 0xFF);

    /// <summary>
    /// Like <see cref="RpmFromBytes"/>, but returns null when either byte is
    /// the unavailable sentinel (-1) — a timed-out read must surface as "no
    /// reading", never be masked into a fabricated RPM.
    /// </summary>
    public static int? RpmOrNull(int hi, int lo) => (hi < 0 || lo < 0) ? (int?)null : RpmFromBytes(hi, lo);

    /// <summary>
    /// True only for the verified Win32_BaseBoard.Product of the target
    /// ThinkCentre M70t Gen 6 ("3376"). Everything else is unsupported.
    /// </summary>
    public static bool IsSupportedBoard(string? boardProduct) => boardProduct?.Trim() == "3376";
}
