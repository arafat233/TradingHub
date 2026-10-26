using TradingHub.Indicators.Native;

namespace TradingHub.Tests;

public class CvdDivergenceTests
{
    [Fact]
    public void A_new_high_with_lower_cumulative_delta_is_bearish_and_a_new_low_with_higher_delta_is_bullish()
    {
        // highs: peak of 110 at bar 3, then a new high of 112 at bar 8 while the cumulative delta fell from 50 to 20
        decimal[] high = { 100, 105, 108, 110, 107, 106, 105, 108, 112 };
        decimal[] low = { 95, 100, 103, 105, 102, 101, 100, 103, 107 };
        decimal[] cum = { 10, 30, 40, 50, 45, 40, 35, 30, 20 };
        Assert.True(CvdDivergence.Find(high, low, cum, 20, 3, 8));
        // same highs but delta also made a new high: no divergence
        decimal[] cumConfirmed = { 10, 30, 40, 50, 45, 40, 35, 45, 60 };
        Assert.Null(CvdDivergence.Find(high, low, cumConfirmed, 20, 3, 8));

        // mirrored: a new low of 88 at bar 8 after a low of 90 at bar 3, with the cumulative delta higher than back then
        decimal[] high2 = { 100, 99, 98, 97, 99, 100, 101, 99, 95 };
        decimal[] low2 = { 95, 94, 93, 90, 92, 93, 94, 92, 88 };
        decimal[] cum2 = { -10, -30, -40, -50, -45, -40, -35, -30, -20 };
        Assert.False(CvdDivergence.Find(high2, low2, cum2, 20, 3, 8));
    }
}
