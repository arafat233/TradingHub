using SkiaSharp;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Rendering.Context;

namespace TradingHub.Charting.Heatmap;

/// <summary>Everything configurable about the liquidity heatmap (stored with the app's settings).</summary>
public sealed class HeatmapSettings
{
    /// <summary>Marked moments of the replay, by instrument key ("feed:symbol"), in UTC.</summary>
    public Dictionary<string, List<DateTime>> Bookmarks { get; set; } = new();

    /// <summary>How much time fits on the screen.</summary>
    public double WindowMinutes { get; set; } = 5;

    /// <summary>Height in pixels of one price tick (smaller = more prices on the screen).</summary>
    public int RowPx { get; set; } = 6;

    /// <summary>1 = the usual look; higher shows smaller resting orders more brightly.</summary>
    public double Contrast { get; set; } = 1;

    public bool ShowBubbles { get; set; } = true;

    /// <summary>Smallest trade shown as a bubble (0 = automatic: only the biggest 5% of the moment).</summary>
    public decimal BubbleMinVolume { get; set; }

    public bool ShowVolume { get; set; } = true;
    public bool ShowDom { get; set; } = true;
    public bool ShowBidAsk { get; set; } = true;
    public bool ShowCvd { get; set; } = true;

    /// <summary>Marks the levels that keep refilling (iceberg orders).</summary>
    public bool ShowIcebergs { get; set; } = true;

    /// <summary>The Market Vol and Avg. Liquidity boxes in the corner of the heatmap.</summary>
    public bool ShowStats { get; set; } = true;

    /// <summary>The dials under the ladder (hidden bid/ask, hit bid/ask, delta/depth, CVD).</summary>
    public bool ShowGauges { get; set; } = true;

    /// <summary>Pale blocks over big orders that sat still for 20 seconds or more.</summary>
    public bool ShowBlocks { get; set; } = true;

    /// <summary>Market Vol: how many seconds back it adds up.</summary>
    public double MarketVolSeconds { get; set; } = 10;

    /// <summary>Avg. Liquidity: how many levels per side it averages.</summary>
    public int LiquidityLevels { get; set; } = 10;

    /// <summary>Smallest volume traded against a level to call it an iceberg (0 = automatic: 4 x the median size of a level).</summary>
    public decimal IcebergMinVolume { get; set; }

    /// <summary>Where the window was last (0 = never saved): size and position come back when it is reopened.</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
}

/// <summary>
/// Liquidity heatmap (the Bookmap style): time runs to the right, price goes up, and the color of each cell is the size of
/// what was resting in the book at that price at that moment (dark = little, teal, yellow, red = a lot). On top:
/// the best bid and ask, the last price, bubbles for the big trades, the live book as bars on the right and the volume below.
/// It needs a feed that sends depth (see <see cref="InstrumentData.DepthHistory"/>).
/// </summary>
public sealed class HeatmapModel
{
    public HeatmapModel(InstrumentData data, HeatmapSettings? settings = null)
    {
        Data = data;
        Settings = settings ?? new HeatmapSettings();
    }

    public InstrumentData Data { get; }
    public Instrument Instrument => Data.Instrument;
    public HeatmapSettings Settings { get; set; }

    /// <summary>Follows the price (it switches off when dragging; a double click switches it back on).</summary>
    public bool AutoFollow { get; set; } = true;

    /// <summary>Price (in ticks) at the middle of the screen.</summary>
    public long CenterTicks { get; set; }

    // what the last drawing used (for the mouse and for the tests)
    public SKRect HeatRect { get; internal set; }
    public long TopTicks { get; internal set; }
    public float TickPx { get; internal set; } = 6;
    public DateTime StartTime { get; internal set; }
    public DateTime EndTime { get; internal set; }

    /// <summary>Reference size of the last drawing: what counts as "a lot" (smoothed from frame to frame so the colors do not flicker).</summary>
    public float ReferenceSize { get; internal set; }

    public float YOfTicks(long ticks) => HeatRect.Top + (TopTicks - ticks) * TickPx;
    public long TicksOfY(float y) => TopTicks - (long)Math.Floor((y - HeatRect.Top) / TickPx);

