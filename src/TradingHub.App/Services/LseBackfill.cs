using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds.LondonStrategicEdge;
using TradingHub.Market;

namespace TradingHub.App.Services;

/// <summary>
/// Keeps a growing tick history of the futures you care about without a chart having to be open. London Strategic Edge's free archive holds the
/// ticks of every futures market in real time, but only the recent weeks; this downloads each finished day of the symbols chosen in the connection
/// (ES, NQ and GC by default), a few days at a time, into the same tick folders the charts, the backtester and the research use, so the history keeps
/// growing instead of expiring. A day that was fetched is remembered, so nothing is downloaded twice, and it never touches today (a chart records
/// that live). It uses the free plan's allowance gently: a day is about 25 MB for ES and the calls are spread out.
/// </summary>
public static class LseBackfill
{
    private const int DaysBack = 14, DaysPerRound = 3;
    private static readonly TimeSpan First = TimeSpan.FromMinutes(2), Every = TimeSpan.FromMinutes(20), BetweenDays = TimeSpan.FromSeconds(20);

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

    private static string StatePath => Path.Combine(AppPaths.Root, "lse-backfill.json");

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
        catch (Exception) { /* the days are fetched again next time: harmless */ }
    }

    /// <summary>One round: up to <see cref="DaysPerRound"/> missing days in total, newest first, for the chosen symbols. Returns how many it fetched.</summary>
    public static async Task<int> RunOnceAsync(AppServices svc, CancellationToken ct)
    {
        var profile = svc.Connections.Enabled.FirstOrDefault(c => c.Kind == ConnectionKind.LondonStrategicEdge);
        var root = svc.Hub.RecordingRoot;
        if (profile == null || root == null || !svc.Hub.RecordLive || profile.LseBackfill.Count == 0) return 0;
        if (string.IsNullOrWhiteSpace(svc.Connections.GetSecret(profile))) return 0;
        var feed = (LseFeed)svc.Connections.GetFeed(profile.Id);
        var state = Load();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        int fetched = 0;
        for (int back = 1; back <= DaysBack && fetched < DaysPerRound; back++)
        {
            var day = today.AddDays(-back);
            foreach (var sym in profile.LseBackfill)
            {
                if (fetched >= DaysPerRound) break;
                var inst = LseFeed.FuturesInstrument(sym);
                if (inst == null) continue;
                var done = state.TryGetValue(inst.Symbol, out var set) ? set : state[inst.Symbol] = new();
                string key = day.ToString("yyyy-MM-dd");
                if (done.Contains(key)) continue;
                // a chart recording this instrument right now owns its file: skip it, the next round gets it
                using var gate = TickFile.TryLock(root, inst.FeedId, inst.Symbol);
                if (gate == null) continue;

                var trades = new List<Trade>();
                var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                await feed.LoadTradesAsync(inst, from, from.AddDays(1), null, b => { trades.AddRange(b); return true; }, null, ct);
                if (trades.Count > 0) HistoricalData.WriteDay(root, inst, day, trades); // merged with anything a chart recorded, without duplicating
                done.Add(key); // a weekend or a holiday has no ticks: that is also an answer
                Save(state);
                fetched++;
                await Task.Delay(BetweenDays, ct);
            }
        }

        return fetched;
    }
}
