using SkiaSharp;
using TradingHub.Charting.Heatmap;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

public class HeatmapTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "test", TickSize = 0.25m, VolumeStep = 1 };

    private static DepthSnapshot Book(DateTime t, decimal bid, decimal ask, params (decimal Price, decimal Size)[] extra)
    {
        var b = new OrderBookBuilder();
        b.Set(BookSide.Bid, Es.ToTicks(bid), 5);
        b.Set(BookSide.Ask, Es.ToTicks(ask), 5);
        foreach (var (price, size) in extra) b.Set(price < ask ? BookSide.Bid : BookSide.Ask, Es.ToTicks(price), size);
        return b.BuildUpdate(t, 50, true).Snapshot;
    }

    [Fact]
    public void The_history_keeps_a_snapshot_per_interval_looks_up_the_book_at_a_time_and_drops_what_is_too_old()
    {
        var t0 = new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);
        var h = new DepthHistory { Interval = TimeSpan.FromMilliseconds(250), MaxAge = TimeSpan.FromMinutes(10) };
        for (int i = 0; i < 1000; i++) h.Add(t0.AddMilliseconds(i * 100), Book(t0.AddMilliseconds(i * 100), 5000, 5000.25m)); // 100 s of updates every 100 ms
        Assert.InRange(h.Count, 333, 335);                           // at least 250 ms apart → one every 300 ms

        Assert.Null(h.At(t0.AddSeconds(-5)));                        // before the first sample
        Assert.NotNull(h.At(t0.AddSeconds(99)));
        Assert.True((t0.AddSeconds(50) - h.At(t0.AddSeconds(50))!.Time).Duration() < TimeSpan.FromSeconds(1)); // the book of that moment

        // with a short memory, what is older than it goes (it trims every 200 snapshots, so with a little slack)
        h.MaxAge = TimeSpan.FromSeconds(30);
        for (int i = 0; i < 300; i++) h.Add(t0.AddSeconds(100).AddMilliseconds(i * 300), Book(t0, 5000, 5000.25m));
        Assert.True(h.FirstTime >= h.LastTime - TimeSpan.FromSeconds(30 + 61), $"{h.FirstTime:O} .. {h.LastTime:O}");
        Assert.True(h.Count < 335);

        // an empty book is not worth keeping, and a clock that goes back (a restarted replay) starts the picture over
        int before = h.Count;
        h.Add(h.LastTime.AddSeconds(1), DepthSnapshot.Empty);
        Assert.Equal(before, h.Count);
        h.Add(t0, Book(t0, 5000, 5000.25m));
        Assert.Equal(1, h.Count);
    }

    private static (HeatmapModel Model, SKBitmap Image) Draw(Action<InstrumentData, DateTime> fill, HeatmapSettings? settings = null)
    {
        var data = new InstrumentData(Es, new SimulatedFeed());
        var now = data.Feed.Now;
        fill(data, now);
        var model = new HeatmapModel(data, settings ?? new HeatmapSettings { WindowMinutes = 5, ShowBubbles = false, ShowVolume = false, ShowDom = false });
        using var surface = SKSurface.Create(new SKImageInfo(1000, 600));
        new HeatmapRenderer().Render(surface.Canvas, model, 1000, 600);
        using var snap = surface.Snapshot();
        return (model, SKBitmap.FromImage(snap));
    }

    [Fact]
    public void A_resting_wall_is_hot_at_its_price_and_dark_where_nothing_rested()
    {
        var (model, image) = Draw((data, now) =>
        {
            data.Trades.Add(new Trade(1, now.AddSeconds(-1), 5000m, 1, TradeSide.Buy));   // the last price: it centres the view
            // five minutes of a small book (5 each side), and a 600-lot wall at 5003.00 for the last two minutes
            for (int s = 300; s >= 0; s -= 1)
            {
                var t = now.AddSeconds(-s);
                data.DepthHistory.Add(t, s <= 120 ? Book(t, 4999.75m, 5000.25m, (5003m, 600)) : Book(t, 4999.75m, 5000.25m));
            }
        });

        int xWall = (int)model.XOfTime(model.EndTime.AddSeconds(-60));
        int yWall = (int)(model.YOfTicks(Es.ToTicks(5003m)) + model.TickPx / 2);
        int yEmpty = (int)(model.YOfTicks(Es.ToTicks(5006m)) + model.TickPx / 2);
        var wall = image.GetPixel(xWall, yWall);
        var empty = image.GetPixel(xWall, yEmpty);
        Assert.True(wall.Red > 200 && wall.Blue < 120, $"the wall should be hot (red/orange), it is {wall}");
        Assert.True(empty.Red < 40 && empty.Green < 40, $"an empty price should stay dark, it is {empty}");

        // before the wall appeared (4 minutes ago) the same price is dark
        var before = image.GetPixel((int)model.XOfTime(model.EndTime.AddSeconds(-240)), yWall);
        Assert.True(before.Red < 40 && before.Green < 40, $"before the wall it should be dark, it is {before}");
    }

    [Fact]
    public void Without_a_book_it_says_so_instead_of_drawing_an_empty_chart()
    {
        var (_, image) = Draw((data, now) => data.Trades.Add(new Trade(1, now, 5000m, 1, TradeSide.Buy)));
        // the message is light text on the dark background: some pixel in that band is bright
        bool text = false;
        for (int x = 20; x < 500 && !text; x++)
            for (int y = 150; y < 330 && !text; y++)
                text = image.GetPixel(x, y).Red > 150;
        Assert.True(text);
        Assert.True(HeatmapRenderer.Heat(0).Red < 20 && HeatmapRenderer.Heat(1).Red > 200 && HeatmapRenderer.Heat(0.5f).Blue > 120 && HeatmapRenderer.Heat(0.7f).Red > 200);
    }

    [Fact]
    public void Hovering_draws_a_crosshair_with_the_resting_size_at_that_price()
    {
        var settings = new HeatmapSettings { WindowMinutes = 5, ShowBubbles = false, ShowVolume = false, ShowDom = false };
        var data = new InstrumentData(Es, new SimulatedFeed());
        var now = data.Feed.Now;
        data.Trades.Add(new Trade(1, now.AddSeconds(-1), 5000m, 1, TradeSide.Buy));
        for (int s = 300; s >= 0; s--) data.DepthHistory.Add(now.AddSeconds(-s), Book(now.AddSeconds(-s), 4999.75m, 5000.25m, (5003m, 600)));
        var model = new HeatmapModel(data, settings);
        using var surface = SKSurface.Create(new SKImageInfo(1000, 600));
        var renderer = new HeatmapRenderer();
        renderer.Render(surface.Canvas, model, 1000, 600); // lays out the model
        using (var plain = SurfaceBitmap(surface))
        {
            model.Hover = new SKPoint(model.XOfTime(model.EndTime.AddSeconds(-60)), model.YOfTicks(Es.ToTicks(5003m)) + model.TickPx / 2);
            renderer.Render(surface.Canvas, model, 1000, 600);
            using var hovered = SurfaceBitmap(surface);
            int diff = 0;
            for (int x = 0; x < 1000; x += 2) for (int y = 0; y < 560; y += 2) if (plain.GetPixel(x, y) != hovered.GetPixel(x, y)) diff++;
            Assert.True(diff > 200, $"the crosshair and readout should change the picture, {diff} samples differ");
        }
    }

    private static SKBitmap SurfaceBitmap(SKSurface surface)
    {
        using var snap = surface.Snapshot();
        return SKBitmap.FromImage(snap);
    }

    [Fact]
    public void Pausing_stops_the_picture_at_that_moment_and_resuming_goes_live()
    {
        var data = new InstrumentData(Es, new SimulatedFeed());
        var model = new HeatmapModel(data);
        model.Freeze();
        var frozen = model.FrozenAt!.Value;
        using var surface = SKSurface.Create(new SKImageInfo(800, 500));
        new HeatmapRenderer().Render(surface.Canvas, model, 800, 500);
        Assert.Equal(frozen, model.EndTime);
        model.Resume();
        Assert.Null(model.FrozenAt);
    }

    [Fact]
    public void The_CVD_counts_from_the_session_start_and_keeps_its_place_between_frames()
    {
        var data = new InstrumentData(Es, new SimulatedFeed());
        var now = data.Feed.Now;
        var sessionStart = now.AddHours(-2);
        data.Trades.Add(new Trade(1, now.AddHours(-3), 5000m, 500, TradeSide.Buy));   // before the session: ignored
        data.Trades.Add(new Trade(2, now.AddMinutes(-90), 5000m, 100, TradeSide.Buy));
        data.Trades.Add(new Trade(3, now.AddMinutes(-80), 5000m, 30, TradeSide.Sell));
        var model = new HeatmapModel(data);
        Assert.Equal(70, model.CvdBefore(sessionStart, now.AddMinutes(-5)));

        data.Trades.Add(new Trade(4, now.AddMinutes(-20), 5000m, 10, TradeSide.Buy));
        Assert.Equal(70, model.CvdBefore(sessionStart, now.AddMinutes(-30)));          // the new trade is after that point
        Assert.Equal(80, model.CvdBefore(sessionStart, now.AddMinutes(-5)));           // continues from where it was
        Assert.Equal(0, model.CvdBefore(now.AddMinutes(-1), now.AddMinutes(-5)));       // the window starts before the session
    }

    [Fact]
    public void A_level_that_absorbs_far_more_than_it_shows_is_an_iceberg_and_an_ordinary_fill_is_not()
    {
        var data = new InstrumentData(Es, new SimulatedFeed());
        var now = data.Feed.Now;
        for (int s = 60; s >= 0; s--) data.DepthHistory.Add(now.AddSeconds(-s), Book(now.AddSeconds(-s), 4999.75m, 5000.25m, (5003m, 600)));
        // buyers hit the 5 lots shown at 5000.25 twenty times for 5 lots each (100 lots in all): it keeps refilling
        for (int i = 0; i < 20; i++) data.Trades.Add(new Trade(i + 1, now.AddSeconds(-50 + i), 5000.25m, 5, TradeSide.Buy));
        // 100 lots against the 600 lot wall is just a big order eating a big level
        for (int i = 0; i < 20; i++) data.Trades.Add(new Trade(100 + i, now.AddSeconds(-30 + i), 5003m, 5, TradeSide.Buy));
        // a single small fill of the displayed size
        data.Trades.Add(new Trade(200, now.AddSeconds(-5), 4999.75m, 3, TradeSide.Sell));

        var d = new IcebergDetector();
        d.Update(data.DepthHistory, data.Trades, 0);
        var ice = Assert.Single(d.Events);
        Assert.True(ice.Ask);
        Assert.Equal(Es.ToTicks(5000.25m), ice.Ticks);
        Assert.Equal(100, ice.Volume);

        d.Update(data.DepthHistory, data.Trades, 0); // nothing new: it does not count the same trades twice
        Assert.Single(d.Events);
    }
}

