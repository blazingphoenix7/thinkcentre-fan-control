using Tcfc.Core;
using Xunit;

public class FanControlTests
{
    // A restart is pending only while Full Speed is armed in the BIOS but the fan
    // has not reached full speed yet. Leaving Full Speed is immediate, so a stock
    // BIOS is never pending regardless of the current rpm. EcReader reports -1 on a
    // failed read, which correctly counts as "not maxed".
    [Theory]
    [InlineData(true, 930, true)]    // Full Speed armed, fan not there yet -> reboot to engage
    [InlineData(true, 3980, false)]  // Full Speed armed and the fan is already maxed
    [InlineData(false, 3980, false)] // stock BIOS: leaving is live, so never pending
    [InlineData(false, 930, false)]  // stock BIOS, fan on the auto curve
    [InlineData(true, -1, true)]     // failed RPM read counts as not maxed
    [InlineData(false, -1, false)]
    [InlineData(true, 3500, false)]  // the threshold itself counts as maxed
    [InlineData(false, 3500, false)]
    public void IsRestartPending_OnlyWhenFullSpeedArmedButNotEngaged(
        bool biosFullSpeed, int currentRpm, bool expected)
        => Assert.Equal(expected, FanControl.IsRestartPending(biosFullSpeed, currentRpm));

    [Theory]
    [InlineData(FanSelection.Quiet, FanMode.Quiet)]
    [InlineData(FanSelection.Balanced, FanMode.Balanced)]
    [InlineData(FanSelection.Performance, FanMode.Performance)]
    public void ToFanMode_MapsByName(FanSelection selection, FanMode expected)
        => Assert.Equal(expected, FanControl.ToFanMode(selection));

    [Fact]
    public void ToFanMode_FullSpeed_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => FanControl.ToFanMode(FanSelection.FullSpeed));

    [Theory]
    [InlineData(FanSelection.Quiet)]
    [InlineData(FanSelection.Balanced)]
    [InlineData(FanSelection.Performance)]
    public void FromFanMode_RoundTripsTheSharedNames(FanSelection selection)
        => Assert.Equal(selection, FanControl.FromFanMode(FanControl.ToFanMode(selection)));
}
