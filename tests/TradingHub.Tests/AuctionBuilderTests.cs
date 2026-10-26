using TradingHub.Data;
using TradingHub.Feeds;

namespace TradingHub.Tests;

public class AuctionBuilderTests
{
    private static Kline Bar(int i, decimal o, decimal h, decimal l, decimal c, decimal v) =>
        new(new DateTimeOffset(new DateTime(2026, 1, 5, 13, 0, 0, DateTimeKind.Utc).AddMinutes(i)).ToUnixTimeMilliseconds(), o, h, l, c, v, 0, false);

    [Fact]
    public void Builds_letters_and_volume_by_row_from_candles_and_the_analysis_reads_them()
    {
        // 60 one-minute candles: the first half hour trades 100-104, the second 103-108; volume 10 per candle
        var bars = new List<Kline>();
        for (int i = 0; i < 30; i++) bars.Add(Bar(i, 100, 104, 100, 102, 10));
        for (int i = 30; i < 60; i++) bars.Add(Bar(i, 103, 108, 103, 106, 10));
        var input = AuctionBuilder.FromCandles(bars, 0, 59, i => i / 30, 1m);
        Assert.Equal(9, input.Rows.Length);                       // rows 100..108
        Assert.Equal(new[] { 0 }, input.Slots[0]);                // 100: only period A
        Assert.Equal(new[] { 0, 1 }, input.Slots[3]);             // 103: both periods
        Assert.Equal(new[] { 1 }, input.Slots[8]);                // 108: only period B
        Assert.Equal(600m, input.Volume.Sum(), 6);                // all the volume is accounted for
        var r = AuctionAnalysis.Analyze(input);
        Assert.Equal(108m, r.IbHigh);                             // the first two periods (the whole hour) reach 108
        Assert.False(r.ExtendedUp);                               // nothing traded beyond the first hour's range
        Assert.True(r.TpoPoc is >= 103m and <= 104m);
    }

    [Fact]
    public void Wilson_interval_brackets_the_rate_and_widens_for_small_samples()
    {
        var (lo, hi) = AuctionStats.Wilson(70, 100);
        Assert.InRange(lo, 0.60, 0.62);
        Assert.InRange(hi, 0.78, 0.80);
        var (lo2, hi2) = AuctionStats.Wilson(7, 10);
        Assert.True(hi2 - lo2 > hi - lo);
        Assert.Equal((0.0, 1.0), AuctionStats.Wilson(0, 0));
    }
}
