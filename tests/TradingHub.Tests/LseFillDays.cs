using TradingHub.Data;
using TradingHub.Feeds.LondonStrategicEdge;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>
/// One-off: downloads whole UTC days of one LSE futures series into the real tick store, only the days that have no file yet.
/// <c>$env:LSE_API_KEY, LSE_FILL_ROOT (…\Ticks), LSE_FILL_SYMBOL (GC.F), LSE_FILL_DAYS ("2026-10-18,2026-10-19")</c>; the app must be closed
/// (the live recorder owns the file, which is also why the app's own backfill skips an instrument it records). Does nothing without them.
/// </summary>
public class LseFillDays
{
    [Fact]
    public async Task Fill_missing_days_into_the_real_store_when_asked()
    {
        string? key = Environment.GetEnvironmentVariable("LSE_API_KEY"), root = Environment.GetEnvironmentVariable("LSE_FILL_ROOT"),
            symbol = Environment.GetEnvironmentVariable("LSE_FILL_SYMBOL"), days = Environment.GetEnvironmentVariable("LSE_FILL_DAYS");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(root) || string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(days)) return;
        var inst = LseFeed.FuturesInstrument(symbol)!;
        using var feed = new LseFeed(new LseSettings { ApiKey = key });
        using var gate = TickFile.TryLock(root, inst.FeedId, inst.Symbol);
        Assert.NotNull(gate); // the app is still recording it
        var log = new List<string>();
        foreach (var s in days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var day = DateOnly.Parse(s);
            if (File.Exists(Path.Combine(root, inst.FeedId, inst.Symbol, s + ".vtk"))) { log.Add($"{s}: already there"); continue; }
            var trades = new List<Trade>();
            var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            await feed.LoadTradesAsync(inst, from, from.AddDays(1), null, b => { trades.AddRange(b); return true; }, null, CancellationToken.None);
            if (trades.Count > 0) HistoricalData.WriteDay(root, inst, day, trades);
            log.Add($"{s}: {trades.Count} ticks");
            await Task.Delay(TimeSpan.FromSeconds(20)); // the free plan allows 5 exports an hour
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "lsefill-result.txt"), log);
    }
}
