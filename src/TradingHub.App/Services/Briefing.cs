using System.Globalization;
using System.Text;
using Avalonia.Threading;
using TradingHub.Indicators.Native;

namespace TradingHub.App.Services;

/// <summary>
/// A briefing put together at set times (default 08:00 and 13:00 local, off until the user turns it on): the next events,
/// for every open instrument the last price, the recent range and the nearest untouched big-trade levels either side, the
/// level scoreboard and the paper trader's results. It is saved as a file, shown in the Today alerts, and sent to Telegram /
/// read aloud if those are on.
/// </summary>
internal static class Briefing
{
    private static readonly HashSet<string> Fired = new();

    public static void Start(AppServices svc) => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            try
            {
                if (!svc.Settings.BriefingsOn) continue;
                var now = DateTime.Now;
                foreach (var t in Times(svc.Settings.BriefingTimes))
                {
                    if (now.TimeOfDay < t || now.TimeOfDay >= t + TimeSpan.FromMinutes(10) || !Fired.Add($"{now:yyyyMMdd}-{t}")) continue;
                    var text = await Dispatcher.UIThread.InvokeAsync(() => Compose(svc));
                    Deliver(svc, text);
                }
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }
        }
    });

    public static IEnumerable<TimeSpan> Times(string setting)
    {
        foreach (var part in setting.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (TimeSpan.TryParseExact(part, "h\\:mm", CultureInfo.InvariantCulture, out var t) || TimeSpan.TryParseExact(part, "hh\\:mm", CultureInfo.InvariantCulture, out t))
                yield return t;
    }

    /// <summary>Builds the text (call it on the UI thread: it reads the open instruments).</summary>
    public static string Compose(AppServices svc)
    {
        var sb = new StringBuilder($"BRIEFING · {DateTime.Now:dddd dd/MM HH:mm}\n\n");
        var soon = EventCalendar.All().Where(e => e.Utc >= DateTime.UtcNow && e.Utc <= DateTime.UtcNow.AddHours(24)).ToList();
        sb.AppendLine("EVENTS IN THE NEXT 24 H");
        if (soon.Count == 0) sb.AppendLine("  none in your events file");
        foreach (var e in soon) sb.AppendLine($"  {e.Utc.ToLocalTime():dd/MM HH:mm}  {e.Name}");
        sb.AppendLine();

        var levels = TodayReport.Load();
        foreach (var d in svc.Hub.Items.Where(i => i.Instrument.FeedId != "sim").OrderBy(i => i.Instrument.Key))
        {
            var store = d.Trades;
            if (store.Count == 0) continue;
            decimal last = d.LastPrice, hi = last, lo = last;
            var since = DateTime.UtcNow.AddHours(-12);
            for (int i = store.Count - 1; i >= 0 && store.GetTime(i) >= since; i--)
            {
                decimal p = store.GetPrice(i);
                if (p > hi) hi = p;
                if (p < lo) lo = p;
            }

            decimal tick = d.Instrument.TickSize > 0 ? d.Instrument.TickSize : 1m;
            sb.AppendLine($"{d.Instrument.Symbol.ToUpperInvariant()}  last {last} · range of the last 12 h {lo} - {hi}");
            if (levels.FirstOrDefault(l => string.Equals(l.Instrument, d.Instrument.Symbol, StringComparison.OrdinalIgnoreCase)) is { Levels: { Count: > 0 } rows })
            {
                var open = rows.Where(r => !r.Touched && !r.Hidden).ToList();
                foreach (var (label, set) in new[] { ("above", open.Where(r => r.Price > last).OrderBy(r => r.Price).Take(3)), ("below", open.Where(r => r.Price < last).OrderByDescending(r => r.Price).Take(3)) })
                    foreach (var r in set)
                        sb.AppendLine($"  {label,-5} {(r.Buy ? "buy " : "sell")} {r.Volume,8:0.##} @ {r.Price}  ({Math.Abs(r.Price - last) / tick:0} ticks away, placed {r.Time.ToLocalTime():dd/MM HH:mm})");
                var board = TodayReport.Scoreboard(rows);
                if (board.Length > 0) sb.AppendLine(board.Replace("- Level", "  Level"));
            }

            sb.AppendLine();
        }

        sb.AppendLine(TodayReport.ShadowSection());
        return sb.ToString();
    }

    private static void Deliver(AppServices svc, string text)
    {
        var dir = Path.Combine(TradingHub.Common.Product.Documents, "Briefings");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd-HHmm}.md");
        File.WriteAllText(file, text);
        AlertLog.Add(DateTime.Now, "Briefing saved: " + file);
        TelegramNotifier.Notify(svc.Settings, text.Length > 3500 ? text[..3500] : text);
        SpokenAlerts.Say(svc.Settings, "Briefing ready. " + text.Split('\n').FirstOrDefault(l => l.StartsWith("BRIEFING")));
    }
}
