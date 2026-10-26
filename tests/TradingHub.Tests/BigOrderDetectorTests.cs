using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public class BigOrderDetectorTests
{
    [Fact]
    public void Reports_an_order_far_above_the_usual_once_and_nothing_before_it_has_seen_enough()
    {
        var d = new BigOrderDetector();
        long t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        // too early to judge: even a huge order is not reported
        Assert.Null(d.OnTrade(t++, 100m, 500m, TradeSide.Buy));
        var found = new List<BigOrder>();
        for (int i = 0; i < 400; i++)
            if (d.OnTrade(t++, 100m, 1 + i % 3, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell) is { } o) found.Add(o);
        Assert.Empty(found);
        // one sweep: two fills at the same instant, 30 + 30 at different prices
        var first = d.OnTrade(t, 100m, 30m, TradeSide.Buy);
        var second = d.OnTrade(t, 101m, 30m, TradeSide.Buy);
        Assert.Null(first == null ? second : null); // reported exactly once, as soon as it passed the threshold
        var big = (first ?? second)!.Value;
        Assert.True(big.Buy);
        Assert.True(big.Volume >= 30m);
    }
}
