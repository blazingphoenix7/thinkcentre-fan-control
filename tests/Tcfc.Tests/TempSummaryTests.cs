using Tcfc.Core;
using Xunit;

public class TempSummaryTests
{
    [Fact]
    public void PicksHottestPlausibleSensor_AndFiltersImplausiblyHigh()
        => Assert.Equal(82, TempSummary.Representative(new[] { 45, 0, 82, 0, 111, 0 }));

    [Fact]
    public void AllSensorsUnused_ReturnsNull()
        => Assert.Null(TempSummary.Representative(new[] { 0, 0, 0 }));

    [Fact]
    public void TimedOutOffsets_AreSkipped()
        => Assert.Equal(50, TempSummary.Representative(new[] { -1, 50, -1 }));
}
