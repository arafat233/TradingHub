using System.Text.Json;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Remembers the marks of the Absorption and Stacked imbalances indicators on disk (Marks\&lt;instrument&gt;.json) so the
/// daily recap can count them. Marks are only added, never changed.
/// </summary>
internal static class MarkStore
{
    /// <param name="Kind">"absorption" (A = price, Flag = passive sellers) or "stacked" (A = low, B = high, Flag = buy stack)</param>
    internal sealed record Rec(string Kind, DateTime Time, decimal A, decimal B, bool Flag);

    private const int Keep = 5000;

    public static string PathFor(string instrument)
    {
        string name = string.Concat(instrument.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string root = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } d ? d : TradingHub.Common.Product.AppData;
        return Path.Combine(root, "Marks", name + ".json");
    }

    /// <summary>The simulator makes up a new market every run, so its marks mean nothing.</summary>
    public static bool Skip(string? instrument) => string.IsNullOrEmpty(instrument) || instrument.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase);

    public static void Merge(string instrument, List<Rec> add)
    {
        if (Skip(instrument) || add.Count == 0) return;
        try
        {
            var path = PathFor(instrument);
            var all = File.Exists(path) ? JsonSerializer.Deserialize<List<Rec>>(File.ReadAllText(path)) ?? new() : new List<Rec>();
            var have = all.Select(r => (r.Kind, r.Time, r.A)).ToHashSet();
            foreach (var r in add)
                if (have.Add((r.Kind, r.Time, r.A))) all.Add(r);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(all.OrderByDescending(r => r.Time).Take(Keep)));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception) { /* disk trouble must not break the chart */ }
    }
}
