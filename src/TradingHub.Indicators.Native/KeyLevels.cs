using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Key levels of the previous session drawn across the current one: high, low, close and the
/// profile's POC / VAH / VAL, plus the current session's open.
/// </summary>
[DisplayName("Previous day levels")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class KeyLevels : Indicator
{
    private readonly record struct Level(string Name, decimal Price, Color Color);

    private readonly List<Level> _levels = new();
    private readonly HoverTip _tip = new();
    private int _sessionStart;
    private int _prevStart = -1;
    private (int Prev, int Start) _builtFor = (-1, -1);

    public KeyLevels() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Value area (%)", GroupName = "Calculation", Order = 10)]
    [Range(10, 100)]
    public int ValueAreaPercent { get; set; } = 70;

    [Display(Name = "High / low / close", GroupName = "Show", Order = 20)]
    public bool ShowHlc { get; set; } = true;

    [Display(Name = "POC / VAH / VAL", GroupName = "Show", Order = 30)]
    public bool ShowProfile { get; set; } = true;

    [Display(Name = "Session open", GroupName = "Show", Order = 40)]
    public bool ShowOpen { get; set; } = true;

    [Display(Name = "Line width", GroupName = "Display", Order = 50)]
    [Range(1, 5)]
    public int LineWidth { get; set; } = 1;

    protected override void OnRecalculate()
    {
        _levels.Clear();
        _sessionStart = 0;
        _prevStart = -1;
        _builtFor = (-1, -1);
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar > 0 && IsNewSession(bar))
        {
            _prevStart = _sessionStart;
            _sessionStart = bar;
        }

        // rebuilt once per session start and on the live bar's first calculation (cheap: it only reads the previous session)
        if (bar != CurrentBar - 1 || _prevStart < 0 || _builtFor == (_prevStart, _sessionStart)) return;
        _builtFor = (_prevStart, _sessionStart);
        Rebuild(bar);
    }

    private void Rebuild(int bar)
    {
        _levels.Clear();
        foreach (var (name, price) in PrevSession.Levels(GetCandle, _prevStart, _sessionStart, ValueAreaPercent))
        {
            bool hlc = name is "PDH" or "PDL" or "PDC", prof = name is "POC" or "VAH" or "VAL";
            if ((hlc && !ShowHlc) || (prof && !ShowProfile) || (name == "Open" && !ShowOpen)) continue;
            _levels.Add(new Level(name, price, name switch
            {
                "PDH" => Color.FromArgb(230, 232, 70, 70),
                "PDL" => Color.FromArgb(230, 50, 190, 120),
                "PDC" => Color.FromArgb(200, 180, 180, 180),
                "POC" => Color.FromArgb(230, 255, 196, 0),
                "Open" => Color.FromArgb(200, 200, 120, 255),
                _ => Color.FromArgb(200, 120, 160, 220),
            }));
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _levels.Count == 0) return;
        _tip.Clear();
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        int x0 = ChartInfo.GetXByBar(Math.Max(first, _sessionStart), true);
        int x1 = ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        foreach (var l in _levels)
        {
            int y = ChartInfo.GetYByPrice(l.Price, false);
            context.DrawLine(new RenderPen(l.Color, LineWidth), x0, y, x1, y);
            context.DrawString($"{l.Name} {l.Price}", font, l.Color, x1 - 90, y - 13);
            _tip.Add(new Rectangle(x0, y - 1, Math.Max(1, x1 - x0), 3), l.Color, Describe(l.Name), $"{l.Price}");
        }

        _tip.Draw(context, x1);
    }

    private static string Describe(string name) => name switch
    {
        "PDH" => "Previous session high",
        "PDL" => "Previous session low",
        "PDC" => "Previous session close",
        "POC" => "Previous session POC (most volume)",
        "VAH" => "Previous session value area high",
        "VAL" => "Previous session value area low",
        _ => "Today's open",
    };

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }

}

/// <summary>The previous session's levels (high, low, close, the profile's POC / VAH / VAL) and the current session's open.</summary>
internal static class PrevSession
{
    public static List<(string Name, decimal Price)> Levels(Func<int, IndicatorCandle> candle, int prevStart, int sessionStart, int valueAreaPercent)
    {
        var list = new List<(string, decimal)>();
        decimal hi = decimal.MinValue, lo = decimal.MaxValue;
        var vol = new SortedDictionary<decimal, decimal>();
        for (int i = prevStart; i < sessionStart; i++)
        {
            var c = candle(i);
            hi = Math.Max(hi, c.High);
            lo = Math.Min(lo, c.Low);
            foreach (var l in c.GetAllPriceLevels())
            {
                vol.TryGetValue(l.Price, out var v);
                vol[l.Price] = v + l.Bid + l.Ask;
            }
        }

        if (hi < lo) return list;
        list.Add(("PDH", hi));
        list.Add(("PDL", lo));
        list.Add(("PDC", candle(sessionStart - 1).Close));
        if (vol.Count > 0)
        {
            var (poc, vah, val) = ValueArea(vol, valueAreaPercent);
            list.Add(("POC", poc));
            list.Add(("VAH", vah));
            list.Add(("VAL", val));
        }

        list.Add(("Open", candle(sessionStart).Open));
        return list;
    }

    /// <summary>POC and the value area that grows from it, one price step at a time towards the larger side.</summary>
    internal static (decimal Poc, decimal Vah, decimal Val) ValueArea(SortedDictionary<decimal, decimal> vol, int percent)
    {
        var prices = vol.Keys.ToList();
        int poc = 0;
        for (int i = 1; i < prices.Count; i++) if (vol[prices[i]] > vol[prices[poc]]) poc = i;
        int up = poc, dn = poc;
        decimal acc = vol[prices[poc]], target = vol.Values.Sum() * percent / 100m;
        while (acc < target && (up < prices.Count - 1 || dn > 0))
        {
            decimal vu = up < prices.Count - 1 ? vol[prices[up + 1]] : -1;
            decimal vd = dn > 0 ? vol[prices[dn - 1]] : -1;
            acc += vu >= vd ? vol[prices[++up]] : vol[prices[--dn]];
        }

        return (prices[poc], prices[up], prices[dn]);
    }
}
