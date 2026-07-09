using Tcfc.Core;
using Xunit;

public class FanModeTests
{
    [Fact]
    public void Mask14_Gives_Quiet_Balanced_Performance()
        => Assert.Equal(
            new[] { FanMode.Quiet, FanMode.Balanced, FanMode.Performance },
            FanModes.SupportedFromMask(14));

    [Fact]
    public void Mask4_GivesOnlyBalanced()
        => Assert.Equal(new[] { FanMode.Balanced }, FanModes.SupportedFromMask(4));

    [Fact]
    public void Mask2_GivesOnlyQuiet()
        => Assert.Equal(new[] { FanMode.Quiet }, FanModes.SupportedFromMask(2));
}
