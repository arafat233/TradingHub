using TradingHub.Data;

namespace TradingHub.Tests;

public class SimilarMomentsTests
{
    private static Candle Bar(int i, long close, decimal buy, decimal sell) => new()
    {
        Time = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
        OpenTicks = close, HighTicks = close + 2, LowTicks = close - 2, CloseTicks = close, Volume = buy + sell, Ask = buy, Bid = sell,
    };

    [Fact]
    public void Finds_the_earlier_stretch_that_looked_like_now_and_reports_what_followed_it()
    {
        // a flat market, then a rally on heavy buying at candles 100-110 that continued (+40 ticks over the next 30 candles),
        // then flat, then the same rally again at the end (the "now" being asked about)
        var bars = new List<Candle>();
        long price = 1000;
        for (int i = 0; i < 400; i++)
        {
            bool rally = (i >= 100 && i < 111) || (i >= 330 && i < 341);
            if (rally) price += 3;
            else if (i >= 111 && i < 141) price += 1; // the follow-through of the first rally
            bars.Add(rally ? Bar(i, price, 90, 10) : Bar(i, price + (i % 2), 10 + i % 3, 10 + (i + 1) % 3));
        }

        var result = SimilarMoments.Find(bars, 340, horizon: 30, k: 3);
        Assert.NotEmpty(result.Matches);
        // the closest match is inside the first rally, and the market went up after it
        Assert.InRange(result.Matches[0].Index, 100, 112);
        Assert.True(result.Matches[0].AheadTicks > 0);
        Assert.True(result.Up >= 1);
    }
}
