using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using TradingHub.Data;

namespace TradingHub.Strategies;

/// <summary>
/// Range of a parameter to optimize. Besides the strategy's own, it can be the candle period
/// (<see cref="PeriodName"/>): it does not change the strategy but how its candles are built, and it is swept like one more.
/// </summary>
public sealed class ParameterRange
{
    /// <summary>Name of the candle period "parameter".</summary>
    public const string PeriodName = "__Periodo";

    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; } = 1;
    public bool IsInteger { get; set; }

    /// <summary>For the candle period: the periods to try (codes like M15 or H1), from the shortest to the longest.</summary>
    public List<string> Choices { get; set; } = new();

    public bool IsPeriod => Name == PeriodName;

    public ParameterRange Clone() =>
        new() { Name = Name, Label = Label, Enabled = Enabled, Min = Min, Max = Max, Step = Step, IsInteger = IsInteger, Choices = Choices.ToList() };

    /// <summary>The candle period as one more parameter: each of <paramref name="periods"/> is tried.</summary>
    public static ParameterRange Periods(IEnumerable<string> periods, bool enabled = true)
    {
        var r = new ParameterRange { Name = PeriodName, Label = "Period", Enabled = enabled, IsInteger = true };
        r.SetChoices(periods);
        return r;
    }

    /// <summary>The periods to try: only the valid ones, without repeats and from the shortest to the longest (neighbours for the plateau).</summary>
    public void SetChoices(IEnumerable<string> periods)
    {
        Choices = periods.Select(p => ChartPeriod.TryParse(p, out var cp) ? cp : (ChartPeriod?)null).OfType<ChartPeriod>().Distinct()
            .OrderBy(p => p.IsTimeBased ? 0 : 1).ThenBy(p => p.ApproxDuration).ThenBy(p => p.Type).ThenBy(p => p.Value)
            .Select(p => p.Code).ToList();
        Min = 0;
        Max = Math.Max(0, Choices.Count - 1);
        Step = 1;
    }

    /// <summary>The value of a combination as a number (a period, by its slot in the list).</summary>
    public double? Numeric(object? value)
    {
        if (Choices.Count == 0) return Robustness.ToDouble(value);
        int i = value == null ? -1 : Choices.IndexOf(value.ToString()!);
        return i >= 0 ? i : null;
    }

    public IEnumerable<double> Values()
    {
        if (!Enabled || Step <= 0 || Max < Min)
        {
            yield return Min;
            yield break;
        }

        int n = (int)Math.Floor((Max - Min) / Step + 1e-9);
        for (int i = 0; i <= n; i++) yield return Math.Round(Min + i * Step, 8);
    }

    public int Count => !Enabled || Step <= 0 || Max < Min ? 1 : (int)Math.Floor((Max - Min) / Step + 1e-9) + 1;

    /// <summary>Ranges suggested from [Optimize] (or around the current value).</summary>
    public static List<ParameterRange> FromStrategy(Type t, Strategy current)
    {
        var list = new List<ParameterRange>();
        foreach (var p in Strategy.Parameters(t))
        {
            var type = p.PropertyType;
            bool numeric = type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal);
            if (!numeric) continue;
            var opt = p.GetCustomAttribute<OptimizeAttribute>();
            double value = Convert.ToDouble(p.GetValue(current), CultureInfo.InvariantCulture);
            bool integer = type == typeof(int) || type == typeof(long);
            list.Add(new ParameterRange
            {
                Name = p.Name,
                Label = Strategy.ParameterName(p),
                Enabled = opt != null,
                Min = opt?.Min ?? value,
                Max = opt?.Max ?? value,
                Step = opt?.Step ?? (integer ? 1 : Math.Max(Math.Abs(value) / 10, 0.01)),
                IsInteger = integer,
            });
        }

        return list;
    }
}

/// <summary>A combination tested in the optimization.</summary>
public sealed class OptimizationRow
{
    /// <summary>Its slot in the grid (to measure the plateau).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int[]? Index { get; set; }

