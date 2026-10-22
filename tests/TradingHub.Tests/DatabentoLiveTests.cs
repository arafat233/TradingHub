using System.Security.Cryptography;
using System.Text;
using TradingHub.Feeds;
using TradingHub.Feeds.Databento;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Databento live against a fake gateway: CRAM login, MBP-10 (trades + Level 2), trades only and errors.</summary>
public class DatabentoLiveTests
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

    private static long Ns(DateTime t) => (t - DateTime.UnixEpoch).Ticks * 100;

    [Fact]
    public void The_cram_answer_is_the_sha256_of_challenge_and_key_plus_the_bucket()
    {
        string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("abc|db-key12345"))) + "-12345";
        Assert.Equal(expected, DatabentoLiveFeed.CramResponse("abc", "db-key12345"));
    }

    [Fact]
    public async Task Mbp10_gives_trades_with_their_side_and_the_top_10_book()
    {
        var t0 = DateTime.UtcNow;
        using var gw = new FakeDatabentoGateway();
        gw.Records.Add(FakeDatabentoGateway.Message(0x17, "Heartbeat"));
        gw.Records.Add(FakeDatabentoGateway.Mbp10(Ns(t0), 'T', 'A', 24500.25, 3, new (double?, uint, double?, uint)[]
        {
            (24500.0, 10, 24500.25, 12), (24499.75, 20, 24500.5, 22), (24499.5, 30, null, 0), // the third ask level is empty
        }));
        gw.Records.Add(FakeDatabentoGateway.Mbp10(Ns(t0) + 1000, 'A', 'B', 24500.0, 5, new (double?, uint, double?, uint)[] { (24500.0, 15, 24500.25, 12) }));
        var feed = new DatabentoLiveFeed(new DatabentoLiveSettings { ApiKey = gw.Key, Gateway = gw.Gateway, Products = new() { "NQ" } });
        var nq = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using var sub = feed.Subscribe(nq, sink);
        await Until(() => sink.Trades.Count > 0 && sink.Depths.Count > 0);
        await Task.Delay(200);

        Assert.Equal(FeedStatus.Connected, sink.Status);
        lock (gw.Lines) Assert.Contains("schema=mbp-10|stype_in=continuous|symbols=NQ.v.0|snapshot=0|is_last=1", gw.Lines);
        lock (gw.Lines) Assert.Contains("start_session=0", gw.Lines);

        // only the action T record is a trade: A = the aggressor sold
        var trade = Assert.Single(sink.Trades);
        Assert.Equal((24500.25m, 3m, TradeSide.Sell), (trade.Price, trade.Volume, trade.Side));
        Assert.True(Math.Abs((trade.Time - t0).TotalMilliseconds) < 1);

        // the book of the first record (the second arrives within 50 ms and is shown with the next one)
        var book = sink.Depths[0].Snapshot;
        Assert.Equal(3, book.Bids.Length);
        Assert.Equal(2, book.Asks.Length);
        Assert.Equal(nq.ToTicks(24500m), book.BestBidTicks);
        Assert.Equal(12m, book.BestAskSize);
    }

    [Fact]
    public async Task Trades_only_subscribes_to_the_trades_schema()
    {
        using var gw = new FakeDatabentoGateway();
        gw.Records.Add(FakeDatabentoGateway.Trade(Ns(DateTime.UtcNow), 2650.1, 2, 'B'));
        var feed = new DatabentoLiveFeed(new DatabentoLiveSettings { ApiKey = gw.Key, Gateway = gw.Gateway, Level2 = false, Products = new() { "GC" } });
        var gc = Assert.Single(await feed.GetInstrumentsAsync(CancellationToken.None));
        var sink = new Sink();
        using var sub = feed.Subscribe(gc, sink);
        await Until(() => sink.Trades.Count > 0);
        var t = Assert.Single(sink.Trades);
        Assert.Equal((2650.1m, TradeSide.Buy), (t.Price, t.Side));
        Assert.Empty(sink.Depths);
        lock (gw.Lines) Assert.Contains(gw.Lines, l => l.StartsWith("schema=trades|stype_in=continuous|symbols=GC.v.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bad_key_and_a_gateway_error_are_reported()
    {
        using var gw = new FakeDatabentoGateway();
        var bad = new DatabentoLiveFeed(new DatabentoLiveSettings { ApiKey = "db-WRONGKEY000000000000000zzzzz", Gateway = gw.Gateway, Products = new() { "ES" } });
        var (ok, msg) = await bad.TestAsync(CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("Authentication failed", msg);

        gw.Records.Add(FakeDatabentoGateway.Message(0x15, "Subscription request for mbp-10 data failed: no live license"));
        var good = new DatabentoLiveFeed(new DatabentoLiveSettings { ApiKey = gw.Key, Gateway = gw.Gateway, Products = new() { "ES" } });
        (ok, msg) = await good.TestAsync(CancellationToken.None);
        Assert.False(ok);
        Assert.Contains("no live license", msg);
    }
}
