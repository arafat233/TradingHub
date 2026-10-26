using TradingHub.Charting.Dom;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>The DOM's Cxl column: size that left the book without trading.</summary>
public class DomCancelledTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "sim", TickSize = 0.25m, VolumeStep = 1 };

    private static DepthUpdate Update(decimal bid, decimal ask, bool full = false)
    {
        var b = new OrderBookBuilder();
        b.Set(BookSide.Bid, Es.ToTicks(5000m), bid);
        b.Set(BookSide.Ask, Es.ToTicks(5000.25m), ask);
        var u = b.BuildUpdate(DateTime.UtcNow, 50, full);
        return new DepthUpdate
        {
            Snapshot = u.Snapshot,
            IsFullRefresh = full,
            Changes = new[] { new BookChange(BookSide.Bid, Es.ToTicks(5000m), bid), new BookChange(BookSide.Ask, Es.ToTicks(5000.25m), ask) },
        };
    }

    [Fact]
    public void A_removal_counts_as_cancelled_only_after_taking_off_what_traded_there()
    {
        var data = new InstrumentData(Es, new SimulatedFeed());
        typeof(InstrumentData).GetField("_historyMerged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(data, true); // as after a history load
        var dom = new DomModel(data);
        var sink = (IMarketDataSink)data;
        long bid = dom.RowOf(Es.ToTicks(5000m));
        sink.OnDepth(Es, Update(100, 100, full: true));
        data.Pump();
        Assert.Empty(dom.Cancelled);

        sink.OnTrades(Es, new[] { new Trade(1, DateTime.UtcNow, 5000m, 30, TradeSide.Sell) }); // 30 sold into the bid
        sink.OnDepth(Es, Update(40, 100));                                                       // the bid fell by 60: 30 traded, 30 pulled
        data.Pump();
        Assert.Equal(30, dom.Cancelled[bid]);

        sink.OnDepth(Es, Update(40, 150));                                                       // growing is not a cancel
        data.Pump();
        Assert.Equal(30, dom.Cancelled[bid]);
    }
}
