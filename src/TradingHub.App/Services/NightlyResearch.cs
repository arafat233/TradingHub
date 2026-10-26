using System.Globalization;
using System.Text;

namespace TradingHub.App.Services;

/// <summary>
/// Once a night (03:00 local by default, off until the user turns it on) it mines the day's new big orders, reruns the edge finder with
/// its fixed rules and tests the locked hypotheses, saves the full report, and tells the user only if something passes. The rules never
/// change from night to night, so a pass is not the product of trying again until something looks good. The app has to be open at that time.
/// </summary>
internal static class NightlyResearch
{
    private static string? _firedFor;

    public static void Start(AppServices svc) => _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(60));
            try
            {
                if (!svc.Settings.ResearchNightly) continue;
                if (!TimeSpan.TryParseExact(svc.Settings.ResearchTime.Trim(), new[] { "h\\:mm", "hh\\:mm" }, CultureInfo.InvariantCulture, out var at)) continue;
                var now = DateTime.Now;
                string key = $"{now:yyyyMMdd}";
                if (now.TimeOfDay < at || now.TimeOfDay >= at + TimeSpan.FromMinutes(30) || _firedFor == key) continue;
                _firedFor = key;
                Run(svc);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }
        }
    });

    public static string Run(AppServices svc)
    {
        var sb = new StringBuilder($"NIGHTLY RESEARCH · {DateTime.Now:dddd dd/MM/yyyy HH:mm}\n\nMINING\n");
        sb.AppendLine(LevelTools.Mine()).AppendLine();
        sb.AppendLine(Research.RunEdgeFinder()).AppendLine();
        sb.AppendLine(ResearchExtra.Hypotheses());
        var text = sb.ToString();
        var dir = Path.Combine(TradingHub.Common.Product.Documents, "Research");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.txt");
        File.WriteAllText(file, text);
        bool news = text.Contains("PASS  ", StringComparison.Ordinal) || text.Contains("SUPPORTED so far", StringComparison.Ordinal);
        if (news)
        {
            string message = "Research: something held up in last night's test (see " + file + "). " + (text.Contains("SUPPORTED so far") ? "A locked hypothesis is supported so far." : "An edge-finder condition passed on data kept back.");
            AlertLog.Add(DateTime.Now, message);
            TelegramNotifier.Notify(svc.Settings, message);
            SpokenAlerts.Say(svc.Settings, "Research report: something held up.");
        }

        return text;
    }
}
