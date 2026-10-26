using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Hover tooltips for the indicators: while drawing, an indicator registers the screen area of each thing it draws with
/// the lines of text to show; the mouse position is kept from the move events, and the tooltip of the topmost
/// (last registered) area under the mouse is drawn at the end of the render.
/// </summary>
internal sealed class HoverTip
{
    private readonly List<(Rectangle Rect, Color Color, string[] Lines)> _hits = new();
    private Point _mouse = new(int.MinValue, int.MinValue);
    private string _shown = "";

    /// <summary>Forget what was registered (call at the start of every render).</summary>
    public void Clear() => _hits.Clear();

    public void Add(Rectangle rect, Color color, params string[] lines) => _hits.Add((rect, color, lines));

    /// <summary>Records the mouse position. True when the tooltip under it changed (the chart should redraw).</summary>
    public bool Move(int x, int y)
    {
        _mouse = new Point(x, y);
        var key = Find() is { } h ? string.Join("|", h.Lines) : "";
        if (key == _shown) return false;
        _shown = key;
        return true;
    }

    private (Rectangle Rect, Color Color, string[] Lines)? Find()
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var r = _hits[i].Rect;
            r.Inflate(3, 3);
            if (r.Contains(_mouse)) return _hits[i];
        }

        return null;
    }

    /// <summary>Draws the tooltip if the mouse is over a registered area. <paramref name="rightEdge"/> keeps it inside the chart.</summary>
    public void Draw(RenderContext context, int rightEdge)
    {
        if (Find() is not { } h) return;
        var font = new RenderFont("Arial", 9, FontStyle.Bold);
        int w = h.Lines.Max(l => l.Length) * 8 + 16, height = h.Lines.Length * 16 + 10;
        int x = Math.Max(4, Math.Min(_mouse.X + 16, rightEdge - w)), y = Math.Max(4, _mouse.Y - height - 10);
        var edge = Color.FromArgb(255, h.Color.R, h.Color.G, h.Color.B);
        context.FillRectangle(Color.FromArgb(235, 18, 20, 26), new Rectangle(x, y, w, height));
        context.DrawRectangle(new RenderPen(edge), new Rectangle(x, y, w, height));
        for (int i = 0; i < h.Lines.Length; i++)
            context.DrawString(h.Lines[i], font, i == 0 ? edge : Color.White, x + 8, y + 5 + i * 16);
    }
}
