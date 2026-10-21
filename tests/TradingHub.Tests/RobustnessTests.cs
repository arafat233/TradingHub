using System.Text.Json;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>The rules against overoptimization (the validation bar).</summary>
public class RobustnessTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<ClosedTrade> Trades(params decimal[] pnls) =>
        pnls.Select((p, i) => new ClosedTrade(i + 1, true, T0.AddHours(i), 100, T0.AddHours(i + 0.5), 100 + p, 1, p, 0, 0, 0, "", "", 1)).ToList();

    /// <summary>Results alternating wins and losses with a given edge (deterministic).</summary>
    private static List<ClosedTrade> Edge(int n, decimal win, decimal loss, int winsEvery = 2) =>
        Trades(Enumerable.Range(0, n).Select(i => i % winsEvery == 0 ? win : -loss).ToArray());

    private static WalkForwardResult Wf(List<ClosedTrade> oos, double efficiency, double windowsPct, int windows, IEnumerable<Dictionary<string, object?>>? best = null, List<ParameterRange>? ranges = null)
    {
        var m = PerformanceMetrics.Compute(oos, new List<EquityPoint>(), 10_000, true, 0, 0);
        var list = best?.ToList() ?? Enumerable.Range(0, windows).Select(_ => new Dictionary<string, object?> { ["Fast"] = 10 }).ToList();
        return new WalkForwardResult
        {
            OutSampleTrades = oos,
            OutSampleMetrics = m,
            WalkForwardEfficiency = efficiency,
            ProfitableWindowsPercent = windowsPct,
            Ranges = ranges ?? new List<ParameterRange> { new() { Name = "Fast", Min = 5, Max = 30, Step = 5 } },
            Windows = list.Select((p, i) => new WalkForwardWindow { Number = i + 1, BestParameters = p, OutSampleFrom = T0.AddDays(i * 10), OutSampleTo = T0.AddDays(i * 10 + 10) }).ToList(),
        };
    }

    [Fact]
    public void The_normal_and_its_inverse_are_consistent()
    {
        Assert.Equal(0.5, Robustness.NormalCdf(0), 6);
        Assert.Equal(0.975, Robustness.NormalCdf(1.959964), 4);
        foreach (var p in new[] { 0.001, 0.02, 0.3, 0.5, 0.8, 0.99, 0.9999 })
            Assert.Equal(p, Robustness.NormalCdf(Robustness.NormalInv(p)), 5);
    }

    [Fact]
    public void The_expected_maximum_grows_with_the_number_of_trials()
    {
        Assert.Equal(0, Robustness.ExpectedMaxZ(1));
        double prev = 0;
        foreach (var n in new[] { 2, 10, 100, 1000 })
        {
            double z = Robustness.ExpectedMaxZ(n);
            Assert.True(z > prev, $"n={n}");
            prev = z;
        }

        // known values: ~1.54 with 10 trials, ~2.53 with 100
        Assert.InRange(Robustness.ExpectedMaxZ(10), 1.45, 1.65);
        Assert.InRange(Robustness.ExpectedMaxZ(100), 2.4, 2.65);
    }

    [Fact]
    public void The_deflated_Sharpe_drops_the_more_things_are_tried()
    {
        var trades = Edge(200, 150, 100); // PF 1.5, half winners
        var (sr, skew, kurt, n) = Robustness.TradeSharpe(trades);
        Assert.True(sr > 0);
        double one = Robustness.DeflatedSharpe(sr, n, skew, kurt, 1, 0);
        double many = Robustness.DeflatedSharpe(sr, n, skew, kurt, 500, 0.01);
        Assert.True(one > 0.95, $"with a single trial {one}");
        Assert.True(many < one, $"{many} < {one}");
        // with many trials and little sample, a small edge cannot be told apart from chance
        var small = Edge(30, 110, 100);
        var s2 = Robustness.TradeSharpe(small);
        Assert.True(Robustness.DeflatedSharpe(s2.Sharpe, s2.Count, s2.Skew, s2.Kurtosis, 200, 0) < 0.5);
    }

    [Fact]
    public void The_luck_reasoning_is_explained_in_money()
    {
        // small edge with many trials: it says how much it makes per trade, how much it varies and how much luck would give
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(40, 110, 100), 0.7, 80, 8), Trials = 26, MinTrades = 30 });
        var luck = gate.Checks.Single(c => c.Name == "Deflated Sharpe");
        Assert.NotEqual(GateStatus.Pass, luck.Status);
        Assert.StartsWith(ValidationGate.LuckFailPrefix, luck.Text);
        Assert.Contains("per trade", luck.Text);
        Assert.Contains("26 strategies with no edge at all", luck.Text);
        Assert.True(ValidationGate.IsLuckLine(luck.Text));
        // the checks stored earlier with the old text are still recognized
        Assert.True(ValidationGate.IsLuckLine("The probability that it is not luck, discounting the 4 configurations that were tested: 40%"));
    }

    [Fact]
    public void A_solid_strategy_passes_every_check()
    {
        var oos = Edge(120, 150, 100);
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(oos, 0.7, 80, 8), Trials = 1, MinTrades = 30 });
        Assert.Equal(GateStatus.Pass, gate.Status);
        Assert.All(gate.Checks, c => Assert.Equal(GateStatus.Pass, c.Status));
        Assert.Equal("pass", gate.StatusText);
    }

    [Fact]
    public void Overoptimized_or_with_little_sample_does_not_pass()
    {
        // efficiency 0.2: the optimized result does not repeat out of sample
        var overfit = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(120, 150, 100), 0.2, 80, 8) });
        Assert.Equal(GateStatus.Fail, overfit.Status);
        Assert.Contains(overfit.Checks, c => c.Name == "Efficiency" && c.Status == GateStatus.Fail);

        // 12 trades: not enough
        var tiny = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(12, 150, 100), 0.8, 80, 6) });
        Assert.Equal(GateStatus.Fail, tiny.Status);
        Assert.Contains(tiny.Checks, c => c.Name == "Sample" && c.Status == GateStatus.Fail);

        // it loses out of sample
        var losing = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(100, 90, 100), 0.8, 30, 6) });
        Assert.Equal(GateStatus.Fail, losing.Status);
    }

    [Fact]
    public void If_it_wins_with_few_trades_it_is_short_on_sample_not_on_edge()
    {
        // 8 trades, PF 3, two windows: not enough to conclude, but it is not a loser
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(8, 300, 100), 0.8, 50, 2), Trials = 4, MinTrades = 30 });
        Assert.Equal(GateStatus.Fail, gate.Status);
        Assert.True(gate.NeedsMoreSample);
        Assert.Equal("needs more sample", gate.StatusText);
        Assert.True(gate.SampleFactor >= 30 / 8.0, $"factor {gate.SampleFactor}");
    }

    [Fact]
    public void If_it_loses_or_does_not_trade_it_is_not_lack_of_sample()
    {
        var losing = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(8, 90, 100), 0.8, 50, 2), Trials = 4 });
        Assert.False(losing.NeedsMoreSample);
        Assert.Equal("does not pass", losing.StatusText);

        var none = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(new List<ClosedTrade>(), 0, 0, 4), Trials = 4 });
        Assert.False(none.NeedsMoreSample);
        Assert.Contains(none.Checks, c => c.Name == "Sample" && c.Text.StartsWith("It did not trade", StringComparison.Ordinal));

        // with few trades but genuinely overfitted and losing money, it does not pass either
        var overfit = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(12, 95, 100), 0.1, 20, 4), Trials = 4 });
        Assert.False(overfit.NeedsMoreSample);
    }

    [Fact]
    public void A_low_deflated_sharpe_with_a_clear_edge_asks_for_more_sample()
    {
        // 40 trades with PF 2 and 100 things already tried: it could still be luck, but with the same edge per
        // trade it would pass with about 3 times more trades
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(40, 200, 100), 0.8, 80, 8), Trials = 100, MinTrades = 30 });
        Assert.Equal(GateStatus.Fail, gate.Status);
        Assert.Equal(GateStatus.Fail, gate.Checks.First(c => c.Name == "Deflated Sharpe").Status);
        Assert.True(gate.NeedsMoreSample);
        Assert.InRange(gate.SampleFactor, 1.5, ValidationGate.MaxSampleFactor);

        // doubtful because of the deflated Sharpe (between 50 and 90%): it is short on sample too
        var doubtful = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(40, 200, 100), 0.8, 80, 8), Trials = 20, MinTrades = 30 });
        Assert.Equal(GateStatus.Warn, doubtful.Status);
        Assert.True(doubtful.NeedsMoreSample);
        Assert.Equal("doubtful", doubtful.StatusText);
    }

    [Fact]
    public void A_small_edge_that_cannot_be_told_apart_from_chance_is_not_lack_of_sample()
    {
        // PF 1.05 with 200 trials: not even with 8 times more trades would it be told apart from chance
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(100, 105, 100), 0.8, 80, 8), Trials = 200, TrialVariance = 0.02, MinTrades = 30 });
        Assert.Equal(GateStatus.Fail, gate.Status);
        Assert.False(gate.NeedsMoreSample);
        Assert.Equal("does not pass", gate.StatusText);
    }

    [Fact]
    public void An_edge_that_fades_over_time_does_not_pass()
    {
        // 90 trades that start winning and end losing (on average it wins: no other rule stops it)
        var dying = Trades(Enumerable.Range(0, 90).Select(i => 240m - 4m * i + (i % 2 == 0 ? 60 : -60)).ToArray());
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(dying, 0.8, 80, 8), Trials = 1, MinTrades = 30 });
        var decay = gate.Checks.Single(c => c.Name == "Edge decay");
        Assert.Equal(GateStatus.Fail, decay.Status);
        Assert.Contains("The edge is fading", decay.Text);
        Assert.True(gate.DecayEarly > 0 && gate.DecayLate < 0 && gate.DecayT < -2);

        // the same edge from end to end: it holds up
        var steady = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(90, 150, 100), 0.8, 80, 8), Trials = 1, MinTrades = 30 });
        Assert.Equal(GateStatus.Pass, steady.Checks.Single(c => c.Name == "Edge decay").Status);
    }

    [Fact]
    public void How_it_does_in_a_range_in_a_trend_and_with_volatility_is_measured()
    {
        // it wins when it enters in a trend and loses when it enters in a range
        var trades = Enumerable.Range(0, 60).Select(i =>
        {
            bool trend = i % 2 == 0;
            decimal pnl = trend ? (i % 6 == 0 ? -80 : 200) : (i % 6 == 1 ? 60 : -120);
            return new ClosedTrade(i + 1, true, T0.AddHours(i), 100, T0.AddHours(i + 0.5), 100 + pnl, 1, pnl, 0, 0, 0, "", "", 1)
            {
                EntryTrend = trend ? 0.5 : 0.08,
                EntryVolatility = 0.2 + i % 10 * 0.05,
            };
        }).ToList();
        var regimes = Robustness.Regimes(trades);
        Assert.Equal(30, regimes.Single(r => r.Name == "trend").Trades);
        Assert.True(regimes.Single(r => r.Name == "trend").Net > 0);
        Assert.True(regimes.Single(r => r.Name == "range").Net < 0);
        Assert.Contains(regimes, r => r.Name == "high volatility");

        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(trades, 0.8, 80, 8), Trials = 1, MinTrades = 30 });
        var check = gate.Checks.Single(c => c.Name == "Regimes");
        Assert.Equal(GateStatus.Warn, check.Status);
        Assert.Contains("it loses in range", check.Text);
        Assert.Contains("wins in trend", check.Text);

        // the old runs (with no regime on each trade) do not have this check
        var old = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(60, 150, 100), 0.8, 80, 8) });
        Assert.DoesNotContain(old.Checks, c => c.Name == "Regimes");
    }

    [Fact]
    public void The_entry_regime_comes_from_the_previous_candles()
    {
        // a clean 24-hour rise gives an efficient trend; a back-and-forth, a range
        var up = new MarketContextTracker();
        var chop = new MarketContextTracker();
        for (int h = 0; h < 30; h++)
        {
            up.Add(T0.AddHours(h), 100 + h);
            chop.Add(T0.AddHours(h), h % 2 == 0 ? 100 : 102);
        }

        Assert.True(up.Trend > Robustness.TrendingTrend, $"up {up.Trend}");
        Assert.Equal(MarketRegime.Trend, up.Regime);
        Assert.True(chop.Trend < Robustness.RangeTrend, $"chop {chop.Trend}");
        Assert.Equal(MarketRegime.Range, chop.Regime);
        Assert.True(chop.Volatility > up.Volatility);
    }

    [Fact]
    public void Parameters_that_jump_between_windows_raise_a_warning()
    {
        var jumping = new[] { 5, 30, 5, 30, 5, 30 }.Select(v => new Dictionary<string, object?> { ["Fast"] = v });
        var stable = new[] { 10, 10, 15, 10, 15, 10 }.Select(v => new Dictionary<string, object?> { ["Fast"] = v });
        var j = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(120, 150, 100), 0.7, 80, 6, jumping) });
        var s = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(120, 150, 100), 0.7, 80, 6, stable) });
        Assert.True(j.Instability > ValidationGate.MaxInstability);
        Assert.Equal(GateStatus.Warn, j.Checks.First(c => c.Name == "Stability").Status);
        Assert.True(s.Instability < ValidationGate.MaxInstability);
        Assert.Equal(GateStatus.Pass, s.Checks.First(c => c.Name == "Stability").Status);
    }

    [Fact]
    public void Parameters_loaded_from_json_are_read_as_numbers()
    {
        var json = JsonSerializer.Deserialize<Dictionary<string, object?>>("{\"Fast\":12,\"Text\":\"7.5\",\"Bool\":true}")!;
        Assert.Equal(12, Robustness.ToDouble(json["Fast"]));
        Assert.Equal(7.5, Robustness.ToDouble(json["Text"]));
        Assert.Null(Robustness.ToDouble(json["Bool"]));
        Assert.Equal(3.0, Robustness.ToDouble(3m));
    }

    private static OptimizationResult Grid(Func<int, int, decimal> net)
    {
        var res = new OptimizationResult
        {
            Ranges = new List<ParameterRange>
            {
                new() { Name = "A", Min = 1, Max = 9, Step = 1, Enabled = true },
                new() { Name = "B", Min = 1, Max = 9, Step = 1, Enabled = true },
            },
        };
        for (int a = 1; a <= 9; a++)
        for (int b = 1; b <= 9; b++)
            res.Rows.Add(new OptimizationRow { Parameters = new() { ["A"] = a, ["B"] = b }, Metrics = new PerformanceMetrics { NetProfit = net(a, b) }, Fitness = (double)net(a, b) });
        res.Rows = res.Rows.OrderByDescending(r => r.Fitness).ToList();
        return res;
    }

    [Fact]
    public void Meseta_contra_pico_aislado()
    {
        // plateau: the whole zone around (5,5) wins
        var plateau = Robustness.Plateau(Grid((a, b) => 1000 - 50 * (Math.Abs(a - 5) + Math.Abs(b - 5))))!.Value;
        Assert.Equal(1.0, plateau.ProfitableShare, 3);
        Assert.Equal(8, plateau.Neighbors);
        // a peak: only (5,5) wins, everything else loses
        var spike = Robustness.Plateau(Grid((a, b) => a == 5 && b == 5 ? 5000 : -200))!.Value;
        Assert.Equal(0.0, spike.ProfitableShare, 3);
        var gate = ValidationGate.Evaluate(new GateInput { WalkForward = Wf(Edge(120, 150, 100), 0.7, 80, 8), Optimization = Grid((a, b) => a == 5 && b == 5 ? 5000 : -200) });
        Assert.Equal(GateStatus.Warn, gate.Checks.First(c => c.Name == "Plateau").Status);
        Assert.Equal(GateStatus.Warn, gate.Checks.First(c => c.Name == "Robustness").Status);
    }
}
