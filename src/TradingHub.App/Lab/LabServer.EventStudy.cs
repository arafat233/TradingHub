using System.Text.Json.Nodes;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Lab;

internal static partial class LabServer
{
    /// <summary>Tests an order-flow idea on the stored ticks of one instrument: where price was 1, 5, 15 and 30 minutes after each event, after costs, with a train/test split.</summary>
    private static JsonNode EventStudyTool(JsonObject a)
    {
        string symbol = Str(a, "symbol");
        string? feed = a["feed"] == null ? null : Str(a, "feed");
        var set = HistorySources.Datasets(AppPaths.Ticks).FirstOrDefault(s => s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && (feed == null || s.FeedId == feed))
                  ?? throw new ArgumentException($"There are no stored ticks for {symbol}{(feed == null ? "" : " on " + feed)}. Use list_data for what exists.");
        string name = Str(a, "event").Replace("_", "");
        if (!Enum.TryParse<StudyEvent>(name, true, out var kind))
            throw new ArgumentException("event is one of: lean, stacked_imbalance, absorption, big_trade, delta_divergence.");
        int dayCount = (int)Math.Clamp(Num(a, "days", 20), 3, 180);
        var chosen = set.Days.TakeLast(dayCount).Select(d => d.Day).ToList();
        decimal tick = HistorySources.StoredTickSize(AppPaths.Ticks, set.FeedId, set.Symbol) ?? 0.01m;
        var inst = new Instrument { Symbol = set.Symbol, FeedId = set.FeedId, TickSize = tick, VolumeStep = 0.00000001m };
        decimal cost = a["cost_bps"] == null ? (MarketInfo.IsCrypto(set.FeedId) ? 8m : 2m) : (decimal)Num(a, "cost_bps", 2);
        int tried = (int)Math.Clamp(Num(a, "tests_tried", 0), 0, 1000);
        var result = EventStudy.RunLazy(chosen.Count, i => HistorySources.ReadDay(AppPaths.Ticks, set.FeedId, set.Symbol, chosen[i]), inst, kind, cost, testsTried: tried); // one day at a time
        return new JsonObject
        {
            ["report"] = result.Report(),
            ["feed"] = set.FeedId,
            ["symbol"] = set.Symbol,
            ["days_used"] = chosen.Count,
            ["first_day"] = chosen[0].ToString("yyyy-MM-dd", Inv),
            ["last_day"] = chosen[^1].ToString("yyyy-MM-dd", Inv),
            ["events"] = result.Events,
            ["note"] = "Read-only: it replays stored ticks and changes nothing. With few days or few events the numbers are noise; say so. Trying several events and horizons on the same days makes some look good by luck: judge on the test rows, not on the best of many.",
        };
    }
}
