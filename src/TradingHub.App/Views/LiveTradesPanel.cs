using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

/// <summary>
/// Every closed trade of the live strategies with full detail: entry and exit, duration, quantity,
/// points, result, MAE and MFE (the worst and the best moment of each one) and how much of the best moment was captured. Click
/// one and it is shown on the chart.
/// </summary>
internal sealed class LiveTradesPanel : UserControl
{
    private static readonly CultureInfo Fmt = new("en-US");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const string Columns = "44,2*,96,58,118,84,118,84,76,58,74,96,88,88,70";
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly ComboBox _filter = new() { MinWidth = 280 };
    private readonly ComboBox _order = new() { MinWidth = 170 };
    private readonly TextBlock _summary = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new() { Background = Brushes.Transparent };
    private readonly TextBlock _empty = new() { Text = "There are no closed live trades yet.", Classes = { "muted" }, Margin = new Thickness(12), IsVisible = false };
    private List<Row> _rows = new();
    private Dictionary<string, (decimal Mae, decimal Mfe, bool Exact)> _ex = new();
    private string? _only;
    private readonly HashSet<string>? _scope;
    private bool _filling;
    private int _computing;

    private sealed record Row(LiveEntry Entry, ClosedTrade Trade)
    {
        public string Id => LiveCenter.TradeId(Entry.Key, Trade);
    }

    public LiveTradesPanel(AppServices svc, Action<string> status, string? onlyKey = null, IEnumerable<string>? scope = null)
    {
        _svc = svc;
        _status = status;
        _only = onlyKey;
        _scope = scope != null ? new HashSet<string>(scope) : null;
        _order.Items.Add(new ComboBoxItem { Content = "Most recent first", Tag = "recent" });
        _order.Items.Add(new ComboBoxItem { Content = "Oldest first", Tag = "old" });
        _order.Items.Add(new ComboBoxItem { Content = "Best result", Tag = "best" });
        _order.Items.Add(new ComboBoxItem { Content = "Worst result", Tag = "worst" });
        _order.Items.Add(new ComboBoxItem { Content = "Worst MAE", Tag = "mae" });
        _order.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) =>
        {
            if (_filling) return;
            _only = (_filter.SelectedItem as ComboBoxItem)?.Tag as string;
            Refresh();
        };
        _order.SelectionChanged += (_, _) =>
        {
            if (!_filling) Render();
        };

