using System.Text.Json;
using TradingHub.Feeds;
using TradingHub.Feeds.Coinbase;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>
/// The Coinbase order-level (Level 3) feed that an API key turns on: the signed subscribe, the REST snapshot of every order, the websocket
/// messages applied after it (open, match, change, done), and the sequence rules, against a fake server. The real "full" channel needs a key, so it
/// was never run here; the real public parts (the level 3 snapshot, the refusal without a key) were checked by hand against the live service.
/// </summary>
public class CoinbaseFullTests
{
    // a base64 secret and the signature an independent HMAC-SHA256 implementation (Python) gives for timestamp 1700000000
    private const string Secret = "dGVzdC1zZWNyZXQtYnl0ZXMtMDEyMzQ1Njc4OWFiY2Q=";
    private const string SignatureAt1700000000 = "iCgguIBHiNJKMvAbMeRMJ3hVb9g92aqAcGVQLcNlui4=";

    private static readonly Instrument Btc = new() { Symbol = "BTC-USD", FeedId = CoinbaseFeed.SpotId, TickSize = 0.01m, VolumeStep = 0.00000001m };

    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public readonly List<DepthUpdate> Depth = new();
        public readonly List<OrderEvent> Orders = new();
        public readonly List<(FeedStatus, string?)> Status = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { lock (Trades) Trades.AddRange(trades); }
        public void OnDepth(Instrument instrument, DepthUpdate update) { lock (Depth) Depth.Add(update); }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { lock (Status) Status.Add((status, message)); }
        public void OnOrderEvents(Instrument instrument, IReadOnlyList<OrderEvent> events) { lock (Orders) Orders.AddRange(events); }
    }

    private static async Task Until(Func<bool> condition, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    private static string Msg(long seq, string type, string fields) =>
        $"{{\"type\":\"{type}\",\"sequence\":{seq},\"product_id\":\"BTC-USD\",\"time\":\"2026-10-26T10:00:{seq % 60:00}.123456Z\",{fields}}}";

    /// <summary>The fake: REST snapshot with sequence 1000, and after the signed subscribe the messages below (some old, some new, one gap if asked).</summary>
    private static FakeHttpServer Server(out List<string> received, bool gap = false, bool rejectKey = false)
    {
        var got = new List<string>();
        received = got;
        var s = new FakeHttpServer();
        s.OnHttp = req =>
        {
            if (req.Route == "/products/BTC-USD/book" && req.Query("level") == "3")
                return (200, "{\"sequence\":1000,\"bids\":[[\"100.00\",\"1.0\",\"b1\"],[\"99.00\",\"2.0\",\"b2\"],[\"99.00\",\"0.5\",\"b3\"]],\"asks\":[[\"101.00\",\"3.0\",\"a1\"]]}");
            return (404, "{}");
        };
        s.OnSocket = async (path, ws, ct) =>
        {
            var text = await FakeHttpServer.ReceiveText(ws, ct);
            if (text == null) return;
            lock (got) got.Add(text);
            if (rejectKey)
            {
                await FakeHttpServer.SendText(ws, "{\"type\":\"error\",\"message\":\"Failed to subscribe\",\"reason\":\"level2, level3, and full channels now require authentication.\"}", ct);
                return;
            }

            await FakeHttpServer.SendText(ws, "{\"type\":\"subscriptions\",\"channels\":[{\"name\":\"full\",\"product_ids\":[\"BTC-USD\"]}]}", ct);
            await FakeHttpServer.SendText(ws, Msg(999, "open", "\"order_id\":\"old\",\"price\":\"105.00\",\"remaining_size\":\"9\",\"side\":\"sell\""), ct);          // before the snapshot: dropped
            await FakeHttpServer.SendText(ws, Msg(1001, "open", "\"order_id\":\"a2\",\"price\":\"102.00\",\"remaining_size\":\"50\",\"side\":\"sell\""), ct);
            await FakeHttpServer.SendText(ws, Msg(1002, "match", "\"trade_id\":7,\"maker_order_id\":\"a1\",\"taker_order_id\":\"t1\",\"size\":\"1.0\",\"price\":\"101.00\",\"side\":\"sell\""), ct); // the maker sold: a buyer took it
            await FakeHttpServer.SendText(ws, Msg(1003, "change", "\"order_id\":\"a2\",\"new_size\":\"40\",\"old_size\":\"50\",\"price\":\"102.00\",\"side\":\"sell\",\"reason\":\"modify_order\""), ct);
            await FakeHttpServer.SendText(ws, Msg(1004, "done", "\"order_id\":\"a1\",\"reason\":\"filled\",\"remaining_size\":\"0\",\"price\":\"101.00\",\"side\":\"sell\""), ct);
            await FakeHttpServer.SendText(ws, Msg(1005, "done", "\"order_id\":\"a2\",\"reason\":\"canceled\",\"remaining_size\":\"40\",\"price\":\"102.00\",\"side\":\"sell\""), ct);
            await FakeHttpServer.SendText(ws, Msg(1006, "done", "\"order_id\":\"market\",\"reason\":\"filled\",\"side\":\"buy\""), ct);                                       // a market order never rested: not in the book
            if (gap) await FakeHttpServer.SendText(ws, Msg(1010, "open", "\"order_id\":\"x\",\"price\":\"103.00\",\"remaining_size\":\"1\",\"side\":\"sell\""), ct);          // 1007 to 1009 were lost
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
        };
        return s;
    }

    [Fact]
    public void The_signature_matches_an_independent_hmac_implementation()
    {
        Assert.Equal(SignatureAt1700000000, CoinbaseFeed.Sign(Secret, "1700000000"));
        Assert.Equal(SignatureAt1700000000, CoinbaseFeed.Sign("  " + Secret + "\n", "1700000000")); // a pasted secret with spaces still works
    }

    [Fact]
    public async Task With_a_key_the_book_is_the_rest_snapshot_plus_the_messages_after_it_and_every_order_event_is_reported()
    {
        using var fake = Server(out var received);
        using var feed = new CoinbaseFeed(new CoinbaseKeys("K", Secret, "P"), fake.Http, fake.Ws);
        Assert.True(feed.HasOrderLevel);
        var sink = new Sink();
        using var sub = feed.Subscribe(Btc, sink);
        await Until(() => { lock (sink.Orders) return sink.Orders.Count >= 5; });

        lock (sink.Orders)
        {
            Assert.Equal(new[] { OrderEventKind.Add, OrderEventKind.Modify, OrderEventKind.Modify, OrderEventKind.Fill, OrderEventKind.Cancel }, sink.Orders.Select(o => o.Kind));
            Assert.Equal(new[] { 50m, 2m, 40m, 2m, 40m }, sink.Orders.Select(o => o.Size));      // a1 had 3, the match took 1; "done" reports what it still had
            Assert.All(sink.Orders, o => Assert.True(o.Ask));
            Assert.Equal(sink.Orders[0].OrderId, sink.Orders[2].OrderId);                        // the same order, a2, through add, change and cancel
            Assert.Equal(sink.Orders[1].OrderId, sink.Orders[3].OrderId);                        // and a1 through the match and the fill
        }

        lock (sink.Trades)
        {
            var t = Assert.Single(sink.Trades);                                                  // the dropped message and the market order made no trade
            Assert.Equal((7L, 101m, 1m, TradeSide.Buy), (t.Id, t.Price, t.Volume, t.Side));      // the maker sold, so the aggressor bought
        }

        lock (sink.Depth)
        {
            Assert.True(sink.Depth[0].IsFullRefresh);
            Assert.Equal(2.5m, sink.Depth[0].Snapshot.GetSize(BookSide.Bid, Btc.ToTicks(99m)));  // two orders at one price add up
            var last = sink.Depth[^1].Snapshot;
            Assert.False(last.HasAsk);                                                           // both asks are gone again
            Assert.Equal(1m, last.GetSize(BookSide.Bid, Btc.ToTicks(100m)));
        }

        lock (received)
        {
            using var doc = JsonDocument.Parse(received[0]);
            var r = doc.RootElement;
            Assert.Equal("subscribe", r.GetProperty("type").GetString());
            Assert.Contains("full", r.GetProperty("channels").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal("K", r.GetProperty("key").GetString());
            Assert.Equal("P", r.GetProperty("passphrase").GetString());
            string ts = r.GetProperty("timestamp").GetString()!;
            Assert.Equal(CoinbaseFeed.Sign(Secret, ts), r.GetProperty("signature").GetString());
        }
    }

    [Fact]
    public async Task A_missing_sequence_number_means_messages_were_lost_so_the_feed_starts_over()
    {
        using var fake = Server(out _, gap: true);
        using var feed = new CoinbaseFeed(new CoinbaseKeys("K", Secret, "P"), fake.Http, fake.Ws);
        var sink = new Sink();
        using var sub = feed.Subscribe(Btc, sink);
        await Until(() => { lock (sink.Status) return sink.Status.Any(s => s.Item1 == FeedStatus.Reconnecting && s.Item2!.Contains("lost")); });
        lock (sink.Status) Assert.Contains(sink.Status, s => s.Item1 == FeedStatus.Reconnecting && s.Item2!.Contains("expected 1007, got 1010"));
    }

    [Fact]
    public async Task The_key_is_tested_with_a_signed_subscribe_and_a_refusal_is_explained()
    {
        using (var good = Server(out _))
        using (var feed = new CoinbaseFeed(new CoinbaseKeys("K", Secret, "P"), good.Http, good.Ws))
        {
            var (ok, msg) = await feed.TestKeysAsync(CancellationToken.None);
            Assert.True(ok);
            Assert.Contains("Level 3", msg);
        }

        using (var bad = Server(out _, rejectKey: true))
        using (var feed = new CoinbaseFeed(new CoinbaseKeys("K", Secret, "P"), bad.Http, bad.Ws))
        {
            var (ok, msg) = await feed.TestKeysAsync(CancellationToken.None);
            Assert.False(ok);
            Assert.Contains("require authentication", msg);
        }

        using var none = new CoinbaseFeed();
        Assert.False(none.HasOrderLevel);
        Assert.False((await none.TestKeysAsync(CancellationToken.None)).Ok);
        Assert.False(new CoinbaseFeed(new CoinbaseKeys("K", "", "P")).HasOrderLevel);             // an incomplete key is not a key: the public feed is used
    }

    [Fact]
    public async Task Without_a_key_the_public_level_2_feed_is_used_unsigned()
    {
        var first = new TaskCompletionSource<string>();
        var s = new FakeHttpServer();
        s.OnSocket = async (path, ws, ct) =>
        {
            var text = await FakeHttpServer.ReceiveText(ws, ct);
            first.TrySetResult(text ?? "");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        };
        using var fake = s;
        using var feed = new CoinbaseFeed(null, fake.Http, fake.Ws);
        using var sub = feed.Subscribe(Btc, new Sink());
        var msg = await first.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Contains("level2_batch", msg);
        Assert.DoesNotContain("signature", msg);
    }
}

/// <summary>The public parts of the order-level feed against the REAL Coinbase (only when BOOKEVENTS_LIVE is set): the level 3 snapshot and the refusal of a bad key.</summary>
public class CoinbaseFullLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class Sink : IMarketDataSink
    {
        public readonly List<DepthUpdate> Depth = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { }
        public void OnDepth(Instrument instrument, DepthUpdate update) { lock (Depth) Depth.Add(update); }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }

    [Fact]
    public async Task CoinbaseLive_real_level3_snapshot_and_a_bad_key_is_refused_with_the_reason()
    {
        if (Environment.GetEnvironmentVariable("BOOKEVENTS_LIVE") is not { Length: > 0 }) return;
        var btc = new Instrument { Symbol = "BTC-USD", FeedId = CoinbaseFeed.SpotId, TickSize = 0.01m, VolumeStep = 0.00000001m };

        // the real REST snapshot, with a fake websocket that accepts the key and then says nothing
        var s = new FakeHttpServer();
        s.OnSocket = async (path, ws, ct) =>
        {
            await FakeHttpServer.ReceiveText(ws, ct);
            await FakeHttpServer.SendText(ws, "{\"type\":\"subscriptions\",\"channels\":[]}", ct);
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
        };
        using (s)
        using (var feed = new CoinbaseFeed(new CoinbaseKeys("K", "dGVzdA==", "P"), null, s.Ws))
        {
            var sink = new Sink();
            using var sub = feed.Subscribe(btc, sink);
            await Task.Run(async () => { var end = DateTime.UtcNow.AddSeconds(40); while (DateTime.UtcNow < end) { lock (sink.Depth) if (sink.Depth.Count > 0) break; await Task.Delay(200); } });
            DepthSnapshot snap;
            lock (sink.Depth) snap = sink.Depth[0].Snapshot;
            output.WriteLine($"SNAPSHOT: {snap.Bids.Length} bid levels, {snap.Asks.Length} ask levels; best {btc.FromTicks(snap.Bids[0].PriceTicks)} / {btc.FromTicks(snap.Asks[0].PriceTicks)}; best bid size {snap.Bids[0].Size}");
            Assert.True(snap.Bids.Length >= 500 && snap.Asks.Length >= 500, "the book is capped at 500 levels a side");
            Assert.True(snap.Bids[0].PriceTicks < snap.Asks[0].PriceTicks, "the book is not crossed");
        }

        // the real server with an invalid key
        using var real = new CoinbaseFeed(new CoinbaseKeys("not-a-real-key", "dGVzdA==", "nope"));
        var (ok, msg) = await real.TestKeysAsync(CancellationToken.None);
        output.WriteLine($"BAD KEY: ok={ok} \"{msg}\"");
        Assert.False(ok);
    }
}
