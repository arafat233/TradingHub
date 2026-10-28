using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Footprint reading aids: stacked imbalance bars, the summary under each candle and hiding small numbers.</summary>
public class FootprintReadingTests
{
    private static readonly Instrument Inst = new() { Symbol = "T", FeedId = "test", TickSize = 1m, VolumeStep = 1 };
    private static readonly DateTime T0 = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// One candle, prices 100..104. Sells (bid) of 1 lot at every level; buys (ask) of 10 lots at 101, 102 and 103. So the ask at 101, 102 and 103 is
    /// 10 against 1 at the level below: three imbalanced rows in a row (a stack of buyers). 100 and 104 carry only 1 lot.
    /// </summary>
    private static (ChartModel Model, ChartRenderer Renderer) Chart(Action<FootprintLayout> tweak)
    {
        var data = new InstrumentData(Inst, new SimulatedFeed());
        long id = 1;
        int sec = 0;
        void Add(decimal price, decimal vol, TradeSide side) => data.Trades.Add(new Trade(id++, T0.AddSeconds(sec++), price, vol, side));
        for (int p = 100; p <= 104; p++) Add(p, 1, TradeSide.Sell);
        foreach (int p in new[] { 101, 102, 103 }) Add(p, 10, TradeSide.Buy);
        var settings = new ChartSettings { PriceStep = 1 };
        settings.Layout = FootprintLayout.Presets[0].Clone();
        tweak(settings.Layout);
        var model = new ChartModel(data, ChartPeriod.M1, settings);
        model.Update();
        return (model, new ChartRenderer());
    }

    private static (SKBitmap Image, ChartLayout Layout) Draw(ChartModel model, ChartRenderer r)
    {
        var bmp = new SKBitmap(1000, 600);
        using var canvas = new SKCanvas(bmp);
        var layout = r.Render(canvas, model, 1000, 600, new ChartRenderState());
        return (bmp, layout);
    }

    private static bool Close(SKColor a, SKColor b) => Math.Abs(a.Red - b.Red) < 12 && Math.Abs(a.Green - b.Green) < 12 && Math.Abs(a.Blue - b.Blue) < 12;

    /// <summary>x of the right-hand stack bar and the y of a price row's middle.</summary>
    private static (float Bar, float Y102, float Y104, float Left, float Width) Positions(ChartLayout l)
    {
        float slotLeft = l.LeftX(0) + l.BarSpacing / 2f, slotW = (float)(l.BarWidth - l.BarSpacing);
        return (slotLeft + slotW - 7 + 1.5f, l.Main.Y(102.0 + 0.5 - 0.5 + 0.0), l.Main.Y(104.0), slotLeft, slotW);
    }

    [Fact]
    public void Three_imbalanced_rows_in_a_row_get_a_bar_and_fewer_rows_do_not()
    {
        var (model, r) = Chart(_ => { });
        var (img, l) = Draw(model, r);
        var (bar, y102, y104, _, _) = Positions(l);
        var buy = model.Theme.ImbalanceAsk;
        Assert.True(Close(img.GetPixel((int)bar, (int)y102), buy), $"the bar beside the stack: {img.GetPixel((int)bar, (int)y102)} vs {buy}");
        Assert.False(Close(img.GetPixel((int)bar, (int)y104), buy), "104 is outside the stack");

        var (off, r2) = Chart(f => f.ShowStackedImbalances = false);
        var (img2, l2) = Draw(off, r2);
        var p = Positions(l2);
        Assert.False(Close(img2.GetPixel((int)p.Bar, (int)p.Y102), off.Theme.ImbalanceAsk), "switched off: no bar");

        var (long4, r3) = Chart(f => f.StackedImbalanceLength = 4);
        var (img3, l3) = Draw(long4, r3);
        var q = Positions(l3);
        Assert.False(Close(img3.GetPixel((int)q.Bar, (int)q.Y102), long4.Theme.ImbalanceAsk), "a stack of 3 is not enough when 4 are needed");
    }

    [Fact]
    public void The_summary_strip_is_drawn_under_the_candle_and_the_price_range_leaves_room_for_it()
    {
        var (on, r) = Chart(_ => { });
        var (withStrip, l) = Draw(on, r);
        var (off, r2) = Chart(f => f.ShowSummary = false);
        var (without, l2) = Draw(off, r2);
        float x = l.LeftX(0) + (float)(l.BarWidth - l.BarSpacing) / 2f;
        int yTop = (int)(l.Main.Rect.Bottom - 30 /* the strip is 30 px high */ + 4);
        var a = withStrip.GetPixel((int)x, yTop + 3);
        var b = without.GetPixel((int)x, yTop + 3);
        Assert.False(Close(a, b), $"with the strip {a}, without {b}");
        Assert.True(l.Main.Bottom < l2.Main.Bottom, "the bottom of the price range moved down to make room");
    }

    [Fact]
    public void Numbers_of_small_levels_can_be_hidden_but_the_poc_and_imbalanced_levels_keep_theirs()
    {
        var (all, r) = Chart(f => { f.ShowSummary = false; f.ShowStackedImbalances = false; f.AutoDeclutter = false; });
        var (full, l) = Draw(all, r);
        var (cut, r2) = Chart(f => { f.ShowSummary = false; f.ShowStackedImbalances = false; f.AutoDeclutter = false; f.HideNumbersBelow = 0.5m; });
        var (hidden, l2) = Draw(cut, r2);
        var p = Positions(l);

        int Changed(SKBitmap a, SKBitmap b, float y)
        {
            int n = 0;
            for (int px = (int)p.Left; px < (int)(p.Left + p.Width); px++)
                for (int py = (int)(y - 5); py <= (int)(y + 5); py++)
                    if (!Close(a.GetPixel(px, py), b.GetPixel(px, py))) n++;
            return n;
        }

        Assert.True(Changed(full, hidden, p.Y104) > 10, "104 trades 1 lot against a POC of 10: its numbers go when small ones are hidden");
        Assert.Equal(0, Changed(full, hidden, p.Y102));   // an imbalanced row, and the POC: its numbers stay
    }
}
