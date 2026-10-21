using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>The candle period as one more parameter of the optimizer and the walk-forward.</summary>
public class OptimizationPeriodTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };

    /// <summary>Days of trades with bursts in the same second, a slow wave and noise.</summary>
    private static TickData Data(int days, int seed = 5)
    {
        var rnd = new Random(seed);
        var list = new List<Trade>();
        var t0 = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        double price = 1000;
        long id = 1;
        for (int m = 0; m < days * 24 * 60; m++)
        {
            price += 4 * Math.Sin(m / 120.0) * 0.1 + (rnd.NextDouble() - 0.5) * 2;
            decimal p = Math.Round((decimal)price);
            var side = rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell;
            int burst = 1 + rnd.Next(4);
            for (int k = 0; k < burst; k++) list.Add(new Trade(id++, t0.AddMinutes(m).AddMilliseconds(k * 30), p, 1, side));
        }

        return TickData.FromTrades(Inst, list);
    }

    private static BacktestSettings Settings(TickData data) => new()
    {
        FeedId = "test", Symbol = "TEST", FromUtc = data.FirstTime.Date, ToUtc = data.LastTime.Date.AddDays(1), Period = "M15",
        CommissionPercent = 0, SlippageTicks = 0, InitialCapital = 100_000,
    };

    /// <summary>Buys on breaking the high of N candles and exits after 3 candles.</summary>
    private sealed class Breakout : Strategy
    {
        [Parameter, Optimize(6, 12, 6)]
        public int Channel { get; set; } = 6;

        private int _entry = -1000;

        protected override void OnBar(Bar bar)
        {
            if (Bars.Count <= Channel) return;
            decimal max = 0;
            for (int i = 1; i <= Channel; i++) max = Math.Max(max, Bars[i].High);
            if (IsFlat && bar.Close > max)
            {
                Buy(1);
                _entry = CurrentBar;
            }
            else if (IsLong && CurrentBar - _entry >= 3) ClosePosition();
        }
    }

    private static List<ParameterRange> Ranges(params string[] periods) =>
        new() { new ParameterRange { Name = nameof(Breakout.Channel), Label = "Channel", Enabled = true, Min = 6, Max = 12, Step = 6, IsInteger = true }, ParameterRange.Periods(periods) };

    [Fact]
    public void The_periods_end_up_valid_sorted_and_without_repeats()
    {
        var r = ParameterRange.Periods(new[] { "H1", "m5", "M15", "nonsense", "M5", "D1" });
        Assert.Equal(new[] { "M5", "M15", "H1", "Daily" }, r.Choices);
        Assert.Equal(4, r.Count);
        Assert.Equal(1, ParameterRange.Periods(new[] { "H1" }, enabled: false).Count);
        Assert.Equal(2d, r.Numeric("H1"));
        Assert.Null(r.Numeric("H4"));
    }

    [Fact]
    public void The_optimizer_tests_every_combination_on_every_period()
    {
        var data = Data(3);
        var res = Optimizer.Optimize(() => new Breakout(), new(), Ranges("M5", "M15", "H1"), data, Settings(data), FitnessMetric.NetProfit, 0, null, CancellationToken.None);
        Assert.Equal(6, res.Combinations);
        Assert.Equal(6, res.Rows.Count);
        Assert.Equal(new[] { "H1", "M15", "M5" }, res.Rows.Select(r => (string)r.Parameters[ParameterRange.PeriodName]!).Distinct().OrderBy(p => p));

        // the same channel on smaller candles gives more trades: each combination ran on its own period
        int Trades(string period) => res.Rows.First(r => (string)r.Parameters[ParameterRange.PeriodName]! == period && Equals(r.Parameters[nameof(Breakout.Channel)], 6)).Metrics.TotalTrades;
        Assert.True(Trades("M5") > Trades("M15") && Trades("M15") > Trades("H1"), $"{Trades("M5")} {Trades("M15")} {Trades("H1")}");
        Assert.Equal(Trades("M15"), new BacktestEngine(new Breakout(), data, Settings(data)).Run().Metrics.TotalTrades);
    }

    [Fact]
    public void Without_the_period_ticked_the_backtests_one_is_used()
    {
        var data = Data(2);
        var ranges = Ranges("M5", "H1");
        ranges[1].Enabled = false;
        var res = Optimizer.Optimize(() => new Breakout(), new(), ranges, data, Settings(data), FitnessMetric.NetProfit, 0, null, CancellationToken.None);
        Assert.Equal(2, res.Combinations);
        Assert.All(res.Rows, r => Assert.False(r.Parameters.ContainsKey(ParameterRange.PeriodName)));
    }

    [Fact]
    public void The_walk_forward_picks_the_period_in_each_window()
    {
        var data = Data(8);
        var res = Optimizer.WalkForward(() => new Breakout(), new(), Ranges("M15", "H1"), data, Settings(data), FitnessMetric.NetProfit, 3, 1, false, 0, null, CancellationToken.None,
            search: SearchMode.Grid);
        Assert.Equal(4, res.Evaluated);
        Assert.NotEmpty(res.Windows);
        Assert.All(res.Windows, w => Assert.Contains((string)w.BestParameters[ParameterRange.PeriodName]!, new[] { "M15", "H1" }));
        Assert.True(res.Compression < 1, "with time periods the repeated ticks are merged");
    }

    [Fact]
    public void With_a_tick_period_the_ticks_are_not_merged()
    {
        var data = Data(6);
        var res = Optimizer.WalkForward(() => new Breakout(), new(), Ranges("M15", "Tick500"), data, Settings(data), FitnessMetric.NetProfit, 3, 1, false, 0, null, CancellationToken.None,
            search: SearchMode.Grid);
        Assert.Equal(1, res.Compression);
        Assert.Contains(res.Windows.SelectMany(w => w.BestParameters.Values).OfType<string>(), p => p is "M15" or "Tick500");
    }
}
