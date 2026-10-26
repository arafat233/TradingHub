using TradingHub.Data;
using TradingHub.Feeds;

namespace TradingHub.Tests;

public class RuleLabCoreTests
{
    private static Kline Bar(int i, decimal o, decimal h, decimal l, decimal c) =>
        new(new DateTimeOffset(new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc).AddMinutes(i)).ToUnixTimeMilliseconds(), o, h, l, c, 1, 0, false);

    [Fact]
    public void Candle_sim_takes_the_stop_when_a_candle_reaches_both_and_the_target_otherwise()
    {
        var bars = new List<Kline> { Bar(0, 100, 100, 100, 100), Bar(1, 100, 106, 94, 100), Bar(2, 100, 110, 99, 109) };
        // long at 100, stop 95, target 105: candle 1 reaches both (94 and 106): the stop is assumed first, plus one tick of slippage (tick = 1)
        Assert.Equal(-6, CandleSim.Run(bars, 1, true, 100m, 95m, 105m, 10, 1m, 1));
        // short at 100, stop 111, target 94: candle 1 hits the target (94) and not the stop
        Assert.Equal(6, CandleSim.Run(bars, 1, false, 100m, 111m, 94m, 10, 1m, 1));
        // nothing reached by the time limit: marked at the close, less slippage
        Assert.Equal(-2, CandleSim.Run(new List<Kline> { Bar(0, 100, 101, 99, 99), Bar(1, 99, 101, 98, 99) }, 0, true, 100m, 90m, 120m, 1, 1m, 1));
    }

    [Fact]
    public void The_candle_where_the_order_filled_can_cost_but_never_pay()
    {
        // one candle that reaches the target (110) and not the stop (95): in the fill candle that must not count; in the next one it does
        var bars = new List<Kline> { Bar(0, 100, 111, 99, 108), Bar(1, 108, 112, 107, 110) };
        Assert.Equal(10, CandleSim.Run(bars, 0, true, 100m, 95m, 110m, 5, 1m, 1));
        Assert.Equal(10, CandleSim.Run(bars, 0, true, 100m, 95m, 110m, 5, 1m, 1, firstCandleStopOnly: true)); // the second candle reaches 110 instead
        // a candle that dips to the stop after the fill is a loss even though it also ends above the entry
        var dip = new List<Kline> { Bar(0, 100, 106, 94, 105), Bar(1, 105, 112, 104, 111) };
        Assert.Equal(-6, CandleSim.Run(dip, 0, true, 100m, 95m, 110m, 5, 1m, 1, firstCandleStopOnly: true));
        Assert.Equal(10, CandleSim.Run(dip, 1, true, 100m, 95m, 110m, 5, 1m, 1)); // starting after it (the old way) hides the loss
    }

    [Fact]
    public void Verdict_card_is_green_for_a_real_edge_red_for_noise_and_grey_for_too_few_trades()
    {
        var rng = new Random(4);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var edge = Enumerable.Range(0, 300).Select(i => (t0.AddHours(i), 3.0 + (rng.NextDouble() - 0.5) * 8, 0.5)).ToList();
        var noise = Enumerable.Range(0, 300).Select(i => (t0.AddHours(i), (rng.NextDouble() - 0.5) * 8, 0.5)).ToList();
        Assert.Equal("green", StrategyVerdict.Build(edge).Light);
        Assert.Equal("red", StrategyVerdict.Build(noise).Light);
        Assert.Equal("grey", StrategyVerdict.Build(edge.Take(10).ToList()).Light);
    }

    [Fact]
    public void Rule_judge_keeps_the_bar_higher_when_many_variants_were_tried()
    {
        var rng = new Random(8);
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        // a small genuine edge: +0.8 ticks a trade with noise of 6
        var trades = Enumerable.Range(0, 400).Select(i => new RuleTrade(t0.AddDays(i).Date, t0.AddDays(i), 0.8 + (rng.NextDouble() - 0.5) * 12, 0.4)).ToList();
        Assert.False(RuleJudge.Judge("x", trades, variantsTried: 200).Passes);
        Assert.True(RuleJudge.Judge("x", trades.Select(t => t with { NetTicks = t.NetTicks + 2 }).ToList(), variantsTried: 200).Passes);
    }
}
