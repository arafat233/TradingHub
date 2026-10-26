using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bitfinex;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>
/// Bitfinex: instruments, REST trade history, and the websocket with the raw order-by-order book (Level 3), against a fake server that sends the
/// message shapes captured from the real service. The real service is only touched by <see cref="BitfinexLiveTests"/>.
/// </summary>
public class BitfinexTests
{
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

    private static readonly Instrument Btc = new() { Symbol = "BTCUSD", FeedId = BitfinexFeed.FeedId, TickSize = 1m, VolumeStep = 0.00000001m };

    private static FakeHttpServer Server(out List<string> received)
    {
        var got = new List<string>();
        received = got;
        var s = new FakeHttpServer();
        s.OnHttp = req =>
        {
            if (req.Route == "/conf/pub:list:pair:exchange") return (200, "[[\"BTCUSD\",\"ETHUSD\",\"AAVE:USD\",\"LUNAUSD\"]]");
            if (req.Route == "/tickers")
                return (200, "[[\"tBTCUSD\",80469,1.6,80487,1.7,41,0.0005,80469,265.2,80961,79371],[\"tETHUSD\",2332.6,8.8,2333.2,1.7,2.2,0.0009,2333.4,12454.7,2365,2294.7]," +
                             "[\"tAAVE:USD\",150.12,1,150.2,1,0.1,0.001,150.1,100,160,140],[\"fUSD\",0.0001,1,0.0002,1,0,0,0.0001,1,0,0],[\"tBTCF0:USTF0\",80000,1,80010,1,0,0,80005,1,0,0],[\"tLUNAUSD\",0,0,0,0,0,0,0,0,0,0]]");
            if (req.Route == "/trades/tBTCUSD/hist")
            {
                // newest ids last (sort=1): three trades, a buy (amount > 0), a sell, and one repeated id that is skipped
                return (200, "[[101,1793008990085,0.5,80499],[102,1793008990086,-0.25,80498],[102,1793008990086,-0.25,80498]]");
            }

            return (404, "{}");
        };
        s.OnSocket = async (path, ws, ct) =>
        {
            await FakeHttpServer.SendText(ws, "{\"event\":\"info\",\"version\":2}", ct);
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var text = await FakeHttpServer.ReceiveText(ws, ct);
                if (text == null) return;
                lock (got) got.Add(text);
                using var doc = JsonDocument.Parse(text);
                string ev = doc.RootElement.GetProperty("event").GetString()!;
                if (ev == "conf") await FakeHttpServer.SendText(ws, "{\"event\":\"conf\",\"status\":\"OK\",\"flags\":32768}", ct);
                else if (ev == "subscribe" && doc.RootElement.GetProperty("channel").GetString() == "book")
                {
                    await FakeHttpServer.SendText(ws, "{\"event\":\"subscribed\",\"channel\":\"book\",\"chanId\":10,\"symbol\":\"tBTCUSD\",\"prec\":\"R0\"}", ct);
                    // the snapshot: two bids (one price twice) and one ask; then an order added, amended and removed (price 0), with the ms timestamp at the end
                    await FakeHttpServer.SendText(ws, "[10,[[1,80000,1.5],[2,80000,0.5],[3,80010,-2.0]]]", ct);
                    await Task.Delay(120, ct);
                    await FakeHttpServer.SendText(ws, "[10,[4,80005,-0.5],1793008996000]", ct);
                    await Task.Delay(120, ct);
                    await FakeHttpServer.SendText(ws, "[10,[4,80005,-0.7],1793008996100]", ct);
                    await Task.Delay(120, ct);
                    await FakeHttpServer.SendText(ws, "[10,[4,0,-1],1793008996200]", ct);
                    await FakeHttpServer.SendText(ws, "[10,\"hb\",1793008996300]", ct);
                }
                else if (ev == "subscribe")
                {
                    await FakeHttpServer.SendText(ws, "{\"event\":\"subscribed\",\"channel\":\"trades\",\"chanId\":8,\"symbol\":\"tBTCUSD\"}", ct);
                    await FakeHttpServer.SendText(ws, "[8,[[200,1793008990000,0.01,80000]]]", ct); // the snapshot of recent trades: not replayed
                    await FakeHttpServer.SendText(ws, "[8,\"te\",[201,1793008997877,-0.0002,80489],1793008997879]", ct);
                    await FakeHttpServer.SendText(ws, "[8,\"tu\",[201,1793008997877,-0.0002,80489],1793008997914]", ct);
                    await FakeHttpServer.SendText(ws, "[8,\"te\",[202,1793008998955,0.0023,80490],1793008998957]", ct);
                }
            }
        };
        return s;
    }

    [Fact]
    public void The_tick_size_is_the_fifth_significant_digit_of_the_price()
    {
        Assert.Equal(1m, BitfinexFeed.TickFor(80469m));
        Assert.Equal(0.1m, BitfinexFeed.TickFor(2332.6m));
        Assert.Equal(0.01m, BitfinexFeed.TickFor(150.12m));
        Assert.Equal(0.00001m, BitfinexFeed.TickFor(0.12345m));
    }

    [Fact]
    public async Task Instruments_are_the_exchange_pairs_with_a_price_and_each_gets_its_own_tick_size()
    {
        using var fake = Server(out _);
        using var feed = new BitfinexFeed(fake.Http, fake.Ws);
        var list = await feed.GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal(new[] { "BTCUSD", "ETHUSD", "AAVE:USD" }, list.Select(i => i.Symbol));   // no funding (fUSD), derivative (F0:USTF0) or priceless pair
        Assert.Equal(new[] { 1m, 0.1m, 0.01m }, list.Select(i => i.TickSize));
        Assert.Equal(("AAVE", "USD"), (list[2].BaseAsset, list[2].QuoteAsset));
        Assert.Equal(("BTC", "USD"), (list[0].BaseAsset, list[0].QuoteAsset));
    }

    [Fact]
    public async Task History_comes_from_the_rest_trades_with_the_taker_side_and_repeated_ids_skipped()
    {
        using var fake = Server(out _);
        using var feed = new BitfinexFeed(fake.Http, fake.Ws);
        var trades = new List<Trade>();
        await feed.LoadTradesAsync(Btc, DateTime.UnixEpoch.AddMilliseconds(1793008990000), DateTime.UnixEpoch.AddMilliseconds(1793008999000), null, b => { trades.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(new long[] { 101, 102 }, trades.Select(t => t.Id));
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Sell }, trades.Select(t => t.Side));      // amount > 0 = the taker bought
        Assert.Equal(new[] { 0.5m, 0.25m }, trades.Select(t => t.Volume));
        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(1793008990085), trades[0].Time);
    }

    [Fact]
    public async Task The_raw_book_gives_the_level_2_book_and_every_order_event_with_its_own_time_and_the_tape_skips_the_snapshot()
    {
        using var fake = Server(out var received);
        using var feed = new BitfinexFeed(fake.Http, fake.Ws);
        var sink = new Sink();
        using var sub = feed.Subscribe(Btc, sink);
        await Until(() => { lock (sink.Orders) return sink.Orders.Count >= 3; });
        await Until(() => { lock (sink.Trades) return sink.Trades.Count >= 2; });

        lock (sink.Orders)
        {
            // an order added, amended (same id, new size) and removed (price 0): the snapshot itself is not news
            Assert.Equal(new[] { OrderEventKind.Add, OrderEventKind.Modify, OrderEventKind.Remove }, sink.Orders.Select(o => o.Kind));
            Assert.All(sink.Orders, o => { Assert.Equal(4, o.OrderId); Assert.True(o.Ask); Assert.Equal(80005, o.PriceTicks); });
            Assert.Equal(new[] { 0.5m, 0.7m, 0.7m }, sink.Orders.Select(o => o.Size));        // Remove carries what it still had
            Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(1793008996000), sink.Orders[0].Time);
        }

        lock (sink.Depth)
        {
            var first = sink.Depth[0].Snapshot;                                                // the snapshot: orders at one price add up
            Assert.Equal(2.0m, first.GetSize(BookSide.Bid, 80000));
            Assert.Equal(2.0m, first.GetSize(BookSide.Ask, 80010));
            Assert.True(sink.Depth[0].IsFullRefresh);
        }

        lock (sink.Trades)
        {
            Assert.Equal(new long[] { 201, 202 }, sink.Trades.Select(t => t.Id));              // "tu" repeats and the trade snapshot are ignored
            Assert.Equal(new[] { TradeSide.Sell, TradeSide.Buy }, sink.Trades.Select(t => t.Side));
            Assert.Equal(new[] { 0.0002m, 0.0023m }, sink.Trades.Select(t => t.Volume));
        }

        lock (received)
        {
            Assert.Contains(received, r => r.Contains("\"flags\":32768"));
            Assert.Contains(received, r => r.Contains("\"prec\":\"R0\"") && r.Contains("\"symbol\":\"tBTCUSD\""));
        }

        lock (sink.Status) Assert.Contains(sink.Status, s => s.Item1 == FeedStatus.Connected);
    }
}

