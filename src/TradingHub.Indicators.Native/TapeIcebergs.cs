using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Iceberg orders read from the tape alone, for feeds with no order book (LSE futures such as GC): many prints at one price, far more
/// volume than a level normally absorbs, and price not trading through it. Red = a resting seller (buyers kept hitting the ask),
/// green = a resting buyer. A heuristic: stops and busy levels look the same, so treat it as context, not a signal.
/// </summary>
[DisplayName("Iceberg orders (from the tape)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class TapeIcebergs : Indicator
{
    private readonly TapeIcebergDetector _det = new() { Keep = TimeSpan.FromDays(3650) };
    private readonly HoverTip _tip = new();
    private bool _requested;

    public TapeIcebergs() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Minimum prints", GroupName = "Calculation", Order = 10, Description = "Trades at the same price in one run before it can count.")]
    [Range(2, 200)]
    public int MinPrints { get => _det.MinPrints; set { _det.MinPrints = Math.Clamp(value, 2, 200); RecalculateValues(); } }

    [Display(Name = "Times a normal level", GroupName = "Calculation", Order = 20, Description = "The run must absorb this many times the median level.")]
    [Range(2, 50)]
    public decimal Multiple { get => (decimal)_det.Ratio; set { _det.Ratio = (double)Math.Clamp(value, 2, 50); RecalculateValues(); } }

    [Display(Name = "Resting seller color", GroupName = "Display", Order = 50)]
    public Color SellerColor { get; set; } = Color.FromArgb(235, 232, 70, 70);

    [Display(Name = "Resting buyer color", GroupName = "Display", Order = 60)]
    public Color BuyerColor { get; set; } = Color.FromArgb(235, 50, 190, 120);

    protected override void OnRecalculate()
    {
        _det.Reset();
        _requested = false;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0 && !_requested && CurrentBar > 0)
        {
            _requested = true;
            RequestForCumulativeTrades(new CumulativeTradesRequest(GetCandle(0).Time));
        }
    }

    private decimal Tick => Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m);

    private void Feed(MarketDataArg t)
    {
        if (t.Direction == TradeDirection.Between) return;
        _det.OnTrade(t.Time, (long)Math.Round(t.Price / Tick), t.Volume, t.Direction == TradeDirection.Buy);
    }

    protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> cumulativeTrades)
    {
        _det.Reset();
        foreach (var c in cumulativeTrades)
            foreach (var t in c.Ticks) Feed(t);
        RedrawChart();
    }

    protected override void OnNewTrade(MarketDataArg trade)
    {
        int before = _det.Events.Count;
        Feed(trade);
        if (_det.Events.Count != before) RedrawChart();
    }

    private int BarOf(DateTime time)
    {
        int lo = 0, hi = CurrentBar - 1, ans = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (GetCandle(mid).Time <= time) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }

        return ans;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _det.Events.Count == 0 || CurrentBar == 0) return;
        _tip.Clear();
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        foreach (var e in _det.Events.ToArray())
        {
            int b0 = BarOf(e.Start), b1 = BarOf(e.Last);
            if (b1 < first || b0 > last) continue;
            var col = e.Ask ? SellerColor : BuyerColor;
            int x0 = ChartInfo.GetXByBar(b0, true), x1 = ChartInfo.GetXByBar(b1, true) + ChartInfo.PriceChartContainer.BarsWidth;
            if (x1 - x0 < 14) x1 = x0 + 14;
            int y = ChartInfo.GetYByPrice(e.Ticks * Tick, false);
            context.DrawLine(new RenderPen(col, 3f), x0, y, x1, y);
            context.DrawString($"ICE {e.Volume:0.#}", font, col, x0, y + (e.Ask ? -14 : 4));
            _tip.Add(new Rectangle(x0, y - 14, Math.Max(40, x1 - x0), 28), col, e.Ask ? "Iceberg: resting seller" : "Iceberg: resting buyer",
                $"price {e.Ticks * Tick}", $"absorbed {e.Volume:0.#} in {(e.Last - e.Start).TotalSeconds:0} s", $"{DateTime.SpecifyKind(e.Start, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm:ss}");
        }

        _tip.Draw(context, ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }
}