    public Dictionary<string, object?> Parameters { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public double Fitness { get; set; }
}

/// <summary>Result of an optimization.</summary>
public sealed class OptimizationResult
{
    public FitnessMetric Metric { get; set; }
    public List<ParameterRange> Ranges { get; set; } = new();
    public List<OptimizationRow> Rows { get; set; } = new();
    public int Combinations { get; set; }
    public double Seconds { get; set; }
    public OptimizationRow? Best => Rows.Count > 0 ? Rows[0] : null;

    /// <summary>The recommended one: the best plateau (average with its neighbours), not the best peak. Index into <see cref="Rows"/>.</summary>
    public int RecommendedIndex { get; set; } = -1;

    [System.Text.Json.Serialization.JsonIgnore]
    public OptimizationRow? Recommended => RecommendedIndex >= 0 && RecommendedIndex < Rows.Count ? Rows[RecommendedIndex] : Best;
}

/// <summary>A walk-forward window.</summary>
public sealed class WalkForwardWindow
{
    public int Number { get; set; }
    public DateTime InSampleFrom { get; set; }
    public DateTime InSampleTo { get; set; }
    public DateTime OutSampleFrom { get; set; }
    public DateTime OutSampleTo { get; set; }
    public Dictionary<string, object?> BestParameters { get; set; } = new();
    public PerformanceMetrics InSample { get; set; } = new();
    public PerformanceMetrics OutSample { get; set; } = new();

    /// <summary>Out-of-sample / in-sample performance (annualized by days).</summary>
    public double Efficiency { get; set; }
}

/// <summary>Result of the walk-forward.</summary>
public sealed class WalkForwardResult
{
    public int InSampleDays { get; set; }
    public int OutSampleDays { get; set; }
    public bool Anchored { get; set; }
    public FitnessMetric Metric { get; set; }

    /// <summary>Ranges optimized in each window (to measure the stability of the chosen parameters).</summary>
    public List<ParameterRange> Ranges { get; set; } = new();
    public List<WalkForwardWindow> Windows { get; set; } = new();

    /// <summary>Every out-of-sample trade, chained together.</summary>
    public List<ClosedTrade> OutSampleTrades { get; set; } = new();
    public List<EquityPoint> OutSampleEquity { get; set; } = new();
    public PerformanceMetrics OutSampleMetrics { get; set; } = new();
    public double WalkForwardEfficiency { get; set; }
    public double ProfitableWindowsPercent { get; set; }
    public double Seconds { get; set; }

    /// <summary>How the parameters were searched for in each window: "grid" or "bayesian".</summary>
    public string Search { get; set; } = "grid";

    /// <summary>Distinct combinations that were run (each one just once, over the whole history) and how many there were.</summary>
    public int Evaluated { get; set; }
    public long SpaceSize { get; set; }

    /// <summary>Events after merging the repeated ticks, over the original ticks (1 = not compressed).</summary>
    public double Compression { get; set; } = 1;

    /// <summary>
    /// Probability of overoptimization (PBO, combinatorially symmetric cross-validation): in what share of the
    /// partitions the best combination of one half of the history landed below the median on the other half. Loss:
    /// in what share it lost money on the other half. null if it could not be measured (fewer than 4 combinations).
    /// </summary>
    public double? Pbo { get; set; }
    public double? PboLoss { get; set; }
    public int PboSplits { get; set; }

