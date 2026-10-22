using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.LondonStrategicEdge;
using TradingHub.Market;
using Xunit.Abstractions;

namespace TradingHub.Tests;

/// <summary>
/// Runs the feed against the REAL service. It only does anything when LSE_API_KEY is set in the environment (it never is on a build
/// server), so the normal test run skips it: <c>$env:LSE_API_KEY='…'; dotnet test --filter LseLive --logger "console;verbosity=detailed"</c>.
/// It uses a little of the free allowance (one catalog, a few candles, a few seconds of ticks).
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
        var es = list.FirstOrDefault(i => i.Symbol == "ES.F");
        Assert.NotNull(es);
        output.WriteLine($"ES.F: tick {es.TickSize}, point value {es.PointValue}, {es.Description}, first data {await feed.FirstAvailableAsync(es, ct):yyyy-MM-dd}");

        // 1) candles: the last day in 5 minute bars
        var klines = new List<Kline>();
        await feed.LoadKlinesAsync(es, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow, 5, 0, b => { klines.AddRange(b); return true; }, null, ct);
        output.WriteLine($"KLINES 5m last day: {klines.Count}; first {DateTimeOffset.FromUnixTimeMilliseconds(klines[0].OpenTimeMs):u} O{klines[0].Open} C{klines[0].Close} V{klines[0].Volume}; last {DateTimeOffset.FromUnixTimeMilliseconds(klines[^1].OpenTimeMs):u} C{klines[^1].Close}");
        Assert.True(klines.Count > 50);

        // 2) ticks: the last 15 minutes replayed
        var trades = new List<Trade>();
        await feed.LoadTradesAsync(es, DateTime.UtcNow.AddMinutes(-15), DateTime.UtcNow, null, b => { trades.AddRange(b); return true; }, null, ct);
        output.WriteLine($"TICK HISTORY 15 min: {trades.Count} trades; {trades.Count(t => t.Side == TradeSide.Buy)} buys / {trades.Count(t => t.Side == TradeSide.Sell)} sells; first {trades[0].Time:HH:mm:ss.fff} {trades[0].Price} x{trades[0].Volume}; last {trades[^1].Time:HH:mm:ss.fff} {trades[^1].Price}");
        Assert.True(trades.Count > 100);
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.First.Time <= p.Second.Time && p.First.Id < p.Second.Id), "in time order with increasing ids");
        Assert.True(trades.All(t => t.Price % 0.25m == 0), "prices land on the ES tick");

        // 3) live: a few seconds of ES.F
        var sink = new Sink();
        using (feed.Subscribe(es, sink))
        {
            var end = DateTime.UtcNow.AddSeconds(12);
            while (DateTime.UtcNow < end) await Task.Delay(100, ct);
        }

        lock (sink.Trades)
        {
            output.WriteLine($"LIVE 12 s: {sink.Trades.Count} trades, {sink.Depth.Count} book updates; status: {string.Join(" > ", sink.Status.Select(s => s.Item1))}");
            if (sink.Trades.Count > 0) output.WriteLine($"  e.g. {sink.Trades[0].Time:HH:mm:ss.fff} {sink.Trades[0].Price} x{sink.Trades[0].Volume} {sink.Trades[0].Side}");
            Assert.True(sink.Trades.Count > 0, "ES.F ticks arrived (the server calls the symbol SP500.F)");
            Assert.True(sink.Trades.All(t => t.Price % 0.25m == 0));
        }

        // 4) the way the app does it: the live chart is up and the history is requested at the same time (the free plan allows ONE websocket)
        var sink2 = new Sink();
        var hist2 = new List<Trade>();
        using (feed.Subscribe(es, sink2))
        {
            await Task.Delay(3000, ct);
            await feed.LoadTradesAsync(es, DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow, null, b => { hist2.AddRange(b); return true; }, null, ct);
            var end2 = DateTime.UtcNow.AddSeconds(15);
            int live0;
            lock (sink2.Trades) live0 = sink2.Trades.Count;
            while (DateTime.UtcNow < end2) await Task.Delay(100, ct);
            lock (sink2.Trades)
                output.WriteLine($"LIVE + HISTORY TOGETHER: history {hist2.Count} ticks, live {live0} before and {sink2.Trades.Count} after; status: {string.Join(" > ", sink2.Status.Select(x => x.Item1 + (x.Item2 != null ? " (" + x.Item2 + ")" : "")))}");
            lock (sink2.Status) Assert.DoesNotContain(sink2.Status, x => x.Item1 == FeedStatus.Error);
        }

        Assert.True(hist2.Count > 50, "the history came back while a live chart was open");
        lock (sink2.Trades) Assert.True(sink2.Trades.Count > 0, "and the live chart kept receiving ticks");
    }
}
