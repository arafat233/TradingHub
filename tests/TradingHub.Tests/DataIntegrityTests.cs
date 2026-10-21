using System.Reflection;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>
/// What happened on 3 Oct: two processes recording the same tick file mixed it up, the app loaded junk
/// (huge fake candles) and an id that ran ahead made the whole live feed get discarded (the chart froze).
/// </summary>
public class DataIntegrityTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m };
    private static readonly DateTime Day = new(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);

    private static List<Trade> Stream(int n, int seed = 7)
    {
        var rnd = new Random(seed);
        var list = new List<Trade>(n);
        decimal price = 84_700m;
        for (int i = 0; i < n; i++)
        {
            price += rnd.Next(-3, 4) * 0.1m;
            decimal vol = Math.Round((decimal)rnd.NextDouble() * 2m + 0.001m, 3);
            list.Add(new Trade(3_474_393_766 + i, Day.AddMilliseconds(i * 40), price, vol, rnd.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        return list;
    }

    /// <summary>
    /// Removes the temp folder. On Windows a file that was just created or replaced can stay in use for a
    /// moment, so the delete is retried; it never throws, because a teardown that fails would replace the
    /// real failure of the test with its own exception.
    /// </summary>
    private static void Cleanup(string dir)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                Directory.Delete(dir, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "tradinghub_integrity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Two_mixed_writers_do_not_let_junk_through()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "2026-10-04.vtk");
            var trades = Stream(60_000);
            var byId = trades.ToDictionary(t => t.Id);

            // as on 3 Oct: the user's Trading Hub (A) records; another process (B) starts on the same file and each
            // one writes at its own position, overwriting the other in pieces; afterwards A closes and a fresh Trading Hub (C)
            // carries on recording at the end. The file ends up with truncated records and mixed chains of differences.
            var a = new TickFile.Writer(path, Btc);
            for (int i = 0; i < 20_000; i++)
            {
                a.Write(trades[i]);
                if (i % 20 == 0) a.Flush();
            }

            a.Flush();
            // Windows enforces the writer's FileShare.Read, so B cannot even open the file and the mix-up
            // is impossible there: what A wrote on its own has to be intact and there is nothing to repair.
            // On macOS the share mode is advisory, both writers get in, and the rest of this test checks
            // that the damage is detected and repaired.
            TickFile.Writer b;
            try
            {
                b = new TickFile.Writer(path, Btc);
            }
            catch (IOException) when (OperatingSystem.IsWindows())
            {
                a.Dispose();
                TickFile.Read(path, out int intact);
                Assert.Equal(0, intact);
                return;
            }

            for (int i = 20_000; i < 21_000; i++) a.Write(trades[i]);
            a.Flush();
            // B had run ahead (as in the real file: 812 trades) and writes at the same position
            for (int i = 20_812; i < 30_000; i++) b.Write(trades[i]);
            b.Flush();
            for (int i = 21_000; i < 22_000; i++)
            {
                a.Write(trades[i]);
                if (i % 50 == 0) a.Flush();
            }

            a.Dispose();
            b.Dispose();
            using (var c = new TickFile.Writer(path, Btc))
                for (int i = 30_000; i < trades.Count; i++) c.Write(trades[i]);

            var read = TickFile.Read(path, out int dropped);
            Assert.True(dropped > 0, "it had to find the damaged stretch");
            // everything left is real: same time, price and volume as the original trade with that id
            var fake = read.Where(t => !byId.TryGetValue(t.Id, out var o) || o.Time != t.Time || o.Price != t.Price || o.Volume != t.Volume).ToList();
            Assert.True(fake.Count == 0, $"{fake.Count} fake trades out of {read.Count} (dropped {dropped}); the first one: {fake.FirstOrDefault()}");

            // what came before and after the mix-up is kept (at most a 4096 stretch is lost on the way out
            // of the mixed zone; that gap is requested from the exchange again on loading)
            int before = read.Count(t => t.Id < trades[15_000].Id), after = read.Count(t => t.Id >= trades[30_000].Id);
            Assert.True(before >= 15_000 && after >= 30_000 - 4_097, $"before {before}, after {after}, read {read.Count}, dropped {dropped}");

            // repairing leaves the file clean and readable with nothing discarded
            Assert.True(TickFile.Repair(path, Btc) > 0);
            var again = TickFile.Read(path, out int dropped2);
            Assert.Equal(0, dropped2);
            Assert.Equal(read.Count, again.Count);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void A_healthy_file_is_read_in_full()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "2026-10-04.vtk");
            var trades = Stream(10_000, seed: 3);
            using (var w = new TickFile.Writer(path, Btc))
                foreach (var t in trades) w.Write(t);
            // old history appended afterwards (it goes back: a keyframe) is also valid
            using (var w = new TickFile.Writer(path, Btc))
                foreach (var t in trades.Take(500)) w.Write(t);

            var read = TickFile.Read(path, out int dropped);
            Assert.Equal(0, dropped);
            Assert.Equal(10_500, read.Count);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Only_one_process_can_record_each_instrument()
    {
        var dir = TempDir();
        try
        {
            using var first = TickFile.TryLock(dir, "binance-futures", "BTCUSDT");
            Assert.NotNull(first);
            Assert.Null(TickFile.TryLock(dir, "binance-futures", "BTCUSDT"));
            using (var other = TickFile.TryLock(dir, "binance-futures", "ETHUSDT")) // another instrument, yes
                Assert.NotNull(other);
            first!.Dispose();
            using var again = TickFile.TryLock(dir, "binance-futures", "BTCUSDT");
            Assert.NotNull(again);

            // the recorder that does not get the permission writes nothing
            using var rec = new TickRecorder(dir, new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m });
            Assert.False(rec.IsActive);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void An_id_that_runs_ahead_does_not_freeze_the_live_feed()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        data.Trades.Add(new Trade(100, Day, 84_700m, 1, TradeSide.Buy));
        data.Trades.Add(new Trade(9_999_999, Day.AddSeconds(1), 84_701m, 1, TradeSide.Buy)); // junk with an id from the future
        typeof(InstrumentData).GetField("_historyMerged", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(data, true);

        IMarketDataSink sink = data;
        sink.OnTrades(Btc, new[]
        {
            new Trade(100, Day, 84_700m, 1, TradeSide.Buy), // repeated: it is ignored
            new Trade(101, Day.AddSeconds(2), 84_702m, 1, TradeSide.Buy),
            new Trade(102, Day.AddSeconds(3), 84_703m, 1, TradeSide.Sell),
        });
        data.Pump();
        Assert.Equal(4, data.Trades.Count);
        Assert.Equal(84_703m, data.LastPrice);
    }

    [Fact]
    public void An_out_of_order_trade_does_not_inflate_the_current_candle()
    {
        var store = new TradeStore(Btc);
        store.Add(new Trade(1, Day, 84_700m, 1, TradeSide.Buy));
        store.Add(new Trade(2, Day.AddMinutes(1), 84_710m, 1, TradeSide.Buy));
        store.Add(new Trade(3, Day.AddMinutes(1).AddSeconds(5), 84_705m, 1, TradeSide.Sell));
        store.Add(new Trade(4, Day.AddDays(-88), 166_543m, 25, TradeSide.Sell)); // July time, absurd price
        var series = new CandleSeries(Btc, ChartPeriod.M1);
        series.Rebuild(store);
        Assert.Equal(2, series.Count);
        Assert.Equal(Btc.ToTicks(84_710m), series[1].HighTicks);
        Assert.Equal(2m, series[1].Volume);
    }
}
