using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>What <see cref="DataQuality.Check"/> found in one day of ticks.</summary>
public sealed record QualityReport(int Ticks, int OutOfOrder, int Duplicates, int BadVolume, int OffGrid, int Spikes, int WrongDay, IReadOnlyList<(DateTime From, DateTime To)> Gaps)
{
    /// <summary>Problems <see cref="DataQuality.Clean"/> removes. Off-grid prices and gaps are only reported.</summary>
    public int Fixable => OutOfOrder + Duplicates + BadVolume + Spikes + WrongDay;

    public override string ToString() =>
        $"{Ticks:N0} ticks · {Fixable} to fix (out of order {OutOfOrder}, duplicates {Duplicates}, bad volume {BadVolume}, spikes {Spikes}, wrong day {WrongDay}) · off-grid {OffGrid} · gaps {Gaps.Count}";
}

/// <summary>Scan and clean one day of ticks: bad prints, duplicates, ordering. Gaps are reported, the app refills them from the feed when it loads.</summary>
public static class DataQuality
{
    private const int Half = 5; // a spike is judged against the median of 5 ticks each side

    /// <param name="spikePct">A price this far (as a fraction) from its neighbours' median is a bad print.</param>
    /// <param name="gap">A silence longer than this inside the day is reported (markets with a break will show it too).</param>
    public static QualityReport Check(IReadOnlyList<Trade> trades, Instrument inst, DateOnly day, double spikePct = 0.01, TimeSpan? gap = null)
    {
        var keep = Classify(trades, inst, day, spikePct, out int ooo, out int dup, out int vol, out int grid, out int spk, out int wrongDay);
        var gaps = new List<(DateTime, DateTime)>();
        var limit = gap ?? TimeSpan.FromMinutes(30);
        DateTime? prev = null;
        for (int i = 0; i < trades.Count; i++)
        {
            if (!keep[i]) continue;
            if (prev is { } p && trades[i].Time - p > limit) gaps.Add((p, trades[i].Time));
            prev = trades[i].Time;
        }

        return new QualityReport(trades.Count, ooo, dup, vol, grid, spk, wrongDay, gaps);
    }

    /// <summary>The trades without the fixable problems, sorted by id.</summary>
    public static List<Trade> Clean(IReadOnlyList<Trade> trades, Instrument inst, DateOnly day, double spikePct = 0.01)
    {
        var keep = Classify(trades, inst, day, spikePct, out _, out _, out _, out _, out _, out _);
        var res = new List<Trade>(trades.Count);
        for (int i = 0; i < trades.Count; i++)
            if (keep[i]) res.Add(trades[i]);
        return TickFile.SortAndDedupe(res);
    }

    private static bool[] Classify(IReadOnlyList<Trade> t, Instrument inst, DateOnly day, double spikePct,
        out int outOfOrder, out int dups, out int badVol, out int offGrid, out int spikes, out int wrongDay)
    {
        outOfOrder = dups = badVol = offGrid = spikes = wrongDay = 0;
        int n = t.Count;
        var keep = new bool[n];
        var seen = new HashSet<long>();
        long lastId = long.MinValue;
        var window = new decimal[2 * Half];
        for (int i = 0; i < n; i++)
        {
            var x = t[i];
            keep[i] = true;
            if (DateOnly.FromDateTime(x.Time) != day) { wrongDay++; keep[i] = false; continue; }
            if (x.Volume <= 0 || x.Price <= 0) { badVol++; keep[i] = false; continue; }
            if (!seen.Add(x.Id)) { dups++; keep[i] = false; continue; }
            if (x.Id < lastId) outOfOrder++; // kept: sorting puts it right
            lastId = Math.Max(lastId, x.Id);
            if (inst.TickSize > 0 && x.Price % inst.TickSize != 0) offGrid++;

            int c = 0;
            for (int j = Math.Max(0, i - Half); j <= Math.Min(n - 1, i + Half); j++)
                if (j != i) window[c++] = t[j].Price;
            if (c < 4) continue; // too few neighbours to judge
            Array.Sort(window, 0, c);
            decimal med = window[c / 2];
            if (med > 0 && Math.Abs((double)((x.Price - med) / med)) > spikePct) { spikes++; keep[i] = false; }
        }

        return keep;
    }
}
