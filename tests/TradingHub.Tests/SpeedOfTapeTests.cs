using System.Drawing;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Indicators.Native;
using TradingHub.Market;

namespace TradingHub.Tests;

public class SpeedOfTapeTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "test", TickSize = 0.25m, VolumeStep = 1 };
    private static readonly DateTime T0 = new(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);

    private static (ChartModel Model, SpeedOfTape Ind) Build(SpeedOfTape.SpeedUnit unit = SpeedOfTape.SpeedUnit.Trades)
    {
        var data = new InstrumentData(Es, new SimulatedFeed());
        long id = 1;
        // ten calm minutes: 6 trades each, one every 10 s (0.1 trades/s)
        for (int m = 0; m < 10; m++)
            for (int k = 0; k < 6; k++)
                data.Trades.Add(new Trade(id++, T0.AddMinutes(m).AddSeconds(k * 10), 5000 + k * 0.25m, 1, TradeSide.Buy));
        // a busy minute: 200 trades in 20 s (10 trades/s) of 5 contracts, sellers hitting the bid
        for (int k = 0; k < 200; k++)
            data.Trades.Add(new Trade(id++, T0.AddMinutes(10).AddMilliseconds(k * 100), 5000 - (k % 8) * 0.25m, 5, TradeSide.Sell));
        // and one more calm minute, so the busy one is a closed candle
        data.Trades.Add(new Trade(id++, T0.AddMinutes(11), 5000, 1, TradeSide.Buy));
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var ind = new SpeedOfTape { Unit = unit };
        model.AddIndicator(ind);
        for (int k = 0; k < 3; k++) model.Update();
        return (model, ind);
    }

    [Fact]
    public void A_busy_candle_reads_trades_per_second_and_is_painted_with_the_side_that_led()
    {
        var (_, ind) = Build();
        var speed = (ValueDataSeries)ind.DataSeries[0];
        var avg = (ValueDataSeries)ind.DataSeries[1];

        Assert.InRange((double)speed[3], 0.11, 0.13);   // 6 trades over the 50 s between its first and last trade
        Assert.InRange((double)speed[10], 9.5, 10.5);    // 200 trades in 19.9 s
        Assert.InRange((double)avg[10], 0.05, 0.2);      // average of the calm candles before it
        Assert.Equal(ind.SellColor, speed.Colors[10]);   // fast, and the delta is negative
        Assert.Equal(ind.NormalColor, speed.Colors[5]);
    }

    [Fact]
    public void Contracts_per_second_weights_the_size_of_each_trade()
    {
        var (_, ind) = Build(SpeedOfTape.SpeedUnit.Volume);
        var speed = (ValueDataSeries)ind.DataSeries[0];
        Assert.InRange((double)speed[10], 47, 52);       // 200 trades x 5 contracts in 19.9 s
    }
}
