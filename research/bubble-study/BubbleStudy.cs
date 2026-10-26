using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using TradingHub.Market;
using TradingHub.Strategies;

/// <summary>
/// Measures the "big trade = a punch" taxonomy (Skydweller) instead of trading it.
/// Every large market order is classified from the footprint of the candle it landed in, and one CSV row is written
/// per event with what price did afterwards. Nothing is bought or sold: this is the study that has to come first.
///
/// Run it over GC (Databento history) in Backtesting; the CSV lands in ~/Documents/Trading Hub/Studies.
/// </summary>
public class BubbleStudy : Strategy
{
    // ------------------------------------------------------------------ parameters (the five that matter)

    /// <summary>Percentile of recent market orders above which one counts as "big". 99.7 ≈ BigOrderDetector's default.</summary>
    public double BigPercentile { get; set; } = 99.7;

    /// <summary>How many recent orders the percentile is measured over. In GC's thin hours this spans more wall clock, which is what we want.</summary>
    public int BigSample { get; set; } = 3000;

    /// <summary>
    /// Volume in the rows beyond the print, as a fraction of the volume in the print's own row: the sheet's
    /// "12 contracts above" against the 3.8K that went in at the level, which is ~0.3%.
    /// </summary>
    public decimal FollowThrough { get; set; } = 0.05m;

    /// <summary>
    /// Ticks per footprint row. 1 = native, which is right for GC ($0.10 rows, ~40 rows in a 5-minute candle).
    /// A BTC candle spans ~1,400 ticks, so a ladder there is unreadable until the rows are grouped.
    /// </summary>
    public int RowTicks { get; set; } = 1;

    /// <summary>
    /// How many rows from the candle's extreme still counts as "at the top/bottom". The sheet means the top row or
    /// two of the ladder, so this is counted in rows: a fraction of the range is 140 rows on BTC and 4 on GC.
    /// </summary>
    public int ExtremeRows { get; set; } = 2;

    /// <summary>
    /// How close to a level still counts as "at the wall", as a fraction of the average candle range. A tick count
    /// cannot work across instruments: 8 ticks is most of a GC candle and nothing at all on BTC.
    /// </summary>
    public decimal LevelBand { get; set; } = 0.5m;

    /// <summary>Rows past the level before it counts as breached: a one-tick poke through is not a break.</summary>
    public int BreachRows { get; set; } = 1;

    /// <summary>
    /// Placebo levels per event: the same candle and the same side, but a price picked at random inside the candle's
    /// range. Without it a ratio of 0.44 means nothing, because we do not know what any old price would have scored.
    /// </summary>
    public int Placebos { get; set; } = 3;

    /// <summary>Bars to follow the level for.</summary>
    public int Horizon { get; set; } = 12;

    // ------------------------------------------------------------------ state

    private readonly BigOrders _big = new();
    private readonly List<Pending> _pending = new();
    private readonly Dictionary<decimal, decimal> _session = new();   // volume by price, this session
    private readonly Queue<decimal> _ranges = new();                  // recent candle ranges, for the relative bands
    private readonly Random _rng = new(20261026);   // fixed: the control has to be reproducible
    private Levels _walls = Levels.None;
    private StreamWriter? _csv;
    private int _events, _bars;

    protected override void OnInit()
    {
        UseFootprint = true;                 // we need volume by price
        _big.Configure(BigSample, BigPercentile);
    }

    protected override void OnTick(Trade trade) => _big.OnTrade(trade);

    protected override void OnSessionStart(DateOnly session)
    {
        if (_session.Count > 0) _walls = Levels.From(_session);
        _session.Clear();
        _big.Reset();                        // thresholds are per session: GC's overnight is a different market
    }

