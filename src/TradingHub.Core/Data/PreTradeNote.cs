using TradingHub.Trading;

namespace TradingHub.Data;

/// <summary>
/// One quiet line before you click, from your own closed trades: a losing run, a tilt after a fast loss, or an hour that has been
/// bad for you. It is only a note: it never blocks or changes an order, and it stays silent unless the sample is big enough to mean something.
/// </summary>
public static class PreTradeNote
{
    private const int MinHourTrades = 10;

    public static string? Build(IReadOnlyList<AccountTrade> trades, DateTime nowLocal)
    {
        if (trades.Count < 5) return null;
        var ordered = trades.OrderBy(t => t.CloseTime).ToList();
        var notes = new List<string>();

        int run = 0;
        for (int i = ordered.Count - 1; i >= 0 && ordered[i].NetPnl < 0; i--) run++;
        var last = ordered[^1];
        var sinceLast = nowLocal.ToUniversalTime() - last.CloseTime;
        if (run >= 3 && sinceLast < TimeSpan.FromHours(3)) notes.Add($"{run} losses in a row.");

        // after a loss, how you did on the very next trade when you took it within 10 minutes (revenge pattern)
        var quick = new List<decimal>();
        for (int i = 1; i < ordered.Count; i++)
            if (ordered[i - 1].NetPnl < 0 && ordered[i].OpenTime - ordered[i - 1].CloseTime < TimeSpan.FromMinutes(10)) quick.Add(ordered[i].NetPnl);
        if (quick.Count >= MinHourTrades && last.NetPnl < 0 && sinceLast < TimeSpan.FromMinutes(10) && quick.Average() < 0)
            notes.Add($"Your {quick.Count} trades taken within 10 min of a loss averaged {quick.Average():+0.##;-0.##}.");

        var hour = ordered.Where(t => t.OpenTime.ToLocalTime().Hour == nowLocal.Hour).ToList();
        if (hour.Count >= MinHourTrades && hour.Sum(t => t.NetPnl) < 0 && ordered.Sum(t => t.NetPnl) > 0)
            notes.Add($"{nowLocal.Hour:00}:00 hour: {hour.Count} trades, {hour.Count(t => t.NetPnl > 0) * 100 / hour.Count}% winners, {hour.Sum(t => t.NetPnl):+0;-0} in total, while you are up overall.");

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }
}