    public float XOfTime(DateTime t) =>
        HeatRect.Left + (float)((t - StartTime).TotalSeconds / Math.Max(1e-6, (EndTime - StartTime).TotalSeconds)) * HeatRect.Width;

    /// <summary>Mouse position over the heatmap (null = outside): the renderer draws a crosshair and a readout there.</summary>
    public SKPoint? Hover { get; set; }

    private readonly IcebergDetector _liveIcebergs = new();

    /// <summary>Icebergs of what is on the screen (the live ones, or those of the replay).</summary>
    public IcebergDetector Icebergs => Replay?.Icebergs ?? _liveIcebergs;

    /// <summary>Add / pull / eaten events of the big resting levels (the L3-style list under the heatmap); the window feeds it.</summary>
    public BookEventDetector BookEvents { get; } = new();

    /// <summary>The recorded day being played back (null = live).</summary>
    public DepthReplay? Replay { get; private set; }

    /// <summary>Volume traded at each price since the session start (the SVP and Delta columns).</summary>
    public SessionProfile Profile { get; } = new();

    public void StartReplay(DateTime start)
    {
        if (Data.RecordingRoot == null) return;
        Replay = new DepthReplay(Data.RecordingRoot, Instrument, start);
        _cvdSession = default;
        Profile.Reset();
    }

    public void StopReplay()
    {
        Replay = null;
        _cvdSession = default;
        Profile.Reset();
    }

    public DepthHistory History => Replay?.History ?? Data.DepthHistory;
    public TradeStore Trades => Replay?.Trades ?? Data.Trades;

    /// <summary>The moment the right edge shows: the market's now, or the replay's cursor.</summary>
    public DateTime Now => Replay?.Cursor ?? Data.Feed.Now;

    /// <summary>The book at that moment.</summary>
    public DepthSnapshot Book => Replay == null ? Data.Depth : History.At(Now) ?? DepthSnapshot.Empty;

    /// <summary>Last traded price at that moment (0 = none yet).</summary>
    public decimal LastPrice
    {
        get
        {
            if (Replay == null) return Data.LastPrice;
            int i = Trades.LowerBound(Now.AddTicks(1)) - 1;
            return i >= 0 ? Trades.GetPrice(i) : 0;
        }
    }

    public void PanTicks(long delta)
    {
        AutoFollow = false;
        CenterTicks += delta;
    }

    public void Follow() => AutoFollow = true;

    /// <summary>Where the trading day starts: the CVD under the heatmap counts from there (default 00:00 UTC, like the chart).</summary>
    public SessionSettings Session { get; set; } = SessionSettings.CryptoUtc;

    private int _cvdFrom, _cvdIdx;
    private decimal _cvdSum;
    private DateTime _cvdSession;

    /// <summary>Delta (buy minus sell volume) from the session start up to <paramref name="start"/>. Incremental: the sum
    /// keeps its place between frames, so a redraw only reads the trades that arrived since the last one.</summary>
    public decimal CvdBefore(DateTime sessionStart, DateTime start)
    {
        var store = Trades;
        int from = store.LowerBound(sessionStart), to = store.LowerBound(start);
        if (to <= from) return 0;
        if (sessionStart != _cvdSession || from != _cvdFrom || _cvdIdx < from || _cvdIdx > to)
        {
            (_cvdSession, _cvdFrom, _cvdIdx, _cvdSum) = (sessionStart, from, from, 0m);
        }

        for (; _cvdIdx < to; _cvdIdx++)
            _cvdSum += store.GetSide(_cvdIdx) == TradeSide.Buy ? store.GetVolume(_cvdIdx) : -store.GetVolume(_cvdIdx);
        return _cvdSum;
    }

    /// <summary>When set, the picture stops at that moment (the book keeps recording; Resume goes back to live).</summary>
    public DateTime? FrozenAt { get; private set; }

    public void Freeze() => FrozenAt = Now;
    public void Resume() => FrozenAt = null;
}

