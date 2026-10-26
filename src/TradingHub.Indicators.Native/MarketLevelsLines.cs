using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Draws the levels the market is organised around as dashed lines: for BTC and ETH the options call wall, put wall, max pain and gamma flip (from
/// Deribit's free public open interest), and, on feeds that publish them (Bybit), where most longs and shorts were liquidated in the last six hours.
/// The same levels are listed in the Insights panel. They are positioning, not predictions: price often reacts near them and often does not.
/// </summary>
[DisplayName("Market levels (options walls, liquidations)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class MarketLevelsLines : Indicator
{
    private readonly HoverTip _tip = new();

    public MarketLevelsLines() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Options (walls, max pain, gamma flip)", GroupName = "Show", Order = 10)]
    public bool ShowOptions { get; set; } = true;

    [Display(Name = "Liquidation clusters", GroupName = "Show", Order = 20)]
    public bool ShowLiquidations { get; set; } = true;

    [Display(Name = "Estimated liquidation prices", GroupName = "Show", Order = 25,
        Description = "Where the longs and shorts opened by the biggest orders of the last 6 hours would be liquidated at 10x to 100x leverage (a guess: nobody can see the real leverage).")]
    public bool ShowLiqEstimate { get; set; } = true;

    [Display(Name = "Your levels", GroupName = "Show", Order = 28, Description = "The levels you add in the Insights panel.")]
    public bool ShowMine { get; set; } = true;

    [Display(Name = "Tags on the price axis", GroupName = "Display", Order = 25, Description = "Writes each level's price on the price axis, like a price alert, so it can be read without following the line.")]
    public bool AxisTagsOn { get => _axisTags; set => _axisTags = value; }

    private bool _axisTags = true;

    [Display(Name = "Line opacity", GroupName = "Display", Order = 30)]
    [Range(10, 255)]
    public int Opacity { get; set; } = 170;

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    private List<LevelMark> Marks()
    {
        string symbol = InstrumentInfo?.Instrument ?? "";
        if (OptionsLevelService.CurrencyOfSymbol(symbol) is { } currency && ShowOptions) OptionsLevelService.Request(currency); // at most every five minutes
        return MarketLevels.ForSymbol(symbol).Where(m => m.Source == "options" ? ShowOptions : m.Source == "liq-estimate" ? ShowLiqEstimate : m.Source == MyLevels.Source ? ShowMine : ShowLiquidations).ToList();
    }

    private Color ToneColor(LevelMark m) => m.Tone > 0 ? Color.FromArgb(Opacity, 60, 200, 110) : m.Tone < 0 ? Color.FromArgb(Opacity, 235, 80, 80) : Color.FromArgb(Opacity, 255, 190, 60);

    public override IEnumerable<(decimal Price, string Text, Color Background)> AxisTags()
    {
        if (!_axisTags || InstrumentInfo == null) yield break;
        foreach (var m in Marks())
            yield return (m.Price, Math.Round(m.Price, Math.Min(InstrumentInfo.Digits, 6)).ToString(System.Globalization.CultureInfo.InvariantCulture), ToneColor(m));
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || InstrumentInfo == null) return;
        var marks = Marks();
        if (marks.Count == 0) return;

        _tip.Clear();
        var container = ChartInfo.PriceChartContainer;
        int x0 = ChartInfo.GetXByBar(FirstVisibleBarNumber, true), x1 = container.Region.Right;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        foreach (var m in marks)
        {
            int y = ChartInfo.GetYByPrice(m.Price, false);
            if (y < container.Region.Top || y > container.Region.Bottom) continue; // off the visible prices
            var c = ToneColor(m);
            context.DrawLine(new RenderPen(c, 1.2f, System.Drawing.Drawing2D.DashStyle.Dash), x0, y, x1, y);
            decimal shown = Math.Round(m.Price, Math.Min(InstrumentInfo.Digits, 6));
            context.DrawString($"{m.ChartText} {shown}", font, c, Math.Max(x0 + 4, x1 - 170), y - 13);
            _tip.Add(new Rectangle(x0, y - 2, Math.Max(1, x1 - x0), 5), c, m.Label, $"{shown}", m.Source);
        }

        _tip.Draw(context, x1);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }
}
