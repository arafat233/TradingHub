using System.Globalization;
using SkiaSharp;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Rendering.Context;

namespace TradingHub.Charting.Heatmap;

/// <summary>
/// Volume traded at each price since the session start, split buy / sell. Incremental: it keeps its place between frames, so a redraw
/// only reads the trades that arrived since the last one (it starts over if the session, the data or the replay time changes).
/// </summary>
public sealed class SessionProfile
{
    private readonly Dictionary<long, (decimal Buy, decimal Sell)> _byPrice = new();
    private int _from, _idx;
    private DateTime _session;

    public void Reset()
    {
        _byPrice.Clear();
        _idx = _from = 0;
        _session = default;
    }

    /// <summary>The profile up to <paramref name="end"/> (the trades after it, as in a replay, are not counted).</summary>
    public IReadOnlyDictionary<long, (decimal Buy, decimal Sell)> Update(TradeStore store, SessionSettings session, DateTime end)
    {
        var sessionStart = session.SessionStartUtc(session.SessionDate(end));
        int from = store.LowerBound(sessionStart), to = store.LowerBound(end.AddTicks(1));
        if (sessionStart != _session || from != _from || _idx < from || _idx > to)
        {
            _byPrice.Clear();
            (_session, _from, _idx) = (sessionStart, from, from);
        }

        for (; _idx < to; _idx++)
        {
            long p = store.GetPriceTicks(_idx);
            decimal v = store.GetVolume(_idx);
            _byPrice.TryGetValue(p, out var cur);
            _byPrice[p] = store.GetSide(_idx) == TradeSide.Buy ? (cur.Buy + v, cur.Sell) : (cur.Buy, cur.Sell + v);
        }

        return _byPrice;
    }
}

public sealed partial class HeatmapRenderer
{
    private const float CobW = 72, SvpW = 100, CqcW = 64;
    private float _cvdNow, _cvdScale = 1;

    /// <summary>
    /// CQC (cumulative quantity change): for each price, how much the resting size changed between the start of the window and now
    /// (positive = orders were added there, negative = pulled or filled). Prices that exist only on one of the two books count in full.
    /// </summary>
    public static Dictionary<long, decimal> ComputeCqc(DepthSnapshot? start, DepthSnapshot end)
    {
        var d = new Dictionary<long, decimal>();
        if (start == null) return d;
        foreach (var l in end.Bids) d[l.PriceTicks] = l.Size;
        foreach (var l in end.Asks) d[l.PriceTicks] = l.Size;
        foreach (var l in start.Bids.Concat(start.Asks))
        {
            d.TryGetValue(l.PriceTicks, out var now);
            d[l.PriceTicks] = now - l.Size;
        }

        d.Keys.Where(k => d[k] == 0).ToList().ForEach(k => d.Remove(k));
        return d;
    }

    private static string Num(decimal v, bool sign = false)
    {
        decimal a = Math.Abs(v);
        string s = a >= 10_000 ? $"{a / 1000:0.#}k" : a >= 100 ? a.ToString("0", CultureInfo.InvariantCulture) : a.ToString("0.##", CultureInfo.InvariantCulture);
        return sign ? (v > 0 ? "+" : v < 0 ? "-" : "") + s : s;
    }

    // ------------------------------------------------------------------ the ladder: COB | SVP | CQC | Delta

