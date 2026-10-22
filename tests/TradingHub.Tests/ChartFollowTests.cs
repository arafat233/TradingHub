using SkiaSharp;
using System.Text.RegularExpressions;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>The chart keeps the last price in the middle while it follows the live edge, and shows the time left in the current candle.</summary>
public class ChartFollowTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTC", FeedId = "test", TickSize = 1m, VolumeStep = 1 };
    private static readonly DateTime T0 = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>120 one-minute candles around 100..106 (the last price is 100) plus the extra trades, then the chart is built.</summary>
    private static (ChartModel Model, ChartRenderer Renderer, ChartRenderState State) Chart(Action<ChartSettings>? tweak = null, params (int Minute, decimal Price)[] extra)
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        long id = 1;
        for (int i = 0; i < 120; i++) data.Trades.Add(new Trade(id++, T0.AddMinutes(i), 100 + (i % 7), 1, TradeSide.Buy));
        foreach (var (minute, price) in extra) data.Trades.Add(new Trade(id++, T0.AddMinutes(minute), price, 1, TradeSide.Buy));
        var settings = new ChartSettings();
        tweak?.Invoke(settings);
        var model = new ChartModel(data, ChartPeriod.M1, settings);
        model.Update();
        return (model, new ChartRenderer(), new ChartRenderState());
    }

    private static ChartLayout Draw(ChartModel model, ChartRenderer r, ChartRenderState st)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1000, 600));
        return r.Render(surface.Canvas, model, 1000, 600, st);
    }

    private static double Middle(ChartLayout l) => (l.Main.Top + l.Main.Bottom) / 2;

    [Fact]
    public void The_scale_re_centres_on_the_last_price_as_it_goes_up_and_down_and_still_shows_every_visible_candle()
    {
        var (model, r, st) = Chart();
        var l = Draw(model, r, st);
        Assert.InRange(Middle(l), 99.5, 100.5);                          // the candles span 100..106 but the last price is 100: the middle is 100, not 103

        // the price runs up: the chart moves with it, the price stays in the middle
        var (up, r2, s2) = Chart(null, (121, 140m));
        l = Draw(up, r2, s2);
        Assert.InRange(Math.Abs(Middle(l) - 140), 0, 0.5);
        Assert.True(l.Main.Bottom < 100 && l.Main.Top > 140, $"every visible candle still fits ({l.Main.Bottom}..{l.Main.Top})");

        // and drops below everything
        var (down, r3, s3) = Chart(null, (121, 140m), (122, 70m));
        l = Draw(down, r3, s3);
        Assert.InRange(Math.Abs(Middle(l) - 70), 0, 0.5);
        Assert.True(l.Main.Top > 140 && l.Main.Bottom < 70);
    }

    [Fact]
    public void With_it_off_scrolled_back_or_with_a_manual_scale_the_price_is_not_forced_to_the_middle()
    {
        var (model, r, st) = Chart(s => s.CenterOnPrice = false, (121, 140m));
        var l = Draw(model, r, st);
        Assert.True(Math.Abs(Middle(l) - 140) > 5, "off: the scale just fits the candles, the price is at the top");

        var (m2, r2, s2) = Chart(null, (121, 140m));
        m2.Viewport.ScrollBars = 60;                                       // looking at the past: the last candle is not even on the screen
        l = Draw(m2, r2, s2);
        Assert.True(Math.Abs(Middle(l) - 140) > 5);

        var (m3, r3, s3) = Chart(null, (121, 99m));
        var before = Draw(m3, r3, s3);
        m3.Viewport.AutoScale = false;                                     // dragged by hand: the user's scale wins
        m3.Viewport.PriceTop = before.Main.Top + 30;
        m3.Viewport.PriceBottom = before.Main.Bottom + 30;
        l = Draw(m3, r3, s3);
        Assert.Equal(before.Main.Top + 30, l.Main.Top, 6);
    }

    [Fact]
    public void The_countdown_is_the_time_left_in_the_candle_and_only_for_time_candles()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        var now = data.Feed.Now;
        data.Trades.Add(new Trade(1, now.AddSeconds(-20), 100, 1, TradeSide.Buy));
        var m5 = new ChartModel(data, ChartPeriod.M5, new ChartSettings());
        m5.Update();
        var text = ChartRenderer.Countdown(m5);
        Assert.NotNull(text);
        Assert.Matches(new Regex(@"^0[0-4]:[0-5]\d$"), text);              // between 00:00 and 04:59
        var m1 = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        m1.Update();
        Assert.Matches(new Regex(@"^00:[0-5]\d$"), ChartRenderer.Countdown(m1)!);
        var h1 = new ChartModel(data, ChartPeriod.H1, new ChartSettings());
        h1.Update();
        Assert.Matches(new Regex(@"^(\d:)?[0-5]\d:[0-5]\d$"), ChartRenderer.Countdown(h1)!);
        var range = new ChartModel(data, new ChartPeriod(PeriodType.Range, 10), new ChartSettings());
        range.Update();
        Assert.Null(ChartRenderer.Countdown(range));                       // ticks, range, volume… have no clock
    }

    [Fact]
    public void The_countdown_is_drawn_under_the_last_price_on_the_axis_and_can_be_switched_off()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        var now = data.Feed.Now;
        for (int i = 0; i < 60; i++) data.Trades.Add(new Trade(i + 1, now.AddMinutes(i - 60).AddSeconds(5), 100 + (i % 5), 1, TradeSide.Buy));
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings { ShowCountdown = true });
        model.Update();
        var r = new ChartRenderer();
        var st = new ChartRenderState();
        using var surface = SKSurface.Create(new SKImageInfo(1000, 600));
        var l = r.Render(surface.Canvas, model, 1000, 600, st);
        using var on = SKBitmap.FromImage(surface.Snapshot());
        float y = l.Main.Y(model.Series.TicksToPrice(model.Series[^1].CloseTicks));
        model.Settings.ShowCountdown = false;
        r.Render(surface.Canvas, model, 1000, 600, st);
        using var off = SKBitmap.FromImage(surface.Snapshot());

        int diff = 0;
        for (int x = (int)l.PriceAxis.Left + 2; x < 998; x++)
            for (int yy = (int)y + 8; yy < (int)y + 28; yy++)
                if (on.GetPixel(x, yy) != off.GetPixel(x, yy)) diff++;
        Assert.True(diff > 150, $"a tag appears just under the price tag ({diff} pixels differ)");
    }
}
