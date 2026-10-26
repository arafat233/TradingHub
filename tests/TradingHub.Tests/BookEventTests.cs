using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Add / cancel / fill style events read out of the Level 2 book and the trades (for feeds without order-level data).</summary>
public class BookEventTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "test", TickSize = 0.25m, VolumeStep = 1 };

    /// <summary>A quiet book (5 lots at the touch, a far level so a wall is inside the window) with an optional 600 lot wall at 5003.</summary>
    private static DepthSnapshot Book(DateTime t, decimal wall)
    {
        var b = new OrderBookBuilder();
        b.Set(BookSide.Bid, Es.ToTicks(4999.75m), 5);
        b.Set(BookSide.Ask, Es.ToTicks(5000.25m), 5);
        b.Set(BookSide.Ask, Es.ToTicks(5010m), 5);
        if (wall > 0) b.Set(BookSide.Ask, Es.ToTicks(5003m), wall);
        return b.BuildUpdate(t, 50, true).Snapshot;
    }

    private static (InstrumentData Data, DateTime T0) Setup() => (new InstrumentData(Es, new SimulatedFeed()), DateTime.UtcNow.AddMinutes(-2));

    [Fact]
    public void A_wall_that_appears_and_is_then_traded_through_is_added_and_eaten()
    {
        var (data, t0) = Setup();
        data.DepthHistory.Add(t0, Book(t0, 0));
        data.DepthHistory.Add(t0.AddSeconds(1), Book(t0, 600));                 // 600 lots appear at 5003 (the typical level is 5)
        data.DepthHistory.Add(t0.AddSeconds(20), Book(t0, 600));
        for (int i = 0; i < 4; i++) data.Trades.Add(new Trade(i + 1, t0.AddSeconds(20.5 + i * 0.1), 5003m, 100, TradeSide.Buy));
        data.DepthHistory.Add(t0.AddSeconds(22), Book(t0, 150));                // 450 of it went against buyers
        data.DepthHistory.Add(t0.AddSeconds(23), Book(t0, 0));                  // and the rest of it was traded too

        var d = new BookEventDetector();
        d.Update(data.DepthHistory, data.Trades);
        Assert.Equal(new[] { BookEventKind.Added, BookEventKind.Eaten }, d.Events.Select(e => e.Kind));
        Assert.All(d.Events, e => { Assert.True(e.Ask); Assert.Equal(Es.ToTicks(5003m), e.Ticks); });
        Assert.Equal(600, d.Events[0].Size);
        Assert.Contains("added", d.Events[0].Text(Es));
        Assert.Contains("eaten", d.Events[1].Text(Es));

        d.Update(data.DepthHistory, data.Trades);                               // nothing new: the same snapshots are not counted twice
        Assert.Equal(2, d.Events.Count);
    }

    [Fact]
    public void A_wall_that_vanishes_without_trades_is_pulled_and_one_that_vanishes_within_seconds_is_a_flash()
    {
        var (data, t0) = Setup();
        data.DepthHistory.Add(t0, Book(t0, 0));
        data.DepthHistory.Add(t0.AddSeconds(1), Book(t0, 600));                 // a wall that stays 30 s ...
        data.DepthHistory.Add(t0.AddSeconds(31), Book(t0, 600));
        data.DepthHistory.Add(t0.AddSeconds(32), Book(t0, 0));                  // ... and goes, with nothing traded: pulled
        data.DepthHistory.Add(t0.AddSeconds(40), Book(t0, 800));                // another one ...
        data.DepthHistory.Add(t0.AddSeconds(41), Book(t0, 0));                  // ... gone a second later: flash

        var d = new BookEventDetector();
        d.Update(data.DepthHistory, data.Trades);
        Assert.Equal(new[] { BookEventKind.Added, BookEventKind.Pulled, BookEventKind.Added, BookEventKind.Flash }, d.Events.Select(e => e.Kind));
    }

    [Fact]
    public void A_level_that_only_grows_a_little_is_not_an_event()
    {
        var (data, t0) = Setup();
        data.DepthHistory.Add(t0, Book(t0, 0));
        data.DepthHistory.Add(t0.AddSeconds(1), Book(t0, 20));                  // 20 lots: below 6 x the median level (30)
        data.DepthHistory.Add(t0.AddSeconds(2), Book(t0, 0));
        var d = new BookEventDetector();
        d.Update(data.DepthHistory, data.Trades);
        Assert.Empty(d.Events);
    }
}

