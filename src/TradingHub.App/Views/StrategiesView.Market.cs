using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

/// <summary>
/// Backtest by market: how it did in a range, mixed or a trend (up or down) and with low, normal
/// or high volatility, and running it again only in the conditions you pick. The new run is a real backtest with the context
/// filter: on each candle the strategy decides with the context measured up to that moment (trades are not pulled out of the
/// previous run), just as it is live.
/// </summary>
internal sealed partial class StrategiesView
{
    private readonly StackPanel _marketBox = new() { Spacing = 14, Margin = new Thickness(0, 0, 10, 10) };
    private readonly MarketConditionPicker _btConditions = new();
    private Ui.Segmented<string>? _resultTabs;
    private BacktestResult? _baseline;

    private static (RegimeFilter Filter, int Min, int Max) FilterOf(BacktestResult r)
    {
        if (r.Parameters == null) return (RegimeFilter.All, 0, 100);
        var probe = new ProbeStrategy();
        probe.SetParameters(r.Parameters);
        return (probe.RegimeFilter, probe.MinVolatilityRank, probe.MaxVolatilityRank);
    }

    /// <summary>Only to read the common parameters of a run.</summary>
    private sealed class ProbeStrategy : Strategy;

    private static bool Filtered((RegimeFilter Filter, int Min, int Max) f) => f is not (RegimeFilter.All, 0, 100);