public class HeatmapPanelTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "test", TickSize = 0.25m, VolumeStep = 1 };

    private static DepthSnapshot Book(DateTime t, decimal bid, decimal ask, params (decimal Price, decimal Size)[] extra)
    {
        var b = new OrderBookBuilder();
        b.Set(BookSide.Bid, Es.ToTicks(bid), 5);
        b.Set(BookSide.Ask, Es.ToTicks(ask), 5);
        foreach (var (price, size) in extra) b.Set(price < ask ? BookSide.Bid : BookSide.Ask, Es.ToTicks(price), size);
        return b.BuildUpdate(t, 50, true).Snapshot;
    }

    [Fact]
    public void CQC_is_the_change_of_resting_size_per_price_between_two_books()
    {
        var t = DateTime.UtcNow;
        var start = Book(t, 4999.75m, 5000.25m, (4999.5m, 10), (5001m, 20));
        var end = Book(t, 4999.75m, 5000.25m, (4999.5m, 25), (5002m, 7));       // 4999.5 grew by 15, 5001 was pulled, 5002 appeared, the best levels did not change
        var cqc = HeatmapRenderer.ComputeCqc(start, end);
        Assert.Equal(15, cqc[Es.ToTicks(4999.5m)]);
        Assert.Equal(-20, cqc[Es.ToTicks(5001m)]);
        Assert.Equal(7, cqc[Es.ToTicks(5002m)]);
        Assert.False(cqc.ContainsKey(Es.ToTicks(5000.25m)));                    // unchanged: not listed
        Assert.Empty(HeatmapRenderer.ComputeCqc(null, end));
    }

    [Fact]
    public void The_session_profile_adds_up_buy_and_sell_per_price_incrementally_and_ignores_the_future()
    {
        var store = new TradeStore(Es);
        var t0 = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
        store.Add(new Trade(1, t0.AddHours(-12), 5000m, 99, TradeSide.Buy));        // the day before (session starts 00:00 UTC)
        store.Add(new Trade(2, t0, 5000m, 4, TradeSide.Buy));
        store.Add(new Trade(3, t0.AddSeconds(1), 5000m, 1, TradeSide.Sell));
        store.Add(new Trade(4, t0.AddSeconds(2), 5000.25m, 3, TradeSide.Sell));
        store.Add(new Trade(5, t0.AddMinutes(5), 5000m, 50, TradeSide.Buy));          // after the cursor
        var p = new SessionProfile();
        var prof = p.Update(store, SessionSettings.CryptoUtc, t0.AddSeconds(3));
        Assert.Equal((4m, 1m), prof[Es.ToTicks(5000m)]);
        Assert.Equal((0m, 3m), prof[Es.ToTicks(5000.25m)]);
        prof = p.Update(store, SessionSettings.CryptoUtc, t0.AddMinutes(6));          // moving on only reads the new trade
        Assert.Equal((54m, 1m), prof[Es.ToTicks(5000m)]);
        prof = p.Update(store, SessionSettings.CryptoUtc, t0.AddSeconds(3));          // going back starts over
        Assert.Equal((4m, 1m), prof[Es.ToTicks(5000m)]);
    }

    /// <summary>One scene drawn several times with different settings: the same data and a frozen clock, so only the settings change the picture.</summary>
    private sealed class Scene
    {
        public readonly HeatmapModel Model;

        private static HeatmapSettings Base() => new() { WindowMinutes = 2, RowPx = 13, ShowBubbles = false, ShowIcebergs = false, ShowCvd = true, ShowDom = true, ShowStats = false, ShowGauges = false, ShowBlocks = false };

        public Scene()
        {
            var data = new InstrumentData(Es, new SimulatedFeed());
            Market(data, data.Feed.Now);
            Model = new HeatmapModel(data, Base());
            Model.Freeze();
        }

        public SKBitmap Draw(Action<HeatmapSettings>? tweak = null)
        {
            var settings = Base();
            tweak?.Invoke(settings);
            Model.Settings = settings;
            using var surface = SKSurface.Create(new SKImageInfo(1300, 700));
            new HeatmapRenderer().Render(surface.Canvas, Model, 1300, 700);
            using var snap = surface.Snapshot();
            return SKBitmap.FromImage(snap);
        }
    }

    private static void Market(InstrumentData data, DateTime now)
    {
        // two minutes: a small book and a 600-lot wall at 5003 that never changes; 5002 has a size that keeps changing
        for (int s = 130; s >= 0; s--)
        {
            var t = now.AddSeconds(-s);
            data.DepthHistory.Add(t, Book(t, 4999.75m, 5000.25m, (5003m, 600), (5002m, 100 + s % 37)));
            if (s % 2 == 0) data.Trades.Add(new Trade(130 - s + 1, t, 5000m + (s % 3) * 0.25m, 2, s % 4 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }
    }

    private static int Diff(SKBitmap a, SKBitmap b, SKRectI area)
    {
        int n = 0;
        for (int x = area.Left; x < area.Right; x++)
            for (int y = area.Top; y < area.Bottom; y++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) n++;
        return n;
    }

    [Fact]
    public void Each_panel_draws_where_it_should_and_can_be_switched_off()
    {
        var scene = new Scene();
        using var plain = scene.Draw();
        using var again = scene.Draw();
        Assert.Equal(0, Diff(plain, again, new SKRectI(0, 0, 1300, 700)));        // the scene is deterministic: any difference below comes from the setting
        // layout at 1300x700: the heat area is 938 x 442, the ladder to its right, the dials under the ladder
        using (var stats = scene.Draw(s => s.ShowStats = true)) Assert.True(Diff(plain, stats, new SKRectI(8, 300, 190, 435)) > 1500, "the corner boxes appear");
        using (var gauges = scene.Draw(s => s.ShowGauges = true)) Assert.True(Diff(plain, gauges, new SKRectI(945, 450, 1295, 670)) > 1500, "the dials appear under the ladder");
        using (var blocks = scene.Draw(s => s.ShowBlocks = true)) Assert.True(Diff(plain, blocks, new SKRectI(0, 0, 938, 440)) > 800, "the 600 lot wall that never moved gets a block");
        using (var noDom = scene.Draw(s => s.ShowDom = false)) Assert.True(Diff(plain, noDom, new SKRectI(1000, 0, 1300, 440)) > 5000, "without the book the ladder is gone");
    }

    [Fact]
    public void A_block_only_covers_a_big_order_that_stood_still_not_one_that_kept_changing()
    {
        var scene = new Scene();
        using var off = scene.Draw();
        using var on = scene.Draw(s => s.ShowBlocks = true);
        var m = scene.Model;
        int yWall = (int)(m.YOfTicks(Es.ToTicks(5003m)) + m.TickPx / 2), ySwing = (int)(m.YOfTicks(Es.ToTicks(5002m)) + m.TickPx / 2);
        int x = (int)m.XOfTime(m.EndTime.AddSeconds(-60));
        var wallOff = off.GetPixel(x, yWall);
        var wallOn = on.GetPixel(x, yWall);
        Assert.True(wallOn.Green > wallOff.Green + 15, $"the wall is covered by a pale block: {wallOff} -> {wallOn}");
        Assert.Equal(off.GetPixel(x, ySwing), on.GetPixel(x, ySwing));
    }
}
