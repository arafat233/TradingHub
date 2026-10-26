using System.Globalization;
using System.Text.Json.Nodes;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Trading;

namespace TradingHub.App.Lab;

internal static partial class LabServer
{
    private static JsonNode ListAccounts()
    {
        var arr = new JsonArray();
        if (Directory.Exists(AppPaths.Accounts))
            foreach (var f in Directory.GetFiles(AppPaths.Accounts, "*.jsonl"))
                arr.Add(Path.GetFileNameWithoutExtension(f));
        return new JsonObject { ["accounts"] = arr, ["note"] = "Account ids as the trade_autopsy tool takes them; \"sim\" is the simulated account." };
    }

    /// <summary>Your closed trades with what the tape did around each one, so Claude can say what happened and why, not only how much.</summary>
    private static JsonNode TradeAutopsyTool(JsonObject a)
    {
        string account = Str(a, "account", "sim");
        int limit = (int)Math.Clamp(Num(a, "limit", 15), 1, 60);
        DateOnly? day = a["date"] == null ? null : DateOnly.Parse(Str(a, "date"), Inv);
        var journal = new AccountJournal(AppPaths.Accounts);
        var fills = journal.FillsOf(account);
        if (fills.Count == 0) throw new ArgumentException($"There are no fills for account “{account}”. Use list_accounts for the ones that exist.");
        var trades = RoundTrips.Analyze(fills).Trades.OrderBy(t => t.CloseTime).ToList();
        if (day is { } d) trades = trades.Where(t => DateOnly.FromDateTime(t.OpenTime) == d).ToList();
        trades = trades.TakeLast(limit).ToList();

        var levelsByInstrument = TodayReport.Load();
        var arr = new JsonArray();
        var withData = new List<TradeContext>();
        foreach (var t in trades)
        {
            var o = new JsonObject
            {
                ["symbol"] = t.Symbol,
                ["side"] = t.IsLong ? "long" : "short",
                ["quantity"] = (double)t.Quantity,
                ["open_utc"] = t.OpenTime.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                ["close_utc"] = t.CloseTime.ToString("yyyy-MM-dd HH:mm:ss", Inv),
                ["minutes"] = Math.Round(t.Duration.TotalMinutes, 1),
                ["net_pnl"] = (double)Math.Round(t.NetPnl, 2),
            };
            var (feed, symbol) = TicksFor(t);
            if (feed == null)
            {
                o["context"] = "no stored ticks for this symbol around that time";
                arr.Add(o);
                continue;
            }

            decimal tick = HistorySources.StoredTickSize(AppPaths.Ticks, feed, symbol!) ?? 0;
            var ticks = HistoricalData.Read(AppPaths.Ticks, feed, symbol!, t.OpenTime.AddHours(-4), t.CloseTime.AddMinutes(6)).ToList();
            if (tick <= 0 || ticks.Count == 0)
            {
                o["context"] = "no stored ticks for this symbol around that time";
                arr.Add(o);
                continue;
            }

            var levels = levelsByInstrument.FirstOrDefault(l => string.Equals(l.Instrument, symbol, StringComparison.OrdinalIgnoreCase)).Levels?.Select(l => l.Price).ToList() ?? new List<decimal>();
            var c = TradeAutopsy.Analyze(t, ticks, tick, levels);
            withData.Add(c);
            o["context"] = new JsonObject
            {
                ["ticks_symbol"] = symbol,
                ["result_ticks"] = (double)c.ResultTicks,
                ["worst_against_you_ticks"] = (double)c.MaeTicks,
                ["best_in_your_favour_ticks"] = (double)c.MfeTicks,
                ["seconds_to_best"] = (int)c.TimeToMfe.TotalSeconds,
                ["seconds_to_worst"] = (int)c.TimeToMae.TotalSeconds,
                ["share_of_best_move_kept"] = c.ExitEfficiency is { } e ? (double)e : null,
                ["tape_delta_5min_before_in_your_direction"] = (double)c.DeltaBefore5MinWithYou,
                ["volume_last_minute_vs_prior_half_hour_average"] = (double)c.VolumeBurst,
                ["entry_position_in_4h_range_0low_1high"] = c.EntryPositionInRange is { } p ? (double)p : null,
                ["nearest_remembered_level_ticks_from_entry"] = c.NearestLevelTicks is { } n ? (double)n : null,
                ["price_moved_in_your_direction_5min_after_exit_ticks"] = (double)c.AfterExit5MinTicks,
            };
            arr.Add(o);
        }

        var summary = new JsonObject { ["trades_analysed"] = withData.Count };
        if (withData.Count > 0)
        {
            summary["avg_share_of_best_move_kept"] = Math.Round(withData.Where(c => c.ExitEfficiency != null).Select(c => (double)c.ExitEfficiency!.Value).DefaultIfEmpty(0).Average(), 2);
            summary["avg_worst_against_you_ticks"] = Math.Round(withData.Average(c => (double)c.MaeTicks), 1);
            summary["avg_price_move_after_exit_ticks"] = Math.Round(withData.Average(c => (double)c.AfterExit5MinTicks), 1);
            summary["entered_against_the_tape_share"] = Math.Round(withData.Count(c => c.DeltaBefore5MinWithYou < 0) / (double)withData.Count, 2);
        }

        return new JsonObject
        {
            ["account"] = account,
            ["summary"] = summary,
            ["trades"] = arr,
            ["note"] = "Numbers only, measured on the recorded ticks. Ticks are in the instrument's tick size; \"in your direction\" means up for a long and down for a short. Explain what they suggest and say how sure you are: a handful of trades is an anecdote.",
        };
    }

    /// <summary>The stored ticks that match a fill's symbol (a contract like GCZ6 matches the continuous GC.F) and cover its time.</summary>
    private static (string? Feed, string? Symbol) TicksFor(AccountTrade t)
    {
        var day = DateOnly.FromDateTime(t.OpenTime);
        foreach (var s in HistorySources.Datasets(AppPaths.Ticks).Where(s => s.FeedId != "sim"))
        {
            string root = s.Symbol.Split('.')[0];
            bool same = s.Symbol.Equals(t.Symbol, StringComparison.OrdinalIgnoreCase) || (root.Length >= 2 && t.Symbol.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            if (same && s.Days.Any(x => x.Day == day)) return (s.FeedId, s.Symbol);
        }

        return (null, null);
    }
}
