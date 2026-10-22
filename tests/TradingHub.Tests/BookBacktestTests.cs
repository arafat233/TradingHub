using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>Backtests on the recorded order book: the strategy sees Book, BookImbalance and Icebergs.</summary>
public class BookBacktestTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
    private static readonly DateTime T0 = new(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>One hour of trades at 1000, a burst of 40 x 5 lots hitting the ask at 00:10, and a book that is bid-heavy for 30 minutes and then ask-heavy.</summary>
    private static TickData Data(string root, bool withBook)
    {
        var trades = new List<Trade>();
        long id = 1;
        for (int s = 0; s < 3600; s += 11) trades.Add(new Trade(id++, T0.AddSeconds(s), s % 22 == 0 ? 1000m : 999m, 1, s % 22 == 0 ? TradeSide.Buy : TradeSide.Sell));
        for (int k = 0; k < 40; k++) trades.Add(new Trade(id++, T0.AddMinutes(10).AddSeconds(k).AddMilliseconds(500), 1000m, 5, TradeSide.Buy));
        trades.Sort((a, b) => a.Time.CompareTo(b.Time));
        for (int i = 0; i < trades.Count; i++) trades[i] = new Trade(i + 1, trades[i].Time, trades[i].Price, trades[i].Volume, trades[i].Side);

        using (var w = new DepthFile.Writer(DepthFile.DayPath(root, "test", "TEST", DateOnly.FromDateTime(T0)), Inst))
        {
            for (int q = 0; q < 3600 * 4; q++)
            {
                var t = T0.AddMilliseconds(q * 250);
                bool bidHeavy = q < 1800 * 4;
                var b = new OrderBookBuilder();
                for (int k = 0; k < 20; k++)
                {
                    b.Set(BookSide.Bid, 999 - k, bidHeavy ? 40 : 10);
                    b.Set(BookSide.Ask, 1000 + k, bidHeavy ? 10 : 40);
                }

                w.Write(t, b.BuildUpdate(t, 100, true).Snapshot, 20);
            }
        }

        var data = TickData.FromTrades(Inst, trades);
        return withBook ? data.WithDepth(root) : data;
    }

    private sealed class Probe : Strategy
    {
        public int WithBook, WithoutBook, IcebergsSeen;
        public decimal EarlyImbalance = 9, LateImbalance = 9;

        protected override void OnInit() => TrackIcebergs = true;

        protected override void OnBar(Bar bar)
        {
            if (Book == null) { WithoutBook++; return; }
            WithBook++;
            if (bar.Time == T0.AddMinutes(15)) EarlyImbalance = BookImbalance(5);
            if (bar.Time == T0.AddMinutes(50)) LateImbalance = BookImbalance(5);
            IcebergsSeen = Math.Max(IcebergsSeen, Icebergs.Count(i => i.Ask && i.Volume >= 200));
        }
    }

    private static BacktestSettings Settings(bool useBook = true) => new()
    {
        FeedId = "test", Symbol = "TEST", FromUtc = T0, ToUtc = T0.AddHours(1), Period = "M5", CommissionPercent = 0, SlippageTicks = 0, UseBook = useBook,
    };

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "bk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void A_strategy_sees_the_recorded_book_its_imbalance_and_the_iceberg_that_was_hit()
    {
        var dir = TempDir();
        try
        {
            var s = new Probe();
            var result = new BacktestEngine(s, Data(dir, withBook: true), Settings()).Run();
            Assert.Contains(result.Log, l => l.Contains("Using the recorded order book"));
            Assert.True(s.WithBook >= 10, $"bars with a book: {s.WithBook}, without: {s.WithoutBook}");
            Assert.True(s.EarlyImbalance > 0.5m, $"bid-heavy first half: {s.EarlyImbalance}");
            Assert.True(s.LateImbalance < -0.5m, $"ask-heavy second half: {s.LateImbalance}");
            Assert.True(s.IcebergsSeen >= 1, "the 200 lots that hit the 10 lots shown at the ask were found");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Without_recorded_books_or_with_the_option_off_the_book_is_null()
    {
        var dir = TempDir();
        try
        {
            var none = new Probe();
            new BacktestEngine(none, Data(dir, withBook: false), Settings()).Run();
            Assert.Equal(0, none.WithBook);

            var off = new Probe();
            new BacktestEngine(off, Data(dir, withBook: true), Settings(useBook: false)).Run();
            Assert.Equal(0, off.WithBook);
            Assert.Equal(0, off.IcebergsSeen);
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Records only the given UTC hours of each day: a book that is bid-heavy on the first day and ask-heavy on the second.</summary>
    private static void WriteDay(string root, DateOnly day, bool bidHeavy, params (int From, int To)[] hours)
    {
        using var w = new DepthFile.Writer(DepthFile.DayPath(root, "test", "TEST", day), Inst);
        foreach (var (from, to) in hours)
            for (var t = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(from); t < day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(to); t = t.AddSeconds(1))
            {
                var b = new OrderBookBuilder();
                for (int k = 0; k < 10; k++)
                {
                    b.Set(BookSide.Bid, 999 - k, bidHeavy ? 40 : 10);
                    b.Set(BookSide.Ask, 1000 + k, bidHeavy ? 10 : 40);
                }

                w.Write(t, b.BuildUpdate(t, 50, true).Snapshot, 10);
            }
    }

    private sealed class Sampler : Strategy
    {
        public readonly Dictionary<DateTime, decimal?> Seen = new();
        protected override void OnBar(Bar bar) => Seen[bar.Time] = Book == null ? null : BookImbalance(5);
    }

    [Fact]
    public void A_backtest_over_several_days_follows_the_recorded_book_across_midnight_gaps_and_a_missing_day()
    {
        var dir = TempDir();
        try
        {
            var d1 = new DateOnly(2026, 2, 2);
            var d2 = d1.AddDays(1);
            // day 1: recorded 22:00-24:00, bid-heavy. day 2: recorded 00:00-01:00 and 02:00-03:00 (a hole of an hour), ask-heavy. day 3: nothing recorded
            WriteDay(dir, d1, bidHeavy: true, (22, 24));
            WriteDay(dir, d2, bidHeavy: false, (0, 1), (2, 3));
            var trades = new List<Trade>();
            long id = 1;
            for (var t = d1.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(21); t < d1.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(24 + 29); t = t.AddSeconds(13))
                trades.Add(new Trade(id++, t, 1000m, 1, id % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
            var data = TickData.FromTrades(Inst, trades).WithDepth(dir);
            Assert.NotNull(data.DepthRoot);

            var s = new Sampler();
            var settings = new BacktestSettings { FeedId = "test", Symbol = "TEST", FromUtc = data.FirstTime, ToUtc = data.LastTime.AddMinutes(1), Period = "M5", CommissionPercent = 0, SlippageTicks = 0 };
            new BacktestEngine(s, data, settings).Run();

            decimal? At(DateTime t) => s.Seen[t];
            var day1 = d1.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            Assert.Null(At(day1.AddHours(21).AddMinutes(30)));                       // before anything was recorded
            Assert.True(At(day1.AddHours(23).AddMinutes(30)) > 0.5m);                 // day 1, bid-heavy
            Assert.True(At(day1.AddHours(24).AddMinutes(30)) < -0.5m);                // day 2 after midnight: the next file is picked up, ask-heavy
            Assert.Null(At(day1.AddHours(25).AddMinutes(30)));                        // the hole in the recording: no stale book is shown
            Assert.True(At(day1.AddHours(26).AddMinutes(30)) < -0.5m);                // recorded again
            Assert.Null(At(day1.AddHours(28).AddMinutes(30)));                        // after the recording of day 2 (and day 3 has no file at all)
        }
        finally { Directory.Delete(dir, true); }
    }
}
