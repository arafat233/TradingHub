using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public class LevelMinerTests
{
    private static readonly DateTime T0 = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Finds_the_big_order_and_follows_price_away_back_to_it_and_on_up()
    {
        var trades = new List<Trade>();
        long id = 1;
        // a calm tape of small orders around 1000 for 300 seconds (so the usual size is known)
        for (int s = 0; s < 300; s++) trades.Add(new Trade(id++, T0.AddSeconds(s), 1000m + (s % 2), 1 + s % 2, s % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        // the big order: a 60-lot buy at about 1000 at +300 s (two fills at the same instant)
        trades.Add(new Trade(id++, T0.AddSeconds(300), 1000m, 40, TradeSide.Buy));
        trades.Add(new Trade(id++, T0.AddSeconds(300), 1000m, 20, TradeSide.Buy));
        // price goes 10 ticks up (away), comes back down to 1000 (touch), then rallies 12 ticks (held)
        decimal[] path = { 1002, 1005, 1010, 1006, 1001, 1000, 1004, 1008, 1012 };
        for (int i = 0; i < path.Length; i++) trades.Add(new Trade(id++, T0.AddSeconds(310 + i * 5), path[i], 1, TradeSide.Buy));

        var big = Assert.Single(LevelMiner.FindOrders(trades, 1m));
        Assert.True(big.Buy);
        Assert.Equal(60m, big.Volume);
        Assert.Equal(1000m, big.Price);

        var times = trades.Select(t => t.Time.Ticks).ToArray();
        var prices = trades.Select(t => (long)t.Price).ToArray();
        int[] thresholds = { 4, 8, 20 };
        var trace = LevelMiner.Trace(times, prices, big, 1m, awayTicks: 4, thresholds, TimeSpan.FromDays(1), TimeSpan.FromHours(1));
        Assert.NotNull(trace);
        Assert.Equal(T0.AddSeconds(310 + 5 * 5), trace!.TouchTime);   // the touch is the return to 1000
        Assert.Equal(1, LevelMiner.Outcome(trace, 1));                // +8 on the buy side before -8 against: held
        Assert.Equal(12m, LevelMiner.Simulate(trace, 2, 20, 2, 20));  // neither 20 reached: marked at the end (+12)
        Assert.Equal(8m, LevelMiner.Simulate(trace, 2, 20, 1, 8));    // the +8 target was reached before any stop
    }

    [Fact]
    public void A_level_price_never_returns_to_has_no_trace()
    {
        var trades = new List<Trade>();
        long id = 1;
        for (int s = 0; s < 300; s++) trades.Add(new Trade(id++, T0.AddSeconds(s), 1000m + (s % 2), 1, s % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        trades.Add(new Trade(id++, T0.AddSeconds(300), 1000m, 60, TradeSide.Sell));
        for (int i = 0; i < 20; i++) trades.Add(new Trade(id++, T0.AddSeconds(310 + i), 990m - i, 1, TradeSide.Sell)); // away on the downside, never back
        var big = Assert.Single(LevelMiner.FindOrders(trades, 1m));
        var trace = LevelMiner.Trace(trades.Select(t => t.Time.Ticks).ToArray(), trades.Select(t => (long)t.Price).ToArray(), big, 1m, 4, new[] { 8 }, TimeSpan.FromDays(1), TimeSpan.FromHours(1));
        Assert.Null(trace);
    }
}