/// <summary>
/// The detector on the REAL Bybit BTCUSDT perpetual book for a minute (it only runs when BOOKEVENTS_LIVE is set, and records nothing to disk):
/// <c>$env:BOOKEVENTS_LIVE='1'; dotnet test --filter BookEventsLive --logger "console;verbosity=detailed"</c>.
/// </summary>
public class BookEventsLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class Sink : IMarketDataSink
    {
        public readonly DepthHistory History = new();
        public TradeStore Trades;
        public readonly object Gate = new();
        public int Books;
        public Sink(Instrument i) => Trades = new TradeStore(i);
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { lock (Gate) foreach (var t in trades) Trades.Add(t); }
        public void OnDepth(Instrument instrument, DepthUpdate update) { lock (Gate) { Books++; History.Add(update.Snapshot.Time, update.Snapshot); } }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }

    [Fact]
    public async Task BookEventsLive_real_bybit_btcusdt_book()
    {
        if (Environment.GetEnvironmentVariable("BOOKEVENTS_LIVE") is not { Length: > 0 }) return;
        using var feed = new TradingHub.Feeds.Bybit.BybitFeed();
        var inst = (await feed.GetInstrumentsAsync(CancellationToken.None)).First(i => i.Symbol == "BTCUSDT");
        var sink = new Sink(inst);
        using (feed.Subscribe(inst, sink)) await Task.Delay(TimeSpan.FromSeconds(75));
        var d = new BookEventDetector();
        lock (sink.Gate) d.Update(sink.History, sink.Trades);
        output.WriteLine($"BOOKS {sink.Books}, snapshots kept {sink.History.Count}, trades {sink.Trades.Count}, events {d.Events.Count}: " +
                         string.Join(", ", d.Events.GroupBy(e => e.Kind).Select(g => $"{g.Key}={g.Count()}")));
        foreach (var e in d.Events.TakeLast(12)) output.WriteLine($"  {e.Time:HH:mm:ss.f} {e.Text(inst)}");
        Assert.True(sink.History.Count > 100, "the real book arrived");
    }
}

public class BookEventGrouperTests
{
    private static readonly Instrument Inst = new() { Symbol = "BTCUSD", FeedId = "test", TickSize = 1m, VolumeStep = 0.001m };
    private static readonly DateTime T = new(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);

    private static BookEvent E(double sec, BookEventKind k, bool ask, long ticks, decimal size) => new(T.AddSeconds(sec), k, ask, ticks, size);

    [Fact]
    public void A_re_quoted_ladder_is_one_line_and_events_apart_in_time_or_kind_are_not_merged()
    {
        var events = new[]
        {
            E(0.0, BookEventKind.Pulled, true, 80500, 1.0m), E(0.1, BookEventKind.Pulled, true, 80502, 1.2m), E(0.2, BookEventKind.Pulled, true, 80510, 0.8m), // one ladder
            E(0.3, BookEventKind.Added, true, 80520, 3.0m),                                                                                                  // another kind
            E(10, BookEventKind.Pulled, true, 80500, 1.1m),                                                                                                  // later
            E(10.2, BookEventKind.Pulled, false, 80400, 1.0m),                                                                                               // other side
        };
        var g = BookEventGrouper.Group(events, minShare: 0);
        Assert.Equal(new[] { 3, 1, 1, 1 }, g.Select(x => x.Count));
        Assert.Equal(3.0m, g[0].TotalSize);
        Assert.Equal("3 offers pulled at 80500–80510, cancelled not traded (3 in all)", g[0].Text(Inst));
        Assert.Contains("Big offer added: 3 at 80520", g[1].Text(Inst));                       // a single event reads as before
    }

    [Fact]
    public void Crumbs_next_to_the_biggest_line_are_dropped()
    {
        var events = new[] { E(0, BookEventKind.Added, false, 80000, 10m), E(20, BookEventKind.Added, false, 80001, 0.5m), E(40, BookEventKind.Pulled, true, 80100, 4m) };
        var g = BookEventGrouper.Group(events, minShare: 0.2m);
        Assert.Equal(new[] { 10m, 4m }, g.Select(x => x.TotalSize));                            // 0.5 is under 20% of 10
    }
}