    private void RenderMarket(BacktestResult r)
    {
        _marketBox.Children.Clear();
        // the picker is always the same one: it is taken out of the previous card before being put in the new one
        if (_btConditions.Parent is Panel previous) previous.Children.Remove(_btConditions);
        var current = FilterOf(r);
        if (!Filtered(current)) _baseline = r;
        else if (_baseline != null && !SameRun(_baseline, r)) _baseline = null;

        _marketBox.Children.Add(Ui.H3("How it did by market regime"));
        _marketBox.Children.Add(new TextBlock
        {
            Text = "Each trade records what the market looked like on entry, measured with closed candles only: the trend of the last 24 hours" +
                   "(range, mixed or uptrend or downtrend) and the volatility compared with the last month's.",
            Classes = { "faint", "small" },
            TextWrapping = TextWrapping.Wrap,
        });
        // with a filter, first the comparison against the run in every condition
        if (Filtered(current) && _baseline != null && !ReferenceEquals(_baseline, r))
            _marketBox.Children.Add(Compare(_baseline, r, current));
        _marketBox.Children.Add(Ui.Card(RegimeGrid(r.Trades), 10));

        // run it again only in the chosen conditions
        _btConditions.Load(current.Filter, current.Min, current.Max);
        var run = Ui.Button("Re-run only in these conditions", MaterialIconKind.Play, () =>
        {
            if (!_btConditions.IsValid)
            {
                _status("Pick at least one market condition and one volatility.");
                return;
            }

            var (filter, min, max) = _btConditions.Value;
            ApplyParameters(new Dictionary<string, object?>
            {
                [nameof(Strategy.RegimeFilter)] = filter, [nameof(Strategy.MinVolatilityRank)] = min, [nameof(Strategy.MaxVolatilityRank)] = max,
            });
            _resultTabs?.Choose("market");
        }, "primary");
        var reset = Ui.Button("Run in every condition", MaterialIconKind.Restore, () =>
        {
            ApplyParameters(new Dictionary<string, object?>
            {
                [nameof(Strategy.RegimeFilter)] = RegimeFilter.All, [nameof(Strategy.MinVolatilityRank)] = 0, [nameof(Strategy.MaxVolatilityRank)] = 100,
            });
            _resultTabs?.Choose("market");
        }, "ghost");
        reset.IsVisible = Filtered(current);
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { run, reset } };
        var note = new TextBlock
        {
            Text = "It is a new backtest with the context filter, not a slice of the previous one: on each candle the strategy looks at the context measured up to that" +
                   "moment and only opens if it is one of the chosen ones (exits always go through), just like live. Careful: if you pick the conditions by looking at" +
                   "these very results, it is on the same data; to know whether it really holds, validate it with walk-forward or on another period.",
            Classes = { "faint", "small" },
            TextWrapping = TextWrapping.Wrap,
        };
        var box = Ui.V(10, Ui.H3("Test only in certain conditions"), _btConditions, actions, note);
        _marketBox.Children.Add(Ui.Card(box, 14));
    }

    /// <summary>The same strategy, market, period, dates and parameters: only the context filter changes.</summary>
    private static bool SameRun(BacktestResult a, BacktestResult b)
    {
        if (a.Strategy != b.Strategy || a.Settings.FeedId != b.Settings.FeedId || a.Settings.Symbol != b.Settings.Symbol ||
            a.Settings.Period != b.Settings.Period || a.Settings.FromUtc != b.Settings.FromUtc || a.Settings.ToUtc != b.Settings.ToUtc) return false;
        var context = new[] { nameof(Strategy.RegimeFilter), nameof(Strategy.MinVolatilityRank), nameof(Strategy.MaxVolatilityRank) };
        foreach (var key in a.Parameters.Keys.Union(b.Parameters.Keys).Except(context))
            if (!Equals(a.Parameters.GetValueOrDefault(key)?.ToString(), b.Parameters.GetValueOrDefault(key)?.ToString())) return false;
        return true;
    }

    /// <summary>Every condition against only the chosen ones, one line each.</summary>
    private Control Compare(BacktestResult all, BacktestResult only, (RegimeFilter Filter, int Min, int Max) f)
    {
        string Line(BacktestResult x) => $"{Money(x.Metrics.NetProfit)} in {x.Metrics.TotalTrades} trades · PF {(x.Metrics.ProfitFactor >= 999 ? "∞" : x.Metrics.ProfitFactor.ToString("0.00", Fmt))} · " +
                                         $"drawdown {Money(-x.Metrics.MaxDrawdown)}";
        string what = Robustness.DescribeFilter(f.Filter, f.Min, f.Max);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, RowSpacing = 4, RowDefinitions = new RowDefinitions("Auto,Auto") };
        void Row(int row, string label, string text, IBrush color)
        {
            var l = new TextBlock { Text = label, Classes = { "muted" } };
            var t = new TextBlock { Text = text, Foreground = color, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, row);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, 1);
            grid.Children.Add(l);
            grid.Children.Add(t);
        }

        Row(0, "In every condition", Line(all), all.Metrics.NetProfit >= 0 ? Ui.Up : Ui.Down);
        Row(1, $"With the filter ({what})", Line(only), only.Metrics.NetProfit >= 0 ? Ui.Up : Ui.Down);
        return new Border { Child = grid, Background = Ui.Bg2, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8) };
    }

    /// <summary>The table: trades, net, PF, winners and average in each condition and volatility band.</summary>
    private Control RegimeGrid(IReadOnlyList<ClosedTrade> trades)
    {
        var known = trades.Where(t => t.EntryTrend != null).ToList();
        if (known.Count == 0)
            return new TextBlock { Text = trades.Count == 0 ? "There were no trades." : "This run did not store each trade's context (it is from before): run the backtest again.", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var rows = new List<(string Name, List<ClosedTrade> Trades, bool Head)>();
        bool Trend(ClosedTrade t) => t.EntryTrend >= MarketContextTracker.TrendLimit;
        rows.Add(("Range", known.Where(t => t.EntryTrend <= MarketContextTracker.RangeLimit).ToList(), false));
        rows.Add(("Mixed", known.Where(t => t.EntryTrend > MarketContextTracker.RangeLimit && !Trend(t)).ToList(), false));
        rows.Add(("Up trend", known.Where(t => Trend(t) && t.EntryMarketDirection > 0).ToList(), false));
        rows.Add(("Down trend", known.Where(t => Trend(t) && t.EntryMarketDirection < 0).ToList(), false));
        var undirected = known.Where(t => Trend(t) && t.EntryMarketDirection is null or 0).ToList();
        if (undirected.Count > 0) rows.Add(("Trend (no direction stored)", undirected, false));
        if (known.Count(t => t.EntryVolatilityRank != null) >= known.Count * 0.8)
        {
            rows.Add(("Low volatility", known.Where(t => t.EntryVolatilityRank <= MarketContextTracker.LowVolatility).ToList(), true));
            rows.Add(("Normal volatility", known.Where(t => t.EntryVolatilityRank > MarketContextTracker.LowVolatility && t.EntryVolatilityRank < MarketContextTracker.HighVolatility).ToList(), false));
            rows.Add(("High volatility", known.Where(t => t.EntryVolatilityRank >= MarketContextTracker.HighVolatility).ToList(), false));
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,0.8*,1.2*,0.8*,1*,1.1*"), RowSpacing = 2 };
        int r = 0;
        void Cell(string text, int col, IBrush? color = null, bool head = false, bool faint = false)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = head ? 11 : 12.5,
                Foreground = faint ? Ui.Text2 : color ?? (head ? Ui.Text2 : Ui.Text0),
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
                Margin = new Thickness(6, 5),
            };
            if (head) tb.Classes.Add("h3");
            Grid.SetRow(tb, r);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] titles = { "Condition on entry", "Ops", "Net", "PF", "Winners", "Average" };
        for (int i = 0; i < titles.Length; i++) Cell(titles[i], i, head: true);
        foreach (var (name, list, head) in rows)
        {
            if (head)
            {
                r++;
                grid.RowDefinitions.Add(new RowDefinition(new GridLength(8)));
            }

            r++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            bool few = list.Count < Math.Max(5, known.Count / 20);
            decimal net = list.Sum(t => t.NetPnl);
            decimal win = list.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), loss = -list.Where(t => t.NetPnl < 0).Sum(t => t.NetPnl);
            string pf = list.Count == 0 ? "—" : loss > 0 ? (win / loss).ToString("0.00", Fmt) : win > 0 ? "∞" : "—";
            Cell(few && list.Count > 0 ? $"{name} (little sample)" : name, 0, faint: list.Count == 0 || few);
            Cell(list.Count.ToString(CultureInfo.InvariantCulture), 1, faint: list.Count == 0 || few);
            Cell(list.Count == 0 ? "—" : Money(net), 2, list.Count == 0 || few ? null : net >= 0 ? Ui.Up : Ui.Down, faint: list.Count == 0 || few);
            Cell(pf, 3, faint: list.Count == 0 || few);
            Cell(list.Count == 0 ? "—" : $"{100.0 * list.Count(t => t.NetPnl > 0) / list.Count:0}%", 4, faint: list.Count == 0 || few);
            Cell(list.Count == 0 ? "—" : Money(net / list.Count), 5, faint: list.Count == 0 || few);
        }

        int unknown = trades.Count - known.Count;
        var v = Ui.V(6, grid);
        if (unknown > 0)
            v.Children.Add(new TextBlock { Text = $"{unknown} trade(s) with no context yet (at the start, before there was history to measure it).", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap });
        return v;
    }
}
