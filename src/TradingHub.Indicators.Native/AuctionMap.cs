using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Reads each session's time profile (TPO) and volume profile together, as in the market-profile method: initial balance and range
/// extension, single prints, tails and poor highs / lows, the shape of the day (D, P, b, trend), high and low volume nodes, the TPO POC
/// against the volume POC and the fair value zone between them, the core of value where the two value areas overlap, what each price
/// says when time and volume are read together (regulars, rush, camper, big spender), and whether price is accepted outside the
/// previous session's value (two TPO periods beyond the level, with volume building there). Put it on a 1 to 15 minute chart; the
/// TPO profile indicator can sit under it for the letters.
/// </summary>
[DisplayName("Auction map (TPO + volume together)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class AuctionMap : Indicator
{
    private sealed class Session
    {
        public int StartBar, EndBar;
        public DateTime Start;
        public decimal Open;
        public readonly Dictionary<decimal, SortedSet<int>> Slots = new();
        public readonly Dictionary<decimal, decimal> Committed = new();
        public readonly Dictionary<int, (decimal Close, decimal Above, decimal Below)> PeriodCommitted = new();
        public int CommittedTo = -1;
        public decimal RefVah, RefVal;           // the previous session's volume value area (row edges), 0 = none
        public AuctionResult? Result;
        public decimal[] Rows = Array.Empty<decimal>();
        public AcceptanceResult? Above, Below;
        public string OpenNote = "";
    }

    private sealed class Open
    {
        public string Kind = "";
        public decimal Low, High;      // the zone (one price for a POC or an edge)
        public int Number;             // the session number it was left by
        public int CreatedBar;
        public bool Filled, Approached;
        public bool Created(int lastVisibleBar) => CreatedBar > lastVisibleBar; // created after the right edge: nothing to draw yet
    }

    private readonly List<Session> _sessions = new();
    private Rectangle _summaryBox;
    private readonly List<Open> _open = new();
    private List<MarkStore.Rec> _marks = new();
    private DateTime _marksAt = DateTime.MinValue;
    private AuctionStats? _stats;
    private bool _warm;
    private readonly HoverTip _tip = new();
    private int _ticksPerRow = 4, _periodMinutes = 30, _valueArea = 70;

    public AuctionMap() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Letter every (minutes)", GroupName = "Calculation", Order = 10)]
    [Range(1, 240)]
    public int PeriodMinutes { get => _periodMinutes; set { _periodMinutes = Math.Clamp(value, 1, 240); RecalculateValues(); } }

    [Display(Name = "Ticks per row", GroupName = "Calculation", Order = 20, Description = "Prices are grouped in rows of this many ticks (4 = one point on ES and NQ).")]
    [Range(1, 1000)]
    public int TicksPerRow { get => _ticksPerRow; set { _ticksPerRow = Math.Clamp(value, 1, 1000); RecalculateValues(); } }

    [Display(Name = "Value area (%)", GroupName = "Calculation", Order = 30)]
    [Range(10, 100)]
    public int ValueAreaPercent { get => _valueArea; set { _valueArea = Math.Clamp(value, 10, 100); RecalculateValues(); } }

    [Display(Name = "Initial balance periods", GroupName = "Calculation", Order = 40, Description = "2 = the first hour with 30-minute periods.")]
    [Range(1, 10)]
    public int IbPeriods { get; set; } = 2;

    [Display(Name = "Poor high / low: letters at the edge", GroupName = "Calculation", Order = 50, Description = "A top or bottom row with at least this many letters and no tail is a poor high / low (unfinished business).")]
    [Range(2, 20)]
    public int PoorLetters { get; set; } = 3;

    [Display(Name = "Tail: single-print rows", GroupName = "Calculation", Order = 60)]
    [Range(2, 30)]
    public int TailRows { get; set; } = 2;

    [Display(Name = "Accepted: periods beyond the level", GroupName = "Acceptance", Order = 70, Description = "Price has to hold beyond the previous session's value for this many TPO periods (2 = one hour).")]
    [Range(1, 10)]
    public int AcceptPeriods { get; set; } = 2;

    [Display(Name = "Accepted: volume beyond (% of the session)", GroupName = "Acceptance", Order = 80, Description = "Volume traded beyond the level, as a share of the session's volume so far, for acceptance to count as confirmed.")]
    [Range(1, 90)]
    public int AcceptVolumePercent { get; set; } = 15;

    [Display(Name = "Unfinished business (naked POCs, single prints, poor highs / lows)", GroupName = "Unfinished business", Order = 180,
        Description = "Levels left by earlier sessions that price has not come back to are carried to the right until price trades there, with their age. They are what the method calls unfinished business.")]
    public bool ShowUnfinished { get; set; } = true;

    [Display(Name = "Sessions to look back", GroupName = "Unfinished business", Order = 190)]
    [Range(1, 60)]
    public int UnfinishedSessions { get; set; } = 10;

    [Display(Name = "Alert when price is within (ticks)", GroupName = "Unfinished business", Order = 200, Description = "One alert the first time live price comes this close to an unfinished level (0 = no alerts).")]
    [Range(0, 1000)]
    public int UnfinishedAlertTicks { get; set; } = 10;

    [Display(Name = "Level board (nearest levels with order-flow signals)", GroupName = "Show", Order = 175)]
    public bool ShowBoard { get; set; } = true;

    [Display(Name = "Past sessions: what usually happened", GroupName = "Show", Order = 176,
        Description = "Adds the open-location and day-type statistics saved by the Auction claims / Open and day types research (Today window) to the summary.")]
    public bool ShowHistoryStats { get; set; } = true;

    [Display(Name = "Initial balance and range extension", GroupName = "Show", Order = 100)] public bool ShowIb { get; set; } = true;
    [Display(Name = "Single prints", GroupName = "Show", Order = 110)] public bool ShowSingles { get; set; } = true;
    [Display(Name = "Tails, poor highs and lows", GroupName = "Show", Order = 120)] public bool ShowTails { get; set; } = true;
    [Display(Name = "High / low volume nodes", GroupName = "Show", Order = 130)] public bool ShowNodes { get; set; } = true;
    [Display(Name = "TPO POC, volume POC, fair value zone", GroupName = "Show", Order = 140)] public bool ShowPocs { get; set; } = true;
    [Display(Name = "Time against volume markers (regulars, rush, camper, big spender)", GroupName = "Show", Order = 150)] public bool ShowCases { get; set; } = true;
    [Display(Name = "Summary of the latest session", GroupName = "Show", Order = 160)] public bool ShowSummary { get; set; } = true;
    [Display(Name = "Acceptance outside the previous value", GroupName = "Show", Order = 170)] public bool ShowAcceptance { get; set; } = true;

    private decimal Tick => InstrumentInfo is { TickSize: > 0 } i ? i.TickSize : 1m;
    private decimal RowSize => Tick * _ticksPerRow;
    private decimal RowOf(decimal price) => Math.Floor(price / RowSize) * RowSize;

    protected override void OnRecalculate()
    {
        _sessions.Clear();
        _open.Clear();
        _warm = false;
        _stats = null;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        bool isNew = bar == 0 || _sessions.Count == 0 || IsNewSession(bar);
        if (isNew && (_sessions.Count == 0 || _sessions[^1].StartBar != bar))
        {
            var prev = _sessions.Count > 0 ? _sessions[^1] : null;
            var s = new Session { StartBar = bar, EndBar = bar, Start = GetCandle(bar).Time, Open = GetCandle(bar).Open, CommittedTo = bar - 1 };
            if (prev?.Result is { } pr)
            {
                s.RefVah = pr.VolumeVah + RowSize;
                s.RefVal = pr.VolumeVal;
                s.OpenNote = s.Open > s.RefVah ? "opened above the previous value: watch whether it is accepted or rejected"
                    : s.Open < s.RefVal ? "opened below the previous value: watch whether it is accepted or rejected"
                    : "opened inside the previous value: expect rotation";
            }

            _sessions.Add(s);
            if (prev?.Result is { } ended) AddUnfinished(prev, ended, bar, _sessions.Count - 1);
            _stats ??= InstrumentInfo?.Instrument is { Length: > 0 } name ? AuctionStats.Load(name) : null;
        }

        var cur = _sessions[^1];
        var c = GetCandle(bar);
        int slot = Math.Max(0, (int)((c.Time - cur.Start).TotalMinutes / _periodMinutes));
        decimal size = RowSize;
        // letters are a set: a forming candle can be added on every tick; the volume of closed candles is added once, the forming one on top
        for (decimal row = RowOf(c.Low); row <= RowOf(c.High); row += size)
        {
            if (!cur.Slots.TryGetValue(row, out var slots)) cur.Slots[row] = slots = new SortedSet<int>();
            slots.Add(slot);
        }

        while (cur.CommittedTo < bar - 1) AddVolume(cur, ++cur.CommittedTo, cur.Committed, commit: true);
        cur.EndBar = bar;
        UpdateUnfinished(bar, c);
        // the analysis is only needed for the live session and for a session at its last candle
        bool lastOfSession = bar == CurrentBar - 1 || (bar + 1 < CurrentBar && IsNewSession(bar + 1));
        if (!lastOfSession) return;
        var volume = new Dictionary<decimal, decimal>(cur.Committed);
        var live = new Dictionary<int, (decimal, decimal, decimal)>();
        AddVolume(cur, bar, volume, commit: false, live);
        Analyze(cur, volume, live);
    }

    /// <summary>Adds a candle's volume by row; for the per-period figures used by the acceptance check, a closed candle is stored for good and a forming one only for this call.</summary>
    private void AddVolume(Session s, int bar, Dictionary<decimal, decimal> volume, bool commit, Dictionary<int, (decimal, decimal, decimal)>? live = null)
    {
        var c = GetCandle(bar);
        int slot = Math.Max(0, (int)((c.Time - s.Start).TotalMinutes / _periodMinutes));
        decimal above = 0, below = 0;
        foreach (var l in c.GetAllPriceLevels())
        {
            decimal v = l.Bid + l.Ask;
            decimal row = RowOf(l.Price);
            volume[row] = volume.GetValueOrDefault(row) + v;
            if (s.RefVah > 0 && l.Price >= s.RefVah) above += v;
            if (s.RefVal > 0 && l.Price < s.RefVal) below += v;
        }

        if (commit)
        {
            var old = s.PeriodCommitted.GetValueOrDefault(slot);
            s.PeriodCommitted[slot] = (c.Close, old.Above + above, old.Below + below);
        }
        else if (live != null)
        {
            var old = s.PeriodCommitted.GetValueOrDefault(slot);
            live[slot] = (c.Close, old.Above + above, old.Below + below);
        }
    }

    private void Analyze(Session s, Dictionary<decimal, decimal> volume, Dictionary<int, (decimal Close, decimal Above, decimal Below)> live)
    {
        if (s.Slots.Count == 0) return;
        var rows = s.Slots.Keys.OrderBy(r => r).ToArray();
        // every row between the extremes exists, even if nothing traded there (a hole is information)
        var full = new List<decimal>();
        for (decimal r = rows[0]; r <= rows[^1]; r += RowSize) full.Add(r);
        s.Rows = full.ToArray();
        var input = new AuctionInput(s.Rows,
            s.Rows.Select(r => s.Slots.TryGetValue(r, out var set) ? set.ToArray() : Array.Empty<int>()).ToArray(),
            s.Rows.Select(r => volume.GetValueOrDefault(r)).ToArray(), RowSize);
        try { s.Result = AuctionAnalysis.Analyze(input, _valueArea, PoorLetters, TailRows, IbPeriods); }
        catch (ArgumentException) { return; }

        if (s.RefVah > 0)
        {
            var periods = s.PeriodCommitted.Keys.Union(live.Keys).OrderBy(k => k).Select(k => live.TryGetValue(k, out var l) ? l : s.PeriodCommitted[k]).ToList();
            decimal total = volume.Values.Sum();
            s.Above = Acceptance.Evaluate(true, s.RefVah, periods.Select(p => (p.Close, p.Above)).ToList(), total, AcceptPeriods, AcceptVolumePercent / 100.0);
            s.Below = Acceptance.Evaluate(false, s.RefVal, periods.Select(p => (p.Close, p.Below)).ToList(), total, AcceptPeriods, AcceptVolumePercent / 100.0);
        }
    }

    // ------------------------------------------------------------------------------------------------------------ unfinished business

    private void AddUnfinished(Session ended, AuctionResult r, int bar, int number)
    {
        decimal size = RowSize;
        void Add(string kind, decimal low, decimal high) => _open.Add(new Open { Kind = kind, Low = low, High = high, Number = number - 1, CreatedBar = bar });
        Add("naked volume POC", r.VolumePoc + size / 2, r.VolumePoc + size / 2);
        if (!r.PocsAligned) Add("naked TPO POC", r.TpoPoc + size / 2, r.TpoPoc + size / 2);
        foreach (var z in r.SinglePrints) Add("single prints", z.Low, z.High + size);
        if (r.PoorHigh) Add("poor high", ended.Rows[^1] + size, ended.Rows[^1] + size);
        if (r.PoorLow) Add("poor low", ended.Rows[0], ended.Rows[0]);
        _open.RemoveAll(o => number - o.Number > UnfinishedSessions);
    }

    /// <summary>A level is filled when price trades into it. Alerts the first time live price comes near an unfilled one.</summary>
    private void UpdateUnfinished(int bar, IndicatorCandle c)
    {
        foreach (var o in _open)
            if (!o.Filled && bar > o.CreatedBar && c.High >= o.Low && c.Low <= o.High) o.Filled = true;
        if (UnfinishedAlertTicks <= 0 || bar != CurrentBar - 1) return;
        decimal reach = UnfinishedAlertTicks * Tick, price = c.Close;
        foreach (var o in _open)
        {
            if (o.Filled || o.Approached || (price >= o.Low - reach && price <= o.High + reach) == false) continue;
            o.Approached = true;
            // the levels already near when the chart opened are marked without an alert
            if (_warm) AddAlert("alert1", $"Price is near unfinished business: {o.Kind} {(o.Low == o.High ? o.Low.ToString() : $"{o.Low} - {o.High}")} (left {_sessions.Count - 1 - o.Number} session{(_sessions.Count - 1 - o.Number == 1 ? "" : "s")} ago, now {price})");
        }

        _warm = true;
    }

    private string AgeOf(Open o) => $"{_sessions.Count - 1 - o.Number}d";

    // ------------------------------------------------------------------------------------------------------------ what happened before, and the board

    private IEnumerable<string> HistoryLines(Session s)
    {
        if (!ShowHistoryStats || _stats == null || s.RefVah <= 0) yield break;
        string type = s.Open > s.RefVah ? "above" : s.Open < s.RefVal ? "below" : "inside";
        string basis = $" ({_stats.Sessions} New York 08:00-17:00 sessions, {_stats.From} to {_stats.To})";
        if (_stats.Open.TryGetValue(type, out var b) && b.N >= 10)
        {
            if (type == "inside")
                yield return $"History, opened inside value (n={b.N}): traded through the previous POC {100 * b.Rate("touchedPreviousPoc"):0}%, reached the far edge {100 * b.Rate("touchedFarEdgeOfValue"):0}%, closed inside {100 * b.Rate("closedInsideValue"):0}%" + basis;
            else
                yield return $"History, opened {type} value (n={b.N}): closed still beyond {100 * b.Rate("closedStillBeyond"):0}%, closed back inside {100 * b.Rate("closedBackInsideValue"):0}%, traded back to the edge {100 * b.Rate("tradedBackIntoValue"):0}%" + basis;
        }

        var cur = s.Result;
        if (cur == null || cur.IbHigh <= 0 || _stats.IbCuts.Length < 2 || (GetCandle(CurrentBar - 1).Time - s.Start).TotalMinutes < IbPeriods * _periodMinutes) yield break;
        double ib = (double)((cur.IbHigh + RowSize - cur.IbLow) / Tick);
        string ibKey = ib <= _stats.IbCuts[0] ? "small" : ib >= _stats.IbCuts[1] ? "large" : "medium";
        string key = _stats.DayTypes.TryGetValue($"{type}|{ibKey}", out var kb) && kb.N >= 12 ? $"{type}|{ibKey}" : $"any|{ibKey}";
        if (!_stats.DayTypes.TryGetValue(key, out var d) || d.N == 0) yield break;
        string caution = d.N < 30 ? " - a small sample" : "";
        yield return $"Days like this ({(key.StartsWith("any") ? "" : "opened " + type + ", ")}{ibKey} first hour, n={d.N}{caution}): ended D {100 * d.Rate("shapeD"):0}%, P {100 * d.Rate("shapeP"):0}%, b {100 * d.Rate("shapeb"):0}%; broke the first hour's range up {100 * d.Rate("extendedUp"):0}%, down {100 * d.Rate("extendedDown"):0}%, both {100 * d.Rate("extendedBoth"):0}%";
    }

    private sealed record BoardRow(string Name, decimal Price, decimal Distance, string State, string Signals, bool Zone);

    private void LoadMarks()
    {
        if (DateTime.UtcNow - _marksAt < TimeSpan.FromSeconds(10)) return;
        _marksAt = DateTime.UtcNow;
        var name = InstrumentInfo?.Instrument;
        if (MarkStore.Skip(name)) { _marks = new(); return; }
        try { _marks = JsonSerializer.Deserialize<List<MarkStore.Rec>>(File.ReadAllText(MarkStore.PathFor(name!))) ?? new(); }
        catch (Exception) { _marks = new(); }
    }

    /// <summary>The nearest auction levels with how far price is from each, its acceptance state, and the order-flow signals that fired near it in the last 30 minutes.</summary>
    private List<BoardRow> Board()
    {
        var rows = new List<BoardRow>();
        if (_sessions.Count == 0 || CurrentBar == 0) return rows;
        var s = _sessions[^1];
        var last = GetCandle(CurrentBar - 1);
        decimal price = last.Close, size = RowSize, tick = Tick, near = 6 * tick;
        var now = last.Time;
        LoadMarks();
        string Signals(decimal low, decimal high)
        {
            var kinds = new SortedSet<string>();
            foreach (var m in _marks)
            {
                var t = DateTime.SpecifyKind(m.Time, DateTimeKind.Utc);
                if (t > now || now - t > TimeSpan.FromMinutes(30)) continue;
                decimal a = m.Kind == "stacked" ? Math.Min(m.A, m.B) : m.A, b = m.Kind == "stacked" ? Math.Max(m.A, m.B) : m.A;
                if (b >= low - near && a <= high + near) kinds.Add(m.Kind switch { "absorption" => "A", "stacked" => "S", "divergence" => "D", "sweep" => "W", _ => "?" });
            }

            return string.Concat(kinds);
        }

        void Add(string name, decimal low, decimal high, string state = "")
        {
            decimal mid = (low + high) / 2, dist = (mid - price) / tick;
            if (low <= price && price <= high) dist = 0;
            rows.Add(new BoardRow(name, mid, dist, state, Signals(low, high), high > low));
        }

        if (s.RefVah > 0 && s.Above != null && s.Below != null)
        {
            Add("previous VAH", s.RefVah, s.RefVah, s.Above.State);
            Add("previous VAL", s.RefVal, s.RefVal, s.Below.State);
            if (_sessions.Count > 1 && _sessions[^2].Result is { } p) Add("previous vPOC", p.VolumePoc + size / 2, p.VolumePoc + size / 2);
        }

        if (s.Result is { } r)
        {
            Add("today tPOC", r.TpoPoc + size / 2, r.TpoPoc + size / 2);
            if (!r.PocsAligned) Add("today vPOC", r.VolumePoc + size / 2, r.VolumePoc + size / 2);
            if (r.IbHigh > 0) { Add("IB high", r.IbHigh + size, r.IbHigh + size); Add("IB low", r.IbLow, r.IbLow); }
        }

        foreach (var o in _open.Where(o => !o.Filled)) Add($"{o.Kind} {AgeOf(o)}", o.Low, o.High);
        return rows.OrderBy(x => Math.Abs(x.Distance)).Take(9).ToList();
    }

    /// <summary>The summary and the board as plain text (used to ask Claude to explain the profile).</summary>
    public string Facts
    {
        get
        {
            if (_sessions.Count == 0 || _sessions[^1].Result == null) return "";
            var sb = new System.Text.StringBuilder();
            var s = _sessions[^1];
            foreach (var l in SummaryLines(s)) sb.AppendLine(l);
            foreach (var l in HistoryLines(s)) sb.AppendLine(l);
            if (s.Above != null && s.Below != null)
            {
                sb.AppendLine($"Above the previous value ({s.RefVah}): {s.Above.State} ({s.Above.PeriodsBeyond} periods, {100 * s.Above.VolumeShare:0}% of the session's volume)");
                sb.AppendLine($"Below the previous value ({s.RefVal}): {s.Below.State} ({s.Below.PeriodsBeyond} periods, {100 * s.Below.VolumeShare:0}% of the session's volume)");
            }

            sb.AppendLine("Nearest levels (distance in ticks, acceptance state, order-flow signals in the last 30 minutes: A absorption, S stacked imbalance, D divergence, W sweep):");
            foreach (var b in Board()) sb.AppendLine($"  {b.Name} {b.Price}: {b.Distance:+0;-0;0} ticks {b.State} {(b.Signals.Length > 0 ? "signals " + b.Signals : "")}".TrimEnd());
            return sb.ToString();
        }
    }

    private List<string> SummaryLines(Session s)
    {
        var cur = s.Result!;
        decimal size = RowSize;
        var lines = new List<string>
        {
            $"Shape: {cur.Shape} · {cur.ShapeMeaning}",
            cur.PocsAligned ? $"POCs: time and volume agree at {cur.TpoPoc} (the strongest fair price)" : $"POCs apart: time {cur.TpoPoc}, volume {cur.VolumePoc}; fair value zone {Range(cur.FairValue, size)}, volume POC stronger",
            cur.CoreOfValue is { } core ? $"Core of value (both value areas overlap): {Range(core, size)}; volume value sits {(cur.VolumeValueVersusTime == "same" ? "level with" : cur.VolumeValueVersusTime + " than")} time value" : "The two value areas do not overlap",
        };
        if (cur.IbHigh > 0) lines.Add($"Initial balance {cur.IbLow} - {cur.IbHigh + size}" + (cur.ExtendedUp ? " · range extension up" : "") + (cur.ExtendedDown ? " · range extension down" : ""));
        if (s.OpenNote.Length > 0) lines.Add("Open: " + s.OpenNote);
        return lines;
    }

    // ------------------------------------------------------------------------------------------------------------ drawing

    private static string Range(Zone z, decimal size) => $"{z.Low} - {z.High + size}";

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _sessions.Count == 0) return;
        _tip.Clear();
        _summaryBox = Rectangle.Empty;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var container = ChartInfo.PriceChartContainer;
        decimal size = RowSize;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        int chartRight = ChartInfo.GetXByBar(last, true) + container.BarsWidth;

        foreach (var s in _sessions)
        {
            if (s.EndBar < first || s.StartBar > last || s.Result is not { } r) continue;
            int x0 = Math.Max(6, ChartInfo.GetXByBar(Math.Max(s.StartBar, first), true));
            int x1 = ChartInfo.GetXByBar(s.EndBar, true) + container.BarsWidth;
            if (x1 <= x0) continue;
            int Y(decimal price) => ChartInfo.GetYByPrice(price, true);
            void Band(Zone z, Color color, string label, string tip1, params string[] more)
            {
                int top = Y(z.High + size), bottom = Y(z.Low);
                var rect = new Rectangle(x0, Math.Min(top, bottom), Math.Max(1, x1 - x0), Math.Max(2, Math.Abs(bottom - top)));
                context.FillRectangle(Color.FromArgb(46, color.R, color.G, color.B), rect);
                context.DrawString(label, font, Color.FromArgb(220, color.R, color.G, color.B), x1 - 6 - label.Length * 6, rect.Y + 1);
                _tip.Add(rect, color, new[] { tip1 }.Concat(more).ToArray());
            }

            if (ShowNodes)
            {
                foreach (var z in r.HighVolumeNodes) Band(z, Color.FromArgb(255, 60, 190, 170), "HVN", $"High volume node {Range(z, size)}", "the market traded heavily here", "price tends to slow down and rotate");
                foreach (var z in r.LowVolumeNodes) Band(z, Color.FromArgb(255, 230, 80, 100), "LVN", $"Low volume node {Range(z, size)}", "the market rejected these prices", "price tends to move through fast; the edges often act as support or resistance");
            }

            if (ShowSingles)
                foreach (var z in r.SinglePrints) Band(z, Color.FromArgb(255, 240, 90, 150), "SP", $"Single prints {Range(z, size)}", "only one letter here: price moved too fast to trade", "an imbalance; the market often comes back to fill it");

            if (ShowTails)
            {
                if (r.TailHigh is { } th) Band(th, Color.FromArgb(255, 245, 160, 60), "TAIL", $"Tail at the high {Range(th, size)}", "excess: the auction went too far and was rejected", "this end is probably done");
                if (r.TailLow is { } tl) Band(tl, Color.FromArgb(255, 245, 160, 60), "TAIL", $"Tail at the low {Range(tl, size)}", "excess: the auction went too far and was rejected", "this end is probably done");
                var edge = new RenderPen(Color.FromArgb(220, 255, 120, 120), 2f);
                if (r.PoorHigh) { int y = Y(s.Rows[^1] + size); context.DrawLine(edge, x0, y, x1, y); context.DrawString("POOR HIGH", font, Color.FromArgb(230, 255, 120, 120), x1 - 66, y - 12); _tip.Add(new Rectangle(x0, y - 2, x1 - x0, 5), Color.IndianRed, "Poor high", "a flat edge with several letters and no tail", "unfinished business: price often comes back to it"); }
                if (r.PoorLow) { int y = Y(s.Rows[0]); context.DrawLine(edge, x0, y, x1, y); context.DrawString("POOR LOW", font, Color.FromArgb(230, 255, 120, 120), x1 - 62, y + 2); _tip.Add(new Rectangle(x0, y - 2, x1 - x0, 5), Color.IndianRed, "Poor low", "a flat edge with several letters and no tail", "unfinished business: price often comes back to it"); }
            }

            if (ShowIb && r.IbHigh > 0)
            {
                var pen = new RenderPen(Color.FromArgb(180, 190, 190, 200), 1f, System.Drawing.Drawing2D.DashStyle.Dot);
                int yh = Y(r.IbHigh + size), yl = Y(r.IbLow);
                context.DrawLine(pen, x0, yh, x1, yh);
                context.DrawLine(pen, x0, yl, x1, yl);
                context.DrawString("IB high", font, Color.FromArgb(200, 190, 190, 200), x0 + 2, yh - 12);
                context.DrawString("IB low", font, Color.FromArgb(200, 190, 190, 200), x0 + 2, yl + 1);
                _tip.Add(new Rectangle(x0, yh - 2, x1 - x0, 5), Color.Gainsboro, "Initial balance high", $"range of the first {IbPeriods * _periodMinutes} minutes: {r.IbLow} - {r.IbHigh + size}", "how far the early traders pushed");
                _tip.Add(new Rectangle(x0, yl - 2, x1 - x0, 5), Color.Gainsboro, "Initial balance low", $"range of the first {IbPeriods * _periodMinutes} minutes: {r.IbLow} - {r.IbHigh + size}");
                if (r.ExtendedUp) { context.DrawString("RE up", font, Color.FromArgb(240, 120, 220, 160), x0 + 2, yh - 24); _tip.Add(new Rectangle(x0, yh - 26, 40, 14), Color.LightGreen, "Range extension up", "price broke above the initial balance", "longer-term traders stepped in"); }
                if (r.ExtendedDown) { context.DrawString("RE down", font, Color.FromArgb(240, 240, 130, 130), x0 + 2, yl + 11); _tip.Add(new Rectangle(x0, yl + 10, 46, 14), Color.IndianRed, "Range extension down", "price broke below the initial balance", "longer-term traders stepped in"); }
            }

            if (ShowPocs)
            {
                int ytp = Y(r.TpoPoc + size / 2), yvp = Y(r.VolumePoc + size / 2);
                if (!r.PocsAligned)
                {
                    int top = Y(r.FairValue.High + size), bottom = Y(r.FairValue.Low);
                    var rect = new Rectangle(x0, Math.Min(top, bottom), x1 - x0, Math.Max(2, Math.Abs(bottom - top)));
                    context.FillRectangle(Color.FromArgb(34, 255, 220, 60), rect);
                    _tip.Add(rect, Color.Gold, "Fair value zone", $"between the two POCs: {Range(r.FairValue, size)}", "time here, money there: value is less settled", "the volume POC is the stronger level");
                }

                context.DrawLine(new RenderPen(Color.FromArgb(230, 255, 196, 0), 1.5f), x0, ytp, x1, ytp);
                context.DrawLine(new RenderPen(Color.FromArgb(230, 255, 120, 40), 1.5f, System.Drawing.Drawing2D.DashStyle.Dash), x0, yvp, x1, yvp);
                context.DrawString(r.PocsAligned ? "tPOC = vPOC" : "tPOC", font, Color.FromArgb(240, 255, 196, 0), x1 - 8 - (r.PocsAligned ? 66 : 28), ytp - 12);
                if (!r.PocsAligned) context.DrawString("vPOC", font, Color.FromArgb(240, 255, 120, 40), x1 - 36, yvp - 12);
                _tip.Add(new Rectangle(x0, ytp - 2, x1 - x0, 5), Color.Gold, "TPO point of control", $"{r.TpoPoc}: the most time, the fairest price", r.PocsAligned ? "same price as the volume POC: time and money agree, the strongest fair price" : "different from the volume POC");
                _tip.Add(new Rectangle(x0, yvp - 2, x1 - x0, 5), Color.OrangeRed, "Volume point of control", $"{r.VolumePoc}: the most contracts, where the most business was done");
            }

            if (ShowCases)
                for (int i = 0; i < s.Rows.Length; i++)
                {
                    if (r.Cases[i] == RowCase.Regulars) continue;
                    var (col, name, meaning) = r.Cases[i] switch
                    {
                        RowCase.Rush => (Color.FromArgb(255, 230, 80, 100), "The rush", "little time, little volume: a confirmed imbalance, price moves through fast"),
                        RowCase.Camper => (Color.FromArgb(255, 90, 170, 230), "The camper", "lots of time but little volume: nobody committed size, a weak level that is more likely to break than hold"),
                        _ => (Color.FromArgb(255, 245, 200, 60), "The big spender", "little time but heavy volume: big orders filled here, often defended on the first retest"),
                    };
                    int top = Y(s.Rows[i] + size), bottom = Y(s.Rows[i]);
                    var rect = new Rectangle(x1 + 2, Math.Min(top, bottom), 5, Math.Max(2, Math.Abs(bottom - top) - 1));
                    context.FillRectangle(col, rect);
                    _tip.Add(rect, col, $"{name} at {s.Rows[i]}", meaning);
                }

            if (ShowAcceptance && s.RefVah > 0 && s.Above != null && s.Below != null)
            {
                void Level(decimal price, AcceptanceResult a, string side)
                {
                    if (a.State == "inside") return;
                    var col = a.State switch { "accepted" => Color.FromArgb(240, 80, 210, 140), "drifting" => Color.FromArgb(240, 245, 170, 60), "rejected" => Color.FromArgb(240, 240, 110, 110), _ => Color.FromArgb(240, 200, 200, 210) };
                    int y = Y(price);
                    context.DrawLine(new RenderPen(Color.FromArgb(150, col.R, col.G, col.B), 1f, System.Drawing.Drawing2D.DashStyle.Dash), x0, y, x1, y);
                    string text = $"{side} previous value: {a.State.ToUpperInvariant()} ({a.PeriodsBeyond} period{(a.PeriodsBeyond == 1 ? "" : "s")}, {100 * a.VolumeShare:0}% of the volume)";
                    context.DrawString(text, font, col, x0 + 70, y - 12);
                    _tip.Add(new Rectangle(x0, y - 3, x1 - x0, 7), col, text, a.State switch
                    {
                        "accepted" => "price held beyond the level for the required time and volume is building there: time and money agree",
                        "drifting" => "time without volume: drifting, not accepted; it often slides back into value",
                        "rejected" => "price went beyond the level and came back inside",
                        _ => "not yet long enough to call it accepted",
                    });
                }

                Level(s.RefVah, s.Above, "above");
                Level(s.RefVal, s.Below, "below");
            }
        }

        if (ShowSummary && _sessions[^1].Result is { } cur && _sessions[^1].EndBar >= first)
        {
            var s = _sessions[^1];
            var lines = SummaryLines(s);
            lines.AddRange(HistoryLines(s));
            // long lines are wrapped so the panel stays narrow
            var wrapped = new List<string>();
            foreach (var line in lines)
            {
                var words = line.Split(' ');
                var cur2 = "";
                foreach (var word in words)
                {
                    if (cur2.Length + word.Length + 1 > 74 && cur2.Length > 0) { wrapped.Add(cur2); cur2 = "  " + word; }
                    else cur2 = cur2.Length == 0 ? word : cur2 + " " + word;
                }

                wrapped.Add(cur2);
            }

            lines = wrapped;
            var panelFont = new RenderFont("Arial", 9);
            int w = (int)(lines.Max(l => l.Length) * 6.9) + 18, h = lines.Count * 15 + 10;
            var box = new Rectangle(Math.Min(70, Math.Max(4, chartRight - w - 4)), 96, w, h);
            _summaryBox = box;
            context.FillRectangle(Color.FromArgb(238, 8, 10, 14), box);
            for (int i = 0; i < lines.Count; i++) context.DrawString(lines[i], panelFont, Color.FromArgb(235, 230, 232, 238), box.X + 8, box.Y + 5 + i * 15);
        }

        if (ShowUnfinished)
        {
            foreach (var o in _open.Where(o => !o.Filled))
            {
                var col = o.Kind switch
                {
                    "naked volume POC" => Color.FromArgb(255, 255, 120, 40),
                    "naked TPO POC" => Color.FromArgb(255, 255, 196, 0),
                    "single prints" => Color.FromArgb(255, 240, 90, 150),
                    _ => Color.FromArgb(255, 255, 120, 120),
                };
                int x = Math.Max(6, ChartInfo.GetXByBar(Math.Max(o.CreatedBar, first), true));
                int top = ChartInfo.GetYByPrice(o.High, true), bottom = ChartInfo.GetYByPrice(o.Low, true);
                if (o.Created(last)) continue;
                if (o.High > o.Low) context.FillRectangle(Color.FromArgb(40, col.R, col.G, col.B), new Rectangle(x, Math.Min(top, bottom), Math.Max(1, chartRight - x), Math.Max(2, Math.Abs(bottom - top))));
                context.DrawLine(new RenderPen(Color.FromArgb(200, col.R, col.G, col.B), 1.2f, System.Drawing.Drawing2D.DashStyle.Dash), x, bottom, chartRight, bottom);
                string label = $"{o.Kind} {AgeOf(o)}";
                context.DrawString(label, font, col, chartRight - label.Length * 6 - 8, bottom - 12);
                _tip.Add(new Rectangle(x, Math.Min(top, bottom) - 2, Math.Max(1, chartRight - x), Math.Abs(bottom - top) + 5), col, $"Unfinished business: {o.Kind}",
                    o.Low == o.High ? $"{o.Low}" : $"{o.Low} - {o.High}", $"left {AgeOf(o)} sessions ago, not traded back to since", "the method expects price to return to it");
            }
        }

        if (ShowBoard)
        {
            var board = Board();
            if (board.Count > 0)
            {
                var bf = new RenderFont("Arial", 9);
                string Line(BoardRow b) => $"{b.Name,-22} {b.Price,10} {b.Distance,6:+0;-0;0}t {b.State,-9}{(b.Signals.Length > 0 ? " " + b.Signals : "")}";
                int w = 360, h = board.Count * 15 + 24;
                var box = new Rectangle(Math.Max(4, chartRight - w - 6), 96, w, h);
                // on a narrow chart the board would sit on the summary: put it underneath
                if (!_summaryBox.IsEmpty && box.IntersectsWith(_summaryBox)) box.Y = _summaryBox.Bottom + 6;
                context.FillRectangle(Color.FromArgb(238, 8, 10, 14), box);
                context.DrawString("Levels near price (ticks away · acceptance · signals)", bf, Color.FromArgb(220, 150, 160, 180), box.X + 8, box.Y + 4);
                for (int i = 0; i < board.Count; i++)
                    context.DrawString(Line(board[i]).Replace("  ", " "), new RenderFont("Consolas", 9), board[i].Signals.Length > 0 ? Color.FromArgb(255, 255, 215, 100) : Color.FromArgb(235, 215, 218, 226), box.X + 8, box.Y + 20 + i * 15);
            }
        }

        _tip.Draw(context, chartRight);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }
}