    /// <summary>Out-of-sample paths of the purged combinatorial cross-validation (empty if it could not be measured).</summary>
    public List<CpcvPath> CpcvPaths { get; set; } = new();
    public int CpcvGroups { get; set; }
    public int EmbargoDays { get; set; }
}

/// <summary>Monte Carlo result (reshuffling the trades at random).</summary>
public sealed class MonteCarloResult
{
    public int Runs { get; set; }
    public decimal DrawdownP50 { get; set; }
    public decimal DrawdownP95 { get; set; }
    public decimal DrawdownP99 { get; set; }
    public decimal WorstDrawdown { get; set; }
    public decimal NetProfit { get; set; }
    public double RuinProbabilityPercent { get; set; }
    public List<List<decimal>> SampleCurves { get; set; } = new();
}

/// <summary>Optimization, walk-forward and Monte Carlo.</summary>
public static class Optimizer
{
    /// <summary>Total combinations of a grid.</summary>
    public static long Combinations(IEnumerable<ParameterRange> ranges) => ranges.Aggregate(1L, (acc, r) => acc * Math.Max(1, r.Count));

    /// <summary>Up to how many combinations the whole grid is swept in the walk-forward (above that, Bayesian search).</summary>
    public const int GridLimit = 300;

    /// <summary>Combinations the Bayesian search measures in each window (unless something else is asked for).</summary>
    public const int DefaultBudget = 80;

    /// <summary>
    /// Repeated ticks can be merged (<see cref="TickData.Compress"/>) without changing the result: time or range candles
    /// and a strategy that decides on candles (not on every tick).
    /// </summary>
    public static bool CanCompress(Type strategyType, string period) =>
        ChartPeriod.TryParse(period, out var p) && (p.IsTimeBased || p.Type == PeriodType.Range) && !Strategy.UsesTicks(strategyType);

    /// <summary>Ticks can be merged if it works with every period being tried.</summary>
    private static bool CanCompress(Type strategyType, ParameterSpace space, BacktestSettings settings) =>
        (space.Periods.Count > 0 ? space.Periods : new[] { settings.Period }).All(p => CanCompress(strategyType, p));

    /// <summary>The settings of a combination: with its period if the period is optimized (otherwise, the same ones).</summary>
    public static BacktestSettings SettingsFor(BacktestSettings settings, IReadOnlyDictionary<string, object?> combo)
    {
        if (!combo.TryGetValue(ParameterRange.PeriodName, out var p) || p is not string period) return settings;
        var s = settings.Clone();
        s.Period = period;
        return s;
    }

    /// <summary>Tests every combination in parallel, sorts by the chosen metric and recommends the best plateau.</summary>
    public static OptimizationResult Optimize(Func<Strategy> factory, Dictionary<string, object?> baseParameters, IReadOnlyList<ParameterRange> ranges,
        TickData data, BacktestSettings settings, FitnessMetric metric, int minTrades, IProgress<double>? progress, CancellationToken ct, int maxParallel = 0,
        IReadOnlyList<(DateTime Time, decimal Close)>? contextHistory = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var type = factory().GetType();
        var space = new ParameterSpace(ranges, type);
        if (!data.Compressed && CanCompress(type, space, settings)) data = data.Compress();
        var combos = space.All().ToList();
        var rows = new ConcurrentBag<OptimizationRow>();
        int done = 0;
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = maxParallel > 0 ? maxParallel : Math.Max(1, Environment.ProcessorCount - 1) };
        Parallel.ForEach(combos, options, idx =>
        {
            var combo = space.Config(idx);
            var s = factory();
            s.SetParameters(baseParameters);
            s.SetParameters(combo);
            var r = new BacktestEngine(s, data, SettingsFor(settings, combo)) { ContextHistory = contextHistory }.Run(ct);
            if (r.Ok)
            {
                double fitness = r.Metrics.TotalTrades < minTrades ? double.NegativeInfinity : r.Metrics.Fitness(metric);
                rows.Add(new OptimizationRow { Index = idx, Parameters = combo, Metrics = r.Metrics, Fitness = fitness });
            }

            progress?.Report(Interlocked.Increment(ref done) / (double)combos.Count);
        });

        var sorted = rows.OrderByDescending(r => r.Fitness).ThenByDescending(r => r.Metrics.NetProfit).ToList();
        return new OptimizationResult
        {
            Metric = metric,
            Ranges = ranges.ToList(),
            Rows = sorted,
            Combinations = combos.Count,
            Seconds = sw.Elapsed.TotalSeconds,
            RecommendedIndex = RobustSelection.Select(sorted.Select(r => (r.Index!, r.Fitness)).ToList()),
        };
    }