    /// <summary>
    /// The ladder on the right, one row per price: COB = the live book (size resting now), SVP = volume traded since the session start
    /// (buy green / sell red), CQC = how the resting size changed over the window, Delta = session buy minus sell at that price.
    /// </summary>
    private void DrawDom(SKCanvas canvas, HeatmapModel m, float x, float heatH, int rows, IReadOnlyDictionary<long, (decimal Buy, decimal Sell)> prof, Dictionary<long, decimal> cqc)
    {
        _fill.Color = new SKColor(16, 22, 30);
        canvas.DrawRect(x, 0, DomW, heatH, _fill);
        var book = m.Book;
        long bottom = m.TopTicks - rows + 1;
        float cobMax = 1, svpMax = 1, dMax = 1, cqMax = 1;
        var cob = new Dictionary<long, (decimal Size, bool Ask)>();
        foreach (var l in book.Bids) if (l.PriceTicks <= m.TopTicks && l.PriceTicks >= bottom) cob[l.PriceTicks] = (l.Size, false);
        foreach (var l in book.Asks) if (l.PriceTicks <= m.TopTicks && l.PriceTicks >= bottom) cob[l.PriceTicks] = (l.Size, true);
        if (cob.Count > 0) cobMax = Math.Max(1f, Percentile(cob.Values.Select(v => (float)v.Size).ToList(), 0.97));
        for (long t = bottom; t <= m.TopTicks; t++)
        {
            if (prof.TryGetValue(t, out var pv))
            {
                svpMax = Math.Max(svpMax, (float)(pv.Buy + pv.Sell));
                dMax = Math.Max(dMax, (float)Math.Abs(pv.Buy - pv.Sell));
            }

            if (cqc.TryGetValue(t, out var q)) cqMax = Math.Max(cqMax, (float)Math.Abs(q));
        }

        float x1 = x, x2 = x + CobW, x3 = x2 + SvpW, x4 = x3 + CqcW;
        bool numbers = m.TickPx >= 9;
        var font = RenderContext.GetFont("Arial", m.TickPx >= 11 ? 10 : 8.5f, false, false);
        float h = Math.Max(1, m.TickPx - 1);
        for (long t = bottom; t <= m.TopTicks; t++)
        {
            float y = m.YOfTicks(t), ty = y + h - (m.TickPx >= 11 ? 2 : 1);
            if (cob.TryGetValue(t, out var c))
            {
                _fill.Color = c.Ask ? new SKColor(190, 60, 55) : new SKColor(45, 150, 85);
                canvas.DrawRect(x1 + 1, y, Math.Max(1, (CobW - 3) * Math.Min(1f, (float)c.Size / cobMax)), h, _fill);
                if (numbers) { _aa.Color = SKColors.White; canvas.DrawText(Num(c.Size), x1 + 4, ty, font, _aa); }
            }

            if (prof.TryGetValue(t, out var v))
            {
                float wTot = (SvpW - 4) * (float)(v.Buy + v.Sell) / svpMax;
                float wBuy = wTot * (float)(v.Buy / Math.Max(1e-9m, v.Buy + v.Sell));
                _fill.Color = new SKColor(60, 190, 95);
                canvas.DrawRect(x2 + 2, y, Math.Max(1, wBuy), h, _fill);
                _fill.Color = new SKColor(215, 70, 55);
                canvas.DrawRect(x2 + 2 + wBuy, y, Math.Max(0, wTot - wBuy), h, _fill);
                if (numbers) { _aa.Color = new SKColor(230, 234, 240); canvas.DrawText(Num(v.Buy + v.Sell), x2 + 5, ty, font, _aa); }

                decimal delta = v.Buy - v.Sell;
                if (delta != 0 && numbers)
                {
                    _aa.Color = delta > 0 ? new SKColor(90, 215, 120) : new SKColor(240, 95, 85);
                    canvas.DrawText(Num(delta, sign: true), x4 + 5, ty, font, _aa);
                }
            }

            if (cqc.TryGetValue(t, out var q) && numbers)
            {
                _aa.Color = q > 0 ? new SKColor(90, 215, 120) : new SKColor(240, 95, 85);
                canvas.DrawText(Num(q, sign: true), x3 + 5, ty, font, _aa);
            }
        }

        // headers and column lines
        var head = RenderContext.GetFont("Arial", 9, true, false);
        _fill.Color = new SKColor(16, 22, 30, 215);
        canvas.DrawRect(x, 0, DomW, 13, _fill);
        _aa.Color = new SKColor(150, 160, 175);
        foreach (var (hx, name) in new[] { (x1, "COB"), (x2, "SVP"), (x3, "CQC"), (x4, "Delta") }) canvas.DrawText(name, hx + 5, 10, head, _aa);
        _stroke.Color = new SKColor(255, 255, 255, 35);
        _stroke.StrokeWidth = 1;
        foreach (float lx in new[] { x2, x3, x4 }) canvas.DrawLine(lx, 0, lx, heatH, _stroke);
    }

