using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Related markets: who is a peer of whom, relative moves, and which of two tapes moves first.</summary>
public class RelatedMarketsTests
{
    private static readonly Instrument Es = new() { Symbol = "ES.F", FeedId = "lse", TickSize = 0.25m, VolumeStep = 1 };
    private static readonly Instrument Nq = new() { Symbol = "NQ.F", FeedId = "lse", TickSize = 0.25m, VolumeStep = 1 };
    private static readonly DateTime Now = new(2026, 10, 26, 15, 0, 0, DateTimeKind.Utc);

    /// <summary>A tape of one trade a second for 40 minutes whose price is base + a random walk (a seeded one), optionally delayed.</summary>
    private static TradeStore Tape(Instrument inst, decimal basePrice, int seed, int delaySeconds = 0, decimal drift30 = 0)
    {
        var rng = new Random(seed);
        int seconds = 40 * 60;
        var steps = new decimal[seconds + 400];
        decimal level = 0;
        // the walk is the same for the same seed; a delayed copy is that walk shifted in time
        for (int i = 0; i < steps.Length; i++) { level += (decimal)(rng.NextDouble() - 0.5) * 2m; steps[i] = level; }
        var store = new TradeStore(inst);
        long id = 1;
        for (int s = 0; s < seconds; s++)
        {
            int src = Math.Max(0, s + 200 - delaySeconds);
            decimal price = basePrice + steps[src] * 0.25m + drift30 * s / seconds;
            store.Add(new Trade(id++, Now.AddSeconds(s - seconds), inst.RoundPrice(price), 1, TradeSide.Buy));
        }

        return store;
    }

    [Fact]
    public void Peers_are_the_sibling_futures_and_the_other_coin_and_unknown_markets_have_none()
    {
        Assert.Contains("NQ.F", RelatedMarkets.PeersOf(Es));
        Assert.Contains("GC.F", RelatedMarkets.PeersOf(new Instrument { Symbol = "SI.F", FeedId = "lse", TickSize = 0.005m }));
        Assert.Equal(new[] { "ETHUSDT" }, RelatedMarkets.PeersOf(new Instrument { Symbol = "BTCUSDT", FeedId = "bybit-linear", TickSize = 0.1m }));
        Assert.Equal(new[] { "BTC-USD" }, RelatedMarkets.PeersOf(new Instrument { Symbol = "ETH-USD", FeedId = "coinbase-spot", TickSize = 0.01m }));
        Assert.Empty(RelatedMarkets.PeersOf(new Instrument { Symbol = "AAPL", FeedId = "lse", TickSize = 0.01m }));
    }

    [Fact]
    public void A_market_that_follows_another_ten_seconds_later_is_found_to_lag_it()
    {
        var nq = Tape(Nq, 29000m, seed: 7);                         // the peer moves first
        var es = Tape(Es, 7600m, seed: 7, delaySeconds: 10);       // the same walk ten seconds later
        var stats = RelatedMarkets.Compute(Es, es, Nq, nq, Now);
        Assert.NotNull(stats);
        Assert.Contains("NQ.F moves first", stats!.Lead);
        Assert.Matches(@"about (5|10|15) s", stats.Lead);
    }

    [Fact]
    public void The_other_way_round_the_chart_instrument_is_named_as_the_one_that_moves_first()
    {
        var nq = Tape(Nq, 29000m, seed: 11, delaySeconds: 10);
        var es = Tape(Es, 7600m, seed: 11);
        var stats = RelatedMarkets.Compute(Es, es, Nq, nq, Now);
        Assert.Contains("ES.F moves first", stats!.Lead);
    }

    [Fact]
    public void Unrelated_random_walks_have_no_leader()
    {
        var stats = RelatedMarkets.Compute(Es, Tape(Es, 7600m, seed: 3), Nq, Tape(Nq, 29000m, seed: 99), Now);
        Assert.DoesNotContain("moves first", stats!.Lead);
    }

    [Fact]
    public void Moves_and_the_gap_between_the_two_are_in_basis_points_and_a_tape_that_is_too_short_gives_nothing()
    {
        var es = Tape(Es, 7600m, seed: 5, drift30: 0m);
        var nq = Tape(Nq, 29000m, seed: 5, drift30: 290m);          // the peer ends 1% higher over the 40 minutes
        var stats = RelatedMarkets.Compute(Es, es, Nq, nq, Now)!;
        Assert.True(stats.Change30Bp > 50, $"30 minute move {stats.Change30Bp}");
        // the gap is this market's own 30 minute move minus the peer's, both read from the tapes
        decimal Move(TradeStore s) => (decimal)(s.GetPriceTicks(s.LowerBound(Now.AddTicks(1)) - 1) - s.GetPriceTicks(s.LowerBound(Now.AddMinutes(-30).AddTicks(1)) - 1)) / s.GetPriceTicks(s.LowerBound(Now.AddMinutes(-30).AddTicks(1)) - 1) * 10_000m;
        Assert.Equal(Move(es) - Move(nq), stats.Relative30Bp, 1);
        Assert.Contains("NQ.F", stats.Text());

        var short1 = new TradeStore(Es);
        short1.Add(new Trade(1, Now.AddMinutes(-5), 7600m, 1, TradeSide.Buy));
        Assert.Null(RelatedMarkets.Compute(Es, short1, Nq, nq, Now));
    }
}
