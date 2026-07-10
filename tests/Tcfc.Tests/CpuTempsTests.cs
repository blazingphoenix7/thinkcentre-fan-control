using Tcfc.Core;
using Xunit;

// Raw MSR values captured on the verified board (Core Ultra 5 225).
public class CpuTempsTests
{
    [Fact]
    public void DecodeTjmax_FromRawMsr1A2()
        => Assert.Equal(105, CpuTemps.DecodeTjmax(0x0000000085691400L));

    [Fact]
    public void DecodeCoreTempC_ValidReading_OffsetBelowTjmax()
        => Assert.Equal(94, CpuTemps.DecodeCoreTempC(0x880B2A82L, 105));

    [Fact]
    public void DecodeCoreTempC_SmallerOffset_HotterCore()
        => Assert.Equal(100, CpuTemps.DecodeCoreTempC(0x88052A82L, 105));

    [Fact]
    public void DecodeCoreTempC_ValidBitClear_ReturnsMinusOne()
        => Assert.Equal(-1, CpuTemps.DecodeCoreTempC(0x000B2A82L, 105));
}
