using System.Text.Json;
using TradingHub.App.Services;

namespace TradingHub.App.Lab;

/// <summary>A lab report (Markdown) with the verdict on the strategy.</summary>
public sealed record LabReport(string Path, string Title, string? Strategy, string Verdict, DateTime Created);

/// <summary>Reports in ~/Documents/Trading Hub/Strategies/Reports (Claude writes them, the app reads them).</summary>
internal static class ReportStore
{
    private static string IndexPath => System.IO.Path.Combine(AppPaths.Reports, "index.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static readonly string[] Verdicts = { "winner", "promising", "loser", "discarded" };

    public static LabReport Save(string title, string markdown, string? strategy, string verdict, IReadOnlyList<(string Name, byte[] Png)>? images = null)
    {
        Directory.CreateDirectory(AppPaths.Reports);
        verdict = Verdicts.FirstOrDefault(v => verdict.StartsWith(v[..5], StringComparison.OrdinalIgnoreCase)) ?? "";
        strategy = string.IsNullOrWhiteSpace(strategy) ? null : System.IO.Path.GetFileNameWithoutExtension(strategy);
        string clean = string.Concat(title.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '-' : c)).Trim();
        if (clean.Length > 80) clean = clean[..80];
        var path = System.IO.Path.Combine(AppPaths.Reports, $"{DateTime.Now:yyyy-MM-dd HHmm} {clean}.md");
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(AppPaths.Reports, $"{DateTime.Now:yyyy-MM-dd HHmm} {clean} ({i}).md");

        var body = markdown.TrimStart();
        if (!body.StartsWith("# ", StringComparison.Ordinal))
        {
            var meta = new List<string> { DateTime.Now.ToString("dd/MM/yyyy HH:mm") };
            if (strategy != null) meta.Insert(0, $"Strategy: {strategy}");
            if (verdict.Length > 0) meta.Add($"Verdict: {verdict.ToUpperInvariant()}");
            body = $"# {title}\n\n> {string.Join(" · ", meta)}\n\n{body}";
        }

        // charts in a folder next to the report
        var assets = System.IO.Path.ChangeExtension(path, null) + "_archivos";
        if (images is { Count: > 0 })
        {
            Directory.CreateDirectory(assets);
            foreach (var (name, png) in images) File.WriteAllBytes(System.IO.Path.Combine(assets, name), png);
        }

        body = body.Replace("{{assets}}", Uri.EscapeDataString(System.IO.Path.GetFileName(assets)));
        File.WriteAllText(path, body);
        var report = new LabReport(path, title, strategy, verdict, DateTime.Now);
        var all = ReadIndex();
        all.Add(report);
        try
        {
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(all, Json));
        }
        catch
        {
            // the report was stored anyway
        }

        return report;
    }

    private static List<LabReport> ReadIndex()
    {
        try
        {
            if (File.Exists(IndexPath)) return JsonSerializer.Deserialize<List<LabReport>>(File.ReadAllText(IndexPath)) ?? new();
        }
        catch
        {
            // corrupt index: it is built from the files
        }

        return new();
    }

    /// <summary>Every report, from the newest to the oldest (including .md files added by hand).</summary>
    public static List<LabReport> List()
    {
        if (!Directory.Exists(AppPaths.Reports)) return new();
        var index = ReadIndex().Where(r => File.Exists(r.Path)).ToDictionary(r => r.Path);
        foreach (var f in Directory.EnumerateFiles(AppPaths.Reports, "*.md"))
            if (!index.ContainsKey(f)) index[f] = new LabReport(f, System.IO.Path.GetFileNameWithoutExtension(f), null, "", File.GetLastWriteTime(f));
        return index.Values.OrderByDescending(r => r.Created).ToList();
    }

    /// <summary>Verdict of a strategy's latest report ("" if it has none).</summary>
    public static string VerdictOf(string strategyFile) =>
        List().FirstOrDefault(r => string.Equals(r.Strategy, strategyFile, StringComparison.OrdinalIgnoreCase) && r.Verdict.Length > 0)?.Verdict ?? "";
}
