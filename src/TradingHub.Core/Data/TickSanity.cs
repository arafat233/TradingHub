using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>A tick that jumped away and straight back (a bad print), or a long silence in the tape.</summary>
public sealed record TickProblem(string Kind, DateTime Time, string Detail);

/// <summary>
/// Looks for bad data in a tick series: single-tick spikes (price jumps far and returns on the very next tick) and long gaps.
/// <see cref="Clean"/> returns the series without the spikes, so levels and results are not built on bad prints.
/// </summary>
public static class TickSanity
{
    /// <param name="spikeTicks">A jump must be at least this many ticks; 0 = decide from the data (30 times the average move, at least 10 ticks).</param>
    public static (List<Trade> Clean, List<TickProblem> Problems) Scan(IReadOnlyList<Trade> trades, decimal tickSize, int spikeTicks = 0, TimeSpan? gap = null)
    {
        var problems = new List<TickProblem>();
        var clean = new List<Trade>(trades.Count);
        if (trades.Count < 3 || tickSize <= 0) return (trades.ToList(), problems);
        gap ??= TimeSpan.FromMinutes(10);
        if (spikeTicks <= 0)
        {
            double sum = 0;
            for (int i = 1; i < trades.Count; i++) sum += (double)(Math.Abs(trades[i].Price - trades[i - 1].Price) / tickSize);
            spikeTicks = (int)Math.Max(10, 30 * sum / (trades.Count - 1));
        }

        // a bad print is also a big move in price terms (at least 5 basis points): on an instrument whose tick is a tiny part of the price,
        // ordinary bounces of a few ticks are not errors
        decimal limit = Math.Max(spikeTicks * tickSize, trades[trades.Count / 2].Price * 0.0005m);
        clean.Add(trades[0]);
        for (int i = 1; i < trades.Count - 1; i++)
        {
            decimal prev = clean[^1].Price, p = trades[i].Price, next = trades[i + 1].Price;
            bool spike = Math.Abs(p - prev) >= limit && Math.Abs(p - next) >= limit && (p - prev) * (next - p) < 0;
            if (spike) problems.Add(new TickProblem("spike", trades[i].Time, $"{p} between {prev} and {next} ({Math.Abs(p - prev) / tickSize:0} ticks away)"));
            else clean.Add(trades[i]);
            if (trades[i + 1].Time - trades[i].Time >= gap) problems.Add(new TickProblem("gap", trades[i].Time, $"{(trades[i + 1].Time - trades[i].Time).TotalMinutes:0} minutes without a trade"));
        }

        clean.Add(trades[^1]);
        return (clean, problems);
    }
}