    // ------------------------------------------------------------------ static blocks

    /// <summary>
    /// Big orders that sat still: a level whose size stays within 10% of what it was for 20 seconds or more (and is a big one) is
    /// covered with a pale block with its size, so passive walls stand out from the orders that come and go.
    /// </summary>
    private void DrawBlocks(SKCanvas canvas, HeatmapModel m, float[] grid, int cols, int rows, float plotW)
    {
        double secPerCol = (m.EndTime - m.StartTime).TotalSeconds / cols;
        int minCols = (int)Math.Ceiling(20 / Math.Max(1e-6, secPerCol));
        if (minCols < 2) minCols = 2;
        float thr = Math.Max(1f, m.ReferenceSize * 0.5f), colW = plotW / cols;
        var font = RenderContext.GetFont("Arial", 9, true, false);
        for (int r = 0; r < rows; r++)
        {
            int c = 0;
            while (c < cols)
            {
                float size = grid[r * cols + c];
                if (size < thr) { c++; continue; }
                int c0 = c;
                while (c < cols && grid[r * cols + c] >= size * 0.9f && grid[r * cols + c] <= size * 1.1f) c++;
                if (c - c0 < minCols) continue;
                float y = r * m.TickPx;
                _fill.Color = new SKColor(205, 212, 225, 70);
                canvas.DrawRect(c0 * colW, y, (c - c0) * colW, m.TickPx, _fill);
                _stroke.Color = new SKColor(215, 222, 235, 150);
                _stroke.StrokeWidth = 1;
                canvas.DrawRect(c0 * colW, y, (c - c0) * colW, m.TickPx, _stroke);
                if (m.TickPx >= 9 && (c - c0) * colW > 34)
                {
                    _aa.Color = SKColors.White;
                    canvas.DrawText(Num((decimal)size), c0 * colW + 4, y + m.TickPx - 1.5f, font, _aa);
                }
            }
        }
    }

    // ------------------------------------------------------------------ tags at the price

    /// <summary>
    /// Small labels at the right edge: on the last price "size (n)" = the size of the last trade and how many trades hit that price in the
    /// last second; on the best bid and the best ask, the size resting there.
    /// </summary>
    private void DrawTags(SKCanvas canvas, HeatmapModel m)
    {
        var font = RenderContext.GetFont("Arial", 10, true, false);
        void Pill(string text, float y, SKColor border)
        {
            float w = RenderContext.MeasureWidth(font, text) + 10, x = m.HeatRect.Right - w - 4;
            _fill.Color = new SKColor(14, 18, 24, 235);
            canvas.DrawRoundRect(x, y - 8, w, 16, 3, 3, _fill);
            _stroke.Color = border;
            _stroke.StrokeWidth = 1;
            canvas.DrawRoundRect(x, y - 8, w, 16, 3, 3, _stroke);
            _aa.Color = SKColors.White;
            canvas.DrawText(text, x + 5, y + 4, font, _aa);
        }

        var book = m.Book;
        if (book.HasBid) Pill(Num(book.BestBidSize), m.YOfTicks(book.BestBidTicks) + m.TickPx / 2, new SKColor(70, 205, 110));
        if (book.HasAsk) Pill(Num(book.BestAskSize), m.YOfTicks(book.BestAskTicks) + m.TickPx / 2, new SKColor(235, 90, 80));

        var store = m.Trades;
        int hi = store.LowerBound(m.EndTime.AddTicks(1)) - 1;
        if (hi < 0) return;
        long price = store.GetPriceTicks(hi);
        int n = 0;
        for (int i = hi; i >= 0 && m.EndTime - store.GetTime(i) <= TimeSpan.FromSeconds(1); i--)
            if (store.GetPriceTicks(i) == price) n++;
        Pill($"{Num(store.GetVolume(hi))} ({Math.Max(1, n)})", m.YOfTicks(price) + m.TickPx / 2, new SKColor(255, 255, 255));
    }

