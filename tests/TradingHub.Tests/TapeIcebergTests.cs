using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public class TapeIcebergTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);

    /// <summary>Quiet market: buyers lift a rising ladder, a few lots a print.</summary>
    private static TapeIcebergDetector Warm()
    {
        var d = new TapeIcebergDetector();
        for (int i = 0; i < 1200; i++) d.OnTrade(T0.AddSeconds(i * 15), 1000 + i, 2, buy: true);
        return d;
    }

    [Fact]
    public void Many_prints_at_one_price_far_above_a_normal_level_that_holds_are_an_iceberg()
    {
        var d = Warm();
        var t = T0.AddHours(6);
        for (int i = 0; i < 20; i++) d.OnTrade(t.AddMilliseconds(i * 300), 5000, 3, buy: true); // 60 lots at one price, never lifted through
        var ev = Assert.Single(d.Events);
        Assert.True(ev.Ask);
        Assert.Equal(5000, ev.Ticks);
        Assert.True(ev.Volume > 30);
    }

    [Fact]
    public void A_level_the_price_trades_through_is_not_flagged()
    {
        var d = Warm();
        var t = T0.AddHours(6);
        for (int i = 0; i < 20; i++)
        {
            d.OnTrade(t.AddMilliseconds(i * 300), 5000, 3, buy: true);
            if (i == 3) d.OnTrade(t.AddMilliseconds(i * 300 + 1), 5001, 1, buy: true); // lifted through: it was not a wall
        }

        Assert.Empty(d.Events);
    }

    [Fact]
    public void Few_prints_or_normal_volume_are_ignored_and_nothing_fires_before_the_warm_up()
    {
        var d = Warm();
        var t = T0.AddHours(6);
        for (int i = 0; i < 5; i++) d.OnTrade(t.AddMilliseconds(i * 300), 5000, 50, buy: true);   // big but only 5 prints
        for (int i = 0; i < 20; i++) d.OnTrade(t.AddSeconds(30).AddMilliseconds(i * 300), 6000, 0.1m, buy: true); // many prints, tiny
        Assert.Empty(d.Events);

        var cold = new TapeIcebergDetector();
        for (int i = 0; i < 20; i++) cold.OnTrade(T0.AddMilliseconds(i * 300), 5000, 3, buy: true);
        Assert.Empty(cold.Events);
    }
}
