using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.CTrader;
using TradingHub.Feeds.Tradovate;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>cTrader (Open API JSON) and Tradovate (REST + WebSocket) against fake servers with their real messages.</summary>
public class BrokerConnectionTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);

    private sealed class Sink : IMarketDataSink
    {
        public readonly List<Trade> Trades = new();
        public readonly List<DepthUpdate> Depth = new();
        public readonly List<(FeedStatus, string?)> Status = new();

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            lock (Trades) Trades.AddRange(trades);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update)
        {
            lock (Depth) Depth.Add(update);
        }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            lock (Status) Status.Add((status, message));
        }
    }

    private static async Task Until(Func<bool> condition, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    // ===================================================================== cTrader

    [Fact]
    public async Task CTrader_authenticates_picks_the_demo_account_and_lists_the_symbols()
    {
        using var fake = new FakeCTrader();
        using var feed = new CTraderFeed(() => fake.Settings());
        var (ok, msg) = await feed.TestAsync(CancellationToken.None);
        Assert.True(ok);
        Assert.Contains("FakeBroker 5012345", msg); // the demo one, not the real one
        var list = await feed.GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal("US100", list[0].Symbol);
        Assert.Equal(0.01m, list[0].TickSize);
        Assert.Equal(1m, list[0].VolumeStep);
        Assert.Equal(0.00001m, list.First(i => i.Symbol == "EURUSD").TickSize);
        Assert.Equal(new[] { 2100, 2149, 2102, 2114, 2116 }, fake.Received.Where(t => t != 51).Take(5));
    }

    [Fact]
    public async Task CTrader_downloads_ticks_with_pages_and_differences_without_losing_or_repeating()
    {
        using var fake = new FakeCTrader { TickPage = 30 };
        using var feed = new CTraderFeed(() => fake.Settings());
        var us100 = (await feed.GetInstrumentsAsync(CancellationToken.None))[0];
        var trades = await feed.DownloadDayAsync(us100, Day, null, CancellationToken.None);
        Assert.NotNull(trades);
        Assert.Equal(100, trades!.Count);
        Assert.Equal(20000.00m, trades[0].Price);
        Assert.Equal(20000.99m, trades[^1].Price);
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.Second.Time > p.First.Time && p.Second.Id > p.First.Id));
        Assert.All(trades.Skip(1), t => Assert.Equal(TradeSide.Buy, t.Side)); // it always rises
        Assert.True(fake.TickRequests >= 4); // 100 ticks, 30 at a time
    }

    [Fact]
    public async Task CTrader_gives_candles_and_live_prices()
    {
        using var fake = new FakeCTrader();
        using var feed = new CTraderFeed(() => fake.Settings());
        var us100 = (await feed.GetInstrumentsAsync(CancellationToken.None))[0];
        var klines = new List<Kline>();
        var from = Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await feed.LoadKlinesAsync(us100, from, from.AddDays(1), 1, 0, b => { klines.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(3, klines.Count);
        Assert.Equal(20000.20m, klines[0].Open);
        Assert.Equal(20000.90m, klines[0].High);
        Assert.Equal(20000.00m, klines[0].Low);
        Assert.Equal(20000.50m, klines[0].Close);
        Assert.Equal(50m, klines[0].Volume);

        var sink = new Sink();
        using (feed.Subscribe(us100, sink))
        {
            await Until(() => sink.Trades.Count >= 3);
        }

        Assert.Equal(new[] { 20001.00m, 20001.01m, 20001.02m }, sink.Trades.Select(t => t.Price)); // at the bid
        Assert.NotEmpty(sink.Depth);
    }

    [Fact]
    public async Task CTrader_with_a_bad_secret_explains_the_error()
    {
        using var fake = new FakeCTrader();
        using var feed = new CTraderFeed(() => fake.Settings("MAL"));
        var ex = await Assert.ThrowsAsync<CTraderException>(() => feed.TestAsync(CancellationToken.None));
        Assert.Equal("CH_CLIENT_AUTH_FAILURE", ex.Code);
        Assert.Contains("incorrect", ex.Message);
    }

    // ===================================================================== Tradovate

    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = TradovateFeed.FeedId, Exchange = "CME", TickSize = 0.25m, VolumeStep = 1, PointValue = 50 };

    [Fact]
    public async Task Tradovate_logs_in_with_the_api_key_and_shares_a_single_token()
    {
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        var (ok, msg) = await feed.TestAsync(CancellationToken.None);
        Assert.True(ok);
        Assert.Contains("demo", msg);
        Assert.Contains("1 account", msg);
        Assert.Equal(1, fake.Logins); // one login: REST and the sockets use the same token
        Assert.Equal("MD1", fake.MdToken); // the data socket uses the data token

        using var bad = new TradovateFeed(() => fake.Settings("mal"));
        var ex = await Assert.ThrowsAsync<TradovateException>(() => bad.TestAsync(CancellationToken.None));
        Assert.Contains("Incorrect username", ex.Message);
    }

    [Fact]
    public async Task Tradovate_downloads_paginated_tick_by_tick_history_with_the_aggressor_side()
    {
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        var trades = new List<Trade>();
        var from = Day.ToDateTime(new TimeOnly(14, 0), DateTimeKind.Utc);
        await feed.LoadTradesAsync(Es, from, from.AddHours(1), null, b => { trades.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(12, trades.Count); // 5 at a time per request, with no repeats
        Assert.True(fake.ChartRequests >= 3);
        Assert.Equal(5000m, trades[0].Price);
        Assert.Equal(5002.75m, trades[^1].Price);
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.Second.Time > p.First.Time && p.Second.Id > p.First.Id));
        // even id: it traded at the ask (a buy); odd: at the bid (a sell)
        Assert.Equal(Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell), trades.Select(t => t.Side));
        Assert.Contains("md/cancelChart", fake.MdRequests);
    }

    [Fact]
    public async Task Tradovate_live_sends_the_tape_and_the_book()
    {
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        var sink = new Sink();
        using (feed.Subscribe(Es, sink))
        {
            await Until(() => sink.Trades.Count >= 2 && sink.Depth.Count >= 1);
        }

        Assert.Equal(2, sink.Trades.Count); // the subscription's history tick is not repeated
        Assert.Equal(5000.25m, sink.Trades[0].Price);
        Assert.Equal(TradeSide.Buy, sink.Trades[0].Side); // at the ask
        Assert.Equal(TradeSide.Sell, sink.Trades[1].Side); // at the bid
        Assert.Contains(sink.Status, s => s.Item1 == FeedStatus.Connected);
        Assert.Contains("md/subscribeDOM", fake.MdRequests);
    }

    [Fact]
    public async Task Tradovate_trades_the_account_with_orders_fills_and_position()
    {
        using var fake = new FakeTradovate();
        using var feed = new TradovateFeed(() => fake.Settings());
        using var session = new TradovateTradingSession(feed.Connection);
        var fills = new List<TradovateFill>();
        session.FillReceived += f => { lock (fills) fills.Add(f); };
        session.Start();
        await Until(() => session.Accounts.Count == 1);
        var acc = Assert.Single(session.Accounts);
        Assert.Equal("DEMO101", acc.Name);

        var data = new InstrumentData(Es, new SimulatedFeed());
        using var broker = new TradovateBroker(data, session, acc, "DEMO101 · Tradovate (demo)");
        Assert.False(broker.IsReal);
        broker.Market(OrderDirections.Buy, 2, "Chart Trader");
        await Until(() => broker.Position.Volume == 2 && fills.Count == 1);
        Assert.Equal(2, broker.Position.Volume);
        Assert.Equal(5000.25m, broker.Position.AveragePrice);
        var f = Assert.Single(fills);
        Assert.Equal(101, f.AccountId);
        Assert.Equal(fake.Contract, f.Symbol);
        var sent = fake.Orders[0];
        Assert.Equal("DEMO101", sent["accountSpec"]!.GetValue<string>());
        Assert.Equal(fake.Contract, sent["symbol"]!.GetValue<string>());
        Assert.False(sent["isAutomated"]!.GetValue<bool>());

        // limit: it stays active, is modified and is cancelled
        var limit = broker.Limit(OrderDirections.Buy, 1, 4990, "Strategy: entry");
        await Until(() => limit.ExchangeOrderId.Length > 0 && broker.ActiveOrders.Contains(limit));
        Assert.True(fake.Orders[1]["isAutomated"]!.GetValue<bool>()); // from a strategy
        Assert.Equal(4990m, fake.Orders[1]["price"]!.GetValue<decimal>());
        broker.Modify(limit, 4985);
        await Until(() => fake.Orders.Count >= 3);
        Assert.Equal(4985m, fake.Orders[2]["price"]!.GetValue<decimal>());
        broker.Cancel(limit);
        await Until(() => limit.State == OrderStates.Done);
        Assert.Equal(OrderStatus.Canceled, limit.Status);
        Assert.DoesNotContain(limit, broker.ActiveOrders);
    }
}
