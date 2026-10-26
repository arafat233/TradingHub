using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Attributes;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Big Trades: marks the large aggressive orders with bubbles (every fill of the same
/// market order are added up, even if it swept several levels). Blue = buy, red = sell.
/// The threshold can be fixed or adaptive (percentile of the recent trades).
/// </summary>
[DisplayName("Big Trades (large trades)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class BigTrades : Indicator
{
    public enum ThresholdMode
    {
        [Display(Name = "Fixed")] Fixed,
        [Display(Name = "Adaptive (percentile)")] Adaptive,
    }

    public enum PriceAnchor
    {
        [Display(Name = "The order's average price")] Average,
        [Display(Name = "The order's last price")] Last,
        [Display(Name = "The order's first price")] First,
    }

    private sealed class Bubble
    {
        public int Bar;
        public decimal Price;
        public decimal Volume;
        public bool Buy;
        public DateTime Time;
        /// <summary>Price has traded back to this level since the order (the level is used up).</summary>
        public bool Touched;
        /// <summary>When price first came back (candle time).</summary>
        public DateTime TouchTime;
        /// <summary>After the touch: 0 = not decided yet, 1 = held (price moved away on the order's side), 2 = broke (it went through).</summary>
        public int Outcome;
        /// <summary>Restored from the save file (it only gets a bubble if it passes today's threshold).</summary>
        public bool FromDisk;
        /// <summary>The user hid the level's line (the bubble stays).</summary>
        public bool Hidden;
        /// <summary>The user deleted it: no bubble, no line, no list row (and it stays gone when the history is read again).</summary>
        public bool Deleted;
        /// <summary>No alert for this level (Ctrl + click in the list).</summary>
        public bool Muted;
        /// <summary>The approach warning was already given (not saved: it only matters during this run).</summary>
        public bool Approached;
    }

    private DateTime _lastSave;
    private readonly List<Bubble> _bubbles = new();
    private readonly Queue<decimal> _recent = new();
    private decimal _adaptiveThreshold;
    private int _sinceRecalc;
    private bool _requested;
    private sealed class Shadow
    {
        public Bubble Level = null!;
        public DateTime EntryTime;
        public decimal Entry;
        public bool Long;
        public bool Live;
        public int Bars;
    }

    private readonly List<Shadow> _shadowOpen = new();
    private readonly List<ShadowStore.Rec> _shadowDone = new();
    private List<Bubble> _listRows = new();
    private readonly HoverTip _tip = new();
    private int _sessStart, _prevStart = -1;
    private DateTime _alertDay;
    private int _alertsToday;
    private decimal _budgetFloor;
    private DateTime _floorAt = DateTime.MinValue;
    private (int Prev, int Start) _prevFor = (-1, -1);
    private List<(string Name, decimal Price)> _prevLevels = new();
    private bool _ready; // history is in: touches seen from now on are live ones
    private CumulativeTrade _liveTrade;
    private Bubble _liveBubble;

    private ThresholdMode _mode = ThresholdMode.Adaptive;
    private decimal _minVolume = 5;
    private decimal _percentile = 99.7m;
    private int _sample = 5000;
    private PriceAnchor _anchor = PriceAnchor.Average;

    public BigTrades() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        DrawAbovePrice = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    // ------------------------------------------------------------------ parameters

    [Display(Name = "Threshold type", GroupName = "Filter", Order = 10)]
    public ThresholdMode Mode
    {
        get => _mode;
        set { _mode = value; RecalculateValues(); }
    }

    [Display(Name = "Minimum volume (fixed)", GroupName = "Filter", Order = 20,
        Description = "The order's minimum size for it to be drawn when the threshold is fixed.")]
    [Range(0, 100000000)]
    public decimal MinVolume
    {
        get => _minVolume;
        set { _minVolume = Math.Max(0, value); RecalculateValues(); }
    }

    [Display(Name = "Percentile (adaptive)", GroupName = "Filter", Order = 30,
        Description = "Only orders larger than this percentile of the recent ones are marked (99.5 = the largest 0.5%).")]
    [Range(50, 99.999)]
    public decimal Percentile
    {
        get => _percentile;
        set { _percentile = Math.Clamp(value, 50, 99.999m); RecalculateValues(); }
    }

    [Display(Name = "Sample (recent orders)", GroupName = "Filter", Order = 40)]
    [Range(100, 200000)]
    public int Sample
    {
        get => _sample;
        set { _sample = Math.Clamp(value, 100, 200000); RecalculateValues(); }
    }

    [Display(Name = "Placement on the price", GroupName = "Filter", Order = 50)]
    public PriceAnchor Anchor
    {
        get => _anchor;
        set { _anchor = value; RecalculateValues(); }
    }

    private WhaleMode _detect = WhaleMode.Aggressive;

    [Display(Name = "Detect", GroupName = "Filter", Order = 5,
        Description = "Aggressive: any big market order. Sweep: a big order that ate 3+ price levels. Level: a big order near a level that matters (yesterday's levels, remembered levels). Big print: one single fill that big (not added up).")]
    public WhaleMode Detect
    {
        get => _detect;
        set { _detect = value; RecalculateValues(); }
    }

    [Display(Name = "Summary line", GroupName = "Display", Order = 170,
        Description = "A sentence at the top of the chart about the whale orders in view: price change, whale net in money and whether they lean one way.")]
    public bool ShowSummary { get; set; } = true;

    [Display(Name = "Whale average price", GroupName = "Display", Order = 175,
        Description = "A line at the average price of the big buys and one for the big sells in view.")]
    public bool ShowAverage { get; set; }

    [Display(Name = "Whale zones", GroupName = "Display", Order = 180,
        Description = "Shades the price bands where three or more big orders of the same side clustered.")]
    public bool ShowZones { get; set; }

    [Display(Name = "Buy color", GroupName = "Display", Order = 100)]
    public Color BuyColor { get; set; } = Color.FromArgb(150, 50, 140, 230);

    [Display(Name = "Sell color", GroupName = "Display", Order = 110)]
    public Color SellColor { get; set; } = Color.FromArgb(150, 232, 70, 70);

    [Display(Name = "Minimum size (px)", GroupName = "Display", Order = 120)]
    [Range(2, 100)]
    public int MinSize { get; set; } = 10;

    [Display(Name = "Maximum size (px)", GroupName = "Display", Order = 130)]
    [Range(4, 300)]
    public int MaxSize { get; set; } = 60;

    [Display(Name = "Show volume", GroupName = "Display", Order = 140)]
    public bool ShowVolume { get; set; } = true;

    /// <summary>What the label of a bubble says.</summary>
    public enum BubbleLabel
    {
        [Display(Name = "Size")] Size,
        [Display(Name = "Money (price × size × contract value)")] Dollars,
    }

    [Display(Name = "Bubble label", GroupName = "Display", Order = 145,
        Description = "What the bubble says: its size, or what it is worth in money (price × size × the contract's value), written above the bubble like $1.2M.")]
    public BubbleLabel LabelAs { get; set; } = BubbleLabel.Dollars;

    [Display(Name = "Border", GroupName = "Display", Order = 150)]
    public bool ShowBorder { get; set; } = true;

    [Display(Name = "Remember levels", GroupName = "Levels", Order = 200,
        Description = "Saves every big trade to disk per instrument and draws its price as a line to the right, so the levels are still there after a restart or on older history.")]
    public bool RememberLevels { get; set; } = true;

    [Display(Name = "Level line opacity", GroupName = "Levels", Order = 210)]
    [Range(0, 255)]
    public int LevelOpacity { get; set; } = 70;

    [Display(Name = "Min volume for a line", GroupName = "Levels", Order = 212,
        Description = "Only trades at least this big get a level line (0 = all of them). The bubbles are not affected.")]
    [Range(0, 100000000)]
    public decimal LevelMinVolume { get; set; }

    [Display(Name = "Max lines shown", GroupName = "Levels", Order = 214,
        Description = "Draws only the largest N lines in view (0 = no limit).")]
    [Range(0, 5000)]
    public int MaxLines { get; set; } = 20;

    [Display(Name = "Hide touched levels", GroupName = "Levels", Order = 216,
        Description = "A level is used up once price trades back to it; its line is then no longer drawn (the bubble stays).")]
    public bool HideTouched { get; set; } = true;

    [Display(Name = "Alert when price touches a level", GroupName = "Levels", Order = 217,
        Description = "A toast and sound the first time live price reaches an untouched level.")]
    public bool AlertOnTouch { get; set; } = true;

    [Display(Name = "Label the lines", GroupName = "Levels", Order = 218)]
    public bool LabelLines { get; set; } = true;

    [Display(Name = "Show level list", GroupName = "Levels", Order = 219,
        Description = "A small list on the chart with the newest remembered levels: side, price, size and whether price has been back to it.")]
    public bool ShowList { get; set; }

    [Display(Name = "Rows in the list", GroupName = "Levels", Order = 219)]
    [Range(3, 30)]
    public int ListRows { get; set; } = 10;

    [Display(Name = "Group levels within (ticks)", GroupName = "Levels", Order = 215,
        Description = "Two or more levels this close together are drawn thicker and labelled xN (a stronger zone). 0 = off.")]
    [Range(0, 200)]
    public int ConfluenceTicks { get; set; } = 4;

    [Display(Name = "Match previous-day levels (ticks)", GroupName = "Levels", Order = 216,
        Description = "A level this close to yesterday's high, low, close, POC, VAH or VAL, or to today's open, gets the name added to its label (for example B 142 @PDH). 0 = off.")]
    [Range(0, 200)]
    public int AlignTicks { get; set; } = 6;

    [Display(Name = "Alert: minimum size", GroupName = "Alerts", Order = 300, Description = "Levels smaller than this never alert (0 = no limit).")]
    [Range(0, 100000000)]
    public decimal AlertMinVolume { get; set; }

    [Display(Name = "Alert: quiet hours", GroupName = "Alerts", Order = 310, Description = "No alerts between the two hours below (your local time).")]
    public bool QuietHours { get; set; }

    [Display(Name = "Quiet from (hour, 0-23)", GroupName = "Alerts", Order = 320)]
    [Range(0, 23)]
    public int QuietFrom { get; set; } = 22;

    [Display(Name = "Quiet until (hour, 0-23)", GroupName = "Alerts", Order = 330)]
    [Range(0, 23)]
    public int QuietTo { get; set; } = 7;

    [Display(Name = "Held / broke after (ticks)", GroupName = "Levels", Order = 217,
        Description = "After price touches a level it counts as HELD when it then moves this many ticks away on the side of the original order (up for a buy), and as BROKE when it moves this many ticks through the level. Used by the Today report.")]
    [Range(1, 500)]
    public int OutcomeTicks { get; set; } = 8;

    [Display(Name = "Alert when price is within (ticks)", GroupName = "Alerts", Order = 305,
        Description = "A first warning the first time live price comes this close to an untouched level (0 = no warning, only the touch alert).")]
    [Range(0, 500)]
    public int ApproachTicks { get; set; } = 8;

    [Display(Name = "Show quality score", GroupName = "Levels", Order = 222, Description = "Adds Q and a 0-100 score to each level label (size, nearby levels, and how often levels have held).")]
    public bool ShowScore { get; set; } = true;

    [Display(Name = "List by quality (not newest first)", GroupName = "Levels", Order = 223)]
    public bool ListByScore { get; set; }

    [Display(Name = "Keep levels that held", GroupName = "Levels", Order = 224,
        Description = "With 'Hide touched levels' on, levels that held after price came back stay on the chart as a dashed line (they proved themselves); the rest disappear.")]
    public bool KeepHeld { get; set; } = true;

    [Display(Name = "Show bubbles", GroupName = "Display", Order = 160)]
    public bool ShowBubbles { get; set; } = true;

    public enum PresetKind
    {
        [Display(Name = "Choose…")] None,
        [Display(Name = "Clean")] Clean,
        [Display(Name = "Full")] Full,
        [Display(Name = "Levels only")] LevelsOnly,
    }

    /// <summary>Picking a preset sets the display options below and goes back to "Choose…" (it is never saved).</summary>
    [Display(Name = "Apply preset", GroupName = "Presets", Order = 1,
        Description = "Clean: a few lines. Full: every level with labels and the list. Levels only: lines without bubbles.")]
    public PresetKind Preset
    {
        get => PresetKind.None;
        set
        {
            switch (value)
            {
                case PresetKind.Clean:
                    MaxLines = 10; LabelLines = false; ShowList = false; ShowBubbles = true; HideTouched = true; LevelOpacity = 60; break;
                case PresetKind.Full:
                    MaxLines = 60; LabelLines = true; ShowList = true; ListRows = 12; ShowBubbles = true; HideTouched = false; LevelOpacity = 90; break;
                case PresetKind.LevelsOnly:
                    MaxLines = 30; LabelLines = true; ShowList = false; ShowBubbles = false; HideTouched = true; LevelOpacity = 90; break;
            }

            if (value != PresetKind.None) RedrawChart();
        }
    }

    [Display(Name = "Mute alerts around events", GroupName = "Alerts", Order = 340,
        Description = "No alerts this many minutes before and after an event in events.txt (the Event markers indicator and Settings explain the file).")]
    public bool MuteAroundEvents { get; set; } = true;

    [Display(Name = "Minutes around an event", GroupName = "Alerts", Order = 350)]
    [Range(1, 240)]
    public int EventMinutes { get; set; } = 15;

    [Display(Name = "Journal the result of each level", GroupName = "Levels", Order = 230,
        Description = "When a level held or broke after price came back (live only), a line with what price did is added to today's journal.")]
    public bool AutopsyToJournal { get; set; } = true;

    [Display(Name = "Shadow trader (paper)", GroupName = "Shadow trader", Order = 400,
        Description = "Fades the first return of price to every level on paper (a buy level is bought, a sell level is sold) with the stop and target below, and records the result. The Today window shows the totals. It assumes a fill at the level price, which is optimistic.")]
    public bool ShadowTrades { get; set; } = true;

    [Display(Name = "Shadow stop (ticks)", GroupName = "Shadow trader", Order = 410)]
    [Range(1, 1000)]
    public int ShadowStopTicks { get; set; } = 20;

    [Display(Name = "Shadow target (ticks)", GroupName = "Shadow trader", Order = 420)]
    [Range(1, 2000)]
    public int ShadowTargetTicks { get; set; } = 40;

    [Display(Name = "Shadow: close after (bars)", GroupName = "Shadow trader", Order = 430)]
    [Range(1, 1000)]
    public int ShadowMaxBars { get; set; } = 60;

    [Display(Name = "Alerts per day (budget)", GroupName = "Alerts", Order = 360,
        Description = "At most this many alerts a day (touch and approach together). With a budget, only the largest levels alert: the size floor is set from how many levels were touched per day recently so the budget is spread over the day instead of used up in the first hour. 0 = no limit.")]
    [Range(0, 500)]
    public int AlertBudget { get; set; }

    [Display(Name = "Max saved levels", GroupName = "Levels", Order = 220)]
    [Range(10, 20000)]
    public int MaxSaved { get; set; } = 2000;

    /// <summary>Current threshold (to show in the UI).</summary>
    [Browsable(false)]
    public decimal CurrentThreshold => _mode == ThresholdMode.Fixed ? _minVolume : _adaptiveThreshold;

    // ------------------------------------------------------------------ calculation

    protected override void OnRecalculate()
    {
        _bubbles.Clear();
        _recent.Clear();
        _adaptiveThreshold = 0;
        _requested = false;
        _ready = false;
        _shadowOpen.Clear();
        _shadowDone.Clear();
        _sessStart = 0;
        _prevStart = -1;
        _prevFor = (-1, -1);
        _prevLevels.Clear();
        _liveTrade = null;
        _liveBubble = null;
        _lastSave = default;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0 && !_requested && CurrentBar > 0)
        {
            _requested = true;
            RequestForCumulativeTrades(new CumulativeTradesRequest(GetCandle(0).Time));
        }

        if (bar > 0 && IsNewSession(bar))
        {
            _prevStart = _sessStart;
            _sessStart = bar;
        }

        if (AlignTicks > 0 && bar == CurrentBar - 1 && _prevStart >= 0 && _prevFor != (_prevStart, _sessStart))
        {
            _prevFor = (_prevStart, _sessStart);
            _prevLevels = PrevSession.Levels(GetCandle, _prevStart, _sessStart, 70);
        }

        if (_ready && bar == CurrentBar - 1)
        {
            CheckTouches(bar, live: true);
            CheckApproach(bar, silent: false);
        }

        if (_shadowDone.Count > 0 && bar == CurrentBar - 1) FlushShadow();
    }

    /// <summary>Marks the untouched levels that the candle's range reaches (a level is not touched by its own bar).</summary>
    private void CheckTouches(int bar, bool live)
    {
        var c = GetCandle(bar);
        decimal lo = c.Low, hi = c.High;
        decimal tick = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m), move = OutcomeTicks * tick;
        if (ShadowTrades) StepShadow(c, tick);
        foreach (var b in _bubbles)
        {
            if (b.Touched)
            {
                // candles after the touching one decide whether the level held or broke (both inside one candle = undecided)
                if (b.Outcome == 0 && c.Time > b.TouchTime)
                {
                    bool up = hi - b.Price >= move, down = b.Price - lo >= move;
                    if (up != down)
                    {
                        b.Outcome = up == b.Buy ? 1 : 2;
                        if (live && AutopsyToJournal) WriteAutopsy(b, (up ? hi - b.Price : b.Price - lo) / tick, (up ? b.Price - lo : hi - b.Price) / tick, c.Time);
                    }
                }

                continue;
            }

            if (b.Bar >= bar || b.Price < lo || b.Price > hi) continue;
            b.Touched = true;
            b.TouchTime = c.Time;
            if (ShadowTrades && !b.Deleted) _shadowOpen.Add(new Shadow { Level = b, EntryTime = c.Time, Entry = b.Price, Long = b.Buy, Live = live });
            if (live && AlertOnTouch && AlertAllowed(b))
                AddAlert("alert1", $"Big trade level touched: {(b.Buy ? "buy" : "sell")} {VolumeFormat.Compact(b.Volume, 0)} @ {b.Price}");
        }
    }

    /// <summary>First warning when price gets close to an untouched level. Silent = only remember it (levels already near when the chart opened).</summary>
    private void CheckApproach(int bar, bool silent)
    {
        if (ApproachTicks <= 0) return;
        decimal price = GetCandle(bar).Close, reach = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m) * ApproachTicks;
        foreach (var b in _bubbles)
        {
            if (b.Touched || b.Approached || b.Deleted || Math.Abs(price - b.Price) > reach) continue;
            b.Approached = true;
            if (!silent && AlertOnTouch && AlertAllowed(b))
                AddAlert("alert1", $"Price is near a big trade level: {(b.Buy ? "buy" : "sell")} {VolumeFormat.Compact(b.Volume, 0)} @ {b.Price} (now {price})");
        }
    }

    /// <summary>Muted, too small, in quiet hours, or over the daily budget: no alert. Counts the alert when it is allowed.</summary>
    private bool AlertAllowed(Bubble b)
    {
        if (b.Muted || b.Volume < AlertMinVolume || InQuietHours()) return false;
        if (AlertBudget <= 0) return true;
        var today = DateTime.Now.Date;
        if (today != _alertDay)
        {
            _alertDay = today;
            _alertsToday = 0;
        }

        if (_alertsToday >= AlertBudget || b.Volume < BudgetFloor()) return false;
        _alertsToday++;
        return true;
    }

    /// <summary>
    /// The size above which about <see cref="AlertBudget"/> levels a day were touched over the last week: alerting only on those
    /// keeps the budget for the levels that matter. Recomputed at most every five minutes.
    /// </summary>
    private decimal BudgetFloor()
    {
        if (DateTime.UtcNow - _floorAt < TimeSpan.FromMinutes(5)) return _budgetFloor;
        _floorAt = DateTime.UtcNow;
        var since = DateTime.UtcNow.AddDays(-7);
        var touched = _bubbles.Where(x => !x.Deleted && x.Touched && x.TouchTime > since).ToList();
        int days = Math.Max(1, touched.Select(x => x.TouchTime.Date).Distinct().Count());
        var sizes = touched.Select(x => x.Volume).OrderByDescending(v => v).ToList();
        int keep = AlertBudget * days;
        _budgetFloor = sizes.Count > keep ? sizes[keep - 1] : 0;
        return _budgetFloor;
    }

    private bool InQuietHours()
    {
        if (MuteAroundEvents && EventCalendar.Near(DateTime.UtcNow, TimeSpan.FromMinutes(EventMinutes), TimeSpan.FromMinutes(EventMinutes)) != null) return true;
        if (!QuietHours) return false;
        int h = DateTime.Now.Hour;
        return QuietFrom <= QuietTo ? h >= QuietFrom && h < QuietTo : h >= QuietFrom || h < QuietTo;
    }

    /// <summary>One journal line when a level's result is known: what price did after coming back.</summary>
    private void WriteAutopsy(Bubble b, decimal ticksOne, decimal ticksOther, DateTime candleTime)
    {
        if (MarkStore.Skip(InstrumentInfo?.Instrument)) return;
        bool held = b.Outcome == 1;
        var touched = DateTime.SpecifyKind(b.TouchTime, DateTimeKind.Utc).ToLocalTime();
        var placed = DateTime.SpecifyKind(b.Time, DateTimeKind.Utc).ToLocalTime();
        string text = held
            ? $"{(b.Buy ? "Buy" : "Sell")} {b.Volume:0.##} @ {b.Price} (placed {placed:dd/MM HH:mm}) was touched at {touched:HH:mm} and HELD: price moved {ticksOne:0.#} ticks away from it (and {ticksOther:0.#} the other way in that candle)."
            : $"{(b.Buy ? "Buy" : "Sell")} {b.Volume:0.##} @ {b.Price} (placed {placed:dd/MM HH:mm}) was touched at {touched:HH:mm} and BROKE: price went {ticksOne:0.#} ticks through it (and {ticksOther:0.#} the other way in that candle).";
        JournalWriter.Append(DateTime.Now, $"## {DateTime.Now:HH:mm} · level result · {InstrumentInfo?.Instrument}{Environment.NewLine}{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
    }

    /// <summary>Moves the open paper trades along with a candle (the stop is assumed hit first if one candle reaches both).</summary>
    private void StepShadow(IndicatorCandle c, decimal tick)
    {
        for (int i = _shadowOpen.Count - 1; i >= 0; i--)
        {
            var t = _shadowOpen[i];
            if (c.Time <= t.EntryTime) continue; // the touching candle itself: the order inside it is unknown
            t.Bars++;
            decimal stop = ShadowStopTicks * tick, target = ShadowTargetTicks * tick;
            decimal sign = t.Long ? 1 : -1;
            decimal worst = t.Long ? t.Entry - c.Low : c.High - t.Entry, best = t.Long ? c.High - t.Entry : t.Entry - c.Low;
            decimal ticks;
            string reason;
            if (worst >= stop) { ticks = -ShadowStopTicks; reason = "stop"; }
            else if (best >= target) { ticks = ShadowTargetTicks; reason = "target"; }
            else if (t.Bars >= ShadowMaxBars) { ticks = sign * (c.Close - t.Entry) / tick; reason = "time"; }
            else continue;
            _shadowOpen.RemoveAt(i);
            _shadowDone.Add(new ShadowStore.Rec(t.Level.Time, t.EntryTime, c.Time, t.Long, t.Entry, Math.Round(ticks, 1), reason, t.Live));
        }
    }

    private void FlushShadow()
    {
        if (_shadowDone.Count == 0) return;
        ShadowStore.Merge(InstrumentInfo?.Instrument ?? "", _shadowDone.ToList());
        _shadowDone.Clear();
    }

    /// <summary>One pass over the closed bars after the history arrives (the forming bar is checked live).</summary>
    private void ScanTouches()
    {
        for (int bar = 0; bar < CurrentBar - 1; bar++) CheckTouches(bar, live: false);
    }

    protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> cumulativeTrades)
    {
        var list = cumulativeTrades as IList<CumulativeTrade> ?? cumulativeTrades.ToList();
        _bubbles.Clear();
        _recent.Clear();

        // initial adaptive threshold: percentile of all the loaded history
        if (_mode == ThresholdMode.Adaptive && list.Count > 0)
        {
            var vols = list.Select(SizeOf).ToArray();
            Array.Sort(vols);
            _adaptiveThreshold = PercentileOf(vols, _percentile);
            foreach (var v in list.Skip(Math.Max(0, list.Count - _sample))) _recent.Enqueue(SizeOf(v));
        }

        int bar = 0;
        foreach (var t in list)
        {
            bar = FindBar(t.Time, bar);
            if (Passes(t)) _bubbles.Add(MakeBubble(t, bar));
        }

        MergeSaved();
        ScanTouches();
        FlushShadow();
        if (CurrentBar > 0) CheckApproach(CurrentBar - 1, silent: true);
        _ready = true;
        RedrawChart();
    }

    protected override void OnCumulativeTrade(CumulativeTrade trade)
    {
        TrackAdaptive(SizeOf(trade));
        _liveTrade = trade;
        _liveBubble = null;
        Consider(trade);
    }

    protected override void OnUpdateCumulativeTrade(CumulativeTrade trade)
    {
        if (!ReferenceEquals(trade, _liveTrade))
        {
            _liveTrade = trade;
            _liveBubble = null;
        }

        Consider(trade);
    }

    private void Consider(CumulativeTrade trade)
    {
        if (!Passes(trade)) return;
        int bar = FindBar(trade.Time, Math.Max(0, CurrentBar - 5));
        if (_liveBubble == null)
        {
            _liveBubble = MakeBubble(trade, bar);
            _bubbles.Add(_liveBubble);
            Save();
        }
        else
        {
            var b = MakeBubble(trade, bar);
            _liveBubble.Price = b.Price;
            _liveBubble.Volume = b.Volume;
            _liveBubble.Bar = b.Bar;
        }

        RedrawChart();
    }

    // ------------------------------------------------------------------ persistence

    private sealed record Saved(DateTime Time, decimal Price, decimal Volume, bool Buy, bool Touched = false, bool Hidden = false, bool Deleted = false, bool Muted = false, DateTime TouchTime = default, int Outcome = 0);

    // the simulator makes up a new market every run, so old levels would mean nothing
    private bool IsSimulated => InstrumentInfo?.Instrument?.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase) == true;

    private string SavePath
    {
        get
        {
            string name = string.Concat((InstrumentInfo?.Instrument ?? "unknown").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            string root = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } d ? d : TradingHub.Common.Product.AppData;
            return Path.Combine(root, "BigTrades", name + ".json");
        }
    }

    /// <summary>Adds the saved levels the loaded history did not recreate (older, or under today's threshold).</summary>
    private void MergeSaved()
    {
        if (!RememberLevels || CurrentBar == 0 || IsSimulated) return;
        try
        {
            var saved = File.Exists(SavePath) ? JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(SavePath)) ?? new() : new List<Saved>();
            var have = _bubbles.Select(b => (b.Time, b.Price)).ToHashSet();
            var start = GetCandle(0).Time;
            // in a replay (or any chart that ends in the past) what was saved later than the last candle is not known yet: leave it out
            var known = GetCandle(CurrentBar - 1).LastTime;
            foreach (var s in saved)
            {
                if (s.Time > known) continue;
                bool touchKnown = s.Touched && (s.TouchTime == default || s.TouchTime <= known);
                if (have.Add((s.Time, s.Price)))
                    _bubbles.Add(new Bubble { Bar = s.Time < start ? -1 : FindBar(s.Time, 0), Price = s.Price, Volume = s.Volume, Buy = s.Buy, Time = s.Time, Touched = touchKnown, TouchTime = touchKnown ? s.TouchTime : default, Outcome = touchKnown ? s.Outcome : 0, FromDisk = true, Hidden = s.Hidden, Deleted = s.Deleted, Muted = s.Muted });
                else
                {
                    var b = _bubbles.First(b => b.Time == s.Time && b.Price == s.Price);
                    if (touchKnown && !b.Touched)
                    {
                        b.Touched = true;
                        b.TouchTime = s.TouchTime;
                    }

                    if (touchKnown && b.Outcome == 0) b.Outcome = s.Outcome;
                    b.Hidden |= s.Hidden;
                    b.Deleted |= s.Deleted;
                    b.Muted |= s.Muted;
                }
            }
            Save();
        }
        catch (Exception) { /* a bad file just means no remembered levels */ }
    }

    /// <summary>Writes the newest <see cref="MaxSaved"/> bubbles (at most every 5 s; a lost update is rewritten by the next one).</summary>
    private void Save()
    {
        if (!RememberLevels || IsSimulated || _bubbles.Count == 0 || DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(5)) return;
        _lastSave = DateTime.UtcNow;
        try
        {
            var path = SavePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // merged with what is on disk (flags are only ever added): a chart that shows an older moment must not shrink the file
            var disk = File.Exists(path) ? JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(path)) ?? new() : new List<Saved>();
            var byKey = disk.ToDictionary(s => (s.Time, s.Price));
            foreach (var b in _bubbles)
            {
                byKey.TryGetValue((b.Time, b.Price), out var d);
                byKey[(b.Time, b.Price)] = new Saved(b.Time, b.Price, b.Volume, b.Buy,
                    b.Touched || d?.Touched == true, b.Hidden || d?.Hidden == true, b.Deleted || d?.Deleted == true, b.Muted || d?.Muted == true,
                    b.TouchTime != default ? b.TouchTime : d?.TouchTime ?? default, b.Outcome != 0 ? b.Outcome : d?.Outcome ?? 0);
            }

            var list = byKey.Values.OrderByDescending(s => s.Time).Take(MaxSaved);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(list));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception) { /* disk trouble must not break the chart */ }
    }

    private Bubble MakeBubble(CumulativeTrade t, int bar)
    {
        decimal price = _anchor switch
        {
            PriceAnchor.Last => t.Lastprice,
            PriceAnchor.First => t.FirstPrice,
            _ => AveragePrice(t),
        };
        if (InstrumentInfo != null && InstrumentInfo.TickSize > 0)
            price = Math.Round(price / InstrumentInfo.TickSize) * InstrumentInfo.TickSize;
        return new Bubble { Bar = bar, Price = price, Volume = t.Volume, Buy = t.Direction == TradeDirection.Buy, Time = t.Time };
    }

    private OrderShape Shape(CumulativeTrade t)
    {
        int levels = t.Ticks is { Count: > 0 } ? t.Ticks.Select(k => k.Price).Distinct().Count() : t.FirstPrice != t.Lastprice ? 2 : 1;
        decimal maxPrint = t.Ticks is { Count: > 0 } ? t.Ticks.Max(k => k.Volume) : t.Volume;
        return new OrderShape(t.Volume, maxPrint, levels, _detect == WhaleMode.Level && NearALevel(AveragePrice(t)));
    }

    private decimal SizeOf(CumulativeTrade t) => WhaleDetect.SizeOf(_detect, Shape(t));

    private bool Passes(CumulativeTrade t) => WhaleDetect.Passes(_detect, Shape(t), CurrentThreshold);

    /// <summary>Within "Match previous-day levels" ticks of yesterday's levels or of a remembered big-trade level (6 ticks when that option is off).</summary>
    private bool NearALevel(decimal price)
    {
        decimal reach = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m) * (AlignTicks > 0 ? AlignTicks : 6);
        return _prevLevels.Any(l => Math.Abs(l.Price - price) <= reach) || _bubbles.Any(b => !b.Deleted && !b.Touched && Math.Abs(b.Price - price) <= reach);
    }

    private static decimal AveragePrice(CumulativeTrade t)
    {
        if (t.Ticks == null || t.Ticks.Count == 0) return t.Lastprice;
        decimal pv = 0, v = 0;
        foreach (var tk in t.Ticks)
        {
            pv += tk.Price * tk.Volume;
            v += tk.Volume;
        }

        return v > 0 ? pv / v : t.Lastprice;
    }

    private void TrackAdaptive(decimal volume)
    {
        if (_mode != ThresholdMode.Adaptive) return;
        _recent.Enqueue(volume);
        while (_recent.Count > _sample) _recent.Dequeue();
        if (++_sinceRecalc >= 250 || _adaptiveThreshold == 0)
        {
            _sinceRecalc = 0;
            var arr = _recent.ToArray();
            Array.Sort(arr);
            _adaptiveThreshold = PercentileOf(arr, _percentile);
        }
    }

    private static decimal PercentileOf(decimal[] sorted, decimal pct)
    {
        if (sorted.Length == 0) return 0;
        double pos = (double)pct / 100.0 * (sorted.Length - 1);
        int idx = Math.Clamp((int)Math.Round(pos), 0, sorted.Length - 1);
        return sorted[idx];
    }

    /// <summary>Bar containing an instant (searching from a hint).</summary>
    private int FindBar(DateTime time, int hint)
    {
        int n = CurrentBar;
        if (n == 0) return 0;
        int lo = 0, hi = n - 1;
        if (hint > 0 && hint < n && GetCandle(hint).Time <= time) lo = hint;
        int ans = lo;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (GetCandle(mid).Time <= time)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans;
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>List rows: left click hides or shows that level's line, Ctrl + left click mutes or unmutes its alert, right click deletes the level for good.</summary>
    public override bool ProcessMouseDown(RenderControlMouseEventArgs e)
    {
        if (!RememberLevels || !ShowList || _listRows.Count == 0) return false;
        if (e.X < ListX - 6 || e.X > ListX - 6 + ListW || e.Y < ListY - 6 || e.Y >= ListY - 6 + _listRows.Count * ListH + 12) return false;
        int i = (e.Y - ListY) / ListH;
        if (i < 0 || i >= _listRows.Count) return true; // the list's padding: swallow the click
        var b = _listRows[i];
        if (e.Button == RenderControlMouseButtons.Left && (e.Modifiers & RenderControlKeyModifiers.Control) != 0) b.Muted = !b.Muted;
        else if (e.Button == RenderControlMouseButtons.Right) b.Deleted = true;
        else if (e.Button == RenderControlMouseButtons.Left) b.Hidden = !b.Hidden;
        else return false;
        _lastSave = default; // write it now
        Save();
        RedrawChart();
        return true;
    }

    private const int ListX = 70, ListY = 60, ListH = 15, ListW = 200;

    /// <summary>" @PDH"-style suffix when the price is near one of the previous session's levels.</summary>
    private string Aligned(decimal price)
    {
        if (AlignTicks <= 0 || _prevLevels.Count == 0) return "";
        decimal reach = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m) * AlignTicks;
        var near = _prevLevels.Where(l => Math.Abs(l.Price - price) <= reach).OrderBy(l => Math.Abs(l.Price - price)).Select(l => l.Name).FirstOrDefault();
        return near == null ? "" : " @" + near;
    }

    private void DrawList(RenderContext context)
    {
        var pool = _bubbles.Where(b => !b.Deleted).ToList();
        var rows = (ListByScore ? pool.OrderByDescending(b => Score(b, GroupOf(b, pool))) : pool.OrderByDescending(b => b.Time)).Take(ListRows).ToList();
        _listRows = rows;
        if (rows.Count == 0) return;
        var font = new RenderFont("Consolas", 9);
        const int x = ListX, y0 = ListY, h = ListH, w = ListW;
        context.FillRectangle(Color.FromArgb(150, 0, 0, 0), new Rectangle(x - 6, y0 - 6, w, rows.Count * h + 12));
        for (int i = 0; i < rows.Count; i++)
        {
            var b = rows[i];
            var c = b.Buy ? BuyColor : SellColor;
            string txt = $"{(b.Buy ? "BUY " : "SELL")} {b.Price,10} {VolumeFormat.Compact(b.Volume, 0),6}{(b.Hidden ? "  hidden" : b.Touched ? "  used" : "")}{(b.Muted ? "  muted" : "")}";
            context.DrawString(txt, font, b.Touched || b.Hidden ? Color.Gray : Color.FromArgb(255, c.R, c.G, c.B), x, y0 + i * h);
        }
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _bubbles.Count == 0) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        decimal thr = Math.Max(CurrentThreshold, 0.0000001m);
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        _tip.Clear();
        UpdateHoldRate();
        int chartRight = ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth;

        if (RememberLevels && LevelOpacity > 0)
        {
            var labelFont = new RenderFont("Arial", 8);
            int right = ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth;
            var lines = _bubbles.Where(b => b.Bar <= last && b.Volume >= LevelMinVolume && !b.Hidden && !b.Deleted && !(HideTouched && b.Touched && !(KeepHeld && b.Outcome == 1)));
            if (MaxLines > 0) lines = lines.OrderByDescending(b => b.Volume).Take(MaxLines);
            var shown = lines.ToList();
            decimal reach = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m) * ConfluenceTicks;
            foreach (var b in shown)
            {
                var c = b.Buy ? BuyColor : SellColor;
                int group = GroupOf(b, shown);
                int score = Score(b, group);
                // a level that held after price came back is dashed: it proved itself
                var pen = new RenderPen(Color.FromArgb(LevelOpacity, c.R, c.G, c.B), group > 1 ? 2.5f : 1f, b.Touched ? System.Drawing.Drawing2D.DashStyle.Dash : System.Drawing.Drawing2D.DashStyle.Solid);
                int y = ChartInfo.GetYByPrice(b.Price, false);
                int x0 = b.Bar < first ? ChartInfo.GetXByBar(first, true) : ChartInfo.GetXByBar(b.Bar, false);
                context.DrawLine(pen, x0, y, right, y);
                _tip.Add(new Rectangle(x0, y - 1, Math.Max(1, right - x0), 3), c, Details(b, group, score));
                if (LabelLines)
                    context.DrawString($"{(b.Buy ? "B" : "S")} {VolumeFormat.Compact(b.Volume, 0)}{(group > 1 ? $" x{group}" : "")}{Aligned(b.Price)}{(ShowScore ? $" Q{score}" : "")}", labelFont, Color.FromArgb(Math.Min(255, LevelOpacity + 100), c.R, c.G, c.B), right - 80, y - 13);
            }
        }

        if (RememberLevels && ShowList) DrawList(context);

        // the small ones first so the large ones end up on top
        foreach (var b in _bubbles.Where(b => ShowBubbles && b.Bar >= first && b.Bar <= last && !b.Deleted && (!b.FromDisk || b.Volume >= thr)).OrderBy(b => b.Volume))
        {
            int x = ChartInfo.GetXByBar(b.Bar, false);
            int y = ChartInfo.GetYByPrice(b.Price, false);
            double ratio = Math.Sqrt((double)(b.Volume / thr));
            int size = (int)Math.Clamp(MinSize * ratio, MinSize, Math.Max(MinSize, MaxSize));
            var rect = new Rectangle(x - size / 2, y - size / 2, size, size);
            var col = b.Buy ? BuyColor : SellColor;
            _tip.Add(rect, col, Details(b, 1, Score(b, 1)));
            context.FillEllipse(col, rect);
            if (ShowBorder) context.DrawEllipse(new RenderPen(Color.FromArgb(220, col.R, col.G, col.B)), rect);
            if (ShowVolume && size >= 22)
            {
                if (LabelAs == BubbleLabel.Dollars)
                {
                    // the money is written above the bubble in its own color, so a small bubble does not have to hold it
                    string money = Money(b.Price, b.Volume);
                    var tw = context.MeasureString(money, font);
                    context.DrawString(money, font, Color.FromArgb(255, col.R, col.G, col.B), x - tw.Width / 2, rect.Top - tw.Height - 1);
                }
                else
                {
                    string txt = VolumeFormat.Compact(b.Volume, ChartInfo.VolumeDecimals > 0 && b.Volume < 100 ? 1 : 0);
                    context.DrawString(txt, font, Color.White, rect, new RenderStringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoClip });
                }
            }
        }

        DrawWhaleView(context, first, last, chartRight);
        _tip.Draw(context, chartRight);
    }

    private decimal MoneyValue(decimal price, decimal volume) =>
        price * volume * (InstrumentInfo is { TickSize: > 0, TickCost: > 0 } i ? i.TickCost / i.TickSize : 1m);

    /// <summary>The summary sentence, the whales' average prices and their zones, all from the bubbles that are drawn in view.</summary>
    private void DrawWhaleView(RenderContext context, int first, int last, int right)
    {
        if (!ShowSummary && !ShowAverage && !ShowZones) return;
        decimal thr = Math.Max(CurrentThreshold, 0.0000001m);
        var inView = _bubbles.Where(b => b.Bar >= first && b.Bar <= last && !b.Deleted && (!b.FromDisk || b.Volume >= thr)).ToList();
        int left = ChartInfo.GetXByBar(first, true);

        if (ShowZones && inView.Count > 0)
        {
            decimal reach = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m) * Math.Max(ConfluenceTicks, 4);
            foreach (var z in WhaleDetect.Zones(inView.Select(b => (b.Price, MoneyValue(b.Price, b.Volume), b.Buy)), reach))
            {
                var c = z.Buy ? BuyColor : SellColor;
                int y1 = ChartInfo.GetYByPrice(z.High, false), y2 = ChartInfo.GetYByPrice(z.Low, false);
                context.FillRectangle(Color.FromArgb(40, c.R, c.G, c.B), new Rectangle(left, y1, Math.Max(1, right - left), Math.Max(2, y2 - y1)));
            }
        }

        if (ShowAverage)
        {
            var font = new RenderFont("Arial", 8);
            foreach (bool buy in new[] { true, false })
            {
                var avg = WhaleDetect.AveragePrice(inView.Where(b => b.Buy == buy).Select(b => (b.Price, b.Volume)));
                if (avg is not { } a) continue;
                var c = buy ? BuyColor : SellColor;
                int y = ChartInfo.GetYByPrice(a, false);
                context.DrawLine(new RenderPen(Color.FromArgb(220, c.R, c.G, c.B), 1.5f, System.Drawing.Drawing2D.DashStyle.Dash), left, y, right, y);
                context.DrawString(buy ? "whale buy avg" : "whale sell avg", font, Color.FromArgb(255, c.R, c.G, c.B), left + 4, y - 13);
            }
        }

        if (ShowSummary && last >= first)
        {
            decimal open = GetCandle(first).Open, close = GetCandle(last).Close;
            decimal pct = open > 0 ? (close - open) / open * 100 : 0;
            decimal buy = inView.Where(b => b.Buy).Sum(b => MoneyValue(b.Price, b.Volume)), sell = inView.Where(b => !b.Buy).Sum(b => MoneyValue(b.Price, b.Volume));
            string since = DateTime.SpecifyKind(GetCandle(first).Time, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm");
            string text = WhaleDetect.Summary(since, pct, inView.Count, buy, sell) + $"  [{_detect}, size ≥ {(_detect == WhaleMode.BigPrint ? "print " : "")}${VolumeFormat.Compact(MoneyValue(close, thr), 1)}]";
            // at the bottom-left of the price panel: the top-left holds the list of indicator names, which this used to be drawn over
            var summaryFont = new RenderFont("Arial", 9);
            var region = ChartInfo.PriceChartContainer.Region;
            context.DrawString(text, summaryFont, Color.FromArgb(220, 200, 200, 200), region.Left + 8, region.Bottom - context.MeasureString(text, summaryFont).Height - 6);
        }
    }

    /// <summary>What a trade is worth: price × size × the contract's value per point (1 for crypto and stocks), written as $1.2M.</summary>
    private string Money(decimal price, decimal volume)
    {
        decimal pointValue = InstrumentInfo is { TickSize: > 0, TickCost: > 0 } i ? i.TickCost / i.TickSize : 1m;
        return "$" + VolumeFormat.Compact(price * volume * pointValue, 1);
    }

    /// <summary>Hovering a bubble or a level line shows its details (the number inside a small bubble is hidden).</summary>
    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false; // the chart keeps handling the move (crosshair, panning)
    }

    private string[] Details(Bubble b, int group, int score)
    {
        var local = DateTime.SpecifyKind(b.Time, DateTimeKind.Utc).ToLocalTime();
        var lines = new List<string> { $"{(b.Buy ? "BUY" : "SELL")}  {b.Volume:0.##}", $"@ {b.Price}", $"{local:dd/MM HH:mm:ss}" };
        if (group > 1) lines.Add($"{group} levels close together");
        string near = Aligned(b.Price).Trim();
        if (near.Length > 0) lines.Add("near " + near.TrimStart('@'));
        if (ShowScore) lines.Add(_model != null ? $"chance it holds {score}% (learned from {_model.TrainedOn} results)" : $"quality {score}/100");
        if (b.Touched) lines.Add(b.Outcome == 1 ? "price came back: held" : b.Outcome == 2 ? "price came back: broke" : "price came back");
        return lines.ToArray();
    }

    /// <summary>
    /// 0-100: how big the order is against the threshold (up to 50), how many levels sit close (up to 20), and how often this
    /// instrument's levels have held once price came back (up to 30; 15 until five results are known).
    /// </summary>
    private int Score(Bubble b, int group)
    {
        // once enough results are known the score is the model's own estimate of the chance the level holds
        if (_model != null) return (int)Math.Round(100 * _model.Predict(new LevelSample(b.Time, b.Price, b.Volume, b.Buy)));
        decimal thr = Math.Max(CurrentThreshold, 0.0000001m);
        double size = Math.Clamp(25 * Math.Log2(Math.Max(1.0, (double)(b.Volume / thr))), 0, 50);
        double conf = Math.Min(20, 10 * (group - 1));
        return (int)Math.Round(size + conf + 30 * _holdRate);
    }

    private double _holdRate = 0.5;
    private HoldModel? _model;
    private DateTime _modelAt = DateTime.MinValue;

    private void UpdateHoldRate()
    {
        int held = 0, broke = 0;
        foreach (var b in _bubbles)
        {
            if (b.Deleted) continue;
            if (b.Outcome == 1) held++;
            else if (b.Outcome == 2) broke++;
        }

        _holdRate = held + broke >= 5 ? (double)held / (held + broke) : 0.5;

        // refit at most once a minute (a few thousand levels: instant)
        if (DateTime.UtcNow - _modelAt < TimeSpan.FromMinutes(1)) return;
        _modelAt = DateTime.UtcNow;
        var live = _bubbles.Where(b => !b.Deleted).ToList();
        var labelled = live.Where(b => b.Outcome != 0).Select(b => (new LevelSample(b.Time, b.Price, b.Volume, b.Buy), b.Outcome == 1)).ToList();
        var trained = HoldModel.Train(labelled, live.Select(b => new LevelSample(b.Time, b.Price, b.Volume, b.Buy)).ToList(), Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m));
        // a model that does no better than guessing is not shown as a probability: the plain quality score stays
        _model = trained is { HasSkill: true } ? trained : null;
    }

    private int GroupOf(Bubble b, IEnumerable<Bubble> pool)
    {
        if (ConfluenceTicks <= 0) return 1;
        decimal reach = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m) * ConfluenceTicks;
        return pool.Count(o => Math.Abs(o.Price - b.Price) <= reach);
    }
}
