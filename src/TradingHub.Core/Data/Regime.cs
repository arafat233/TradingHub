namespace TradingHub.Data;

public enum RegimeKind { Range, Mixed, Trend }

/// <summary>
/// Is the market trending or going nowhere? The efficiency ratio of a stretch of closes: the net move divided by the total path
/// walked (1 = a straight line, near 0 = back and forth). Below 0.15 is called a range and above 0.35 a trend, the same cut-offs
/// the strategy context filters use.
/// </summary>
public static class Regime
{
    public const double RangeBelow = 0.15, TrendAbove = 0.35;

    public static RegimeKind KindOf(double efficiency) => efficiency <= RangeBelow ? RegimeKind.Range : efficiency >= TrendAbove ? RegimeKind.Trend : RegimeKind.Mixed;

    /// <summary>Efficiency of the candles that close within <paramref name="window"/> of the last one (0 if there are too few).</summary>
    public static (RegimeKind Kind, double Efficiency, int Bars) Classify(IReadOnlyList<Candle> candles, TimeSpan window)
    {
        if (candles.Count < 10) return (RegimeKind.Mixed, 0, candles.Count);
        var end = candles[^1].Time;
        int from = candles.Count - 1;
        while (from > 0 && end - candles[from - 1].Time <= window) from--;
        if (candles.Count - from < 10) return (RegimeKind.Mixed, 0, candles.Count - from);
        double path = 0;
        for (int i = from + 1; i < candles.Count; i++) path += Math.Abs(candles[i].CloseTicks - candles[i - 1].CloseTicks);
        double er = path <= 0 ? 0 : Math.Abs(candles[^1].CloseTicks - candles[from].CloseTicks) / path;
        return (KindOf(er), er, candles.Count - from);
    }

    /// <summary>One-minute closes of a tick series, with the running total of the minute-to-minute moves (to get any window's efficiency in one step).</summary>
    public sealed class Minutes
    {
        public readonly long[] Start;
        public readonly long[] Close;
        public readonly long[] Path;

        public Minutes(long[] times, long[] prices)
        {
            var start = new List<long>();
            var close = new List<long>();
            long cur = long.MinValue;
            for (int i = 0; i < times.Length; i++)
            {
                long m = times[i] / TimeSpan.TicksPerMinute;
                if (m != cur)
                {
                    start.Add(m);
                    close.Add(prices[i]);
                    cur = m;
                }
                else close[^1] = prices[i];
            }

            Start = start.ToArray();
            Close = close.ToArray();
            Path = new long[Close.Length];
            for (int i = 1; i < Close.Length; i++) Path[i] = Path[i - 1] + Math.Abs(Close[i] - Close[i - 1]);
        }

        /// <summary>Efficiency ratio and average minute move (in ticks) over the <paramref name="minutes"/> before a moment; NaN if the series does not reach back that far.</summary>
        public (double Efficiency, double AverageMove) Before(long timeTicks, int minutes)
        {
            int idx = Array.BinarySearch(Start, timeTicks / TimeSpan.TicksPerMinute);
            if (idx < 0) idx = ~idx - 1;
            if (idx - minutes < 0) return (double.NaN, double.NaN);
            long path = Path[idx] - Path[idx - minutes];
            return (path == 0 ? 0 : Math.Abs(Close[idx] - Close[idx - minutes]) / (double)path, path / (double)minutes);
        }
    }
}
