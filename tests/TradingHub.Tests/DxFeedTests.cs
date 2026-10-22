using System.Text.Json.Nodes;
using TradingHub.Feeds;
using TradingHub.Feeds.DxFeed;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>dxFeed (dxLink) against a fake server: token, trades with their side, Level 2 snapshots and front-month symbols.</summary>
public class DxFeedTests
{
    private static async Task Until(Func<bool> condition, int ms = 6000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public readonly List<DepthUpdate> Depths = new();
        public volatile FeedStatus Status;
        public string? Message;

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            lock (Trades) Trades.AddRange(trades);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update)
        {
            lock (Depths) Depths.Add(update);
        }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            Status = status;
            Message = message;
        }
    }

    private static JsonObject Tns(long ms, double? price, double size, string side, int flags = 0, bool spreadLeg = false) => new()
    {
        ["eventType"] = "TimeAndSale", ["eventSymbol"] = "x", ["time"] = ms, ["price"] = price is { } p ? JsonValue.Create(p) : JsonValue.Create("NaN"),
        ["size"] = size, ["aggressorSide"] = side, ["eventFlags"] = flags, ["spreadLeg"] = spreadLeg, ["validTick"] = true,
    };

    [Fact]
    public async Task Trades_with_their_side_and_the_level2_book_arrive_after_the_token()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var server = new FakeDxLinkServer();
        server.Trades.Add(Tns(now, 24500.25, 3, "BUY"));
        server.Trades.Add(Tns(now + 1, 24500.0, 2, "SELL"));
        server.Trades.Add(Tns(now + 2, null, 1, "BUY"));                 // NaN price: skipped
        server.Trades.Add(Tns(now + 3, 24400.0, 1, "BUY", spreadLeg: true)); // spread leg: skipped
        server.Trades.Add(Tns(now + 4, 24500.5, 1, "SELL", flags: 0x2));     // removed event: skipped
        server.Books.Add((new[] { new[] { 24500.0, 10 }, new[] { 24499.75, 20 }, new[] { 24499.5, 30 } }, new[] { new[] { 24500.25, 12 }, new[] { 24500.5, 22 } }));
        server.Books.Add((new[] { new[] { 24500.0, 11 } }, new[] { new[] { 24500.25, 9 } }));

        var feed = new DxFeedFeed(new DxFeedSettings { Url = server.Uri, Token = "good-token", Products = new() { "NQ" } });
        var nq = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        Assert.Equal(20m, nq.PointValue);
        var sink = new Sink();
        using var sub = feed.Subscribe(nq, sink);
        await Until(() => sink.Trades.Count >= 2 && sink.Depths.Count >= 2);

        Assert.Equal(FeedStatus.Connected, sink.Status);
        Assert.Equal(2, sink.Trades.Count);
        Assert.Equal((24500.25m, TradeSide.Buy, 3m), (sink.Trades[0].Price, sink.Trades[0].Side, sink.Trades[0].Volume));
        Assert.Equal(TradeSide.Sell, sink.Trades[1].Side);
        Assert.True(sink.Trades[1].Id > sink.Trades[0].Id);

        // each snapshot is the whole book: the second one leaves a single level per side
        var first = sink.Depths[0].Snapshot;
        Assert.Equal(3, first.Bids.Length);
        Assert.Equal(nq.ToTicks(24500m), first.BestBidTicks);
        Assert.Equal(12m, first.BestAskSize);
        var second = sink.Depths[1];
        Assert.True(second.IsFullRefresh);
        Assert.Single(second.Snapshot.Bids);
        Assert.Equal(9m, second.Snapshot.BestAskSize);

        // the DOM asked for the front month with the CME Globex source
        JsonObject dom;
        lock (server.Received) dom = server.Received.Single(m => (string?)m["service"] == "DOM");
        Assert.Equal(feed.DxSymbol("NQ", "CME", DateTime.UtcNow), (string?)dom["parameters"]!["symbol"]);
        Assert.Equal("glbx", (string?)dom["parameters"]!["sources"]![0]);
    }

    [Fact]
    public async Task A_refused_level2_source_keeps_the_trades_and_the_test_says_why()
    {
        // the real server answers ERROR on the DOM channel for a source it does not have: that used to drop the whole connection
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var server = new FakeDxLinkServer();
        server.Trades.Add(Tns(now, 5000.25, 1, "BUY"));
        var settings = new DxFeedSettings { Url = server.Uri, Token = "good-token", DomSource = "GLBX", Products = new() { "ES" } };
        var feed = new DxFeedFeed(settings);
        var es = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using (feed.Subscribe(es, sink))
        {
            await Until(() => sink.Trades.Count > 0);
            await Task.Delay(300);
            Assert.Single(sink.Trades);
            Assert.Equal(FeedStatus.Connected, sink.Status);
        }

        var (ok, message) = await new DxFeedFeed(settings).TestAsync(CancellationToken.None);
        Assert.True(ok, message);
        Assert.Contains("Level 2 not available: Unsupported source: 'GLBX'", message);
    }

    [Fact]
    public async Task A_wrong_token_is_reported_instead_of_connecting()
    {
        using var server = new FakeDxLinkServer();
        var feed = new DxFeedFeed(new DxFeedSettings { Url = server.Uri, Token = "wrong", Products = new() { "ES" } });
        var (ok, message) = await feed.TestAsync(CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("rejected the token", message);

        server.Books.Add((new[] { new[] { 5000.0, 1 } }, new[] { new[] { 5000.25, 1 } }));
        var good = new DxFeedFeed(new DxFeedSettings { Url = server.Uri, Token = "good-token", Products = new() { "ES" } });
        (ok, message) = await good.TestAsync(CancellationToken.None);
        Assert.True(ok, message);
        Assert.Contains("Level 2 (glbx) arriving", message);
    }

    [Fact]
    public void Front_month_symbols_for_indices_and_gold_and_exact_ones_from_products()
    {
        var feed = new DxFeedFeed(new DxFeedSettings { Products = new() { "NQ", "GC", "/CLZ26:XNYM" } });
        var oct21 = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("/NQZ26:XCME", feed.DxSymbol("NQ", "CME", oct21));
        Assert.Equal("/NQH27:XCME", feed.DxSymbol("NQ", "CME", new DateTime(2026, 12, 14, 12, 0, 0, DateTimeKind.Utc))); // roll Monday of the December expiry
        Assert.Equal("/GCZ26:XCEC", feed.DxSymbol("GC", "COMEX", oct21));
        Assert.Equal("/MGCG27:XCEC", feed.DxSymbol("MGC", "COMEX", new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("/CLZ26:XNYM", feed.DxSymbol("CL", "NYMEX", oct21));
        Assert.Null(feed.DxSymbol("ZN", "CBOT", oct21));
        Assert.Equal("MGC", DxFeedFeed.RootOf("/MGCZ26:XCEC"));
    }
}
