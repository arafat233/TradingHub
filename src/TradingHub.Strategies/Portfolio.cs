namespace TradingHub.Strategies;

/// <summary>A strategy inside a portfolio: a stored run and its weight.</summary>
public sealed class PortfolioComponent
{
    public string StrategyId { get; set; } = "";
    public string StrategyName { get; set; } = "";
    public string RunId { get; set; } = "";

    /// <summary>Size multiplier (1 = as in the run).</summary>
    public decimal Weight { get; set; } = 1;

    public string Label { get; set; } = "";
    public BacktestSettings Settings { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public List<ClosedTrade> Trades { get; set; } = new();

    public static PortfolioComponent FromRun(StrategyRun run, decimal weight = 1) => new()
    {
        StrategyId = run.StrategyId,
        StrategyName = run.StrategyName,
        RunId = run.Id,
        Weight = weight,
        Label = $"{run.StrategyName} · {run.Settings.Symbol} {run.Settings.Period}",
        Settings = run.Settings,
        Metrics = run.Metrics ?? new PerformanceMetrics(),
        Trades = run.Trades ?? new List<ClosedTrade>(),
    };
}

/// <summary>Result of combining several strategies.</summary>
public sealed class PortfolioResult
{
    public List<PortfolioComponent> Components { get; set; } = new();
    public DateTime From { get; set; }
    public DateTime To { get; set; }

    /// <summary>True if the components share no dates and the union of the ranges was used.</summary>
    public bool NoOverlap { get; set; }

    public List<DateOnly> Days { get; set; } = new();

    /// <summary>Daily result of each component (already multiplied by its weight), aligned with <see cref="Days"/>.</summary>
    public List<decimal[]> DailyPnl { get; set; } = new();

    /// <summary>Correlation of the daily results (Pearson), component x component.</summary>
    public double[,] Correlation { get; set; } = new double[0, 0];

    public List<ClosedTrade> Trades { get; set; } = new();
    public List<EquityPoint> Equity { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public List<PerformanceMetrics> ComponentMetrics { get; set; } = new();

    public double AverageCorrelation { get; set; }
    public double MaxCorrelation { get; set; }
    public decimal SumOfComponentDrawdowns { get; set; }
    public double AverageComponentSharpe { get; set; }

    /// <summary>How much less drawdown the portfolio has than the sum of the individual drawdowns (%).</summary>
    public double DrawdownReductionPercent => SumOfComponentDrawdowns > 0 ? (double)(1 - Metrics.MaxDrawdown / SumOfComponentDrawdowns) * 100 : 0;
}

/// <summary>
/// Strategy portfolios: it adds up each one's daily results (with its weight), measures the correlation
/// between them and looks for lightly correlated combinations that improve the Sharpe and lower the drawdown.
/// </summary>
public static class PortfolioAnalyzer
{
    private static DateTime Start(PortfolioComponent c) => c.Settings.FromUtc != default ? c.Settings.FromUtc : c.Trades.Count > 0 ? c.Trades.Min(t => t.EntryTime) : DateTime.MinValue;
    private static DateTime End(PortfolioComponent c) => c.Settings.ToUtc != default ? c.Settings.ToUtc : c.Trades.Count > 0 ? c.Trades.Max(t => t.ExitTime) : DateTime.MinValue;

    private static Dictionary<DateOnly, decimal> Daily(PortfolioComponent c, DateTime from, DateTime to)
    {
        var d = new Dictionary<DateOnly, decimal>();
        foreach (var t in c.Trades)
        {
            if (t.ExitTime < from || t.ExitTime > to) continue;
            var day = DateOnly.FromDateTime(t.ExitTime);
            d[day] = d.GetValueOrDefault(day) + t.NetPnl * c.Weight;
        }

        return d;
    }

    /// <summary>Pearson correlation (0 if one of them does not vary).</summary>
    public static double Pearson(IReadOnlyList<decimal> a, IReadOnlyList<decimal> b)
    {
        int n = Math.Min(a.Count, b.Count);
        if (n < 3) return 0;
        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++)
        {
            ma += (double)a[i];
            mb += (double)b[i];
        }

        ma /= n;
        mb /= n;
        double cov = 0, va = 0, vb = 0;
        for (int i = 0; i < n; i++)
        {
            double x = (double)a[i] - ma, y = (double)b[i] - mb;
            cov += x * y;
            va += x * x;
            vb += y * y;
        }

        return va > 0 && vb > 0 ? cov / Math.Sqrt(va * vb) : 0;
    }