public sealed partial class HeatmapRenderer
{
    private const int ColPx = 3;
    private const float AxisW = 62, DomW = 300, TimeAxisH = 20;
    private static readonly SKColor Background = new(4, 6, 10);
    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _aa = new() { IsAntialias = true };
    private readonly SKPaint _stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };

    // ------------------------------------------------------------------ colors

    private static readonly SKColor[] HeatLut = BuildHeatLut();

    private static SKColor[] BuildHeatLut()
    {
        (float At, byte R, byte G, byte B)[] stops =
        {
            (0.02f, 6, 10, 22), (0.15f, 14, 34, 62), (0.35f, 26, 76, 116), (0.55f, 62, 126, 168), (0.7f, 228, 200, 60), (0.85f, 236, 118, 36), (1f, 224, 48, 34),
        };
        var lut = new SKColor[256];
        for (int n = 0; n < lut.Length; n++)
        {
            float t = n / 255f;
            if (t <= 0.02f) { lut[n] = Background; continue; }
            int i = 1;
            while (i < stops.Length - 1 && t > stops[i].At) i++;
            var (a, b) = (stops[i - 1], stops[i]);
            float k = (t - a.At) / (b.At - a.At);
            lut[n] = new SKColor((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
        }

        return lut;
    }

    /// <summary>Heat ramp: nothing → navy → blue → cyan → green → yellow → orange → red (the Bookmap look) (a 256-step table: this runs once per cell).</summary>
    public static SKColor Heat(float t) => HeatLut[(int)(Math.Clamp(t, 0f, 1f) * 255f + 0.5f)];

    private static float Percentile(List<float> values, double p)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[Math.Min(values.Count - 1, (int)(values.Count * p))];
    }

    // ------------------------------------------------------------------ drawing

    public void Render(SKCanvas canvas, HeatmapModel m, float width, float height)
    {
        var s = m.Settings;
        canvas.Clear(Background);
        var inst = m.Instrument;
        var hist = m.History;

        // layout: heatmap on the left, the price axis and the live book on the right, the volume and the time at the bottom
        float domW = s.ShowDom ? DomW : 0;
        float plotW = Math.Max(40, width - AxisW - domW);
        float volH = s.ShowVolume ? Math.Max(30, height * 0.14f) : 0;
        float cvdH = s.ShowCvd ? Math.Max(40, height * 0.2f) : 0;
        float heatH = Math.Max(40, height - TimeAxisH - volH - cvdH);
        m.HeatRect = new SKRect(0, 0, plotW, heatH);
        m.TickPx = Math.Clamp(s.RowPx, 2, 40);
        int rows = Math.Clamp((int)Math.Ceiling(heatH / m.TickPx), 8, 1200);

        // time: the last N minutes up to now
        var end = m.Replay != null ? m.Now : m.FrozenAt ?? m.Now;
        var start = end - TimeSpan.FromMinutes(Math.Clamp(s.WindowMinutes, 0.5, 30));
        m.StartTime = start;
        m.EndTime = end;

        // price: it follows the last price, recentering only when it gets near the edge (so the picture does not jitter)
        decimal lastPrice = m.LastPrice;
        long last = lastPrice > 0 ? inst.ToTicks(lastPrice)
            : m.Book is { HasBid: true, HasAsk: true } d ? (d.BestBidTicks + d.BestAskTicks) / 2 : m.CenterTicks;
        if (m.AutoFollow && last != 0 && (m.CenterTicks == 0 || Math.Abs(last - m.CenterTicks) > rows * 0.3)) m.CenterTicks = last;
        if (m.CenterTicks == 0) m.CenterTicks = last;
        m.TopTicks = m.CenterTicks + rows / 2;

        if (hist.Count == 0)
        {
            if (m.Replay is { Loading: true }) { Message(canvas, width, heatH, "Loading the recorded book…", "Reading the day from disk."); DrawTimeAxis(canvas, m, plotW, heatH + volH + cvdH); return; }
            Message(canvas, width, heatH, "Waiting for the order book…", "The heatmap needs a connection that sends depth (Rithmic, dxFeed, Databento, Tradovate, Binance, Bybit or the Simulator).");
            DrawTimeAxis(canvas, m, plotW, heatH + volH + cvdH);
            return;
        }

        int cols = Math.Max(10, (int)(plotW / ColPx));
        var books = new DepthSnapshot?[cols];
        var sizes = new List<float>(4096);
        for (int c = 0; c < cols; c++)
        {
            var t = start + TimeSpan.FromTicks((long)((end - start).Ticks * ((c + 0.5) / cols)));
            int i = hist.IndexAtOrBefore(t);
            // a sample that is much older than the column means the feed stopped: it is left blank instead of smeared
            if (i < 0 || t - hist[i].Time > TimeSpan.FromSeconds(5)) continue;
            books[c] = hist[i].Book;
            foreach (var lv in hist[i].Book.Bids) if (lv.PriceTicks <= m.TopTicks && lv.PriceTicks > m.TopTicks - rows) sizes.Add((float)lv.Size);
            foreach (var lv in hist[i].Book.Asks) if (lv.PriceTicks <= m.TopTicks && lv.PriceTicks > m.TopTicks - rows) sizes.Add((float)lv.Size);
        }

        // what counts as "a lot": the 97th percentile of what is on the screen, smoothed between frames
        float target = Math.Max(1f, Percentile(sizes, 0.97));
        m.ReferenceSize = m.ReferenceSize <= 0 ? target : m.ReferenceSize * 0.85f + target * 0.15f;
        float contrast = (float)Math.Clamp(s.Contrast, 0.2, 6);

        var grid = new float[cols * rows];
        var pixels = new SKColor[cols * rows];
        Array.Fill(pixels, Background);
        for (int c = 0; c < cols; c++)
        {
            if (books[c] is not { } book) continue;
            void Paint(BookLevel[] levels)
            {
                foreach (var lv in levels)
                {
                    long row = m.TopTicks - lv.PriceTicks;
                    if (row < 0 || row >= rows) continue;
                    float t = MathF.Pow(Math.Min(1f, (float)lv.Size / m.ReferenceSize), 0.7f) * contrast;
                    pixels[row * cols + c] = Heat(t);
                    grid[row * cols + c] = (float)lv.Size;
                }
            }

            Paint(book.Bids);
            Paint(book.Asks);
        }

        using (var bmp = new SKBitmap(cols, rows, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            bmp.Pixels = pixels;
            using var image = SKImage.FromBitmap(bmp);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
            canvas.DrawImage(image, new SKRect(0, 0, cols, rows), new SKRect(0, 0, plotW, rows * m.TickPx), paint);
        }

        // keep everything below inside the heat area
        canvas.Save();
        canvas.ClipRect(m.HeatRect);
        if (s.ShowBlocks) DrawBlocks(canvas, m, grid, cols, rows, plotW);
        if (s.ShowBidAsk) DrawBestLines(canvas, m, books, cols, plotW);
        var (colPrice, volBuy, volSell, bubbles, _) = ScanTrades(m, cols, rows);
        DrawLastPrice(canvas, m, colPrice, cols, plotW);
        if (s.ShowBubbles) DrawBubbles(canvas, m, bubbles, plotW, cols);
        if (s.ShowIcebergs || s.ShowGauges) m.Icebergs.Update(m.History, m.Trades, s.IcebergMinVolume);
        if (s.ShowIcebergs) DrawIcebergs(canvas, m, rows, plotW);
        DrawTags(canvas, m);
        if (s.ShowStats) DrawStats(canvas, m, books, heatH);
        DrawHover(canvas, m);
        canvas.Restore();

        DrawPriceAxis(canvas, m, plotW, heatH);
        if (s.ShowDom)
        {
            var prof = m.Profile.Update(m.Trades, m.Session, m.EndTime);
            var startBook = books.FirstOrDefault(b => b != null);
            DrawDom(canvas, m, plotW + AxisW, heatH, rows, prof, ComputeCqc(startBook, m.Book));
        }

        if (s.ShowVolume) DrawVolume(canvas, m, volBuy, volSell, cols, plotW, heatH, volH);
        if (s.ShowCvd) DrawCvd(canvas, m, volBuy, volSell, cols, plotW, heatH + volH, cvdH);
        if (s.ShowDom && s.ShowGauges) DrawGauges(canvas, m, plotW, heatH, width - plotW, volH + cvdH, volBuy.Sum(), volSell.Sum());
        DrawTimeAxis(canvas, m, plotW, heatH + volH + cvdH);
    }

    private void Message(SKCanvas canvas, float w, float h, string title, string detail)
    {
        var big = RenderContext.GetFont("Arial", 15, true, false);
        var small = RenderContext.GetFont("Arial", 12, false, false);
        _aa.Color = new SKColor(200, 205, 215);
        canvas.DrawText(title, 24, h / 2 - 6, big, _aa);
        _aa.Color = new SKColor(130, 138, 150);
        canvas.DrawText(detail, 24, h / 2 + 16, small, _aa);
    }

    private void DrawBestLines(SKCanvas canvas, HeatmapModel m, DepthSnapshot?[] books, int cols, float plotW)
    {
        float colW = plotW / cols;
        foreach (var (color, ask) in new[] { (new SKColor(200, 210, 220, 150), false), (new SKColor(200, 210, 220, 150), true) })
        {
            using var path = new SKPath();
            bool open = false;
            float prevY = 0;
            for (int c = 0; c < cols; c++)
            {
                if (books[c] is not { } b || (ask ? !b.HasAsk : !b.HasBid)) { open = false; continue; }
                float x = (c + 0.5f) * colW;
                float y = m.YOfTicks(ask ? b.BestAskTicks : b.BestBidTicks) + m.TickPx / 2;
                if (open) { path.LineTo(x, prevY); path.LineTo(x, y); } else path.MoveTo(x, y); // steps, like Bookmap: flat until the price moves
                prevY = y;
                open = true;
            }

            _stroke.Color = color;
            _stroke.StrokeWidth = 1.3f;
            canvas.DrawPath(path, _stroke);
        }
    }

    private record struct Bubble(int Col, long Ticks, decimal Buy, decimal Sell);

    /// <summary>One pass over the trades in the window: last price and volume per column, and the volume per bubble cell.</summary>
    private (long[] LastTicks, decimal[] Buy, decimal[] Sell, List<Bubble> Bubbles, Dictionary<long, (decimal Buy, decimal Sell)> ByPrice) ScanTrades(HeatmapModel m, int cols, int rows)
    {
        var lastTicks = new long[cols];
        var buy = new decimal[cols];
        var sell = new decimal[cols];
        var cells = new Dictionary<(int, long), (decimal Buy, decimal Sell)>();
        var byPrice = new Dictionary<long, (decimal Buy, decimal Sell)>();
        var store = m.Trades;
        double span = (m.EndTime - m.StartTime).TotalSeconds;
        for (int i = store.LowerBound(m.StartTime); i < store.Count; i++)
        {
            double sec = (store.GetTime(i) - m.StartTime).TotalSeconds;
            if (sec < 0 || sec > span) continue;
            int c = Math.Min(cols - 1, (int)(sec / span * cols));
            long ticks = store.GetPriceTicks(i);
            decimal vol = store.GetVolume(i);
            lastTicks[c] = ticks;
            bool isBuy = store.GetSide(i) == TradeSide.Buy;
            if (isBuy) buy[c] += vol; else sell[c] += vol;
            byPrice.TryGetValue(ticks, out var pv);
            byPrice[ticks] = isBuy ? (pv.Buy + vol, pv.Sell) : (pv.Buy, pv.Sell + vol);
            if (ticks > m.TopTicks || ticks <= m.TopTicks - rows) continue;
            var key = (c / 2, ticks); // two columns per bubble cell: neighbouring trades add up into one bubble
            cells.TryGetValue(key, out var v);
            cells[key] = isBuy ? (v.Buy + vol, v.Sell) : (v.Buy, v.Sell + vol);
        }

        return (lastTicks, buy, sell, cells.Select(kv => new Bubble(kv.Key.Item1, kv.Key.Item2, kv.Value.Buy, kv.Value.Sell)).ToList(), byPrice);
    }

    private void DrawLastPrice(SKCanvas canvas, HeatmapModel m, long[] lastTicks, int cols, float plotW)
    {
        float colW = plotW / cols;
        using var path = new SKPath();
        bool open = false;
        long prev = 0;
        float prevY = 0;
        for (int c = 0; c < cols; c++)
        {
            if (lastTicks[c] != 0) prev = lastTicks[c];
            if (prev == 0) continue;
            float x = (c + 0.5f) * colW, y = m.YOfTicks(prev) + m.TickPx / 2;
            if (open) { path.LineTo(x, prevY); path.LineTo(x, y); } else path.MoveTo(x, y);
            prevY = y;
            open = true;
        }

        _stroke.Color = new SKColor(250, 250, 252, 245);
        _stroke.StrokeWidth = 2f;
        canvas.DrawPath(path, _stroke);
    }

    private void DrawBubbles(SKCanvas canvas, HeatmapModel m, List<Bubble> bubbles, float plotW, int cols)
    {
        if (bubbles.Count == 0) return;
        float colW = plotW / cols;
        var totals = bubbles.Select(b => (float)(b.Buy + b.Sell)).ToList();
        float max = totals.Max();
        float min = m.Settings.BubbleMinVolume > 0 ? (float)m.Settings.BubbleMinVolume : Math.Max(1f, Percentile(new List<float>(totals), 0.95));
        var font = RenderContext.GetFont("Arial", 10, true, false);
        foreach (var b in bubbles.OrderBy(b => b.Buy + b.Sell))
        {
            float total = (float)(b.Buy + b.Sell);
            if (total < min) continue;
            float r = Math.Clamp(4 + 22 * MathF.Sqrt(total / max), 4, 26);
            float x = (b.Col * 2 + 1) * colW, y = m.YOfTicks(b.Ticks) + m.TickPx / 2;
            var c = b.Buy >= b.Sell ? new SKColor(60, 205, 95) : new SKColor(232, 70, 52);
            _aa.Color = c.WithAlpha(215);
            canvas.DrawCircle(x, y, r, _aa);
            _aa.Color = SKColors.White.WithAlpha(70);
            canvas.DrawCircle(x - r * 0.3f, y - r * 0.3f, r * 0.4f, _aa);
            _stroke.Color = SKColors.White.WithAlpha(120);
            _stroke.StrokeWidth = 1;
            canvas.DrawCircle(x, y, r, _stroke);
            if (r >= 9)
            {
                _aa.Color = SKColors.White;
                string label = total >= 1000 ? $"{total / 1000:0.#}k" : total.ToString("0");
                canvas.DrawText(label, x - RenderContext.MeasureWidth(font, label) / 2, y + 3.5f, font, _aa);
            }
        }
    }

    /// <summary>Each iceberg as a box on its price (from its first to its last trade) with the volume it absorbed: green = resting buy, red = resting sell.</summary>
    private void DrawIcebergs(SKCanvas canvas, HeatmapModel m, int rows, float plotW)
    {
        var font = RenderContext.GetFont("Arial", 10, true, false);
        int n = 0;
        foreach (var e in m.Icebergs.Events)
        {
            if (e.Last < m.StartTime || e.Start > m.EndTime || e.Ticks > m.TopTicks || e.Ticks <= m.TopTicks - rows) continue;
            n++;
            float x1 = Math.Max(0, m.XOfTime(e.Start) - 4), x2 = Math.Min(plotW, Math.Max(m.XOfTime(e.Last), x1 + 10) + 4);
            float y = m.YOfTicks(e.Ticks);
            var c = e.Ask ? new SKColor(255, 90, 80) : new SKColor(70, 225, 110);
            _fill.Color = c.WithAlpha(60);
            canvas.DrawRect(x1, y - 1, x2 - x1, m.TickPx + 2, _fill);
            _stroke.Color = c;
            _stroke.StrokeWidth = 1.5f;
            canvas.DrawRect(x1, y - 1, x2 - x1, m.TickPx + 2, _stroke);
            string label = e.Volume >= 1000 ? $"{e.Volume / 1000:0.#}k" : e.Volume.ToString("0");
            _aa.Color = SKColors.White;
            canvas.DrawText(label, x2 + 3, y + m.TickPx / 2 + 3.5f, font, _aa);
        }

        if (n > 0)
        {
            _aa.Color = new SKColor(210, 215, 225);
            canvas.DrawText($"Icebergs: {n}", 8, 14, font, _aa);
        }
    }

    /// <summary>Crosshair with the price, the time and how much was resting at that price at that moment.</summary>
    private void DrawHover(SKCanvas canvas, HeatmapModel m)
    {
        if (m.Hover is not { } p || !m.HeatRect.Contains(p.X, p.Y)) return;
        long ticks = m.TicksOfY(p.Y);
        var time = m.StartTime + TimeSpan.FromTicks((long)((m.EndTime - m.StartTime).Ticks * ((p.X - m.HeatRect.Left) / m.HeatRect.Width)));
        decimal size = 0;
        var hist = m.History;
        int i = hist.IndexAtOrBefore(time);
        if (i >= 0 && time - hist[i].Time <= TimeSpan.FromSeconds(5))
            foreach (var lv in hist[i].Book.Bids.Concat(hist[i].Book.Asks)) if (lv.PriceTicks == ticks) size = lv.Size;

        _stroke.Color = new SKColor(255, 255, 255, 70);
        _stroke.StrokeWidth = 1;
        float y = m.YOfTicks(ticks) + m.TickPx / 2;
        canvas.DrawLine(m.HeatRect.Left, y, m.HeatRect.Right, y, _stroke);
        canvas.DrawLine(p.X, m.HeatRect.Top, p.X, m.HeatRect.Bottom, _stroke);

        string text = $"{m.Instrument.FormatPrice(m.Instrument.FromTicks(ticks))}  ·  {TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(time, DateTimeKind.Utc), TimeZoneInfo.Local):HH:mm:ss}  ·  {(size > 0 ? size.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "–")} resting";
        var font = RenderContext.GetFont("Arial", 11, false, false);
        float w = RenderContext.MeasureWidth(font, text) + 12;
        float bx = p.X + 14 + w > m.HeatRect.Right ? p.X - 14 - w : p.X + 14;
        float by = Math.Clamp(p.Y - 28, m.HeatRect.Top + 4, m.HeatRect.Bottom - 24);
        _fill.Color = new SKColor(20, 22, 28, 230);
        canvas.DrawRect(bx, by, w, 20, _fill);
        _aa.Color = new SKColor(225, 230, 238);
        canvas.DrawText(text, bx + 6, by + 14, font, _aa);
    }

    private void DrawPriceAxis(SKCanvas canvas, HeatmapModel m, float x, float heatH)
    {
        var font = RenderContext.GetFont("Arial", 11, false, false);
        _fill.Color = new SKColor(16, 17, 22);
        canvas.DrawRect(x, 0, AxisW, heatH, _fill);
        // a label about every 28 px, on a round number of ticks (1, 2, 5, 10, 20, 50…)
        double perLabel = Math.Max(1, 28 / m.TickPx);
        long step = 1;
        foreach (long nice in new long[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000, 10000 })
        {
            step = nice;
            if (nice >= perLabel) break;
        }

        _aa.Color = new SKColor(150, 158, 172);
        for (long t = m.TopTicks - (m.TopTicks % step); t > m.TopTicks - (long)Math.Ceiling(heatH / m.TickPx); t -= step)
        {
            float y = m.YOfTicks(t) + m.TickPx / 2;
            if (y < 8 || y > heatH - 4) continue;
            canvas.DrawText(m.Instrument.FormatPrice(m.Instrument.FromTicks(t)), x + 6, y + 4, font, _aa);
        }

        if (m.LastPrice > 0)
        {
            long lt = m.Instrument.ToTicks(m.LastPrice);
            float y = m.YOfTicks(lt) + m.TickPx / 2;
            if (y >= 0 && y <= heatH)
            {
                _fill.Color = new SKColor(70, 200, 110);
                canvas.DrawRect(x, y - 9, AxisW, 18, _fill);
                _aa.Color = SKColors.Black;
                canvas.DrawText(m.Instrument.FormatPrice(m.LastPrice), x + 6, y + 4, RenderContext.GetFont("Arial", 11, true, false), _aa);
            }
        }
    }

    /// <summary>Cumulative delta since the session start (it starts from what traded before the left edge, and starts over at a new session).</summary>
    private void DrawCvd(SKCanvas canvas, HeatmapModel m, decimal[] buy, decimal[] sell, int cols, float plotW, float top, float h)
    {
        _fill.Color = new SKColor(14, 19, 26);
        canvas.DrawRect(0, top, plotW, h, _fill);
        var sessionStart = m.Session.SessionStartUtc(m.Session.SessionDate(m.EndTime));
        double span = (m.EndTime - m.StartTime).TotalSeconds;
        int reset = sessionStart > m.StartTime ? (int)Math.Ceiling((sessionStart - m.StartTime).TotalSeconds / span * cols) : -1;
        var cum = new decimal[cols];
        decimal run = reset >= 0 ? 0 : m.CvdBefore(sessionStart, m.StartTime), lo = decimal.MaxValue, hi = decimal.MinValue;
        for (int c = 0; c < cols; c++)
        {
            if (c == reset) run = 0;
            run += buy[c] - sell[c];
            cum[c] = run;
            lo = Math.Min(lo, run);
            hi = Math.Max(hi, run);
        }

        _cvdNow = (float)cum[^1];
        _cvdScale = (float)Math.Max(Math.Abs(hi), Math.Abs(lo));
        if (hi == lo) return;
        float colW = plotW / cols;
        float Y(decimal v) => top + 6 + (float)((hi - v) / (hi - lo)) * (h - 12);
        _stroke.StrokeWidth = 1;
        if (lo < 0 && hi > 0)
        {
            _stroke.Color = new SKColor(255, 255, 255, 50);
            canvas.DrawLine(0, Y(0), plotW, Y(0), _stroke);
        }

        using var path = new SKPath();
        for (int c = 0; c < cols; c++)
        {
            float px = (c + 0.5f) * colW, py = Y(cum[c]);
            if (c == 0 || c == reset) path.MoveTo(px, py); else path.LineTo(px, py);
        }

        _stroke.Color = new SKColor(245, 245, 250);
        _stroke.StrokeWidth = 1.5f;
        canvas.DrawPath(path, _stroke);
        _aa.Color = new SKColor(150, 160, 175);
        var font = RenderContext.GetFont("Arial", 10, true, false);
        canvas.DrawText($"CVD (session)  {cum[^1]:+0;-0;0}", 6, top + 14, font, _aa);
    }

    private void DrawVolume(SKCanvas canvas, HeatmapModel m, decimal[] buy, decimal[] sell, int cols, float plotW, float top, float volH)
    {
        float colW = plotW / cols;
        decimal max = 0;
        for (int c = 0; c < cols; c++) max = Math.Max(max, buy[c] + sell[c]);
        _fill.Color = new SKColor(14, 15, 19);
        canvas.DrawRect(0, top, plotW, volH, _fill);
        if (max <= 0) return;
        for (int c = 0; c < cols; c++)
        {
            float h = (float)((buy[c] + sell[c]) / max) * (volH - 4);
            if (h < 0.5f) continue;
            _fill.Color = buy[c] >= sell[c] ? new SKColor(80, 200, 130, 220) : new SKColor(225, 80, 100, 220);
            canvas.DrawRect(c * colW, top + volH - h, Math.Max(1, colW - 0.5f), h, _fill);
        }
    }

    private void DrawTimeAxis(SKCanvas canvas, HeatmapModel m, float plotW, float top)
    {
        _fill.Color = new SKColor(16, 17, 22);
        canvas.DrawRect(0, top, plotW + AxisW + DomW, TimeAxisH, _fill);
        var font = RenderContext.GetFont("Arial", 10, false, false);
        double span = (m.EndTime - m.StartTime).TotalSeconds;
        double step = new[] { 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800 }.FirstOrDefault(v => span / v <= plotW / 80, 1800);
        _aa.Color = new SKColor(140, 148, 162);
        var tz = TimeZoneInfo.Local;
        double firstEpoch = Math.Ceiling((m.StartTime - DateTime.UnixEpoch).TotalSeconds / step) * step;
        for (double e = firstEpoch; ; e += step)
        {
            var t = DateTime.UnixEpoch.AddSeconds(e);
            if (t > m.EndTime) break;
            float x = m.XOfTime(t);
            if (x < 4 || x > plotW - 30) continue;
            canvas.DrawText(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), tz).ToString("HH:mm:ss"), x - 18, top + 14, font, _aa);
        }
    }
}
