using Tcfc.Core;
using Xunit;

// CurrentSetting strings as Lenovo_BiosSetting reports them on the verified board.
public class BiosCoolingTests
{
    private const string Tail = ";[Optional:Performance Mode,Balance Mode,Full speed]";

    [Fact]
    public void FullSpeed_ParsesTrue()
        => Assert.True(BiosCooling.ParseIsFullSpeed(
            "IntelligentCoolingPerformanceMode,Full speed" + Tail));

    [Theory]
    [InlineData("Performance Mode")]
    [InlineData("Balance Mode")]
    public void OtherModes_ParseFalse(string value)
        => Assert.False(BiosCooling.ParseIsFullSpeed(
            "IntelligentCoolingPerformanceMode," + value + Tail));

    [Fact]
    public void ExtraWhitespace_IsTolerated()
        => Assert.True(BiosCooling.ParseIsFullSpeed(
            "IntelligentCoolingPerformanceMode , Full speed " + Tail));

    [Theory]
    [InlineData("")]
    [InlineData("Full speed")]
    [InlineData("IntelligentCoolingPerformanceMode")]
    [InlineData("no delimiters at all")]
    public void UnexpectedShape_ParsesFalse_InsteadOfThrowing(string s)
        => Assert.False(BiosCooling.ParseIsFullSpeed(s));
}
