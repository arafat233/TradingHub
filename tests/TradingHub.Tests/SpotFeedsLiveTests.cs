using TradingHub.Feeds;
using TradingHub.Feeds.Coinbase;
using TradingHub.Feeds.Kraken;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Talks to the real public Kraken and Coinbase services, so it only runs with TRADINGHUB_LIVE_TESTS=1 (and needs the internet).</summary>
public class SpotFeedsLiveTests
{
    private static bool Live => Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_TESTS") == "1";

    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public int Depth;
        public int MaxLevels;

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { lock (Trades) Trades.AddRange(trades); }

        public void OnDepth(Instrument instrument, DepthUpdate update) { Depth++; MaxLevels = Math.Max(MaxLevels, update.Snapshot.Bids.Length + update.Snapshot.Asks.Length); }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { }
    }

    private static async Task Check(IMarketDataFeed feed, string symbol)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var all = await feed.GetInstrumentsAsync(cts.Token);
        var inst = all.First(i => i.Symbol == symbol);
        Assert.True(inst.TickSize > 0 && inst.VolumeStep > 0);

        var hist = new List<Trade>();
        await feed.LoadTradesAsync(inst, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, null, b => { hist.AddRange(b); return true; }, null, cts.Token);
        Assert.NotEmpty(hist);
        Assert.True(hist.Zip(hist.Skip(1)).All(p => p.Second.Time >= p.First.Time), "history comes in time order");
        Assert.All(hist, t => Assert.True(t.Price > 0 && t.Volume > 0 && t.Side != TradeSide.None));

        var sink = new Sink();
        using (feed.Subscribe(inst, sink))
        {
            for (int i = 0; i < 40 && (sink.Depth == 0 || sink.Trades.Count == 0); i++) await Task.Delay(500);
        }

        Assert.True(sink.Depth > 0, "the book arrived");
        Assert.True(sink.MaxLevels > 10, $"the book has depth ({sink.MaxLevels} levels)");
        Assert.NotEmpty(sink.Trades);
    }

    [Fact]
    public async Task Kraken_gives_history_tape_and_book()
    {
        if (!Live) return;
        using var feed = new KrakenFeed();
        await Check(feed, "BTC/USD");
    }

    [Fact]
    public async Task Coinbase_gives_history_tape_and_book()
    {
        if (!Live) return;
        using var feed = new CoinbaseFeed();
        await Check(feed, "BTC-USD");
    }
}
