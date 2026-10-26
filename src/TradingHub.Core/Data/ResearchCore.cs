using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>How well the "tick rule" (up-tick = buy, down-tick = sell, same price = same side as before) guesses the aggressor of trades whose real side is known.</summary>
public sealed record TickRuleResult(int Trades, double Accuracy, double AccuracyWhenPriceMoved, double AccuracyWhenUnchanged, int Orders, double OrderAccuracy, int BigOrders, double BigOrderAccuracy);

public static class TickRuleAudit
{
    /// <summary>
    /// Runs the tick rule over trades that carry their true side and counts how often it agrees, per trade and per order (fills with
    /// the same time and side added up, judged by the volume-weighted vote of their guessed sides), and for the largest 1% of orders.
    /// </summary>
    public static TickRuleResult Evaluate(IReadOnlyList<Trade> trades)
    {
        int total = 0, right = 0, moved = 0, movedRight = 0, same = 0, sameRight = 0;
        var orders = new List<(bool Correct, decimal Volume)>();
        decimal last = 0;
        var guess = TradeSide.Buy;
        long curTicks = long.MinValue;
        var curSide = TradeSide.None;
        decimal vote = 0, vol = 0;

        void Finish()
        {
            if (vol <= 0 || curSide == TradeSide.None) return;
            orders.Add(((vote > 0) == (curSide == TradeSide.Buy), vol));
        }

        foreach (var t in trades)
        {
            if (t.Side == TradeSide.None) continue;
            bool changed = last != 0 && t.Price != last;
            if (last != 0 && t.Price > last) guess = TradeSide.Buy;
            else if (last != 0 && t.Price < last) guess = TradeSide.Sell;
            last = t.Price;
            total++;
            bool ok = guess == t.Side;
            if (ok) right++;
            if (changed) { moved++; if (ok) movedRight++; }
            else { same++; if (ok) sameRight++; }

            if (t.Time.Ticks != curTicks || t.Side != curSide)
            {
                Finish();
                curTicks = t.Time.Ticks;
                curSide = t.Side;
                vote = vol = 0;
            }

            vote += (guess == TradeSide.Buy ? 1 : -1) * t.Volume;
            vol += t.Volume;
        }

        Finish();
        double Share(int a, int b) => b == 0 ? 0 : (double)a / b;
        var big = orders.OrderByDescending(o => o.Volume).Take(Math.Max(1, orders.Count / 100)).ToList();
        return new TickRuleResult(total, Share(right, total), Share(movedRight, moved), Share(sameRight, same), orders.Count,
            orders.Count == 0 ? 0 : orders.Count(o => o.Correct) / (double)orders.Count, big.Count, big.Count == 0 ? 0 : big.Count(o => o.Correct) / (double)big.Count);
    }
}

/// <summary>Results of the risk simulation of a series of trade results (in ticks).</summary>
public sealed record RiskResult(int Trades, int Paths, double DrawdownMedian, double Drawdown95, double EndP5, double EndMedian, double EndP95, double ProbabilityOfLoss);

public static class RiskSim
{
    /// <summary>
    /// Shuffles the known results into many possible sequences of <paramref name="trades"/> trades (bootstrap: each trade is drawn at random,
    /// with replacement) and reports how deep the worst fall from a peak got, and where the total ended. It shows what a bad stretch can look
    /// like with the same trades, which one lucky or unlucky order hides.
    /// </summary>
    public static RiskResult Bootstrap(IReadOnlyList<double> results, int trades, int paths = 5000, int seed = 1)
    {
        var rng = new Random(seed);
        var draws = new double[paths];
        var ends = new double[paths];
        for (int p = 0; p < paths; p++)
        {
            double sum = 0, peak = 0, dd = 0;
            for (int i = 0; i < trades; i++)
            {
                sum += results[rng.Next(results.Count)];
                peak = Math.Max(peak, sum);
                dd = Math.Max(dd, peak - sum);
            }

            draws[p] = dd;
            ends[p] = sum;
        }

        Array.Sort(draws);
        Array.Sort(ends);
        double Q(double[] a, double q) => a[Math.Clamp((int)Math.Round(q * (a.Length - 1)), 0, a.Length - 1)];
        return new RiskResult(trades, paths, Q(draws, 0.5), Q(draws, 0.95), Q(ends, 0.05), Q(ends, 0.5), Q(ends, 0.95), ends.Count(e => e < 0) / (double)paths);
    }

    /// <summary>The latest <paramref name="recent"/> results against the ones before them: the means and Welch's t statistic of the difference (negative = recent is worse).</summary>
    public static (double Recent, double Prior, double T) Decay(IReadOnlyList<double> chronological, int recent = 50)
    {
        if (chronological.Count < recent + 20) return (0, 0, 0);
        var a = chronological.Skip(chronological.Count - recent).ToList();
        var b = chronological.Take(chronological.Count - recent).ToList();
        double ma = a.Average(), mb = b.Average();
        double va = a.Sum(x => (x - ma) * (x - ma)) / (a.Count - 1), vb = b.Sum(x => (x - mb) * (x - mb)) / (b.Count - 1);
        double se = Math.Sqrt(va / a.Count + vb / b.Count);
        return (ma, mb, se <= 1e-12 ? 0 : (ma - mb) / se);
    }
}

/// <summary>How far price moved after a scheduled event, against how far it usually moves in the same time.</summary>
public static class EventImpact
{
    /// <summary>Signed move in ticks from one minute before the event to each horizon after it (null where the ticks do not cover the event).</summary>
    public static double[]? Measure(long[] times, long[] prices, DateTime eventUtc, int[] minutes)
    {
        long? p0 = PriceAt(times, prices, eventUtc.AddMinutes(-1).Ticks);
        if (p0 == null) return null;
        var result = new double[minutes.Length];
        for (int i = 0; i < minutes.Length; i++)
        {
            long? p1 = PriceAt(times, prices, eventUtc.AddMinutes(minutes[i]).Ticks);
            if (p1 == null) return null;
            result[i] = p1.Value - p0.Value;
        }

        return result;
    }

    /// <summary>The median absolute move over a window of <paramref name="minutes"/> at random times in the data: what "normal" looks like.</summary>
    public static double TypicalAbsMove(long[] times, long[] prices, int minutes, int samples = 400, int seed = 3)
    {
        if (times.Length < 10) return 0;
        var rng = new Random(seed);
        long span = times[^1] - times[0] - TimeSpan.FromMinutes(minutes).Ticks;
        if (span <= 0) return 0;
        var moves = new List<double>();
        for (int i = 0; i < samples; i++)
        {
            long t = times[0] + (long)(rng.NextDouble() * span);
            long? a = PriceAt(times, prices, t), b = PriceAt(times, prices, t + TimeSpan.FromMinutes(minutes).Ticks);
            if (a != null && b != null) moves.Add(Math.Abs(b.Value - a.Value));
        }

        moves.Sort();
        return moves.Count == 0 ? 0 : moves[moves.Count / 2];
    }

    private static long? PriceAt(long[] times, long[] prices, long ticks)
    {
        int i = Array.BinarySearch(times, ticks);
        if (i < 0) i = ~i - 1;
        if (i < 0 || ticks - times[i] > TimeSpan.TicksPerMinute * 5) return null; // no trade for five minutes: the price is not known
        return prices[i];
    }
}
