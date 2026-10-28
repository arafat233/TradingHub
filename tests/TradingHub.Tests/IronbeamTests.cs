using TradingHub.Feeds;
using TradingHub.Feeds.Ironbeam;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>
/// Ironbeam: sign-in, stream, subscriptions, trades with their aggressor side and the order book, against a fake server that sends the message
/// shapes of Ironbeam's published API. The real service was not reachable when this was written (it needs an account), so the depth semantics
/// (whole book or changed levels) are covered both ways here and still have to be checked live.
/// </summary>
public class IronbeamTests
{
    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public readonly List<DepthUpdate> Depth = new();
        public readonly List<(FeedStatus Status, string? Message)> Status = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { lock (Trades) Trades.AddRange(trades); }
        public void OnDepth(Instrument instrument, DepthUpdate update) { lock (Depth) Depth.Add(update); }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { lock (Status) Status.Add((status, message)); }
    }

    private static async Task Until(Func<bool> condition, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = IronbeamFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1 };
    private const string EsSymbol = FakeIronbeam.EsSymbol;

    private static IronbeamSettings Settings(FakeHttpServer s) => new()
    {
        BaseUrl = s.Http + "/v2", Username = "me", Password = "pw", ApiKey = "key-1", Products = new() { EsSymbol },
    };

    [Fact]
    public async Task Products_become_instruments_and_the_front_month_symbol_follows_the_roll()
    {
        var feed = new IronbeamFeed(new IronbeamSettings { Products = new() { "ES", "GC", "XNYM:CL.Z26", "NOPE" } });
        var list = await feed.GetInstrumentsAsync(default);
        Assert.Equal(new[] { "ES", "GC", "CL" }, list.Select(i => i.Symbol).ToArray());
        Assert.All(list, i => Assert.Equal(IronbeamFeed.FeedId, i.FeedId));
        Assert.Equal(0.25m, list[0].TickSize);

        var utc = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("XCME:ES.Z26", feed.IronSymbol("ES", "CME", utc));
        Assert.Equal("XCEC:GC.Z26", feed.IronSymbol("GC", "COMEX", utc));          // gold: October moves on to December after the 25th of September
        Assert.Equal("XNYM:CL.Z26", feed.IronSymbol("CL", "NYMEX", utc));          // an exact symbol in Products wins
        Assert.Null(feed.IronSymbol("ZN", "CBOT", utc));                           // other roots have to be typed exactly
        Assert.Equal("ES", IronbeamFeed.RootOf("XCME:ES.U26"));
        Assert.Equal("6E", IronbeamFeed.RootOf("XCME:6E.U16"));
    }

    [Fact]
    public async Task It_signs_in_opens_the_stream_subscribes_and_turns_the_messages_into_trades_and_a_book()
    {
        using var server = FakeIronbeam.Server(out var log);
        using var feed = new IronbeamFeed(Settings(server));
        var sink = new Sink();
        using var sub = feed.Subscribe(Es, sink);
        await Until(() => { lock (sink.Trades) lock (sink.Depth) return sink.Trades.Count >= 3 && sink.Depth.Count >= 2; });

        // the calls the API asks for, with the token as a Bearer header and the symbol in the query
        string[] calls;
        lock (log) calls = log.ToArray();
        Assert.Contains(calls, l => l.StartsWith("POST /v2/auth") && l.Contains("\"username\":\"me\"") && l.Contains("\"password\":\"pw\"") && l.Contains("\"apikey\":\"key-1\""));
        Assert.Contains(calls, l => l.StartsWith("GET /v2/stream/create | Bearer tok-1"));
        Assert.Contains(calls, l => l.StartsWith($"GET /v2/market/trades/subscribe/s-1?symbols={EsSymbol} | Bearer tok-1"));
        Assert.Contains(calls, l => l.StartsWith($"GET /v2/market/depths/subscribe/s-1?symbols={EsSymbol} | Bearer tok-1"));
        Assert.Contains(calls, l => l == "WS /v2/stream/s-1?token=tok-1");

        lock (sink.Trades)
        {
            Assert.Equal(3, sink.Trades.Count);
            Assert.Equal((TradeSide.Buy, 5000.5m, 3m), (sink.Trades[0].Side, sink.Trades[0].Price, sink.Trades[0].Volume));
            Assert.Equal((TradeSide.Sell, 5000.25m, 2m), (sink.Trades[1].Side, sink.Trades[1].Price, sink.Trades[1].Volume));
            Assert.Equal(TradeSide.None, sink.Trades[2].Side);
            Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(1793008990000), sink.Trades[0].Time);
            Assert.True(sink.Trades[0].Id < sink.Trades[1].Id && sink.Trades[1].Id < sink.Trades[2].Id);
        }

        lock (sink.Depth)
        {
            var first = sink.Depth[0].Snapshot;
            Assert.Equal(12, first.GetSize(BookSide.Bid, Es.ToTicks(5000.25m)));
            Assert.Equal(20, first.GetSize(BookSide.Bid, Es.ToTicks(5000.0m)));
            Assert.Equal(7, first.GetSize(BookSide.Ask, Es.ToTicks(5000.5m)));
            Assert.Equal(9, first.GetSize(BookSide.Ask, Es.ToTicks(5000.75m)));

            // the update with only the levels that changed: the best bid is gone, the other levels stay
            var last = sink.Depth[^1].Snapshot;
            Assert.Equal(0, last.GetSize(BookSide.Bid, Es.ToTicks(5000.25m)));
            Assert.Equal(20, last.GetSize(BookSide.Bid, Es.ToTicks(5000.0m)));
            Assert.Equal(7, last.GetSize(BookSide.Ask, Es.ToTicks(5000.5m)));
            Assert.Equal(4, last.GetSize(BookSide.Ask, Es.ToTicks(5000.75m)));
        }

        lock (sink.Status) Assert.Contains(sink.Status, s => s.Status == FeedStatus.Connected);
    }

    [Fact]
    public async Task The_connection_test_says_whether_level_2_arrives()
    {
        using var server = FakeIronbeam.Server(out _);
        using var feed = new IronbeamFeed(Settings(server));
        var (ok, message) = await feed.TestAsync(CancellationToken.None);
        Assert.True(ok, message);
        Assert.Contains("Level 2 arriving", message);
        Assert.Contains("demo", message);
    }

    [Fact]
    public async Task An_account_without_depth_still_connects_and_the_test_says_so()
    {
        using var server = FakeIronbeam.Server(out _, depthRefused: true);
        using var feed = new IronbeamFeed(Settings(server));
        var (ok, message) = await feed.TestAsync(CancellationToken.None);
        Assert.True(ok, message);
        Assert.Contains("Level 2 not available", message);
        Assert.Contains("Depth is not enabled", message);
    }

    [Fact]
    public async Task A_refused_login_is_reported_once_and_not_retried_and_missing_fields_are_caught_before_any_call()
    {
        using var server = FakeIronbeam.Server(out var log, badLogin: true);
        using var bad = new IronbeamFeed(Settings(server));
        var (ok, message) = await bad.TestAsync(CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("rejected the user, password or API key", message);
        Assert.Contains("Invalid API key", message);
        await Task.Delay(300);
        lock (log) Assert.Equal(1, log.Count(l => l.StartsWith("POST /v2/auth")));      // it did not keep asking

        using var empty = new IronbeamFeed(new IronbeamSettings { BaseUrl = server.Http + "/v2", Products = new() { EsSymbol } });
        var (ok2, message2) = await empty.TestAsync(CancellationToken.None);
        Assert.False(ok2);
        Assert.StartsWith("Fill in your Ironbeam user, password and API key", message2);
        lock (log) Assert.Equal(1, log.Count(l => l.StartsWith("POST /v2/auth")));      // nothing was sent
    }
}
