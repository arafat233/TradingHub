using TradingHub.Data;
using TradingHub.Trading;

namespace TradingHub.Tests;

public class PreTradeNoteTests
{
    private static AccountTrade T(DateTime close, decimal pnl) => new("ES", true, close.AddMinutes(-5), close, 1, 100, 101, pnl, 0);

    [Fact]
    public void Silent_without_history_and_warns_after_a_losing_run()
    {
        var now = new DateTime(2026, 3, 2, 15, 0, 0, DateTimeKind.Utc);
        Assert.Null(PreTradeNote.Build(new[] { T(now.AddHours(-1), -5) }, now.ToLocalTime()));

        var trades = Enumerable.Range(0, 6).Select(i => T(now.AddHours(-10 + i), 10)).ToList();
        trades.AddRange(Enumerable.Range(0, 3).Select(i => T(now.AddMinutes(-60 + i * 10), -4)));
        Assert.Contains("3 losses in a row", PreTradeNote.Build(trades, now.ToLocalTime()));
    }
}
