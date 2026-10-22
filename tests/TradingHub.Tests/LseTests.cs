using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.LondonStrategicEdge;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>London Strategic Edge: websocket (auth, subscribe, 24 h replay, live ticks) and REST (catalog, candles, usage) against a fake server that speaks the service's public client protocol.</summary>
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
    private static readonly Instrument Eur = new() { Symbol = "EUR/USD", FeedId = LseFeed.FeedId, Exchange = "fx", TickSize = 0.01m, VolumeStep = 1 };

    [Fact]
    public async Task The_catalog_gives_the_tradable_instruments_with_their_class_and_a_sensible_tick_size()
    {
        using var fake = FakeLse.Server(out _);
        using var feed = Feed(fake);
        var list = await feed.GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal(new[] { "AAPL", "BTC/USD", "ES.F", "EUR/USD", "FDAX", "USD/JPY" }, list.Select(i => i.Symbol).OrderBy(x => x, StringComparer.Ordinal)); // no economics, bonds, credit, derivatives or option contracts
        Assert.Equal("ES.F", list[0].Symbol);                                                                                                                // the popular ones first
        var es = list[0];
        Assert.Equal((0.25m, 50m), (es.TickSize, es.PointValue));                                                                                            // a known future gets its real spec
        Assert.DoesNotContain("history only", es.Description);
        Assert.Contains("history only", list.Single(i => i.Symbol == "FDAX").Description);                                                                  // "live": 0
        Assert.Equal(0.00000001m, list.Single(i => i.Symbol == "BTC/USD").VolumeStep);
        var jpy = list.Single(i => i.Symbol == "USD/JPY");
        Assert.Equal(0.001m, jpy.TickSize);
        Assert.Equal(0.00001m, list.Single(i => i.Symbol == "EUR/USD").TickSize);
        Assert.Equal(0.0001m, list.Single(i => i.Symbol == "BTC/USD").TickSize);
        Assert.Equal(0.01m, list.Single(i => i.Symbol == "AAPL").TickSize);
        Assert.Equal("fx", jpy.Exchange);
        Assert.Equal("USD", list.Single(i => i.Symbol == "BTC/USD").QuoteAsset);
        Assert.Equal(new DateTime(2003, 9, 10, 13, 30, 0, DateTimeKind.Utc), await feed.FirstAvailableAsync(new Instrument { Symbol = "AAPL", FeedId = "lse", Exchange = "stocks", TickSize = 0.01m }, CancellationToken.None));
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
    public async Task History_replays_the_last_hours_as_ticks_with_the_side_from_the_tick_rule_and_stops_before_the_live_ticks()
    {
        using var fake = FakeLse.Server(out var received);
        using var feed = Feed(fake);
        var trades = new List<Trade>();
        await feed.LoadTradesAsync(Eur, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow, null, b => { trades.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(4, trades.Count);                                                       // the 3 live ticks that followed replay_complete are not history (replayed ticks carry epoch seconds)
        Assert.True(trades.All(t => DateTime.UtcNow - t.Time < TimeSpan.FromHours(1) && t.Time < DateTime.UtcNow), "the epoch times are read as UTC");
        // the quote says nothing (bid is the price), so the tick rule decides: 100.00 first = buy · 100.05 rose = buy · 99.95 fell = sell · 99.94 fell = sell
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Buy, TradeSide.Sell, TradeSide.Sell }, trades.Select(t => t.Side));
        Assert.Equal(new[] { 2m, 3m, 1m, 1m }, trades.Select(t => t.Volume));                // a missing volume counts as 1
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.First.Id < p.Second.Id && p.First.Time < p.Second.Time));
        lock (received)
        {
            Assert.Contains(received, r => r.Contains("\"action\":\"auth\"") && r.Contains("\"api_key\":\"KEY\""));
            Assert.Contains(received, r => r.Contains("\"action\":\"subscribe\"") && r.Contains("\"symbol\":\"EUR/USD\"") && r.Contains("\"start\":\""));
        }
    }

    [Fact]
    public async Task A_history_request_never_reaches_further_back_than_the_24_hours_the_service_replays()
    {
        using var fake = FakeLse.Server(out var received);
        using var feed = Feed(fake);
        await feed.LoadTradesAsync(Eur, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow, null, _ => true, null, CancellationToken.None);
        string sub;
        lock (received) sub = received.First(r => r.Contains("\"action\":\"subscribe\""));
        var start = DateTime.Parse(JsonDocument.Parse(sub).RootElement.GetProperty("start").GetString()!, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.InRange((DateTime.UtcNow - start).TotalHours, 23.9, 24.1);
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
                Assert.Equal(3, sink.Trades.Count);                                              // the server called the symbol EUR/USD.ALIAS: it is still the one subscription
                Assert.All(sink.Trades, t => Assert.Equal(TradeSide.Buy, t.Side));          // the price rose each time
                Assert.Equal(new[] { 100.10m, 100.12m, 100.14m }, sink.Trades.Select(t => Math.Round(t.Price, 2)));
            }

            lock (sink.Depth) Assert.Empty(sink.Depth);                                    // the service has no book: the bid/ask it sends are not shown as one
            lock (sink.Status) Assert.Contains(sink.Status, s => s.Item1 == FeedStatus.Connected);
            lock (received) Assert.DoesNotContain(received, r => r.Contains("\"start\""));  // live: no replay asked
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
    public async Task Candles_come_in_pages_of_5000_and_the_next_page_starts_after_the_last_row()
    {
        using var fake = FakeLse.Server(out _);
        using var feed = Feed(fake);
        var klines = new List<Kline>();
        await feed.LoadKlinesAsync(new Instrument { Symbol = "AAPL", FeedId = "lse", Exchange = "stocks", TickSize = 0.01m }, new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 5, 0, b => { klines.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(5003, klines.Count);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 1, 5, 14, 30, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds(), klines[0].OpenTimeMs);
        Assert.Equal(200.5m, klines[0].Close);                                              // a price sent as a string is read too
        Assert.True(klines.All(k => k.Volume >= 1));
        var requests = fake.Requests.Where(r => r.Route == "/candles").ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal("stocks", requests[0].Query("dataset"));
    }

    [Fact]
    public async Task With_one_websocket_allowed_the_history_borrows_the_slot_from_the_live_chart_and_the_live_chart_comes_back()
    {
        using var fake = FakeLse.Server(out var received, oneConnection: true);
        using var feed = Feed(fake);
        var sink = new Sink();
        using var sub = feed.Subscribe(Eur, sink);
        await Until(() => { lock (sink.Trades) return sink.Trades.Count >= 3; });            // the live chart is up and holds the only connection

        var history = new List<Trade>();
        await feed.LoadTradesAsync(Eur, DateTime.UtcNow.AddHours(-2), DateTime.UtcNow, null, b => { history.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(4, history.Count);                                                      // the replay worked although a chart was live

        await Until(() => { lock (sink.Trades) return sink.Trades.Count >= 6; });             // and the live chart reconnected on its own (3 ticks per connection)
        lock (sink.Trades) Assert.True(sink.Trades.Count >= 6, $"live ticks after the history: {sink.Trades.Count}");
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
}
