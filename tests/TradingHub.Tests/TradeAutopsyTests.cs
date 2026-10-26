using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

public class TradeAutopsyTests
{
    private static readonly DateTime T0 = new(2026, 3, 2, 15, 0, 0, DateTimeKind.Utc);

    private static Trade Tk(int sec, decimal price, decimal vol = 1, TradeSide side = TradeSide.Buy) => new(sec, T0.AddSeconds(sec), price, vol, side);

    [Fact]
    public void Measures_excursions_exit_efficiency_and_what_came_after()
    {
        var ticks = new List<Trade>();
        // an hour of quiet buying at 5000, then the trade: in at 5000 (t=3600), best 5002.5 at +60 s, worst 4999 at +20 s, out at 5001.5 (t=3900), then price runs on to 5003
        for (int s = 0; s < 3600; s += 10) ticks.Add(Tk(s, 5000, 1, TradeSide.Buy));
        ticks.Add(Tk(3600, 5000));
        ticks.Add(Tk(3620, 4999));
        ticks.Add(Tk(3660, 5002.5m));
        ticks.Add(Tk(3900, 5001.5m));
        ticks.Add(Tk(4000, 5003));
        var trade = new AccountTrade("ES", true, T0.AddSeconds(3600), T0.AddSeconds(3900), 1, 5000, 5001.5m, 0, 0);

        var c = TradeAutopsy.Analyze(trade, ticks, 0.25m, new[] { 5010m, 4990m });
        Assert.Equal(6m, c.ResultTicks);        // 1.5 points = 6 ticks
        Assert.Equal(10m, c.MfeTicks);
        Assert.Equal(4m, c.MaeTicks);
        Assert.Equal(TimeSpan.FromSeconds(60), c.TimeToMfe);
        Assert.Equal(0.6m, c.ExitEfficiency);   // kept 6 of the best 10 ticks
        Assert.Equal(6m, c.AfterExit5MinTicks); // 5003 vs 5001.5
        Assert.True(c.DeltaBefore5MinWithYou > 0, "the tape was buying before a long entry");
        Assert.Equal(40m, Math.Abs(c.NearestLevelTicks!.Value)); // 5010 is 10 points = 40 ticks away; 4990 is as far
    }

    [Fact]
    public void A_short_is_measured_in_its_own_direction()
    {
        var ticks = new List<Trade> { Tk(0, 5000), Tk(10, 5000), Tk(20, 4998), Tk(30, 5001), Tk(40, 4999) };
        var trade = new AccountTrade("ES", false, T0.AddSeconds(10), T0.AddSeconds(40), 1, 5000, 4999, 0, 0);
        var c = TradeAutopsy.Analyze(trade, ticks, 0.25m, Array.Empty<decimal>());
        Assert.Equal(4m, c.ResultTicks);
        Assert.Equal(8m, c.MfeTicks);   // fell to 4998
        Assert.Equal(4m, c.MaeTicks);   // rose to 5001
        Assert.Null(c.NearestLevelTicks);
    }
}
