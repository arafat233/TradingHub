namespace TradingHub.Data;

/// <summary>Which days are missing from a stored dataset between its first and last day.</summary>
public static class DataCoverage
{
    /// <summary>Days with no file between the first and last stored day; for non-24/7 markets Saturdays (always closed) are not counted.</summary>
    /// <summary>
    /// Stored days that are much smaller than usual (a day with the app closed for most of it): the share of a typical day's size, for each day under
    /// <paramref name="below"/>. "Typical" is the median of the same weekday (a Sunday session is always small), or of all days with fewer than 3 of that weekday.
    /// The day <paramref name="today"/> is never flagged: it is still being recorded.
    /// </summary>
    public static Dictionary<DateOnly, double> ThinDays(IReadOnlyList<(DateOnly Day, long Bytes)> days, DateOnly today, double below = 0.35)
    {
        static double Median(List<long> v) { v.Sort(); return v.Count == 0 ? 0 : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2.0; }
        double all = Median(days.Select(d => d.Bytes).ToList());
        var res = new Dictionary<DateOnly, double>();
        foreach (var (day, bytes) in days)
        {
            if (day >= today) continue;
            var same = days.Where(d => d.Day.DayOfWeek == day.DayOfWeek).Select(d => d.Bytes).ToList();
            double typical = same.Count >= 3 ? Median(same) : all;
            if (typical > 0 && bytes < typical * below) res[day] = bytes / typical;
        }

        return res;
    }

    public static List<DateOnly> MissingDays(IEnumerable<DateOnly> have, bool always24x7)
    {
        var set = have.ToHashSet();
        var missing = new List<DateOnly>();
        if (set.Count == 0) return missing;
        for (var d = set.Min(); d <= set.Max(); d = d.AddDays(1))
            if (!set.Contains(d) && (always24x7 || d.DayOfWeek != DayOfWeek.Saturday)) missing.Add(d);
        return missing;
    }
}
