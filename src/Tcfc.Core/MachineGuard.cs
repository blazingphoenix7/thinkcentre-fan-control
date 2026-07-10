namespace Tcfc.Core;

public static class MachineGuard
{
    /// <summary>Tach register pair is big-endian: 0x00 is the high byte, 0x01 the low.</summary>
    public static int RpmFromBytes(int hi, int lo) => ((hi & 0xFF) << 8) | (lo & 0xFF);

    /// <summary>Null when either byte is the -1 sentinel, so a timed-out read never turns into a fake RPM.</summary>
    public static int? RpmOrNull(int hi, int lo) => (hi < 0 || lo < 0) ? (int?)null : RpmFromBytes(hi, lo);

    /// <summary>Only the board this was verified on (M70t Gen 6, "3376").</summary>
    public static bool IsSupportedBoard(string? boardProduct) => boardProduct?.Trim() == "3376";
}