    /// <summary>Combines the components over the period they share (or over the union if they share nothing).</summary>
    public static PortfolioResult Analyze(IReadOnlyList<PortfolioComponent> components, decimal capital)
    {
        var res = new PortfolioResult { Components = components.ToList() };
        if (components.Count == 0) return res;
        var from = components.Max(Start);
        var to = components.Min(End);
        if (to <= from)
        {
            res.NoOverlap = true;
            from = components.Min(Start);
            to = components.Max(End);
        }

        res.From = from;
        res.To = to;
        var dailies = components.Select(c => Daily(c, from, to)).ToList();
        for (var d = DateOnly.FromDateTime(from); d <= DateOnly.FromDateTime(to); d = d.AddDays(1)) res.Days.Add(d);
        foreach (var daily in dailies) res.DailyPnl.Add(res.Days.Select(d => daily.GetValueOrDefault(d)).ToArray());

        int n = components.Count;
        res.Correlation = new double[n, n];
        double sum = 0, max = double.MinValue;
        int pairs = 0;
        for (int i = 0; i < n; i++)
        {
            res.Correlation[i, i] = 1;
            for (int j = i + 1; j < n; j++)
            {
                double c = Pearson(res.DailyPnl[i], res.DailyPnl[j]);
                res.Correlation[i, j] = res.Correlation[j, i] = c;
                sum += c;
                max = Math.Max(max, c);
                pairs++;
            }
        }

        res.AverageCorrelation = pairs > 0 ? sum / pairs : 0;
        res.MaxCorrelation = pairs > 0 ? max : 0;

        // the portfolio's trades (with their weight) and daily curve
        res.Trades = components
            .SelectMany(c => c.Trades.Where(t => t.ExitTime >= from && t.ExitTime <= to)
                .Select(t => t with { Pnl = t.Pnl * c.Weight, Commission = t.Commission * c.Weight, Quantity = t.Quantity * c.Weight }))
            .OrderBy(t => t.ExitTime).ToList();
        decimal eq = capital, peak = capital;
        res.Equity.Add(new EquityPoint(from, capital, 0));
        for (int k = 0; k < res.Days.Count; k++)
        {
            eq += res.DailyPnl.Sum(p => p[k]);
            peak = Math.Max(peak, eq);
            res.Equity.Add(new EquityPoint(res.Days[k].ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc), eq, peak - eq));
        }

        bool crypto = components.All(c => c.Settings.FeedId.StartsWith("binance") || c.Settings.FeedId.StartsWith("bybit") || c.Settings.FeedId == "sim");
        res.Metrics = PerformanceMetrics.Compute(res.Trades, res.Equity, capital, crypto, 0, 0);

        // each component on its own, over the same period, with its weight and the same daily curve (days with no trades included),
        // so its metrics are comparable with the portfolio's
        for (int i = 0; i < components.Count; i++)
        {
            var c = components[i];
            var trades = c.Trades.Where(t => t.ExitTime >= from && t.ExitTime <= to)
                .Select(t => t with { Pnl = t.Pnl * c.Weight, Commission = t.Commission * c.Weight }).OrderBy(t => t.ExitTime).ToList();
            var curve = new List<EquityPoint> { new(from, capital, 0) };
            decimal e = capital, pk = capital;
            for (int k = 0; k < res.Days.Count; k++)
            {
                e += res.DailyPnl[i][k];
                pk = Math.Max(pk, e);
                curve.Add(new EquityPoint(res.Days[k].ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc), e, pk - e));
            }

            res.ComponentMetrics.Add(PerformanceMetrics.Compute(trades, curve, capital, crypto, 0, 0));
        }

        res.SumOfComponentDrawdowns = res.ComponentMetrics.Sum(m => m.MaxDrawdown);
        res.AverageComponentSharpe = res.ComponentMetrics.Count > 0 ? res.ComponentMetrics.Average(m => m.SharpeRatio) : 0;
        return res;
    }

    /// <summary>Equal-risk weights: each strategy weighs the inverse of the volatility of its daily result.</summary>
    public static decimal[] InverseVolatilityWeights(IReadOnlyList<PortfolioComponent> components, decimal capital)
    {
        var unit = components.Select(c => new PortfolioComponent { Settings = c.Settings, Trades = c.Trades, Metrics = c.Metrics, Weight = 1 }).ToList();
        var r = Analyze(unit, capital);
        var vols = r.DailyPnl.Select(p =>
        {
            double mean = p.Average(x => (double)x);
            return Math.Sqrt(p.Sum(x => ((double)x - mean) * ((double)x - mean)) / Math.Max(1, p.Length - 1));
        }).ToArray();
        double avg = vols.Where(v => v > 0).DefaultIfEmpty(1).Average();
        return vols.Select(v => v > 0 ? (decimal)Math.Round(avg / v, 2) : 1m).ToArray();
    }

    /// <summary>
    /// Builds a lightly correlated combination: it starts with the best Sharpe and adds, one at a time, the strategy
    /// that improves the set's Sharpe the most without exceeding the maximum correlation with the ones already chosen.
    /// Returns the chosen indices (in the order they were added).
    /// </summary>
    public static List<int> SuggestUncorrelated(IReadOnlyList<PortfolioComponent> candidates, decimal capital, int maxComponents = 5, double maxCorrelation = 0.5)
    {
        var chosen = new List<int>();
        var usable = Enumerable.Range(0, candidates.Count).Where(i => candidates[i].Trades.Count >= 5 && candidates[i].Metrics.NetProfit > 0).ToList();
        if (usable.Count == 0) return chosen;
        chosen.Add(usable.OrderByDescending(i => candidates[i].Metrics.SharpeRatio).First());
        double current = Analyze(new[] { candidates[chosen[0]] }, capital).Metrics.SharpeRatio;
        while (chosen.Count < maxComponents)
        {
            int best = -1;
            double bestSharpe = current;
            foreach (int i in usable.Except(chosen))
            {
                bool tooCorrelated = false;
                foreach (int j in chosen)
                {
                    var pair = Analyze(new[] { candidates[i], candidates[j] }, capital);
                    if (!pair.NoOverlap && pair.Correlation[0, 1] > maxCorrelation)
                    {
                        tooCorrelated = true;
                        break;
                    }
                }

                if (tooCorrelated) continue;
                var set = chosen.Append(i).Select(k => candidates[k]).ToList();
                double s = Analyze(set, capital).Metrics.SharpeRatio;
                if (s > bestSharpe + 0.01)
                {
                    bestSharpe = s;
                    best = i;
                }
            }

            if (best < 0) break;
            chosen.Add(best);
            current = bestSharpe;
        }

        return chosen;
    }
}
