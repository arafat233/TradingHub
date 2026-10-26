using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// A label in the corner of the chart saying whether the market has been ranging, mixed or trending over the last few hours,
/// from the efficiency ratio of the closes (net move divided by the distance walked). Below 0.15 is a range, above 0.35 a trend.
/// </summary>
[DisplayName("Regime light (range / trend)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class RegimeLight : Indicator
{
    private double _efficiency;
    private int _bars;
    private RegimeKind _kind = RegimeKind.Mixed;

    public RegimeLight() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Look back (hours)", GroupName = "Calculation", Order = 10)]
    [Range(1, 48)]
    public int Hours { get; set; } = 3;

    [Display(Name = "Range color", GroupName = "Display", Order = 20)]
    public Color RangeColor { get; set; } = Color.FromArgb(255, 90, 170, 230);

    [Display(Name = "Mixed color", GroupName = "Display", Order = 30)]
    public Color MixedColor { get; set; } = Color.FromArgb(255, 170, 170, 170);

    [Display(Name = "Trend color", GroupName = "Display", Order = 40)]
    public Color TrendColor { get; set; } = Color.FromArgb(255, 245, 165, 60);

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar != CurrentBar - 1 || CurrentBar < 10) return;
        var end = GetCandle(bar).Time;
        int from = bar;
        while (from > 0 && end - GetCandle(from - 1).Time <= TimeSpan.FromHours(Hours)) from--;
        _bars = bar - from + 1;
        if (_bars < 10) return;
        decimal path = 0;
        for (int i = from + 1; i <= bar; i++) path += Math.Abs(GetCandle(i).Close - GetCandle(i - 1).Close);
        _efficiency = path <= 0 ? 0 : (double)(Math.Abs(GetCandle(bar).Close - GetCandle(from).Close) / path);
        _kind = Regime.KindOf(_efficiency);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _bars < 10) return;
        var col = _kind switch { RegimeKind.Range => RangeColor, RegimeKind.Trend => TrendColor, _ => MixedColor };
        var font = new RenderFont("Arial", 10, FontStyle.Bold);
        string name = _kind switch { RegimeKind.Range => "RANGE", RegimeKind.Trend => "TREND", _ => "MIXED" };
        context.FillEllipse(col, new Rectangle(70, 62, 12, 12));
        context.DrawString($"{name}  efficiency {_efficiency:0.00} over {Hours} h ({_bars} candles)", font, col, 88, 59);
    }
}
