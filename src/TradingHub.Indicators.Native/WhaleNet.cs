using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Control;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// What the big orders did, candle by candle, in money: the bars are the big buy orders minus the big sell orders of each candle, and the line is
/// the running total from the first candle on the chart. A "big order" is one in the top <see cref="Percentile"/> percent by size of the orders
/// seen recently (a sweep that keeps eating levels counts once, as the order it is), so it adapts to each market. It shows whether the whales
/// have been net buyers or sellers, which a single bubble on the chart cannot.
/// </summary>
[DisplayName("Whale net (big orders, money)")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class WhaleNet : Indicator
{
    private readonly ValueDataSeries _net = new("Net", "Net bought minus sold (big orders)") { VisualType = VisualMode.Histogram };
    private readonly ValueDataSeries _total = new("Total", "Running total") { Color = Color.FromArgb(255, 255, 196, 0), Width = 2, VisualType = VisualMode.Line };
    private readonly ValueDataSeries _crowd = new("Everyone else", "Net bought minus sold by everyone else (own scale)") { Color = Color.FromArgb(255, 90, 200, 240), Width = 1, VisualType = VisualMode.Hide, IsHidden = true };
    private readonly List<decimal> _byBar = new();
    private readonly List<decimal> _otherBar = new();
    private decimal _crowdScale = 1, _liveOtherAdded;
    private readonly Queue<decimal> _recent = new();
    private decimal _threshold;
    private bool _requested;
    private CumulativeTrade? _live;
    private decimal _liveAdded;
    private int _percentile = 99, _sample = 2000;

    public WhaleNet()
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _net;
        DataSeries.Add(_total);
        DataSeries.Add(_crowd);
        _total.IsHidden = true;
        _total.VisualType = VisualMode.Hide;
    }

    [Display(Name = "Big = top (percent of orders)", GroupName = "Calculation", Order = 10, Description = "An order is big when it is in this top share by size of the orders seen recently.")]
    [Range(50, 99)]
    public int Percentile { get => _percentile; set { _percentile = Math.Clamp(value, 50, 99); RecalculateValues(); } }

    [Display(Name = "Sample (recent orders)", GroupName = "Calculation", Order = 20)]
    [Range(100, 20000)]
    public int Sample { get => _sample; set { _sample = Math.Clamp(value, 100, 20000); RecalculateValues(); } }

    private bool _showTotal;

    [Display(Name = "Show the running total", GroupName = "Display", Order = 90,
        Description = "A line with the total since the first candle. It is in the same scale as the bars, so over a long chart it dwarfs them: off by default (the latest total is still in the title).")]
    public bool ShowTotal
    {
        get => _showTotal;
        set
        {
            _showTotal = value;
            _total.IsHidden = !value;
            _total.VisualType = value ? VisualMode.Line : VisualMode.Hide;
            RedrawChart();
        }
    }

    private bool _showCrowd;

    [Display(Name = "Compare with everyone else", GroupName = "Display", Order = 95,
        Description = "A dashed line with the running net of all the orders that are NOT big, drawn on its own scale (stretched to the size of the whale total) so you can see whether the crowd is doing the same as the whales or the opposite.")]
    public bool ShowCrowd
    {
        get => _showCrowd;
        set
        {
            _showCrowd = value;
            _crowd.IsHidden = !value;
            _crowd.VisualType = value ? VisualMode.Line : VisualMode.Hide;
            if (value) ShowTotal = true; // the two lines only make sense together
            RedrawChart();
        }
    }

    [Display(Name = "Buy color", GroupName = "Display", Order = 100)]
    public Color BuyColor { get; set; } = Color.FromArgb(255, 73, 209, 73);

    [Display(Name = "Sell color", GroupName = "Display", Order = 110)]
    public Color SellColor { get; set; } = Color.FromArgb(255, 232, 69, 72);

    protected override void OnRecalculate()
    {
        _byBar.Clear();
        _otherBar.Clear();
        _liveOtherAdded = 0;
        _crowdScale = 1;
        _recent.Clear();
        _threshold = 0;
        _requested = false;
        _live = null;
        _liveAdded = 0;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0 && !_requested && CurrentBar > 0)
        {
            _requested = true;
            RequestForCumulativeTrades(new CumulativeTradesRequest(GetCandle(0).Time));
        }

        while (_byBar.Count <= bar) _byBar.Add(0);
        while (_otherBar.Count <= bar) _otherBar.Add(0);
        Paint(bar);
    }

    private void Paint(int bar)
    {
        decimal total = 0;
        for (int i = 0; i <= bar && i < _byBar.Count; i++) total += _byBar[i];
        _net[bar] = bar < _byBar.Count ? _byBar[bar] : 0;
        _total[bar] = total;
        decimal crowd = 0;
        for (int i = 0; i <= bar && i < _otherBar.Count; i++) crowd += _otherBar[i];
        _crowd[bar] = crowd * _crowdScale;
        _net.Colors[bar] = _net[bar] >= 0 ? BuyColor : SellColor;
    }

    private decimal Money(CumulativeTrade t)
    {
        decimal pointValue = InstrumentInfo is { TickSize: > 0, TickCost: > 0 } i ? i.TickCost / i.TickSize : 1m;
        decimal price = (t.FirstPrice + t.Lastprice) / 2;
        return (t.Direction == TradeDirection.Buy ? 1 : -1) * price * t.Volume * pointValue;
    }

    private int BarOf(DateTime time, int hint)
    {
        int n = CurrentBar;
        if (n == 0) return 0;
        int lo = hint > 0 && hint < n && GetCandle(hint).Time <= time ? hint : 0, hi = n - 1, ans = lo;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (GetCandle(mid).Time <= time) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }

        return ans;
    }

    protected override void OnCumulativeTradesResponse(CumulativeTradesRequest request, IEnumerable<CumulativeTrade> cumulativeTrades)
    {
        var list = cumulativeTrades as IList<CumulativeTrade> ?? cumulativeTrades.ToList();
        _recent.Clear();
        for (int i = 0; i < CurrentBar; i++) { if (i < _byBar.Count) _byBar[i] = 0; }
        _otherBar.Clear();
        while (_otherBar.Count < CurrentBar) _otherBar.Add(0);
        if (list.Count == 0) return;
        var vols = list.Select(t => t.Volume).OrderBy(v => v).ToArray();
        _threshold = vols[Math.Min(vols.Length - 1, (int)(vols.Length * _percentile / 100.0))];
        foreach (var v in list.Skip(Math.Max(0, list.Count - _sample))) _recent.Enqueue(v.Volume);
        int bar = 0;
        foreach (var t in list)
        {
            bar = BarOf(t.Time, bar);
            if (t.Volume >= _threshold && t.Volume > 0)
            {
                while (_byBar.Count <= bar) _byBar.Add(0);
                _byBar[bar] += Money(t);
            }
            else if (bar < _otherBar.Count) _otherBar[bar] += Money(t);
        }

        _crowdScale = ScaleFor();
        for (int i = 0; i < CurrentBar; i++) Paint(i);
        RedrawChart();
    }

    protected override void OnCumulativeTrade(CumulativeTrade trade)
    {
        _recent.Enqueue(trade.Volume);
        while (_recent.Count > _sample) _recent.Dequeue();
        if (_recent.Count % 50 == 0 && _recent.Count > 100)
        {
            var v = _recent.OrderBy(x => x).ToArray();
            _threshold = v[Math.Min(v.Length - 1, (int)(v.Length * _percentile / 100.0))];
        }

        _live = trade;
        _liveAdded = 0;
        _liveOtherAdded = 0;
        Consider(trade);
    }

    /// <summary>Stretch factor that gives the crowd line the same range as the whale total (the two are far apart in size).</summary>
    private decimal ScaleFor()
    {
        decimal w = 0, c = 0, rw = 0, rc = 0;
        for (int i = 0; i < _byBar.Count; i++) { rw += _byBar[i]; w = Math.Max(w, Math.Abs(rw)); }
        for (int i = 0; i < _otherBar.Count; i++) { rc += _otherBar[i]; c = Math.Max(c, Math.Abs(rc)); }
        return c > 0 && w > 0 ? w / c : 1;
    }

    protected override void OnUpdateCumulativeTrade(CumulativeTrade trade)
    {
        if (!ReferenceEquals(trade, _live))
        {
            _live = trade;
            _liveAdded = 0;
            _liveOtherAdded = 0;
        }

        Consider(trade);
    }

    /// <summary>A live order that is still sweeping is counted once: only the change since it was last counted is added.</summary>
    private void Consider(CumulativeTrade trade)
    {
        if (_threshold <= 0 || trade.Volume <= 0) return;
        int bar = BarOf(trade.Time, Math.Max(0, CurrentBar - 5));
        decimal now = Money(trade);
        if (trade.Volume < _threshold)
        {
            while (_otherBar.Count <= bar) _otherBar.Add(0);
            _otherBar[bar] += now - _liveOtherAdded;
            _liveOtherAdded = now;
            if (_showCrowd) { for (int i = bar; i < CurrentBar; i++) Paint(i); RedrawChart(); }
            return;
        }

        if (_liveOtherAdded != 0)
        {
            // the order kept sweeping and became big: it moves from the crowd to the whales
            while (_otherBar.Count <= bar) _otherBar.Add(0);
            _otherBar[bar] -= _liveOtherAdded;
            _liveOtherAdded = 0;
        }

        while (_byBar.Count <= bar) _byBar.Add(0);
        _byBar[bar] += now - _liveAdded;
        _liveAdded = now;
        for (int i = bar; i < CurrentBar; i++) Paint(i);
        RedrawChart();
    }
}
