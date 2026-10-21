using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradingHub.Strategies;

namespace TradingHub.App.Controls;

/// <summary>
/// In which market conditions a strategy opens trades: range, mixed, uptrend or downtrend (any
/// combination) and at which volatility (low, normal or high against the last month). It is the strategy's
/// context filter: it is measured with closed candles only, the same live as in a backtest, and exits always go through.
/// </summary>
internal sealed class MarketConditionPicker : UserControl
{
    private readonly CheckBox _range = Box("Range"), _mixed = Box("Mixed"), _up = Box("Up trend"), _down = Box("Down trend");
    private readonly CheckBox _low = Box("Low"), _normal = Box("Normal"), _high = Box("High");
    private readonly TextBlock _summary = new() { FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _now = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private (int Min, int Max)? _custom; // a percentile range that is not one of the three bands (for example, a variant's)
    private bool _loading;
    private (MarketRegime Regime, int? Direction, double? Rank)? _context;

    public MarketConditionPicker()
    {
        foreach (var b in new[] { _range, _mixed, _up, _down }) b.IsCheckedChanged += (_, _) => Changed_();
        foreach (var b in new[] { _low, _normal, _high })
            b.IsCheckedChanged += (_, _) =>
            {
                if (_loading) return;
                _custom = null;
                // volatility runs continuously: low and high include normal
                if (_low.IsChecked == true && _high.IsChecked == true && _normal.IsChecked != true)
                {
                    _loading = true;
                    _normal.IsChecked = true;
                    _loading = false;
                }

                Changed_();
            };
        var regimes = new WrapPanel { ItemSpacing = 14, LineSpacing = 6, Children = { _range, _mixed, _up, _down } };
        var vols = new WrapPanel { ItemSpacing = 14, LineSpacing = 6, Children = { _low, _normal, _high } };
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Label("Trades when the market is in"), regimes,
                Label("With volatility (compared with the last month's)"), vols,
                _summary, _now,
            },
        };
        Load(RegimeFilter.All, 0, 100);
    }

    /// <summary>The selection changed.</summary>
    public event Action? Changed;

    private static CheckBox Box(string text) => new() { Content = text };

    private static TextBlock Label(string text) => new() { Text = text, Classes = { "faint", "small" } };

    public void Load(RegimeFilter filter, int minRank, int maxRank)
    {
        _loading = true;
        var set = MarketContextTracker.SetOf(filter);
        _range.IsChecked = set.HasFlag(RegimeSet.Range);
        _mixed.IsChecked = set.HasFlag(RegimeSet.Mixed);
        _up.IsChecked = set.HasFlag(RegimeSet.UpTrend);
        _down.IsChecked = set.HasFlag(RegimeSet.DownTrend);
        minRank = Math.Clamp(minRank, 0, 100);
        maxRank = Math.Clamp(maxRank, 0, 100);
        _low.IsChecked = minRank <= MarketContextTracker.LowVolatility;
        _normal.IsChecked = minRank <= MarketContextTracker.HighVolatility - 1 && maxRank >= MarketContextTracker.LowVolatility + 1;
        _high.IsChecked = maxRank >= MarketContextTracker.HighVolatility;
        _custom = Bands() == (minRank, maxRank) ? null : (minRank, maxRank);
        _loading = false;
        Changed_(raise: false);
    }

    /// <summary>The percentiles of the ticked bands (low 0-33, normal 34-66, high 67-100).</summary>
    private (int Min, int Max) Bands()
    {
        bool low = _low.IsChecked == true, normal = _normal.IsChecked == true, high = _high.IsChecked == true;
        int min = low ? 0 : normal ? 34 : high ? 67 : 0;
        int max = high ? 100 : normal ? 66 : low ? 33 : 100;
        return (min, max);
    }

    private RegimeSet Set =>
        (_range.IsChecked == true ? RegimeSet.Range : 0) | (_mixed.IsChecked == true ? RegimeSet.Mixed : 0) |
        (_up.IsChecked == true ? RegimeSet.UpTrend : 0) | (_down.IsChecked == true ? RegimeSet.DownTrend : 0);

    /// <summary>At least one condition and one volatility band are ticked.</summary>
    public bool IsValid => Set != RegimeSet.None && (_low.IsChecked == true || _normal.IsChecked == true || _high.IsChecked == true);

    /// <summary>The selection as the strategy's context filter parameters.</summary>
    public (RegimeFilter Filter, int Min, int Max) Value
    {
        get
        {
            var (min, max) = _custom ?? Bands();
            return (MarketContextTracker.FilterOf(Set), min, max);
        }
    }

    /// <summary>No filter: every condition and any volatility.</summary>
    public bool IsAll => Value is (RegimeFilter.All, 0, 100);

    public bool Editable
    {
        set
        {
            foreach (var b in new[] { _range, _mixed, _up, _down, _low, _normal, _high }) b.IsEnabled = value;
        }
    }

    /// <summary>How the market is right now (what the strategy sees): it says whether the selection would open trades.</summary>
    public void SetContext(MarketRegime regime, int? direction, double? rank)
    {
        _context = (regime, direction, rank);
        Changed_(raise: false);
    }

    /// <summary>What is told to the strategy and to you: “range only, low volatility”.</summary>
    public string Describe()
    {
        var (f, min, max) = Value;
        string text = Robustness.DescribeFilter(f, min, max);
        return text.Length == 0 ? "in any market condition" : text;
    }

    private void Changed_(bool raise = true)
    {
        if (_loading) return;
        _summary.Text = IsValid ? $"Trades {Describe()}." : "Pick at least one condition and one volatility.";
        _summary.Foreground = IsValid ? Ui.Text0 : Ui.Warn;
        if (_context is { } c && IsValid)
        {
            var (f, min, max) = Value;
            bool regimeOk = c.Regime == MarketRegime.Unknown || MarketContextTracker.RegimeAllows(f, c.Regime, c.Direction);
            bool volOk = c.Rank is not { } r || (r >= min && r <= max);
            string now = c.Regime == MarketRegime.Unknown ? "no context data yet"
                : MarketContextTracker.Label(c.Regime, c.Direction).ToLowerInvariant() + (c.Rank is { } rk ? $", {MarketContextTracker.VolatilityLabel(rk)} volatility (percentile {rk:0})" : "");
            _now.Text = regimeOk && volOk ? $"Right now there is {now}: with this, it trades." : $"Right now there is {now}: with this, it would not open new trades (it keeps managing any it already has open).";
            _now.Foreground = regimeOk && volOk ? Ui.Up : Ui.Warn;
            _now.IsVisible = true;
        }
        else _now.IsVisible = false;

        if (raise) Changed?.Invoke();
    }
}
