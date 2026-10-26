using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;

namespace TradingHub.App.Services;

/// <summary>
/// Builds a long tick history of the Bybit perpetuals you choose (BTCUSDT and ETHUSDT by default) from Bybit's free public daily files, which go back
/// years: a few finished days every round, newest first, into the same tick folders the charts, the backtester, the event tester and the research use.
/// With months of ticks an idea can be judged on more than a couple of weeks. A day that was fetched is remembered, so nothing is downloaded twice;
/// a day Bybit has not published yet is tried again next round. A day is 20 to 150 MB to download and about 6 MB stored.
/// </summary>
public static class BybitBackfill
{
    private const int DaysPerRound = 2;
    private static readonly TimeSpan First = TimeSpan.FromMinutes(5), Every = TimeSpan.FromMinutes(20);
    private static Dictionary<string, Instrument>? _instruments;

    public static void Start(AppServices svc) => _ = Task.Run(async () =>
    {
        await Task.Delay(First);
        while (true)
        {
            try { await RunOnceAsync(svc, CancellationToken.None); }
            catch (Exception ex) { CrashLog.Write(ex); } // the next round tries again
            await Task.Delay(Every);
        }
    });

    private static string StatePath => Path.Combine(AppPaths.Root, "bybit-backfill.json");

    private static Dictionary<string, HashSet<string>> Load()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllText(StatePath)) ?? new() : new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    private static void Save(Dictionary<string, HashSet<string>> state)
    {
        try { File.WriteAllText(StatePath, JsonSerializer.Serialize(state)); }
        catch (Exception) { /* fetched again next time: harmless */ }
    }

    /// <summary>One round: up to <see cref="DaysPerRound"/> missing days in total. Returns how many it stored.</summary>
    public static async Task<int> RunOnceAsync(AppServices svc, CancellationToken ct)
    {
        var profile = svc.Connections.Enabled.FirstOrDefault(c => c.Kind == ConnectionKind.Bybit);
        var root = svc.Hub.RecordingRoot;
        if (profile == null || root == null || !svc.Hub.RecordLive || profile.BybitBackfill.Count == 0 || profile.BackfillDays <= 0) return 0;
        _instruments ??= (await new BybitFeed().GetInstrumentsAsync(ct)).ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);
        var state = Load();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        int stored = 0;
        for (int back = 1; back <= profile.BackfillDays && stored < DaysPerRound; back++)
        {
            var day = today.AddDays(-back);
            foreach (var sym in profile.BybitBackfill)
            {
                if (stored >= DaysPerRound) break;
                if (!_instruments.TryGetValue(sym, out var inst)) continue;
                var done = state.TryGetValue(inst.Symbol, out var set) ? set : state[inst.Symbol] = new();
                string key = day.ToString("yyyy-MM-dd");
                if (done.Contains(key) || HistoricalData.HasDay(root, inst.FeedId, inst.Symbol, day) && day < today.AddDays(-1)) { done.Add(key); continue; } // already there
                using var gate = TickFile.TryLock(root, inst.FeedId, inst.Symbol);
                if (gate == null) continue; // a chart is recording it right now
                var trades = await PublicArchives.DownloadDayAsync(inst, day, null, ct);
                if (trades == null)
                {
                    if (back > 3) done.Add(key); // an old day Bybit has no file for: that is the answer; a recent one is just not published yet
                    continue;
                }

                if (trades.Count > 0) HistoricalData.WriteDay(root, inst, day, trades);
                done.Add(key);
                Save(state);
                stored++;
            }
        }

        Save(state);
        return stored;
    }
}
