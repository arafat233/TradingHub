using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Stacked diagonal imbalances: three (by default) or more consecutive prices where the ask is
/// several times the bid below it (buy stack) or the bid several times the ask above it (sell stack).
/// The stack is drawn as a zone that runs to the right until price comes back to it.
/// </summary>
[DisplayName("Stacked imbalances")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class StackedImbalance : Indicator
{
    private sealed class Zone
    {
        public int Bar;
        public decimal Lo, Hi;
        public bool Buy;
        public bool Touched;
    }

    private readonly List<Zone> _zones = new();
    private readonly HoverTip _tip = new();
    private readonly List<MarkStore.Rec> _pending = new();
    private int _done = -1; // last bar whose stacks were extracted

    public StackedImbalance() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Imbalance ratio", GroupName = "Calculation", Order = 10)]
    [Range(1.1, 100)]
    public decimal Ratio { get; set; } = 3m;

    [Display(Name = "Minimum volume at the level", GroupName = "Calculation", Order = 20)]
    [Range(0, 100000000)]
    public decimal MinVolume { get; set; } = 1m;

    [Display(Name = "Levels in a stack", GroupName = "Calculation", Order = 30)]
    [Range(2, 20)]
    public int StackSize { get; set; } = 3;

    [Display(Name = "Hide touched zones", GroupName = "Display", Order = 40)]
    public bool HideTouched { get; set; } = true;

    [Display(Name = "Buy color", GroupName = "Display", Order = 50)]
    public Color BuyColor { get; set; } = Color.FromArgb(60, 50, 190, 120);

    [Display(Name = "Sell color", GroupName = "Display", Order = 60)]
    public Color SellColor { get; set; } = Color.FromArgb(60, 232, 70, 70);

    protected override void OnRecalculate()
    {
        _zones.Clear();
        _pending.Clear();
        _done = -1;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // a bar is final once the next one starts
        while (_done < bar - 1) Extract(++_done);
        if (_pending.Count > 0 && bar == CurrentBar - 1)
        {
            MarkStore.Merge(InstrumentInfo?.Instrument ?? "", _pending.ToList());
            _pending.Clear();
        }

        if (bar == 0) return;
        var c = GetCandle(bar);
        foreach (var z in _zones)
            if (!z.Touched && z.Bar < bar && (z.Buy ? c.Low <= z.Hi : c.High >= z.Lo)) z.Touched = true;
    }

    private void Extract(int bar)
    {
        decimal tick = InstrumentInfo?.TickSize > 0 ? InstrumentInfo.TickSize : 1m;
        var rows = GetCandle(bar).GetAllPriceLevels().OrderBy(l => l.Price).ToList();
        var at = rows.ToDictionary(l => l.Price);
        decimal Bid(decimal p) => at.TryGetValue(p, out var l) ? l.Bid : 0;
        decimal Ask(decimal p) => at.TryGetValue(p, out var l) ? l.Ask : 0;

        // each price gets a +1 (ask imbalance), -1 (bid imbalance) or 0; a run of equal non-zero marks is a stack
        int dir = 0, run = 0;
        decimal start = 0, prev = 0;
        void Flush()
        {
            if (run >= StackSize)
            {
                _zones.Add(new Zone { Bar = bar, Lo = start, Hi = prev, Buy = dir > 0 });
                _pending.Add(new MarkStore.Rec("stacked", GetCandle(bar).Time, start, prev, dir > 0));
            }

            run = 0;
        }

        foreach (var l in rows)
        {
            int m = 0;
            if (l.Ask >= MinVolume && l.Ask >= Ratio * Math.Max(Bid(l.Price - tick), 1)) m = 1;
            else if (l.Bid >= MinVolume && l.Bid >= Ratio * Math.Max(Ask(l.Price + tick), 1)) m = -1;
            // the runs must be on neighbouring prices
            if (m != 0 && m == dir && run > 0 && l.Price - prev <= tick) { run++; prev = l.Price; continue; }
            Flush();
            if (m != 0) { dir = m; run = 1; start = prev = l.Price; }
        }

        Flush();
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _zones.Count == 0) return;
        _tip.Clear();
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        int right = ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth;
        foreach (var z in _zones)
        {
            if (z.Bar > last || (HideTouched && z.Touched)) continue;
            int x0 = ChartInfo.GetXByBar(Math.Max(z.Bar, first), true);
            int yTop = ChartInfo.GetYByPrice(z.Hi, false), yBot = ChartInfo.GetYByPrice(z.Lo, false);
            var rect = new Rectangle(x0, Math.Min(yTop, yBot) - 2, Math.Max(1, right - x0), Math.Abs(yBot - yTop) + 4);
            context.FillRectangle(z.Buy ? BuyColor : SellColor, rect);
            var when = GetCandle(z.Bar).Time;
            _tip.Add(rect, z.Buy ? BuyColor : SellColor, z.Buy ? "Buy stack (stacked ask imbalances)" : "Sell stack (stacked bid imbalances)", $"{z.Lo} - {z.Hi}", $"{DateTime.SpecifyKind(when, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm}", z.Touched ? "price has been back" : "not touched yet");
        }

        _tip.Draw(context, right);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }

}
