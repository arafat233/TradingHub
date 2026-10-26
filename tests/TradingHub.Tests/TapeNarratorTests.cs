using TradingHub.Data;

namespace TradingHub.Tests;

public class TapeNarratorTests
{
    private static TapeFacts Facts(decimal buy = 10, decimal sell = 10, decimal change = 0, decimal typical = 20, decimal big = 2, decimal typTrade = 1,
        decimal bid = 100, decimal ask = 100, bool ice = false) =>
        new(30, buy, sell, change, 10, typical, big, true, 5000m, typTrade, bid, ask, ice, true, 5001m, 120);

    [Fact]
    public void Says_nothing_when_nothing_stands_out()
    {
        Assert.Null(TapeNarrator.Describe(Facts()));
    }

    [Fact]
    public void An_iceberg_beats_everything_else()
    {
        var s = TapeNarrator.Describe(Facts(buy: 300, sell: 10, change: 12, ice: true));
        Assert.Contains("resting seller", s);
    }

    [Fact]
    public void Heavy_one_sided_volume_with_no_movement_is_absorption()
    {
        var s = TapeNarrator.Describe(Facts(buy: 90, sell: 10, change: 1));
        Assert.Contains("absorbing", s);
    }

    [Fact]
    public void A_one_sided_move_is_a_sweep_and_a_huge_print_is_called_out()
    {
        Assert.Contains("lifting offers", TapeNarrator.Describe(Facts(buy: 15, sell: 5, change: 10, typical: 100)));
        Assert.Contains("Large buy", TapeNarrator.Describe(Facts(big: 40)));
        Assert.Contains("stacked", TapeNarrator.Describe(Facts(bid: 400, ask: 100)));
    }
}
