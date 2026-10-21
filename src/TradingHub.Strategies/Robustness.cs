using System.Globalization;
using System.Text.Json;

namespace TradingHub.Strategies;

/// <summary>
/// Tests against overoptimization: probabilistic and deflated Sharpe (Bailey and Lopez de Prado), stability of
/// the parameters chosen in the walk-forward and plateau around the optimization's best combination.
/// </summary>
public static class Robustness
{
    private const double EulerGamma = 0.5772156649015329;

    /// <summary>Cumulative standard normal distribution.</summary>
    public static double NormalCdf(double x)
    {
        // erfc with the Numerical Recipes approximation (error < 1.2e-7)
        double z = Math.Abs(x) / Math.Sqrt(2);
        double t = 1 / (1 + 0.5 * z);
        double r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 + t * (0.27886807 +
            t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? 1 - r / 2 : r / 2;
    }

    /// <summary>Inverse of the standard normal (Acklam's algorithm, relative error &lt; 1.2e-9).</summary>
    public static double NormalInv(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        double[] a = { -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00 };
        double[] b = { -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01 };
        double[] c = { -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00 };
        double[] d = { 7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00 };
        const double low = 0.02425, high = 1 - low;
        if (p < low)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        if (p > high)
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        double u = p - 0.5, r = u * u;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * u / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }

    /// <summary>Expected maximum of <paramref name="n"/> independent standard normals (what luck alone gives when you try n things).</summary>
    public static double ExpectedMaxZ(int n)
    {
        if (n <= 1) return 0;
        return (1 - EulerGamma) * NormalInv(1 - 1.0 / n) + EulerGamma * NormalInv(1 - 1.0 / (n * Math.E));
    }

    /// <summary>Sharpe per trade (mean / standard deviation of the net result), skewness, kurtosis (normal = 3) and count.</summary>
    public static (double Sharpe, double Skew, double Kurtosis, int Count) TradeSharpe(IReadOnlyList<ClosedTrade> trades)
    {
        int n = trades.Count;
        if (n < 2) return (0, 0, 3, n);
        double mean = trades.Average(t => (double)t.NetPnl);
        double m2 = 0, m3 = 0, m4 = 0;
        foreach (var t in trades)
        {
            double d = (double)t.NetPnl - mean;
            m2 += d * d;
            m3 += d * d * d;
            m4 += d * d * d * d;
        }

        m2 /= n;
        m3 /= n;
        m4 /= n;
        if (m2 <= 0) return (0, 0, 3, n);
        double sd = Math.Sqrt(m2);
        return (mean / sd, m3 / (sd * sd * sd), m4 / (m2 * m2), n);
    }

    /// <summary>Probability that the true Sharpe exceeds <paramref name="benchmark"/> given what was observed (PSR).</summary>
    public static double ProbabilisticSharpe(double sharpe, double benchmark, int count, double skew, double kurtosis)
    {
        if (count < 2) return 0;
        double denom = 1 - skew * sharpe + (kurtosis - 1) / 4 * sharpe * sharpe;
        if (denom <= 1e-9) denom = 1e-9;
        return NormalCdf((sharpe - benchmark) * Math.Sqrt(count - 1) / Math.Sqrt(denom));
    }

    /// <summary>
    /// Deflated Sharpe: compares against the best Sharpe you would expect out of pure luck after trying
    /// <paramref name="trials"/> configurations whose Sharpes have variance <paramref name="trialVariance"/>.
    /// The variance is never taken below that of the noise of estimating a Sharpe with that sample (1/n).
    /// </summary>
    public static double DeflatedSharpe(double sharpe, int count, double skew, double kurtosis, int trials, double trialVariance)
    {
        if (count < 2) return 0;
        return ProbabilisticSharpe(sharpe, DeflatedBenchmark(count, trials, trialVariance), count, skew, kurtosis);
    }

    /// <summary>
    /// The bar for the deflated Sharpe: the Sharpe per trade that the best of
    /// <paramref name="trials"/> strategies with no edge at all would show by pure chance, with <paramref name="count"/> trades each.
    /// </summary>
    public static double DeflatedBenchmark(int count, int trials, double trialVariance) =>
        count < 2 ? 0 : Math.Sqrt(Math.Max(trialVariance, 1.0 / count)) * ExpectedMaxZ(Math.Max(1, trials));

    /// <summary>
    /// Edge decay: does it perform the same at the end as at the start? Mean profit per trade of the first and of the last
    /// third (by exit order) and the t of the slope of the result per trade over time (negative = it is fading).
    /// </summary>
    public static (decimal Early, decimal Late, double T, int Count) EdgeDecay(IReadOnlyList<ClosedTrade> trades)
    {
        var list = trades.OrderBy(t => t.ExitTime).ToList();
        int n = list.Count;
        if (n < 6) return (0, 0, 0, n);
        int third = n / 3;
        decimal early = list.Take(third).Average(t => t.NetPnl);
        decimal late = list.Skip(n - third).Average(t => t.NetPnl);
        double mx = (n - 1) / 2.0, my = list.Average(t => (double)t.NetPnl);
        double sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            sxx += (i - mx) * (i - mx);
            sxy += (i - mx) * ((double)list[i].NetPnl - my);
        }

        double slope = sxx > 0 ? sxy / sxx : 0, sse = 0;
        for (int i = 0; i < n; i++)
        {
            double e = (double)list[i].NetPnl - (my + slope * (i - mx));
            sse += e * e;
        }

        double se = sxx > 0 ? Math.Sqrt(sse / (n - 2) / sxx) : 0;
        return (early, late, se > 0 ? slope / se : 0, n);
    }