/// <summary>The order-by-order events turned into "big order added / pulled / flashed / eaten".</summary>
public class OrderFlowTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSD", FeedId = "test", TickSize = 1m, VolumeStep = 0.00000001m };
    private static readonly DateTime T0 = new(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);

    private static OrderEvent Ev(double sec, OrderEventKind kind, long id, decimal size, bool ask = true, long ticks = 80500) =>
        new(T0.AddSeconds(sec), kind, id, ask, ticks, size);

    /// <summary>300 small orders (1 each), so a big order is learned to be at least 6.</summary>
    private static OrderFlowDetector Learned()
    {
        var d = new OrderFlowDetector();
        d.Apply(Enumerable.Range(0, 300).Select(i => Ev(i * 0.01, OrderEventKind.Add, 1000 + i, 1m, ask: i % 2 == 0)).ToList());
        Assert.Empty(d.Events);                                                              // small orders are not news
        Assert.True(d.BigSize >= 6m);
        return d;
    }

    [Fact]
    public void A_big_order_that_is_cancelled_is_pulled_after_a_while_and_a_flash_if_it_was_gone_within_seconds()
    {
        var d = Learned();
        d.Apply(new[] { Ev(10, OrderEventKind.Add, 1, 50), Ev(20, OrderEventKind.Cancel, 1, 50) });
        d.Apply(new[] { Ev(30, OrderEventKind.Add, 2, 60), Ev(31, OrderEventKind.Cancel, 2, 60) });
        Assert.Equal(new[] { BookEventKind.Added, BookEventKind.Pulled, BookEventKind.Added, BookEventKind.Flash }, d.Events.Select(e => e.Kind));
        Assert.Equal(new[] { 50m, 50m, 60m, 60m }, d.Events.Select(e => e.Size));
        Assert.True(d.Active);
    }

    [Fact]
    public void A_big_order_that_is_filled_is_eaten_and_an_amended_one_keeps_its_identity()
    {
        var d = Learned();
        d.Apply(new[] { Ev(10, OrderEventKind.Add, 1, 50), Ev(11, OrderEventKind.Modify, 1, 40, ticks: 80501), Ev(12, OrderEventKind.Fill, 1, 40) });
        Assert.Equal(new[] { BookEventKind.Added, BookEventKind.Eaten }, d.Events.Select(e => e.Kind));
        Assert.Equal(80501, d.Events[1].Ticks);                                              // it moved before it was filled
        d.Apply(new[] { Ev(13, OrderEventKind.Cancel, 999, 5) });                            // an order that was never big: not news
        Assert.Equal(2, d.Events.Count);
    }

    [Fact]
    public void An_order_that_left_without_a_reason_is_filled_if_trades_hit_its_price_around_it_and_cancelled_if_not()
    {
        var d = Learned();
        var store = new TradeStore(Btc);
        // order 1 (ask 80500): buyers hit that price for 40 of its 50 around the moment it left; order 2 (bid 80400): nothing traded there
        d.Apply(new[] { Ev(10, OrderEventKind.Add, 1, 50), Ev(10.5, OrderEventKind.Add, 2, 50, ask: false, ticks: 80400) });
        store.Add(new Trade(1, T0.AddSeconds(20.2), 80500m, 40, TradeSide.Buy));
        d.Apply(new[] { Ev(20, OrderEventKind.Remove, 1, 50), Ev(20.1, OrderEventKind.Remove, 2, 50, ask: false, ticks: 80400) });
        d.Resolve(store, T0.AddSeconds(20.5));
        Assert.Equal(2, d.Events.Count);                                                     // still waiting: the trades may not all have arrived
        d.Resolve(store, T0.AddSeconds(22));
        Assert.Equal(new[] { BookEventKind.Added, BookEventKind.Added, BookEventKind.Eaten, BookEventKind.Pulled }, d.Events.Select(e => e.Kind));
        Assert.Equal(80500, d.Events[2].Ticks);
    }
}

