using System.Text;
using System.Text.Json;
using TradingHub.Indicators.Native;

namespace TradingHub.App.Services;

/// <summary>
/// "What did I know then?": rebuilds, for any past moment, the big-trade levels that existed and price had not come back to,
/// the signals and alerts of the hours before, the journal notes already written, and the events around it. Only things
/// stamped before that moment are used, so it cannot show hindsight.
/// </summary>
internal static class AsOf
{
    private sealed record MarkRec(string Kind, DateTime Time, decimal A, decimal B, bool Flag);

    public static string Build(DateTime localMoment)
    {
        var utc = localMoment.ToUniversalTime();
        var sb = new StringBuilder($"AS OF {localMoment:dddd dd/MM/yyyy HH:mm} (local)\n\n");

        sb.AppendLine("BIG-TRADE LEVELS PRICE HAD NOT COME BACK TO");
        bool any = false;
        foreach (var (name, rows) in TodayReport.Load())
        {
            if (name.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) continue;
            var known = rows.Where(r => r.Time <= utc).ToList();
            if (known.Count == 0) continue;
            // a level flagged touched without a time cannot be placed in the past: it is left out rather than guessed
            var open = known.Where(r => !r.Hidden && !(r.Touched && (r.TouchTime == default || r.TouchTime <= utc))).OrderByDescending(r => r.Volume).ToList();
            any = true;
            sb.AppendLine($"  {name}: {known.Count} levels existed, {open.Count} not yet touched. Largest:");
            foreach (var r in open.Take(8)) sb.AppendLine($"      {(r.Buy ? "buy " : "sell")} {r.Volume,8:0.##} @ {r.Price}   placed {r.Time.ToLocalTime():dd/MM HH:mm}");
        }

        if (!any) sb.AppendLine("  none saved before that moment");
        sb.AppendLine();

        sb.AppendLine("SIGNALS IN THE 3 HOURS BEFORE (absorption, stacked imbalances, divergences, sweeps)");
        any = false;
        var dir = Path.Combine(AppPaths.Root, "Marks");
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var marks = (JsonSerializer.Deserialize<List<MarkRec>>(File.ReadAllText(f)) ?? new()).Where(m => m.Time <= utc && m.Time >= utc.AddHours(-3)).OrderBy(m => m.Time).ToList();
                    foreach (var m in marks)
                    {
                        any = true;
                        sb.AppendLine($"  {m.Time.ToLocalTime():HH:mm}  {Path.GetFileNameWithoutExtension(f)}  {m.Kind}");
                    }
                }
                catch (Exception) { /* a bad file is left out */ }
            }

        if (!any) sb.AppendLine("  none saved");
        sb.AppendLine();

        sb.AppendLine("ALERTS THAT DAY BEFORE THEN");
        var alerts = AlertLog.OnDay(localMoment.Date).Where(a => a.Time <= localMoment).TakeLast(15).ToList();
        if (alerts.Count == 0) sb.AppendLine("  none logged (alerts are logged from the version that added this feature)");
        foreach (var a in alerts) sb.AppendLine($"  {a.Time:HH:mm:ss}  {a.Text}");
        sb.AppendLine();

        sb.AppendLine("JOURNAL NOTES THAT DAY BEFORE THEN");
        var journal = Path.Combine(TradingHub.Common.Product.Documents, "Journal", $"{localMoment:yyyy-MM-dd}.md");
        var heads = File.Exists(journal)
            ? File.ReadLines(journal).Where(l => l.StartsWith("## ") && TimeSpan.TryParse(l.Length >= 8 ? l.Substring(3, 5) : "", out var t) && t <= localMoment.TimeOfDay).ToList()
            : new List<string>();
        if (heads.Count == 0) sb.AppendLine("  none");
        foreach (var h in heads) sb.AppendLine("  " + h[3..]);
        sb.AppendLine();

        sb.AppendLine("EVENTS WITHIN 2 HOURS EITHER SIDE");
        var events = EventCalendar.All().Where(e => Math.Abs((e.Utc - utc).TotalHours) <= 2).ToList();
        if (events.Count == 0) sb.AppendLine("  none in your events file");
        foreach (var e in events) sb.AppendLine($"  {e.Utc.ToLocalTime():HH:mm}  {e.Name}");
        return sb.ToString();
    }
}
