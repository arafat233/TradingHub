using System.Globalization;

namespace TradingHub.Indicators.Native;

/// <summary>
/// The user's own list of market-moving events (CPI, FOMC, NFP...), read from <c>events.txt</c> in the data folder.
/// One event per line: <c>2026-11-04 19:00 FOMC rate decision</c> (the time is UTC; lines starting with # are comments).
/// Nothing is bundled: the dates are for the user to fill in.
/// </summary>
public static class EventCalendar
{
    public sealed record Event(DateTime Utc, string Name);

    private static List<Event> _cache = new();
    private static DateTime _loadedAt = DateTime.MinValue;

    public static string FilePath
    {
        get
        {
            string root = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } d ? d : TradingHub.Common.Product.AppData;
            return Path.Combine(root, "events.txt");
        }
    }

    /// <summary>Creates the file with the format explained if it does not exist yet (and returns its path).</summary>
    public static string EnsureFile()
    {
        var path = FilePath;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path,
                    "# Market-moving events for Trading Hub. One per line, time in UTC:\n" +
                    "#   yyyy-MM-dd HH:mm  Name of the event\n" +
                    "# Example (not a real date):\n" +
                    "#   2026-12-31 13:30  Example: US data release\n" +
                    "# Level alerts are muted around these (Big Trades settings) and the Event markers indicator draws them.\n");
            }
        }
        catch (Exception) { /* the file is optional */ }

        return path;
    }

    /// <summary>The events in the file, sorted by time (re-read at most once a minute).</summary>
    public static IReadOnlyList<Event> All()
    {
        if (DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(1)) return _cache;
        _loadedAt = DateTime.UtcNow;
        var list = new List<Event>();
        try
        {
            if (File.Exists(FilePath))
                foreach (var raw in File.ReadLines(FilePath))
                {
                    var line = raw.Trim();
                    if (line.Length < 16 || line[0] == '#') continue;
                    if (DateTime.TryParseExact(line[..16], "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                        list.Add(new Event(t, line.Length > 16 ? line[16..].Trim() : "Event"));
                }
        }
        catch (Exception) { /* a bad file is the same as no events */ }

        _cache = list.OrderBy(e => e.Utc).ToList();
        return _cache;
    }

    /// <summary>An event that starts within <paramref name="before"/> from now or ended less than <paramref name="after"/> ago.</summary>
    public static Event? Near(DateTime utcNow, TimeSpan before, TimeSpan after) =>
        All().FirstOrDefault(e => e.Utc - utcNow <= before && utcNow - e.Utc <= after);

    public static Event? Next(DateTime utcNow) => All().FirstOrDefault(e => e.Utc >= utcNow);
}
