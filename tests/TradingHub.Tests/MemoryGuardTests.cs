using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>The low-memory mode: when it switches on and off, and that giving memory back drops the right history and keeps the rest.</summary>
public class MemoryGuardTests
{
    [Fact]
    public void It_switches_on_at_92_percent_and_only_off_under_85_so_it_does_not_flip_around_one_number()
    {
        Assert.False(MemoryGuard.Decide(0.80, wasLow: false));
        Assert.False(MemoryGuard.Decide(0.91, wasLow: false));
        Assert.True(MemoryGuard.Decide(0.92, wasLow: false));
        Assert.True(MemoryGuard.Decide(0.90, wasLow: true));         // still low: it has not fallen under 85 yet
        Assert.True(MemoryGuard.Decide(0.86, wasLow: true));
        Assert.False(MemoryGuard.Decide(0.85, wasLow: true));
    }

    [Fact]
    public void The_machines_memory_load_can_be_read_and_is_a_share()
    {
        var load = MemoryGuard.CurrentLoad();
        Assert.NotNull(load);
        Assert.InRange(load!.Value, 0.0, 1.0);
    }

    [Fact]
    public void Giving_memory_back_shortens_the_book_and_flow_history_and_going_back_to_normal_keeps_what_is_left()
    {
        var inst = new Instrument { Symbol = "TEST", FeedId = "test", TickSize = 0.25m, VolumeStep = 1 };
        var data = new InstrumentData(inst, new TradingHub.Feeds.SimulatedFeed());
        var t0 = DateTime.UtcNow.AddHours(-3);
        var book = new OrderBookBuilder();
        book.Set(BookSide.Bid, 100, 5);
        book.Set(BookSide.Ask, 101, 5);
        var snap = book.BuildUpdate(t0, 10, true).Snapshot;
        data.DepthHistory.MaxAge = TimeSpan.FromHours(5);
        for (int i = 0; i < 3 * 3600 * 4; i += 40) data.DepthHistory.Add(t0.AddMilliseconds(i * 250), snap); // three hours of snapshots
        int before = data.DepthHistory.Count;
        Assert.True(before > 100);

        // the flow tracker has six hours of minutes
        long id = 1;
        for (int m = 0; m < 360; m++)
            data.Trades.Add(new Trade(id++, t0.AddMinutes(m - 180 + 180), 100m, 1m, TradeSide.Buy));
        data.Insights.Flow.Update(data.Trades);
        int bars = data.Insights.Flow.Bars.Count;

        MemoryGuard.Apply(new[] { data }, low: true);
        Assert.True(data.DepthHistory.Count < before);                                     // only the last 5 minutes of the book are kept
        Assert.True(data.DepthHistory.LastTime - data.DepthHistory.FirstTime <= TimeSpan.FromMinutes(5.1));
        Assert.True(data.Insights.Flow.Bars.Count < bars);                                 // 4 hours of minutes
        Assert.Equal(360, data.Trades.Count);                                              // the trades are never touched

        MemoryGuard.Apply(new[] { data }, low: false);
        Assert.Equal(TimeSpan.FromMinutes(30), data.DepthHistory.MaxAge);                  // normal limits are back
        Assert.Equal(TimeSpan.FromHours(26), data.Insights.Flow.Keep);
    }
}