    protected override void OnBar(Bar bar)
    {
        Resolve(bar);                        // first settle the events still waiting on their horizon
        foreach (var kv in bar.Levels) Add(_session, kv.Key, kv.Value.Buy + kv.Value.Sell);
        _bars++;
        _ranges.Enqueue(bar.Range);
        while (_ranges.Count > 200) _ranges.Dequeue();

        foreach (var order in _big.TakeWithin(bar.Time, bar.CloseTime))
        {
            if (Classify(bar, order) is not { } c) continue;
            int wall = WallOf(c.Pattern, c.Buy);
            _pending.Add(new Pending { C = c, Bar = bar.Index, Level = c.Price, Wall = wall });

            // the same candle and the same side, but a price nobody chose: whatever this scores is the floor
            for (int k = 0; k < Placebos && bar.Range > 0; k++)
            {
                decimal at = bar.Low + (decimal)_rng.NextDouble() * bar.Range;
                _pending.Add(new Pending { C = c, Bar = bar.Index, Level = at, Wall = wall, Kind = 1 });
            }

            // the sharper control: the candle's own extreme on that side, with no big trade attached. The wick
            // readings sit at the extreme by construction, so this asks whether the bubble adds anything to it.
            _pending.Add(new Pending { C = c, Bar = bar.Index, Level = wall < 0 ? bar.High : bar.Low, Wall = wall, Kind = 2 });
        }
    }

    protected override void OnStop()
    {
        foreach (var p in _pending) Write(p, settled: false);
        _csv?.Flush();
        _csv?.Dispose();
        _csv = null;
        Log($"BubbleStudy: {_events} events over {_bars} bars ({(_bars > 0 ? (double)_events / _bars : 0):0.00} per bar).");
    }

    // ------------------------------------------------------------------ classification

    /// <summary>
    /// Six classes that cover every case, which the cheat sheet's eight collapse into. The sheet splits the two body
    /// cases by candle colour and draws the wick cases twice; it also leaves the counter-directional wicks out
    /// (a bubble in the low of a green candle), and those are the same phenomenon as the ones it does name.
    /// </summary>
    public enum Pattern
    {
        /// <summary>Big trade in the body, against where the candle closed. Effort, no result: the other side held and won.</summary>
        AbsorptionContinuation,

        /// <summary>Big trade in the body, with where the candle closed. Effort = result.</summary>
        Acceptance,

        /// <summary>Buyer at the top of the upper wick with nothing above. The move's own driver ran out.</summary>
        AbsorptionContrarianTop,

        /// <summary>Seller in the upper wick, and the wick is made of selling. Someone answered at the high.</summary>
        RejectionTop,

        /// <summary>Seller at the bottom of the lower wick with nothing below.</summary>
        AbsorptionContrarianBottom,

        /// <summary>Buyer in the lower wick, and the wick is made of buying.</summary>
        RejectionBottom,
    }

