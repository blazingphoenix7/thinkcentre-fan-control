using Tcfc.Core;
using Xunit;

public class FanControlTests
{
    // A restart is pending whenever the BIOS intent disagrees with what the fan
    // is actually doing. EcReader reports -1 when a read fails, which correctly
    // counts as "not maxed".
    [Theory]
    [InlineData(true, 930, true)]    // BIOS wants full speed, fan still on the auto curve
    [InlineData(true, 3980, false)]  // BIOS wants full speed, fan already maxed
    [InlineData(false, 3980, true)]  // BIOS back to stock, fan still maxed
    [InlineData(false, 930, false)]  // BIOS stock, fan on the auto curve
    [InlineData(true, -1, true)]     // failed RPM read counts as not maxed
    [InlineData(false, -1, false)]
    [InlineData(true, 3500, false)]  // the threshold itself counts as maxed
    [InlineData(false, 3500, true)]
    public void IsRestartPending_ComparesBiosIntentToActualRpm(
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
