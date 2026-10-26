using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Absorption: a closed candle with much more volume than usual whose range is small. The delta says who was
/// absorbed: heavy aggressive buying that did not lift price (red, passive sellers) or heavy selling that
/// did not push it down (green, passive buyers).
/// </summary>
[DisplayName("Absorption")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class Absorption : Indicator
{
    private readonly record struct Mark(int Bar, bool Sellers, decimal Price);

    private readonly List<MarkStore.Rec> _pending = new();
    private readonly HoverTip _tip = new();

    private readonly List<Mark> _marks = new();
    private int _done = -1;

    public Absorption() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Average over (bars)", GroupName = "Calculation", Order = 10)]
    [Range(5, 500)]
    public int Lookback { get; set; } = 30;

    [Display(Name = "Volume at least (x average)", GroupName = "Calculation", Order = 20)]
    [Range(1, 20)]
    public decimal VolumeMultiple { get; set; } = 2m;

    [Display(Name = "Range at most (x average)", GroupName = "Calculation", Order = 30)]
    [Range(0.1, 5)]
    public decimal RangeMultiple { get; set; } = 0.8m;

    [Display(Name = "Minimum delta (% of volume)", GroupName = "Calculation", Order = 40)]
    [Range(0, 100)]
    public int MinDeltaPercent { get; set; } = 10;

    [Display(Name = "Passive sellers color", GroupName = "Display", Order = 50)]
    public Color SellersColor { get; set; } = Color.FromArgb(230, 232, 70, 70);

    [Display(Name = "Passive buyers color", GroupName = "Display", Order = 60)]
    public Color BuyersColor { get; set; } = Color.FromArgb(230, 50, 190, 120);

    protected override void OnRecalculate()
    {
        _marks.Clear();
        _pending.Clear();
        _done = -1;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        while (_done < bar - 1) Check(++_done);
        if (_pending.Count > 0 && bar == CurrentBar - 1)
        {
            MarkStore.Merge(InstrumentInfo?.Instrument ?? "", _pending.ToList());
            _pending.Clear();
        }
    }

    private void Check(int bar)
    {
        if (bar < Lookback) return;
        decimal vol = 0, range = 0;
        for (int i = bar - Lookback; i < bar; i++)
        {
            var p = GetCandle(i);
            vol += p.Volume;
            range += p.High - p.Low;
        }

        vol /= Lookback;
        range /= Lookback;
        var c = GetCandle(bar);
        if (vol <= 0 || c.Volume < VolumeMultiple * vol || c.High - c.Low > RangeMultiple * range) return;
        if (Math.Abs(c.Delta) < c.Volume * MinDeltaPercent / 100m) return;
        bool sellers = c.Delta > 0; // buyers hit the asks and price stayed: someone passive sold into them
        _marks.Add(new Mark(bar, sellers, sellers ? c.High : c.Low));
        _pending.Add(new MarkStore.Rec("absorption", c.Time, sellers ? c.High : c.Low, 0, sellers));
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _marks.Count == 0) return;
        _tip.Clear();
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        foreach (var m in _marks)
        {
            if (m.Bar < first || m.Bar > last) continue;
            int x = ChartInfo.GetXByBar(m.Bar, true) + ChartInfo.PriceChartContainer.BarsWidth / 2;
            int y = ChartInfo.GetYByPrice(m.Price, false);
            var col = m.Sellers ? SellersColor : BuyersColor;
            var tri = m.Sellers
                ? new[] { new Point(x, y - 3), new Point(x - 6, y - 12), new Point(x + 6, y - 12) }
                : new[] { new Point(x, y + 3), new Point(x - 6, y + 12), new Point(x + 6, y + 12) };
            context.FillPolygon(col, tri);
            context.DrawString("ABS", font, col, x - 11, y + (m.Sellers ? -26 : 12));
            var when = DateTime.SpecifyKind(GetCandle(m.Bar).Time, DateTimeKind.Utc).ToLocalTime();
            _tip.Add(new Rectangle(x - 12, m.Sellers ? y - 28 : y, 24, 28), col, m.Sellers ? "Absorption: passive sellers" : "Absorption: passive buyers", m.Sellers ? "heavy buying did not lift price" : "heavy selling did not push price down", $"@ {m.Price}", $"{when:dd/MM HH:mm}");
        }

        _tip.Draw(context, ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }

}
