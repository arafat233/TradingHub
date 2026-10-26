using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>The Insights panel's logic: minute bars, the toxicity gauge, the decision checks, wall statistics and the edge meter.</summary>
public class InsightsTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 0.01m, VolumeStep = 0.001m };
    private static readonly DateTime T0 = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc); // on a quarter hour

    /// <summary>
    /// A tape: for each minute 60 trades, one a second. <paramref name="buyShare"/> of them are buys; price starts at 100 and moves by
    /// <paramref name="ticksPerMinute"/> ticks a minute (steady, so the candles and the 15-minute trend agree with it).
    /// </summary>
    private static TradeStore Tape(int minutes, double buyShare, int ticksPerMinute, int firstMinute = 0, TradeStore? into = null, decimal volume = 1m)
    {
        var store = into ?? new TradeStore(Inst);
        long id = store.Count + 1;
        for (int m = firstMinute; m < firstMinute + minutes; m++)
            for (int s = 0; s < 60; s++)
            {
                decimal price = 100m + (m * ticksPerMinute + s * ticksPerMinute / 60m) * 0.01m;
                bool buy = s < 60 * buyShare;
                store.Add(new Trade(id++, T0.AddMinutes(m).AddSeconds(s), Inst.RoundPrice(price), volume, buy ? TradeSide.Buy : TradeSide.Sell));
            }

        return store;
    }

    private static InsightInput Input(TradeStore store, FlowTracker flow, decimal costBps = 6, decimal stop = 0, decimal target = 0)
    {
        flow.Update(store);
        return new InsightInput(Inst, flow, store, DepthSnapshot.Empty, store.LastTime, costBps, stop, target);
    }

    [Fact]
    public void Minute_bars_add_up_buys_and_sells_and_are_not_counted_twice()
    {
        var store = Tape(3, 0.75, 2);
        var flow = new FlowTracker();
        flow.Update(store);
        flow.Update(store);                                              // nothing new: nothing changes
        Assert.Equal(3, flow.Bars.Count);
        Assert.All(flow.Bars, b => { Assert.Equal(45, b.Buy); Assert.Equal(15, b.Sell); });
        Assert.Equal(T0, flow.Bars[0].Start);
        Assert.True(flow.Bars[0].CloseTicks > flow.Bars[0].OpenTicks);
        Tape(1, 0.75, 2, firstMinute: 3, into: store);                   // a fourth minute arrives later
        flow.Update(store);
        Assert.Equal(4, flow.Bars.Count);
        Assert.Equal(45, flow.Bars[^1].Buy);
    }

    [Fact]
    public void Toxicity_is_low_for_a_balanced_tape_and_high_when_one_side_takes_everything_and_it_is_ranked_against_its_own_history()
    {
        var calm = new TradeStore(Inst);
        long id = 1;
        for (int i = 0; i < 6000; i++) calm.Add(new Trade(id++, T0.AddSeconds(i), 100m, 1m, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        var f1 = new FlowTracker();
        f1.Update(calm);
        Assert.True(f1.Vpin < 0.1m, $"balanced: {f1.Vpin}");

        // three hours of balance, then half an hour of buying only (the gauge ranks itself only after 90 minutes of readings)
        var store = new TradeStore(Inst);
        id = 1;
        for (int i = 0; i < 10800; i++) store.Add(new Trade(id++, T0.AddSeconds(i), 100m, 1m, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        for (int i = 10800; i < 12600; i++) store.Add(new Trade(id++, T0.AddSeconds(i), 100m, 1m, TradeSide.Buy));
        var early = new FlowTracker();
        var firstHour = new TradeStore(Inst);
        for (int i = 0; i < 5400; i++) firstHour.Add(store.Get(i));
        early.Update(firstHour);
        Assert.Null(early.VpinPercentile);                                // 90 minutes of tape is not 90 minutes of readings: no ranking yet
        var f2 = new FlowTracker();
        f2.Update(store);
        Assert.True(f2.Vpin > 0.4m, $"one-sided for the last half of the hour the gauge averages: {f2.Vpin}"); // about half its fifty buckets are one-sided
        Assert.True(f2.VpinPercentile >= 0.95m, $"percentile: {f2.VpinPercentile}");
    }

    [Fact]
    public void With_less_than_twenty_minutes_of_tape_the_panel_says_it_is_learning()
    {
        var store = Tape(10, 0.7, 2);
        var d = DecisionEngine.Evaluate(Input(store, new FlowTracker()));
        Assert.Equal(Verdict.Wait, d.Verdict);
        Assert.Contains("10 of 20", d.Learning);
    }

    [Fact]
    public void A_strong_steady_move_with_cheap_costs_passes_all_six_checks_and_is_a_buy_setup()
    {
        var store = Tape(90, 0.8, 4);                                    // buyers take 80%, price rises 4 ticks a minute for an hour and a half
        var d = DecisionEngine.Evaluate(Input(store, new FlowTracker(), costBps: 0.1m, stop: 10, target: 30));
        Assert.Equal(6, d.Checks.Count);
        Assert.True(d.Checks.All(c => c.Pass), string.Join("; ", d.Checks.Where(c => !c.Pass).Select(c => $"{c.Name} {c.Value} needs {c.Need}")));
        Assert.Equal(Verdict.BuySetup, d.Verdict);
        Assert.Equal(1, d.Lean);
        Assert.Equal("BUY SETUP", d.Headline);
        Assert.Contains(d.Chips, c => c.Text.StartsWith("Buyers took") && c.Tone == 1);
        Assert.True(d.BuyAbove > d.SellBelow);
    }

    [Fact]
    public void The_same_move_with_expensive_costs_is_only_a_lean_and_the_failing_checks_are_named()
    {
        var store = Tape(90, 0.8, 4);
        var d = DecisionEngine.Evaluate(Input(store, new FlowTracker(), costBps: 60, stop: 10, target: 30)); // 0.6% round trip against a 0.3% target
        Assert.Equal(Verdict.Wait, d.Verdict);
        Assert.Equal(1, d.Lean);
        Assert.False(d.Checks.Single(c => c.Name == "Target vs costs").Pass);
        Assert.False(d.Checks.Single(c => c.Name == "R:R after costs").Pass);
        Assert.Contains("Leaning buy", d.Detail);
        Assert.Contains("missing", d.Detail);
    }

    [Fact]
    public void A_balanced_tape_has_no_lean_and_waits()
    {
        var store = Tape(60, 0.5, 0);
        var d = DecisionEngine.Evaluate(Input(store, new FlowTracker()));
        Assert.Equal(Verdict.Wait, d.Verdict);
        Assert.Equal(0, d.Lean);
        Assert.Contains("balanced", d.Detail);
    }

    [Fact]
    public void Wall_statistics_count_how_many_of_the_gone_walls_were_traded_through_and_need_three_to_say()
    {
        var now = T0.AddMinutes(10);
        BookEvent E(BookEventKind k, bool ask, double sec) => new(now.AddSeconds(-sec), k, ask, 100, 5);
        var events = new[]
        {
            E(BookEventKind.Added, false, 50),
            E(BookEventKind.Eaten, false, 40), E(BookEventKind.Pulled, false, 30), E(BookEventKind.Flash, false, 20), E(BookEventKind.Pulled, false, 10), // bids: 1 of 4 real
            E(BookEventKind.Eaten, true, 40), E(BookEventKind.Eaten, true, 30),                                                                             // offers: only 2 gone: too few
            E(BookEventKind.Pulled, false, 3000),                                                                                                           // too old
        };
        var w = WallStats.Compute(events, now, TimeSpan.FromMinutes(10));
        Assert.Equal(0.25m, w.BidReal);
        Assert.Equal(4, w.BidCount);
        Assert.Null(w.AskReal);
        Assert.Equal(2, w.AskCount);
    }

    [Fact]
    public void Stacked_imbalances_need_three_rows_in_a_row_where_buyers_took_at_least_three_times_the_sellers_one_row_below()
    {
        var store = new TradeStore(Inst);
        long id = 1;
        // twenty one-tick price rows; rows 5 to 10 have buyers at 10 where sellers one row below took 1: a stack of six buy imbalances
        for (int row = 0; row < 20; row++)
            for (int k = 0; k < 4; k++)
            {
                store.Add(new Trade(id++, T0.AddSeconds(id), 100m + row * 0.01m, row is >= 5 and <= 10 ? 10m : 1m, TradeSide.Buy));
                store.Add(new Trade(id++, T0.AddSeconds(id), 100m + row * 0.01m, 1m, TradeSide.Sell));
            }

        var (buy, sell) = DecisionEngine.StackedImbalances(store, Inst, store.LastTime, TimeSpan.FromMinutes(5));
        Assert.Equal(6, buy);
        Assert.Equal(0, sell);
    }

    [Fact]
    public void The_edge_meter_writes_down_a_setup_scores_it_fifteen_minutes_later_after_costs_and_keeps_the_score()
    {
        string path = Path.Combine(Path.GetTempPath(), $"edge-{Guid.NewGuid():N}.json");
        try
        {
            var store = Tape(40, 0.8, 4);                               // rising 4 ticks a minute
            var edge = new EdgeTracker(path);
            var t = T0.AddMinutes(10);
            // the panel calls a buy setup at minute 10, and again 1 minute later (inside the cooldown: not a new one)
            var atTen = new TradeStore(Inst);
            Tape(11, 0.8, 4, into: atTen);
            edge.Observe(Verdict.BuySetup, Inst, atTen, atTen.LastTime, costBps: 1);
            edge.Observe(Verdict.BuySetup, Inst, atTen, atTen.LastTime.AddMinutes(1), costBps: 1);
            Assert.Single(edge.Setups);
            Assert.Equal(0, edge.Scored);
            Assert.Contains("learning: 0 of 120", edge.Text());

            // fifteen minutes on, the full tape reaches it: price rose 4 ticks a minute for 15 minutes = about 60 ticks, minus costs (1 bp of 100.4 = 1 tick)
            edge.Observe(Verdict.Wait, Inst, store, t.AddMinutes(16), costBps: 1);
            Assert.Equal(1, edge.Scored);
            Assert.InRange(edge.Setups[0].ResultTicks!.Value, 50, 62);
            Assert.Equal(1m, edge.Summary().HitRate);

            var again = new EdgeTracker(path);                           // reopened: the score is still there
            Assert.Equal(1, again.Scored);
            Assert.InRange(again.Setups[0].ResultTicks!.Value, 50, 62);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_sell_setup_into_a_rising_market_is_scored_as_a_loss()
    {
        var store = Tape(40, 0.8, 4);
        var edge = new EdgeTracker(null);
        var atTen = new TradeStore(Inst);
        Tape(11, 0.8, 4, into: atTen);
        edge.Observe(Verdict.SellSetup, Inst, atTen, atTen.LastTime, costBps: 1);
        edge.Observe(Verdict.Wait, Inst, store, T0.AddMinutes(26), costBps: 1);
        Assert.True(edge.Setups[0].ResultTicks < 0);
        Assert.Equal(0m, edge.Summary().HitRate);
    }
}

public class InsightsReplayTests
{
    /// <summary>A feed that is not live and whose clock stands still.</summary>
    private sealed class FixedClockFeed(DateTime now) : TradingHub.Feeds.IMarketDataFeed
    {
        public string Id => "fixed";
        public string DisplayName => "Fixed clock";
        public bool SupportsHistory => false;
        public bool IsLive => false;
        public DateTime Now => now;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(Array.Empty<Instrument>());
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument instrument, TradingHub.Feeds.IMarketDataSink sink) => new Nothing();
        public void Dispose() { }
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 0.01m, VolumeStep = 0.001m };

    [Fact]
    public void Calls_on_a_replay_or_the_simulator_are_listed_and_scored_in_their_own_memory_and_never_in_the_real_meter()
    {
        var end = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        var data = new InstrumentData(Inst, new FixedClockFeed(end));                // not live, with a clock that stands still: a replay paused at this moment
        long id = 1;
        for (int m = 0; m < 90; m++)                                                  // buyers take 80% and price rises 4 ticks a minute
            for (int s = 0; s < 60; s++)
            {
                var t = end.AddMinutes(-90 + m).AddSeconds(s);
                data.Trades.Add(new Trade(id++, t, Inst.RoundPrice(100m + (m * 4 + s * 4 / 60m) * 0.01m), 1m, s < 48 ? TradeSide.Buy : TradeSide.Sell));
            }

        var snap = data.Insights.Compute(costBps: 0.1m, stopTicks: 10, targetTicks: 30);
        Assert.True(snap.Decision.Verdict == Verdict.BuySetup, "checks: " + string.Join("; ", snap.Decision.Checks.Select(c => $"{c.Name} {c.Value} {(c.Pass ? "ok" : "FAIL")}")) + " | " + snap.Decision.Detail + " | " + snap.Decision.Learning);
        Assert.Empty(data.Insights.Edge.Setups);                                      // the instrument's own, saved score is untouched
        Assert.Single(data.Insights.ReplayEdge.Setups);
        var call = Assert.Single(snap.Calls!);
        Assert.Contains("BUY setup at", call);
        Assert.Contains("waiting for its 15 minutes", call);
        Assert.Contains("(this replay only)", snap.EdgeText);
    }
}
