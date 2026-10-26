namespace TradingHub.Data;

/// <summary>What one market's price did after a big order in another (read by Research.RunCrossMarket).</summary>
public sealed record LeadResult(int Seconds, int Orders, double MeanBps, double T);

// NOTE: this file was overwritten by mistake on 2026-10-26 and rebuilt from the compiled assembly (the comments were lost; the code is the same).
public static class CrossMarket
{
    public static List<LeadResult> Study(IReadOnlyList<(DateTime Time, bool Buy)> orders, long[] otherTimes, double[] otherPrices, int[] horizonsSeconds)
    {
        List<LeadResult> list = new List<LeadResult>();
        foreach (int seconds in horizonsSeconds)
        {
            List<double> moves = new List<double>();
            foreach (var order in orders)
            {
                DateTime time = order.Time;
                bool buy = order.Buy;
                double? before = PriceAt(otherTimes, otherPrices, time.Ticks);
                double? after = PriceAt(otherTimes, otherPrices, time.AddSeconds(seconds).Ticks);
                if (before.HasValue && before.GetValueOrDefault() > 0.0 && after.HasValue)
                {
                    moves.Add((after.Value / before.Value - 1.0) * 10000.0 * (double)(buy ? 1 : (-1)));
                }
            }

            var (meanBps, t) = EdgeFinder.Stats(moves);
            list.Add(new LeadResult(seconds, moves.Count, meanBps, t));
        }

        return list;
    }

    private static double? PriceAt(long[] times, double[] prices, long ticks)
    {
        int index = Array.BinarySearch(times, ticks);
        if (index < 0)
        {
            index = ~index - 1;
        }

        if (index < 0 || ticks - times[index] > 600000000)
        {
            return null;
        }

        return prices[index];
    }
}
