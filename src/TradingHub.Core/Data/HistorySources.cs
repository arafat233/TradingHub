using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Where each instrument's history comes from. Each source stores its own in {root}/{source}/{symbol}: Rithmic (or
/// Tradovate) whatever it records and downloads from its own server, Databento whatever is bought from it. On reading you pick:
/// <list type="bullet">
/// <item>"rithmic": the combined history (Rithmic's and, for the minutes Rithmic is missing, Databento's);</item>
/// <item>"rithmic-solo": Rithmic's only;</item>
/// <item>"databento": Databento's only.</item>
/// </list>
/// That way whatever was already stored as "rithmic" (backtests, Claude's lab, the charts) still sees the whole history.
/// </summary>
public static class HistorySources
{
    public const string Databento = "databento";

    /// <summary>Suffix for "that connection only", without filling in from Databento (rithmic-solo).</summary>
    public const string OnlySuffix = "-solo";

    /// <summary>The CME futures connections whose history Databento fills in (the same continuous contracts).</summary>
    public static bool FilledByDatabento(string feedId) => feedId is "rithmic" or "tradovate";

    /// <summary>A source's own folder (rithmic-solo -> rithmic).</summary>
    public static string Folder(string feedId) => feedId.EndsWith(OnlySuffix, StringComparison.Ordinal) ? feedId[..^OnlySuffix.Length] : feedId;

    /// <summary>The folders read for a source, in order: the first one wins and the rest fill in what it is missing.</summary>
    public static IReadOnlyList<string> Folders(string feedId)
    {
        if (feedId.EndsWith(OnlySuffix, StringComparison.Ordinal)) return new[] { Folder(feedId) };
        return FilledByDatabento(feedId) ? new[] { feedId, Databento } : new[] { feedId };
    }

    /// <summary>The files of a day that exist, in the order of <see cref="Folders"/>.</summary>
    public static List<string> DayFiles(string root, string feedId, string symbol, DateOnly day)
    {
        var files = new List<string>(2);
        foreach (var folder in Folders(feedId))
        {
            var path = TickFile.DayPath(root, folder, symbol, day);
            if (File.Exists(path) && new FileInfo(path).Length > 16) files.Add(path);
        }

        return files;
    }

    public static bool HasDay(string root, string feedId, string symbol, DateOnly day) => DayFiles(root, feedId, symbol, day).Count > 0;

    /// <summary>The days with data in any of the source's folders, sorted.</summary>
    public static List<DateOnly> AvailableDays(string root, string feedId, string symbol)
    {
        var days = new SortedSet<DateOnly>();
        foreach (var folder in Folders(feedId))
            foreach (var d in TickFile.AvailableDays(root, folder, symbol))
                days.Add(d);
        return days.ToList();
    }

    /// <summary>The tick the instrument was stored with (from the last stored day), or null if there are no files.</summary>
    public static decimal? StoredTickSize(string root, string feedId, string symbol)
    {
        var days = AvailableDays(root, feedId, symbol);
        if (days.Count == 0) return null;
        foreach (var file in DayFiles(root, feedId, symbol, days[^1]))
            if (TickFile.ReadTickSize(file) is { } tick)
                return tick;
        return null;
    }

    /// <summary>
    /// A day's trades, sorted. With more than one folder, the first one wins: from the rest only the
    /// minutes where the first one has no trades at all are taken (for example, the hours the app was closed and did not record).
    /// </summary>
    public static List<Trade> ReadDay(string root, string feedId, string symbol, DateOnly day)
    {
        var files = DayFiles(root, feedId, symbol, day);
        if (files.Count == 0) return new List<Trade>();
        var main = TickFile.SortAndDedupe(TickFile.Read(files[0]));
        if (files.Count == 1) return main;

        var covered = new HashSet<long>();
        foreach (var t in main) covered.Add(t.Time.Ticks / TimeSpan.TicksPerMinute);
        var merged = new List<Trade>(main);
        bool added = false;
        for (int f = 1; f < files.Count; f++)
        {
            var extra = TickFile.SortAndDedupe(TickFile.Read(files[f]));
            var minutes = new HashSet<long>();
            foreach (var t in extra)
            {
                long minute = t.Time.Ticks / TimeSpan.TicksPerMinute;
                if (covered.Contains(minute)) continue;
                merged.Add(t);
                minutes.Add(minute);
                added = true;
            }

            covered.UnionWith(minutes);
        }

        if (!added) return main;

        // each source numbers things its own way: they are sorted by time (stable: at the same time, the winning one first) and
        // renumbered, so the ids keep increasing and do not repeat between sources
        var ordered = merged.Select((t, i) => (t, i)).OrderBy(x => x.t.Time.Ticks).ThenBy(x => x.i).Select(x => x.t).ToList();
        long baseId = Math.Max(0, ordered.Min(t => t.Id));
        for (int i = 0; i < ordered.Count; i++)
        {
            var t = ordered[i];
            ordered[i] = new Trade(baseId + i, t.Time, t.Price, t.Volume, t.Side);
        }

        return ordered;
    }

    /// <summary>
    /// The stored history as it is picked for testing: each folder's own and, for the futures that have history from their
    /// connection and from Databento, also the combined one ("rithmic", marked <see cref="SymbolData.Combined"/>) and the one from that
    /// connection only ("rithmic-solo"). The ones of the same instrument stay together, the combined one first.
    /// </summary>
    public static List<SymbolData> Datasets(string root)
    {
        var stored = HistoricalData.Scan(root);
        var databento = stored.Where(s => s.FeedId == Databento).ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<List<SymbolData>>();
        foreach (var s in stored.Where(s => s.FeedId != Databento))
        {
            if (!FilledByDatabento(s.FeedId) || !databento.TryGetValue(s.Symbol, out var db))
            {
                groups.Add(new List<SymbolData> { s });
                continue;
            }

            var byDay = new SortedDictionary<DateOnly, DayData>();
            foreach (var d in db.Days) byDay[d.Day] = d;
            foreach (var d in s.Days) byDay[d.Day] = byDay.TryGetValue(d.Day, out var other) ? d with { Bytes = d.Bytes + other.Bytes } : d;
            var group = new List<SymbolData>
            {
                new(s.FeedId, s.Symbol, byDay.Values.ToList()) { Combined = true },
                new(s.FeedId + OnlySuffix, s.Symbol, s.Days),
            };
            if (owned.Add(db.Symbol)) group.Add(db);
            groups.Add(group);
        }

        foreach (var db in databento.Values.Where(d => !owned.Contains(d.Symbol))) groups.Add(new List<SymbolData> { db });
        return groups.OrderByDescending(g => g.Max(x => x.Bytes)).SelectMany(g => g).ToList();
    }

    /// <summary>What the source of a set of <see cref="Datasets"/> is called (without the connection's own name).</summary>
    public static string Describe(SymbolData s)
    {
        if (s.FeedId == Databento) return "Databento";
        if (s.FeedId.EndsWith(OnlySuffix, StringComparison.Ordinal)) return "only " + Pretty(Folder(s.FeedId));
        return s.Combined ? $"{Pretty(s.FeedId)} + Databento" : Pretty(s.FeedId);
    }

    private static string Pretty(string feedId) => feedId switch
    {
        "rithmic" => "Rithmic",
        "tradovate" => "Tradovate",
        _ => feedId,
    };
}
