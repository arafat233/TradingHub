using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Indicators;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

/// <summary>
/// Reads every large market order against the footprint of the candle it landed in and marks what it means:
/// exhaustion, rejection, absorbed or accepted. The two wick readings also mark a price - a ceiling or a floor -
/// and the line says whether the next candle confirmed it or broke it.
///
/// It is a reading aid, not a signal. Measured on BTC it marked levels no better than the candle's own high or
/// low did, so "Draw the bare extreme" is on by default: if the marked levels do not hold better than those
/// faint ticks, the bubble is not earning its place.
/// </summary>
[DisplayName("Big Trade Read")]
[Category("My indicators")]
public class BigTradeRead : Indicator
{
    // ------------------------------------------------------------------ what it is

    private enum Kind { ExhaustionTop, RejectionTop, ExhaustionBottom, RejectionBottom, Absorbed, Accepted }

    private sealed class Mark
    {
        public int Bar;
        public decimal Price;
        public Kind Kind;
        public bool Buy;
        public int Wall;          // -1 ceiling, +1 floor, 0 no level claimed
        public bool AtLevel;      // sits on a prior-session level
        public bool Combo;        // exhaustion and rejection on the same extreme
        public int State;         // 0 pending, 1 the next candle confirmed, -1 it broke
        public int EndBar = -1;   // where the level was finally lost
        public decimal Volume;
    }

    private readonly List<Mark> _marks = new();
    private readonly List<(DateTime Time, decimal Price, decimal Volume, bool Buy)> _orders = new();
    private readonly Queue<decimal> _recent = new();
    private readonly Dictionary<decimal, decimal> _session = new();
    private decimal _threshold, _poc, _vah, _val;
    private bool _levels;
    private int _lastDate = -1, _done = -1, _cursor;
    private bool _asked;

    public BigTradeRead() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    // ------------------------------------------------------------------ settings

    [Display(Name = "Big trade percentile", GroupName = "Detection", Order = 10)]
    [Range(90, 99.99)]
    public decimal Percentile { get; set; } = 99.7m;

    [Display(Name = "Orders in the sample", GroupName = "Detection", Order = 20)]
    [Range(200, 20000)]
    public int Sample { get; set; } = 3000;

    [Display(Name = "Ticks per footprint row", GroupName = "Detection", Order = 30,
        Description = "1 is right for GC. A BTC candle spans ~1,400 ticks, so group them or the ladder means nothing.")]
    [Range(1, 1000)]
    public int RowTicks { get; set; } = 1;

    [Display(Name = "Rows from the extreme", GroupName = "Detection", Order = 40,
        Description = "How close to the high or low still counts as 'at the top'. The sheet means the top row or two.")]
    [Range(0, 20)]
    public int ExtremeRows { get; set; } = 2;

    [Display(Name = "Follow-through under (%)", GroupName = "Detection", Order = 50,
        Description = "Volume past the print over volume in its row. Under this, nobody followed.")]
    [Range(0.1, 100)]
    public decimal FollowThrough { get; set; } = 5m;

    [Display(Name = "Level tolerance (ticks)", GroupName = "Context", Order = 60)]
    [Range(0, 500)]
    public int LevelTicks { get; set; } = 8;

    [Display(Name = "Session resets at (UTC hour)", GroupName = "Context", Order = 70)]
    [Range(0, 23)]
    public int SessionHourUtc { get; set; } = 22;

    [Display(Name = "Draw the bare extreme", GroupName = "Display", Order = 80,
        Description = "A faint tick at every candle's high and low, to compare against. If the marked levels do not hold better, the reading adds nothing.")]
    public bool ShowBareExtreme { get; set; } = true;

    [Display(Name = "Only mark at a level", GroupName = "Display", Order = 90,
        Description = "A punch in the middle of nowhere is noise.")]
    public bool OnlyAtLevel { get; set; }

    [Display(Name = "Show body reads (ABS / ACC)", GroupName = "Display", Order = 95,
        Description = "The body readings claim no level and are most of the events. Turn them off to leave only the wick reads, which are the ones that mark a price.")]
    public bool ShowBodyReads { get; set; } = true;

    [Display(Name = "Ceiling", GroupName = "Display", Order = 100)]
    public Color CeilingColor { get; set; } = Color.FromArgb(235, 235, 80, 90);

    [Display(Name = "Floor", GroupName = "Display", Order = 110)]
    public Color FloorColor { get; set; } = Color.FromArgb(235, 70, 210, 130);