    private Classified? Classify(Bar bar, BigOrder o)
    {
        if (bar.Levels.Count == 0 || bar.Open == bar.Close) return null;   // no footprint, or a doji: no direction to be with or against

        decimal tick = TickSize;
        decimal rowSize = tick * Math.Max(1, RowTicks);
        long Row(decimal price) => (long)Math.Floor(price / rowSize);

        long printRow = Row(o.Price), highRow = Row(bar.High), lowRow = Row(bar.Low);
        long bodyHighRow = Row(Math.Max(bar.Open, bar.Close)), bodyLowRow = Row(Math.Min(bar.Open, bar.Close));
        bool upperWick = printRow > bodyHighRow;
        bool lowerWick = printRow < bodyLowRow;

        // the ladder, grouped into rows: what went in at the print's level, and what went past it afterwards
        decimal atRow = 0, beyond = 0, wickBuy = 0, wickSell = 0;
        foreach (var kv in bar.Levels)
        {
            long r = Row(kv.Key);
            decimal vol = kv.Value.Buy + kv.Value.Sell;
            if (r == printRow) atRow += vol;
            else if (o.Buy ? r > printRow : r < printRow) beyond += vol;
            if ((upperWick && r > bodyHighRow) || (lowerWick && r < bodyLowRow)) { wickBuy += kv.Value.Buy; wickSell += kv.Value.Sell; }
        }

        // "nobody followed": the rows past the print hold almost nothing next to the row the big trade went into
        decimal ratio = atRow <= 0 ? 1 : beyond / atRow;
        bool blocked = ratio < FollowThrough;
        bool withCandle = o.Buy == bar.IsBullish;

        // the sheet's wick patterns are the top row or two of the ladder, and the two of them need different proof:
        // absorption is the aggressor reaching the edge and nobody following, rejection is the wick being built by
        // the other side. "Blocked" cannot be asked of a rejection: a seller at the high has the whole body below.
        decimal wickDelta = wickBuy - wickSell;
        bool nearTop = upperWick && printRow >= highRow - ExtremeRows;
        bool nearBottom = lowerWick && printRow <= lowRow + ExtremeRows;

        bool absorbTop = nearTop && o.Buy && blocked;             // bought the high, nothing above
        bool rejectTop = nearTop && !o.Buy && wickDelta < 0;      // the upper wick is made of selling
        bool absorbBottom = nearBottom && !o.Buy && blocked;      // sold the low, nothing below
        bool rejectBottom = nearBottom && o.Buy && wickDelta > 0; // the lower wick is made of buying

        Pattern pattern;
        if (absorbTop) pattern = Pattern.AbsorptionContrarianTop;
        else if (rejectTop) pattern = Pattern.RejectionTop;
        else if (absorbBottom) pattern = Pattern.AbsorptionContrarianBottom;
        else if (rejectBottom) pattern = Pattern.RejectionBottom;
        else pattern = withCandle ? Pattern.Acceptance : Pattern.AbsorptionContinuation;

        bool atExtreme = absorbTop || rejectTop || absorbBottom || rejectBottom;

        // what the sheet says should happen next, as +1 up / -1 down, so the CSV can be scored directly
        int expect = pattern switch
        {
            Pattern.Acceptance or Pattern.AbsorptionContinuation => bar.IsBullish ? 1 : -1,
            Pattern.AbsorptionContrarianTop or Pattern.RejectionTop => -1,
            _ => 1,
        };

        decimal avgRange = _ranges.Count > 0 ? _ranges.Average() : bar.Range;
        return new Classified(
            Time: bar.CloseTime, BarIndex: bar.Index, Pattern: pattern, Expect: expect,
            Price: o.Price, Volume: o.Volume, Buy: o.Buy,
            AtExtreme: atExtreme, Blocked: blocked, Beyond: ratio,
            WickDelta: wickDelta, BarDelta: bar.Delta, Threshold: o.Threshold,
            AtWall: _walls.Near(o.Price, avgRange * LevelBand, out string which), Wall: which,
            High: bar.High, Low: bar.Low, Close: bar.Close);
    }

    // ------------------------------------------------------------------ what happened next

    /// <summary>
    /// The reading makes a claim about a price, not about a trade: "it should not get much past here". So what is
    /// followed is the level itself — whether it was breached, by how far, how soon, and whether price came back.
    /// </summary>
    private sealed class Pending
    {
        public Classified C;
        public int Bar;
        public decimal Level;        // the price the big trade marked
        public int Wall;             // -1 ceiling (should not go above), +1 floor (should not go below)
        public decimal MaxBeyond;    // furthest it got on the wrong side
        public decimal MaxHeld;      // furthest it got on the right side
        public int BreachBar = -1;   // bars after the event when it first went clearly past
        public bool Inside = true;   // on the right side at the end
        public int Kind;             // 0 real, 1 random price in the candle, 2 the candle's own edge
    }

    /// <summary>Which side of the level the reading says price should stay on. Acceptance is the opposite of absorption: the aggressor got through, so the level is behind them.</summary>
    private static int WallOf(Pattern p, bool buy) => p switch
    {
        Pattern.AbsorptionContrarianTop or Pattern.RejectionTop => -1,
        Pattern.AbsorptionContrarianBottom or Pattern.RejectionBottom => +1,
        Pattern.AbsorptionContinuation => buy ? -1 : +1,   // the aggressor was held at the level
        _ => buy ? +1 : -1,                                // Acceptance: they went through it
    };

