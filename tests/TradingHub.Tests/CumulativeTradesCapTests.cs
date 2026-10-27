using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>An indicator asking for accumulated trades from the first candle froze the UI on a year of ticks; the chart only builds the newest ones.</summary>
public class CumulativeTradesCapTests
{
    private sealed class Asker : Indicator
    {
        public int Received = -1;
        private bool _asked;

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar != 0 || _asked) return;
            _asked = true;
            RequestForCumulativeTrades(new CumulativeTradesRequest(GetCandle(0).Time));
        }

        protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> trades) =>
            Received = trades.Count();
    }

    [Fact]
    public void A_request_from_the_first_candle_only_builds_the_newest_ticks()
    {
        var btc = new Instrument { Symbol = "BTC", FeedId = "test", TickSize = 1m, VolumeStep = 1 };
        var data = new InstrumentData(btc, new SimulatedFeed());
        var t0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        int total = ChartModel.MaxCumulativeTicks + 50_000;
        for (int i = 0; i < total; i++) data.Trades.Add(new Trade(i + 1, t0.AddMilliseconds(i * 2), 100 + (i % 5), 1, TradeSide.Buy));

        var model = new ChartModel(data, ChartPeriod.H1, new ChartSettings());
        var asker = new Asker();
        model.AddIndicator(asker);
        model.Update();
        model.Rebuild();

        Assert.InRange(asker.Received, 1, ChartModel.MaxCumulativeTicks);
    }
}
