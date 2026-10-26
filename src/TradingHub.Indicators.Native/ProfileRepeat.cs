using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Compares today's volume profile with the previous session's and marks the prices that are among the busiest
/// (top share of the price rows by volume) in both: places the market keeps coming back to.
/// </summary>
[DisplayName("Repeated volume (today vs yesterday)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class ProfileRepeat : Indicator
{
    private Dictionary<decimal, decimal> _cur = new(), _prev = new();
    private int _committedTo = -1;
    private List<decimal> _repeat = new();
    private readonly HoverTip _tip = new();

    public ProfileRepeat() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Busiest prices (% of the rows)", GroupName = "Calculation", Order = 10,
        Description = "A price counts when it is among this share of the session's price rows with the most volume, today and in the previous session.")]
    [Range(1, 50)]
    public int TopPercent { get; set; } = 15;

    [Display(Name = "Width (px)", GroupName = "Display", Order = 20)]
    [Range(20, 600)]
    public int Width { get; set; } = 140;

    [Display(Name = "Color", GroupName = "Display", Order = 30)]
    public Color BandColor { get; set; } = Color.FromArgb(110, 255, 196, 0);

    protected override void OnRecalculate()
    {
        _cur = new();
        _prev = new();
        _committedTo = -1;
        _repeat = new();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar > 0 && IsNewSession(bar))
        {
            _prev = _cur;
            _cur = new();
            _committedTo = bar - 1;
        }

        // closed bars are added once; the forming one is added to a copy on every tick
        while (_committedTo < bar - 1) AddTo(_cur, ++_committedTo);
        if (bar != CurrentBar - 1 || _prev.Count == 0) return;
        var live = new Dictionary<decimal, decimal>(_cur);
        AddTo(live, bar);
        var a = Top(live);
        var b = Top(_prev);
        _repeat = a.Intersect(b).OrderBy(p => p).ToList();
    }

    private void AddTo(Dictionary<decimal, decimal> dict, int bar)
    {
        foreach (var l in GetCandle(bar).GetAllPriceLevels())
        {
            dict.TryGetValue(l.Price, out var v);
            dict[l.Price] = v + l.Bid + l.Ask;
        }
    }

    private HashSet<decimal> Top(Dictionary<decimal, decimal> profile)
    {
        int n = Math.Max(1, profile.Count * TopPercent / 100);
        return profile.OrderByDescending(kv => kv.Value).Take(n).Select(kv => kv.Key).ToHashSet();
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _repeat.Count == 0) return;
        _tip.Clear();
        int right = ChartInfo.GetXByBar(LastVisibleBarNumber, true) + ChartInfo.PriceChartContainer.BarsWidth;
        int rowH = Math.Max(2, (int)Math.Round(ChartInfo.PriceChartContainer.PriceRowHeight));
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        foreach (var p in _repeat)
        {
            int y = ChartInfo.GetYByPrice(p, false);
            var band = new Rectangle(right - Width, y - rowH / 2, Width, rowH);
            context.FillRectangle(BandColor, band);
            _tip.Add(band, BandColor, "Repeated volume", "busy price today and in the previous session", $"{p}");
        }

        context.DrawString("repeated volume", font, Color.FromArgb(220, BandColor.R, BandColor.G, BandColor.B), right - Width, ChartInfo.GetYByPrice(_repeat[^1], false) - rowH - 12);
        _tip.Draw(context, right);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }
}
