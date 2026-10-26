using System.Globalization;
using System.Text;
using TradingHub.Data;

namespace TradingHub.App.Services;

/// <summary>
/// "Find me this day again": for a session (the latest by default), the past sessions that looked most like it at the end of the first hour
/// (where it opened against the previous value, the gap, the size of the first hour, yesterday's range and direction and shape), and what those
/// sessions went on to do, next to how often the same thing happens on any day. Uses the New York 08:00-17:00 sessions of the cached 1-minute
/// candles, like the auction research. It only describes the past: a handful of look-alikes is a hint, never a forecast.
/// </summary>
internal static class SimilarDays
{
    private static readonly string[] FeatureNames = { "gap", "open vs previous value", "first hour size", "previous range", "previous direction", "previous trend day" };

    public static string Find(string? symbol, DateOnly? day, int count)
    {
        count = Math.Clamp(count, 3, 40);
        var sb = new StringBuilder();
        foreach (var s in ResearchHistory.Load())
        {
            if (!string.IsNullOrWhiteSpace(symbol) && !s.Symbol.Equals(symbol.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var sessions = ResearchAuction.Sessions(s, out var size);
            if (sessions.Count < 60) continue;
            // features need a previous session; row i of the table is session i + 1
            var rows = new List<double[]>();
            var ids = new List<int>();
            for (int i = 1; i < sessions.Count; i++)
            {
                var prev = sessions[i - 1];
                var cur = sessions[i];
                if (cur.DayIndex - prev.DayIndex > 3) continue;
                decimal prevRange = Math.Max(s.Tick, prev.High - prev.Low);
                decimal vah = prev.R.VolumeVah + size, val = prev.R.VolumeVal;
                double ib = (double)((cur.R.IbHigh + size - cur.R.IbLow) / s.Tick);
                rows.Add(new[]
                {
                    (double)((cur.Open - prev.Close) / prevRange),
                    cur.Open > vah ? 1.0 : cur.Open < val ? -1.0 : 0.0,
                    Math.Log(Math.Max(1, ib)),
                    Math.Log((double)(prevRange / s.Tick)),
                    (double)((prev.Close - prev.Open) / prevRange),
                    prev.R.Shape == "Trend" ? 1.0 : 0.0,
                });
                ids.Add(i);
            }

            int target = day is { } d ? ids.FindIndex(i => sessions[i].Day == d) : ids.Count - 1;
            if (target < 1)
            {
                sb.AppendLine($"{s.Symbol}: no session for {day} with enough history before it.");
                continue;
            }

            var near = SessionMatcher.Nearest(rows, target, count);
            var tgt = sessions[ids[target]];
            decimal tick = s.Tick;
            sb.AppendLine($"{s.Symbol} · session {tgt.Day:yyyy-MM-dd} · compared with {target} earlier sessions (New York 08:00-17:00)");
            sb.AppendLine("  features of the target (known after the first hour): " + string.Join(", ", FeatureNames.Select((n, j) => $"{n} {rows[target][j].ToString("0.##", CultureInfo.InvariantCulture)}")));
            sb.AppendLine($"  the {near.Count} most similar days:");
            foreach (var m in near)
            {
                var o = sessions[ids[m.Index]];
                sb.AppendLine($"    {o.Day:yyyy-MM-dd} (distance {m.Distance:0.0}) → {Outcome(o, tick)}");
            }

            var similar = near.Select(m => sessions[ids[m.Index]]).ToList();
            var all = Enumerable.Range(0, target).Select(i => sessions[ids[i]]).ToList();
            sb.AppendLine("  what happened afterwards, similar days vs every day:");
            foreach (var (label, test) in Questions())
            {
                int a = similar.Count(test), b = all.Count(test);
                var (lo, hi) = AuctionStats.Wilson(a, similar.Count);
                sb.AppendLine($"    {label}: {100.0 * a / similar.Count:0}% of look-alikes (95% range {100 * lo:0}-{100 * hi:0}%) vs {100.0 * b / all.Count:0}% of all days");
            }

            double medRange = Median(all.Select(x => (double)((x.High - x.Low) / tick)));
            double simRange = Median(similar.Select(x => (double)((x.High - x.Low) / tick)));
            sb.AppendLine($"    median range: {simRange:0} ticks for look-alikes vs {medRange:0} for all days");
            sb.AppendLine($"  Read it as a hint: with {similar.Count} days the ranges above are wide, and the matching only uses what is known after the first hour.");
        }

        if (sb.Length == 0) sb.AppendLine("No cached 1-minute history with enough sessions (open a chart of the instrument with a long history first).");
        return sb.ToString();
    }

    private static IEnumerable<(string, Func<ResearchAuction.Sess, bool>)> Questions()
    {
        yield return ("extended the first hour's range up", x => x.R.ExtendedUp);
        yield return ("extended it down", x => x.R.ExtendedDown);
        yield return ("stayed inside the first hour's range", x => !x.R.ExtendedUp && !x.R.ExtendedDown);
        yield return ("closed in the top third", x => x.High > x.Low && (x.Close - x.Low) / (x.High - x.Low) >= 0.67m);
        yield return ("closed in the bottom third", x => x.High > x.Low && (x.Close - x.Low) / (x.High - x.Low) <= 0.33m);
        yield return ("trend day", x => x.R.Shape == "Trend");
    }

    private static string Outcome(ResearchAuction.Sess o, decimal tick)
    {
        decimal pos = o.High > o.Low ? (o.Close - o.Low) / (o.High - o.Low) : 0.5m;
        string ext = o.R.ExtendedUp && o.R.ExtendedDown ? "extended both ways" : o.R.ExtendedUp ? "extended up" : o.R.ExtendedDown ? "extended down" : "stayed in the first hour";
        return $"shape {o.R.Shape}, {ext}, range {(o.High - o.Low) / tick:0} ticks, closed at {100 * pos:0}% of the range";
    }

    private static double Median(IEnumerable<double> v)
    {
        var l = v.OrderBy(x => x).ToList();
        return l.Count == 0 ? 0 : l[l.Count / 2];
    }
}