    // ------------------------------------------------------------------ Market Vol and Avg Liquidity boxes

    /// <summary>
    /// Two small boxes in the corner. Market Vol: what traded in the last N seconds, at the ask (buyers) and at the bid (sellers).
    /// Avg. Liquidity: the average size resting on the first L levels of each side, averaged over the window.
    /// </summary>
    private void DrawStats(SKCanvas canvas, HeatmapModel m, DepthSnapshot?[] books, float heatH)
    {
        var s = m.Settings;
        decimal ask = 0, bid = 0;
        var store = m.Trades;
        var from = m.EndTime - TimeSpan.FromSeconds(Math.Clamp(s.MarketVolSeconds, 1, 600));
        for (int i = store.LowerBound(from); i < store.Count && store.GetTime(i) <= m.EndTime; i++)
        {
            if (store.GetSide(i) == TradeSide.Buy) ask += store.GetVolume(i); else bid += store.GetVolume(i);
        }

        int levels = Math.Clamp(s.LiquidityLevels, 1, 20);
        double sumAsk = 0, sumBid = 0;
        int n = 0;
        foreach (var b in books)
        {
            if (b == null) continue;
            sumAsk += b.Asks.Take(levels).Select(l => (double)l.Size).DefaultIfEmpty(0).Average();
            sumBid += b.Bids.Take(levels).Select(l => (double)l.Size).DefaultIfEmpty(0).Average();
            n++;
        }

        decimal avgAsk = n == 0 ? 0 : (decimal)(sumAsk / n), avgBid = n == 0 ? 0 : (decimal)(sumBid / n);
        LastAvgLiquidity = (avgBid, avgAsk);
        float y = heatH - 134;
        Box(canvas, 8, y, "Market Vol", $"Period {s.MarketVolSeconds:0}s", ("Ask", ask, new SKColor(60, 190, 95)), ("Bid", bid, new SKColor(215, 70, 55)));
        Box(canvas, 8, y + 66, "Avg. Liquidity", $"DOM level 1-{levels}", ("Ask", avgAsk, new SKColor(190, 60, 55)), ("Bid", avgBid, new SKColor(45, 150, 85)));
    }

    /// <summary>The last average resting size (bid, ask) over the top levels, for the delta/depth gauge.</summary>
    private (decimal Bid, decimal Ask) LastAvgLiquidity;

    private void Box(SKCanvas canvas, float x, float y, string title, string sub, (string, decimal, SKColor) a, (string, decimal, SKColor) b)
    {
        const float w = 176, h = 60;
        _fill.Color = new SKColor(20, 26, 36, 225);
        canvas.DrawRoundRect(x, y, w, h, 4, 4, _fill);
        _stroke.Color = new SKColor(255, 255, 255, 40);
        _stroke.StrokeWidth = 1;
        canvas.DrawRoundRect(x, y, w, h, 4, 4, _stroke);
        var bold = RenderContext.GetFont("Arial", 10, true, false);
        var small = RenderContext.GetFont("Arial", 9, false, false);
        _aa.Color = new SKColor(225, 230, 238);
        canvas.DrawText(title, x + 8, y + 14, bold, _aa);
        _aa.Color = new SKColor(140, 150, 165);
        canvas.DrawText(sub, x + w - 8 - RenderContext.MeasureWidth(small, sub), y + 14, small, _aa);
        decimal max = Math.Max(1e-9m, Math.Max(a.Item2, b.Item2));
        int row = 0;
        foreach (var (name, value, color) in new[] { a, b })
        {
            float ry = y + 22 + row++ * 17;
            _aa.Color = new SKColor(190, 198, 210);
            canvas.DrawText(name, x + 8, ry + 11, small, _aa);
            _fill.Color = new SKColor(40, 48, 60);
            canvas.DrawRect(x + 34, ry + 2, w - 82, 11, _fill);
            _fill.Color = color;
            canvas.DrawRect(x + 34, ry + 2, Math.Max(1, (w - 82) * (float)(value / max)), 11, _fill);
            _aa.Color = SKColors.White;
            string text = Num(value);
            canvas.DrawText(text, x + w - 8 - RenderContext.MeasureWidth(small, text), ry + 11, small, _aa);
        }
    }

