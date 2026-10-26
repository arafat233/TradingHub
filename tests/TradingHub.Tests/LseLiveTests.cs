using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.LondonStrategicEdge;
using TradingHub.Market;
using Xunit.Abstractions;

namespace TradingHub.Tests;

/// <summary>
/// Runs the feed against the REAL service. It only does anything when LSE_API_KEY is set in the environment (it never is on a build
/// server), so the normal test run skips it: <c>$env:LSE_API_KEY='…'; dotnet test --filter LseLive --logger "console;verbosity=detailed"</c>.
/// It uses a little of the free allowance (a few candle exports, a few seconds of ticks).
/// </summary>
public class LseLiveTests(ITestOutputHelper output)
{
    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public readonly List<DepthUpdate> Depth = new();
        public readonly List<(FeedStatus, string?)> Status = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { lock (Trades) Trades.AddRange(trades); }
        public void OnDepth(Instrument instrument, DepthUpdate update) { lock (Depth) Depth.Add(update); }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { lock (Status) Status.Add((status, message)); }
    }

    [Fact]
    public async Task LseLive_the_real_service_through_the_feed()
    {
        var key = Environment.GetEnvironmentVariable("LSE_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return;
        using var feed = new LseFeed(new LseSettings { ApiKey = key });
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = cts.Token;

        var (ok, msg) = await feed.TestAsync(ct);
        output.WriteLine($"TEST: {ok} {msg}");
        Assert.True(ok);

        var list = await feed.GetInstrumentsAsync(ct);
        output.WriteLine($"INSTRUMENTS: {list.Count}; by class: " + string.Join(", ", list.GroupBy(i => i.Exchange).Select(g => $"{g.Key}={g.Count()}")));
        var btc = list.FirstOrDefault(i => i.Symbol == "BTC/USD");
        Assert.NotNull(btc);

        // 1) candles: the last 2 days in 5 minute bars, for a crypto (BTC/USD streams, BTCUSDT is stored) and a forex pair (EUR/USD streams, EUR_USD is stored)
        foreach (var symbol in new[] { "BTC/USD", "EUR/USD", "AAPL" })
        {
            var inst = list.First(i => i.Symbol == symbol);
            var klines = new List<Kline>();
            await feed.LoadKlinesAsync(inst, DateTime.UtcNow.AddDays(-5), DateTime.UtcNow.AddDays(-1), 5, 0, b => { klines.AddRange(b); return true; }, null, ct);
            output.WriteLine($"KLINES {symbol} 5m: {klines.Count}; first {DateTimeOffset.FromUnixTimeMilliseconds(klines[0].OpenTimeMs):u} O{klines[0].Open} C{klines[0].Close} V{klines[0].Volume}; last {DateTimeOffset.FromUnixTimeMilliseconds(klines[^1].OpenTimeMs):u} C{klines[^1].Close}");
            Assert.True(klines.Count > 50);
        }

        // 1b) futures, added by hand because the service no longer lists them: GC.F is the archive's GOLD (candles, real ticks) and streams as GOLD.F
        var gc = list.FirstOrDefault(i => i.Symbol == "GC.F");
        Assert.NotNull(gc);
        var gcKlines = new List<Kline>();
        await feed.LoadKlinesAsync(gc, DateTime.UtcNow.AddDays(-4), DateTime.UtcNow, 5, 0, b => { gcKlines.AddRange(b); return true; }, null, ct);
        output.WriteLine($"KLINES GC.F 5m: {gcKlines.Count}; last {DateTimeOffset.FromUnixTimeMilliseconds(gcKlines[^1].OpenTimeMs):u} C{gcKlines[^1].Close}");
        Assert.True(gcKlines.Count > 50);
        var gcTicks = new List<Trade>();
        var lastBar = DateTimeOffset.FromUnixTimeMilliseconds(gcKlines[^1].OpenTimeMs).UtcDateTime;
        await feed.LoadTradesAsync(gc, lastBar.AddMinutes(-20), lastBar, null, b => { gcTicks.AddRange(b); return true; }, null, ct);
        output.WriteLine($"TICKS GC.F 20 min: {gcTicks.Count}; {gcTicks.Count(t => t.Side == TradeSide.Buy)} buys / {gcTicks.Count(t => t.Side == TradeSide.Sell)} sells");
        Assert.True(gcTicks.Count > 100);
        Assert.True(gcTicks.All(t => t.Price % 0.1m == 0), "prices land on the gold tick");

        // 1c) a future live: no websocket, the tick export is polled; during Globex hours the ticks are current to the second
        var gcSink = new Sink();
        using (feed.Subscribe(gc, gcSink))
        {
            var endGc = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < endGc) await Task.Delay(200, ct);
        }

        lock (gcSink.Trades)
        {
            output.WriteLine($"LIVE GC.F 30 s (polled): {gcSink.Trades.Count} trades; status {string.Join(" > ", gcSink.Status.Select(s => s.Item1))}" +
                             (gcSink.Trades.Count > 0 ? $"; newest {gcSink.Trades[^1].Time:HH:mm:ss.fff} against now {DateTime.UtcNow:HH:mm:ss}" : ""));
            if (Environment.GetEnvironmentVariable("LSE_FUTURES_OPEN") is { Length: > 0 })
            {
                Assert.True(gcSink.Trades.Count > 0, "gold ticks arrived by polling");
                Assert.True(DateTime.UtcNow - gcSink.Trades[^1].Time < TimeSpan.FromSeconds(20), "and they are current");
            }
        }

        // 2) live: a few seconds of BTC/USD
        var sink = new Sink();
        using (feed.Subscribe(btc, sink))
        {
            var end = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < end) await Task.Delay(100, ct);
        }

        lock (sink.Trades)
        {
            output.WriteLine($"LIVE 15 s: {sink.Trades.Count} trades, {sink.Depth.Count} book updates; status: {string.Join(" > ", sink.Status.Select(s => s.Item1))}");
            if (sink.Trades.Count > 0) output.WriteLine($"  e.g. {sink.Trades[0].Time:HH:mm:ss.fff} {sink.Trades[0].Price} x{sink.Trades[0].Volume} {sink.Trades[0].Side}");
            Assert.True(sink.Trades.Count > 0, "BTC/USD ticks arrived");
            Assert.True(sink.Trades.All(t => DateTime.UtcNow - t.Time < TimeSpan.FromMinutes(2)), "the times are read right (9 decimals, UTC)");
        }
        lock (sink.Status) Assert.DoesNotContain(sink.Status, x => x.Item1 == FeedStatus.Error);
    }
}
