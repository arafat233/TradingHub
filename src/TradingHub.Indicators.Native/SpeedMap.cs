using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// A thin strip beside the price axis that shows, for each price row, how much the market traded there over the last several sessions:
/// thin rows (red) are where price has tended to cross quickly, thick rows (green) where it has tended to linger. On gold, price crossed
/// low volume zones faster than high volume ones over 13 months (see the Auction claims test); the strip shows where those zones lie ahead.
/// It describes the past, and the effect is a statement about the average, not about the next move.
/// </summary>
[DisplayName("Speed map (fast and slow price zones)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class SpeedMap : Indicator
{
    public enum MapBasis
    {
        [Display(Name = "Volume at price")] Volume,
        [Display(Name = "Time at price")] Time,
    }

    private Dictionary<decimal, decimal> _rows = new();
    private decimal _lowCut, _highCut, _median;
    private int _used;
    private DateTime _at = DateTime.MinValue;
    private readonly HoverTip _tip = new();

    public SpeedMap() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Based on", GroupName = "Calculation", Order = 5, Description = "Volume at price is what the research tested. Time at price needs no footprint data.")]
    public MapBasis Basis { get; set; } = MapBasis.Volume;

    [Display(Name = "Sessions", GroupName = "Calculation", Order = 10)]
    [Range(1, 60)]
    public int Sessions { get; set; } = 10;

    [Display(Name = "Ticks per row", GroupName = "Calculation", Order = 20)]
    [Range(1, 1000)]
    public int TicksPerRow { get; set; } = 4;

    [Display(Name = "Fast below (percentile)", GroupName = "Calculation", Order = 30)]
    [Range(1, 49)]
    public int FastPercentile { get; set; } = 25;

    [Display(Name = "Slow above (percentile)", GroupName = "Calculation", Order = 40)]
    [Range(51, 99)]
    public int SlowPercentile { get; set; } = 75;

    [Display(Name = "Width (px)", GroupName = "Display", Order = 50)]
    [Range(3, 40)]
    public int Width { get; set; } = 9;

    protected override void OnRecalculate()
    {
        _rows = new();
        _at = DateTime.MinValue;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar != CurrentBar - 1 || DateTime.UtcNow - _at < TimeSpan.FromSeconds(8)) return;
        _at = DateTime.UtcNow;
        int start = bar, seen = 1;
        while (start > 0 && seen < Sessions)
        {
            if (IsNewSession(start)) seen++;
            start--;
        }

        _used = seen;
        decimal size = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m) * TicksPerRow;
        double minutes = CurrentBar > 1 ? Math.Max(1, (GetCandle(1).Time - GetCandle(0).Time).TotalMinutes) : 1;
        var map = new Dictionary<decimal, decimal>();
        for (int i = start; i <= bar; i++)
        {
            var c = GetCandle(i);
            if (Basis == MapBasis.Volume)
                foreach (var l in c.GetAllPriceLevels())
                {
                    decimal row = Math.Floor(l.Price / size) * size;
                    map[row] = map.GetValueOrDefault(row) + l.Bid + l.Ask;
                }
            else
                for (decimal row = Math.Floor(c.Low / size) * size; row <= Math.Floor(c.High / size) * size; row += size)
                    map[row] = map.GetValueOrDefault(row) + (decimal)minutes;
        }

        if (map.Count < 8) return;
        // fill the holes between the extremes: an empty row is the fastest kind
        for (decimal r = map.Keys.Min(); r <= map.Keys.Max(); r += size) if (!map.ContainsKey(r)) map[r] = 0;
        var sorted = map.Values.OrderBy(v => v).ToList();
        _lowCut = sorted[sorted.Count * FastPercentile / 100];
        _highCut = sorted[Math.Min(sorted.Count - 1, sorted.Count * SlowPercentile / 100)];
        _median = sorted[sorted.Count / 2];
        _rows = map;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _rows.Count == 0) return;
        _tip.Clear();
        int right = ChartInfo.GetXByBar(LastVisibleBarNumber, true) + ChartInfo.PriceChartContainer.BarsWidth;
        decimal size = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m) * TicksPerRow;
        string unit = Basis == MapBasis.Volume ? "volume" : "minutes";
        foreach (var (row, amount) in _rows)
        {
            int top = ChartInfo.GetYByPrice(row + size, true), bottom = ChartInfo.GetYByPrice(row, true);
            var col = amount <= _lowCut ? Color.FromArgb(210, 232, 70, 70) : amount >= _highCut ? Color.FromArgb(210, 60, 190, 120) : Color.FromArgb(90, 150, 150, 160);
            var rect = new Rectangle(right - Width, Math.Min(top, bottom), Width, Math.Max(2, Math.Abs(bottom - top) - 1));
            context.FillRectangle(col, rect);
            string kind = amount <= _lowCut ? "fast zone: price has tended to cross it quickly" : amount >= _highCut ? "slow zone: price has tended to linger here" : "in between";
            _tip.Add(rect, col, $"{row} - {row + size}", kind, $"{unit} over the last {_used} sessions: {amount:0.##} (median row {_median:0.##})");
        }

        _tip.Draw(context, right);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }
}
