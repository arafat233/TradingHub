using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Data;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// A composite volume profile over the last several sessions (5 by default, a week), drawn as a histogram at the right edge with the
/// composite POC, value area and the thickest and thinnest zones (high / low volume nodes) carried across the chart. The bigger the
/// composite, the bigger the reference levels it gives.
/// </summary>
[DisplayName("Composite volume profile (days)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class CompositeProfile : Indicator
{
    private AuctionInput? _input;
    private AuctionResult? _result;
    private int _sessionsUsed;
    private DateTime _at = DateTime.MinValue;
    private readonly HoverTip _tip = new();

    public CompositeProfile() : base(true)
    {
        DataSeries[0].IsHidden = true;
        ((ValueDataSeries)DataSeries[0]).VisualType = VisualMode.Hide;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = false;
    }

    [Display(Name = "Sessions", GroupName = "Calculation", Order = 10, Description = "How many sessions (days) the composite covers, counting the current one. 5 is about a week.")]
    [Range(1, 60)]
    public int Sessions { get; set; } = 5;

    [Display(Name = "Ticks per row", GroupName = "Calculation", Order = 20)]
    [Range(1, 1000)]
    public int TicksPerRow { get; set; } = 4;

    [Display(Name = "Value area (%)", GroupName = "Calculation", Order = 30)]
    [Range(10, 100)]
    public int ValueAreaPercent { get; set; } = 70;

    [Display(Name = "Nodes kept (each kind)", GroupName = "Calculation", Order = 40)]
    [Range(1, 10)]
    public int Nodes { get; set; } = 3;

    [Display(Name = "Width (px)", GroupName = "Display", Order = 50)]
    [Range(30, 600)]
    public int Width { get; set; } = 150;

    [Display(Name = "Bar color", GroupName = "Display", Order = 60)]
    public Color BarColor { get; set; } = Color.FromArgb(110, 120, 150, 200);

    [Display(Name = "Draw the levels across the chart", GroupName = "Display", Order = 70)]
    public bool ShowLines { get; set; } = true;

    protected override void OnRecalculate()
    {
        _input = null;
        _result = null;
        _at = DateTime.MinValue;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar != CurrentBar - 1 || DateTime.UtcNow - _at < TimeSpan.FromSeconds(8)) return;
        _at = DateTime.UtcNow;
        int start = bar, seen = 1;
        while (start > 0 && seen < Sessions)
        {
            if (IsNewSession(start)) seen++;
            start--;
        }

        _sessionsUsed = seen;
        decimal size = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m) * TicksPerRow;
        var volume = new Dictionary<decimal, decimal>();
        for (int i = start; i <= bar; i++)
            foreach (var l in GetCandle(i).GetAllPriceLevels())
            {
                decimal row = Math.Floor(l.Price / size) * size;
                volume[row] = volume.GetValueOrDefault(row) + l.Bid + l.Ask;
            }

        if (volume.Count < 5) return;
        var rows = new List<decimal>();
        for (decimal r = volume.Keys.Min(); r <= volume.Keys.Max(); r += size) rows.Add(r);
        _input = new AuctionInput(rows.ToArray(), rows.Select(_ => Array.Empty<int>()).ToArray(), rows.Select(r => volume.GetValueOrDefault(r)).ToArray(), size);
        _result = AuctionAnalysis.Analyze(_input, ValueAreaPercent, maxNodes: Nodes);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || _input == null || _result == null) return;
        _tip.Clear();
        int right = ChartInfo.GetXByBar(LastVisibleBarNumber, true) + ChartInfo.PriceChartContainer.BarsWidth;
        int left = Math.Max(4, ChartInfo.GetXByBar(FirstVisibleBarNumber, true));
        decimal size = _input.RowSize, max = _input.Volume.Max();
        if (max <= 0) return;
        var font = new RenderFont("Arial", 8, FontStyle.Bold);
        var r = _result;

        if (ShowLines)
        {
            foreach (var z in r.HighVolumeNodes)
            {
                int top = ChartInfo.GetYByPrice(z.High + size, true), bottom = ChartInfo.GetYByPrice(z.Low, true);
                var rect = new Rectangle(left, Math.Min(top, bottom), right - left, Math.Max(2, Math.Abs(bottom - top)));
                context.FillRectangle(Color.FromArgb(34, 60, 190, 170), rect);
                context.DrawString($"cHVN {z.Low} - {z.High + size}", font, Color.FromArgb(200, 60, 190, 170), left + 4, rect.Y + 1);
                _tip.Add(rect, Color.MediumAquamarine, "Composite high volume node", $"{z.Low} - {z.High + size}", $"over the last {_sessionsUsed} sessions", "price tends to slow down and rotate here");
            }

            foreach (var z in r.LowVolumeNodes)
            {
                foreach (var price in new[] { z.Low, z.High + size })
                {
                    int y = ChartInfo.GetYByPrice(price, true);
                    context.DrawLine(new RenderPen(Color.FromArgb(170, 230, 80, 100), 1f, System.Drawing.Drawing2D.DashStyle.Dot), left, y, right, y);
                    _tip.Add(new Rectangle(left, y - 2, right - left, 5), Color.IndianRed, "Composite low volume node edge", $"{z.Low} - {z.High + size}", "price tends to move through the zone fast; its edges often act as support or resistance");
                }

                context.DrawString($"cLVN {z.Low} - {z.High + size}", font, Color.FromArgb(200, 230, 80, 100), left + 4, ChartInfo.GetYByPrice(z.High + size, true) + 1);
            }

            foreach (var (label, price, color, dash) in new[]
            {
                ("cPOC", r.VolumePoc + size / 2, Color.FromArgb(240, 255, 196, 0), System.Drawing.Drawing2D.DashStyle.Solid),
                ("cVAH", r.VolumeVah + size, Color.FromArgb(200, 120, 170, 240), System.Drawing.Drawing2D.DashStyle.Dash),
                ("cVAL", r.VolumeVal, Color.FromArgb(200, 120, 170, 240), System.Drawing.Drawing2D.DashStyle.Dash),
            })
            {
                int y = ChartInfo.GetYByPrice(price, true);
                context.DrawLine(new RenderPen(color, 1.2f, dash), left, y, right, y);
                context.DrawString($"{label} {price}", font, color, right - Width - 74, y - 12);
                _tip.Add(new Rectangle(left, y - 2, right - left, 5), color, $"Composite {label.Substring(1)}", $"{price}", $"over the last {_sessionsUsed} sessions");
            }
        }

        for (int i = 0; i < _input.Rows.Length; i++)
        {
            int top = ChartInfo.GetYByPrice(_input.Rows[i] + size, true), bottom = ChartInfo.GetYByPrice(_input.Rows[i], true);
            int w = (int)(Width * _input.Volume[i] / max);
            var rect = new Rectangle(right - w, Math.Min(top, bottom), Math.Max(1, w), Math.Max(2, Math.Abs(bottom - top) - 1));
            context.FillRectangle(_input.Rows[i] == r.VolumePoc ? Color.FromArgb(200, 255, 196, 0) : BarColor, rect);
            _tip.Add(rect, BarColor, $"{_input.Rows[i]} - {_input.Rows[i] + size}", $"volume {_input.Volume[i]:0.##}", $"over the last {_sessionsUsed} sessions");
        }

        // where price is against the nodes: the next high volume node either way (natural targets), and whether it is inside a thin zone
        if (CurrentBar > 0)
        {
            decimal price = GetCandle(CurrentBar - 1).Close, tick = Math.Max(InstrumentInfo?.TickSize ?? 0, 0.0000001m);
            var lines = new List<string>();
            var up = r.HighVolumeNodes.Where(z => z.Low > price).OrderBy(z => z.Low).FirstOrDefault();
            var down = r.HighVolumeNodes.Where(z => z.High + size < price).OrderByDescending(z => z.High).FirstOrDefault();
            if (up != null) lines.Add($"Next high volume node above: {up.Low} - {up.High + size} ({(up.Low - price) / tick:0} ticks)");
            if (down != null) lines.Add($"Next high volume node below: {down.Low} - {down.High + size} ({(price - (down.High + size)) / tick:0} ticks)");
            var inLvn = r.LowVolumeNodes.FirstOrDefault(z => price >= z.Low && price <= z.High + size);
            if (inLvn != null) lines.Add($"Price is inside a low volume node ({inLvn.Low} - {inLvn.High + size}): thin zone, price has tended to cross it fast");
            if (lines.Count > 0)
            {
                var f = new RenderFont("Arial", 9);
                int w = (int)(lines.Max(l => l.Length) * 6.6) + 16, h = lines.Count * 15 + 8;
                var box = new Rectangle(Math.Max(4, left + 4), 330, w, h);
                context.FillRectangle(Color.FromArgb(235, 8, 10, 14), box);
                for (int i = 0; i < lines.Count; i++) context.DrawString(lines[i], f, Color.FromArgb(235, 225, 228, 235), box.X + 8, box.Y + 4 + i * 15);
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
