using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

public class DepthFileTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "test", TickSize = 0.25m, VolumeStep = 1 };
    private static readonly Instrument Btc = new() { Symbol = "BTC", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m };

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "vdk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static DepthSnapshot Book(Instrument inst, DateTime t, decimal mid, int i, int levels = 20, decimal sizeStep = 1)
    {
        var b = new OrderBookBuilder();
        for (int k = 0; k < levels; k++)
        {
            b.Set(BookSide.Bid, inst.ToTicks(mid) - k, (k + 1 + i % 7) * sizeStep);
            b.Set(BookSide.Ask, inst.ToTicks(mid) + 1 + k, (k + 2 + i % 5) * sizeStep);
        }

        return b.BuildUpdate(t, 100, true).Snapshot;
    }

    private static void AssertSame(DepthSnapshot a, DepthSnapshot b)
    {
        Assert.Equal(a.Bids, b.Bids);
        Assert.Equal(a.Asks, b.Asks);
    }

    [Fact]
    public void A_day_of_books_comes_back_exactly_across_keyframes_and_with_fractional_sizes()
    {
        var dir = TempDir();
        try
        {
            var t0 = new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);
            var path = DepthFile.DayPath(dir, "test", "BTC", DateOnly.FromDateTime(t0));
            var written = new List<(DateTime, DepthSnapshot)>();
            using (var w = new DepthFile.Writer(path, Btc))
            {
                for (int i = 0; i < 700; i++) // more than two keyframe intervals
                {
                    var t = t0.AddMilliseconds(i * 250);
                    var b = Book(Btc, t, 86000m + (i % 40) * 0.1m - (i % 13) * 0.3m, i, sizeStep: 0.137m);
                    w.Write(t, b, 20);
                    written.Add((t, b));
                }
            }

            var read = DepthFile.Read(path);
            Assert.Equal(written.Count, read.Count);
            for (int i = 0; i < read.Count; i++)
            {
                Assert.Equal(written[i].Item1, read[i].Time);
                AssertSame(written[i].Item2, read[i].Book);
            }

            Assert.Equal(new[] { DateOnly.FromDateTime(t0) }, DepthFile.AvailableDays(dir, "test", "BTC").ToArray());
            Assert.True(new FileInfo(path).Length / 700 < 140, "about 100 bytes per 40-level snapshot");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_file_cut_in_the_middle_of_a_record_keeps_everything_before_it_and_levels_are_capped()
    {
        var dir = TempDir();
        try
        {
            var t0 = new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);
            var path = DepthFile.DayPath(dir, "test", "ES", DateOnly.FromDateTime(t0));
            using (var w = new DepthFile.Writer(path, Es))
                for (int i = 0; i < 50; i++) w.Write(t0.AddSeconds(i), Book(Es, t0, 5000m, i, levels: 30), 10); // 30 levels offered, 10 kept
            var full = DepthFile.Read(path);
            Assert.Equal(50, full.Count);
            Assert.Equal(10, full[0].Book.Bids.Length);
            Assert.Equal(10, full[0].Book.Asks.Length);

            var bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..^7]); // the last record is left half-written
            var cut = DepthFile.Read(path);
            Assert.Equal(49, cut.Count);
            AssertSame(full[48].Book, cut[48].Book);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_recorder_throttles_and_writes_on_its_own_thread_and_a_range_reads_across_days()
    {
        var dir = TempDir();
        try
        {
            var t0 = new DateTime(2026, 10, 1, 23, 59, 58, DateTimeKind.Utc);
            using (var r = new DepthRecorder(dir, Es))
                for (int i = 0; i < 80; i++) r.Offer(t0.AddMilliseconds(i * 100), Book(Es, t0, 5000m, i)); // every 100 ms, kept every 250
            var all = DepthFile.ReadRange(dir, "test", "ES", t0, t0.AddSeconds(10));
            Assert.InRange(all.Count, 25, 32);                                  // ~8 s / 250 ms → one every 300 ms (offers are 100 ms apart)
            Assert.Equal(new[] { new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2) }, DepthFile.AvailableDays(dir, "test", "ES").ToArray());
            Assert.True(all.Zip(all.Skip(1)).All(p => p.First.Time < p.Second.Time));
            var tail = DepthFile.ReadRange(dir, "test", "ES", new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), t0.AddSeconds(10));
            Assert.True(tail.Count > 0 && tail.Count < all.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class DepthReplayTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "test", TickSize = 0.25m, VolumeStep = 1 };
    private static readonly DateTime T0 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Three hours recorded: a book every second and a trade every 5 s, with a 600-lot wall at 5003 from minute 100 on.</summary>
    private static string Record()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rp-" + Guid.NewGuid().ToString("N"));
        var day = DateOnly.FromDateTime(T0);
        using (var w = new DepthFile.Writer(DepthFile.DayPath(dir, "test", "ES", day), Es))
            for (int s = 0; s < 3 * 3600; s++)
            {
                var t = T0.AddSeconds(s);
                var b = new OrderBookBuilder();
                b.Set(BookSide.Bid, Es.ToTicks(4999.75m), 5);
                b.Set(BookSide.Ask, Es.ToTicks(5000.25m), 5);
                if (s >= 6000) b.Set(BookSide.Ask, Es.ToTicks(5003m), 600);
                w.Write(t, b.BuildUpdate(t, 50, true).Snapshot, 20);
            }

        using (var w = new TickFile.Writer(TickFile.DayPath(dir, "test", "ES", day), Es))
            for (int s = 0; s < 3 * 3600; s += 5) w.Write(new Trade(s + 1, T0.AddSeconds(s), 5000m + (s / 5 % 4) * 0.25m, 1, TradeSide.Buy));
        return dir;
    }

    private static void WaitLoaded(DepthReplay r, Func<bool> done)
    {
        for (int i = 0; i < 400 && !done(); i++)
        {
            Thread.Sleep(50);
            r.Advance(TimeSpan.Zero);
        }
    }

    [Fact]
    public void A_replay_loads_a_chunk_around_the_cursor_plays_at_speed_and_loads_the_next_chunk_on_its_own()
    {
        var dir = Record();
        try
        {
            var r = new DepthReplay(dir, Es, T0.AddMinutes(10)) { Playing = false };
            WaitLoaded(r, () => r.History.Count > 0);
            Assert.True(r.History.Count > 0 && r.Trades.Count > 0, "the chunk around the cursor is loaded");
            Assert.True(r.History.FirstTime >= T0.AddMinutes(10 - 35) && r.History.LastTime <= T0.AddMinutes(10 + 55), "only about an hour and a half is kept");
            Assert.True(r.History.At(r.Cursor) is { HasBid: true });

            r.Playing = true;
            r.Speed = 60;
            r.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(T0.AddMinutes(20), r.Cursor);                          // 10 s at 60x = 10 minutes

            // seeking far away reads another chunk, and the book of that moment has the wall that only exists later
            r.Seek(T0.AddMinutes(150));
            WaitLoaded(r, () => r.History.Count > 0 && r.History.FirstTime > T0.AddMinutes(100));
            var book = r.History.At(r.Cursor)!;
            Assert.Equal(600, book.GetSize(BookSide.Ask, Es.ToTicks(5003m)));

            // playing past the end of the chunk keeps going: the next one is read before the cursor gets there
            r.Seek(T0.AddMinutes(120));
            WaitLoaded(r, () => r.History.FirstTime > T0.AddMinutes(80));
            for (int i = 0; i < 20; i++) { r.Advance(TimeSpan.FromSeconds(1) * 0 + TimeSpan.FromSeconds(1)); Thread.Sleep(40); }
            Assert.True(r.Cursor > T0.AddMinutes(120), "it moved on");
            Assert.Equal(T0, DepthReplay.FirstTime(dir, Es, DateOnly.FromDateTime(T0)));
            Assert.Null(DepthReplay.FirstTime(dir, Es, DateOnly.FromDateTime(T0).AddDays(5)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_heatmap_draws_a_recorded_day_with_its_wall_and_its_trades()
    {
        var dir = Record();
        try
        {
            var data = new TradingHub.Data.InstrumentData(Es, new SimulatedFeed()) { RecordingRoot = dir };
            var model = new TradingHub.Charting.Heatmap.HeatmapModel(data, new TradingHub.Charting.Heatmap.HeatmapSettings { WindowMinutes = 5, ShowBubbles = false, ShowVolume = false, ShowDom = false, ShowCvd = false, ShowIcebergs = false });
            model.StartReplay(T0.AddMinutes(150));
            var rp = model.Replay!;
            rp.Playing = false;
            WaitLoaded(rp, () => rp.History.Count > 0);
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(1000, 600));
            new TradingHub.Charting.Heatmap.HeatmapRenderer().Render(surface.Canvas, model, 1000, 600);
            Assert.Equal(T0.AddMinutes(150), model.EndTime);                       // the right edge is the cursor, not the clock
            using var snap = surface.Snapshot();
            using var image = SkiaSharp.SKBitmap.FromImage(snap);
            int y = (int)(model.YOfTicks(Es.ToTicks(5003m)) + model.TickPx / 2);
            var wall = image.GetPixel((int)model.XOfTime(T0.AddMinutes(148)), y);
            Assert.True(wall.Red > 200 && wall.Blue < 120, $"the recorded wall is hot, it is {wall}");
            model.StopReplay();
            Assert.Null(model.Replay);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class DepthRecordingTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "livetest", TickSize = 0.25m, VolumeStep = 1 };

    /// <summary>A feed that claims to be live and lets the test push depth through the sink the instrument subscribed with.</summary>
    private sealed class LiveFeed : IMarketDataFeed
    {
        public IMarketDataSink? Sink;
        public string Id => "livetest";
        public string DisplayName => "Live test";
        public bool SupportsHistory => false;
        public bool IsLive => true;
        public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Instrument>>(new[] { Es });
        public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId, Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;
        public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
        {
            Sink = sink;
            return new Nop();
        }

        public void Dispose()
        {
        }

        private sealed class Nop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private static int Record(bool recordDepth, string dir)
    {
        var feed = new LiveFeed();
        var data = new InstrumentData(Es, feed) { RecordingRoot = dir, RecordLive = true, RecordDepth = recordDepth, RecordDepthLevels = 10 };
        data.Start(DateTime.UtcNow.AddMinutes(-5));
        for (int i = 0; i < 5; i++)
        {
            var b = new OrderBookBuilder();
            for (int k = 0; k < 15; k++)
            {
                b.Set(BookSide.Bid, Es.ToTicks(5000m) - k, 10 + i + k);
                b.Set(BookSide.Ask, Es.ToTicks(5000m) + 1 + k, 20 + i + k);
            }

            feed.Sink!.OnDepth(Es, b.BuildUpdate(DateTime.UtcNow, 50, true));
            data.Pump();
            Thread.Sleep(300); // the recorder keeps one snapshot per 250 ms
        }

        data.Dispose(); // stops the recorder, which flushes and closes the files
        return DepthFile.AvailableDays(dir, "livetest", "ES").SelectMany(d => DepthFile.Read(DepthFile.DayPath(dir, "livetest", "ES", d))).Count();
    }

    [Fact]
    public void An_instrument_on_a_live_feed_records_its_book_only_when_the_option_is_on_and_keeps_the_configured_levels()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rec-" + Guid.NewGuid().ToString("N"));
        var off = Path.Combine(Path.GetTempPath(), "rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.InRange(Record(true, dir), 4, 5);
            var first = DepthFile.AvailableDays(dir, "livetest", "ES").SelectMany(d => DepthFile.Read(DepthFile.DayPath(dir, "livetest", "ES", d))).First();
            Assert.Equal(10, first.Book.Bids.Length);                       // 15 offered, 10 configured
            Assert.Equal(Es.ToTicks(5000m), first.Book.Bids[0].PriceTicks);

            Assert.Equal(0, Record(false, off));                           // option off: no .vdk at all
            Assert.Empty(DepthFile.AvailableDays(off, "livetest", "ES"));
        }
        finally
        {
            foreach (var d in new[] { dir, off }) if (Directory.Exists(d)) Directory.Delete(d, true);
        }
    }
}
