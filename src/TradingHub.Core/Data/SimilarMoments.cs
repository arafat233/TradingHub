namespace TradingHub.Data;

/// <summary>
/// "Have we seen this before?": describes every candle with a few numbers (see <see cref="Features"/>), finds the earlier
/// candles that look most like the latest one, and reports what price did after each of them. A description of the past,
/// not a forecast: the sample is small and the market changes.
/// </summary>
public static class SimilarMoments
{
    /// <summary>Candles needed before the first one that can be described.</summary>
    public const int Warmup = 70;

    public sealed record Match(int Index, double Distance, decimal AheadTicks);

    public sealed record Result(List<Match> Matches, int Horizon)
    {
        public int Up => Matches.Count(m => m.AheadTicks > 0);
        public int Down => Matches.Count(m => m.AheadTicks < 0);
        public decimal Median => Matches.Count == 0 ? 0 : Matches.Select(m => m.AheadTicks).OrderBy(v => v).ElementAt(Matches.Count / 2);
    }

    /// <summary>
    /// Per candle: the move over the last 10 candles in units of the average range, the share of that volume that was
    /// net aggressive buying (delta), the recent volume against the longer average, the recent range against the average
    /// range, and where the close sits in the last 60 candles' range. Null for candles too early to describe.
    /// </summary>
    public static double[]?[] Features(IReadOnlyList<Candle> c)
    {
        int n = c.Count;
        var f = new double[]?[n];
        for (int i = Warmup - 1; i < n; i++)
        {
            double atr = 0, vol60 = 0, hi60 = double.MinValue, lo60 = double.MaxValue;
            for (int k = i - 59; k <= i; k++)
            {
                atr += c[k].HighTicks - c[k].LowTicks;
                vol60 += (double)c[k].Volume;
                hi60 = Math.Max(hi60, c[k].HighTicks);
                lo60 = Math.Min(lo60, c[k].LowTicks);
            }

            atr = Math.Max(atr / 60, 1);
            vol60 = Math.Max(vol60 / 60, 1e-9);
            double move = (c[i].CloseTicks - c[i - 10].CloseTicks) / (atr * Math.Sqrt(10));
            double delta = 0, vol10 = 0, vol5 = 0, range5 = 0;
            for (int k = i - 9; k <= i; k++)
            {
                delta += (double)c[k].Delta;
                vol10 += (double)c[k].Volume;
                if (k > i - 5)
                {
                    vol5 += (double)c[k].Volume;
                    range5 += c[k].HighTicks - c[k].LowTicks;
                }
            }

            f[i] = new[]
            {
                move,
                vol10 > 0 ? delta / vol10 : 0,
                Math.Log(Math.Max(vol5 / 5 / vol60, 0.05)),
                Math.Log(Math.Max(range5 / 5 / atr, 0.05)),
                hi60 > lo60 ? (c[i].CloseTicks - lo60) / (hi60 - lo60) : 0.5,
            };
        }

        return f;
    }

    /// <summary>
    /// The <paramref name="k"/> candles most like candle <paramref name="current"/>, among those with a known outcome
    /// (at least <paramref name="horizon"/> candles of future) and not part of the latest stretch itself.
    /// </summary>
    public static Result Find(IReadOnlyList<Candle> candles, int current, int horizon, int k)
    {
        var f = Features(candles);
        var matches = new List<Match>();
        if (current < Warmup - 1 || current >= f.Length || f[current] is not { } now) return new Result(matches, horizon);
        var scale = new double[now.Length];
        // distances in units of each feature's spread, so no single number dominates
        for (int d = 0; d < scale.Length; d++)
        {
            var vals = f.Where(x => x != null).Select(x => x![d]).ToList();
            double mean = vals.Average();
            scale[d] = Math.Max(Math.Sqrt(vals.Sum(v => (v - mean) * (v - mean)) / vals.Count), 1e-9);
        }

        var all = new List<(int I, double Dist)>();
        for (int i = Warmup - 1; i + horizon < candles.Count && i < current - horizon; i++)
        {
            if (f[i] is not { } g) continue;
            double dist = 0;
            for (int d = 0; d < g.Length; d++) dist += Math.Pow((g[d] - now[d]) / scale[d], 2);
            all.Add((i, Math.Sqrt(dist)));
        }

        // matches within a few candles of each other are the same moment: keep the closest of each stretch
        foreach (var (i, dist) in all.OrderBy(a => a.Dist))
        {
            if (matches.Count >= k) break;
            if (matches.Any(m => Math.Abs(m.Index - i) < 15)) continue;
            matches.Add(new Match(i, dist, candles[i + horizon].CloseTicks - candles[i].CloseTicks));
        }

        return new Result(matches, horizon);
    }
}
