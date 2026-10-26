using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;

namespace TradingHub.App.Services;

/// <summary>
/// Downloads about 13 months of 1-minute candles for more London Strategic Edge instruments into the same cache the charts use, so the
/// research (claims test, rule lab, seasonality) can be repeated on other markets. Closed months are stored; this uses the plan's
/// allowance for candle requests (a few dozen calls per instrument), never more than a month at a time, and can be repeated: months
/// already stored are not downloaded again.
/// </summary>
internal static class HistoryDownload
{
    public static async Task<string> RunAsync(ConnectionManager connections, IEnumerable<string> symbols, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var profile = connections.Enabled.FirstOrDefault(c => c.Kind == ConnectionKind.LondonStrategicEdge);
        if (profile == null) return "No London Strategic Edge connection is enabled.";
        if (connections.GetFeed(profile.Id) is not (IMarketDataFeed feed and IKlineHistory klines)) return "The connection has no candle history.";
        KlineHistory.CacheRoot ??= Path.Combine(AppPaths.Root, "Klines");
        var instruments = await feed.GetInstrumentsAsync(ct);
        var sb = new StringBuilder();
        foreach (var symbol in symbols)
        {
            var inst = instruments.FirstOrDefault(i => string.Equals(i.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
            if (inst == null) { sb.AppendLine($"{symbol}: not in the London Strategic Edge catalog"); continue; }
            try
            {
                var to = DateTime.UtcNow;
                var from = new DateTime(to.Year, to.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-13);
                progress?.Report($"{symbol}: downloading from {from:yyyy-MM}...");
                var loaded = await KlineHistory.LoadAsync(klines, inst, from, to, 1, 0m, null, ct);
                sb.AppendLine(loaded.Count == 0
                    ? $"{symbol}: the service returned no candles"
                    : $"{symbol}: {loaded.Count:N0} one-minute candles, {DateTimeOffset.FromUnixTimeMilliseconds(loaded[0].OpenTimeMs):yyyy-MM-dd} to {DateTimeOffset.FromUnixTimeMilliseconds(loaded[^1].OpenTimeMs):yyyy-MM-dd}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"{symbol}: could not download ({ex.GetType().Name}: {ex.Message})");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
