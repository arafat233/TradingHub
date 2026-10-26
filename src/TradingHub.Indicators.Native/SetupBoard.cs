using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Text.Json;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Setup confluence: counts, for every candle, how many independent signals agree. The signals are the saved marks of the
/// Absorption, Stacked imbalances, Delta divergence and Liquidity sweeps indicators, and a Big Trades level touched on that candle.
/// A badge shows candles where at least N signals point the same way (green bullish, red bearish). The signals come from files, so
/// those indicators must be on a chart of the instrument for their marks to exist.
/// </summary>
[DisplayName("Setup confluence (signals that agree)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class SetupBoard : Indicator
{
    private sealed record Vote(string Kind, bool Bearish);

    private sealed record LevelRow(DateTime Time, decimal Price, decimal Volume, bool Buy, bool Deleted = false, DateTime TouchTime = default);

    private Dictionary<int, List<Vote>> _votes = new();
    private DateTime _loadedAt = DateTime.MinValue;
    private int _lastCount = -1;
    private readonly HashSet<int> _alerted = new();
    private readonly HoverTip _tip = new();

    public SetupBoard() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    [Display(Name = "Show from (signals that agree)", GroupName = "Calculation", Order = 10)]
    [Range(2, 5)]
    public int MinVotes { get; set; } = 2;

    [Display(Name = "Alert from (signals that agree)", GroupName = "Alerts", Order = 20, Description = "One alert when the latest candles reach this many agreeing signals (0 = no alerts).")]
    [Range(0, 5)]
    public int AlertVotes { get; set; } = 3;

    [Display(Name = "Bullish color", GroupName = "Display", Order = 30)]
    public Color BullishColor { get; set; } = Color.FromArgb(230, 50, 190, 120);

    [Display(Name = "Bearish color", GroupName = "Display", Order = 40)]
    public Color BearishColor { get; set; } = Color.FromArgb(230, 232, 70, 70);

    protected override void OnRecalculate()
    {
        _votes = new();
        _loadedAt = DateTime.MinValue;
        _alerted.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar != CurrentBar - 1) return;
        if (DateTime.UtcNow - _loadedAt > TimeSpan.FromSeconds(10)) Reload();
        if (AlertVotes <= 0) return;
        // the last two candles: a signal can arrive a little after its candle closed
        for (int b = Math.Max(0, bar - 1); b <= bar; b++)
        {
            var (agree, bearish, kinds) = Tally(b);
            if (agree >= AlertVotes && _alerted.Add(b * 2 + (bearish ? 1 : 0)))
                AddAlert("alert1", $"Setup: {agree} signals agree ({(bearish ? "bearish" : "bullish")}: {string.Join(", ", kinds)})");
        }
    }

    private static string Letter(string kind) => kind switch
    {
        "absorption" => "A", "stacked" => "S", "divergence" => "D", "sweep" => "W", _ => "L",
    };

    private static string KindName(string kind) => kind switch
    {
        "absorption" => "absorption", "stacked" => "stacked imbalance", "divergence" => "delta divergence", "sweep" => "liquidity sweep", _ => "big-trade level touched",
    };

    private int BarAt(DateTime utc)
    {
        int lo = 0, hi = CurrentBar - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (GetCandle(mid).Time <= utc) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        return ans;
    }

    private void Reload()
    {
        _loadedAt = DateTime.UtcNow;
        var votes = new Dictionary<int, List<Vote>>();
        var name = InstrumentInfo?.Instrument;
        if (MarkStore.Skip(name) || CurrentBar == 0) { _votes = votes; return; }
        void Add(DateTime utc, string kind, bool bearish)
        {
            var t = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            if (t < GetCandle(0).Time || t > GetCandle(CurrentBar - 1).LastTime) return;
            int bar = BarAt(t);
            if (bar < 0) return;
            if (!votes.TryGetValue(bar, out var list)) votes[bar] = list = new List<Vote>();
            // one vote per kind and direction per candle: ten absorption marks on one candle are still one signal
            if (!list.Any(v => v.Kind == kind && v.Bearish == bearish)) list.Add(new Vote(kind, bearish));
        }

        try
        {
            var marks = JsonSerializer.Deserialize<List<MarkStore.Rec>>(File.ReadAllText(MarkStore.PathFor(name!))) ?? new();
            foreach (var m in marks)
                Add(m.Time, m.Kind, m.Kind == "stacked" ? !m.Flag : m.Flag); // stacked: Flag = buy stack (bullish); the others: Flag = bearish
        }
        catch (Exception) { /* no marks saved yet */ }

        try
        {
            string safe = string.Concat(name!.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string root = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } d ? d : TradingHub.Common.Product.AppData;
            var path = Path.Combine(root, "BigTrades", safe + ".json");
            foreach (var l in JsonSerializer.Deserialize<List<LevelRow>>(File.ReadAllText(path)) ?? new())
                if (!l.Deleted && l.TouchTime != default) Add(l.TouchTime, "level", !l.Buy); // a buy level that is touched is expected to hold: bullish
        }
        catch (Exception) { /* no levels saved yet */ }

        _votes = votes;
    }

    private (int Agree, bool Bearish, List<string> Kinds) Tally(int bar)
    {
        if (!_votes.TryGetValue(bar, out var list)) return (0, false, new());
        var bear = list.Where(v => v.Bearish).Select(v => KindName(v.Kind)).ToList();
        var bull = list.Where(v => !v.Bearish).Select(v => KindName(v.Kind)).ToList();
        return bear.Count > bull.Count ? (bear.Count, true, bear) : (bull.Count, false, bull);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _votes.Count == 0) return;
        _tip.Clear();
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var font = new RenderFont("Arial", 9, FontStyle.Bold);
        for (int b = first; b <= last; b++)
        {
            var (agree, bearish, kinds) = Tally(b);
            if (agree < MinVotes) continue;
            var c = GetCandle(b);
            var col = bearish ? BearishColor : BullishColor;
            int x = ChartInfo.GetXByBar(b, true) + ChartInfo.PriceChartContainer.BarsWidth / 2;
            int y = bearish ? ChartInfo.GetYByPrice(c.High, false) - 44 : ChartInfo.GetYByPrice(c.Low, false) + 30;
            string letters = string.Concat(_votes[b].Where(v => v.Bearish == bearish).Select(v => Letter(v.Kind)));
            var box = new Rectangle(x - 22, y, 44, 22);
            context.FillRectangle(Color.FromArgb(200, col.R, col.G, col.B), box);
            context.DrawString($"{agree}x {letters}", font, Color.White, box.X + 4, box.Y + 4);
            _tip.Add(box, col, $"{agree} signals agree: {(bearish ? "bearish" : "bullish")}", string.Join(", ", kinds), $"{DateTime.SpecifyKind(c.Time, DateTimeKind.Utc).ToLocalTime():dd/MM HH:mm}");
        }

        _tip.Draw(context, ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }
}
