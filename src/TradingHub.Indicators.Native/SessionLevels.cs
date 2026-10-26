using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// High and low of the Asian, London and New York sessions and of the New York opening range, drawn to the
/// right for today and the two days before. The session hours are fixed UTC hours (they do not follow daylight saving).
/// </summary>
[DisplayName("Session highs and lows")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class SessionLevels : Indicator
{
    private sealed class Sess
    {
        public DateOnly Day;
        public string Name = "";
        public Color Color;
        public int StartBar;
        public decimal Hi = decimal.MinValue, Lo = decimal.MaxValue;
    }

    private readonly List<Sess> _sessions = new();
    private readonly HoverTip _tip = new();

    public SessionLevels() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Asian", GroupName = "Show", Order = 10)] public bool ShowAsia { get; set; } = true;
    [Display(Name = "London", GroupName = "Show", Order = 11)] public bool ShowLondon { get; set; } = true;
    [Display(Name = "New York", GroupName = "Show", Order = 12)] public bool ShowNewYork { get; set; } = true;
    [Display(Name = "New York opening range", GroupName = "Show", Order = 13)] public bool ShowOpeningRange { get; set; } = true;

    [Display(Name = "Asian start (UTC hour)", GroupName = "Hours", Order = 20)] [Range(0, 23)] public int AsiaFrom { get; set; } = 0;
    [Display(Name = "Asian end (UTC hour)", GroupName = "Hours", Order = 21)] [Range(1, 24)] public int AsiaTo { get; set; } = 8;
    [Display(Name = "London start (UTC hour)", GroupName = "Hours", Order = 22)] [Range(0, 23)] public int LondonFrom { get; set; } = 8;
    [Display(Name = "London end (UTC hour)", GroupName = "Hours", Order = 23)] [Range(1, 24)] public int LondonTo { get; set; } = 13;
    [Display(Name = "New York start (UTC hour)", GroupName = "Hours", Order = 24)] [Range(0, 23)] public int NewYorkFrom { get; set; } = 13;
    [Display(Name = "New York end (UTC hour)", GroupName = "Hours", Order = 25)] [Range(1, 24)] public int NewYorkTo { get; set; } = 21;

    [Display(Name = "Opening range starts (UTC, minutes after midnight)", GroupName = "Hours", Order = 30,
        Description = "810 = 13:30 UTC, the US cash open when daylight saving is on (870 = 14:30 when it is off).")]
    [Range(0, 1439)]
    public int OpeningRangeStart { get; set; } = 810;

    [Display(Name = "Opening range length (minutes)", GroupName = "Hours", Order = 31)]
    [Range(1, 240)]
    public int OpeningRangeMinutes { get; set; } = 30;

    protected override void OnRecalculate() => _sessions.Clear();

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        var t = DateTime.SpecifyKind(c.Time, DateTimeKind.Utc);
        var day = DateOnly.FromDateTime(t);
        int hour = t.Hour, minute = hour * 60 + t.Minute;
        if (ShowAsia && hour >= AsiaFrom && hour < AsiaTo) Add(day, "Asia", Color.FromArgb(220, 150, 130, 230), bar, c);
        if (ShowLondon && hour >= LondonFrom && hour < LondonTo) Add(day, "London", Color.FromArgb(220, 90, 170, 230), bar, c);
        if (ShowNewYork && hour >= NewYorkFrom && hour < NewYorkTo) Add(day, "NY", Color.FromArgb(220, 240, 170, 60), bar, c);
        if (ShowOpeningRange && minute >= OpeningRangeStart && minute < OpeningRangeStart + OpeningRangeMinutes) Add(day, "OR", Color.FromArgb(220, 230, 100, 160), bar, c);
    }

    private void Add(DateOnly day, string name, Color color, int bar, IndicatorCandle c)
    {
        var s = _sessions.LastOrDefault(x => x.Day == day && x.Name == name);
        if (s == null)
        {
            s = new Sess { Day = day, Name = name, Color = color, StartBar = bar };
            _sessions.Add(s);
            _sessions.RemoveAll(x => x.Day < day.AddDays(-2)); // today and the two days before
        }

        s.Hi = Math.Max(s.Hi, c.High);
        s.Lo = Math.Min(s.Lo, c.Low);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _sessions.Count == 0) return;
        _tip.Clear();
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        int right = ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        foreach (var s in _sessions)
        {
            if (s.StartBar > last || s.Hi < s.Lo) continue;
            int x0 = ChartInfo.GetXByBar(Math.Max(s.StartBar, first), true);
            var pen = new RenderPen(s.Color);
            string tag = s.Name + (s.Day == _sessions[^1].Day ? "" : $" {s.Day:dd/MM}");
            foreach (var (price, kind) in new[] { (s.Hi, "H"), (s.Lo, "L") })
            {
                int y = ChartInfo.GetYByPrice(price, false);
                context.DrawLine(pen, x0, y, right, y);
                context.DrawString($"{tag} {kind} {price}", font, s.Color, x0 + 4, y - 13);
                _tip.Add(new Rectangle(x0, y - 1, Math.Max(1, right - x0), 3), s.Color, $"{s.Name} {(kind == "H" ? "high" : "low")}", $"{price}", $"{s.Day:dddd dd/MM} (UTC session)");
            }
        }

        _tip.Draw(context, right);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }

}
