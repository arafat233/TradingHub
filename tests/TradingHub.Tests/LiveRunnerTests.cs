using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

/// <summary>
/// Live execution of a strategy: the portfolio size does not break the exits, a pause only allows reducing, the
/// risk limits halt or stop it, and closing never sends more than what the account holds.
/// </summary>
[Collection("LiveStore")] // they change the portfolio folder (static): not in parallel
public class LiveRunnerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub_live_" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = LiveStore.Root;
    private readonly Instrument _inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
    private InstrumentData _data = null!;
    private long _id = 1;
    private DateTime _time = T0;

    public LiveRunnerTests() => LiveStore.Root = _root;

    public void Dispose()
    {
        LiveStore.Root = _previousRoot;
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // cleanup
        }
    }

    /// <summary>Strategy that does whatever the test asks it to on the next candle.</summary>
    private sealed class Scripted : Strategy
    {
        public readonly Queue<Action<Scripted>> Next = new();
        public readonly List<decimal> Seen = new();

        protected override void OnBar(Bar bar)
        {
            Seen.Add(Position);
            if (Next.Count > 0) Next.Dequeue()(this);
        }

        public void DoBuy(decimal q) => Buy(q);
        public void DoSell(decimal q) => Sell(q);
        public void DoClose() => ClosePosition();
        public void DoShort(decimal q) => EnterShort(q);
        public void DoLong(decimal q) => EnterLong(q);
    }

    private (Scripted Strategy, SimBroker Broker, LiveStrategyRunner Runner) Setup(decimal size = 1, Action<LiveStrategyRunner>? configure = null, SimBroker? broker = null)
    {
        _data = new InstrumentData(_inst, new SimulatedFeed());
        for (int i = 0; i < 5; i++) _data.Trades.Add(new Trade(_id++, (_time = T0.AddSeconds(i * 30)), 100, 1, TradeSide.Buy));
        broker ??= new SimBroker(_data, new Portfolio(), new TradingStatistics());
        var s = new Scripted();
        var r = new LiveStrategyRunner(s, _data, broker, ChartPeriod.M1) { SizeMultiplier = size };
        configure?.Invoke(r);
        r.Start();
        return (s, broker, r);
    }

    /// <summary>A new trade arrives one minute later (it closes the previous candle).</summary>
    private void Tick(decimal price, double minutes = 1)
    {
        int start = _data.Trades.Count;
        _time = _time.AddMinutes(minutes);
        _data.Trades.Add(new Trade(_id++, _time, price, 1, TradeSide.Buy));
        var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        ((Action<int, int>?)field!.GetValue(_data))?.Invoke(start, 1);
    }

    [Fact]
    public void Live_the_context_starts_with_the_month_of_history_the_app_passes_it()
    {
        var (s, _, r) = Setup();
        // with two minutes of warm-up there is no context (and with no data the filter does not block)
        Assert.Equal(MarketRegime.Unknown, s.Regime);

        // a month of hourly closes rising straight up to the current price
        var hourly = Enumerable.Range(0, 31 * 24).Select(i => (T0.AddHours(-31 * 24 + i), 100m - (31 * 24 - i) * 0.05m)).ToList();
        r.SeedContext(hourly);
        Assert.Equal(MarketRegime.Trend, s.Regime);
        Assert.Equal(1, s.MarketDirection);
        Assert.NotNull(s.VolatilityRank);
        Assert.Equal(31, r.ContextDays);
        Assert.Contains(r.LogLines, l => l.Contains("Market context with 31 days", StringComparison.Ordinal));

        // a "range only" filter now blocks entries (before, with no data, it would have entered in any context)
        s.RegimeFilter = RegimeFilter.RangeOnly;
        Assert.False(s.ContextAllowsEntries);
        s.RegimeFilter = RegimeFilter.UpTrendOnly;
        Assert.True(s.ContextAllowsEntries);
    }

    [Fact]
    public void The_market_condition_is_changed_live_without_restarting()
    {
        var (s, _, r) = Setup();
        var hourly = Enumerable.Range(0, 31 * 24).Select(i => (T0.AddHours(-31 * 24 + i), 100m - (31 * 24 - i) * 0.05m)).ToList();
        r.SeedContext(hourly);
        r.SetContextFilter(RegimeFilter.RangeOrDownTrend, 0, 100);
        Assert.Equal(RegimeFilter.RangeOrDownTrend, s.RegimeFilter);
        Assert.False(s.ContextAllowsEntries); // it has been rising
        Assert.Contains(r.LogLines, l => l.Contains("trades range or down trend", StringComparison.Ordinal));
        Assert.True(r.IsRunning);
        r.SetContextFilter(RegimeFilter.NoDownTrend, 0, 100);
        Assert.True(s.ContextAllowsEntries);
        Assert.Equal(RegimeFilter.NoDownTrend, r.Info().Parameters["RegimeFilter"]);
    }

    [Fact]
    public void Changing_the_size_with_the_position_open_does_not_break_the_exit()
    {
        var (s, broker, r) = Setup(size: 1.5m);
        s.Next.Enqueue(x => x.DoBuy(2));
        Tick(101);
        Tick(102);
        Assert.Equal(3m, broker.Position!.Volume); // 2 × 1,5
        Assert.Equal(2m, r.VirtualPosition); // the strategy sees its 2

        r.SizeMultiplier = 0.5m;
        s.Next.Enqueue(x => x.DoClose()); // it sells "its" position (2): the 3 real ones are closed
        Tick(103);
        Tick(104);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Equal(0m, r.VirtualPosition);

        s.Next.Enqueue(x => x.DoBuy(2)); // with x0.5 it enters 1
        Tick(105);
        Tick(106);
        Assert.Equal(1m, broker.Position!.Volume);

        // an exit with a fixed quantity (the strategy's) also closes exactly the real amount
        r.SizeMultiplier = 2m;
        s.Next.Enqueue(x => x.DoSell(2));
        Tick(107);
        Tick(108);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
    }

    [Fact]
    public void A_size_that_rounds_to_zero_still_sends_the_minimum()
    {
        // 1 contract x 0.5 does not exist on a future: before, the order "ended up at zero" and the strategy never traded
        var (s, broker, r) = Setup(size: 0.5m);
        Assert.Equal(1m, r.UnitsFor(1)); // it shows "each order: 1 contract (minimum)"
        s.Next.Enqueue(x => x.DoLong(1));
        Tick(101);
        Tick(102);
        Assert.Equal(1m, broker.Position!.Volume);
        Assert.Equal(1m, r.VirtualPosition);
        Assert.True(r.SizeBelowMinimum(1));

        s.Next.Enqueue(x => x.DoClose()); // and the exit closes exactly that contract
        Tick(103);
        Tick(104);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
    }

    [Fact]
    public void With_the_micro_the_fraction_of_the_mini_executes_whole_in_micros()
    {
        // the strategy watches "TEST" (the mini) and executes on "MTEST" (its micro, 10 times smaller): 1 x 0.5 = 5 micros
        var micro = new Instrument { Symbol = "MTEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 0.1m };
        var microData = new InstrumentData(micro, new SimulatedFeed());
        microData.Trades.Add(new Trade(1, T0, 100, 1, TradeSide.Buy));
        var microBroker = new SimBroker(microData, new Portfolio(), new TradingStatistics());
        var (s, _, r) = Setup(size: 0.5m, configure: x =>
        {
            x.ExecInstrument = micro;
            x.ExecRatio = 10;
        }, broker: microBroker);
        var notices = new List<string>();
        r.Executed += (_, text) => notices.Add(text);
        Assert.Equal(5m, r.UnitsFor(1)); // what is shown as "each order: 5 MTEST"
        s.Next.Enqueue(x => x.DoLong(1));
        Tick(101);
        Tick(102);
        Assert.Equal(5m, microBroker.Position!.Volume);
        Assert.Equal(1m, r.VirtualPosition); // the strategy sees its contract
        Assert.Equal("MTEST", r.Ledger.OpenSymbol);
        Assert.True(r.CrossExecution);
        Assert.False(r.SizeBelowMinimum(1));
        // the notice says how many contracts, of which one and that it is the micro (with its mini equivalent)
        var notice = Assert.Single(notices);
        Assert.Contains("bought 5 MTEST", notice);
        Assert.Contains("micro: equals 0.5 TEST", notice);

        microData.Trades.Add(new Trade(2, T0.AddMinutes(10), 110, 1, TradeSide.Buy)); // the micro rises 10 points
        s.Next.Enqueue(x => x.DoClose());
        Tick(110);
        Tick(110);
        Assert.Equal(0m, microBroker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Null(r.Ledger.OpenSymbol);
        // result with the micro's point value: 5 x (exit price - entry) x 0.1
        var trade = Assert.Single(r.Ledger.Trades);
        Assert.Equal(5m * (trade.ExitPrice - trade.EntryPrice) * 0.1m, trade.NetPnl);
    }

    [Fact]
    public void Old_bookkeeping_with_no_contract_uses_the_strategys_point_value()
    {
        var l = new LiveLedger("x", 20);
        l.Apply(new LiveFill(T0, true, 1, 100)); // old fill: no contract and no point value
        l.Apply(new LiveFill(T0.AddMinutes(1), false, 1, 101));
        Assert.Equal(20m, l.Realized);
        l.Apply(new LiveFill(T0.AddMinutes(2), true, 5, 100, "MNQ", 2)); // now in micros
        Assert.Equal("MNQ", l.OpenSymbol);
        Assert.Equal(10m, l.OpenPnL(101)); // 5 × 1 × 2
        l.Apply(new LiveFill(T0.AddMinutes(3), false, 5, 102, "MNQ", 2));
        Assert.Equal(40m, l.Realized); // 20 + 5 × 2 × 2
    }

    [Fact]
    public void A_reversal_sends_the_real_close_plus_the_entry_with_the_size()
    {
        var (s, broker, r) = Setup(size: 2m);
        s.Next.Enqueue(x => x.DoLong(1));
        Tick(101);
        Tick(102);
        Assert.Equal(2m, broker.Position!.Volume);
        s.Next.Enqueue(x => x.DoShort(1)); // the strategy asks for 1 + 1: on the account, close 2 and open 2
        Tick(101);
        Tick(100);
        Assert.Equal(-2m, broker.Position!.Volume);
        Assert.Equal(-1m, r.VirtualPosition);
    }

    [Fact]
    public void While_paused_it_does_not_open_but_allows_closing()
    {
        var (s, broker, r) = Setup();
        r.SetPaused(true);
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(101);
        Tick(102);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Contains(r.LogLines, l => l.Contains("Paused:", StringComparison.Ordinal) && l.Contains("was not sent", StringComparison.Ordinal));

        r.SetPaused(false);
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(103);
        Tick(104);
        Assert.Equal(1m, broker.Position!.Volume);

        // paused with a position: a reversal is trimmed to a close
        r.SetPaused(true);
        s.Next.Enqueue(x => x.DoShort(1));
        Tick(103);
        Tick(102);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.VirtualPosition);
        Assert.True(r.Info().Paused);
    }

    [Fact]
    public void The_maximum_daily_loss_closes_and_halts_until_the_next_session()
    {
        var (s, broker, r) = Setup(configure: x => x.MaxDailyLoss = 5);
        string? hit = null;
        r.LimitHit += (_, msg) => hit = msg;
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        Assert.Equal(1m, broker.Position!.Volume);

        Tick(94); // -6 open: it hits the limit
        Tick(94);
        Assert.NotNull(hit);
        Assert.True(r.DailyLimitActive);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position);
        Assert.True(r.IsRunning);

        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(95);
        Tick(95);
        Assert.Equal(0m, broker.Position!.Volume); // halted

        Tick(96, minutes: 24 * 60); // another session day
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(96);
        Tick(96);
        Assert.False(r.DailyLimitActive);
        Assert.Equal(1m, broker.Position!.Volume);
    }

    [Fact]
    public void The_maximum_drawdown_stops_it_and_closes_its_position()
    {
        var (s, broker, r) = Setup(configure: x => x.MaxDrawdown = 8);
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        Tick(91); // −9
        Assert.False(r.IsRunning);
        Assert.Contains("drawdown", r.StopReason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0m, r.Ledger.Position); // the close landed in its bookkeeping even though it was already stopped
    }

    [Fact]
    public void Stop_and_close_only_closes_its_own_and_stays_booked()
    {
        var (s, broker, r) = Setup();
        s.Next.Enqueue(x => x.DoBuy(2));
        Tick(101);
        Tick(102);
        broker.Market(OrderDirections.Buy, 3, "manual"); // you, by hand, on the same account
        Assert.Equal(5m, broker.Position!.Volume);
        r.Stop(flatten: true);
        Assert.Equal(3m, broker.Position!.Volume); // yours stays
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Equal(0m, LiveStore.LoadLedger(r.Key, 1).Position);
    }

    [Fact]
    public void Closing_never_sends_more_than_what_the_account_holds()
    {
        var (s, broker, r) = Setup();
        s.Next.Enqueue(x => x.DoBuy(2));
        Tick(101);
        Tick(102);
        broker.Market(OrderDirections.Sell, 2, "manual"); // you closed it by hand from the Chart Trader
        Assert.Equal(0m, broker.Position!.Volume);
        r.ClosePosition();
        Assert.Equal(0m, broker.Position!.Volume); // it did not open a short
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Contains(r.LogLines, l => l.Contains("The account did not hold", StringComparison.Ordinal));
    }

    [Fact]
    public void An_inherited_position_the_simulated_account_does_not_hold_is_closed_in_the_bookkeeping()
    {
        // it was left short 2 from last time and Trading Hub closed (a fresh simulated account starts flat)
        var key = $"{typeof(Scripted).FullName}|test:TEST|Simulated account";
        LiveStore.AppendFill(key, new LiveFill(T0.AddDays(-1), false, 2, 100));
        var (s, broker, r) = Setup();
        Assert.Equal(key, r.Key);
        Assert.Equal(0m, r.Ledger.Position);
        Assert.Equal(0m, r.VirtualPosition);
        s.Next.Enqueue(x => x.DoBuy(1)); // and it does not buy 2 extra to "close" something that does not exist
        Tick(101);
        Tick(102);
        Assert.Equal(1m, broker.Position!.Volume);
    }

    [Fact]
    public void An_inherited_position_the_account_holds_keeps_being_managed()
    {
        _data = new InstrumentData(_inst, new SimulatedFeed());
        for (int i = 0; i < 5; i++) _data.Trades.Add(new Trade(_id++, (_time = T0.AddSeconds(i * 30)), 100, 1, TradeSide.Buy));
        var broker = new SimBroker(_data, new Portfolio(), new TradingStatistics());
        broker.Market(OrderDirections.Sell, 2, "from before");
        var key = $"{typeof(Scripted).FullName}|test:TEST|Simulated account";
        LiveStore.AppendFill(key, new LiveFill(T0.AddDays(-1), false, 2, 100));
        var s = new Scripted();
        var r = new LiveStrategyRunner(s, _data, broker, ChartPeriod.M1) { SizeMultiplier = 2 };
        r.Start();
        Assert.Equal(-2m, r.Ledger.Position);
        Assert.Equal(-1m, r.VirtualPosition); // with x2, the strategy sees 1
        s.Next.Enqueue(x => x.DoClose());
        Tick(99);
        Tick(98);
        Assert.Equal(0m, broker.Position!.Volume);
    }

    [Fact]
    public void On_restarting_it_puts_the_stop_back_but_never_duplicates_it()
    {
        // restart (for example with other parameters): the new one protects the position again
        var (s, broker, r) = Setup(configure: x => x.ProtectOnStart = true);
        s.StopLossTicks = 5;
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(100);
        Tick(100);
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        r.Stop(flatten: false);
        Assert.Empty(broker.ActiveOrders);
        var s2 = new Scripted { StopLossTicks = 5 };
        var r2 = new LiveStrategyRunner(s2, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r2.Start();
        Assert.Equal(1m, r2.Ledger.Position);
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);

        // Trading Hub was killed and the stop stayed live at the broker: another one is not placed
        r2.Dispose();
        broker.Stop(OrderDirections.Sell, 1, 95, "Scripted: Stop loss");
        var r3 = new LiveStrategyRunner(new Scripted { StopLossTicks = 5 }, _data, broker, ChartPeriod.M1) { ProtectOnStart = true };
        r3.Start();
        Assert.Single(broker.ActiveOrders, o => o.Type == OrderTypes.Stop);
        Assert.True(r3.LogLines.Any(l => l.Contains("no new stop or target is set", StringComparison.Ordinal)), string.Join(" | ", r3.LogLines));
    }

    /// <summary>Test limiter: it lets things through up to a certain position.</summary>
    private sealed class MaxPosition(decimal max) : ILiveOrderGuard
    {
        public int Blocked;

        public string? Check(LiveStrategyRunner runner, OrderSide side, decimal quantity, decimal positionAfter)
        {
            if (Math.Abs(positionAfter) <= max) return null;
            Blocked++;
            return "it goes over the cap";
        }
    }

    [Fact]
    public void The_limiter_blocks_what_opens_too_much_but_never_an_exit()
    {
        var guard = new MaxPosition(1);
        var (s, broker, r) = Setup(configure: x => x.Guard = guard);
        s.Next.Enqueue(x => x.DoBuy(2)); // it would end up at 2: blocked
        Tick(101);
        Tick(102);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(1, guard.Blocked);
        Assert.Contains(r.LogLines, l => l.Contains("Risk limiter", StringComparison.Ordinal));
        s.Next.Enqueue(x => x.DoBuy(1)); // within the cap
        Tick(103);
        Tick(104);
        Assert.Equal(1m, broker.Position!.Volume);
        guard = new MaxPosition(0); // now it does not allow opening anything… but exiting, yes
        r.Guard = guard;
        s.Next.Enqueue(x => x.DoClose());
        Tick(105);
        Tick(106);
        Assert.Equal(0m, broker.Position!.Volume);
        Assert.Equal(0, guard.Blocked);
    }

    [Fact]
    public void A_seeded_position_keeps_being_managed_and_its_exit_stays_tied()
    {
        _data = new InstrumentData(_inst, new SimulatedFeed());
        for (int i = 0; i < 5; i++) _data.Trades.Add(new Trade(_id++, (_time = T0.AddSeconds(i * 30)), 100, 1, TradeSide.Buy));
        var broker = new SimBroker(_data, new Portfolio(), new TradingStatistics()) { Name = "Visual" };
        broker.Market(OrderDirections.Buy, 1, "history");
        var s = new Scripted();
        var r = new LiveStrategyRunner(s, _data, broker, ChartPeriod.M1) { Persist = false };
        r.SeedPosition(new LiveFill(T0.AddMinutes(-30), true, 1, 95));
        r.Start();
        Assert.Equal(1m, r.VirtualPosition);
        s.Next.Enqueue(x => x.DoClose());
        Tick(101);
        Tick(102);
        Assert.Equal(0m, broker.Position!.Volume);
        var t = Assert.Single(r.Ledger.Trades);
        Assert.Equal(T0.AddMinutes(-30), t.EntryTime); // the exit is tied to the history's entry
        Assert.Equal(95m, t.EntryPrice);
        Assert.False(File.Exists(LiveStore.LedgerFile(r.Key))); // visual: it is not stored
    }

    [Fact]
    public void Starting_from_scratch_only_with_no_position()
    {
        var (s, broker, r) = Setup();
        s.Next.Enqueue(x => x.DoBuy(1));
        Tick(101);
        Tick(102);
        Assert.False(r.ResetLedger()); // with a position: no
        s.Next.Enqueue(x => x.DoClose());
        Tick(103);
        Tick(104);
        Assert.Single(r.Ledger.Trades);
        Assert.True(r.ResetLedger());
        Assert.Empty(r.Ledger.Trades);
        Assert.Empty(LiveStore.LoadLedger(r.Key, 1).Fills);
    }

    [Fact]
    public void The_excursions_come_from_the_prices_between_entry_and_exit()
    {
        var t = new ClosedTrade(1, true, T0, 100, T0.AddMinutes(5), 104, 2, 8, 0, 0, 0, "", "", 0);
        var (mae, mfe) = TradeExcursion.Compute(t, new decimal[] { 100, 97, 103, 106, 104 }, 5);
        Assert.Equal(-30m, mae); // (97−100) × 2 × 5
        Assert.Equal(60m, mfe); // (106−100) × 2 × 5
        var shortTrade = t with { IsLong = false, ExitPrice = 98 };
        var (smae, smfe) = TradeExcursion.Compute(shortTrade, new decimal[] { 100, 101, 95, 98 }, 1);
        Assert.Equal(-2m, smae);
        Assert.Equal(10m, smfe);
    }
}
