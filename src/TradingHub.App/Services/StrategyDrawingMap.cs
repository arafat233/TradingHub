using SkiaSharp;
using TradingHub.Charting;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>
/// What a strategy draws (its canvas) and its trades, translated into what the chart paints. The app's charts
/// and the lab's snapshots (the ones Claude looks at) use it.
/// </summary>
internal static class StrategyDrawingMap
{
    private static readonly Dictionary<string, SKColor> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["verde"] = new(34, 197, 94), ["green"] = new(34, 197, 94),
        ["rojo"] = new(239, 68, 68), ["red"] = new(239, 68, 68),
        ["azul"] = new(59, 130, 246), ["blue"] = new(59, 130, 246),
        ["celeste"] = new(56, 189, 248), ["cyan"] = new(56, 189, 248),
        ["amarillo"] = new(250, 204, 21), ["yellow"] = new(250, 204, 21),
        ["naranja"] = new(249, 115, 22), ["orange"] = new(249, 115, 22),
        ["violeta"] = new(168, 85, 247), ["purple"] = new(168, 85, 247),
        ["rosa"] = new(236, 72, 153), ["pink"] = new(236, 72, 153),
        ["gris"] = new(148, 163, 184), ["gray"] = new(148, 163, 184), ["grey"] = new(148, 163, 184),
        ["blanco"] = new(241, 245, 249), ["white"] = new(241, 245, 249),
        ["negro"] = new(15, 23, 42), ["black"] = new(15, 23, 42),
    };

    /// <summary>"#RRGGBB", "#AARRGGBB" or a name ("green", "red"…); with no color, the strategy's own.</summary>
    public static SKColor ParseColor(string? text, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (NamedColors.TryGetValue(text.Trim(), out var named)) return named;
        return SKColor.TryParse(text.Trim(), out var c) ? c : fallback;
    }

    public static StrategyShape ToShape(StrategyDrawing d, SKColor fallback) =>
        new((StrategyShapeKind)(int)d.Kind, d.Time1, d.Price1, d.Time2, d.Price2, ParseColor(d.Color, fallback), (byte)Math.Round(d.FillOpacity * 2.55),
            d.Width, (int)d.Style, (StrategyMarker)(int)d.Shape, d.Text, d.ExtendRight);

    public static StrategyLine ToLine(string name, string? color, int width, LineStyle style, IReadOnlyList<(DateTime Time, decimal? Value)> points, SKColor fallback) =>
        new(name, ParseColor(color, fallback), width, (int)style, points);

    /// <summary>Entries (an arrow) and exits (a cross with the result) of the trades, and the line joining them.</summary>
    public static (List<StrategySignal> Signals, List<TradeLink> Links) Trades(IEnumerable<ClosedTrade> trades, OpenPosition? open = null)
    {
        var signals = new List<StrategySignal>();
        var links = new List<TradeLink>();
        foreach (var t in trades)
        {
            signals.Add(new StrategySignal(t.EntryTime, t.EntryPrice, t.IsLong, true, t.IsLong ? "L" : "S"));
            signals.Add(new StrategySignal(t.ExitTime, t.ExitPrice, !t.IsLong, false, t.NetPnl.ToString("+#,##0.##;-#,##0.##;0", System.Globalization.CultureInfo.InvariantCulture)));
            links.Add(new TradeLink(t.EntryTime, t.EntryPrice, t.ExitTime, t.ExitPrice, t.IsLong, t.NetPnl));
        }

        if (open != null) signals.Add(new StrategySignal(open.EntryTime, open.AveragePrice, open.IsLong, true, open.IsLong ? "L" : "S"));
        return (signals, links);
    }
}
