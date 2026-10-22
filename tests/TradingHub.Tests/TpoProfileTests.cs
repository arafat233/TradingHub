using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public class TpoProfileTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "test", TickSize = 0.25m, VolumeStep = 1 };
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A trade at the start and one at the end of each minute, so the candle's range is exactly [low, high].</summary>
    private static void Candle(InstrumentData data, ref long id, DateTime t, decimal low, decimal high)
    {
        data.Trades.Add(new Trade(id++, t, low, 1, TradeSide.Buy));
        data.Trades.Add(new Trade(id++, t.AddSeconds(30), high, 1, TradeSide.Sell));
    }

    [Fact]
    public void Letters_count_the_periods_that_traded_each_row_and_the_poc_is_the_busiest_one()
    {
        var data = new InstrumentData(Es, new SimulatedFeed());
        long id = 1;
        // period A (minutes 0-29): 5000.00-5002.00 · period B (30-59): 5001.00-5003.00 · period C (60-89): 5001.00-5002.00
        Candle(data, ref id, T0, 5000m, 5002m);
        Candle(data, ref id, T0.AddMinutes(30), 5001m, 5003m);
        Candle(data, ref id, T0.AddMinutes(60), 5001m, 5002m);
        // the next day, so the first profile is a closed one and a second profile starts
        Candle(data, ref id, T0.AddDays(1), 5100m, 5101m);
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var tpo = new TpoProfile { PeriodMinutes = 30 }; // 4 ticks per row by default: rows of 1.00
        model.AddIndicator(tpo);
        for (int k = 0; k < 3; k++) model.Update();

        Assert.Equal(2, tpo.Profiles.Count);
        var p = tpo.Profiles[0];
        Assert.Equal(3, p.Periods);                 // A, B and C
        Assert.Equal(5001m, p.Poc);                 // A, B and C all traded 5001; 5002 too, but 5001 is closer to the middle (5001.5)
        // letters per row: 5000→1, 5001→3, 5002→3, 5003→1 = 8, so 70% = 5.6: the POC (3) plus 5002 (3) already holds 6
        Assert.Equal(5001m, p.ValueAreaLow);
        Assert.Equal(5002m, p.ValueAreaHigh);
        Assert.Equal(5000m, p.InitialBalanceLow);   // the first two periods: 5000 to 5003
        Assert.Equal(5003m, p.InitialBalanceHigh);
        Assert.Equal('A', TpoProfile.LetterOf(0));
        Assert.Equal('z', TpoProfile.LetterOf(51));
        Assert.Equal('A', TpoProfile.LetterOf(52));
    }
}