    /// <summary>
    /// Walk-forward: in each window it picks parameters looking only at what is inside (in sample) and tests them on the following
    /// days (out of sample); what matters is the chained out-of-sample result. How it works:
    /// <list type="bullet">
    /// <item>Each combination is run just once over the whole history (repeated ticks merged, indicators warm
    /// and context as it is live) and the windows trim that result: backtests are not repeated.</item>
    /// <item>It searches the whole grid if it is small (<see cref="GridLimit"/>) or with Bayesian optimization (TPE) if it is
    /// large, in each window using that window's data only.</item>
    /// <item>It picks the best plateau (average with the neighbours), not the best peak.</item>
    /// <item>At the end it measures overfitting with every combination that was run: PBO and CPCV paths.</item>
    /// </list>
    /// </summary>
    public static WalkForwardResult WalkForward(Func<Strategy> factory, Dictionary<string, object?> baseParameters, IReadOnlyList<ParameterRange> ranges,
        TickData data, BacktestSettings settings, FitnessMetric metric, int inSampleDays, int outSampleDays, bool anchored, int minTrades,
        IProgress<double>? progress, CancellationToken ct, IReadOnlyList<(DateTime Time, decimal Close)>? contextHistory = null,
        SearchMode search = SearchMode.Auto, int budget = 0, int maxParallel = 0)
    {
        // each window starts its market context with whatever came before it (from before the range and from the previous windows),
        // as it is live: without this, each out-of-sample window used to start without knowing whether it was a range or a trend
        contextHistory = MarketContextTracker.Merge(contextHistory, MarketContextTracker.HourlyCloses(data));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var type = factory().GetType();
        int raw = data.Count;
        var space = new ParameterSpace(ranges, type);
        if (!data.Compressed && CanCompress(type, space, settings)) data = data.Compress();
        bool grid = search == SearchMode.Grid || (search == SearchMode.Auto && space.Size <= GridLimit);
        int parallel = maxParallel > 0 ? maxParallel : Math.Max(1, Environment.ProcessorCount - 1);
        if (budget <= 0) budget = DefaultBudget;
        var res = new WalkForwardResult
        {
            InSampleDays = inSampleDays, OutSampleDays = outSampleDays, Anchored = anchored, Metric = metric, Ranges = ranges.Where(r => r.Enabled).ToList(),
            Search = grid ? "grid" : "bayesian", SpaceSize = space.Size, Compression = raw > 0 ? Math.Round((double)data.Count / raw, 4) : 1,
        };
        var windows = Windows(settings, inSampleDays, outSampleDays, anchored);
        if (windows.Count == 0) throw new InvalidOperationException("The range is too short for those windows: lengthen the period or shrink the windows.");

        var cache = new ConfigCache(factory, baseParameters, data, settings.Clone(), contextHistory, space, ct);
        long expected = grid ? space.Size : Math.Min(space.Size, (long)budget * windows.Count / 2 + budget);
        int ran = 0;
        void Ran() => progress?.Report(Math.Min(0.95, 0.95 * Interlocked.Increment(ref ran) / Math.Max(1, expected)));
        if (grid) cache.Ensure(space.All().ToList(), parallel, Ran);

        double isProfit = 0, isDaysTotal = 0, osProfit = 0, osDaysTotal = 0;
        decimal capital = settings.InitialCapital;
        var chained = new List<EquityPoint>();
        for (int w = 0; w < windows.Count; w++)
        {
            ct.ThrowIfCancellationRequested();
            var (isFrom, isTo, osTo) = windows[w];
            double Score(ConfigRun run)
            {
                if (!run.Ok) return double.NegativeInfinity;
                var m = cache.Measure(run, isFrom, isTo);
                return m.TotalTrades < minTrades ? double.NegativeInfinity : m.Fitness(metric);
            }

            var rows = grid
                ? space.All().Select(idx => (Idx: idx, Score: Score(cache.Get(idx)))).ToList()
                : TpeSearch.Run(space, points =>
                {
                    cache.Ensure(points, parallel, Ran);
                    return points.Select(p => Score(cache.Get(p))).ToArray();
                }, budget, 1000 + w, parallel, ct);
            int pick = RobustSelection.Select(rows);
            if (pick < 0) continue;
            var best = cache.Get(rows[pick].Idx);
            var isM = cache.Measure(best, isFrom, isTo);
            var (osTrades, osEquity) = cache.Slice(best, isTo, osTo, chained.Count > 0 ? chained[^1].Equity : capital);
            var osM = PerformanceMetrics.Compute(osTrades, osEquity, chained.Count > 0 ? chained[^1].Equity : capital, cache.Crypto, 0, 0);
            foreach (var t in osTrades) res.OutSampleTrades.Add(t with { Number = res.OutSampleTrades.Count + 1 });
            chained.AddRange(osEquity);

            double isDays = Math.Max(1, (isTo - isFrom).TotalDays), osDays = Math.Max(1, (osTo - isTo).TotalDays);
            double isRate = (double)isM.NetProfit / isDays, osRate = (double)osM.NetProfit / osDays;
            isProfit += (double)isM.NetProfit;
            isDaysTotal += isDays;
            osProfit += (double)osM.NetProfit;
            osDaysTotal += osDays;
            res.Windows.Add(new WalkForwardWindow
            {
                Number = w + 1,
                InSampleFrom = isFrom,
                InSampleTo = isTo,
                OutSampleFrom = isTo,
                OutSampleTo = osTo,
                BestParameters = best.Parameters,
                InSample = isM,
                OutSample = osM,
                Efficiency = isRate > 0 ? osRate / isRate : 0,
            });
        }

        // the chained out-of-sample curve, with its drawdown
        decimal peak = settings.InitialCapital;
        foreach (var e in chained)
        {
            peak = Math.Max(peak, e.Equity);
            res.OutSampleEquity.Add(e with { Drawdown = peak - e.Equity });
        }

        res.OutSampleMetrics = PerformanceMetrics.Compute(res.OutSampleTrades, res.OutSampleEquity, settings.InitialCapital, cache.Crypto, 0, 0);
        // overall efficiency: out-of-sample daily profit / in-sample daily profit
        double isDaily = isDaysTotal > 0 ? isProfit / isDaysTotal : 0, osDaily = osDaysTotal > 0 ? osProfit / osDaysTotal : 0;
        res.WalkForwardEfficiency = isDaily > 0 ? osDaily / isDaily : 0;
        res.ProfitableWindowsPercent = res.Windows.Count > 0 ? 100.0 * res.Windows.Count(x => x.OutSample.NetProfit > 0) / res.Windows.Count : 0;
        Overfit(res, cache, settings, minTrades);
        res.Evaluated = cache.Count;
        res.Seconds = sw.Elapsed.TotalSeconds;
        progress?.Report(1);
        return res;
    }

