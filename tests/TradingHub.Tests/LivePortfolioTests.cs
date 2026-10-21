using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

/// <summary>Live portfolio: bookkeeping per strategy, sizing rules and multiplier.</summary>
[Collection("LiveStore")] // they change the portfolio folder (static): not in parallel
public class LivePortfolioTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_own_bookkeeping_closes_partials_and_reversals()
    {
        var l = new LiveLedger("k", 1);
        l.Apply(new LiveFill(T0, true, 2, 100));
        l.Apply(new LiveFill(T0.AddMinutes(1), false, 1, 110)); // closes half: +10
        Assert.Equal(1m, l.Position);
        Assert.Equal(10m, l.Realized);
        Assert.Equal(100m, l.AveragePrice);
        l.Apply(new LiveFill(T0.AddMinutes(2), false, 3, 105)); // closes 1 (+5) and is left short 2 at 105
        Assert.Equal(-2m, l.Position);
        Assert.Equal(15m, l.Realized);
        Assert.Equal(105m, l.AveragePrice);
        Assert.Equal(2, l.Trades.Count);
        Assert.Equal(-10m, l.OpenPnL(110)); // short 2 from 105 with the price at 110
    }

    private static LiveStats Stat(string key, decimal size, params decimal[] pnls)
    {
        var l = new LiveLedger(key, 1);
        for (int i = 0; i < pnls.Length; i++)
        {
            l.Apply(new LiveFill(T0.AddMinutes(i * 2), true, 1, 100));
            l.Apply(new LiveFill(T0.AddMinutes(i * 2 + 1), false, 1, 100 + pnls[i]));
        }

        return LiveStore.StatsOf(new LiveRunnerInfo { Key = key, Strategy = key, Running = true, Size = size }, l, 10_000);
    }

    [Fact]
    public void The_rules_raise_the_one_performing_and_pause_the_one_degrading()
    {
        var good = Stat("good", 1, 30, -10, 25, 20, -10, 30, 15, -5, 40, 20, 10, -10);
        var bad = Stat("bad", 1, 20, 30, 10, -40, -30, -20, -35, -25, -30, -20, -15, -30, -25, -20, -10, -30);
        var young = Stat("new", 1, 50, 60);
        var p = LiveSizing.SuggestByRules(new[] { good, bad, young });
        Assert.Equal(1.25m, p.Items.Single(i => i.Key == "good").ProposedSize);
        Assert.Equal(0m, p.Items.Single(i => i.Key == "bad").ProposedSize);
        Assert.DoesNotContain(p.Items, i => i.Key == "new"); // little sample: it is not touched
        Assert.All(p.Items, i => Assert.Equal("pending", i.Status));
    }

    /// <summary>Buys 2 on the first closed candle.</summary>
    private sealed class BuyTwo : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            if (IsFlat && ActiveOrders.Count == 0) Buy(2);
        }
    }

    [Fact]
    public void The_portfolio_size_multiplies_the_orders_and_is_recorded()
    {
        var inst = new Instrument { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m };
        LiveStore.Root = Path.Combine(Path.GetTempPath(), "tradinghub_live_" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new InstrumentData(inst, new SimulatedFeed());
            long id = 1;
            for (int i = 0; i < 5; i++) data.Trades.Add(new Trade(id++, T0.AddSeconds(i * 30), 100 + i, 1, TradeSide.Buy));
            var broker = new SimBroker(data, new Portfolio(), new TradingStatistics());
            var runner = new LiveStrategyRunner(new BuyTwo(), data, broker, ChartPeriod.M1) { SizeMultiplier = 1.5m };
            runner.Start();

            // a new trade arrives closing the candle: the strategy asks for 2 and with x1.5 three are sent
            int start = data.Trades.Count;
            data.Trades.Add(new Trade(id++, T0.AddMinutes(3), 106, 1, TradeSide.Buy));
            var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ((Action<int, int>?)field!.GetValue(data))?.Invoke(start, 1);

            Assert.Equal(3m, broker.Position!.Volume);
            Assert.Equal(3m, runner.Ledger.Position);
            var saved = LiveStore.LoadLedger(runner.Key, 1);
            Assert.Equal(3m, saved.Position); // the fills were left on disk

            runner.SizeMultiplier = 0; // paused: it sends no more
            runner.Stop(flatten: false);
        }
        finally
        {
            try { Directory.Delete(LiveStore.Root, true); } catch { /* cleanup */ }
        }
    }
}
