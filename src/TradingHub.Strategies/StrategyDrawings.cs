namespace TradingHub.Strategies;

/// <summary>Dash of a line or border.</summary>
public enum LineStyle
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>Shape of a marker on the chart.</summary>
public enum MarkerShape
{
    ArrowUp,
    ArrowDown,
    Circle,
    Square,
    Diamond,
    Cross,
}

/// <summary>What a strategy drawing is.</summary>
public enum DrawingKind
{
    Line,
    Box,
    HorizontalLine,
    Marker,
    Text,
}

/// <summary>
/// A strategy drawing on the chart (range, level, arrow, text…). The times are UTC (like Bar.Time) and the
/// colors, "#RRGGBB", "#AARRGGBB" or a name ("green", "red", "blue", "yellow", "orange", "purple", "gray",
/// "white", "cyan", "pink"); with no color, the strategy's own.
/// </summary>
public sealed class StrategyDrawing
{
    public required string Id { get; init; }
    public DrawingKind Kind { get; init; }
    public DateTime Time1 { get; init; }
    public decimal Price1 { get; init; }
    public DateTime Time2 { get; init; }
    public decimal Price2 { get; init; }
    public string? Color { get; init; }

    /// <summary>Fill of the rectangles: opacity from 0 to 100.</summary>
    public int FillOpacity { get; init; }

    public int Width { get; init; } = 1;
    public LineStyle Style { get; init; }
    public MarkerShape Shape { get; init; }
    public string Text { get; init; } = "";

    /// <summary>The horizontal line carries on to the chart's right edge.</summary>
    public bool ExtendRight { get; init; }
}

/// <summary>A continuous line the strategy traces candle by candle (for example a channel's ceiling and floor).</summary>
public sealed class StrategyPlot
{
    public required string Name { get; init; }
    public string? Color { get; set; }
    public int Width { get; set; } = 1;
    public LineStyle Style { get; set; }

    /// <summary>Points in time order; a null value breaks the line.</summary>
    public List<(DateTime Time, decimal? Value)> Points { get; } = new();
}

/// <summary>
/// What the strategy draws: one drawing per id (repeating the id replaces it, which is useful for stretching today's range candle
/// by candle) and continuous lines by name. It has a cap so the chart does not seize up: past it, the oldest ones are dropped.
/// </summary>
public sealed class StrategyCanvas
{
    public const int MaxDrawings = 3000, MaxPlots = 20, MaxPointsPerPlot = 300_000;

    private readonly Dictionary<string, StrategyDrawing> _drawings = new();
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new();
    private readonly Dictionary<string, StrategyPlot> _plots = new();
    private readonly object _lock = new();

    /// <summary>Goes up with every change (to redraw only when needed).</summary>
    public int Version { get; private set; }

    public void Set(StrategyDrawing d)
    {
        lock (_lock)
        {
            if (_nodes.TryGetValue(d.Id, out var node)) _order.Remove(node);
            _drawings[d.Id] = d;
            _nodes[d.Id] = _order.AddLast(d.Id);
            while (_drawings.Count > MaxDrawings && _order.First is { } first)
            {
                _drawings.Remove(first.Value);
                _nodes.Remove(first.Value);
                _order.RemoveFirst();
            }

            Version++;
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            if (!_drawings.Remove(id)) return;
            if (_nodes.Remove(id, out var node)) _order.Remove(node);
            Version++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _drawings.Clear();
            _nodes.Clear();
            _order.Clear();
            _plots.Clear();
            Version++;
        }
    }

    public void Plot(string name, DateTime time, decimal? value, string? color, int width, LineStyle style)
    {
        lock (_lock)
        {
            if (!_plots.TryGetValue(name, out var p))
            {
                if (_plots.Count >= MaxPlots) return;
                _plots[name] = p = new StrategyPlot { Name = name };
            }

            p.Color = color ?? p.Color;
            p.Width = width;
            p.Style = style;
            // the same candle again (for example, when recalculating): the value is replaced
            if (p.Points.Count > 0 && p.Points[^1].Time >= time)
            {
                if (p.Points[^1].Time == time) p.Points[^1] = (time, value);
            }
            else p.Points.Add((time, value));
            if (p.Points.Count > MaxPointsPerPlot) p.Points.RemoveRange(0, p.Points.Count - MaxPointsPerPlot);
            Version++;
        }
    }

    /// <summary>Copy of the drawings (to paint them from another thread).</summary>
    public List<StrategyDrawing> Drawings()
    {
        lock (_lock) return _order.Select(id => _drawings[id]).ToList();
    }

    /// <summary>Copy of the continuous lines.</summary>
    public List<(string Name, string? Color, int Width, LineStyle Style, List<(DateTime Time, decimal? Value)> Points)> Plots()
    {
        lock (_lock) return _plots.Values.Select(p => (p.Name, p.Color, p.Width, p.Style, p.Points.ToList())).ToList();
    }
}