    // ------------------------------------------------------------------ gauges

    /// <summary>
    /// Six dials under the ladder: hidden bid / hidden ask (volume absorbed by the icebergs found in the window, on each side), hit bid /
    /// hit ask (what sellers and buyers traded in the window), delta / depth (the window's delta relative to the average resting
    /// size) and the session CVD.
    /// </summary>
    private void DrawGauges(SKCanvas canvas, HeatmapModel m, float x, float y, float w, float h, decimal hitAsk, decimal hitBid)
    {
        if (h < 90 || w < 150) return;
        _fill.Color = new SKColor(16, 22, 30);
        canvas.DrawRect(x, y, w, h, _fill);
        decimal hidBid = 0, hidAsk = 0;
        foreach (var e in m.AllIcebergs)
        {
            if (e.Last < m.StartTime || e.Start > m.EndTime) continue;
            if (e.Ask) hidAsk += e.Volume; else hidBid += e.Volume;
        }

        decimal depth = Math.Max(1, LastAvgLiquidity.Bid + LastAvgLiquidity.Ask);
        decimal dd = (hitAsk - hitBid) / depth;
        float hs = (float)Math.Max(1, Math.Max(hidAsk, hidBid)), ts = (float)Math.Max(1, Math.Max(hitAsk, hitBid));
        float cw = w / 3, ch = h / 2, r = Math.Min(cw, ch) / 2 - 14;
        var green = new SKColor(70, 205, 110);
        var red = new SKColor(235, 90, 80);
        void G(int col, int row, string label, string value, float frac, bool signed, SKColor color) =>
            DrawGauge(canvas, x + cw * (col + 0.5f), y + ch * row + ch / 2 - 6, r, label, value, frac, signed, color);
        G(0, 0, "Hidden Bid", Num(hidBid), (float)hidBid / hs, false, green);
        G(1, 0, "Hidden Ask", Num(hidAsk), (float)hidAsk / hs, false, red);
        G(2, 0, "Delta / Depth", Num(dd, sign: true), (float)(dd / 3), true, dd >= 0 ? green : red);
        G(0, 1, "Hit Bid", Num(hitBid), (float)hitBid / ts, false, red);
        G(1, 1, "Hit Ask", Num(hitAsk), (float)hitAsk / ts, false, green);
        G(2, 1, "CVD", Num((decimal)_cvdNow, sign: true), _cvdNow / Math.Max(1e-9f, _cvdScale), true, _cvdNow >= 0 ? green : red);
    }

    private void DrawGauge(SKCanvas canvas, float cx, float cy, float r, string label, string value, float frac, bool signed, SKColor color)
    {
        var oval = new SKRect(cx - r, cy - r, cx + r, cy + r);
        _stroke.StrokeWidth = 5;
        _stroke.StrokeCap = SKStrokeCap.Round;
        _stroke.Color = new SKColor(58, 66, 80);
        canvas.DrawArc(oval, 135, 270, false, _stroke);
        _stroke.Color = color;
        frac = Math.Clamp(frac, signed ? -1f : 0f, 1f);
        if (signed) { if (Math.Abs(frac) > 0.01f) canvas.DrawArc(oval, 270, 135 * frac, false, _stroke); }
        else if (frac > 0.01f) canvas.DrawArc(oval, 135, 270 * frac, false, _stroke);
        _stroke.StrokeCap = SKStrokeCap.Butt;
        _stroke.StrokeWidth = 1;
        var bold = RenderContext.GetFont("Arial", 11, true, false);
        var small = RenderContext.GetFont("Arial", 9, false, false);
        _aa.Color = SKColors.White;
        canvas.DrawText(value, cx - RenderContext.MeasureWidth(bold, value) / 2, cy + 4, bold, _aa);
        _aa.Color = new SKColor(150, 160, 175);
        canvas.DrawText(label, cx - RenderContext.MeasureWidth(small, label) / 2, cy + r + 16, small, _aa);
    }
}
