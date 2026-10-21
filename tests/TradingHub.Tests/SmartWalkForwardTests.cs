using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>The validation engine: sessions, compressed ticks, Bayesian search, plateau, PBO and CPCV.</summary>
public class SmartWalkForwardTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };

    /// <summary>Records every new session the engine reports.</summary>
    private sealed class SessionSpy : Strategy
    {
        public List<DateOnly> Sessions { get; } = new();

        protected override void OnSessionStart(DateOnly session) => Sessions.Add(session);

        protected override void OnBar(Bar bar)
        {
        }
    }

    [Fact]
    public void The_session_changes_on_the_same_ticks_even_across_a_daylight_saving_change()
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var session = new SessionSettings { TimeZone = tz, Start = TimeSpan.FromHours(18) };
        foreach (var start in new[] { new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc) })
        {
            var list = new List<Trade>();
            for (int i = 0; i < 6 * 24 * 12; i++) list.Add(new Trade(i + 1, start.AddMinutes(5 * i), 100 + i % 9, 1, TradeSide.Buy));
            var data = TickData.FromTrades(Inst, list);

            // what it has to give: the session date of each tick, as before (converting time zones on every one)
            var expected = new List<DateOnly>();
            DateOnly last = default;
            foreach (var t in list)
            {
                var d = session.SessionDate(t.Time);
                if (last != default && d != last) expected.Add(d);
                last = d;
            }

            var spy = new SessionSpy();
            var settings = new BacktestSettings
            {
                FeedId = "test", Symbol = "TEST", FromUtc = start, ToUtc = start.AddDays(6), Period = "M15", TimeZoneId = "America/New_York",
                SessionStart = TimeSpan.FromHours(18), CommissionPercent = 0, SlippageTicks = 0,
            };
            var r = new BacktestEngine(spy, data, settings).Run();
            Assert.True(r.Ok, r.Error);
            Assert.Equal(expected, spy.Sessions);
        }
    }

    /// <summary>
    /// Minutes with bursts: several trades in a row at the same price and side within the same second (as on a real
    /// future), a gentle trend with back-and-forth and some noise.
    /// </summary>
    private static TickData Bursty(int days, int seed = 3)
    {
        var rnd = new Random(seed);
        var list = new List<Trade>();
        var t0 = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
        double price = 1000;
        long id = 1;
        for (int m = 0; m < days * 24 * 60; m++)
        {
            price += 0.05 + 6 * Math.Sin(m / 90.0) * 0.08 + (rnd.NextDouble() - 0.5) * 2;
            decimal p = Math.Round((decimal)price);
            var side = rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell;
            int burst = 1 + rnd.Next(5);
            for (int k = 0; k < burst; k++) list.Add(new Trade(id++, t0.AddMinutes(m).AddMilliseconds(k * 40), p, 1 + rnd.Next(3), side));
            list.Add(new Trade(id++, t0.AddMinutes(m).AddSeconds(20), p + 1, 1, TradeSide.Buy));
        }

        return TickData.FromTrades(Inst, list);
    }

    private static BacktestSettings Days(TickData data) => new()
    {
        FeedId = "test", Symbol = "TEST", FromUtc = data.FirstTime.Date, ToUtc = data.LastTime.Date.AddDays(1), Period = "M15",
        CommissionPercent = 0, CommissionPerUnit = 0.5m, SlippageTicks = 1, InitialCapital = 100_000,
    };

    /// <summary>Buys when it breaks the high of the last N candles and exits after "Hold" candles (with a stop).</summary>
    private sealed class Breakout : Strategy
    {
        [Parameter, Optimize(4, 40, 4)]
        public int Channel { get; set; } = 12;

        [Parameter, Optimize(2, 12, 2)]
        public int Hold { get; set; } = 4;

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
            else if (IsLong && CurrentBar - _entry >= Hold) ClosePosition();
        }
    }

    /// <summary>The same but looking at every trade (it cannot be compressed).</summary>
    private sealed class TickWatcher : Strategy
    {
        protected override void OnTick(Trade trade)
        {
        }
    }

    [Fact]
    public void Merging_the_repeated_ticks_changes_neither_the_candles_nor_the_trades()
    {
        var data = Bursty(4);
        var packed = data.Compress();
        Assert.True(packed.Compressed && packed.Count < data.Count * 0.6, $"{packed.Count} of {data.Count}");
        var settings = Days(data);
        var a = new BacktestEngine(new Breakout(), data, settings).Run();
        var b = new BacktestEngine(new Breakout(), packed, settings).Run();
        Assert.True(a.Ok && b.Ok, a.Error ?? b.Error);
        Assert.True(a.Trades.Count > 10, $"{a.Trades.Count} trades");
        Assert.Equal(a.Trades.Count, b.Trades.Count);
        for (int i = 0; i < a.Trades.Count; i++)
        {
            Assert.Equal(a.Trades[i].EntryPrice, b.Trades[i].EntryPrice);
            Assert.Equal(a.Trades[i].ExitPrice, b.Trades[i].ExitPrice);
            Assert.Equal(a.Trades[i].EntryTime.Ticks / TimeSpan.TicksPerSecond, b.Trades[i].EntryTime.Ticks / TimeSpan.TicksPerSecond);
        }

        Assert.Equal(a.Metrics.NetProfit, b.Metrics.NetProfit);
        Assert.True(Optimizer.CanCompress(typeof(Breakout), "M15"));
        Assert.False(Optimizer.CanCompress(typeof(Breakout), "Tick500"));
        Assert.False(Optimizer.CanCompress(typeof(TickWatcher), "M15"));
    }

    [Fact]
    public void The_bayesian_search_finds_the_good_part_with_far_fewer_trials_than_the_grid()
    {
        var space = new ParameterSpace(30, 30);
        double F(int[] x) => 100 * Math.Exp(-(Math.Pow(x[0] - 21, 2) + Math.Pow(x[1] - 8, 2)) / 40.0);
        int calls = 0;
        var found = TpeSearch.Run(space, pts =>
        {
            calls += pts.Count;
            return pts.Select(F).ToArray();
        }, budget: 80, seed: 5, batch: 8);
        Assert.True(calls <= 80, $"{calls} tests");
        double best = found.Max(f => f.Score);
        Assert.True(best >= 90, $"best {best:0.0} of 100 with {calls} trials out of 900");
    }

    [Fact]
    public void The_plateau_beats_the_isolated_peak()
    {
        var space = new ParameterSpace(9, 9);
        var rows = space.All().Select(x =>
        {
            double f = -5;
            if (x[0] == 1 && x[1] == 1) f = 10; // a lone peak: the neighbours lose
            if (Math.Abs(x[0] - 6) <= 1 && Math.Abs(x[1] - 6) <= 1) f = 6; // a whole zone that wins
            return (x, f);
        }).ToList();
        int pick = RobustSelection.Select(rows);
        Assert.Equal(new[] { 6, 6 }, rows[pick].x);
    }

    [Fact]
    public void PBO_is_high_with_pure_noise_and_low_with_a_real_edge()
    {
        var rnd = new Random(11);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Sin(2 * Math.PI * rnd.NextDouble());
        // with pure noise the PBO is 0.5 on average (on a single history it can land far off): several are averaged
        var noise = Enumerable.Range(0, 10).Select(_ => Overfitting.Pbo(Enumerable.Range(0, 24).Select(_ => Enumerable.Range(0, 500).Select(_ => Gauss() * 100).ToArray()).ToList())!.Value).ToList();
        var edge = Enumerable.Range(0, 24).Select(i => Enumerable.Range(0, 500).Select(_ => 6.0 * i + Gauss() * 100).ToArray()).ToList();
        var e = Overfitting.Pbo(edge)!.Value;
        Assert.All(noise, n => Assert.Equal(252, n.Splits));
        double pbo = noise.Average(n => n.Pbo), loss = noise.Average(n => n.Loss);
        Assert.InRange(pbo, 0.3, 0.7);
        Assert.True(e.Pbo < 0.15 && e.Loss < 0.05, $"with an edge: PBO {e.Pbo:0.00}, loss {e.Loss:0.00}");
        Assert.True(loss > e.Loss + 0.2, $"noise {loss:0.00} vs edge {e.Loss:0.00}");
    }

    [Fact]
    public void The_CPCV_builds_several_complete_out_of_sample_paths()
    {
        var rnd = new Random(2);
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Sin(2 * Math.PI * rnd.NextDouble());
        var space = new ParameterSpace(6);
        var index = space.All().ToList();
        var daily = index.Select(x => Enumerable.Range(0, 400).Select(_ => 8.0 * x[0] + Gauss() * 60).ToArray()).ToList();
        var perDay = index.Select(_ => Enumerable.Repeat(1, 400).ToArray()).ToList();
        var paths = Overfitting.Cpcv(index, daily, perDay, 5, 100_000, false, groups: 8, embargo: 1);
        Assert.Equal(7, paths.Count);
        Assert.All(paths, p => Assert.Equal(400, p.Days));
        Assert.True(paths.Count(p => p.Net > 0) >= 6, string.Join(", ", paths.Select(p => p.Net)));
    }

    [Fact]
    public void The_walk_forward_runs_each_combination_just_once_and_measures_the_overfitting()
    {
        var data = Bursty(45, 7);
        var settings = Days(data);
        var ranges = new List<ParameterRange>
        {
            new() { Name = nameof(Breakout.Channel), Min = 4, Max = 20, Step = 4, IsInteger = true },
            new() { Name = nameof(Breakout.Hold), Min = 2, Max = 6, Step = 2, IsInteger = true },
        };
        var wf = Optimizer.WalkForward(() => new Breakout(), new(), ranges, data, settings, FitnessMetric.NetProfit, 12, 6, false, 3, null, CancellationToken.None);
        Assert.Equal("grid", wf.Search);
        Assert.Equal(15, wf.SpaceSize);
        Assert.Equal(15, wf.Evaluated); // 15 combinations, each one once (not one per window)
        Assert.True(wf.Compression < 0.7, $"compression {wf.Compression}");
        Assert.True(wf.Windows.Count >= 4);
        // every out-of-sample trade entered inside an out-of-sample window
        Assert.All(wf.OutSampleTrades, t => Assert.Contains(wf.Windows, w => t.EntryTime >= w.OutSampleFrom && t.EntryTime < w.OutSampleTo));
        Assert.NotNull(wf.Pbo);
        Assert.True(wf.PboSplits > 0 && wf.CpcvPaths.Count >= 3, $"PBO {wf.PboSplits} partitions, {wf.CpcvPaths.Count} paths");

        // with many combinations it searches with the Bayesian one and runs far fewer than the grid
        var big = new List<ParameterRange>
        {
            new() { Name = nameof(Breakout.Channel), Min = 2, Max = 40, Step = 1, IsInteger = true },
            new() { Name = nameof(Breakout.Hold), Min = 1, Max = 20, Step = 1, IsInteger = true },
        };
        var tpe = Optimizer.WalkForward(() => new Breakout(), new(), big, data, settings, FitnessMetric.NetProfit, 12, 6, false, 3, null, CancellationToken.None,
            budget: 16);
        Assert.Equal("bayesian", tpe.Search);
        Assert.True(tpe.SpaceSize == 780 && tpe.Evaluated < 780 / 3, $"it ran {tpe.Evaluated} of {tpe.SpaceSize}");
        Assert.True(tpe.Windows.Count >= 4);
    }
}
