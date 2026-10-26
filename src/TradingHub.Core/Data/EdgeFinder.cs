namespace TradingHub.Data;

/// <summary>
/// One big-order level with what was known about it when price came back, and what fading it (and following the break) paid.
/// </summary>
public sealed record LevelRecord(
    DateTime Time, bool Buy, double SizePercentile, int HourUtc, double Efficiency, double VolatilityTicks,
    double DistanceToPreviousDayTicks, bool AfterSweep, double FadeTicks, double FollowTicks);

/// <summary>One way of cutting the levels (for example "NY session") and how it did on the part of the data used to find it and on the part kept back.</summary>
public sealed record SliceResult(string Name, int Found, double FoundMean, double FoundT, int Kept, double KeptMean, double KeptT, bool Passes);

/// <summary>
/// Looks for conditions under which fading (or following) the levels paid, without fooling itself: the levels are split by time,
/// slices are chosen on the first part, the significance bar is raised for the number of slices tried (Bonferroni), and a slice only
/// "passes" if it also holds, same sign, on the later part it never saw.
/// </summary>
public static class EdgeFinder
{
    public static List<(string Name, Func<LevelRecord, bool> Test)> Slices(IReadOnlyList<LevelRecord> all)
    {
        double[] Cuts(Func<LevelRecord, double> f) => new[] { Quantile(all.Select(f).Where(v => !double.IsNaN(v)).ToList(), 1 / 3.0), Quantile(all.Select(f).Where(v => !double.IsNaN(v)).ToList(), 2 / 3.0) };
        var eff = Cuts(r => r.Efficiency);
        var vol = Cuts(r => r.VolatilityTicks);
        var list = new List<(string, Func<LevelRecord, bool>)>
        {
            ("buy levels", r => r.Buy), ("sell levels", r => !r.Buy),
            ("size: smallest third", r => r.SizePercentile < 1 / 3.0), ("size: middle third", r => r.SizePercentile >= 1 / 3.0 && r.SizePercentile < 2 / 3.0),
            ("size: largest third", r => r.SizePercentile >= 2 / 3.0), ("size: largest tenth", r => r.SizePercentile >= 0.9),
            ("market ranging (low efficiency)", r => r.Efficiency <= eff[0]), ("market mixed", r => r.Efficiency > eff[0] && r.Efficiency < eff[1]),
            ("market trending (high efficiency)", r => r.Efficiency >= eff[1]),
            ("quiet (low volatility)", r => r.VolatilityTicks <= vol[0]), ("normal volatility", r => r.VolatilityTicks > vol[0] && r.VolatilityTicks < vol[1]),
            ("fast (high volatility)", r => r.VolatilityTicks >= vol[1]),
            ("within 20 ticks of the previous day's high/low", r => r.DistanceToPreviousDayTicks <= 20),
            ("far from the previous day's high/low", r => r.DistanceToPreviousDayTicks > 20),
            ("after the previous day's high/low was swept", r => r.AfterSweep), ("no sweep yet", r => !r.AfterSweep),
        };
        for (int h = 0; h < 24; h += 4)
        {
            int from = h;
            list.Add(($"{from:00}:00-{from + 4:00}:00 UTC", r => r.HourUtc >= from && r.HourUtc < from + 4));
        }

        return list;
    }

    /// <summary>Evaluates every slice on the result chosen by <paramref name="result"/> (fade or follow).</summary>
    public static List<SliceResult> Evaluate(IReadOnlyList<LevelRecord> records, Func<LevelRecord, double> result, double keepFraction = 0.4, double alpha = 0.05)
    {
        var ordered = records.OrderBy(r => r.Time).ToList();
        int keptFrom = (int)Math.Round(ordered.Count * (1 - keepFraction));
        var found = ordered.Take(keptFrom).ToList();
        var kept = ordered.Skip(keptFrom).ToList();
        var slices = Slices(ordered);
        double bar = alpha / Math.Max(1, slices.Count); // Bonferroni: the more slices tried, the harder to impress
        var output = new List<SliceResult>();
        foreach (var (name, test) in slices)
        {
            var a = found.Where(test).Select(result).ToList();
            var b = kept.Where(test).Select(result).ToList();
            var (ma, ta) = Stats(a);
            var (mb, tb) = Stats(b);
            bool passes = a.Count >= 30 && b.Count >= 15 && TwoSidedP(ta) < bar && Math.Sign(ma) == Math.Sign(mb) && Math.Abs(tb) >= 1.0;
            output.Add(new SliceResult(name, a.Count, ma, ta, b.Count, mb, tb, passes));
        }

        return output;
    }

    /// <summary>Mean and t statistic (mean over its standard error); 0 when there are fewer than two values.</summary>
    public static (double Mean, double T) Stats(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return (v.Count == 1 ? v[0] : 0, 0);
        double mean = v.Average(), sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / (v.Count - 1));
        return (mean, sd <= 1e-12 ? 0 : mean / (sd / Math.Sqrt(v.Count)));
    }

    /// <summary>Two-sided p value of a t statistic, normal approximation (fine from about 30 values).</summary>
    public static double TwoSidedP(double t) => 2 * (1 - NormalCdf(Math.Abs(t)));

    private static double NormalCdf(double x)
    {
        // Abramowitz and Stegun 7.1.26
        double t = 1 / (1 + 0.2316419 * Math.Abs(x));
        double d = 0.3989423 * Math.Exp(-x * x / 2);
        double p = d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
        return x >= 0 ? 1 - p : p;
    }

    private static double Quantile(List<double> v, double q)
    {
        if (v.Count == 0) return 0;
        v.Sort();
        return v[Math.Clamp((int)Math.Round(q * (v.Count - 1)), 0, v.Count - 1)];
    }
}
