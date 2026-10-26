using System.Text.Json.Nodes;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.App.Lab;

internal static partial class LabServer
{
    /// <summary>What an account's fills really cost: slippage against the recorded tape a second before each fill, plus commissions, overall and by hour, instrument and side.</summary>
    private static JsonNode FillQualityTool(JsonObject a)
    {
        string account = Str(a, "account", "sim");
        int days = (int)Math.Clamp(Num(a, "days", 30), 1, 365);
        var fills = new AccountJournal(AppPaths.Accounts).FillsOf(account);
        if (fills.Count == 0) throw new ArgumentException($"There are no fills for account “{account}”. Use list_accounts for the ones that exist.");
        var since = DateTime.UtcNow.AddDays(-days);
        fills = fills.Where(f => f.Time >= since).ToList();

        // one day of one instrument's ticks is read once and kept while the fills of that day are looked up
        var cache = new Dictionary<(string Feed, string Symbol, DateOnly Day), (long[] Times, long[] Prices, decimal Tick)?>();
        decimal? Arrival(AccountFill f)
        {
            var day = DateOnly.FromDateTime(f.Time);
            foreach (var s in HistorySources.Datasets(AppPaths.Ticks).Where(s => s.FeedId != "sim"))
            {
                string root = s.Symbol.Split('.')[0];
                bool same = s.Symbol.Equals(f.Symbol, StringComparison.OrdinalIgnoreCase) || (root.Length >= 2 && f.Symbol.StartsWith(root, StringComparison.OrdinalIgnoreCase));
                if (!same || !s.Days.Any(x => x.Day == day)) continue;
                var key = (s.FeedId, s.Symbol, day);
                if (!cache.TryGetValue(key, out var tape))
                {
                    var trades = HistorySources.ReadDay(AppPaths.Ticks, s.FeedId, s.Symbol, day);
                    tape = trades.Count == 0 ? null : (trades.Select(t => t.Time.Ticks).ToArray(), trades.Select(t => (long)Math.Round(t.Price * 100000m)).ToArray(), 0.00001m);
                    cache[key] = tape;
                }

                if (tape is not { } tp) continue;
                int i = Array.BinarySearch(tp.Times, f.Time.AddSeconds(-1).Ticks);
                if (i < 0) i = ~i - 1;
                if (i >= 0 && f.Time.Ticks - tp.Times[i] < TimeSpan.FromMinutes(5).Ticks) return tp.Prices[i] * tp.Tick; // a tape that covers the moment
            }

            return null;
        }

        var report = FillQuality.Analyze(fills, Arrival);
        return new JsonObject
        {
            ["report"] = report.Text(),
            ["account"] = account,
            ["fills"] = report.Fills,
            ["measured"] = report.Measured,
            ["slippage_bp"] = (double)report.SlippageBp,
            ["commission_bp"] = (double)report.CommissionBp,
            ["round_trip_bp"] = (double)report.RoundTripBp,
            ["note"] = "Read-only. Compare round_trip_bp with the costs the Insights panel assumes (8 for crypto, 2 for the rest) and say what it suggests; a simulated account has no real slippage.",
        };
    }
}
