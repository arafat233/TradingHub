using System.Text;
using System.Text.Json;

namespace TradingHub.App.Services;

/// <summary>
/// Writes a short Markdown recap of yesterday's remembered big-trade levels (per instrument: how many, the biggest,
/// how many price has come back to, and the biggest ones still untouched) to <c>Documents\Trading Hub\Recaps</c>.
/// It runs at startup and skips days that already have a recap.
/// </summary>
public static class DailyRecap
{
    private const string Marker = "## Claude's take";

    /// <summary>
    /// If the setting is on, asks Claude Code (the user's session) for a few sentences on yesterday's recap and appends them.
    /// It never runs twice for the same day, and nothing here may fail the app.
    /// </summary>
    public static async Task AddCommentAsync(AppServices svc)
    {
        try
        {
            if (!svc.Settings.RecapComment) return;
            var file = Path.Combine(TradingHub.Common.Product.Documents, "Recaps", $"{DateTime.Now.Date.AddDays(-1):yyyy-MM-dd}.md");
            if (!File.Exists(file)) return;
            var text = await File.ReadAllTextAsync(file);
            if (text.Contains(Marker, StringComparison.Ordinal)) return;
            var dir = Path.Combine(AppPaths.Root, "recap-agent");
            var res = await new ClaudeAgent().RunAsync(new ClaudeAgentRequest
            {
                Role = "daily-recap",
                Prompt = "Here is yesterday's recap of the big market orders (big-trade levels) remembered per instrument. In at most 6 sentences, say what stood out and which untouched levels are worth watching today. Use only these numbers; do not invent anything.\n\n" + text,
                SystemPrompt = "You are a concise order-flow analyst writing a short note for a discretionary futures trader. Plain sentences, no headings, no advice to trade, no invented data.",
                WorkDir = dir,
                PermissionMode = "dontAsk",
                Model = ClaudeCodeBridge.Model,
                Effort = "low",
                MaxUsd = 0.5,
                Timeout = TimeSpan.FromMinutes(3),
            }, CancellationToken.None);
            if (res.Ok && res.Text.Trim().Length > 0)
                await File.AppendAllTextAsync(file, $"{Marker}{Environment.NewLine}{Environment.NewLine}{res.Text.Trim()}{Environment.NewLine}");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    public static void Run()
    {
        try
        {
            var src = Path.Combine(AppPaths.Root, "BigTrades");
            if (!Directory.Exists(src)) return;
            var day = DateTime.Now.Date.AddDays(-1);
            var dir = Path.Combine(TradingHub.Common.Product.Documents, "Recaps");
            var file = Path.Combine(dir, $"{day:yyyy-MM-dd}.md");
            if (File.Exists(file)) return;

            var sb = new StringBuilder();
            sb.AppendLine($"# Recap {day:dddd dd/MM/yyyy}").AppendLine();
            int total = 0;
            foreach (var f in Directory.GetFiles(src, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var all = JsonSerializer.Deserialize<List<SavedLevel>>(File.ReadAllText(f)) ?? new();
                var rows = all.Where(s => !s.Deleted && s.Time.ToLocalTime().Date == day).ToList();
                if (rows.Count == 0) continue;
                total += rows.Count;
                var big = rows.MaxBy(s => s.Volume)!;
                sb.AppendLine($"## {Path.GetFileNameWithoutExtension(f)}").AppendLine();
                sb.AppendLine($"- {rows.Count} big orders ({rows.Count(s => s.Buy)} buys, {rows.Count(s => !s.Buy)} sells); price came back to {rows.Count(s => s.Touched)} of them");
                sb.AppendLine($"- Biggest: {(big.Buy ? "buy" : "sell")} {big.Volume:0.##} @ {big.Price} at {big.Time.ToLocalTime():HH:mm}");
                var open = rows.Where(s => !s.Touched).OrderByDescending(s => s.Volume).Take(5).ToList();
                if (open.Count > 0)
                {
                    sb.AppendLine("- Biggest levels price has not come back to:");
                    foreach (var s in open) sb.AppendLine($"  - {(s.Buy ? "buy" : "sell")} {s.Volume:0.##} @ {s.Price} ({s.Time.ToLocalTime():HH:mm})");
                }

                var marks = TodayReport.MarksLine(Path.GetFileNameWithoutExtension(f), day);
                if (marks.Length > 0) sb.AppendLine(marks);
                sb.AppendLine(TodayReport.Scoreboard(all.Where(s => !s.Deleted).ToList()));
                sb.AppendLine();
            }

            if (total == 0) return;
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, sb.ToString());
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex); // a recap is a nicety: never a reason to fail the start
        }
    }
}