/// <summary>
/// Runs against the REAL Bitfinex for about 80 seconds (it only runs when BOOKEVENTS_LIVE is set; nothing is written to disk):
/// <c>$env:BOOKEVENTS_LIVE='1'; dotnet test --filter BitfinexLive --logger "console;verbosity=detailed"</c>.
/// </summary>
public class BitfinexLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task BitfinexLive_real_btcusd_raw_book_and_tape()
    {
        if (Environment.GetEnvironmentVariable("BOOKEVENTS_LIVE") is not { Length: > 0 }) return;
        using var feed = new BitfinexFeed();
        var list = await feed.GetInstrumentsAsync(CancellationToken.None);
        var inst = list.First(i => i.Symbol == "BTCUSD");
        output.WriteLine($"INSTRUMENTS {list.Count}; BTCUSD tick {inst.TickSize}; first five: {string.Join(", ", list.Take(5).Select(i => i.Symbol + " " + i.TickSize))}");

        var hist = new List<Trade>();
        await feed.LoadTradesAsync(inst, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow, null, b => { hist.AddRange(b); return true; }, null, CancellationToken.None);
        output.WriteLine($"HISTORY 10 min: {hist.Count} trades; {hist.Count(t => t.Side == TradeSide.Buy)} buys / {hist.Count(t => t.Side == TradeSide.Sell)} sells; last {hist[^1].Time:HH:mm:ss.fff} {hist[^1].Price}");
        Assert.True(hist.Count > 20);
        Assert.True(hist.Zip(hist.Skip(1)).All(p => p.First.Id < p.Second.Id));

        var data = new InstrumentData(inst, feed);
        var sink = (IMarketDataSink)data;
        int orders = 0, depth = 0, trades = 0;
        var counting = new CountingSink(sink, () => orders++, () => depth++, () => trades++);
        using (feed.Subscribe(inst, counting))
        {
            var end = DateTime.UtcNow.AddSeconds(80);
            while (DateTime.UtcNow < end) { await Task.Delay(500); data.Pump(); }
        }

        data.Pump();
        output.WriteLine($"LIVE 80 s: {orders} order events, {depth} book updates, {trades} trades; big order = {data.OrderFlow.BigSize:0.####} BTC; active {data.OrderFlow.Active}");
        output.WriteLine($"  book {data.Depth.Bids.Length} bid / {data.Depth.Asks.Length} ask levels; best {data.Depth.Bids[0].PriceTicks}/{data.Depth.Asks[0].PriceTicks}");
        foreach (var e in data.OrderFlow.Events.TakeLast(10)) output.WriteLine($"  {e.Time:HH:mm:ss.f} {e.Text(inst)}");
        Assert.True(orders > 100, "order events arrived");
        Assert.True(data.OrderFlow.Active);
        Assert.True(data.Depth.Bids[0].PriceTicks < data.Depth.Asks[0].PriceTicks, "the book is not crossed");
    }

    private sealed class CountingSink(IMarketDataSink inner, Action onOrder, Action onDepth, Action onTrade) : IMarketDataSink
    {
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) { foreach (var _ in trades) onTrade(); inner.OnTrades(instrument, trades); }
        public void OnDepth(Instrument instrument, DepthUpdate update) { onDepth(); inner.OnDepth(instrument, update); }
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) => inner.OnStatus(instrument, status, message);
        public void OnOrderEvents(Instrument instrument, IReadOnlyList<OrderEvent> events) { foreach (var _ in events) onOrder(); inner.OnOrderEvents(instrument, events); }
    }
}