    /// <summary>Trend efficiency up to which it counts as a range, and from which it counts as a trend.</summary>
    public const double RangeTrend = MarketContextTracker.RangeLimit, TrendingTrend = MarketContextTracker.TrendLimit;

    /// <summary>Volatility percentiles (of the last month) that count as low and high.</summary>
    public const double LowVolatilityRank = MarketContextTracker.LowVolatility, HighVolatilityRank = MarketContextTracker.HighVolatility;

    /// <summary>
    /// Result of the trades by the market they entered in: range, mixed or trend (trend efficiency of the
    /// last 24 hours; a trend being, in addition, up or down) and with low or high volatility (the lowest and the
    /// highest volatility third). Empty if the trades carry no regime (runs from before it existed).
    /// </summary>
    public static List<RegimeStats> Regimes(IReadOnlyList<ClosedTrade> trades)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        var list = new List<RegimeStats>();
        if (known.Count == 0) return list;
        static RegimeStats Of(string name, IEnumerable<ClosedTrade> group, bool detail = false)
        {
            var g = group.ToList();
            decimal win = g.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), loss = -g.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
            return new RegimeStats(name, g.Count, g.Sum(t => t.NetPnl), loss > 0 ? Math.Round(win / loss, 2) : win > 0 ? 999 : 0, detail);
        }

        list.Add(Of("range", known.Where(t => t.EntryTrend <= RangeTrend)));
        list.Add(Of("mixed", known.Where(t => t.EntryTrend > RangeTrend && t.EntryTrend < TrendingTrend)));
        var trending = known.Where(t => t.EntryTrend >= TrendingTrend).ToList();
        list.Add(Of("trend", trending));
        // the trend, split into up and down (a detail: it does not count separately for the regime rule)
        if (trending.Count > 0 && trending.Count(t => t.EntryMarketDirection != null) >= trending.Count * 0.8)
        {
            list.Add(Of("up trend", trending.Where(t => t.EntryMarketDirection > 0), detail: true));
            list.Add(Of("down trend", trending.Where(t => t.EntryMarketDirection < 0), detail: true));
        }
        // volatility: with the last month's percentile (the same one the context filter uses); older runs,
        // by thirds of their own trades
        if (known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8)
        {
            list.Add(Of("low volatility", known.Where(t => t.EntryVolatilityRank <= LowVolatilityRank)));
            list.Add(Of("high volatility", known.Where(t => t.EntryVolatilityRank >= HighVolatilityRank)));
        }
        else
        {
            var vols = known.Where(t => t.EntryVolatility != null).Select(t => t.EntryVolatility!.Value).OrderBy(v => v).ToList();
            if (vols.Count >= 9)
            {
                double low = vols[vols.Count / 3], high = vols[vols.Count * 2 / 3];
                list.Add(Of("low volatility", known.Where(t => t.EntryVolatility < low)));
                list.Add(Of("high volatility", known.Where(t => t.EntryVolatility >= high)));
            }
        }

        return list;
    }

    /// <summary>
    /// Does it only win in certain contexts? It tries the regime filters (range only, no trend, trend only, no range,
    /// up or down trend only) and the volatility ones (last month's percentile) on the out-of-sample trades and keeps the one that improves the most:
    /// what it keeps has to win with PF >= 1.5 and at least 20 trades (and 30% of all of them), and what it drops has to
    /// lose. null if there is no such filter (or the trades carry no context).
    /// </summary>
    public static ContextFilterSuggestion? SuggestContextFilter(IReadOnlyList<ClosedTrade> trades)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        if (known.Count < 30) return null;
        bool ranks = known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8;
        bool directions = known.Count(t => t.EntryMarketDirection != null) >= known.Count * 0.8;
        var regimes = new List<RegimeFilter> { RegimeFilter.All, RegimeFilter.RangeOnly, RegimeFilter.NoTrend, RegimeFilter.TrendOnly, RegimeFilter.NoRange };
        if (directions) regimes.AddRange(new[] { RegimeFilter.UpTrendOnly, RegimeFilter.DownTrendOnly });
        var vols = ranks ? new[] { (0, 100), (0, 33), (0, 66), (34, 100), (67, 100) } : new[] { (0, 100) };

        static (decimal Net, decimal Pf, double T) Stats(List<ClosedTrade> g)
        {
            decimal win = g.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), loss = -g.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
            double mean = g.Count > 0 ? g.Average(t => (double)t.NetPnl) : 0;
            double sd = g.Count > 1 ? Math.Sqrt(g.Sum(t => Math.Pow((double)t.NetPnl - mean, 2)) / (g.Count - 1)) : 0;
            return (g.Sum(t => t.NetPnl), loss > 0 ? win / loss : win > 0 ? 999 : 0, sd > 0 ? mean / sd * Math.Sqrt(g.Count) : 0);
        }

        var all = Stats(known);
        int minKept = Math.Max(20, (int)Math.Ceiling(known.Count * 0.3));
        ContextFilterSuggestion? best = null;
        double bestT = all.T;
        foreach (var regime in regimes)
        foreach (var (lo, hi) in vols)
        {
            if (regime == RegimeFilter.All && lo == 0 && hi == 100) continue;
            var kept = known.Where(t => InRegime(t, regime) && (!ranks || (t.EntryVolatilityRank is { } r && r >= lo && r <= hi))).ToList();
            if (kept.Count < minKept || kept.Count == known.Count) continue;
            var k = Stats(kept);
            var dropped = known.Except(kept).ToList();
            decimal droppedNet = dropped.Sum(t => t.NetPnl);
            if (k.Net <= 0 || k.Pf < 1.5m || droppedNet >= 0 || k.Pf < Math.Min(all.Pf, 99) + 0.3m || k.T <= bestT) continue;
            bestT = k.T;
            best = new ContextFilterSuggestion(regime, lo, hi, DescribeFilter(regime, lo, hi), kept.Count, k.Net, Math.Round(Math.Min(k.Pf, 999), 2), dropped.Count, droppedNet);
        }

        return best;
    }

    /// <summary>Did it enter in the market condition the regime filter asks for? (using the context stored on the trade)</summary>
    private static bool InRegime(ClosedTrade t, RegimeFilter f) =>
        f == RegimeFilter.All || (MarketContextTracker.SetOf(f) & MarketContextTracker.SetOf(t.EntryTrend!.Value, t.EntryMarketDirection)) != 0;

    /// <summary>
    /// What a context filter keeps and drops out of a set of trades (the ones carrying the stored context): how many and with
    /// what net. Volatility is only looked at if the trades carry its percentile.
    /// </summary>
    public static (int Kept, decimal KeptNet, int Dropped, decimal DroppedNet) ApplyFilter(IReadOnlyList<ClosedTrade> trades, RegimeFilter regime, int minRank, int maxRank)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        bool ranks = known.Count > 0 && known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8;
        var kept = known.Where(t => InRegime(t, regime) && (!ranks || (t.EntryVolatilityRank is { } r && r >= minRank && r <= maxRank))).ToList();
        var dropped = known.Except(kept).ToList();
        return (kept.Count, kept.Sum(t => t.NetPnl), dropped.Count, dropped.Sum(t => t.NetPnl));
    }

    /// <summary>
    /// In words, the conditions it trades in: "range only", "range or up trend", "everything but
    /// down trend"… null if they are all of them (no filter).
    /// </summary>
    public static string? DescribeRegimes(RegimeSet set)
    {
        if (set is RegimeSet.All or RegimeSet.None) return null;
        if (set == (RegimeSet.UpTrend | RegimeSet.DownTrend)) return "trend only";
        if (set == (RegimeSet.Range | RegimeSet.Mixed)) return "no trend (range or mixed)";
        if (set == (RegimeSet.Mixed | RegimeSet.UpTrend | RegimeSet.DownTrend)) return "no range (mixed or trend)";
        static string Name(RegimeSet one) => one switch
        {
            RegimeSet.Range => "range",
            RegimeSet.Mixed => "mixed",
            RegimeSet.UpTrend => "up trend",
            _ => "down trend",
        };
        var on = new[] { RegimeSet.Range, RegimeSet.Mixed, RegimeSet.UpTrend, RegimeSet.DownTrend }.Where(x => set.HasFlag(x)).ToList();
        if (on.Count == 1) return Name(on[0]) + " only";
        if (on.Count == 3) return "everything but " + Name(RegimeSet.All & ~set);
        return string.Join(" or ", on.Select(Name));
    }

    /// <summary>"range only, volatility up to the 66th percentile".</summary>
    public static string DescribeFilter(RegimeFilter regime, int minRank, int maxRank)
    {
        var parts = new List<string>();
        if (DescribeRegimes(MarketContextTracker.SetOf(regime)) is { } r) parts.Add(r);
        if (minRank > 0 && maxRank < 100) parts.Add($"volatility between percentiles {minRank} and {maxRank}");
        else if (maxRank < 100) parts.Add(maxRank <= 33 ? "low volatility" : $"volatility up to percentile {maxRank}");
        else if (minRank > 0) parts.Add(minRank >= 67 ? "high volatility" : $"volatility from percentile {minRank}");
        return string.Join(", ", parts);
    }

    /// <summary>Number out of a parameter value (the ones loaded from JSON come as JsonElement).</summary>
    public static double? ToDouble(object? v) => v switch
    {
        null => null,
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.String } je when double.TryParse(je.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        JsonElement => null,
        bool => null,
        IConvertible c when double.TryParse(Convert.ToString(c, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    /// <summary>
    /// Instability of the parameters each walk-forward window chose: standard deviation of the chosen values over the
    /// width of the range tested, averaged across parameters (0 = always the same; more than ~0.35 = they jump all over the range).
    /// </summary>
    public static double? ParameterInstability(IReadOnlyList<WalkForwardWindow> windows, IReadOnlyList<ParameterRange>? ranges)
    {
        if (windows.Count < 2) return null;
        var names = ranges is { Count: > 0 }
            ? ranges.Where(r => r.Enabled && r.Max > r.Min).Select(r => r.Name).ToList()
            : windows.SelectMany(w => w.BestParameters.Keys).Distinct().ToList();
        var scores = new List<double>();
        foreach (var name in names)
        {
            var range = ranges?.FirstOrDefault(r => r.Name == name);
            var values = windows.Select(w => w.BestParameters.TryGetValue(name, out var v) ? range?.Numeric(v) ?? ToDouble(v) : null).Where(v => v != null).Select(v => v!.Value).ToList();
            if (values.Count < 2) continue;
            double mean = values.Average();
            double sd = Math.Sqrt(values.Sum(x => (x - mean) * (x - mean)) / values.Count);
            double width = range != null ? range.Max - range.Min : 0;
            if (width > 0) scores.Add(sd / width);
            else if (Math.Abs(mean) > 1e-12) scores.Add(Math.Min(1, sd / Math.Abs(mean)));
        }

        return scores.Count == 0 ? 0 : scores.Average();
    }

    /// <summary>
    /// Plateau around the best combination: of its neighbours (one step away in each parameter), what share makes
    /// money. An isolated peak (the neighbours lose) is usually overfitting.
    /// </summary>
    public static (double ProfitableShare, int Neighbors)? Plateau(OptimizationResult opt)
    {
        if (opt.Rows.Count < 3 || opt.Best is not { } best) return null;
        var ranges = opt.Ranges.Where(r => r.Enabled && r.Step > 0 && r.Max > r.Min).ToList();
        if (ranges.Count == 0) return null;
        int neighbors = 0, profitable = 0;
        foreach (var row in opt.Rows.Skip(1))
        {
            bool near = true, same = true;
            foreach (var r in ranges)
            {
                double? a = best.Parameters.TryGetValue(r.Name, out var av) ? r.Numeric(av) : null;
                double? b = row.Parameters.TryGetValue(r.Name, out var bv) ? r.Numeric(bv) : null;
                if (a == null || b == null) continue;
                double diff = Math.Abs(a.Value - b.Value);
                if (diff > r.Step * 1.0001) near = false;
                if (diff > 1e-9) same = false;
            }

            if (!near || same) continue;
            neighbors++;
            if (row.Metrics.NetProfit > 0) profitable++;
        }

        return neighbors == 0 ? null : ((double)profitable / neighbors, neighbors);
    }

    /// <summary>Share of the combinations tested that make money (general robustness of the idea).</summary>
    public static double? ProfitableShare(OptimizationResult opt) =>
        opt.Rows.Count == 0 ? null : (double)opt.Rows.Count(r => r.Metrics.NetProfit > 0) / opt.Rows.Count;
}

/// <summary>Result of one validation check.</summary>
public enum GateStatus
{
    Pass,
    Warn,
    Fail,
}

/// <summary>One validation check with its explanation.</summary>
public sealed record GateCheck(string Name, GateStatus Status, string Text);

/// <summary>What is asked of a strategy validated with walk-forward.</summary>
public sealed class GateInput
{
    public required WalkForwardResult WalkForward { get; init; }
    public OptimizationResult? Optimization { get; init; }

    /// <summary>Configurations (strategy x market) already tested, counting this one.</summary>
    public int Trials { get; init; } = 1;

    /// <summary>Variance of the per-trade Sharpes of those configurations.</summary>
    public double TrialVariance { get; init; }

    public int MinTrades { get; init; } = 30;
}

/// <summary>Result of the trades that entered in a market regime.</summary>
/// <summary>Result in one market condition. Detail: a subgroup of another one (up trend inside trend).</summary>
public sealed record RegimeStats(string Name, int Trades, decimal Net, decimal ProfitFactor, bool Detail = false);

/// <summary>
/// Context filter that would be advisable: in that context the strategy wins clearly and outside it loses. These are the
/// values for its RegimeFilter, MinVolatilityRank and MaxVolatilityRank parameters.
/// </summary>
public sealed record ContextFilterSuggestion(RegimeFilter Regime, int MinVolatilityRank, int MaxVolatilityRank, string Description,
    int Trades, decimal Net, decimal ProfitFactor, int DroppedTrades, decimal DroppedNet);

/// <summary>Verdict of the validation with every check.</summary>
public sealed class GateResult
{
    public GateStatus Status { get; init; }
    public List<GateCheck> Checks { get; init; } = new();
    public double TradeSharpe { get; init; }
    public double Deflated { get; init; }
    public double? Instability { get; init; }
    public (double ProfitableShare, int Neighbors)? Plateau { get; init; }
    public double? ProfitableCombos { get; init; }

    /// <summary>
    /// It does not pass for lack of sample, not for lack of edge: it wins out of sample and the only things failing depend on how many
    /// trades there are (the sample, the deflated Sharpe and, with few trades, consistency and efficiency). With the
    /// same edge per trade it would pass with more trades: before discarding it, more history has to be gathered.
    /// </summary>
    public bool NeedsMoreSample { get; init; }

    /// <summary>How many times more out-of-sample trades it would take to conclude (with the same edge per trade).</summary>
    public double SampleFactor { get; init; } = 1;

    /// <summary>Out-of-sample result by market regime (range, mixed, trend, low and high volatility).</summary>
    public List<RegimeStats> Regimes { get; init; } = new();

    /// <summary>Decay: mean profit per trade of the first and last third, and the t of the slope over time.</summary>
    public decimal DecayEarly { get; init; }
    public decimal DecayLate { get; init; }
    public double DecayT { get; init; }

    public string StatusText => Status switch
    {
        GateStatus.Pass => "pass",
        GateStatus.Warn => "doubtful",
        _ when NeedsMoreSample => "needs more sample",
        _ => "does not pass",
    };
}

/// <summary>
/// Fixed rules against overoptimization (no agent decides them): only the out-of-sample part is judged, with a
/// sufficient sample, walk-forward efficiency, consistency across windows, parameter stability, plateau and a Sharpe
/// deflated by how many things were tried.
/// </summary>
public static class ValidationGate
{
    public const double MinProfitFactor = 1.2, FailProfitFactor = 1.0;
    public const double MinEfficiency = 0.5, FailEfficiency = 0.3;
    public const double MinWindowsPercent = 60, FailWindowsPercent = 40;
    public const int MinWindows = 4;
    public const double MaxInstability = 0.35;
    public const double MinPlateau = 0.5, MinProfitableCombos = 0.25;
    public const double MinDeflated = 0.9, FailDeflated = 0.5;

    /// <summary>How the deflated Sharpe line starts (older ones said "Probability that it is not luck").</summary>
    public const string LuckPassPrefix = "The probability that the edge is real and not luck:", LuckFailPrefix = "It could be luck:";

    /// <summary>Is this the deflated Sharpe line? (the current one or the one of previously stored checks)</summary>
    public static bool IsLuckLine(string text) =>
        text.StartsWith(LuckFailPrefix, StringComparison.Ordinal) || text.StartsWith(LuckPassPrefix, StringComparison.Ordinal) ||
        text.StartsWith("The probability that it is not luck", StringComparison.Ordinal);

    /// <summary>How many times more trades it is worth going to look for so the deflated Sharpe passes.</summary>
    public const double MaxSampleFactor = 8;

    /// <summary>Checks that depend on the sample size (with few trades, and with enough).</summary>
    private static readonly HashSet<string> SmallSampleChecks = new() { "Sample", "Deflated Sharpe", "Consistency", "Efficiency", "Windows", "Overfitting", "Paths" };

    /// <summary>Overfitting (PBO): the chosen one loses out of sample in under 20% of the partitions (passes) or in half or more (fails).</summary>
    public const double MaxPboLoss = 0.2, FailPboLoss = 0.5;

    /// <summary>Share of CPCV paths that have to win (and the median has to be positive).</summary>
    public const double MinProfitablePaths = 0.7;
    private static readonly HashSet<string> LargeSampleChecks = new() { "Deflated Sharpe", "Windows" };

    public static GateResult Evaluate(GateInput input)
    {
        var wf = input.WalkForward;
        var m = wf.OutSampleMetrics;
        var checks = new List<GateCheck>();
        void Add(string name, GateStatus st, string text) => checks.Add(new GateCheck(name, st, text));
        var fmt = CultureInfo.GetCultureInfo("en-US");

        int trades = m.TotalTrades;
        Add("Sample", trades >= input.MinTrades ? GateStatus.Pass : GateStatus.Fail,
            trades >= input.MinTrades ? $"{trades} out-of-sample trades"
            : trades == 0 ? "It did not trade out of sample (0 trades): its conditions never occurred in the period tested"
            : $"Only {trades} out-of-sample trades (minimum {input.MinTrades}): not enough to conclude anything");
        Add("Result", m.NetProfit > 0 ? GateStatus.Pass : GateStatus.Fail,
            m.NetProfit > 0 ? $"It makes {m.NetProfit.ToString("N2", fmt)} out of sample" : $"It loses out of sample ({m.NetProfit.ToString("N2", fmt)})");
        decimal pf = m.ProfitFactor;
        Add("Profit factor", pf >= (decimal)MinProfitFactor ? GateStatus.Pass : pf >= (decimal)FailProfitFactor ? GateStatus.Warn : GateStatus.Fail,
            $"Out-of-sample PF {(pf >= 999 ? "∞" : pf.ToString("0.00", fmt))} (it should be ≥ {MinProfitFactor.ToString("0.0", fmt)})");
        double wfe = wf.WalkForwardEfficiency;
        Add("Efficiency", wfe >= MinEfficiency ? GateStatus.Pass : wfe >= FailEfficiency ? GateStatus.Warn : GateStatus.Fail,
            wfe >= MinEfficiency ? $"Walk-forward efficiency {wfe.ToString("0.00", fmt)}: out of sample it performs much like the optimized run"
            : wfe >= FailEfficiency ? $"Walk-forward efficiency {wfe.ToString("0.00", fmt)}: it loses a fair amount out of sample (it should be ≥ {MinEfficiency.ToString("0.0", fmt)})"
            : $"Walk-forward efficiency {wfe.ToString("0.00", fmt)}: overoptimized (the optimized result does not repeat out of sample)");
        double win = wf.ProfitableWindowsPercent;
        Add("Consistency", win >= MinWindowsPercent ? GateStatus.Pass : win >= FailWindowsPercent ? GateStatus.Warn : GateStatus.Fail,
            $"{win.ToString("0", fmt)}% of the out-of-sample windows win (it should be ≥ {MinWindowsPercent}%)");
        Add("Windows", wf.Windows.Count >= MinWindows ? GateStatus.Pass : GateStatus.Warn,
            wf.Windows.Count >= MinWindows ? $"{wf.Windows.Count} walk-forward windows" : $"Only {wf.Windows.Count} windows: little evidence that it repeats over time");

        double? instability = Robustness.ParameterInstability(wf.Windows, wf.Ranges);
        if (instability is { } inst)
            Add("Stability", inst <= MaxInstability ? GateStatus.Pass : GateStatus.Warn,
                inst <= MaxInstability ? $"The chosen parameters are stable across windows (spread {inst.ToString("0.00", fmt)})"
                : $"The chosen parameters jump all over the range across windows (spread {inst.ToString("0.00", fmt)}): the edge depends on tuning them");

        (double, int)? plateau = null;
        double? profitableCombos = null;
        if (input.Optimization is { } opt)
        {
            plateau = Robustness.Plateau(opt);
            if (plateau is { } p)
                Add("Plateau", p.Item1 >= MinPlateau ? GateStatus.Pass : GateStatus.Warn,
                    p.Item1 >= MinPlateau ? $"{p.Item1 * 100:0}% of the combinations neighbouring the best one also win (a plateau)"
                    : $"Only {p.Item1 * 100:0}% of the best combination's neighbours win: an isolated peak, typical of overfitting");
            profitableCombos = Robustness.ProfitableShare(opt);
            if (profitableCombos is { } pc)
                Add("Robustness", pc >= MinProfitableCombos ? GateStatus.Pass : GateStatus.Warn,
                    $"{pc * 100:0}% of every combination tested makes money");
        }

        // overfitting (Bailey, Borwein, Lopez de Prado and Zhu): the best combination is picked on half of the history and
        // the other half is looked at, in every possible way. What decides it is whether the chosen one loses money on the other half; the
        // PBO (whether it lands in the bottom half of the rest) is reported: a high one with everything winning is a plateau, not a problem
        if (wf.Pbo is { } pbo && wf.PboLoss is { } pboLoss && wf.PboSplits > 0)
            Add("Overfitting", pboLoss < MaxPboLoss ? GateStatus.Pass : pboLoss < FailPboLoss ? GateStatus.Warn : GateStatus.Fail,
                (pboLoss < MaxPboLoss
                    ? $"The optimization is not picking luck: the combination that did best on one half of the history made money on the other half in {(1 - pboLoss) * 100:0}% of the {wf.PboSplits} partitions"
                    : pboLoss < FailPboLoss
                        ? $"Some overfitting: the combination that did best on one half of the history lost money on the other half in {pboLoss * 100:0}% of the {wf.PboSplits} partitions (it should be under {MaxPboLoss * 100:0}%)"
                        : $"Overoptimized: the combination that did best on one half of the history lost money on the other half in {pboLoss * 100:0}% of the {wf.PboSplits} partitions; picking the best one is picking luck") +
                $" (among the {wf.Evaluated} combinations tested, it landed in the bottom half {pbo * 100:0}% of the time: PBO)");

        // out-of-sample paths of the purged combinatorial cross-validation: not a single path like the walk-forward
        if (wf.CpcvPaths.Count >= 3)
        {
            var paths = wf.CpcvPaths.OrderBy(p => p.Net).ToList();
            int good = paths.Count(p => p.Net > 0);
            decimal median = paths[paths.Count / 2].Net, worst = paths[0].Net;
            double share = (double)good / paths.Count;
            Add("Paths", median <= 0 ? GateStatus.Fail : share >= MinProfitablePaths ? GateStatus.Pass : GateStatus.Warn,
                $"{good} of {paths.Count} out-of-sample paths win (cross-validation with {wf.CpcvGroups} blocks and {wf.EmbargoDays} day(s) of embargo): " +
                $"the middle one {median.ToString("N0", fmt)}, the worst {worst.ToString("N0", fmt)}" +
                (median <= 0 ? ". With another way of splitting the history, it loses" : share >= MinProfitablePaths ? "" : $" (it should win at least {MinProfitablePaths * 100:0}%)"));
        }

        var (sr, skew, kurt, n) = Robustness.TradeSharpe(wf.OutSampleTrades);
        double dsr = Robustness.DeflatedSharpe(sr, n, skew, kurt, input.Trials, input.TrialVariance);
        double bar = Robustness.DeflatedBenchmark(n, input.Trials, input.TrialVariance);
        // in money, so it is understandable: how much it makes per trade, how much each one varies and how much the luckiest
        // of the no-edge strategies that were tested would show by pure chance, with that same variation
        var pnls = wf.OutSampleTrades.Select(t => (double)t.NetPnl).ToList();
        double mean = pnls.Count > 0 ? pnls.Average() : 0;
        double sd = pnls.Count > 1 ? Math.Sqrt(pnls.Sum(x => (x - mean) * (x - mean)) / (pnls.Count - 1)) : 0;
        string money = n >= 2 && sd > 0
            ? $". On average it makes {mean.ToString("N0", fmt)} per trade, but each trade varies by about ±{sd.ToString("N0", fmt)}: among {input.Trials} strategies with no edge at all" +
              $"(the ones that were tested), the luckiest one usually shows ~{(bar * sd).ToString("N0", fmt)} per trade with that variation"
            : "";
        Add("Deflated Sharpe", dsr >= MinDeflated ? GateStatus.Pass : dsr >= FailDeflated ? GateStatus.Warn : GateStatus.Fail,
            dsr >= MinDeflated
                ? $"{LuckPassPrefix} {dsr * 100:0}% (it should be ≥ {MinDeflated * 100:0}%, discounting the {input.Trials} strategies that were tested){money}"
                : $"{LuckFailPrefix} only a {dsr * 100:0}% probability that the edge is real (it should be ≥ {MinDeflated * 100:0}%){money}" +
                  (dsr < FailDeflated ? ". With this it cannot be told apart from chance" : ""));

        // edge decay: with 30 trades or more, does the last third perform like the first?
        var (early, late, tDecay, _) = Robustness.EdgeDecay(wf.OutSampleTrades);
        if (trades >= 30)
        {
            bool dying = early > 0 && late < 0 && tDecay <= -2;
            bool fading = (early > 0 && late < early * 0.5m) || tDecay <= -1.5;
            Add("Edge decay", dying ? GateStatus.Fail : fading ? GateStatus.Warn : GateStatus.Pass,
                dying ? $"The edge is fading: the last third of the trades loses ({late.ToString("N2", fmt)} per trade) while the first third was making {early.ToString("N2", fmt)} (trend against it, t {tDecay.ToString("0.0", fmt)})"
                : fading ? $"The edge is shrinking over time: the last third of the trades yields {late.ToString("N2", fmt)} per trade against {early.ToString("N2", fmt)} of the first third"
                : $"The edge holds up over time: the last third of the trades yields {late.ToString("N2", fmt)} per trade (the first third {early.ToString("N2", fmt)})");
        }

        // market regimes: how it does in a range, in a trend and with low or high volatility (groups with a sample)
        var regimes = Robustness.Regimes(wf.OutSampleTrades);
        int minGroup = Math.Max(8, trades * 15 / 100);
        var judged = regimes.Where(r => !r.Detail && r.Trades >= minGroup).ToList();
        if (judged.Count >= 2)
        {
            string Line(RegimeStats r) => $"{r.Name} ({r.Trades} trades, PF {(r.ProfitFactor >= 999 ? "∞" : r.ProfitFactor.ToString("0.00", fmt))})";
            var losing = judged.Where(r => r.Net < 0).ToList();
            var winning = judged.Except(losing).ToList();
            Add("Regimes", losing.Count == 0 ? GateStatus.Pass : GateStatus.Warn,
                losing.Count == 0 ? $"It wins in every market condition with a sample: {string.Join(", ", judged.Select(Line))}"
                : winning.Count == 0 ? $"It loses in every market condition: {string.Join(", ", losing.Select(Line))}"
                : $"It depends on the regime: it loses in {string.Join(", ", losing.Select(Line))} and wins in {string.Join(", ", winning.Select(Line))}. A regime filter is advisable");
        }

        var status = checks.Any(c => c.Status == GateStatus.Fail) ? GateStatus.Fail : checks.Any(c => c.Status == GateStatus.Warn) ? GateStatus.Warn : GateStatus.Pass;
        var (needs, factor) = status == GateStatus.Pass ? (false, 1.0)
            : SampleNeed(checks.Select(c => (c.Name, c.Status)), m.NetProfit, trades, input.MinTrades, sr, skew, kurt, input.Trials, input.TrialVariance, dsr);
        return new GateResult
        {
            Status = status,
            Checks = checks,
            TradeSharpe = sr,
            Deflated = dsr,
            Instability = instability,
            Plateau = plateau,
            ProfitableCombos = profitableCombos,
            NeedsMoreSample = needs,
            SampleFactor = factor,
            Regimes = regimes,
            DecayEarly = early,
            DecayLate = late,
            DecayT = tDecay,
        };
    }

    /// <summary>
    /// Is it short on sample or short on edge? Only if it wins out of sample and everything that fails depends on the sample
    /// size. With 10 trades or more the deflated Sharpe is projected with the same edge per trade: if not even with
    /// <see cref="MaxSampleFactor"/> times more trades it would pass, it is not the sample, it is that it cannot be told apart from chance.
    /// With fewer than 10 the Sharpe says nothing yet: it is enough that it wins.
    /// </summary>
    public static (bool Needs, double Factor) SampleNeed(IEnumerable<(string Name, GateStatus Status)> checks, decimal net, int trades, int minTrades,
                                                         double sr, double skew, double kurt, int trials, double trialVariance, double dsr)
    {
        if (net <= 0 || trades <= 0 || (trades >= minTrades && dsr >= MinDeflated)) return (false, 1);
        var bound = trades < minTrades ? SmallSampleChecks : LargeSampleChecks;
        if (checks.Any(c => c.Status == GateStatus.Fail && !bound.Contains(c.Name))) return (false, 1);
        double factor = Math.Max(1, (double)minTrades / trades);
        if (trades < 10 || sr <= 0) return (true, Math.Round(factor, 2));
        double f = factor;
        while (Robustness.DeflatedSharpe(sr, (int)Math.Ceiling(trades * f), skew, kurt, trials, trialVariance) < MinDeflated)
        {
            f *= 1.15;
            if (f > MaxSampleFactor * factor) return (false, 1);
        }

        return (true, Math.Round(f, 2));
    }
}
