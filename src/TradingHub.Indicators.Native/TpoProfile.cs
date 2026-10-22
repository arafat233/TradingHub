using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// TPO (market profile) per session: the day is cut into periods (30 minutes by default); each price row gets the
/// letter of every period that traded there (A = first period, B = second…). The row with the most letters is the
/// POC, the value area holds the chosen percentage of the letters around it, and the initial balance is the range of
/// the first two periods. It works from the candles' high and low: use candles no longer than the period (1, 5 or 15
/// minutes for 30-minute periods); a longer candle counts in the period it opens in.
/// </summary>
[DisplayName("TPO profile (market profile)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class TpoProfile : Indicator
{
    public enum TpoPeriod
    {
        [Display(Name = "Session (day)")] Session,
        [Display(Name = "Week")] Week,
    }

    /// <summary>One profile's result (for the legend of whoever reads it, and for the tests).</summary>
    public sealed record Summary(int StartBar, int EndBar, decimal Poc, decimal ValueAreaHigh, decimal ValueAreaLow,
        decimal InitialBalanceHigh, decimal InitialBalanceLow, int Periods, int Tpos);

    private sealed class Profile
    {
        public int StartBar, EndBar;
        public DateTime Start;
        public readonly Dictionary<decimal, SortedSet<int>> Rows = new();
        public decimal Poc, Vah, Val, IbHigh = decimal.MinValue, IbLow = decimal.MaxValue;
        public int MaxCount, Periods, Tpos;
    }

    private readonly List<Profile> _profiles = new();
    private TpoPeriod _period = TpoPeriod.Session;
    private int _minutes = 30;
    private int _ticksPerRow = 4;
    private int _valueArea = 70;

    public TpoProfile() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Profile per", GroupName = "Calculation", Order = 10)]
    public TpoPeriod Period { get => _period; set { _period = value; RecalculateValues(); } }

    [Display(Name = "Letter every (minutes)", GroupName = "Calculation", Order = 20)]
    [Range(1, 240)]
    public int PeriodMinutes { get => _minutes; set { _minutes = Math.Clamp(value, 1, 240); RecalculateValues(); } }

    [Display(Name = "Ticks per row", GroupName = "Calculation", Order = 30,
        Description = "Prices are grouped in rows of this many ticks: 4 is one point on ES and NQ. Letters need rows about 10 px tall, so zoom in or raise this on a coarse price scale.")]
    [Range(1, 1000)]
    public int TicksPerRow { get => _ticksPerRow; set { _ticksPerRow = Math.Clamp(value, 1, 1000); RecalculateValues(); } }

    [Display(Name = "Value area (%)", GroupName = "Calculation", Order = 40)]
    [Range(10, 100)]
    public int ValueAreaPercent { get => _valueArea; set { _valueArea = Math.Clamp(value, 10, 100); RecalculateValues(); } }

    [Display(Name = "Maximum width (% of the session)", GroupName = "Display", Order = 50)]
    [Range(5, 100)]
    public int WidthPercent { get; set; } = 80;

    [Display(Name = "Letter color", GroupName = "Display", Order = 60)]
    public Color LetterColor { get; set; } = Color.FromArgb(200, 150, 165, 190);

    [Display(Name = "Value area color", GroupName = "Display", Order = 70)]
    public Color ValueAreaColor { get; set; } = Color.FromArgb(255, 120, 170, 240);

    [Display(Name = "POC color", GroupName = "Display", Order = 80)]
    public Color PocColor { get; set; } = Color.FromArgb(255, 255, 196, 0);

    [Display(Name = "Show initial balance", GroupName = "Display", Order = 90)]
    public bool ShowInitialBalance { get; set; } = true;

    /// <summary>The profiles calculated so far, oldest first.</summary>
    public IReadOnlyList<Summary> Profiles =>
        _profiles.Select(p => new Summary(p.StartBar, p.EndBar, p.Poc, p.Vah, p.Val, p.IbHigh, p.IbLow, p.Periods, p.Tpos)).ToList();

    private decimal Tick => InstrumentInfo is { TickSize: > 0 } i ? i.TickSize : 1m;
    private decimal RowSize => Tick * _ticksPerRow;
    private decimal RowOf(decimal price) => Math.Floor(price / RowSize) * RowSize;

    protected override void OnRecalculate() => _profiles.Clear();

    protected override void OnCalculate(int bar, decimal value)
    {
        bool isNew = bar == 0 || _profiles.Count == 0 || _period switch
        {
            TpoPeriod.Session => IsNewSession(bar),
            _ => IsNewWeek(bar),
        };

        if (isNew && (_profiles.Count == 0 || _profiles[^1].StartBar != bar))
            _profiles.Add(new Profile { StartBar = bar, EndBar = bar, Start = GetCandle(bar).Time });

        var p = _profiles[^1];
        var c = GetCandle(bar);
        int slot = Math.Max(0, (int)((c.Time - p.Start).TotalMinutes / _minutes));
        decimal size = RowSize;
        // a candle that is still forming only ever widens its range, so adding it again on every tick is harmless (a set)
        for (decimal row = RowOf(c.Low); row <= RowOf(c.High); row += size)
        {
            if (!p.Rows.TryGetValue(row, out var slots)) p.Rows[row] = slots = new SortedSet<int>();
            slots.Add(slot);
        }

        if (slot <= 1)
        {
            p.IbHigh = Math.Max(p.IbHigh, c.High);
            p.IbLow = Math.Min(p.IbLow, c.Low);
        }

        p.EndBar = bar;
        Finish(p);
    }

    private void Finish(Profile p)
    {
        if (p.Rows.Count == 0) return;
        var rows = p.Rows.Keys.OrderBy(k => k).ToList();
        decimal middle = (rows[0] + rows[^1]) / 2;
        p.MaxCount = 0;
        p.Tpos = 0;
        var periods = new HashSet<int>();
        foreach (var (row, slots) in p.Rows)
        {
            p.Tpos += slots.Count;
            periods.UnionWith(slots);
            // the biggest row wins; with a tie, the one closest to the middle of the day's range
            if (slots.Count > p.MaxCount || (slots.Count == p.MaxCount && Math.Abs(row - middle) < Math.Abs(p.Poc - middle)))
            {
                p.MaxCount = slots.Count;
                p.Poc = row;
            }
        }

        p.Periods = periods.Count;

        // value area: it grows from the POC towards the side with more letters until it holds the chosen percentage
        int pocIdx = rows.IndexOf(p.Poc), up = pocIdx, dn = pocIdx;
        int acc = p.Rows[p.Poc].Count;
        double target = p.Tpos * _valueArea / 100.0;
        while (acc < target && (up < rows.Count - 1 || dn > 0))
        {
            int cu = up < rows.Count - 1 ? p.Rows[rows[up + 1]].Count : -1;
            int cd = dn > 0 ? p.Rows[rows[dn - 1]].Count : -1;
            if (cu >= cd) acc += p.Rows[rows[++up]].Count;
            else acc += p.Rows[rows[--dn]].Count;
        }

        p.Vah = rows[up];
        p.Val = rows[dn];
    }

    /// <summary>A, B, C… Z, then a, b, c… z, and around again.</summary>
    public static char LetterOf(int slot)
    {
        int i = slot % 52;
        return (char)(i < 26 ? 'A' + i : 'a' + i - 26);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _profiles.Count == 0) return;
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        var container = ChartInfo.PriceChartContainer;
        decimal tick = Tick, size = RowSize;
        // the real height of a row on screen (the chart's tick height is clamped to 1 px when it is zoomed out)
        decimal refPrice = _profiles[^1].Poc;
        int h = Math.Max(1, Math.Abs(ChartInfo.GetYByPrice(refPrice + size, true) - ChartInfo.GetYByPrice(refPrice, true)));
        int rowH = Math.Max(1, h / _ticksPerRow);
        float fontSize = Math.Clamp(h * 0.62f, 6f, 13f);
        var font = new RenderFont("Arial", fontSize);

        foreach (var p in _profiles)
        {
            if (p.EndBar < first || p.StartBar > last || p.MaxCount <= 0) continue;
            // a session that began off the left edge starts at the first visible candle, so its letters are on screen
            int x0 = Math.Max(6, ChartInfo.GetXByBar(Math.Max(p.StartBar, first), true));
            int x1 = ChartInfo.GetXByBar(p.EndBar, true) + container.BarsWidth;
            int maxW = Math.Max(10, (x1 - x0) * WidthPercent / 100);
            // letters are as wide as the font, or narrower if the busiest row would not fit
            float letterW = Math.Min(fontSize * 0.85f, maxW / (float)p.MaxCount);

            foreach (var (row, slots) in p.Rows)
            {
                int y = ChartInfo.GetYByPrice(row + size - tick, true);
                var color = row == p.Poc ? PocColor : row <= p.Vah && row >= p.Val ? ValueAreaColor : LetterColor;
                int n = 0;
                foreach (int slot in slots)
                {
                    float x = x0 + n++ * letterW;
                    if (letterW >= 5 && h >= 8) context.DrawString(LetterOf(slot).ToString(), font, color, x, y + Math.Max(0f, (h - fontSize * 1.4f) / 2));
                    else context.FillRectangle(Color.FromArgb(120, color), new Rectangle((int)x, y, Math.Max(1, (int)letterW - 1), Math.Max(1, h - 1))); // too small for letters: blocks
                }
            }

            int yPoc = ChartInfo.GetYByPrice(p.Poc + size - tick, true) + h / 2;
            context.DrawLine(new RenderPen(PocColor, 1), x0, yPoc, x1, yPoc);
            var dash = new RenderPen(ValueAreaColor, 1, System.Drawing.Drawing2D.DashStyle.Dash);
            int yVah = ChartInfo.GetYByPrice(p.Vah + size - tick, true);
            int yVal = ChartInfo.GetYByPrice(p.Val, true) + rowH;
            context.DrawLine(dash, x0, yVah, x1, yVah);
            context.DrawLine(dash, x0, yVal, x1, yVal);
            if (ShowInitialBalance && p.IbHigh > p.IbLow)
            {
                var ib = new RenderPen(Color.FromArgb(160, 180, 180, 190), 1, System.Drawing.Drawing2D.DashStyle.Dot);
                int yh = ChartInfo.GetYByPrice(p.IbHigh, true), yl = ChartInfo.GetYByPrice(p.IbLow, true) + rowH;
                context.DrawLine(ib, x0, yh, x1, yh);
                context.DrawLine(ib, x0, yl, x1, yl);
            }
        }
    }
}
