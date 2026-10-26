using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Delta divergence: price makes a new high (low) over the last N bars but cumulative delta is lower (higher) than at the
/// previous high (low), so the move was not backed by aggressive buying (selling). Marked on the candle that makes the new extreme.
/// </summary>
[DisplayName("Delta divergence (CVD)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class CvdDivergence : Indicator
{
    private readonly List<decimal> _cum = new(), _high = new(), _low = new();
    private readonly List<(int Bar, bool Bearish)> _marks = new();
    private readonly HoverTip _tip = new();
    private readonly List<MarkStore.Rec> _pending = new();
    private int _done = -1;

    public CvdDivergence() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Look back (bars)", GroupName = "Calculation", Order = 10,
        Description = "A new high (low) means higher (lower) than every bar of this many before it.")]
    [Range(5, 300)]
    public int Lookback { get; set; } = 20;

    [Display(Name = "Minimum distance to the previous extreme (bars)", GroupName = "Calculation", Order = 20)]
    [Range(1, 100)]
    public int MinGap { get; set; } = 3;

    [Display(Name = "Bearish color", GroupName = "Display", Order = 30)]
    public Color BearishColor { get; set; } = Color.FromArgb(230, 232, 70, 70);

    [Display(Name = "Bullish color", GroupName = "Display", Order = 40)]
    public Color BullishColor { get; set; } = Color.FromArgb(230, 50, 190, 120);

    protected override void OnRecalculate()
    {
        _cum.Clear();
        _high.Clear();
        _low.Clear();
        _marks.Clear();
        _pending.Clear();
        _done = -1;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // a bar is final once the next one starts
        while (_done < bar - 1)
        {
            var c = GetCandle(++_done);
            _cum.Add((_cum.Count > 0 ? _cum[^1] : 0) + c.Delta);
            _high.Add(c.High);
            _low.Add(c.Low);
            if (Find(_high, _low, _cum, Lookback, MinGap, _done) is { } bearish)
            {
                _marks.Add((_done, bearish));
                _pending.Add(new MarkStore.Rec("divergence", c.Time, bearish ? c.High : c.Low, 0, bearish));
            }
        }

        if (_pending.Count > 0 && bar == CurrentBar - 1)
        {
            MarkStore.Merge(InstrumentInfo?.Instrument ?? "", _pending.ToList());
            _pending.Clear();
        }
    }

    /// <summary>
    /// Divergence at bar <paramref name="i"/>: true = bearish (new high, cumulative delta below the previous high's), false = bullish,
    /// null = none. Public so it can be checked without a chart.
    /// </summary>
    public static bool? Find(IReadOnlyList<decimal> high, IReadOnlyList<decimal> low, IReadOnlyList<decimal> cum, int lookback, int minGap, int i)
    {
        if (i < 1) return null;
        int from = Math.Max(0, i - lookback);
        int hi = from, lo = from;
        for (int k = from; k < i; k++)
        {
            if (high[k] >= high[hi]) hi = k;
            if (low[k] <= low[lo]) lo = k;
        }

        if (high[i] > high[hi] && i - hi >= minGap && cum[i] < cum[hi]) return true;
        if (low[i] < low[lo] && i - lo >= minGap && cum[i] > cum[lo]) return false;
        return null;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _marks.Count == 0) return;
        _tip.Clear();
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        foreach (var (bar, bearish) in _marks)
        {
            if (bar < first || bar > last) continue;
            var c = GetCandle(bar);
            int x = ChartInfo.GetXByBar(bar, true) + ChartInfo.PriceChartContainer.BarsWidth / 2;
            int y = ChartInfo.GetYByPrice(bearish ? c.High : c.Low, false);
            var col = bearish ? BearishColor : BullishColor;
            var tri = bearish
                ? new[] { new Point(x, y - 3), new Point(x - 6, y - 12), new Point(x + 6, y - 12) }
                : new[] { new Point(x, y + 3), new Point(x - 6, y + 12), new Point(x + 6, y + 12) };
            context.FillPolygon(col, tri);
            context.DrawString("DIV", font, col, x - 10, y + (bearish ? -26 : 12));
            _tip.Add(new Rectangle(x - 12, bearish ? y - 28 : y, 24, 28), col, bearish ? "Bearish divergence" : "Bullish divergence", bearish ? "new high, but cumulative delta is lower than at the last high" : "new low, but cumulative delta is higher than at the last low", $"{DateTime.SpecifyKind(c.Time, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm}");
        }

        _tip.Draw(context, ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }

}