    /// <summary>The windows: (from, in-sample to, out-of-sample to).</summary>
    private static List<(DateTime IsFrom, DateTime IsTo, DateTime OsTo)> Windows(BacktestSettings settings, int inSampleDays, int outSampleDays, bool anchored)
    {
        var start = settings.FromUtc.Date;
        var end = settings.ToUtc;
        var windows = new List<(DateTime IsFrom, DateTime IsTo, DateTime OsTo)>();
        for (var isFrom = start; ; isFrom = anchored ? start : isFrom.AddDays(outSampleDays))
        {
            var isTo = anchored ? start.AddDays(inSampleDays + windows.Count * outSampleDays) : isFrom.AddDays(inSampleDays);
            var osTo = isTo.AddDays(outSampleDays);
            if (isTo >= end) break;
            if (osTo > end) osTo = end;
            windows.Add((isFrom, isTo, osTo));
            if (osTo >= end) break;
        }

        return windows;
    }

    /// <summary>PBO and CPCV with the daily result of every combination that was run (4 or more are needed).</summary>
    private static void Overfit(WalkForwardResult res, ConfigCache cache, BacktestSettings settings, int minTrades)
    {
        var runs = cache.Done.Where(r => r.Ok && r.HourTimes.Length > 0).OrderBy(r => ParameterSpace.Key(r.Index), StringComparer.Ordinal).ToList();
        if (runs.Count < 4) return;
        var days = runs[0].HourTimes.Select(t => DateOnly.FromDateTime(new DateTime(t, DateTimeKind.Utc))).Distinct().OrderBy(d => d).ToList();
        if (days.Count < 20) return;
        double capital = (double)settings.InitialCapital;
        var daily = runs.Select(r => ConfigCache.Daily(r, days, capital)).ToList();
        if (Overfitting.Pbo(daily) is { } pbo)
        {
            res.Pbo = Math.Round(pbo.Pbo, 4);
            res.PboLoss = Math.Round(pbo.Loss, 4);
            res.PboSplits = pbo.Splits;
        }

        // embargo: how long a trade usually lasts (at least a day), so the chosen one does not see the test through the adjacent window
        var holds = runs.SelectMany(r => r.Trades.Take(200)).Select(t => (t.ExitTime - t.EntryTime).TotalDays).OrderBy(d => d).ToList();
        int embargo = holds.Count > 0 ? Math.Clamp((int)Math.Ceiling(holds[holds.Count / 2]), 1, 5) : 1;
        var perDay = runs.Select(r => ConfigCache.TradesPerDay(r, days)).ToList();
        res.CpcvPaths = Overfitting.Cpcv(runs.Select(r => r.Index).ToList(), daily, perDay, minTrades, capital, cache.Crypto, groups: 8, embargo: embargo);
        res.CpcvGroups = res.CpcvPaths.Count > 0 ? res.CpcvPaths.Count + 1 : 0;
        res.EmbargoDays = embargo;
    }

