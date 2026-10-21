using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Long history: exchange candles turned into trades and mixed with real ticks.</summary>
public class LongHistoryTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 0.5m, VolumeStep = 0.001m };

    private static long Ms(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();

    [Fact]
    public void The_built_candles_respect_ohlc_volume_and_delta()
    {
        var t0 = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var klines = new List<Kline>
        {
            new(Ms(t0), 100, 104, 99, 103, 10, 7, true),               // bullish: buys 7, sells 3
            new(Ms(t0.AddMinutes(1)), 103, 103.5m, 97, 98, 4, 0, true), // sells only
            new(Ms(t0.AddMinutes(2)), 98, 99, 98, 99, 2, 2, true),      // buys only
        };
        var trades = new List<Trade>();
        KlineHistory.Synthesize(klines, Inst, 1, new[] { (t0, t0.AddMinutes(3)) }, trades);
        Assert.Equal(12, trades.Count);
        Assert.All(trades, t => Assert.True(KlineHistory.IsSynthetic(t.Id)));
        Assert.True(trades.Zip(trades.Skip(1)).All(p => p.First.Time < p.Second.Time && p.First.Id < p.Second.Id));

        var store = new TradeStore(Inst);
        foreach (var t in trades) store.Add(t);
        var m1 = new CandleSeries(Inst, ChartPeriod.M1);
        m1.Rebuild(store);
        Assert.Equal(3, m1.Count);
        for (int i = 0; i < 3; i++)
        {
            var k = klines[i];
            var c = m1[i];
            Assert.True(c.IsSynthetic);
            Assert.Equal(k.Open, m1.TicksToPrice(c.OpenTicks));
            Assert.Equal(k.High, m1.TicksToPrice(c.HighTicks));
            Assert.Equal(k.Low, m1.TicksToPrice(c.LowTicks));
            Assert.Equal(k.Close, m1.TicksToPrice(c.CloseTicks));
            Assert.Equal(k.Volume, c.Volume);
            Assert.Equal(k.BuyVolume - (k.Volume - k.BuyVolume), c.Delta);
            Assert.Equal(0m, c.MaxTradeVolume); // they do not count as "big trades"
        }

        // aggregated into a one-hour candle: exact totals
        var h1 = new CandleSeries(Inst, ChartPeriod.H1);
        h1.Rebuild(store);
        Assert.Single(h1.Candles);
        Assert.Equal(16m, h1[0].Volume);
        Assert.Equal(104m, h1.TicksToPrice(h1[0].HighTicks));
        Assert.Equal(97m, h1.TicksToPrice(h1[0].LowTicks));
        Assert.Equal((7 + 0 + 2) - (3 + 4 + 0), h1[0].Delta);
    }

    [Fact]
    public void The_candles_only_fill_the_gaps_with_no_ticks()
    {
        var t0 = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var real = new List<Trade>
        {
            new(1, t0.AddMinutes(30), 100, 1, TradeSide.Buy),
            new(2, t0.AddMinutes(31), 100, 1, TradeSide.Buy),
            new(3, t0.AddMinutes(32), 100, 1, TradeSide.Buy),
        };
        var coverage = KlineHistory.Coverage(real);
        Assert.Single(coverage);
        var windows = KlineHistory.Subtract(new[] { (t0, t0.AddHours(1)) }, coverage);
        Assert.Equal(2, windows.Count);
        Assert.Equal(t0.AddMinutes(30), windows[0].To);
        Assert.Equal(t0.AddMinutes(33), windows[1].From);

        var klines = Enumerable.Range(0, 60).Select(i => new Kline(Ms(t0.AddMinutes(i)), 100, 101, 99, 100, 1, 0.5m, true)).ToList();
        var synth = new List<Trade>();
        KlineHistory.Synthesize(klines, Inst, 1, windows, synth);
        // 30 minutes before and 27 after (the minutes with real ticks are not invented)
        Assert.Equal((30 + 27) * 4, synth.Count);
        var merged = KlineHistory.MergeByTime(synth, real);
        Assert.True(merged.Zip(merged.Skip(1)).All(p => p.First.Time <= p.Second.Time));
    }

    /// <summary>Fake feed: real ticks only for what was requested and 1-minute candles for everything.</summary>
    private sealed class FakeFeed : IMarketDataFeed, IKlineHistory
    {
        public int TickRequests;
        public DateTime EarliestTickRequested = DateTime.MaxValue;
        public string Id => "test";
        public string DisplayName => "Test";
        public DateTime Now => DateTime.UtcNow;
        public bool SupportsHistory => true;
        public bool IsLive => false;
        public void Dispose() { }
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { Inst });
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink) => new Nothing();
        public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(DateTime.UtcNow.AddYears(-1));

        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            TickRequests++;
            if (fromUtc < EarliestTickRequested) EarliestTickRequested = fromUtc;
            var list = new List<Trade>();
            long id = 1_000_000 + (long)(fromUtc - DateTime.UnixEpoch).TotalSeconds;
            for (var t = fromUtc; t < toUtc; t = t.AddSeconds(20)) list.Add(new Trade(id++, t, 100, 0.01m, TradeSide.Buy));
            onBatch(list);
            return Task.CompletedTask;
        }

        public Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice, Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
        {
            var list = new List<Kline>();
            long step = minutes * 60_000L;
            for (long t = Ms(fromUtc) / step * step; t < Ms(toUtc); t += step) list.Add(new Kline(t, 100, 101, 99, 100, 1, 0.6m, true));
            onBatch(list);
            return Task.CompletedTask;
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }

    [Fact]
    public void A_year_of_history_loads_in_candles_and_the_recent_part_in_ticks()
    {
        var feed = new FakeFeed();
        var data = new InstrumentData(Inst, feed) { TickWindow = TimeSpan.FromHours(1) };
        data.Start(feed.Now.AddMinutes(-30));
        for (int i = 0; i < 400 && data.IsLoadingHistory; i++)
        {
            data.Pump();
            Thread.Sleep(10);
        }

        Assert.False(data.IsLoadingHistory);
        int initial = data.Trades.Count;
        Assert.True(initial > 0);

        // requesting a year: anything more than an hour back comes in candles
        Assert.True(data.ExtendHistory(feed.Now.AddDays(-365)));
        for (int i = 0; i < 1000 && data.IsExtending; i++)
        {
            data.Pump();
            Thread.Sleep(10);
        }

        Assert.False(data.IsExtending);
        Assert.Null(data.HistoryError);
        Assert.True(feed.EarliestTickRequested >= feed.Now.AddHours(-1.1), "it did not request ticks from a year back");
        Assert.True(data.Trades.FirstTime <= feed.Now.AddDays(-364));
        Assert.True(data.Trades.Count < 3_000_000);

        var series = new CandleSeries(Inst, ChartPeriod.H1);
        series.Rebuild(data.Trades);
        Assert.True(series[0].IsSynthetic);
        Assert.False(series[series.Count - 1].IsSynthetic);
    }
}