        var copy = Ui.Button("Copy (CSV)", MaterialIconKind.ContentCopy, () => _ = CopyAsync());
        ToolTip.SetTip(copy, "Copies the trades (with MAE and MFE) to paste into a spreadsheet");
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(copy, Dock.Right);
        bar.Children.Add(copy);
        bar.Children.Add(Ui.H(10, Ui.Muted("Strategy"), _filter, Ui.Muted("Order"), _order));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Margin = new Thickness(12, 0, 12, 6) };
        string[] titles = { "#", "Strategy", "Market", "Side", "Entry", "Price", "Exit", "Price", "Duration", "Qty", "Points", "Result", "MAE", "MFE", "Capture" };
        string[] tips =
        {
            "The trade's number within that strategy", "", "", "", "", "Average entry price", "", "Exit price", "", "Actual quantity (with the size)",
            "Points in favour (+) or against (−)", "Result in money", "MAE: the worst it got (in money) before closing", "MFE: the best it got (in money) before closing",
            "On the winners: how much of the best moment (MFE) it took when closing",
        };
        for (int i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], Classes = { "h3" }, TextAlignment = i is 0 or 5 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 8, 0) };
            if (tips[i].Length > 0) ToolTip.SetTip(t, tips[i]);
            Grid.SetColumn(t, i);
            header.Children.Add(t);
        }

        _list.ItemTemplate = new FuncDataTemplate<Row>((r, _) => r == null ? new Grid() : BuildRow(r));
        var table = new DockPanel { MinWidth = 1260 };
        DockPanel.SetDock(header, Dock.Top);
        table.Children.Add(header);
        DockPanel.SetDock(_empty, Dock.Top);
        table.Children.Add(_empty);
        table.Children.Add(_list);
        var scroll = new ScrollViewer { Content = table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        var hint = Ui.Faint("Click a trade to see it on the chart (a box from the entry to the exit, with its worst and best price). The ✕ on its label (or Escape) removes it.");
        hint.Margin = new Thickness(0, 8, 0, 0);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        DockPanel.SetDock(_summary, Dock.Top);
        _summary.Margin = new Thickness(0, 0, 0, 10);
        root.Children.Add(_summary);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(Ui.Card(scroll, 8));
        Content = root;
        Refresh();
    }

    /// <summary>Reads the trades again (and computes MAE / MFE for the ones missing it).</summary>
    public void Refresh()
    {
        var entries = _svc.Live.Entries().Where(e => _scope == null || _scope.Contains(e.Key)).ToList();
        _filling = true;
        var keep = _only;
        _filter.Items.Clear();
        _filter.Items.Add(new ComboBoxItem { Content = _scope == null ? "Every strategy" : "All of the portfolio's", Tag = null });
        foreach (var e in entries.Where(e => e.Ledger.Trades.Count > 0).OrderBy(e => e.Info.Strategy))
            _filter.Items.Add(new ComboBoxItem { Content = $"{e.Info.Strategy} · {e.Info.Symbol} · {e.Info.Account} ({e.Ledger.Trades.Count})", Tag = e.Key });
        _filter.SelectedItem = _filter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == keep) ?? _filter.Items[0];
        _filling = false;
        _rows = entries.Where(e => keep == null || e.Key == keep)
            .SelectMany(e => e.Ledger.Trades.ToList().Select(t => new Row(e, t))).ToList();
        Render();
        _ = ComputeExcursionsAsync();
    }

    private async Task ComputeExcursionsAsync()
    {
        var missing = _rows.Where(r => !_ex.ContainsKey(r.Id)).Select(r => (r.Entry, r.Trade)).ToList();
        if (missing.Count == 0) return;
        int ticket = ++_computing;
        _summary.Text += " · calculating MAE and MFE…";
        var res = await _svc.Live.ExcursionsAsync(missing);
        if (ticket != _computing) return;
        foreach (var (k, v) in res) _ex[k] = v;
        Render();
    }

    private IEnumerable<Row> Ordered()
    {
        string order = (_order.SelectedItem as ComboBoxItem)?.Tag as string ?? "recent";
        return order switch
        {
            "old" => _rows.OrderBy(r => r.Trade.ExitTime),
            "best" => _rows.OrderByDescending(r => r.Trade.NetPnl),
            "worst" => _rows.OrderBy(r => r.Trade.NetPnl),
            "mae" => _rows.OrderBy(r => _ex.TryGetValue(r.Id, out var x) && x.Exact ? x.Mae : 0),
            _ => _rows.OrderByDescending(r => r.Trade.ExitTime),
        };
    }

    private void Render()
    {
        var rows = Ordered().ToList();
        _list.ItemsSource = rows;
        _empty.IsVisible = rows.Count == 0;
        if (rows.Count == 0)
        {
            _summary.Text = "No closed trades yet.";
            return;
        }

        decimal net = rows.Sum(r => r.Trade.NetPnl);
        decimal gp = rows.Where(r => r.Trade.NetPnl > 0).Sum(r => r.Trade.NetPnl), gl = -rows.Where(r => r.Trade.NetPnl <= 0).Sum(r => r.Trade.NetPnl);
        int wins = rows.Count(r => r.Trade.NetPnl > 0);
        var exact = rows.Select(r => _ex.TryGetValue(r.Id, out var x) && x.Exact ? x : ((decimal, decimal, bool)?)null).Where(x => x != null).Select(x => x!.Value).ToList();
        string exText = exact.Count > 0
            ? $" · avg MAE {Money(exact.Average(x => x.Item1))} · avg MFE {Money(exact.Average(x => x.Item2))}"
            : "";
        var durations = rows.Select(r => r.Trade.ExitTime - r.Trade.EntryTime).ToList();
        _summary.Text = $"{rows.Count} trades · net {Money(net)} · {(rows.Count > 0 ? 100.0 * wins / rows.Count : 0):0} % winners · PF {(gl > 0 ? (gp / gl).ToString("0.00", Fmt) : gp > 0 ? "∞" : "—")}" +
                        $" · average duration {Duration(TimeSpan.FromTicks((long)durations.Average(d => d.Ticks)))}{exText}";
    }

    private Grid BuildRow(Row r)
    {
        var t = r.Trade;
        var info = r.Entry.Info;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(Columns), Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand) };
        void Cell(int col, string text, IBrush? fg = null, bool right = false, string? tip = null)
        {
            var tb = new TextBlock { Text = text, FontSize = 12, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (fg != null) tb.Foreground = fg;
            if (tip != null) ToolTip.SetTip(tb, tip);
            Grid.SetColumn(tb, col);
            g.Children.Add(tb);
        }

        decimal sign = t.IsLong ? 1 : -1;
        decimal points = (t.ExitPrice - t.EntryPrice) * sign;
        bool hasEx = _ex.TryGetValue(r.Id, out var ex) && ex.Exact;
        Cell(0, t.Number.ToString(Inv), Ui.Text2, true);
        Cell(1, info.Strategy, null, false, $"{info.Strategy} · {info.Account} · {r.Entry.SourceLabel}");
        Cell(2, info.Symbol, Ui.Text1);
        Cell(3, t.IsLong ? "Long" : "Short", t.IsLong ? Ui.Up : Ui.Down);
        Cell(4, t.EntryTime.ToLocalTime().ToString("dd/MM HH:mm:ss", Fmt));
        Cell(5, t.EntryPrice.ToString("0.#####", Inv), null, true);
        Cell(6, t.ExitTime.ToLocalTime().ToString("dd/MM HH:mm:ss", Fmt));
        Cell(7, t.ExitPrice.ToString("0.#####", Inv), null, true);
        Cell(8, Duration(t.ExitTime - t.EntryTime), Ui.Text2, true);
        Cell(9, t.Quantity.ToString("0.####", Inv), null, true);
        Cell(10, points.ToString("+0.#####;-0.#####;0", Inv), points >= 0 ? Ui.Up : Ui.Down, true);
        Cell(11, Money(t.NetPnl), t.NetPnl >= 0 ? Ui.Up : Ui.Down, true);
        Cell(12, hasEx ? Money(ex.Mae) : "—", Ui.Down, true, hasEx ? null : "No stored data for that moment");
        Cell(13, hasEx ? Money(ex.Mfe) : "—", Ui.Up, true, hasEx ? null : "No stored data for that moment");
        Cell(14, hasEx && ex.Mfe > 0 && t.NetPnl > 0 ? $"{Math.Min(100, t.NetPnl / ex.Mfe * 100):0} %" : "—", Ui.Text2, true);
        g.Tapped += async (_, _) =>
        {
            var err = await _svc.Live.ShowTradeAsync(r.Entry, t);
            _status(err ?? $"Trade #{t.Number} of {info.Strategy} on the chart");
        };
        return g;
    }

    private async Task CopyAsync()
    {
        var sb = new StringBuilder("strategy;market;account;origin;number;side;entry_utc;entry_price;exit_utc;exit_price;duration_min;quantity;points;result;mae;mfe\n");
        foreach (var r in Ordered())
        {
            var t = r.Trade;
            bool hasEx = _ex.TryGetValue(r.Id, out var ex) && ex.Exact;
            decimal points = (t.ExitPrice - t.EntryPrice) * (t.IsLong ? 1 : -1);
            sb.AppendLine(string.Join(";", r.Entry.Info.Strategy, r.Entry.Info.Symbol, r.Entry.Info.Account, r.Entry.SourceLabel, t.Number.ToString(Inv), t.IsLong ? "long" : "short",
                t.EntryTime.ToString("yyyy-MM-dd HH:mm:ss", Inv), t.EntryPrice.ToString(Inv), t.ExitTime.ToString("yyyy-MM-dd HH:mm:ss", Inv), t.ExitPrice.ToString(Inv),
                (t.ExitTime - t.EntryTime).TotalMinutes.ToString("0.##", Inv), t.Quantity.ToString(Inv), points.ToString(Inv), t.NetPnl.ToString("0.##", Inv),
                hasEx ? ex.Mae.ToString("0.##", Inv) : "", hasEx ? ex.Mfe.ToString("0.##", Inv) : ""));
        }

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb)
        {
            await cb.SetTextAsync(sb.ToString());
            _status($"Copied {_rows.Count} trades (paste them into a spreadsheet)");
        }
    }

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Fmt);

    internal static string Duration(TimeSpan d) =>
        d.TotalDays >= 1 ? $"{(int)d.TotalDays} d {d.Hours} h" : d.TotalHours >= 1 ? $"{(int)d.TotalHours} h {d.Minutes:00} m" : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} m {d.Seconds:00} s" : $"{Math.Max(0, (int)d.TotalSeconds)} s";
}

/// <summary>Window with every closed live trade (or those of a single strategy).</summary>
internal sealed class LiveTradesWindow : Window
{
    public LiveTradesWindow(AppServices svc, Action<string> status, string? onlyKey = null, string? title = null, IEnumerable<string>? scope = null)
    {
        Title = title ?? "Closed live trades";
        Width = 1320;
        Height = 760;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;
        Panel = new LiveTradesPanel(svc, status, onlyKey, scope) { Margin = new Thickness(20, 18) };
        Content = Panel;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    public LiveTradesPanel Panel { get; }
}