    private void Resolve(Bar bar)
    {
        decimal breach = Math.Max(1, RowTicks) * BreachRows * (TickSize <= 0 ? 1 : TickSize);
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (bar.Index <= p.Bar) continue;

            decimal beyond = p.Wall < 0 ? bar.High - p.Level : p.Level - bar.Low;
            decimal held = p.Wall < 0 ? p.Level - bar.Low : bar.High - p.Level;
            if (beyond > p.MaxBeyond) p.MaxBeyond = beyond;
            if (held > p.MaxHeld) p.MaxHeld = held;
            if (p.BreachBar < 0 && beyond > breach) p.BreachBar = bar.Index - p.Bar;
            p.Inside = p.Wall < 0 ? bar.Close <= p.Level : bar.Close >= p.Level;

            if (bar.Index - p.Bar < Horizon) continue;
            Write(p, settled: true);
            _pending.RemoveAt(i);
        }
    }

    private void Write(Pending p, bool settled)
    {
        var c = p.C;
        _csv ??= Open();
        decimal tick = TickSize <= 0 ? 1 : TickSize;

        _csv.WriteLine(string.Join(",",
            c.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            Instrument.Symbol, c.Pattern, p.Wall < 0 ? "ceiling" : "floor", p.Kind,
            F(p.Level), F(c.Volume), c.Buy ? 1 : 0, F(c.Threshold),
            c.AtExtreme ? 1 : 0, c.Blocked ? 1 : 0, F(c.Beyond, 4),
            F(c.WickDelta), F(c.BarDelta),
            c.AtWall ? 1 : 0, c.Wall,
            F((c.High - c.Low) / tick), settled ? 1 : 0,
            p.BreachBar >= 0 ? 1 : 0,                               // did it get past the level at all
            p.BreachBar >= 0 ? p.BreachBar.ToString() : "",         // how many bars it held first
            F(p.MaxBeyond / tick),                                  // how far past, in ticks
            F(p.MaxHeld / tick),                                    // how far it ran on the right side
            p.BreachBar >= 0 && p.Inside ? 1 : 0));                 // breached, then came back
        _events++;
    }

    private StreamWriter Open()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Trading Hub", "Studies");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"bubbles-{Instrument.Symbol}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var w = new StreamWriter(file) { AutoFlush = false };
        w.WriteLine("time,symbol,pattern,side,placebo,level,volume,buy,threshold,at_extreme,blocked,beyond_ratio," +
                    "wick_delta,bar_delta,at_wall,wall,bar_range_ticks,settled," +
                    "breached,bars_held,max_beyond_ticks,max_held_ticks,came_back");
        Log($"BubbleStudy -> {file}");
        return w;
    }

    private static string F(decimal v, int d = 2) => Math.Round(v, d).ToString(CultureInfo.InvariantCulture);

    private readonly record struct Classified(
        DateTime Time, int BarIndex, Pattern Pattern, int Expect,
        decimal Price, decimal Volume, bool Buy,
        bool AtExtreme, bool Blocked, decimal Beyond,
        decimal WickDelta, decimal BarDelta, decimal Threshold,
        bool AtWall, string Wall,
        decimal High, decimal Low, decimal Close);

    private static void Add(Dictionary<decimal, decimal> d, decimal k, decimal v) => d[k] = d.TryGetValue(k, out var c) ? c + v : v;

    // ------------------------------------------------------------------ big orders (same rule the app uses)

    /// <summary>One market order: the fills that share a timestamp and a side are one aggressor sweeping the book.</summary>
    public readonly record struct BigOrder(DateTime Time, decimal Price, decimal Volume, bool Buy, decimal Threshold);

    /// <summary>
    /// Fills with the same time and side add up into one order, and "big" is above a percentile of the recent ones, so
    /// the threshold follows the instrument and the session instead of being a number we picked.
    /// </summary>
    private sealed class BigOrders
    {
        private readonly Queue<decimal> _recent = new();
        private readonly List<BigOrder> _found = new();
        private int _sample = 3000, _since, _minOrders = 200;
        private double _percentile = 99.7;
        private decimal _threshold, _vol, _pv;
        private DateTime _time;
        private TradeSide _side;
        private bool _reported;

        public decimal Threshold => _threshold;

        public void Configure(int sample, double percentile)
        {
            _sample = Math.Max(200, sample);
            _percentile = Math.Clamp(percentile, 50, 99.99);
            _minOrders = Math.Min(200, _sample / 4);
        }

        public void Reset()
        {
            Close();
            _recent.Clear();
            _threshold = 0;
            _since = 0;
        }

        public void OnTrade(Trade t)
        {
            if (t.Side == TradeSide.None || t.Volume <= 0) return;
            if (t.Time != _time || t.Side != _side) { Close(); _time = t.Time; _side = t.Side; }
            _vol += t.Volume;
            _pv += t.Price * t.Volume;
            if (_reported || _threshold <= 0 || _vol <= _threshold) return;
            _reported = true;
            _found.Add(new BigOrder(_time, _pv / _vol, _vol, _side == TradeSide.Buy, _threshold));
        }

        /// <summary>The order that was being built is finished: it joins the sample and may move the threshold.</summary>
        private void Close()
        {
            if (_vol > 0)
            {
                _recent.Enqueue(_vol);
                while (_recent.Count > _sample) _recent.Dequeue();
                if (++_since >= 250 && _recent.Count >= _minOrders) { _threshold = Percentile(); _since = 0; }
                else if (_threshold <= 0 && _recent.Count >= _minOrders) _threshold = Percentile();
            }
            _vol = _pv = 0;
            _reported = false;
        }

        private decimal Percentile()
        {
            var v = _recent.ToList();
            v.Sort();
            int i = (int)Math.Floor(_percentile / 100.0 * (v.Count - 1));
            return v[Math.Clamp(i, 0, v.Count - 1)];
        }

        /// <summary>The big orders that landed in one candle; they are handed over once.</summary>
        public List<BigOrder> TakeWithin(DateTime from, DateTime to)
        {
            Close();                                   // the last order of the candle counts too
            var inBar = _found.Where(o => o.Time >= from && o.Time < to).ToList();
            _found.RemoveAll(o => o.Time < to);
            return inBar;
        }
    }

    // ------------------------------------------------------------------ the walls

    /// <summary>Previous session's point of control and value area: where passive orders were already resting.</summary>
    private readonly record struct Levels(decimal Poc, decimal High, decimal Low, bool Ok)
    {
        public static readonly Levels None = new(0, 0, 0, false);

        public static Levels From(Dictionary<decimal, decimal> byPrice)
        {
            if (byPrice.Count == 0) return None;
            var rows = byPrice.OrderByDescending(k => k.Value).ToList();
            decimal total = rows.Sum(r => r.Value), taken = 0;
            decimal lo = decimal.MaxValue, hi = decimal.MinValue;
            foreach (var r in rows)                     // the 70% of volume around the POC
            {
                if (taken >= total * 0.70m) break;
                taken += r.Value;
                lo = Math.Min(lo, r.Key);
                hi = Math.Max(hi, r.Key);
            }
            return new Levels(rows[0].Key, hi, lo, true);
        }

        public bool Near(decimal price, decimal tolerance, out string which)
        {
            which = "";
            if (!Ok) return false;
            if (Math.Abs(price - Poc) <= tolerance) which = "poc";
            else if (Math.Abs(price - High) <= tolerance) which = "vah";
            else if (Math.Abs(price - Low) <= tolerance) which = "val";
            return which.Length > 0;
        }
    }
}
