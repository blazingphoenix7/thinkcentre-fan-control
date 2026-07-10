using Tcfc.Core;
using Xunit;

public class MachineGuardTests
{
    [Theory]
    [InlineData(0x04, 0x3B, 1083)]
    [InlineData(0x06, 0xB9, 1721)]
    [InlineData(0x00, 0x00, 0)]
    public void RpmFromBytes_BigEndian(int hi, int lo, int rpm)
        => Assert.Equal(rpm, MachineGuard.RpmFromBytes(hi, lo));

    [Theory]
    [InlineData(0x04, 0x3B, 1083)]
    [InlineData(-1, 0x3B, null)]
    [InlineData(0x04, -1, null)]
    public void RpmOrNull_NullInsteadOfFabricatedReading(int hi, int lo, int? rpm)
        => Assert.Equal(rpm, MachineGuard.RpmOrNull(hi, lo));

    [Theory]
    [InlineData("3376", true)]
    [InlineData(" 3376 ", true)]
    [InlineData("3427", false)]
    [InlineData(null, false)]
    public void IsSupportedBoard_OnlyM70tGen6(string? b, bool ok)
        => Assert.Equal(ok, MachineGuard.IsSupportedBoard(b));
}
