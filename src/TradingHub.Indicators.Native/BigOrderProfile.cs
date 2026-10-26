using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Text.Json;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// A volume-profile-style histogram at the right edge, but only of big orders and over several days: for each price band the
/// size of the buy and sell orders the Big Trades indicator has remembered. It shows the prices big orders keep coming to.
/// It reads the Big Trades save file, so that indicator has to have been on a chart of the instrument.
/// </summary>
[DisplayName("Big-order profile (days)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class BigOrderProfile : Indicator
{
    private sealed record Row(DateTime Time, decimal Price, decimal Volume, bool Buy, bool Touched = false, bool Deleted = false);

    private sealed record Band(decimal Lo, decimal Hi, decimal Buy, decimal Sell, int Count);

    private List<Band> _bands = new(), _visible = new();
    private List<Row> _rows = new();
    private DateTime _visibleFrom;
    private DateTime _loadedAt = DateTime.MinValue;
    private readonly HoverTip _tip = new();

    public BigOrderProfile() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Days", GroupName = "Calculation", Order = 10)]
    [Range(1, 365)]
    public int Days { get; set; } = 5;

    [Display(Name = "Band height (ticks)", GroupName = "Calculation", Order = 20, Description = "Orders within this many ticks are added together.")]
    [Range(1, 1000)]
    public int BandTicks { get; set; } = 20;

    [Display(Name = "Only levels not touched yet", GroupName = "Calculation", Order = 30)]
    public bool OnlyUntouched { get; set; }

    [Display(Name = "Second column: only what is on the chart", GroupName = "Calculation", Order = 35,
        Description = "Adds a second column to the left of the first with the big orders of the candles visible now (it follows scrolling and zooming), so the prices whales used today can be told from the ones of the last days.")]
    public bool VisibleColumn { get; set; }

    [Display(Name = "Maximum width (px)", GroupName = "Display", Order = 40)]
    [Range(30, 800)]
    public int MaxWidth { get; set; } = 90;

    [Display(Name = "Buy color", GroupName = "Display", Order = 50)]
    public Color BuyColor { get; set; } = Color.FromArgb(150, 50, 140, 230);

    [Display(Name = "Sell color", GroupName = "Display", Order = 60)]
    public Color SellColor { get; set; } = Color.FromArgb(150, 232, 70, 70);

    protected override void OnRecalculate() => _loadedAt = DateTime.MinValue;

    protected override void OnCalculate(int bar, decimal value)
    {
    }

    private void Reload()
    {
        _loadedAt = DateTime.UtcNow;
        _bands = new();
        _rows = new();
        _visibleFrom = default;
        var name = InstrumentInfo?.Instrument;
        if (MarkStore.Skip(name)) return;
        try
        {
            string safe = string.Concat(name!.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string root = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } d ? d : TradingHub.Common.Product.AppData;
            var path = Path.Combine(root, "BigTrades", safe + ".json");
            if (!File.Exists(path)) return;
            decimal tick = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m), size = tick * BandTicks;
            var since = DateTime.UtcNow.AddDays(-Days);
            _rows = (JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path)) ?? new())
                .Where(r => !r.Deleted && r.Time.ToUniversalTime() >= since && !(OnlyUntouched && r.Touched)).ToList();
            _bands = Bands(_rows, size);
        }
        catch (Exception) { /* no file or a bad one: nothing to draw */ }
    }

    private static List<Band> Bands(IEnumerable<Row> rows, decimal size) =>
        rows.GroupBy(r => Math.Floor(r.Price / size))
            .Select(g => new Band(g.Key * size, (g.Key + 1) * size, g.Where(r => r.Buy).Sum(r => r.Volume), g.Where(r => !r.Buy).Sum(r => r.Volume), g.Count()))
            .ToList();

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        if (DateTime.UtcNow - _loadedAt > TimeSpan.FromSeconds(20)) Reload();
        if (_bands.Count == 0) return;
        _tip.Clear();
        int right = ChartInfo.PriceChartContainer.Region.Right; // pinned to the price axis, in the empty margin, not over the candles
        Column(context, _bands, right, $"Big orders ({Days} days)");
        if (VisibleColumn && CurrentBar > 0)
        {
            var from = GetCandle(Math.Clamp(FirstVisibleBarNumber, 0, CurrentBar - 1)).Time.ToUniversalTime();
            if (from != _visibleFrom)
            {
                _visibleFrom = from;
                decimal tick = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m);
                _visible = Bands(_rows.Where(r => r.Time.ToUniversalTime() >= from), tick * BandTicks);
            }

            Column(context, _visible, right - MaxWidth - 6, "Big orders (visible candles)");
        }

        _tip.Draw(context, right);
    }

    private void Column(RenderContext context, List<Band> bands, int right, string title)
    {
        if (bands.Count == 0) return;
        decimal max = bands.Max(b => b.Buy + b.Sell);
        if (max <= 0) return;
        foreach (var b in bands)
        {
            int yTop = ChartInfo.GetYByPrice(b.Hi, false), yBot = ChartInfo.GetYByPrice(b.Lo, false);
            if (yBot < 0 || yTop > 5000) continue;
            int h = Math.Max(2, yBot - yTop - 1), total = (int)(MaxWidth * (b.Buy + b.Sell) / max);
            int wBuy = (int)(MaxWidth * b.Buy / max), wSell = Math.Max(0, total - wBuy);
            context.FillRectangle(SellColor, new Rectangle(right - wSell, yTop, wSell, h));
            context.FillRectangle(BuyColor, new Rectangle(right - wSell - wBuy, yTop, wBuy, h));
            var box = new Rectangle(right - total, yTop, Math.Max(total, 1), h);
            _tip.Add(box, b.Buy >= b.Sell ? BuyColor : SellColor, $"{title} {b.Lo} - {b.Hi}", $"buy {b.Buy:0.##}", $"sell {b.Sell:0.##}", $"{b.Count} orders");
        }
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }
}
