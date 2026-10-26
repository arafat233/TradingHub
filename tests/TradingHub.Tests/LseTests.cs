using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.LondonStrategicEdge;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>London Strategic Edge: websocket (auth with the symbol list, subscribe, live ticks) and the Data 3.0 REST API (pairs, candle exports, usage) against a fake server that speaks the service's public client protocol.</summary>
public class LseTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public readonly List<DepthUpdate> Depth = new();
        public readonly List<(FeedStatus, string?)> Status = new();
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { lock (Trades) Trades.AddRange(trades); }
        public void OnDepth(Instrument instrument, DepthUpdate update) { lock (Depth) Depth.Add(update); }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) { lock (Status) Status.Add((status, message)); }
    }

    private static async Task Until(Func<bool> condition, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    private static LseFeed Feed(FakeHttpServer s, string key = "KEY") => new(new LseSettings { ApiKey = key, WsUrl = s.Ws, ApiUrl = s.Http });
    private static readonly Instrument Eur = new() { Symbol = "EUR/USD", FeedId = LseFeed.FeedId, Exchange = "forex", TickSize = 0.01m, VolumeStep = 1 };

    [Fact]
    public async Task The_catalog_comes_from_the_websocket_symbol_list_with_their_class_and_a_sensible_tick_size()
    {
        using var fake = FakeLse.Server(out _);
        using var feed = Feed(fake);
        var list = await feed.GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal(new[] { "AAPL", "BTC/USD", "EUR/USD", "USD/JPY", "XAU/USD" }, list.Where(i => i.Exchange != "futures").Select(i => i.Symbol).OrderBy(x => x, StringComparer.Ordinal)); // the uncategorised bond yield is left out
        Assert.Equal(27, list.Count(i => i.Exchange == "futures"));                                                                                                      // the futures are added by hand (the service no longer lists them)
        Assert.Contains(list, i => i.Symbol == "CL.F" && i.Description == "Crude Oil (WTI) Futures");
        Assert.Equal("ES.F", list[0].Symbol);                                                                                                                                           // the popular ones first
        var gc = list.Single(i => i.Symbol == "GC.F");
        Assert.Equal((0.1m, 100m, "futures"), (gc.TickSize, gc.PointValue, gc.Exchange));
        Assert.Equal(0.00000001m, list.Single(i => i.Symbol == "BTC/USD").VolumeStep);
        var jpy = list.Single(i => i.Symbol == "USD/JPY");
        Assert.Equal(0.001m, jpy.TickSize);
        Assert.Equal(0.00001m, list.Single(i => i.Symbol == "EUR/USD").TickSize);
        Assert.Equal(0.0001m, list.Single(i => i.Symbol == "BTC/USD").TickSize);
        Assert.Equal(0.01m, list.Single(i => i.Symbol == "AAPL").TickSize);
        Assert.Equal("forex", jpy.Exchange);
        Assert.Equal("USD", list.Single(i => i.Symbol == "BTC/USD").QuoteAsset);
        Assert.DoesNotContain(list, i => i.Description.Contains("history only"));
    }

    [Fact]
    public async Task The_key_is_tested_and_a_wrong_one_says_so_in_plain_words()
    {
        using var fake = FakeLse.Server(out _);
        using (var good = Feed(fake))
        {
            var (ok, msg) = await good.TestAsync(CancellationToken.None);
            Assert.True(ok);
            Assert.Contains("API key OK", msg);
            Assert.DoesNotContain("{", msg);                                          // plain words, not the raw JSON
        }

        using var bad = Feed(fake, "WRONG");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => bad.TestAsync(CancellationToken.None));
        Assert.Contains("rejected the API key", ex.Message);
        Assert.Contains("invalid key", ex.Message);
        using var none = Feed(fake, "");
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.TestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Tick_history_comes_from_the_tick_export_with_trades_only_and_the_side_from_the_tick_rule()
    {
        using var fake = FakeLse.Server(out var received);
        using var feed = Feed(fake);
        var gc = new Instrument { Symbol = "GC.F", FeedId = LseFeed.FeedId, Exchange = "futures", TickSize = 0.1m, VolumeStep = 1 };
        var trades = new List<Trade>();
        var to = DateTime.UtcNow;
        await feed.LoadTradesAsync(gc, to.AddMinutes(-30), to, null, b => { trades.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(new[] { 100m, 100.2m, 100.1m }, trades.Select(t => t.Price));                  // the quote row is not a trade
        Assert.Equal(new[] { 2m, 3m, 1m }, trades.Select(t => t.Volume));
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Buy, TradeSide.Sell }, trades.Select(t => t.Side)); // 100 first = buy · rose = buy · fell = sell
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.First.Id < p.Second.Id && p.First.Time < p.Second.Time));
        var export = fake.Requests.Single(r => r.Route == "/v3/export");
        Assert.Equal("GOLD", export.Query("symbol"));                                                // GC.F is the archive's GOLD
        Assert.Equal("futures", export.Query("market"));
        lock (received) Assert.Empty(received);                                                      // no websocket traffic for history
    }

    [Fact]
    public async Task Live_ticks_become_trades_and_a_bad_key_stops_with_an_error_instead_of_retrying()
    {
        using var fake = FakeLse.Server(out var received);
        using (var feed = Feed(fake))
        {
            var sink = new Sink();
            using var sub = feed.Subscribe(Eur, sink);
            await Until(() => { lock (sink.Trades) return sink.Trades.Count >= 3; });
            lock (sink.Trades)
            {
                Assert.Equal(3, sink.Trades.Count);                                              // the server called the symbol EUR/USD.ALIAS: it is still the one subscription; its times have 9 decimals
                Assert.All(sink.Trades, t => Assert.Equal(TradeSide.Buy, t.Side));          // the price rose each time
                Assert.Equal(new[] { 100.10m, 100.12m, 100.14m }, sink.Trades.Select(t => Math.Round(t.Price, 2)));
            }

            lock (sink.Depth) Assert.Empty(sink.Depth);                                    // the service has no book: the bid/ask it sends are not shown as one
            lock (sink.Status) Assert.Contains(sink.Status, s => s.Item1 == FeedStatus.Connected);
            lock (received) Assert.DoesNotContain(received, r => r.Contains("\"start\""));  // live only: the replay is gone
        }

        using var bad = Feed(fake, "WRONG");
        var sink2 = new Sink();
        using var sub2 = bad.Subscribe(Eur, sink2);
        await Until(() => { lock (sink2.Status) return sink2.Status.Any(s => s.Item1 == FeedStatus.Error); });
        lock (sink2.Status) Assert.Contains(sink2.Status, s => s.Item1 == FeedStatus.Error && s.Item2!.Contains("bad key"));
        await Task.Delay(600);
        lock (sink2.Status) Assert.DoesNotContain(sink2.Status, s => s.Item1 == FeedStatus.Reconnecting);   // it does not hammer the server
    }

    [Fact]
    public async Task Candles_are_exported_in_windows_and_a_window_with_nothing_stored_is_skipped()
    {
        using var fake = FakeLse.Server(out _);
        using var feed = Feed(fake);
        var klines = new List<Kline>();
        await feed.LoadKlinesAsync(new Instrument { Symbol = "AAPL", FeedId = "lse", Exchange = "stocks", TickSize = 0.01m }, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), 5, 0, b => { klines.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(3, klines.Count);                                                      // the second window answered "no stored data": skipped, not an error
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), klines[0].OpenTimeMs);
        Assert.Equal(200.5m, klines[0].Close);
        Assert.True(klines.All(k => k.Volume >= 1));
        var requests = fake.Requests.Where(r => r.Route == "/v3/export").ToList();
        Assert.Equal(2, requests.Count);                                                    // 5-minute candles are fetched 70 days at a time
        Assert.Equal("us_stocks", requests[0].Query("market"));
        Assert.Equal("0", requests[0].Query("adjusted"));
    }

    [Fact]
    public async Task Forex_and_crypto_symbols_are_found_in_the_archive_under_their_stored_spelling()
    {
        using var fake = FakeLse.Server(out _);
        using var feed = Feed(fake);
        // the fake only exports AAPL, so the 400 proves the symbol was resolved first and then asked for under its archive spelling
        foreach (var (symbol, exchange) in new[] { ("EUR/USD", "forex"), ("BTC/USD", "crypto") })
            await Assert.ThrowsAsync<HttpRequestException>(() => feed.LoadKlinesAsync(new Instrument { Symbol = symbol, FeedId = "lse", Exchange = exchange, TickSize = 0.01m },
                new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc), 1, 0, _ => true, null, CancellationToken.None));
        Assert.Contains(fake.Requests, r => r.Route == "/v3/export" && r.Query("market") == "forex" && r.Query("symbol") == "EUR_USD");
        Assert.Contains(fake.Requests, r => r.Route == "/v3/export" && r.Query("market") == "crypto" && r.Query("symbol") == "BTCUSDT");
        await Assert.ThrowsAsync<InvalidOperationException>(() => feed.LoadKlinesAsync(new Instrument { Symbol = "NOPE", FeedId = "lse", Exchange = "stocks", TickSize = 0.01m },
            new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc), 1, 0, _ => true, null, CancellationToken.None));
    }

    [Fact]
    public async Task With_one_websocket_allowed_the_symbol_list_borrows_the_slot_from_the_live_chart_and_the_live_chart_comes_back()
    {
        using var fake = FakeLse.Server(out var received, oneConnection: true);
        using var feed = Feed(fake);
        var sink = new Sink();
        using var sub = feed.Subscribe(Eur, sink);
        await Until(() => { lock (sink.Trades) return sink.Trades.Count >= 3; });            // the live chart is up and holds the only connection

        var list = await feed.GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal(32, list.Count);                                                         // the symbol list was read although a chart was live

        await Until(() => { lock (sink.Trades) return sink.Trades.Count >= 6; });             // and the live chart reconnected on its own (3 ticks per connection)
        lock (sink.Trades) Assert.True(sink.Trades.Count >= 6, $"live ticks after the symbol list: {sink.Trades.Count}");
        lock (sink.Status) Assert.DoesNotContain(sink.Status, s => s.Item1 == FeedStatus.Error);
        // (a refusal while the server is still noticing the dropped live connection is possible and is retried: only the end result matters)
    }

    [Fact]
    public async Task A_connection_refused_because_another_program_holds_the_slot_is_retried_with_an_explanation_and_not_treated_as_a_bad_key()
    {
        using var fake = FakeLse.Server(out var received, oneConnection: true);
        // someone else (another program, an earlier run) holds the only websocket
        using var other = new System.Net.WebSockets.ClientWebSocket();
        await other.ConnectAsync(new Uri(fake.Ws), CancellationToken.None);
        var buf = new byte[4096];
        await other.ReceiveAsync(buf, CancellationToken.None);                                // welcome
        await other.SendAsync(Encoding.UTF8.GetBytes("{\"action\":\"auth\",\"api_key\":\"KEY\"}"), System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);
        await other.ReceiveAsync(buf, CancellationToken.None);                                // authenticated

        using var feed = Feed(fake);
        var sink = new Sink();
        using var sub = feed.Subscribe(Eur, sink);
        await Until(() => { lock (sink.Status) return sink.Status.Any(s => s.Item1 == FeedStatus.Reconnecting && s.Item2!.Contains("only 1 websocket")); });
        lock (sink.Status)
        {
            Assert.Contains(sink.Status, s => s.Item1 == FeedStatus.Reconnecting && s.Item2!.Contains("close whatever else is using the key"));
            Assert.DoesNotContain(sink.Status, s => s.Item1 == FeedStatus.Error);              // not "bad key": it keeps trying
        }

        await other.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        other.Dispose();                                                                        // the slot is free now
        await Until(() => { lock (sink.Trades) return sink.Trades.Count >= 3; }, 20000);
        lock (sink.Trades) Assert.True(sink.Trades.Count >= 3, "it connected as soon as the slot was free");
    }

    [Fact]
    public void Futures_carry_their_real_tick_size_and_contract_value()
    {
        Assert.Equal((0.25m, 50m, 1m), LseFeed.SpecOf("futures", "ES.F"));
        Assert.Equal((0.01m, 1000m, 1m), LseFeed.SpecOf("futures", "CL.F"));               // crude oil: a cent a tick, $10 a tick
        Assert.Equal((0.0000005m, 12500000m, 1m), LseFeed.SpecOf("futures", "6J.F"));      // yen: the finest tick of them all
        Assert.Equal((0.015625m, 1000m, 1m), LseFeed.SpecOf("futures", "ZN.F"));            // the 10-year note ticks in 64ths
        Assert.Equal(0.00001m, LseFeed.SpecOf("forex", "EUR/USD").Tick);                     // everything else as before
    }

    [Fact]
    public async Task A_futures_chart_gets_its_live_ticks_by_polling_the_tick_export_not_the_websocket()
    {
        using var fake = FakeLse.Server(out var received);
        using var feed = Feed(fake);
        var gc = new Instrument { Symbol = "GC.F", FeedId = LseFeed.FeedId, Exchange = "futures", TickSize = 0.1m, VolumeStep = 1 };
        var sink = new Sink();
        using var sub = feed.Subscribe(gc, sink);
        await Until(() => { lock (sink.Trades) return sink.Trades.Count >= 3; });
        lock (sink.Trades)
        {
            Assert.Equal(new[] { 100m, 100.2m, 100.1m }, sink.Trades.Take(3).Select(t => t.Price));   // the quote row is not a trade
            Assert.Equal(new[] { TradeSide.Buy, TradeSide.Buy, TradeSide.Sell }, sink.Trades.Take(3).Select(t => t.Side));
            Assert.True(sink.Trades.Zip(sink.Trades.Skip(1)).All(p => p.First.Id < p.Second.Id), "ids increase");
        }

        lock (sink.Status) Assert.Contains(sink.Status, s => s.Item1 == FeedStatus.Connected);
        lock (received) Assert.Empty(received);                                                        // the websocket was never opened
        var export = fake.Requests.First(r => r.Route == "/v3/export");
        Assert.Equal("tick", export.Query("timeframe"));
        Assert.Equal("GOLD", export.Query("symbol"));
        Assert.Equal("futures", export.Query("market"));
    }
}
