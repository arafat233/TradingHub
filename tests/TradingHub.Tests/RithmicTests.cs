using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Rithmic;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>Rithmic against a fake server: paginated history, candles, front-month contract and orders.</summary>
public class RithmicTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = RithmicFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 50 };
    private const long T0 = 1_790_000_000;

    private static RithmicSettings Settings(FakeRithmicServer s, string password = "demo") =>
        new() { Uri = s.Uri, System = "Rithmic Paper Trading", User = "demo", Password = password, TimeoutMs = 5000 };

    private static async Task Until(Func<bool> condition, int ms = 6000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    [Fact]
    public async Task The_tick_by_tick_history_paginates_without_losing_or_repeating()
    {
        using var server = new FakeRithmicServer { PageSize = 7 };
        // 31 trades; several in the same second right where a page is cut
        int n = 0;
        for (int sec = 0; sec < 10; sec++)
            for (int k = 0; k < (sec == 2 ? 6 : 3); k++)
                server.Ticks.Add((T0 + sec, k * 1000, 5000 + sec * 0.25, 1 + k, (n++ % 2) == 0));
        server.Ticks.Add((T0 + 30, 0, 5005, 4, true));
        var feed = new RithmicFeed(Settings(server));
        var got = new List<Trade>();
        await feed.LoadTradesAsync(Es, DateTimeOffset.FromUnixTimeSeconds(T0 - 5).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(T0 + 60).UtcDateTime, null,
            b => { got.AddRange(b); return true; }, null, CancellationToken.None);

        Assert.Equal(server.Ticks.Count, got.Count);
        Assert.Equal(got.Select(t => t.Id).Distinct().Count(), got.Count);
        Assert.True(got.Zip(got.Skip(1)).All(p => p.Second.Time >= p.First.Time && p.Second.Id > p.First.Id));
        Assert.Equal(server.Ticks.Sum(t => t.Size), (int)got.Sum(t => t.Volume));
        Assert.Equal(server.Ticks.Count(t => t.Buy), got.Count(t => t.Side == TradeSide.Buy));
        Assert.Equal(5005m, got[^1].Price);
    }

    [Fact]
    public async Task The_one_minute_candles_arrive_with_buy_volume()
    {
        using var server = new FakeRithmicServer { PageSize = 5 };
        for (int i = 1; i <= 17; i++) server.Bars.Add((T0 + i * 60, 5000, 5002, 4999, 5001, 100 + i, 60));
        var feed = new RithmicFeed(Settings(server));
        var got = new List<Kline>();
        await feed.LoadKlinesAsync(Es, DateTimeOffset.FromUnixTimeSeconds(T0).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(T0 + 3600).UtcDateTime, 1, 0,
            b => { got.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(17, got.Count);
        Assert.Equal(T0 * 1000, got[0].OpenTimeMs); // the stamp is the close: it opens a minute earlier
        Assert.All(got, k => Assert.Equal(60m, k.BuyVolume));
        Assert.Equal(117m, got[^1].Volume);
    }

    [Fact]
    public async Task It_knows_the_front_month_contract_and_the_last_trade()
    {
        using var server = new FakeRithmicServer { LastTradeSsboe = T0 + 123 };
        var feed = new RithmicFeed(Settings(server));
        var (symbol, exchange) = await feed.ContractAsync(Es, CancellationToken.None);
        Assert.Equal("ESZ6", symbol);
        Assert.Equal("CME", exchange);
        var last = await feed.LastTradeTimeAsync(Es, CancellationToken.None);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(T0 + 123).UtcDateTime, last);
    }

    [Fact]
    public async Task The_account_trades_market_limit_modifies_and_cancels()
    {
        using var server = new FakeRithmicServer { MarketPrice = 5000 };
        var feed = new RithmicFeed(Settings(server));
        using var session = new RithmicTradingSession(Settings(server));
        session.Start();
        await Until(() => session.IsConnected);
        Assert.True(session.IsConnected, session.StatusText);
        Assert.Equal("ACC1", Assert.Single(session.Accounts).AccountId);
        Assert.True(session.IsPractice);

        var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = new RithmicBroker(data, session, "ACC1", "ACC1", ct => feed.ContractAsync(Es, ct));
        Assert.False(broker.IsReal);

        // market: it fills and the position arrives via the PnL plant
        broker.Market(OrderDirections.Buy, 2);
        await Until(() => broker.Position.Volume == 2);
        Assert.Equal(2m, broker.Position.Volume);
        Assert.Equal(5000m, broker.Position.AveragePrice);
        await Until(() => !broker.ActiveOrders.Any());
        Assert.Empty(broker.ActiveOrders);

        // limit: it stays active with its Rithmic number
        var limit = broker.Limit(OrderDirections.Sell, 1, 5010);
        await Until(() => limit.ExchangeOrderId.Length > 0);
        Assert.StartsWith("B", limit.ExchangeOrderId);
        Assert.Contains(limit, broker.ActiveOrders);
        Assert.Equal(5010.0, server.Open[limit.ExchangeOrderId].Price);

        // modify the price
        broker.Modify(limit, 5012);
        await Until(() => server.Open[limit.ExchangeOrderId].Price == 5012);
        Assert.Equal(5012.0, server.Open[limit.ExchangeOrderId].Price);
        Assert.Equal(5012m, limit.Price);

        // cancel
        broker.Cancel(limit);
        await Until(() => !broker.ActiveOrders.Contains(limit));
        Assert.DoesNotContain(limit, broker.ActiveOrders);
        Assert.Equal(OrderStatus.Canceled, limit.Status);

        // close the position
        broker.Flatten();
        await Until(() => broker.Position.Volume == 0);
        Assert.Equal(0m, broker.Position.Volume);
    }

    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public volatile FeedStatus Status;
        public string? Message;

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            lock (Trades) Trades.AddRange(trades);
        }

        public volatile DepthUpdate? Depth;

        public void OnDepth(Instrument instrument, DepthUpdate update) => Depth = update;

        public readonly List<OrderEvent> Orders = new();

        public void OnOrderEvents(Instrument instrument, IReadOnlyList<OrderEvent> events)
        {
            lock (Orders) Orders.AddRange(events);
        }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            Status = status;
            Message = message;
        }
    }

    [Fact]
    public async Task Level2_order_book_fills_the_dom_and_the_best_bid_offer_no_longer_touches_it()
    {
        using var server = new FakeRithmicServer { LastTradeSsboe = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2 };
        using var feed = new RithmicFeed(Settings(server));
        var sink = new Sink();
        using var sub = feed.Subscribe(Es, sink);
        await Until(() => sink.Message?.Contains("ESZ6") == true);
        Assert.Contains(7L, server.SubscribeBits); // last trade + BBO + order book

        // full book: levels with their absolute size
        await server.PushBook("ESZ6", 7, new[] { (5000.0, 10), (4999.75, 20), (4999.5, 30) }, new[] { (5000.25, 12), (5000.5, 22) });
        await Until(() => sink.Depth?.Snapshot.Bids.Length == 3);
        var d = sink.Depth!;
        Assert.True(d.IsFullRefresh);
        Assert.Equal(Es.ToTicks(5000m), d.Snapshot.BestBidTicks);
        Assert.Equal(12m, d.Snapshot.BestAskSize);
        Assert.Equal(2, d.Snapshot.Asks.Length);

        // a BEGIN/END set: 4999.5 goes away (size 0), a new ask level appears, the best bid changes size
        await server.PushBook("ESZ6", 4, new[] { (4999.5, 0) }, Array.Empty<(double, int)>());
        await server.PushBook("ESZ6", 6, new[] { (5000.0, 15) }, new[] { (5000.75, 5) });
        await Until(() => sink.Depth?.Snapshot.Asks.Length == 3);
        d = sink.Depth!;
        Assert.Equal(2, d.Snapshot.Bids.Length);
        Assert.Equal(15m, d.Snapshot.BestBidSize);
        Assert.False(d.IsFullRefresh);

        // with depth arriving, a BBO at another price must not wipe levels from the book
        await server.PushBbo("ESZ6", 5001.0, 1, 5001.25, 1);
        await server.PushBook("ESZ6", 7, Array.Empty<(double, int)>(), new[] { (5000.25, 13) });
        await Until(() => sink.Depth?.Snapshot.BestAskSize == 13m);
        Assert.Equal(Es.ToTicks(5000m), sink.Depth!.Snapshot.BestBidTicks);
        Assert.Equal(2, sink.Depth.Snapshot.Bids.Length);
    }

    [Fact]
    public async Task Depth_by_order_becomes_level3_order_events()
    {
        using var server = new FakeRithmicServer { LastTradeSsboe = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2 };
        using var feed = new RithmicFeed(Settings(server));
        var sink = new Sink();
        using var sub = feed.Subscribe(Es, sink);
        await Until(() => sink.Message?.Contains("ESZ6") == true);
        await Until(() => server.MboRequests.Count > 0);
        Assert.Equal("ESZ6", server.MboRequests[0]);

        await server.PushOrders("ESZ6", (1, true, 5000.0, 10, "111"), (1, false, 5000.25, 7, "222"));
        await Until(() => sink.Orders.Count == 2);
        await server.PushOrders("ESZ6", (2, true, 5000.0, 4, "111"), (3, false, 5000.25, 0, "222"));
        await Until(() => sink.Orders.Count == 4);

        OrderEvent[] e;
        lock (sink.Orders) e = sink.Orders.ToArray();
        Assert.Equal(new OrderEvent(e[0].Time, OrderEventKind.Add, 111, false, Es.ToTicks(5000m), 10), e[0]);
        Assert.Equal((OrderEventKind.Add, 222L, true, 7m), (e[1].Kind, e[1].OrderId, e[1].Ask, e[1].Size));
        Assert.Equal((OrderEventKind.Modify, 4m), (e[2].Kind, e[2].Size));
        // the delete carries no size: it reports the one the order last had
        Assert.Equal((OrderEventKind.Remove, 222L, true, 7m), (e[3].Kind, e[3].OrderId, e[3].Ask, e[3].Size));
    }

    [Fact]
    public async Task Without_the_order_level_entitlement_the_book_keeps_working_and_no_error_shows()
    {
        using var server = new FakeRithmicServer { LastTradeSsboe = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2, NoMbo = true };
        using var feed = new RithmicFeed(Settings(server));
        var sink = new Sink();
        using var sub = feed.Subscribe(Es, sink);
        await Until(() => server.MboRequests.Count > 0);
        await server.PushBook("ESZ6", 7, new[] { (5000.0, 10) }, new[] { (5000.25, 12) });
        await Until(() => sink.Depth?.Snapshot.Bids.Length == 1);
        Assert.NotEqual(FeedStatus.Error, sink.Status);
        Assert.Empty(sink.Orders);
    }

    [Fact]
    public async Task Without_level2_permission_it_keeps_trades_and_best_bid_offer()
    {
        using var server = new FakeRithmicServer { LastTradeSsboe = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 2, NoLevel2 = true };
        using var feed = new RithmicFeed(Settings(server));
        var sink = new Sink();
        using var sub = feed.Subscribe(Es, sink);
        await Until(() => server.SubscribeBits.Contains(3L));
        Assert.Equal(new[] { 7L, 3L }, server.SubscribeBits.ToArray()); // asked with depth, refused, asked again without it

        await server.PushBbo("ESZ6", 5000.0, 4, 5000.25, 6);
        await Until(() => sink.Depth != null);
        Assert.Equal(Es.ToTicks(5000m), sink.Depth!.Snapshot.BestBidTicks);
        await server.PushTrade("ESZ6", 5000.25, 2, true);
        await Until(() => sink.Trades.Count > 0);
        Assert.NotEqual(FeedStatus.Error, sink.Status);
    }

    private static readonly Instrument Mes = new() { Symbol = "MES", FeedId = RithmicFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 5 };

    [Fact]
    public async Task Several_charts_and_the_history_share_a_single_session_per_plant()
    {
        // Rithmic allows a single session per user on each plant: before, each chart opened its own and they cut each other off
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var server = new FakeRithmicServer { PageSize = 50, LastTradeSsboe = now - 2 };
        for (int i = 0; i < 30; i++) server.Ticks.Add((now - 600 + i * 10, 0, 5000 + i * 0.25, 1, i % 2 == 0));
        using var feed = new RithmicFeed(Settings(server));
        var es = new Sink();
        var mes = new Sink();
        using var s1 = feed.Subscribe(Es, es);
        using var s2 = feed.Subscribe(Mes, mes);
        await Until(() => es.Message?.Contains("ESZ6") == true && mes.Message?.Contains("MESZ6") == true);
        Assert.Equal(FeedStatus.Connected, es.Status);
        Assert.Equal(FeedStatus.Connected, mes.Status);

        // history of both at once + the front-month contract, over the same sessions
        var got = new List<Trade>[] { new(), new() };
        var from = DateTimeOffset.FromUnixTimeSeconds(now - 700).UtcDateTime;
        var to = DateTime.UtcNow;
        await Task.WhenAll(
            feed.LoadTradesAsync(Es, from, to, null, b => { got[0].AddRange(b); return true; }, null, CancellationToken.None),
            feed.LoadTradesAsync(Mes, from, to, null, b => { got[1].AddRange(b); return true; }, null, CancellationToken.None),
            feed.ContractAsync(Mes, CancellationToken.None));
        Assert.Equal(30, got[0].Count);
        Assert.Equal(30, got[1].Count);

        // live, each one receives its own
        await server.PushTrade("ESZ6", 5010.25, 3, true);
        await server.PushTrade("MESZ6", 5010.5, 7, false);
        await Until(() => es.Trades.Count > 0 && mes.Trades.Count > 0);
        Assert.Equal(5010.25m, Assert.Single(es.Trades).Price);
        var m = Assert.Single(mes.Trades);
        Assert.Equal(7m, m.Volume);
        Assert.Equal(TradeSide.Sell, m.Side);

        Assert.Equal(0, server.Kicks);
        Assert.Equal(1, server.Logins[1]); // a single quote session
        Assert.Equal(1, server.Logins[3]); // a single history session (reused)
        Assert.Equal(1, feed.TickerLogins);
    }

    [Fact]
    public async Task Testing_the_connection_uses_the_charts_session()
    {
        using var server = new FakeRithmicServer();
        using var feed = new RithmicFeed(Settings(server));
        var es = new Sink();
        using var sub = feed.Subscribe(Es, es);
        await Until(() => es.Status == FeedStatus.Connected);
        var (ok, _) = await feed.TestAsync(CancellationToken.None);
        Assert.True(ok);
        Assert.Equal(0, server.Kicks);
        Assert.Equal(1, server.Logins[1]);
    }

    [Theory]
    [InlineData("2026-09-01", "ESU6")] // before the September roll
    [InlineData("2026-09-11", "ESU6")] // the previous Friday the September one still trades (ESZ6: 127 thousand contracts)
    [InlineData("2026-09-14", "ESZ6")] // roll: Monday of expiry week (Friday the 18th) -> 845 thousand contracts
    [InlineData("2026-10-11", "ESZ6")]
    [InlineData("2026-12-11", "ESZ6")]
    [InlineData("2026-12-14", "ESH7")] // December roll: it moves to the following year's March one
    [InlineData("2026-03-13", "ESH6")] // expiry 20/03/2026 -> roll Monday 16/03
    [InlineData("2026-03-16", "ESM6")]
    public void The_contract_of_each_date_follows_the_rolls(string date, string expected)
    {
        var t = DateTime.SpecifyKind(DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        Assert.Equal(expected, FuturesRolls.On("ES", t).Symbol);
    }

    [Fact]
    public async Task The_long_history_requests_each_contract_in_its_stretch()
    {
        using var server = new FakeRithmicServer { PageSize = 500, LastTradeSsboe = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        long sep5 = new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        long sep15 = new DateTimeOffset(2026, 9, 15, 15, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        server.Ticks.Add((sep5, 0, 6500, 2, true));
        server.Ticks.Add((sep15, 0, 6550, 3, false));
        using var feed = new RithmicFeed(Settings(server));
        var got = new List<Trade>();
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        await feed.LoadTradesAsync(Es, from, from.AddDays(20), null, b => { got.AddRange(b); return true; }, null, CancellationToken.None);

        var roll = new DateTimeOffset(FuturesRolls.Roll(2026, 9)).ToUnixTimeSeconds();
        Assert.Contains(server.TickRequests, r => r.Symbol == "ESU6" && r.Start == new DateTimeOffset(from).ToUnixTimeSeconds() && r.End == roll);
        Assert.Contains(server.TickRequests, r => r.Symbol == "ESZ6" && r.Start == roll);
        Assert.Equal(2, got.Count);

        // a single day for the backtest: with that day's contract
        server.TickRequests.Clear();
        var day = await feed.DownloadDayAsync(Es, new DateOnly(2026, 9, 5), null, CancellationToken.None);
        Assert.Single(day!);
        Assert.Equal("ESU6", Assert.Single(server.TickRequests.Select(r => r.Symbol).Distinct()));
        Assert.Null(await feed.DownloadDayAsync(Es, new DateOnly(2026, 9, 6), null, CancellationToken.None));
    }

    [Fact]
    public async Task An_expired_contract_with_no_history_is_tried_with_two_digits_and_reported()
    {
        // the server (like Rithmic) only has the current contract: the expired ones come back empty
        using var server = new FakeRithmicServer { PageSize = 500, OnlySymbol = "ESZ6", LastTradeSsboe = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        server.Ticks.Add((new DateTimeOffset(2026, 9, 3, 15, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), 0, 6500, 2, true));
        using var feed = new RithmicFeed(Settings(server));
        var day = await feed.DownloadDayAsync(Es, new DateOnly(2026, 9, 3), null, CancellationToken.None);
        Assert.Null(day);
        Assert.Contains(server.TickRequests, r => r.Symbol == "ESU6");
        Assert.Contains(server.TickRequests, r => r.Symbol == "ESU26");
        Assert.Equal(new[] { "ESU6" }, feed.MissingContracts);
        Assert.Equal("NQU26", FuturesRolls.TwoDigitYear("NQU6", new DateTime(2026, 10, 1)));
        Assert.Equal("ESZ25", FuturesRolls.TwoDigitYear("ESZ5", new DateTime(2026, 10, 1)));
    }

    [Fact]
    public async Task A_day_with_a_hundred_thousand_trades_downloads_in_full()
    {
        // big pages like Rithmic's: nothing is lost or repeated and it does not take forever
        using var server = new FakeRithmicServer { PageSize = 10_000 };
        long t0 = new DateTimeOffset(2026, 10, 6, 13, 30, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        for (int i = 0; i < 100_000; i++) server.Ticks.Add((t0 + i / 20, (i % 20) * 50_000, 6700 + (i % 40) * 0.25, 1 + i % 3, i % 2 == 0));
        using var feed = new RithmicFeed(Settings(server));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var day = await feed.DownloadDayAsync(Es, new DateOnly(2026, 10, 6), null, CancellationToken.None);
        sw.Stop();
        Assert.Equal(100_000, day!.Count);
        Assert.Equal(day.Count, day.Select(t => t.Id).Distinct().Count());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"it took {sw.Elapsed}");
        Console.WriteLine($"100,000 trades in {sw.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task With_the_wrong_password_the_session_says_so()
    {
        using var server = new FakeRithmicServer();
        using var session = new RithmicTradingSession(Settings(server, password: "mal"));
        session.Start();
        await Until(() => session.StatusText.Contains("permission", StringComparison.OrdinalIgnoreCase) || session.StatusText.Contains("rejected", StringComparison.OrdinalIgnoreCase));
        Assert.False(session.IsConnected);
        Assert.Empty(session.Accounts);
        Assert.Contains("permission denied", session.StatusText);
    }

    [Theory]
    [InlineData("2026-10-04T03:35:00Z", true, "2026-10-04T22:00:00Z")] // Saturday night in Chicago: it opens on Sunday at 17:00
    [InlineData("2026-10-02T20:59:00Z", false, "2026-10-02T20:59:00Z")] // Friday 15:59 in Chicago: open
    [InlineData("2026-10-02T21:00:00Z", true, "2026-10-04T22:00:00Z")] // Friday 16:00: it closes for the weekend
    [InlineData("2026-09-30T21:30:00Z", true, "2026-09-30T22:00:00Z")] // Wednesday in the daily break
    [InlineData("2026-09-30T22:00:00Z", false, "2026-09-30T22:00:00Z")] // Wednesday 17:00: open again
    [InlineData("2026-12-06T22:30:00Z", true, "2026-12-06T23:00:00Z")] // winter Sunday (CST, UTC-6): it opens at 23:00 UTC
    public void The_Globex_schedule_knows_when_it_is_closed(string utc, bool closed, string nextOpen)
    {
        var t = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.Equal(closed, FuturesSessions.IsGlobexClosed(t));
        Assert.Equal(DateTime.Parse(nextOpen, null, System.Globalization.DateTimeStyles.AdjustToUniversal), FuturesSessions.NextGlobexOpen(t));
    }

    [Fact]
    public async Task With_the_market_closed_the_history_shows_the_last_session()
    {
        // the last trade was 2 days ago: the history is built up to there
        long last = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeSeconds();
        using var server = new FakeRithmicServer { LastTradeSsboe = last, PageSize = 50 };
        for (int i = 0; i < 40; i++) server.Ticks.Add((last - 600 + i * 10, 0, 5000 + i * 0.25, 1, i % 2 == 0));
        var feed = new RithmicFeed(Settings(server));
        var data = new InstrumentData(Es, feed) { TickWindow = TimeSpan.FromHours(2) };
        data.Start(DateTime.UtcNow.AddHours(-1.5));
        await Until(() => { data.Pump(); return !data.IsLoadingHistory; }, 15000);
        data.Stop();
        Assert.False(data.IsLoadingHistory);
        Assert.Equal(40, data.Trades.Count);
        Assert.True(data.HistoryFrom < DateTime.UtcNow.AddDays(-1.9));
    }
}
