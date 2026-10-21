using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Views;

internal sealed partial class StrategiesView
{
    private static readonly CultureInfo Fmt = CultureInfo.GetCultureInfo("en-US");

    // configuration
    private readonly ComboBox _dataSource = new() { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "No stored data" };
    private readonly TextBlock _dataInfo = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _period = new() { Text = "M5", Watermark = "M5, H1, D1, Tick500…" };
    private readonly CalendarDatePicker _from = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CalendarDatePicker _to = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _capital = new() { Value = 10_000, Minimum = 1, Increment = 1000, FormatString = "N0" };
    private readonly NumericUpDown _commPct = new() { Value = 0.04m, Minimum = 0, Increment = 0.01m, FormatString = "0.###" };
    private readonly NumericUpDown _commUnit = new() { Value = 0, Minimum = 0, Increment = 0.5m, FormatString = "0.##" };
    private readonly NumericUpDown _slippage = new() { Value = 1, Minimum = 0, Maximum = 100, Increment = 1, FormatString = "0" };
    private readonly ComboBox _limitMode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _timeZone = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _sessionStart = new() { Value = 0, Minimum = 0, Maximum = 23, Increment = 1, FormatString = "0" };
    private readonly StackPanel _paramsPanel = new() { Spacing = 12 };
    private Button _runButton = null!;
    private Button _stopButton = null!;
    private readonly ProgressBar _runProgress = new() { Minimum = 0, Maximum = 1, Height = 5, IsVisible = false };
    private readonly TextBlock _runStatus = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };

    // results
    private readonly WrapPanel _kpis = new() { ItemSpacing = 10, LineSpacing = 10 };
    private readonly EquityChart _equity = new() { Height = 270 };
    private readonly ListBox _tradesList = new() { Background = Brushes.Transparent };
    private readonly Grid _metricsGrid = new() { ColumnDefinitions = new ColumnDefinitions("*,20,*") };
    private readonly TextBox _logBox = new() { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Menlo, monospace"), FontSize = 11.5, TextWrapping = TextWrapping.NoWrap };
    private readonly EquityChart _mcChart = new() { Height = 260, EmptyText = "Run the simulation to see possible paths" };
    private readonly WrapPanel _mcKpis = new() { ItemSpacing = 10, LineSpacing = 10 };
    private readonly Panel _resultsPanel = new();
    private Control _emptyResults = null!;
    private BacktestResult? _lastResult;
    private CancellationTokenSource? _cts;
    private TickData? _ticks;
    private string? _ticksKey;
    private string _compiledSource = "";

    private const string TradeColumns = "44,62,128,96,128,96,64,96,84,84,56,*";

    private Control BuildBacktestPage()
    {
        _runButton = Ui.Button("Run backtest", MaterialIconKind.Play, () => _ = RunBacktestAsync(), "primary");
        _stopButton = Ui.Button("Stop", MaterialIconKind.Stop, () => _cts?.Cancel());
        _stopButton.IsEnabled = false;
        _limitMode.Items.Add(new ComboBoxItem { Content = "Only if price trades through", Tag = LimitFillMode.TradeThrough });
        _limitMode.Items.Add(new ComboBoxItem { Content = "As soon as it touches (optimistic)", Tag = LimitFillMode.Touch });
        _limitMode.SelectedIndex = 0;
        foreach (var (id, label) in new[] { ("", "UTC"), ("America/Argentina/Buenos_Aires", "Buenos Aires"), ("America/New_York", "New York"), ("America/Chicago", "Chicago (CME)"), ("Europe/London", "Londres") })
            _timeZone.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        _timeZone.SelectedIndex = 0;
        _dataSource.SelectionChanged += (_, _) => OnDataSourceChanged();

        var periods = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        foreach (var p in new[] { "M1", "M5", "M15", "H1", "H4", "D1", "Tick500", "Range20" })
        {
            var b = new Button { Content = p, Padding = new Thickness(8, 2), MinHeight = 24, FontSize = 11.5 };
            b.Click += (_, _) => _period.Text = p;
            periods.Children.Add(b);
        }

        // quick ranges over what is stored for the chosen instrument (with "All", Databento's whole history)
        var ranges = new WrapPanel { ItemSpacing = 4, LineSpacing = 4 };
        foreach (var (label, days) in new[] { ("1 week", 7), ("1 month", 30), ("3 months", 91), ("6 months", 182), ("1 year", 365), ("All", 0) })
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 2), MinHeight = 24, FontSize = 11.5 };
            b.Click += (_, _) => SetRange(days);
            ranges.Children.Add(b);
        }

        var dataCard = Ui.Card(Ui.V(12,
            Ui.H(8, Ui.Icon(MaterialIconKind.DatabaseOutline, 17, Ui.Accent), Ui.H2("Data")),
            Ui.Field("Instrument (stored data)", _dataSource),
            _dataInfo,
            Ui.Row(10, Ui.Field("From", _from), Ui.Field("To", _to)),
            ranges,
            Ui.Field("Candle period", Ui.V(6, _period, periods))));

        var execCard = Ui.Card(Ui.V(12,
            Ui.H(8, Ui.Icon(MaterialIconKind.ScaleBalance, 17, Ui.Accent), Ui.H2("Execution")),
            Ui.Row(10, Ui.Field("Initial capital", _capital), Ui.Field("Slippage (ticks)", _slippage)),
            Ui.Row(10, Ui.Field("Commission % per side", _commPct), Ui.Field("Fixed commission per unit", _commUnit)),
            Ui.Field("Limit orders", _limitMode),
            Ui.Row(10, Ui.Field("Session zone", _timeZone), Ui.Field("Session start (h)", _sessionStart))));

        var paramsCard = Ui.Card(Ui.V(12,
            Ui.H(8, Ui.Icon(MaterialIconKind.TuneVariant, 17, Ui.Accent), Ui.H2("Parameters")),
            _paramsPanel));

        var liveCard = Ui.Card(Ui.V(12,
            Ui.H(8, Ui.Icon(MaterialIconKind.AccessPoint, 17, Ui.Accent), Ui.H2("Take it live")),
            new TextBlock { Text = "With these parameters, these candles and this session. Whatever runs in real time (from here and from the charts) is managed in the Live section.", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap },
            Ui.Button("Run live…", MaterialIconKind.Play, () => _ = OpenLiveLaunchAsync(), "primary"),
            Ui.Button("Put on the active chart", MaterialIconKind.ChartBoxPlusOutline, () => _ = PutOnChartAsync()),
            new TextBlock { Text = "On the chart it works like an indicator: it marks where it enters and exits; from its settings you leave it visual only or executing on an account.", Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap }));

        var runBox = Ui.V(10, Ui.Row(10, _stopButton, _runButton), _runProgress, _runStatus);
        var config = new ScrollViewer
        {
            Content = Ui.V(14, dataCard, paramsCard, execCard, liveCard),
            Padding = new Thickness(0, 0, 10, 0),
        };
        var left = new DockPanel { Width = 360 };
        DockPanel.SetDock(runBox, Dock.Bottom);
        runBox.Margin = new Thickness(0, 14, 10, 0);
        left.Children.Add(runBox);
        left.Children.Add(config);

        // results
        var resultTabs = new Ui.Segmented<string>(new[]
        {
            ("trades", "Trades", (string?)null),
            ("metrics", "Metrics", (string?)null),
            ("market", "By market regime", (string?)"How it did in range, mixed or trend and at low, normal or high volatility; and re-running it only in the conditions you choose"),
            ("mc", "Monte Carlo", (string?)null),
            ("log", "Log", (string?)null),
        });
        var tradesBox = BuildTradesTable();
        var metricsBox = new ScrollViewer { Content = _metricsGrid };
        var mcRun = Ui.Button("Simulate 1,000 random orderings", MaterialIconKind.Dice5Outline, RunMonteCarlo);
        var mcBox = Ui.V(14, Ui.H(12, mcRun, new TextBlock { Text = "It shuffles the order of the trades to see which drawdowns could have happened with the same edge.", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 }), _mcKpis, _mcChart);
        var logBox = _logBox;
        var marketBox = new ScrollViewer { Content = _marketBox };
        var tabPanels = new Panel { Children = { tradesBox, metricsBox, marketBox, mcBox, logBox } };
        resultTabs.SelectionChanged += id =>
        {
            tradesBox.IsVisible = id == "trades";
            metricsBox.IsVisible = id == "metrics";
            marketBox.IsVisible = id == "market";
            mcBox.IsVisible = id == "mc";
            logBox.IsVisible = id == "log";
        };
        _resultTabs = resultTabs;
        resultTabs.Select("trades");
        metricsBox.IsVisible = marketBox.IsVisible = mcBox.IsVisible = logBox.IsVisible = false;

        var bottom = new DockPanel();
        DockPanel.SetDock(resultTabs, Dock.Top);
        resultTabs.Margin = new Thickness(0, 0, 0, 10);
        resultTabs.HorizontalAlignment = HorizontalAlignment.Left;
        bottom.Children.Add(resultTabs);
        bottom.Children.Add(tabPanels);

        var results = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        results.Children.Add(_kpis);
        var eqCard = Ui.Card(_equity, 6);
        eqCard.Margin = new Thickness(0, 14);
        Grid.SetRow(eqCard, 1);
        results.Children.Add(eqCard);
        Grid.SetRow(bottom, 2);
        results.Children.Add(bottom);
        _emptyResults = Ui.V(10,
            Ui.Icon(MaterialIconKind.ChartBellCurveCumulative, 40, Ui.Text2),
            new TextBlock { Text = "Choose data and parameters and click “Run backtest”", Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = "The simulation uses every real stored trade: entries, stops and targets fill as they would in the market.", Classes = { "faint", "small" }, HorizontalAlignment = HorizontalAlignment.Center });
        _emptyResults.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyResults.VerticalAlignment = VerticalAlignment.Center;
        ((StackPanel)_emptyResults).Children[0].HorizontalAlignment = HorizontalAlignment.Center;
        results.IsVisible = false;
        _resultsPanel.Children.Add(results);
        _resultsPanel.Children.Add(_emptyResults);

        var root = new DockPanel { Margin = new Thickness(20, 16) };
        DockPanel.SetDock(left, Dock.Left);
        root.Children.Add(left);
        _resultsPanel.Margin = new Thickness(18, 0, 0, 0);
        root.Children.Add(_resultsPanel);
        RefreshDataSources();
        return root;
    }

    private Control BuildTradesTable()
    {
        Grid Header()
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(TradeColumns), Margin = new Thickness(12, 0, 12, 6) };
            string[] titles = { "#", "Side", "Entry", "Price", "Exit", "Price", "Qty", "Result", "MAE", "MFE", "Candles", "Exit by" };
            for (int i = 0; i < titles.Length; i++)
            {
                var t = new TextBlock { Text = titles[i], Classes = { "h3" }, TextAlignment = i is 3 or 5 or 6 or 7 or 8 or 9 or 10 ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 8, 0) };
                Grid.SetColumn(t, i);
                g.Children.Add(t);
            }

            return g;
        }

        _tradesList.ItemTemplate = new FuncDataTemplate<ClosedTrade>((t, _) =>
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions(TradeColumns) };
            if (t == null) return g;
            void Cell(int col, string text, IBrush? fg = null, bool right = false)
            {
                var tb = new TextBlock { Text = text, FontSize = 12, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                if (fg != null) tb.Foreground = fg;
                Grid.SetColumn(tb, col);
                g.Children.Add(tb);
            }

            Cell(0, t.Number.ToString(), Ui.Text2);
            Cell(1, t.IsLong ? "Long" : "Short", t.IsLong ? Ui.Up : Ui.Down);
            Cell(2, t.EntryTime.ToLocalTime().ToString("dd/MM HH:mm:ss", Fmt));
            Cell(3, t.EntryPrice.ToString("0.#####", CultureInfo.InvariantCulture), right: true);
            Cell(4, t.ExitTime.ToLocalTime().ToString("dd/MM HH:mm:ss", Fmt));
            Cell(5, t.ExitPrice.ToString("0.#####", CultureInfo.InvariantCulture), right: true);
            Cell(6, t.Quantity.ToString("0.####", CultureInfo.InvariantCulture), right: true);
            Cell(7, Money(t.NetPnl), t.NetPnl >= 0 ? Ui.Up : Ui.Down, true);
            Cell(8, Money(t.Mae), Ui.Text2, true);
            Cell(9, Money(t.Mfe), Ui.Text2, true);
            Cell(10, t.Bars.ToString(), Ui.Text2, true);
            Cell(11, t.ExitName, Ui.Text1);
            return g;
        });
        var dock = new DockPanel();
        var header = Header();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(_tradesList);
        return Ui.Card(dock, 8);
    }

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Fmt);

    /// <summary>Places this strategy (with its parameters) on Trading's active chart.</summary>
    private async Task PutOnChartAsync()
    {
        if (_current?.Type == null && !await CompileAsync()) return;
        if (TopLevel.GetTopLevel(this) is not TradingHub.App.Windows.MainWindow main || main.Trading.ActiveTab is not { Model: not null } tab)
        {
            _status("Open a chart in Trading first.");
            return;
        }

        main.Navigate("trading");
        await main.Trading.AddStrategyAsync(tab, _current!, _state.Parameters);
    }

    /// <summary>Opens "New live strategy" with this strategy and its parameters (in the Live section).</summary>
    private async Task OpenLiveLaunchAsync()
    {
        if (_current?.Type == null && !await CompileAsync()) return;
        SaveState();
        if (TopLevel.GetTopLevel(this) is not TradingHub.App.Windows.MainWindow main) return;
        main.Navigate("live");
        if (main.Page("live") is LiveView live) live.OpenLaunch(_current!.Id, _state.Parameters);
    }

    // ===================================================================== available data

    private void RefreshDataSources()
    {
        var keep = (_dataSource.SelectedItem as ComboBoxItem)?.Tag as SymbolData;
        // the futures with Databento history show up three times: combined, from their connection only and from Databento only
        var index = HistorySources.Datasets(AppPaths.Ticks).Where(s => !s.FeedId.StartsWith(TradingHub.Feeds.ReplayFeed.FeedPrefix, StringComparison.Ordinal)).ToList();
        _dataSource.Items.Clear();
        foreach (var s in index)
            _dataSource.Items.Add(new ComboBoxItem { Content = $"{s.Symbol} · {SourceName(s)} · {s.Days.Count} day{(s.Days.Count == 1 ?"" : "s")}", Tag = s });

        var want = _state.Settings;
        _dataSource.SelectedItem = _dataSource.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SymbolData sd && want != null && sd.FeedId == want.FeedId && sd.Symbol == want.Symbol)
                                   ?? _dataSource.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is SymbolData sd && keep != null && sd.FeedId == keep.FeedId && sd.Symbol == keep.Symbol)
                                   ?? _dataSource.Items.OfType<ComboBoxItem>().FirstOrDefault();
        if (index.Count == 0) _dataInfo.Text = "There is no stored data yet. Open charts (they record automatically) or download days in the Data section.";
    }

    /// <summary>Where the data comes from: the connection's name or, with Databento, which of the three histories.</summary>
    private string SourceName(SymbolData s) =>
        s.Combined || s.FeedId == HistorySources.Databento || s.FeedId.EndsWith(HistorySources.OnlySuffix, StringComparison.Ordinal)
            ? HistorySources.Describe(s)
            : _svc.Connections.Find(s.FeedId)?.Name ?? s.FeedId;

    /// <summary>The last <paramref name="days"/> stored days of the chosen instrument (0 = everything).</summary>
    private void SetRange(int days)
    {
        if (_dataSource.SelectedItem is not ComboBoxItem { Tag: SymbolData s }) return;
        var from = days <= 0 ? s.First : s.Last.AddDays(-(days - 1));
        if (from < s.First) from = s.First;
        _from.SelectedDate = from.ToDateTime(TimeOnly.MinValue);
        _to.SelectedDate = s.Last.ToDateTime(TimeOnly.MinValue);
    }

    private void OnDataSourceChanged()
    {
        if (_dataSource.SelectedItem is not ComboBoxItem { Tag: SymbolData s }) return;
        string detail = "";
        if (s.Combined)
        {
            int own = TickFile.AvailableDays(AppPaths.Ticks, s.FeedId, s.Symbol).Count();
            int db = TickFile.AvailableDays(AppPaths.Ticks, HistorySources.Databento, s.Symbol).Count();
            detail = $" · {SourceName(s with { Combined = false })} {own} and Databento {db} (Databento fills the gaps)";
        }

        _dataInfo.Text = $"{s.Days.Count} day(s) stored: {s.First:dd/MM/yyyy} → {s.Last:dd/MM/yyyy} · {s.Bytes / 1024 / 1024.0:0.#} MB{detail}";
        bool crypto = s.FeedId.StartsWith("binance", StringComparison.Ordinal) || s.FeedId.StartsWith("bybit", StringComparison.Ordinal);
        if (_state.Settings == null || _state.Settings.Symbol != s.Symbol)
        {
            var from = s.Days.Count > 7 ? s.Days[^7].Day : s.First;
            _from.SelectedDate = from.ToDateTime(TimeOnly.MinValue);
            _to.SelectedDate = s.Last.ToDateTime(TimeOnly.MinValue);
            _commPct.Value = crypto ? 0.04m : 0;
        }
    }

    private void ApplySettingsToForm(BacktestSettings? s)
    {
        RefreshDataSources();
        if (s == null)
        {
            OnDataSourceChanged();
            return;
        }

        _period.Text = s.Period;
        if (s.FromUtc > DateTime.MinValue) _from.SelectedDate = s.FromUtc.Date;
        if (s.ToUtc > DateTime.MinValue) _to.SelectedDate = s.ToUtc.Date.AddDays(-1);
        _capital.Value = s.InitialCapital;
        _commPct.Value = s.CommissionPercent;
        _commUnit.Value = s.CommissionPerUnit;
        _slippage.Value = s.SlippageTicks;
        _limitMode.SelectedIndex = s.LimitFill == LimitFillMode.Touch ? 1 : 0;
        _timeZone.SelectedItem = _timeZone.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == (s.TimeZoneId ?? "")) ?? _timeZone.Items[0];
        _sessionStart.Value = (decimal)s.SessionStart.TotalHours;
    }

    private BacktestSettings? ReadSettingsFromForm(bool validate)
    {
        var src = (_dataSource.SelectedItem as ComboBoxItem)?.Tag as SymbolData;
        if (validate && src == null)
        {
            _runStatus.Text = "There is no data to test with. Open a chart or download days in Data.";
            return null;
        }

        string period = (_period.Text ?? "M5").Trim();
        if (validate && !ChartPeriod.TryParse(period, out _))
        {
            _runStatus.Text = "Invalid period (examples: M5, H1, D1 or “daily”, Tick500, Volume1000, Range20).";
            return null;
        }

        var from = (_from.SelectedDate ?? DateTime.UtcNow.Date.AddDays(-7)).Date;
        var to = (_to.SelectedDate ?? DateTime.UtcNow.Date).Date.AddDays(1);
        if (validate && to <= from)
        {
            _runStatus.Text = "The end date has to be later than the start date.";
            return null;
        }

        return new BacktestSettings
        {
            FeedId = src?.FeedId ?? "",
            Symbol = src?.Symbol ?? "",
            FromUtc = DateTime.SpecifyKind(from, DateTimeKind.Utc),
            ToUtc = DateTime.SpecifyKind(to, DateTimeKind.Utc),
            Period = period,
            InitialCapital = _capital.Value ?? 10_000,
            CommissionPercent = _commPct.Value ?? 0,
            CommissionPerUnit = _commUnit.Value ?? 0,
            SlippageTicks = (int)(_slippage.Value ?? 0),
            LimitFill = (_limitMode.SelectedItem as ComboBoxItem)?.Tag is LimitFillMode m ? m : LimitFillMode.TradeThrough,
            TimeZoneId = (_timeZone.SelectedItem as ComboBoxItem)?.Tag is string tz && tz != "" ? tz : null,
            SessionStart = TimeSpan.FromHours((double)(_sessionStart.Value ?? 0)),
        };
    }

    private async Task<Instrument> ResolveInstrumentAsync(string feedId, string symbol)
    {
        try
        {
            // Databento and "Rithmic only" are not connections with live data: their spec comes from the CME catalog
            if (feedId != HistorySources.Databento && _svc.Connections.Find(feedId) != null)
            {
                var list = await InstrumentPicker.InstrumentsAsync(_svc, feedId);
                var inst = list.FirstOrDefault(i => i.Symbol == symbol);
                if (inst != null) return inst;
            }
        }
        catch
        {
            // with no connection: it is built from what the file says
        }

        if (MarketInfo.IsFutures(feedId)) return MarketInfo.FuturesInstrument(feedId, symbol);
        decimal tick = HistorySources.StoredTickSize(AppPaths.Ticks, feedId, symbol) ?? 0.01m;
        return new Instrument { Symbol = symbol, FeedId = feedId, TickSize = tick, VolumeStep = 0.001m, Exchange = feedId };
    }

    private async Task<TickData?> LoadTicksAsync(BacktestSettings s, CancellationToken ct)
    {
        string key = $"{s.FeedId}|{s.Symbol}|{s.FromUtc:O}|{s.ToUtc:O}";
        if (_ticks != null && _ticksKey == key) return _ticks;
        var inst = await ResolveInstrumentAsync(s.FeedId, s.Symbol);
        _runStatus.Text = $"Loading {s.Symbol} trades…";
        _runProgress.IsVisible = true;
        var progress = new Progress<double>(p =>
        {
            _runProgress.Value = p;
            _runStatus.Text = $"Loading {s.Symbol} trades… {p * 100:0}%";
        });
        var data = await Task.Run(() => TickData.Load(AppPaths.Ticks, inst, s.FromUtc, s.ToUtc, progress, ct), ct);
        if (data.Count == 0)
        {
            _runStatus.Text = "There are no stored trades in that range. Download those days in the Data section.";
            return null;
        }

        _ticks = data;
        _ticksKey = key;
        return data;
    }

    // ===================================================================== parameters

    private void BuildParameterForm()
    {
        _paramsPanel.Children.Clear();
        if (_current?.Type == null) return;
        var desc = Strategy.Description(_current.Type);
        if (!string.IsNullOrWhiteSpace(desc)) _paramsPanel.Children.Add(new TextBlock { Text = desc, Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
        bool commonHeader = false;
        foreach (var p in Strategy.Parameters(_current.Type))
        {
            if (p.DeclaringType == typeof(Strategy) && !commonHeader)
            {
                commonHeader = true;
                _paramsPanel.Children.Add(Ui.Divider());
                _paramsPanel.Children.Add(Ui.H3("Position management"));
            }

            _paramsPanel.Children.Add(ParameterEditor(p));
        }
    }

    private Control ParameterEditor(PropertyInfo p)
    {
        var attr = p.GetCustomAttribute<ParameterAttribute>();
        string label = Strategy.ParameterName(p);
        _state.Parameters.TryGetValue(p.Name, out var value);
        Control editor;
        var t = p.PropertyType;
        if (t == typeof(bool))
        {
            var ts = new ToggleSwitch { IsChecked = value is true, OnContent = "Yes", OffContent = "No" };
            ts.IsCheckedChanged += (_, _) => SetParam(p.Name, ts.IsChecked == true);
            editor = ts;
        }
        else if (t.IsEnum)
        {
            var cb = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var v in Enum.GetValues(t)) cb.Items.Add(new ComboBoxItem { Content = Controls.PropertyGrid.EnumDisplay(v), Tag = v });
            cb.SelectedItem = cb.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, value));
            cb.SelectionChanged += (_, _) => SetParam(p.Name, (cb.SelectedItem as ComboBoxItem)?.Tag);
            editor = cb;
        }
        else if (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal) || t == typeof(float))
        {
            bool integer = t == typeof(int) || t == typeof(long);
            var nud = new NumericUpDown
            {
                Value = value == null ? 0 : Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                Increment = integer ? 1 : 0.1m,
                FormatString = integer ? "0" : "0.#####",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            nud.ValueChanged += (_, _) => SetParam(p.Name, Convert.ChangeType(nud.Value ?? 0, t, CultureInfo.InvariantCulture));
            editor = nud;
        }
        else
        {
            var tb = new TextBox { Text = value?.ToString() ?? "" };
            tb.LostFocus += (_, _) => SetParam(p.Name, tb.Text);
            editor = tb;
        }

        return Ui.Field(label, editor, attr?.Description);
    }

    private void SetParam(string name, object? value)
    {
        _state.Parameters[name] = value;
        SaveState();
    }

    // ===================================================================== run

    private void SetRunning(bool running, string text = "")
    {
        _runButton.IsEnabled = !running;
        _stopButton.IsEnabled = running;
        _runProgress.IsVisible = running;
        if (running) _runProgress.IsIndeterminate = true;
        _runStatus.Text = text;
        _optButton.IsEnabled = !running;
        _wfoButton.IsEnabled = !running;
    }

    private async Task RunBacktestAsync()
    {
        if (_current?.Type == null && !await CompileAsync()) return;
        var file = _current!;
        var settings = ReadSettingsFromForm(validate: true);
        if (settings == null) return;
        SaveState();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        SetRunning(true, "Preparing…");
        try
        {
            var data = await LoadTicksAsync(settings, ct);
            if (data == null)
            {
                SetRunning(false, _runStatus.Text ?? "");
                return;
            }

            _runProgress.IsIndeterminate = true;
            _runStatus.Text = $"Simulating {data.Count:N0} trades…";
            var strategy = file.Create();
            strategy.SetParameters(_state.Parameters);
            // the market context starts with the previous month, as it is live (otherwise, in the first hours it does not know whether there is
            // a range or a trend and a context filter would block nothing)
            var res = await Task.Run(() => new BacktestEngine(strategy, data, settings) { ContextHistory = MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc) }.Run(ct), ct);
            ShowResult(res);
            var run = new StrategyRun
            {
                StrategyId = file.Id,
                StrategyName = file.DisplayName,
                Kind = RunKind.Backtest,
                Settings = settings,
                Parameters = res.Parameters,
                Metrics = res.Metrics,
                Trades = res.Trades,
                Equity = res.Equity.ToList(),
                Ticks = res.Ticks,
                Bars = res.Bars,
                Seconds = res.Seconds,
            };
            if (res.Ok) StrategyStore.Save(run, _compiledSource.Length > 0 ? _compiledSource : File.ReadAllText(file.Path));
            SetRunning(false, res.Ok
                ? $"{res.Metrics.TotalTrades} trades · {res.Ticks:N0} ticks · {res.Bars:N0} candles in {res.Seconds:0.0} s · stored in the history"
                : "The strategy failed: " + res.Error);
            _status(res.Ok ? $"Backtest of {file.DisplayName}: {Money(res.Metrics.NetProfit)}" : "Error in the backtest: " + res.Error);
        }
        catch (OperationCanceledException)
        {
            SetRunning(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            SetRunning(false, "Error: " + ex.Message);
            CrashLog.Write(ex);
        }
    }

    private void ShowResult(BacktestResult r)
    {
        _lastResult = r;
        _emptyResults.IsVisible = false;
        _resultsPanel.Children[0].IsVisible = true;
        var m = r.Metrics;
        _kpis.Children.Clear();
        _kpis.Children.Add(Kpi("Net result", Money(m.NetProfit), $"{m.ReturnPercent:0.##}% of the capital", m.NetProfit >= 0 ? Ui.Up : Ui.Down));
        _kpis.Children.Add(Kpi("Profit factor", m.ProfitFactor >= 999 ? "∞" : m.ProfitFactor.ToString("0.00", Fmt), $"{Money(m.GrossProfit)} / {Money(-m.GrossLoss)}", m.ProfitFactor >= 1.3m ? Ui.Up : m.ProfitFactor >= 1 ? Ui.Warn : Ui.Down));
        _kpis.Children.Add(Kpi("Winners", $"{m.WinRate:0.#}%", $"{m.Winners} of {m.TotalTrades}", null));
        _kpis.Children.Add(Kpi("Max drawdown", Money(-m.MaxDrawdown), $"{m.MaxDrawdownPercent:0.##}% · recovery {m.RecoveryFactor:0.##}x", Ui.Down));
        _kpis.Children.Add(Kpi("Sharpe", m.SharpeRatio.ToString("0.00", Fmt), $"Sortino {m.SortinoRatio:0.00}", m.SharpeRatio >= 1 ? Ui.Up : null));
        _kpis.Children.Add(Kpi("Expectancy", Money(m.Expectancy), $"avg. {Money(m.AverageWin)} / {Money(m.AverageLoss)}", m.Expectancy >= 0 ? Ui.Up : Ui.Down));
        _equity.SetData(r.Equity, r.Settings.InitialCapital);
        _tradesList.ItemsSource = r.Trades;
        FillMetrics(m, r);
        _logBox.Text = r.Log.Count == 0 ? "The strategy wrote nothing (use Log(\"…\") to leave messages)." : string.Join('\n', r.Log);
        RenderMarket(r);
        _mcKpis.Children.Clear();
        _mcChart.SetData(Array.Empty<EquityPoint>(), r.Settings.InitialCapital);
    }

    private static Control Kpi(string title, string value, string sub, IBrush? color)
    {
        var v = new TextBlock { Text = value, FontSize = 19, FontWeight = FontWeight.SemiBold };
        if (color != null) v.Foreground = color;
        var card = Ui.Card(Ui.V(3, new TextBlock { Text = title.ToUpperInvariant(), Classes = { "h3" } }, v, new TextBlock { Text = sub, Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis }), 14);
        card.Width = 178;
        return card;
    }

    private void FillMetrics(PerformanceMetrics m, BacktestResult r)
    {
        _metricsGrid.Children.Clear();
        _metricsGrid.RowDefinitions.Clear();
        var rows = new (string, string)[]
        {
            ("Net result", Money(m.NetProfit)), ("Gross profit", Money(m.GrossProfit)), ("Gross loss", Money(-m.GrossLoss)), ("Commissions", Money(-m.Commission)),
            ("Profit factor", m.ProfitFactor.ToString("0.00", Fmt)), ("Return", $"{m.ReturnPercent:0.##}%"), ("Trades", m.TotalTrades.ToString()), ("Winners / losers", $"{m.Winners} / {m.Losers}"),
            ("% winners", $"{m.WinRate:0.##}%"), ("Average per trade", Money(m.AverageTrade)), ("Average win", Money(m.AverageWin)), ("Average loss", Money(m.AverageLoss)),
            ("Win/loss ratio", m.PayoffRatio.ToString("0.00", Fmt)), ("Expectancy", Money(m.Expectancy)), ("Largest win", Money(m.LargestWin)), ("Largest loss", Money(m.LargestLoss)),
            ("Max winning streak", m.MaxConsecutiveWins.ToString()), ("Max losing streak", m.MaxConsecutiveLosses.ToString()), ("Maximum drawdown", $"{Money(-m.MaxDrawdown)} ({m.MaxDrawdownPercent:0.##}%)"), ("Recovery factor", m.RecoveryFactor.ToString("0.00", Fmt)),
            ("Sharpe (anual)", m.SharpeRatio.ToString("0.00", Fmt)), ("Sortino (anual)", m.SortinoRatio.ToString("0.00", Fmt)), ("Average candles in a trade", m.AverageBarsInTrade.ToString("0.#", Fmt)), ("Time in the market", $"{m.ExposurePercent:0.#}%"),
            ("Long result", Money(m.LongNetProfit)), ("Short result", Money(m.ShortNetProfit)), ("Ticks simulated", r.Ticks.ToString("N0", Fmt)), ("Candles", r.Bars.ToString("N0", Fmt)),
        };
        int half = (rows.Length + 1) / 2;
        for (int i = 0; i < half; i++) _metricsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int i = 0; i < rows.Length; i++)
        {
            var (label, value) = rows[i];
            var row = new DockPanel { Margin = new Thickness(4, 6) };
            var v = new TextBlock { Text = value, FontWeight = FontWeight.Medium };
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(new TextBlock { Text = label, Classes = { "muted" } });
            var cell = new Border { Child = row, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
            Grid.SetRow(cell, i % half);
            Grid.SetColumn(cell, i < half ? 0 : 2);
            _metricsGrid.Children.Add(cell);
        }
    }

    private void RunMonteCarlo()
    {
        if (_lastResult == null || _lastResult.Trades.Count < 2)
        {
            _status("At least 2 trades are needed for Monte Carlo.");
            return;
        }

        var r = _lastResult;
        var mc = Optimizer.MonteCarlo(r.Trades, r.Settings.InitialCapital, 1000);
        _mcKpis.Children.Clear();
        _mcKpis.Children.Add(Kpi("Typical DD (50%)", Money(-mc.DrawdownP50), "half of the paths", null));
        _mcKpis.Children.Add(Kpi("DD probable (95%)", Money(-mc.DrawdownP95), "1 in every 20 paths is worse", Ui.Warn));
        _mcKpis.Children.Add(Kpi("Extreme DD (99%)", Money(-mc.DrawdownP99), $"worst seen {Money(-mc.WorstDrawdown)}", Ui.Down));
        _mcKpis.Children.Add(Kpi("Risk of ruin", $"{mc.RuinProbabilityPercent:0.#}%", "paths with a DD ≥ 50% of capital", mc.RuinProbabilityPercent > 5 ? Ui.Down : Ui.Up));
        _mcChart.SetData(Array.Empty<EquityPoint>(), r.Settings.InitialCapital, mc.SampleCurves.Select(c => (IReadOnlyList<decimal>)c).ToList());
    }
}
