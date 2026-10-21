using SkiaSharp;
using TradingHub.Indicators;

namespace TradingHub.Charting.Rendering;

/// <summary>Candles, OHLC bars and line.</summary>
internal sealed class CandleRenderer
{
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeJoin = SKStrokeJoin.Round };

    public void Render(SKCanvas canvas, ChartModel model, ChartLayout layout, PanelLayout main, ChartStyle style, Dictionary<int, SKColor>? paintBars)
    {
        var series = model.Series;
        var t = model.Theme;
        if (series.Count == 0) return;

        if (style == ChartStyle.Line)
        {
            using var path = new SKPath();
            bool first = true;
            for (int i = Math.Max(0, layout.FirstVisible); i <= Math.Min(layout.LastVisible, series.Count - 1); i++)
            {
                var c = series[i];
                float x = layout.CenterX(i), y = main.Y(series.TicksToPrice(c.CloseTicks));
                if (first) { path.MoveTo(x, y); first = false; }
                else path.LineTo(x, y);
            }

            _line.Color = t.DrawingDefault;
            canvas.DrawPath(path, _line);
            return;
        }

        float slot = (float)layout.BarWidth;
        float bodyW = Math.Max(1, slot - layout.BarSpacing);
        bool ha = style == ChartStyle.HeikinAshi;
        decimal haOpen = 0, haClose = 0;
        if (ha)
        {
            // Heikin Ashi: the open is recursive; starting 300 candles earlier is enough to converge
            int start = Math.Max(0, layout.FirstVisible - 300);
            for (int i = start; i < Math.Min(layout.FirstVisible, series.Count); i++) NextHa(series, i, start, ref haOpen, ref haClose, out _, out _);
        }

        // the series can shrink between the layout being computed and this draw (it was rebuilt with another period): never out of range
        int last = Math.Min(layout.LastVisible, series.Count - 1);
        for (int i = Math.Max(0, layout.FirstVisible); i <= last; i++)
        {
            var c = series[i];
            decimal o = series.TicksToPrice(c.OpenTicks), h = series.TicksToPrice(c.HighTicks), l = series.TicksToPrice(c.LowTicks), cl = series.TicksToPrice(c.CloseTicks);
            if (ha)
            {
                NextHa(series, i, Math.Max(0, layout.FirstVisible - 300), ref haOpen, ref haClose, out var haHigh, out var haLow);
                o = haOpen;
                cl = haClose;
                h = haHigh;
                l = haLow;
            }

            if (c.Trades == 0) continue;
            if (paintBars != null && paintBars.TryGetValue(i, out var pcHidden) && pcHidden.Alpha == 0) continue;
            float cx = layout.CenterX(i);
            float yH = main.Y(h);
            float yL = main.Y(l);
            float yO = main.Y(o);
            float yC = main.Y(cl);
            bool up = cl >= o;
            var col = up ? t.UpCandle : t.DownCandle;
            if (paintBars != null && paintBars.TryGetValue(i, out var pc)) col = pc;

            float xLine = MathF.Floor(cx) + 0.5f;
            if (style == ChartStyle.Bars)
            {
                _stroke.Color = col;
                _stroke.StrokeWidth = bodyW >= 6 ? 2 : 1;
                canvas.DrawLine(xLine, yH, xLine, yL, _stroke);
                float tick = Math.Max(2, bodyW / 2);
                canvas.DrawLine(xLine - tick, yO, xLine, yO, _stroke);
                canvas.DrawLine(xLine, yC, xLine + tick, yC, _stroke);
                continue;
            }

            _stroke.Color = col;
            _stroke.StrokeWidth = 1;
            canvas.DrawLine(xLine, yH, xLine, yL, _stroke);
            float top = Math.Min(yO, yC), bot = Math.Max(yO, yC);
            if (bot - top < 1) bot = top + 1;
            float left = MathF.Round(cx - bodyW / 2), right = left + MathF.Max(1, MathF.Round(bodyW));
            _fill.Color = col;
            canvas.DrawRect(new SKRect(left, top, right, bot), _fill);
        }
    }

    /// <summary>Advances the Heikin Ashi calculation by one candle.</summary>
    private static void NextHa(TradingHub.Data.CandleSeries series, int i, int start, ref decimal haOpen, ref decimal haClose, out decimal haHigh, out decimal haLow)
    {
        var c = series[i];
        decimal o = series.TicksToPrice(c.OpenTicks), h = series.TicksToPrice(c.HighTicks), l = series.TicksToPrice(c.LowTicks), cl = series.TicksToPrice(c.CloseTicks);
        decimal close = (o + h + l + cl) / 4;
        decimal open = i == start ? (o + cl) / 2 : (haOpen + haClose) / 2;
        haOpen = open;
        haClose = close;
        haHigh = Math.Max(h, Math.Max(open, close));
        haLow = Math.Min(l, Math.Min(open, close));
    }
}
