using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>The extra Insights evidence: large traders against the crowd, the stale-data guard, the gamma regime and the session stats.</summary>
public class InsightsExtrasTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 0.01m, VolumeStep = 0.001m };
    private static readonly DateTime T0 = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>A second-by-second tape of small alternating trades, with one big buy every 100 seconds.</summary>
    private static TradeStore Tape(int seconds, int gapSeconds = 1)
    {
        var store = new TradeStore(Inst);
        for (int i = 0; i < seconds; i++)
        {
            bool big = i % 100 == 50;
            store.Add(new Trade(i + 1, T0.AddSeconds(i * gapSeconds), 100m, big ? 500m : 1m, big ? TradeSide.Buy : i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        return store;
    }

    [Fact]
    public void Large_prints_are_split_from_everyone_else()
    {
        var store = Tape(600);
        var w = WhaleFlow.Compute(store, Inst, store.LastTime, TimeSpan.FromMinutes(30))!;
        Assert.Equal(6, w.Prints);
        Assert.Equal(6 * 500 * 100m, w.Buy);
        Assert.Equal(0, w.Sell);
        Assert.Contains("agree", WhaleFlow.VersusLean(w, 1));
        Assert.Contains("against", WhaleFlow.VersusLean(w, -1));
        Assert.Null(WhaleFlow.VersusLean(w, 0));
    }

    [Fact]
    public void Too_few_trades_say_nothing()
    {
        var store = Tape(100);
        Assert.Null(WhaleFlow.Compute(store, Inst, store.LastTime, TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Data_is_stale_only_when_a_live_feed_goes_quiet_for_much_longer_than_usual()
    {
        var store = Tape(200);
        Assert.Null(StaleGuard.StaleSeconds(store, store.LastTime.AddSeconds(10), live: true));
        Assert.Equal(20, StaleGuard.StaleSeconds(store, store.LastTime.AddSeconds(20), live: true));
        Assert.Null(StaleGuard.StaleSeconds(store, store.LastTime.AddSeconds(900), live: false)); // a replay is never stale
        var slow = Tape(200, gapSeconds: 10);                                                      // a thin market: 10 s between trades is normal
        Assert.Null(StaleGuard.StaleSeconds(slow, slow.LastTime.AddSeconds(60), live: true));
        Assert.NotNull(StaleGuard.StaleSeconds(slow, slow.LastTime.AddSeconds(120), live: true));
    }

    [Fact]
    public void Gamma_regime_follows_the_flip()
    {
        Assert.Equal(1, GammaRegime.Of(105, 100)!.Value.Sign);
        Assert.Equal(-1, GammaRegime.Of(95, 100)!.Value.Sign);
        Assert.Null(GammaRegime.Of(95, null));
    }

    [Fact]
    public void Session_stats_count_the_day_and_pick_the_busiest_price()
    {
        var store = new TradeStore(Inst);
        store.Add(new Trade(1, T0, 100m, 3m, TradeSide.Buy));
        store.Add(new Trade(2, T0.AddSeconds(1), 101m, 1m, TradeSide.Sell));
        store.Add(new Trade(3, T0.AddSeconds(2), 100m, 1m, TradeSide.Sell));
        var flow = new FlowTracker();
        flow.Update(store);
        Assert.Equal(1, flow.SessionDelta);
        Assert.Equal(100m * 3 - 101m - 100m, flow.SessionDeltaMoney);
        Assert.Equal(Inst.ToTicks(100m), flow.SessionPocTicks);
        store.Add(new Trade(4, T0.AddDays(1), 102m, 1m, TradeSide.Buy)); // the next UTC day starts afresh
        flow.Update(store);
        Assert.Equal(1, flow.SessionDelta);
        Assert.Equal(Inst.ToTicks(102m), flow.SessionPocTicks);
    }

    [Fact]
    public void Options_profile_adds_open_interest_per_strike_and_signs_the_gamma()
    {
        var expiry = T0.AddDays(10);
        var quotes = new List<OptionQuote>
        {
            new(100m, true, expiry, 50, 60), new(100m, false, expiry, 20, 60),
            new(110m, true, expiry, 30, 60), new(90m, false, expiry, 40, 60),
            new(300m, true, expiry, 99, 60), // far from the price: left out
        };
        var rows = OptionsProfile.Build(quotes, 100m, T0);
        Assert.Equal(new[] { 90m, 100m, 110m }, rows.Select(r => r.Strike));
        var at100 = rows.Single(r => r.Strike == 100m);
        Assert.Equal(50, at100.CallOi);
        Assert.Equal(20, at100.PutOi);
        Assert.True(at100.Gex > 0);                       // more calls than puts there: positive
        Assert.True(rows.Single(r => r.Strike == 90m).Gex < 0);
    }
}
