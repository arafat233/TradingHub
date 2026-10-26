using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Draws the events of the user's events.txt on the chart (a dashed vertical line and the name), shows the next event
/// in a corner, and warns shortly before one starts. Nothing is bundled: the dates are the user's own.
/// </summary>
[DisplayName("Event markers")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class EventMarkers : Indicator
{
    private readonly HashSet<DateTime> _warned = new();
    private readonly HoverTip _tip = new();

    public EventMarkers() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
        EventCalendar.EnsureFile();
    }

    [Display(Name = "Warn before the event (minutes)", GroupName = "Alerts", Order = 10, Description = "One alert this long before an event starts (0 = no warning).")]
    [Range(0, 240)]
    public int WarnMinutes { get; set; } = 15;

    [Display(Name = "Color", GroupName = "Display", Order = 20)]
    public Color LineColor { get; set; } = Color.FromArgb(210, 255, 160, 60);

    protected override void OnRecalculate() => _warned.Clear();

    protected override void OnCalculate(int bar, decimal value)
    {
        if (WarnMinutes <= 0 || bar != CurrentBar - 1) return;
        var now = DateTime.UtcNow;
        foreach (var e in EventCalendar.All())
        {
            var ahead = e.Utc - now;
            if (ahead <= TimeSpan.Zero || ahead > TimeSpan.FromMinutes(WarnMinutes) || !_warned.Add(e.Utc)) continue;
            AddAlert("alert1", $"Event in {Math.Ceiling(ahead.TotalMinutes):0} min: {e.Name}");
        }
    }

    private int BarAt(DateTime utc)
    {
        int lo = 0, hi = CurrentBar - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (GetCandle(mid).Time <= utc) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        return ans;
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar == 0) return;
        _tip.Clear();
        int first = FirstVisibleBarNumber, last = LastVisibleBarNumber;
        int right = ChartInfo.GetXByBar(last, true) + ChartInfo.PriceChartContainer.BarsWidth;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        var pen = new RenderPen(LineColor, 1f, System.Drawing.Drawing2D.DashStyle.Dash);
        var area = ChartInfo.PriceChartContainer;
        foreach (var e in EventCalendar.All())
        {
            int bar = BarAt(e.Utc);
            if (bar < first || bar > last || e.Utc > GetCandle(CurrentBar - 1).LastTime) continue;
            int x = ChartInfo.GetXByBar(bar, true);
            context.DrawLine(pen, x, 0, x, 2000);
            context.DrawString(e.Name, font, LineColor, x + 4, 24);
            _tip.Add(new Rectangle(x - 2, 0, 5, 2000), LineColor, e.Name, $"{e.Utc.ToLocalTime():dd/MM HH:mm} local");
        }

        if (EventCalendar.Next(DateTime.UtcNow) is { } next)
        {
            var ahead = next.Utc - DateTime.UtcNow;
            string when = ahead.TotalHours >= 24 ? $"{ahead.TotalDays:0.#} days" : ahead.TotalMinutes >= 60 ? $"{(int)ahead.TotalHours} h {ahead.Minutes} min" : $"{Math.Max(0, (int)ahead.TotalMinutes)} min";
            context.DrawString($"Next event: {next.Name} in {when}", font, LineColor, 70, 40);
        }

        _tip.Draw(context, right);
    }

    public override bool ProcessMouseMove(RenderControlMouseEventArgs e)
    {
        if (_tip.Move(e.X, e.Y)) RedrawChart();
        return false;
    }
}
