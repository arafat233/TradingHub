using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Feeds;
using TradingHub.Feeds.Alpaca;
using TradingHub.Feeds.Databento;
using TradingHub.Feeds.Oanda;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>New data sources (OANDA, Alpaca, Databento) against fake servers with their real formats.</summary>
public class DataSourceTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly DateOnly Day = new(2026, 10, 12); // Monday

    /// <summary>Gathers what the feed sends (from another thread); the public lists are copies, so they can be read without races.</summary>
    private sealed class Sink : IMarketDataSink
    {
        private readonly List<Trade> _trades = new();
        private readonly List<DepthUpdate> _depth = new();
        private readonly List<(FeedStatus, string?)> _status = new();

        public List<Trade> Trades
        {
            get
            {
                lock (_trades) return _trades.ToList();
            }
        }

        public List<DepthUpdate> Depth
        {
            get
            {
                lock (_depth) return _depth.ToList();
            }
        }

        public List<(FeedStatus, string?)> Status
        {
            get
            {
                lock (_status) return _status.ToList();
            }
        }

        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
            lock (_trades) _trades.AddRange(trades);
        }

        public void OnDepth(Instrument instrument, DepthUpdate update)
        {
            lock (_depth) _depth.Add(update);
        }

        public void OnStatus(Instrument instrument, FeedStatus status, string? message)
        {
            lock (_status) _status.Add((status, message));
        }
    }

    private static async Task Until(Func<bool> condition, int ms = 8000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(30);
    }

    // ===================================================================== OANDA

    private static OandaFeed OandaFeedFor(FakeHttpServer s, string token = "TOKEN") => new(new OandaSettings { Token = token, RestUrl = s.Http, StreamUrl = s.Http });

    [Fact]
    public async Task Oanda_lists_the_cfds_with_their_tick_and_downloads_a_day_in_trades()
    {
        using var server = FakeBrokers.Oanda();
        using var feed = OandaFeedFor(server);
        var list = await feed.GetInstrumentsAsync(CancellationToken.None);
        var nas = list[0];
        Assert.Equal("NAS100_USD", nas.Symbol);
        Assert.Equal(0.1m, nas.TickSize);
        Assert.Contains("CFD", nas.Description);
        Assert.Equal(0.00001m, list.First(i => i.Symbol == "EUR_USD").TickSize);

        var trades = await feed.DownloadDayAsync(nas, Day, null, CancellationToken.None);
        Assert.NotNull(trades);
        Assert.Equal(720 * 4, trades!.Count); // one hour of 5 s candles, four trades per candle
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.Second.Time >= p.First.Time && p.Second.Id > p.First.Id));
        Assert.All(trades, t => Assert.Equal(2m, t.Volume)); // 8 of tick volume split across 4
        Assert.Equal(Day.ToDateTime(new TimeOnly(13, 0), DateTimeKind.Utc), trades[0].Time);
        // requests of at most 5000 candles (one day = 4 requests)
        Assert.Equal(4, server.Requests.Count(r => r.Route.EndsWith("/candles", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Oanda_gives_one_minute_candles_and_live_prices()
    {
        using var server = FakeBrokers.Oanda();
        using var feed = OandaFeedFor(server);
        var nas = (await feed.GetInstrumentsAsync(CancellationToken.None))[0];
        var klines = new List<Kline>();
        await feed.LoadKlinesAsync(nas, Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1), 1, 0,
            b => { klines.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(60, klines.Count);

        var sink = new Sink();
        using (feed.Subscribe(nas, sink))
        {
            await Until(() => sink.Trades.Count >= 5);
        }

        Assert.Equal(5, sink.Trades.Count);
        Assert.Equal(20000.5m, sink.Trades[0].Price); // mid price
        Assert.True(sink.Depth.Count >= 5);
        Assert.Contains(sink.Status, x => x.Item1 == FeedStatus.Connected);
    }

    [Fact]
    public async Task Oanda_with_a_bad_token_explains_the_problem()
    {
        using var server = FakeBrokers.Oanda();
        using var feed = OandaFeedFor(server, "OTHER");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => feed.GetInstrumentsAsync(CancellationToken.None));
        Assert.Contains("token", ex.Message);
    }

    // ===================================================================== Alpaca

    private static AlpacaFeed AlpacaFeedFor(FakeHttpServer s) =>
        new(new AlpacaSettings { KeyId = "KEY", Secret = "SECRET", DataUrl = s.Http, TradingUrl = s.Http, StreamUrl = s.Ws + "/v2/iex" });

    [Fact]
    public async Task Alpaca_downloads_the_real_trades_paginated_with_the_side_by_the_tick_rule()
    {
        using var server = FakeBrokers.Alpaca();
        using var feed = AlpacaFeedFor(server);
        var list = await feed.GetInstrumentsAsync(CancellationToken.None);
        Assert.Equal("SPY", list[0].Symbol); // the popular ones first
        Assert.Equal("QQQ", list[1].Symbol);
        Assert.DoesNotContain(list, i => i.Symbol == "XYZ");
        var spy = list[0];
        var trades = await feed.DownloadDayAsync(spy, Day, null, CancellationToken.None);
        Assert.NotNull(trades);
        Assert.Equal(5, trades!.Count);
        Assert.Equal(new[] { 100m, 50m, 20m, 10m, 5m }, trades.Select(t => t.Volume));
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Buy, TradeSide.Buy, TradeSide.Sell, TradeSide.Buy }, trades.Select(t => t.Side));
        Assert.Equal(2, server.Requests.Count(r => r.Route == "/v2/stocks/SPY/trades"));

        var klines = new List<Kline>();
        await feed.LoadKlinesAsync(spy, Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1), 1, 0,
            b => { klines.AddRange(b); return true; }, null, CancellationToken.None);
        Assert.Equal(2, klines.Count);
        Assert.Equal(120000m, klines[0].Volume);
    }

    [Fact]
    public async Task Alpaca_live_sends_trades_and_the_best_bid_ask()
    {
        using var server = FakeBrokers.Alpaca();
        using var feed = AlpacaFeedFor(server);
        var spy = new Instrument { Symbol = "SPY", FeedId = AlpacaFeed.FeedId, TickSize = 0.01m };
        var sink = new Sink();
        using (feed.Subscribe(spy, sink))
        {
            await Until(() => sink.Trades.Count >= 2);
        }

        Assert.Equal(2, sink.Trades.Count);
        Assert.Equal(580.12m, sink.Trades[0].Price);
        Assert.Equal(TradeSide.Sell, sink.Trades[1].Side);
        Assert.NotEmpty(sink.Depth);
        Assert.Contains(sink.Status, x => x.Item1 == FeedStatus.Connected);
    }

    // ===================================================================== Databento

    [Fact]
    public async Task Databento_computes_the_cost_and_downloads_trades_with_the_real_aggressor_side()
    {
        using var server = FakeBrokers.Databento();
        using var client = new DatabentoClient("db-KEY", server.Http + "/v0");
        Assert.Equal(1.2575m, await client.CostAsync("NQ", Day, Day, CancellationToken.None));
        var (start, end) = await client.RangeAsync(CancellationToken.None);
        Assert.Equal(2010, start.Year);
        Assert.Equal(new DateTime(2026, 10, 13), end.Date);
        var nq = new Instrument { Symbol = "NQ", FeedId = "rithmic", TickSize = 0.25m, PointValue = 20 };
        var trades = await client.DownloadDayAsync(nq, Day, null, CancellationToken.None);
        Assert.NotNull(trades);
        Assert.Equal(4, trades!.Count);
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Sell, TradeSide.Sell, TradeSide.Buy }, trades.Select(t => t.Side));
        Assert.Equal(new[] { 20000.25m, 20000m, 19999.75m, 20000.25m }, trades.Select(t => t.Price)); // 20000.26 -> to the tick
        Assert.Equal(Day.ToDateTime(new TimeOnly(14, 30), DateTimeKind.Utc), trades[0].Time);
        Assert.Equal(TimeSpan.FromMilliseconds(1), trades[1].Time - trades[0].Time);
        // the cost is requested without downloading anything (separate requests)
        Assert.Single(server.Requests, r => r.Route == "/v0/timeseries.get_range");
    }

    [Fact]
    public async Task Databento_with_a_bad_key_says_so_clearly()
    {
        using var server = FakeBrokers.Databento();
        using var client = new DatabentoClient("other", server.Http + "/v0");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.CostAsync("ES", Day, Day, CancellationToken.None));
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public void Synthetic_history_from_short_candles_respects_the_ohlc()
    {
        var inst = new Instrument { Symbol = "X", FeedId = "x", TickSize = 0.5m };
        var list = new List<Trade>();
        long last = 0;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        HistoryBuilder.Synthesize(t0, TimeSpan.FromSeconds(5), 100, 103, 99, 98.9m, 4, inst, ref last, list);
        Assert.Equal(new[] { 100m, 103m, 99m, 99m }, list.Select(t => t.Price)); // down: the high first, then the low; close at the tick
        Assert.Equal(new[] { TradeSide.Sell, TradeSide.Buy, TradeSide.Sell, TradeSide.Buy }, list.Select(t => t.Side));
        Assert.All(list, t => Assert.Equal(1m, t.Volume));
        Assert.True(list.Zip(list.Skip(1)).All(p => p.Second.Id > p.First.Id && p.Second.Time > p.First.Time));
    }
}
