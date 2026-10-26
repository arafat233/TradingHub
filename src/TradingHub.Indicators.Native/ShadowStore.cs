using System.Text.Json;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Paper trades of the Big Trades "shadow trader" (Shadow\&lt;instrument&gt;.json): every first return of price to a level is
/// faded with a fixed stop and target, so the levels can be judged by what trading them would have done.
/// </summary>
internal static class ShadowStore
{
    /// <param name="Ticks">Result in ticks (negative = loss).</param>
    /// <param name="Reason">"stop", "target" or "time".</param>
    /// <param name="Live">Opened while the chart was live (false = found when the history was read).</param>
    internal sealed record Rec(DateTime LevelTime, DateTime Entry, DateTime Exit, bool Long, decimal EntryPrice, decimal Ticks, string Reason, bool Live);

    private const int Keep = 5000;

    public static string PathFor(string instrument)
    {
        string name = string.Concat(instrument.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string root = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } d ? d : TradingHub.Common.Product.AppData;
        return Path.Combine(root, "Shadow", name + ".json");
    }

    /// <summary>Adds the new trades; one that is already stored (same level and entry price) is kept as it was.</summary>
    public static void Merge(string instrument, List<Rec> add)
    {
        if (MarkStore.Skip(instrument) || add.Count == 0) return;
        try
        {
            var path = PathFor(instrument);
            var all = File.Exists(path) ? JsonSerializer.Deserialize<List<Rec>>(File.ReadAllText(path)) ?? new() : new List<Rec>();
            var have = all.Select(r => (r.LevelTime, r.EntryPrice)).ToHashSet();
            foreach (var r in add)
                if (have.Add((r.LevelTime, r.EntryPrice))) all.Add(r);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(all.OrderByDescending(r => r.Exit).Take(Keep)));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception) { /* disk trouble must not break the chart */ }
    }
}

/// <summary>Appends notes to today's journal file (Documents\Trading Hub\Journal\yyyy-MM-dd.md).</summary>
internal static class JournalWriter
{
    public static void Append(DateTime localNow, string markdown)
    {
        try
        {
            var dir = Path.Combine(TradingHub.Common.Product.Documents, "Journal");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"{localNow:yyyy-MM-dd}.md"), markdown);
        }
        catch (Exception) { /* a note is a nicety */ }
    }
}
