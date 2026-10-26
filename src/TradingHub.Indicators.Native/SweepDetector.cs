using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Liquidity sweep (stop run): price trades beyond the previous session's high or low by a few ticks, then closes back inside
/// within a few candles. Marked on the candle that closes back inside. Optionally the candle's delta must also have flipped.
/// </summary>
[DisplayName("Liquidity sweeps (previous high / low)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class SweepDetector : Indicator
{
    private sealed record Sweep(int Bar, bool OfHigh, decimal Level, decimal Excess);

    private readonly List<Sweep> _sweeps = new();
    private readonly List<MarkStore.Rec> _pending = new();
    private readonly HoverTip _tip = new();
    private int _done = -1, _sessStart, _prevStart = -1;
    private decimal _pdh, _pdl;
    private int _highBar = -1, _lowBar = -1;
    private decimal _highExcess, _lowExcess;

    public SweepDetector() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Beyond the level by (ticks)", GroupName = "Calculation", Order = 10, Description = "How far past the previous high / low price must trade to count as taking it out.")]
    [Range(1, 500)]
    public int MinTicks { get; set; } = 2;

    [Display(Name = "Back inside within (candles)", GroupName = "Calculation", Order = 20)]
    [Range(1, 50)]
    public int WithinBars { get; set; } = 3;

    [Display(Name = "Delta must flip", GroupName = "Calculation", Order = 30, Description = "The candle that closes back inside must also have delta against the sweep (negative after a high is taken, positive after a low).")]
    public bool RequireDeltaFlip { get; set; }

    [Display(Name = "Alert on a new sweep", GroupName = "Alerts", Order = 40)]
    public bool AlertOnSweep { get; set; } = true;

    [Display(Name = "High sweep color", GroupName = "Display", Order = 50)]
    public Color HighColor { get; set; } = Color.FromArgb(230, 232, 70, 70);

    [Display(Name = "Low sweep color", GroupName = "Display", Order = 60)]
    public Color LowColor { get; set; } = Color.FromArgb(230, 50, 190, 120);

    protected override void OnRecalculate()
    {
        _sweeps.Clear();
        _pending.Clear();
        _done = -1;
        _sessStart = 0;
        _prevStart = -1;
        _pdh = _pdl = 0;
        _highBar = _lowBar = -1;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        // a candle is final once the next one starts
        while (_done < bar - 1) Check(++_done, bar);
        if (_pending.Count > 0 && bar == CurrentBar - 1)
        {
            MarkStore.Merge(InstrumentInfo?.Instrument ?? "", _pending.ToList());
            _pending.Clear();
        }
    }

    private void Check(int i, int liveBar)
    {
        if (i > 0 && IsNewSession(i))
        {
            _prevStart = _sessStart;
            _sessStart = i;
            _highBar = _lowBar = -1;
            _pdh = _pdl = 0;
            foreach (var (name, price) in PrevSession.Levels(GetCandle, _prevStart, _sessStart, 70))
            {
                if (name == "PDH") _pdh = price;
                else if (name == "PDL") _pdl = price;
            }
        }

        if (_pdh == 0 && _pdl == 0) return;
        var c = GetCandle(i);
        decimal tick = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m), reach = MinTicks * tick;

        if (_pdh > 0)
        {
            if (c.High >= _pdh + reach)
            {
                if (_highBar < 0) _highBar = i;
                _highExcess = Math.Max(_highExcess, (c.High - _pdh) / tick);
            }

            if (_highBar >= 0)
            {
                if (c.Close < _pdh && (!RequireDeltaFlip || c.Delta < 0)) Add(i, true, _pdh, _highExcess, liveBar);
                if (i - _highBar >= WithinBars || c.Close < _pdh) { _highBar = -1; _highExcess = 0; }
            }
        }

        if (_pdl > 0)
        {
            if (c.Low <= _pdl - reach)
            {
                if (_lowBar < 0) _lowBar = i;
                _lowExcess = Math.Max(_lowExcess, (_pdl - c.Low) / tick);
            }

            if (_lowBar >= 0)
            {
                if (c.Close > _pdl && (!RequireDeltaFlip || c.Delta > 0)) Add(i, false, _pdl, _lowExcess, liveBar);
                if (i - _lowBar >= WithinBars || c.Close > _pdl) { _lowBar = -1; _lowExcess = 0; }
            }
        }
    }

    private void Add(int bar, bool ofHigh, decimal level, decimal excess, int liveBar)
    {
        _sweeps.Add(new Sweep(bar, ofHigh, level, Math.Round(excess, 1)));
        _pending.Add(new MarkStore.Rec("sweep", GetCandle(bar).Time, level, 0, ofHigh));
        // only a sweep that just closed is news; the ones found while reading the history are not
        if (AlertOnSweep && bar >= liveBar - 2 && bar == CurrentBar - 2)
            AddAlert("alert1", $"Liquidity sweep: price took out the previous {(ofHigh ? "high" : "low")} {level} by {excess:0.#} ticks and closed back inside");
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _sweeps.Count == 0) return;
        _tip.Clear();
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        foreach (var s in _sweeps)
        {
            if (s.Bar < first || s.Bar > last) continue;
            var c = GetCandle(s.Bar);
            int x = ChartInfo.GetXByBar(s.Bar, true) + ChartInfo.PriceChartContainer.BarsWidth / 2;
            int y = ChartInfo.GetYByPrice(s.OfHigh ? c.High : c.Low, false);
            var col = s.OfHigh ? HighColor : LowColor;
            int ly = ChartInfo.GetYByPrice(s.Level, false);
            context.DrawLine(new RenderPen(Color.FromArgb(120, col.R, col.G, col.B), 1f, System.Drawing.Drawing2D.DashStyle.Dot), x - 40, ly, x + 40, ly);
            var tri = s.OfHigh
                ? new[] { new Point(x, y - 3), new Point(x - 6, y - 12), new Point(x + 6, y - 12) }
                : new[] { new Point(x, y + 3), new Point(x - 6, y + 12), new Point(x + 6, y + 12) };
            context.FillPolygon(col, tri);
            context.DrawString("SWEEP", font, col, x - 17, y + (s.OfHigh ? -26 : 12));
            _tip.Add(new Rectangle(x - 18, s.OfHigh ? y - 28 : y, 36, 28), col, s.OfHigh ? "Sweep of the previous high" : "Sweep of the previous low",
                $"level {s.Level}, taken out by {s.Excess:0.#} ticks", "closed back inside", $"{DateTime.SpecifyKind(c.Time, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm}");
        }

        _tip.Draw(context, ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }
}
