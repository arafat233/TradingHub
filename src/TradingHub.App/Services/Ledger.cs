using System.Text;
using System.Text.Json;

namespace TradingHub.App.Services;

/// <summary>
/// One page of everything that has been tested: the claim, the data it was tested on, how it came out, and when. Research results are
/// written here as they are produced, so an idea is never tested twice by accident and a result is never remembered better than it was.
/// A supported claim is also locked with a time, so it can be checked later on data recorded after the lock.
/// </summary>
internal static class Ledger
{
    public sealed record Entry(string Id, string Title, string Status, string Headline, string Basis, DateTime Updated);

    private sealed class Store
    {
        public List<Entry> Entries { get; set; } = new();
        public Dictionary<string, DateTime> Locks { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(AppPaths.Root, "research-ledger.json");

    private static Store Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    private static void Save(Store s)
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, true);
    }

    /// <param name="status">"supported", "not supported", "too few cases", "mixed" or "descriptive" (a table of what happened, not a claim).</param>
    public static void Record(string id, string title, string status, string headline, string basis)
    {
        try
        {
            lock (Gate)
            {
                var s = Load();
                s.Entries.RemoveAll(e => e.Id == id);
                s.Entries.Add(new Entry(id, title, status, headline, basis, DateTime.Now));
                Save(s);
            }
        }
        catch (Exception) { /* the ledger is a nicety: a research run must not fail because of it */ }
    }

    /// <summary>The time a claim was locked for forward testing (it is locked now the first time this is asked for).</summary>
    public static DateTime LockTime(string id)
    {
        lock (Gate)
        {
            var s = Load();
            if (!s.Locks.TryGetValue(id, out var t))
            {
                t = DateTime.UtcNow;
                s.Locks[id] = t;
                Save(s);
            }

            return t;
        }
    }

    public static bool IsLocked(string id)
    {
        lock (Gate) return Load().Locks.ContainsKey(id);
    }

    public static string Section()
    {
        var s = Load();
        var sb = new StringBuilder("RESEARCH LEDGER (everything tested so far; the newest run of each test)\n");
        if (s.Entries.Count == 0) return sb.Append("  nothing recorded yet: run Auction claims, Rule lab, Seasonality or the Edge finder").ToString();
        foreach (var group in s.Entries.GroupBy(e => e.Status).OrderBy(g => g.Key == "supported" ? 0 : g.Key == "mixed" ? 1 : g.Key == "not supported" ? 2 : g.Key == "too few cases" ? 3 : 4))
        {
            sb.AppendLine($"  {group.Key.ToUpperInvariant()} ({group.Count()})");
            foreach (var e in group.OrderBy(e => e.Title)) sb.AppendLine($"    {e.Title}: {e.Headline}  [{e.Basis}; {e.Updated:dd/MM/yyyy}]");
        }

        if (s.Locks.Count > 0)
            sb.AppendLine("  Locked for forward testing: " + string.Join(", ", s.Locks.Select(l => $"{l.Key} since {l.Value.ToLocalTime():dd/MM/yyyy}")));
        return sb.ToString().TrimEnd();
    }
}