    [Display(Name = "Body reads", GroupName = "Display", Order = 120)]
    public Color BodyColor { get; set; } = Color.FromArgb(180, 150, 150, 200);

    // ------------------------------------------------------------------ data

    protected override void OnRecalculate()
    {
        _marks.Clear();
        _orders.Clear();
        _recent.Clear();
        _session.Clear();
        _threshold = _poc = _vah = _val = 0;
        _levels = false;
        _lastDate = -1;
        _done = -1;
        _cursor = 0;
        _asked = false;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (!_asked && CurrentBar > 0)
        {
            _asked = true;
            RequestForCumulativeTrades(new CumulativeTradesRequest(GetCandle(0).Time));
        }

        // only closed candles are read; the forming one has no final footprint
        while (_done < bar - 1) Read(++_done);
    }

    protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> trades)
    {
        foreach (var t in trades) Take(t);
        _orders.Sort((a, b) => a.Time.CompareTo(b.Time));   // history and live can interleave
        _done = -1;
        _cursor = 0;
        _marks.Clear();
        RedrawChart();
    }

    protected override void OnCumulativeTrade(CumulativeTrade trade) => Take(trade);

    protected override void OnUpdateCumulativeTrade(CumulativeTrade trade) => Take(trade);

    /// <summary>One aggregated market order: the fills a single aggressor produced sweeping the book.</summary>
    private void Take(CumulativeTrade t)
    {
        if (t.Volume <= 0) return;
        _recent.Enqueue(t.Volume);
        while (_recent.Count > Sample) _recent.Dequeue();
        if (_recent.Count >= 200)
        {
            var v = _recent.ToList();
            v.Sort();
            int i = (int)Math.Floor((double)Percentile / 100.0 * (v.Count - 1));
            _threshold = v[Math.Clamp(i, 0, v.Count - 1)];
        }

        if (_threshold <= 0 || t.Volume <= _threshold) return;
        decimal price = (t.FirstPrice + t.Lastprice) / 2m;
        _orders.Add((t.Time, price, t.Volume, t.Direction == TradeDirection.Buy));
    }

    // ------------------------------------------------------------------ the read

    private void Read(int bar)
    {
        if (bar < 1 || bar >= CurrentBar) return;
        var c = GetCandle(bar);
        RollSession(c.Time, bar);
        if (c.Open == c.Close) return;              // a doji has no direction to be with or against

        decimal tick = InstrumentInfo?.TickSize ?? 0.01m;
        decimal rowSize = tick * Math.Max(1, RowTicks);
        long Row(decimal p) => (long)Math.Floor(p / rowSize);

        DateTime from = c.Time, to = GetCandle(bar + 1 < CurrentBar ? bar + 1 : bar).Time;
        if (to <= from) to = c.LastTime.AddTicks(1);

        var atTop = new List<Mark>();
        var atBottom = new List<Mark>();

        // bars are read in order and orders are time-sorted, so each one is only ever visited once
        while (_cursor < _orders.Count && _orders[_cursor].Time < from) _cursor++;
        for (int i = _cursor; i < _orders.Count && _orders[i].Time < to; i++)
        {
            var o = _orders[i];
            var m = Classify(c, o.Price, o.Volume, o.Buy, bar, Row, rowSize, tick);
            if (m == null) continue;
            if (OnlyAtLevel && !m.AtLevel) continue;
            _marks.Add(m);
            if (m.Kind is Kind.ExhaustionTop or Kind.RejectionTop) atTop.Add(m);
            if (m.Kind is Kind.ExhaustionBottom or Kind.RejectionBottom) atBottom.Add(m);
        }

        // exhaustion AND rejection on the same extreme is the strongest read on the sheet
        Combo(atTop);
        Combo(atBottom);
        Settle(bar);
    }

    private static void Combo(List<Mark> side)
    {
        bool exh = side.Any(m => m.Kind is Kind.ExhaustionTop or Kind.ExhaustionBottom);
        bool rej = side.Any(m => m.Kind is Kind.RejectionTop or Kind.RejectionBottom);
        if (exh && rej) foreach (var m in side) m.Combo = true;
    }

    private Mark Classify(IndicatorCandle c, decimal price, decimal volume, bool buy, int bar,
                          Func<decimal, long> Row, decimal rowSize, decimal tick)
    {
        long rowP = Row(price), rowHigh = Row(c.High), rowLow = Row(c.Low);
        long bodyHigh = Row(Math.Max(c.Open, c.Close)), bodyLow = Row(Math.Min(c.Open, c.Close));
        bool upper = rowP > bodyHigh, lower = rowP < bodyLow;

        decimal atRow = 0, beyond = 0, wickBuy = 0, wickSell = 0;
        foreach (var l in c.GetAllPriceLevels())
        {
            long r = Row(l.Price);
            if (r == rowP) atRow += l.Volume;
            else if (buy ? r > rowP : r < rowP) beyond += l.Volume;
            if ((upper && r > bodyHigh) || (lower && r < bodyLow)) { wickBuy += l.Ask; wickSell += l.Bid; }
        }

        bool blocked = atRow > 0 && beyond / atRow < FollowThrough / 100m;
        decimal wickDelta = wickBuy - wickSell;
        bool nearTop = upper && rowP >= rowHigh - ExtremeRows;
        bool nearBottom = lower && rowP <= rowLow + ExtremeRows;

        Kind kind;
        int wall;
        if (nearTop && buy && blocked) { kind = Kind.ExhaustionTop; wall = -1; }
        else if (nearTop && !buy && wickDelta < 0) { kind = Kind.RejectionTop; wall = -1; }
        else if (nearBottom && !buy && blocked) { kind = Kind.ExhaustionBottom; wall = +1; }
        else if (nearBottom && buy && wickDelta > 0) { kind = Kind.RejectionBottom; wall = +1; }
        else if (upper || lower) return null;                       // in a wick but proves nothing
        else { kind = buy == c.IsBullish ? Kind.Accepted : Kind.Absorbed; wall = 0; }

        return new Mark
        {
            Bar = bar, Price = price, Kind = kind, Buy = buy, Wall = wall, Volume = volume,
            AtLevel = NearLevel(price, LevelTicks * tick),
        };
    }

    /// <summary>The next candle decides, then the level runs until a close finally goes past it.</summary>
    private void Settle(int upTo)
    {
        foreach (var m in _marks)
        {
            if (m.Wall == 0 || m.EndBar >= 0) continue;
            for (int b = m.Bar + 1; b <= upTo; b++)
            {
                decimal close = GetCandle(b).Close;
                bool past = m.Wall < 0 ? close > m.Price : close < m.Price;
                if (b == m.Bar + 1) m.State = past ? -1 : 1;
                if (past) { m.EndBar = b; break; }
            }
        }
    }

    // ------------------------------------------------------------------ prior-session levels

    private void RollSession(DateTime time, int bar)
    {
        int day = time.AddHours(-SessionHourUtc).Date.DayOfYear;
        if (_lastDate >= 0 && day != _lastDate && _session.Count > 0) Value();
        _lastDate = day;
        foreach (var l in GetCandle(bar).GetAllPriceLevels())
            _session[l.Price] = _session.TryGetValue(l.Price, out var v) ? v + l.Volume : l.Volume;
    }

    private void Value()
    {
        var rows = _session.OrderByDescending(k => k.Value).ToList();
        decimal total = rows.Sum(r => r.Value), taken = 0, hi = decimal.MinValue, lo = decimal.MaxValue;
        foreach (var r in rows)
        {
            if (taken >= total * 0.70m) break;
            taken += r.Value;
            hi = Math.Max(hi, r.Key);
            lo = Math.Min(lo, r.Key);
        }

        _poc = rows[0].Key; _vah = hi; _val = lo; _levels = true;
        _session.Clear();
    }

    private bool NearLevel(decimal price, decimal tol) =>
        _levels && (Math.Abs(price - _poc) <= tol || Math.Abs(price - _vah) <= tol || Math.Abs(price - _val) <= tol);

    // ------------------------------------------------------------------ drawing

    private static string Tag(Kind k) => k switch
    {
        Kind.ExhaustionTop => "EXH",
        Kind.ExhaustionBottom => "EXH",
        Kind.RejectionTop => "REJ",
        Kind.RejectionBottom => "REJ",
        Kind.Absorbed => "ABS",
        _ => "ACC",
    };

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        int w = ChartInfo.PriceChartContainer.BarsWidth;
        var font = new RenderFont("Arial", 9, FontStyle.Bold);
        var small = new RenderFont("Arial", 7);

        if (ShowBareExtreme)
        {
            var faint = new RenderPen(Color.FromArgb(70, 170, 170, 190), 1);
            for (int b = Math.Max(first, 0); b <= last && b < CurrentBar; b++)
            {
                var c = GetCandle(b);
                int x = ChartInfo.GetXByBar(b, true) + w / 2;
                context.DrawLine(faint, x - 5, ChartInfo.GetYByPrice(c.High, false), x + 5, ChartInfo.GetYByPrice(c.High, false));
                context.DrawLine(faint, x - 5, ChartInfo.GetYByPrice(c.Low, false), x + 5, ChartInfo.GetYByPrice(c.Low, false));
            }
        }

        // the level is drawn at the print's price, but the label goes outside the candle: the built-in Big Trades
        // bubble already sits on that price, and two marks on one pixel is how the chart became unreadable
        var stacked = new Dictionary<(int Bar, bool Above), int>();

        foreach (var m in _marks.OrderBy(m => m.Wall == 0))   // the wick reads matter: draw them over the body ones
        {
            if (m.Bar > last) continue;
            int endBar = m.EndBar >= 0 ? m.EndBar : last;
            if (endBar < first) continue;
            if (m.Wall == 0 && !ShowBodyReads) continue;

            var col = m.Wall < 0 ? CeilingColor : m.Wall > 0 ? FloorColor : BodyColor;
            int y = ChartInfo.GetYByPrice(m.Price, false);
            int x = ChartInfo.GetXByBar(m.Bar, true) + w / 2;

            if (m.Wall != 0)
            {
                int x2 = ChartInfo.GetXByBar(Math.Min(endBar, last), true) + w;
                if (m.State == 0) Dashed(context, col, x, y, x2);
                else if (m.State > 0) context.DrawLine(new RenderPen(col, 2), x, y, x2, y);
                else Dashed(context, Color.FromArgb(90, col), x, y, x2);
                if (m.EndBar >= 0 && m.EndBar <= last)
                {
                    context.DrawLine(new RenderPen(col, 1), x2 - 5, y - 5, x2 + 5, y + 5);
                    context.DrawLine(new RenderPen(col, 1), x2 - 5, y + 5, x2 + 5, y - 5);
                }
            }

            if (m.Bar < first) continue;
            var c = GetCandle(m.Bar);
            bool above = m.Wall < 0 || (m.Wall == 0 && c.IsBearish);     // the read points away from the candle
            bool body = m.Wall == 0;

            // clear the candle, then step further out for every mark already placed on this side of this bar
            stacked.TryGetValue((m.Bar, above), out int n);
            stacked[(m.Bar, above)] = n + 1;
            int edge = ChartInfo.GetYByPrice(above ? c.High : c.Low, false);
            int ly = above ? edge - 18 - n * 15 : edge + 18 + n * 15;

            // a thin leader back to the price being talked about, so the label is never ambiguous
            context.DrawLine(new RenderPen(Color.FromArgb(body ? 60 : 110, col), 1), x, above ? ly + 10 : ly - 10, x, y);

            if (body)
            {
                // body reads claim no level and are most of the events: a small bar, not a shout
                context.DrawLine(new RenderPen(col, 2), x - 5, ly, x + 5, ly);
                context.DrawString(Tag(m.Kind), small, Color.FromArgb(190, col), x - 10, above ? ly - 13 : ly + 2);
                continue;
            }

            // a chevron, so it never reads as one of the built-in's filled circles
            var pen = new RenderPen(col, 2);
            int d = above ? 1 : -1;
            context.DrawLine(pen, x - 7, ly + 5 * d, x, ly - 4 * d);
            context.DrawLine(pen, x, ly - 4 * d, x + 7, ly + 5 * d);
            context.DrawLine(pen, x - 7, ly + 11 * d, x, ly + 2 * d);
            context.DrawLine(pen, x, ly + 2 * d, x + 7, ly + 11 * d);
            context.DrawString(Tag(m.Kind), font, col, x - 12, above ? ly - 20 : ly + 8);

            if (m.AtLevel) context.DrawEllipse(new RenderPen(col, 1), new Rectangle(x - 12, ly - 10, 24, 20));
            if (m.Combo) context.DrawEllipse(new RenderPen(col, 2), new Rectangle(x - 16, ly - 14, 32, 28));
        }
    }

    private static void Dashed(RenderContext context, Color col, int x1, int y, int x2)
    {
        var pen = new RenderPen(col, 1);
        for (int x = x1; x < x2; x += 8) context.DrawLine(pen, x, y, Math.Min(x + 4, x2), y);
    }
}