    /// <summary>Reshuffles the trades many times to see which drawdowns are possible.</summary>
    public static MonteCarloResult MonteCarlo(IReadOnlyList<ClosedTrade> trades, decimal capital, int runs = 1000, decimal ruinDrawdownPercent = 50, int seed = 7)
    {
        var res = new MonteCarloResult { Runs = runs, NetProfit = trades.Sum(t => t.NetPnl) };
        if (trades.Count < 2) return res;
        var rnd = new Random(seed);
        var pnls = trades.Select(t => t.NetPnl).ToArray();
        var dds = new decimal[runs];
        int ruined = 0;
        for (int r = 0; r < runs; r++)
        {
            var order = pnls.OrderBy(_ => rnd.Next()).ToArray();
            decimal eq = capital, peak = capital, maxDd = 0;
            List<decimal>? curve = r < 40 ? new List<decimal>(order.Length + 1) { capital } : null;
            foreach (var p in order)
            {
                eq += p;
                peak = Math.Max(peak, eq);
                maxDd = Math.Max(maxDd, peak - eq);
                curve?.Add(eq);
            }

            dds[r] = maxDd;
            if (capital > 0 && maxDd / capital * 100 >= ruinDrawdownPercent) ruined++;
            if (curve != null) res.SampleCurves.Add(curve);
        }

        Array.Sort(dds);
        res.DrawdownP50 = dds[(int)(runs * 0.50)];
        res.DrawdownP95 = dds[Math.Min(runs - 1, (int)(runs * 0.95))];
        res.DrawdownP99 = dds[Math.Min(runs - 1, (int)(runs * 0.99))];
        res.WorstDrawdown = dds[^1];
        res.RuinProbabilityPercent = 100.0 * ruined / runs;
        return res;
    }
}
