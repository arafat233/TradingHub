using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Indicators;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Long/short position, compatibility of stored drawings and the TradingView-style legend.</summary>
public class DrawingAndLegendTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m };
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private static CandleSeries Series(params (decimal High, decimal Low)[] bars)
    {
        var store = new TradeStore(Btc);
        long id = 1;
        for (int i = 0; i < bars.Length; i++)
        {
            store.Add(new Trade(id++, T0.AddMinutes(i), bars[i].High, 1, TradeSide.Buy));
            store.Add(new Trade(id++, T0.AddMinutes(i).AddSeconds(30), bars[i].Low, 1, TradeSide.Sell));
        }

        var s = new CandleSeries(Btc, ChartPeriod.M1);
        s.Rebuild(store);
        return s;
    }

    [Fact]
    public void The_long_position_starts_1_to_2_and_each_handler_moves_its_own_part()
    {
        var d = new ChartDrawing { Kind = DrawingKind.LongPosition, Points = { new ChartPoint(T0, 100) } };
        d.InitPosition(5, T0.AddMinutes(30));
        Assert.Equal(110, d.Points[1].Price); // target 2R
        Assert.Equal(95, d.Points[2].Price);  // stop 1R

        d.MoveHandle(1, new ChartPoint(T0.AddMinutes(99), 120)); // target: the price only
        Assert.Equal(120, d.Points[1].Price);
        Assert.Equal(T0.AddMinutes(30), d.Points[1].Time);
        d.MoveHandle(3, new ChartPoint(T0.AddMinutes(45), 1)); // length: only until when
        Assert.Equal(T0.AddMinutes(45), d.Points[1].Time);
        Assert.Equal(T0.AddMinutes(45), d.Points[2].Time);
        Assert.Equal(95, d.Points[2].Price);
    }

    [Fact]
    public void The_position_marks_whether_it_hit_the_target_or_the_stop()
    {
        var series = Series((101, 99), (104, 100), (111, 103), (112, 90));
        var d = new ChartDrawing { Kind = DrawingKind.LongPosition, Points = { new ChartPoint(T0, 100) } };
        d.InitPosition(5, T0.AddMinutes(10));
        Assert.Equal(1, d.PositionOutcome(series).Outcome); // candle 3 reaches 111 before the stop

        var shortPos = new ChartDrawing { Kind = DrawingKind.ShortPosition, Points = { new ChartPoint(T0, 100) } };
        shortPos.InitPosition(3, T0.AddMinutes(10)); // stop 103, target 94
        Assert.Equal(-1, shortPos.PositionOutcome(series).Outcome); // candle 2 reaches 104: stop

        // in the same candle it touches both: the worst case is taken (the stop)
        var both = Series((100, 100), (120, 80));
        var d2 = new ChartDrawing { Kind = DrawingKind.LongPosition, Points = { new ChartPoint(T0, 100) } };
        d2.InitPosition(5, T0.AddMinutes(10));
        Assert.Equal(-1, d2.PositionOutcome(both).Outcome);

        // just placed on the last candle: that candle's range (from before entering) does not count
        var fresh = Series((101, 99), (103, 90));
        var d3 = new ChartDrawing { Kind = DrawingKind.LongPosition, Points = { new ChartPoint(T0.AddMinutes(1).AddSeconds(40), 100) } };
        d3.InitPosition(5, T0.AddMinutes(20));
        Assert.Equal(0, d3.PositionOutcome(fresh).Outcome);
    }

    [Fact]
    public void The_old_drawings_are_still_read_the_same()
    {
        const string json = """[{"Kind":0,"Points":[{"Time":"2026-10-01T12:00:00Z","Price":100}],"ColorArgb":4294049850,"Width":1.5,"Dashed":true,"Text":"alerta","Locked":false}]""";
        var list = System.Text.Json.JsonSerializer.Deserialize<List<ChartDrawing>>(json)!;
        var d = Assert.Single(list);
        Assert.Equal(LineStyleKind.Dashed, d.EffectiveLineStyle);
        Assert.True(d.IsAlert);
        Assert.True(d.ShowPriceLabel);
        Assert.Null(d.OnlyPeriod);

        // and the new part survives saving and reading back
        d.LineStyle = LineStyleKind.Dotted;
        d.OnlyPeriod = "M5";
        d.Levels = new() { new FibLevel { Value = 0.5, ColorArgb = 0xFFFF0000 } };
        var back = System.Text.Json.JsonSerializer.Deserialize<List<ChartDrawing>>(System.Text.Json.JsonSerializer.Serialize(list))!.Single();
        Assert.Equal(LineStyleKind.Dotted, back.EffectiveLineStyle);
        Assert.False(back.IsVisibleOn(ChartPeriod.M1));
        Assert.True(back.IsVisibleOn(ChartPeriod.M5));
        Assert.Equal(0.5, back.EffectiveLevels.Single().Value);
    }

    /// <summary>Minimal indicator for the legend.</summary>
    private sealed class Line : Indicator
    {
        protected override void OnCalculate(int bar, decimal value) => this[bar] = value;
    }

    [Fact]
    public void From_the_legend_it_is_hidden_removed_and_the_names_are_collapsed()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        long id = 1;
        for (int i = 0; i < 120; i++) data.Trades.Add(new Trade(id++, T0.AddMinutes(i), 100 + (i % 7), 1, TradeSide.Buy));
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        var slot = model.AddIndicator(new Line());
        for (int k = 0; k < 3; k++) model.Update();
        var renderer = new ChartRenderer();
        var state = new ChartRenderState();
        var controller = new ChartController(model, state);
        using var surface = SKSurface.Create(new SKImageInfo(900, 500));

        renderer.Render(surface.Canvas, model, 900, 500, state);
        var name = state.Legend.First(h => h.Action == LegendAction.Name && ReferenceEquals(h.Slot, slot));

        // hovering shows eye, settings and remove
        state.MouseX = name.Rect.MidX;
        state.MouseY = name.Rect.MidY;
        renderer.Render(surface.Canvas, model, 900, 500, state);
        var eye = state.Legend.First(h => h.Action == LegendAction.ToggleVisible);
        Assert.Contains(state.Legend, h => h.Action == LegendAction.Settings);
        Assert.Contains(state.Legend, h => h.Action == LegendAction.Remove);

        controller.MouseDown(eye.Rect.MidX, eye.Rect.MidY, PointerButton.Left, 1, InputModifiers.None);
        Assert.False(slot.Visible);

        IndicatorSlot? asked = null;
        controller.IndicatorSettingsRequested += s => asked = s;
        renderer.Render(surface.Canvas, model, 900, 500, state);
        var gear = state.Legend.First(h => h.Action == LegendAction.Settings);
        controller.MouseDown(gear.Rect.MidX, gear.Rect.MidY, PointerButton.Left, 1, InputModifiers.None);
        Assert.Same(slot, asked);

        // the little arrow collapses the names
        var arrow = state.Legend.First(h => h.Action == LegendAction.Collapse);
        controller.MouseDown(arrow.Rect.MidX, arrow.Rect.MidY, PointerButton.Left, 1, InputModifiers.None);
        Assert.False(model.Settings.ShowIndicatorNames);

        // remove (the icons appear with the mouse over the name)
        model.Settings.ShowIndicatorNames = true;
        state.MouseX = name.Rect.MidX;
        state.MouseY = name.Rect.MidY;
        renderer.Render(surface.Canvas, model, 900, 500, state);
        var remove = state.Legend.First(h => h.Action == LegendAction.Remove);
        controller.MouseDown(remove.Rect.MidX, remove.Rect.MidY, PointerButton.Left, 1, InputModifiers.None);
        Assert.DoesNotContain(slot, model.Indicators);
    }

    [Fact]
    public void The_go_to_latest_button_appears_when_scrolled_and_goes_away()
    {
        var data = new InstrumentData(Btc, new SimulatedFeed());
        long id = 1;
        for (int i = 0; i < 400; i++) data.Trades.Add(new Trade(id++, T0.AddMinutes(i), 100 + (i % 9), 1, TradeSide.Buy));
        var model = new ChartModel(data, ChartPeriod.M1, new ChartSettings());
        model.Update();
        var renderer = new ChartRenderer();
        var state = new ChartRenderState();
        var controller = new ChartController(model, state);
        using var surface = SKSurface.Create(new SKImageInfo(900, 500));
        renderer.Render(surface.Canvas, model, 900, 500, state);
        Assert.Null(state.GoLatestButton);

        model.Viewport.ScrollBars = 120; // scrolled back
        renderer.Render(surface.Canvas, model, 900, 500, state);
        var b = Assert.NotNull(state.GoLatestButton);
        controller.MouseDown(b.MidX, b.MidY, PointerButton.Left, 1, InputModifiers.None);
        Assert.Equal(0, model.Viewport.ScrollBars);
    }
}
