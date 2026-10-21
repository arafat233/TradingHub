using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Strategies;

namespace TradingHub.App.Windows;

/// <summary>
/// Management of a live strategy (whether the panel or a chart launched it): state, pause, close
/// position, stop, resume, delete, its risk (size and limits), its parameters, its portfolio and its activity. The
/// locked ones have their risk and parameters fixed.
/// </summary>
internal sealed class LiveStrategyWindow : Window
{
    private static readonly CultureInfo Fmt = new("en-US");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private string _key;
    private LiveEntry _entry;
    private readonly TextBlock _title = new() { Classes = { "h2" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _subtitle = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _state = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Border _lockNote = new() { IsVisible = false };
    private readonly WrapPanel _kpis = new() { ItemSpacing = 8, LineSpacing = 8 };
    private readonly WrapPanel _actions = new() { ItemSpacing = 8, LineSpacing = 8 };
    private readonly NumericUpDown _size = Num(0.01m, 0, 100, "0.##");
    private readonly NumericUpDown _dailyLoss = Num(10, 0, 100_000_000, "0.##");
    private readonly NumericUpDown _maxDd = Num(10, 0, 100_000_000, "0.##");
    private readonly CheckBox _closeOnLimit = new() { Content = "On hitting a limit, also close its position" };
    private readonly TextBlock _riskNote = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
    private Button _applyRisk = null!, _applyParams = null!, _applyConditions = null!;
    private readonly ParameterEditor _params = new();
    private readonly MarketConditionPicker _conditions = new();

    /// <summary>The context filter's parameters: they are edited in "Market condition", not in the parameter list.</summary>
    internal static readonly string[] ContextParameters = { nameof(Strategy.RegimeFilter), nameof(Strategy.MinVolatilityRank), nameof(Strategy.MaxVolatilityRank) };
    private readonly ComboBox _portfolio = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _log = new() { Background = Brushes.Transparent, MaxHeight = 260 };
    private readonly Avalonia.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _logCount = -1;
    private string _actionsKey = "";
    private bool _fillingPortfolio;

    public LiveStrategyWindow(AppServices svc, Action<string> status, LiveEntry entry)
    {
        _svc = svc;
        _status = status;
        _entry = entry;
        _key = entry.Key;
        Title = $"Live strategy · {entry.Info.Strategy}";
        Width = 620;
        Height = 820;
        MinWidth = 520;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;

        _applyRisk = Ui.Button("Apply risk", MaterialIconKind.Check, () => _ = ApplyRiskAsync(), "primary");
        _applyParams = Ui.Button("Apply and restart", MaterialIconKind.Restart, () => _ = ApplyParametersAsync(), "primary");
        _applyConditions = Ui.Button("Apply", MaterialIconKind.Check, ApplyConditions, "primary");
        _conditions.Changed += () => _applyConditions.IsEnabled = !_entry.Locked && _conditions.IsValid;
        _portfolio.SelectionChanged += (_, _) =>
        {
            if (_fillingPortfolio || _portfolio.SelectedItem is not ComboBoxItem item) return;
            _svc.Live.Assign(_key, item.Tag as string);
            _status($"{_entry.Info.Strategy}: {(item.Tag == null ? "no portfolio (it goes to the automatic one)" : "assigned to «" + item.Content + "»")}");
        };

        var lockText = new TextBlock
        {
            Text = "Locked: the risk and the parameters cannot be changed from here. You can view it, stop it or resume it.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
        };
        var lockIcon = Ui.Icon(MaterialIconKind.ShieldLockOutline, 18, Ui.Warn);
        lockIcon.Margin = new Thickness(0, 0, 10, 0);
        lockIcon.VerticalAlignment = VerticalAlignment.Top;
        var lockRow = new DockPanel();
        DockPanel.SetDock(lockIcon, Dock.Left);
        lockRow.Children.Add(lockIcon);
        lockRow.Children.Add(lockText);
        _lockNote.Child = Ui.Card(lockRow, 12);

        var riskGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*") };
        void Put(Control c, int col)
        {
            Grid.SetColumn(c, col);
            riskGrid.Children.Add(c);
        }

        Put(Ui.Field("Size (×)", _size, "1 = as the strategy says"), 0);
        Put(Ui.Field("Max daily loss", _dailyLoss, "0 = no limit"), 2);
        Put(Ui.Field("Maximum drawdown", _maxDd, "0 = no limit"), 4);

        var body = new StackPanel { Spacing = 18, Margin = new Thickness(22, 18, 22, 18) };
        body.Children.Add(Ui.V(4, _title, _subtitle, _state));
        body.Children.Add(_lockNote);
        body.Children.Add(_kpis);
        body.Children.Add(Section("Actions", _actions));
        body.Children.Add(Section("Risk", riskGrid, _closeOnLimit, _riskNote, Right(_applyRisk)));
        body.Children.Add(Section("Market condition", _conditions,
            Note("It only opens trades when the market is in whatever you choose (measured with closed candles only: the trend of the last 24 hours and the volatility of the last month). It applies right away, without restarting; exits always go through."),
            Right(_applyConditions)));
        body.Children.Add(Section("Parameters", _params, Right(_applyParams)));
        body.Children.Add(Section("Portfolio", _portfolio, Note("A strategy belongs to only one portfolio of yours; the ones with none go to the automatic one (everything running live).")));
        body.Children.Add(Section("Activity", Ui.Card(_log, 6)));
        Content = new ScrollViewer { Content = body };

        LoadForm();
        Refresh();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private static NumericUpDown Num(decimal increment, decimal min, decimal max, string format) => new()
    {
        Increment = increment,
        Minimum = min,
        Maximum = max,
        FormatString = format,
        NumberFormat = CultureInfo.InvariantCulture.NumberFormat,
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private static Control Right(Control c)
    {
        c.HorizontalAlignment = HorizontalAlignment.Right;
        return c;
    }

    private static Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 9 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Fmt);

    /// <summary>Form with the current values (risk, parameters and portfolio).</summary>
    private void LoadForm()
    {
        var e = _entry;
        var r = e.Runner;
        _size.Value = r?.SizeMultiplier ?? e.Info.Size;
        _dailyLoss.Value = r?.MaxDailyLoss ?? e.Info.MaxDailyLoss;
        _maxDd.Value = r?.MaxDrawdown ?? e.Info.MaxDrawdown;
        _closeOnLimit.IsChecked = r?.CloseOnLimit ?? e.Info.CloseOnLimit;
        bool locked = e.Locked;
        _lockNote.IsVisible = locked;
        foreach (var c in new Control[] { _size, _dailyLoss, _maxDd, _closeOnLimit, _applyRisk, _applyParams }) c.IsEnabled = !locked;

        var catalog = _svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        var file = catalog.Files.FirstOrDefault(f => f.Id == e.Info.StrategyId);
        var type = r?.StrategyInstance.GetType() ?? file?.Type;
        var values = _svc.Live.ParametersOf(e);
        _params.Load(type, values, readOnly: locked, hidden: ContextParameters);
        var probe = type != null ? (Strategy)Activator.CreateInstance(type)! : null;
        probe?.SetParameters(values);
        _conditions.Load(probe?.RegimeFilter ?? RegimeFilter.All, probe?.MinVolatilityRank ?? 0, probe?.MaxVolatilityRank ?? 100);
        _conditions.Editable = !locked;
        _applyConditions.IsEnabled = !locked && _conditions.IsValid;

        _fillingPortfolio = true;
        _portfolio.Items.Clear();
        _portfolio.Items.Add(new ComboBoxItem { Content = "None (it goes to the automatic one)", Tag = null });
        var defs = LiveStore.LoadPortfolios().Where(d => !d.Auto).ToList();
        foreach (var d in defs) _portfolio.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Id });
        string? current = defs.FirstOrDefault(d => d.Members.Contains(_key))?.Id;
        _portfolio.SelectedItem = _portfolio.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (i.Tag as string) == current) ?? _portfolio.Items[0];
        _fillingPortfolio = false;
    }

    private static bool MarketInfoHasMicro(TradingHub.Strategies.LiveRunnerInfo info) =>
        MarketInfo.IsFutures(info.FeedId) && MarketInfo.MicroOf(info.Symbol) != null;

    /// <summary>Which contract the orders go to and why (smart contract).</summary>
    private string ContractNote(TradingHub.Strategies.LiveRunnerInfo info, TradingHub.Strategies.LiveStrategyRunner? r)
    {
        decimal qty = LiveCenter.QuantityOf(info.Parameters);
        decimal size = r?.SizeMultiplier ?? info.Size;
        var (symbol, ratio) = _svc.Live.PreferredContract(info.FeedId, info.Symbol, qty, size);
        string q(decimal v) => v.ToString("0.####", Fmt);
        if (r is { CrossExecution: true })
            return $"\nContract: {q(qty)} {info.Symbol} × {q(size)} = {q(qty * size * r.ExecRatio)} {r.ExecInstrument.Symbol} (the same exposure in micros).";
        if (!symbol.Equals(info.Symbol, StringComparison.OrdinalIgnoreCase))
            return $"\nContract: at this size {symbol} is preferable ({q(qty * size * ratio)} {symbol}); it switches on its own once it is flat.";
        if (r != null && r.SizeBelowMinimum(qty))
            return $"\nContract: {q(qty)} {info.Symbol} × {q(size)} does not reach 1 contract: 1 is sent.";
        return "";
    }

    private void Refresh()
    {
        var fresh = _svc.Live.Entries().FirstOrDefault(x => x.Key == _key);
        if (fresh != null) _entry = fresh;
        var e = _entry;
        var info = e.Info;
        var r = e.Runner;
        _title.Text = info.Strategy;
        _subtitle.Text = $"{info.Symbol} · {info.Period} · {info.Account}{(info.IsReal ? " — REAL" : "")} · launched from {e.SourceLabel}" +
                         (r is { CrossExecution: true } ? $" · executes on {r.ExecInstrument.Symbol}" : "");
        _subtitle.Foreground = info.IsReal ? Ui.Warn : Ui.Text1;
        // how the market is for the strategy (what decides whether its filter lets it enter)
        if (r is { IsRunning: true }) _conditions.SetContext(r.StrategyInstance.Regime, r.StrategyInstance.MarketDirection, r.StrategyInstance.VolatilityRank);
        string why = r?.StopReason ?? info.StopReason ?? "";
        _state.Text = "Status: " + e.StateLabel + (why.Length > 0 && !e.Running ? $" · {why}" : r is { DailyLimitActive: true } ? $" · {why}" : "");
        _state.Foreground = e.Running ? (e.Paused || r!.DailyLimitActive ? Ui.Warn : Ui.Up) : Ui.Text1;

        var stats = LiveStore.StatsOf(info, e.Ledger, 25_000m);
        _kpis.Children.Clear();
        decimal pos = e.Ledger.Position;
        var plan = _svc.Live.PlanOf(e);
        string posSymbol = e.Ledger.OpenSymbol ?? plan.Symbol;
        _kpis.Children.Add(Kpi("Position", pos == 0 ? "Flat" : $"{(pos > 0 ? "Long" : "Short")} {Math.Abs(pos).ToString("0.####", Fmt)} {posSymbol}", pos == 0 ? "" : "@ " + e.Ledger.AveragePrice.ToString("0.#####", Inv), pos > 0 ? Ui.Up : pos < 0 ? Ui.Down : null));
        // how many contracts and of which one each order sends: so there is never any doubt between the mini and the micro
        _kpis.Children.Add(Kpi("Each order", plan.Text, plan.Detail.Length > 0 ? plan.Detail : MarketInfoHasMicro(info) ? "mini contract" : "", plan.Micro ? Ui.Accent : null));
        _kpis.Children.Add(Kpi("Open", Money(e.OpenPnL), r != null ? $"today {Money(r.TodayPnL)}" : "", e.OpenPnL >= 0 ? Ui.Up : Ui.Down));
        _kpis.Children.Add(Kpi("Net", Money(e.Net), $"{stats.Metrics.TotalTrades} trades", e.Net >= 0 ? Ui.Up : Ui.Down));
        _kpis.Children.Add(Kpi("Profit factor", stats.Metrics.TotalTrades > 0 ? (stats.Metrics.ProfitFactor >= 999 ? "∞" : stats.Metrics.ProfitFactor.ToString("0.00", Fmt)) : "—", stats.Metrics.TotalTrades > 0 ? $"{stats.Metrics.WinRate:0} % winners" : ""));
        _kpis.Children.Add(Kpi("Drawdown", (-(r?.CurrentDrawdown ?? stats.CurrentDrawdown)).ToString("#,##0.00", Fmt), $"worst {(-stats.Metrics.MaxDrawdown).ToString("#,##0.00", Fmt)}"));
        _riskNote.Text =
            $"The size multiplies the new orders (exits always close exactly what is open). Daily loss: it stops until the next session. Drawdown: it halts it. Current drawdown {(r?.CurrentDrawdown ?? stats.CurrentDrawdown).ToString("#,##0.00", Fmt)}." +
              ContractNote(info, r);

        RefreshActions();
        var lines = r?.LogLines;
        int count = lines?.Count ?? 0;
        if (count != _logCount)
        {
            _logCount = count;
            _log.Items.Clear();
            if (lines == null || lines.Count == 0)
                _log.Items.Add(new ListBoxItem { Content = new TextBlock { Text = e.Running ? "No activity yet." : "The activity is shown while it runs in this Trading Hub session.", Classes = { "muted", "small" } }, IsEnabled = false });
            else
                foreach (var line in lines.Reverse().Take(200))
                    _log.Items.Add(new ListBoxItem { Content = new TextBlock { Text = line, Classes = { "mono", "small" }, TextWrapping = TextWrapping.Wrap }, Padding = new Thickness(8, 2) });
        }
    }

    private static Control Kpi(string label, string value, string sub, IBrush? color = null) => Ui.Card(Ui.V(2,
        new TextBlock { Text = label, Classes = { "faint", "small" } },
        new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeight.Bold, Foreground = color ?? Ui.Text0 },
        new TextBlock { Text = sub, Classes = { "faint", "small" } }), 10).Also(b => b.MinWidth = 104);

    /// <summary>The buttons based on the state (they are only rebuilt if they changed).</summary>
    private void RefreshActions()
    {
        var e = _entry;
        bool running = e.Running, locked = e.Locked;
        string key = $"{running}|{e.Paused}|{locked}|{e.WaitingResume}|{e.Ledger.Position != 0}|{e.Ledger.Trades.Count > 0}";
        if (key == _actionsKey) return;
        _actionsKey = key;
        _actions.Children.Clear();
        void Add(Button b) => _actions.Children.Add(b);
        if (running && !locked)
        {
            Add(e.Paused
                ? Ui.Button("Resume", MaterialIconKind.PlayCircleOutline, () => SetPaused(false), "primary")
                : Ui.Button("Pause", MaterialIconKind.PauseCircleOutline, () => SetPaused(true)));
            if (e.Ledger.Position != 0) Add(Ui.Button("Close position", MaterialIconKind.CloseCircleOutline, () => _ = ClosePositionAsync(), "danger"));
        }

        if (running)
        {
            Add(Ui.Button("Stop", MaterialIconKind.Stop, () => _ = StopAsync(false)));
            if (!locked && e.Ledger.Position != 0) Add(Ui.Button("Stop and close", MaterialIconKind.StopCircleOutline, () => _ = StopAsync(true), "danger"));
        }
        else Add(Ui.Button(e.WaitingResume ? "Resume on the chart" : "Start again", MaterialIconKind.Play, () => _ = ResumeAsync(), "primary"));

        Add(Ui.Button("View on the chart", MaterialIconKind.ChartBoxOutline, () => _ = ShowOnChartAsync()));
        if (e.Ledger.Trades.Count > 0) Add(Ui.Button("View trades", MaterialIconKind.FormatListBulleted, OpenTrades));
        if (!locked && e.Ledger.Fills.Count > 0 && e.Ledger.Position == 0)
            Add(Ui.Button("Start from scratch", MaterialIconKind.Restart, () => _ = ResetHistoryAsync()));
        if (!locked) Add(Ui.Button("Delete", MaterialIconKind.DeleteOutline, () => _ = DeleteAsync(), "danger"));
    }

    private async Task ResetHistoryAsync()
    {
        if (!await ConfirmDialog.ShowAsync(this, "Start from scratch",
                $"The {_entry.Ledger.Trades.Count} recorded trade(s) of “{_entry.Info.Strategy}” are deleted (result, statistics and curve). The strategy carries on as it is. It cannot be undone.",
                "Clear its history", danger: true))
            return;
        var err = _svc.Live.ResetHistory(_entry);
        _status(err ?? $"{_entry.Info.Strategy}: history cleared, it starts from scratch");
        _actionsKey = "";
        _logCount = -1;
        Refresh();
    }

    private async Task<bool> ConfirmRealAsync(string title, string message, string ok)
    {
        if (!_entry.Info.IsReal) return true;
        return await ConfirmDialog.ShowAsync(this, title, message, ok, danger: true);
    }

    private void SetPaused(bool paused)
    {
        _svc.Live.SetPaused(_entry, paused);
        _status(paused ? $"{_entry.Info.Strategy} paused: it opens no new trades" : $"{_entry.Info.Strategy} resumed");
        _actionsKey = "";
        Refresh();
    }

    private async Task ClosePositionAsync()
    {
        if (!await ConfirmDialog.ShowAsync(this, "Close the position", $"The position of “{_entry.Info.Strategy}” is closed at market ({_entry.Ledger.Position.ToString("0.####", Inv)}). The strategy keeps running and may enter again (pause it if you would rather it did not).", "Close position", danger: true))
            return;
        _svc.Live.ClosePosition(_entry);
        _status($"{_entry.Info.Strategy}: position closed at market");
        _actionsKey = "";
        Refresh();
    }

    private async Task StopAsync(bool close)
    {
        string what =
            close ? "It is halted and its position is closed at market." : "It is halted; if it has an open position, it is left as it is.";
        if (_entry.ChartStrategy != null && !_entry.Locked) what += " On the chart it goes back to visual mode.";
        if (!await ConfirmDialog.ShowAsync(this, "Stop the strategy", what, close ? "Stop and close" : "Stop", danger: close)) return;
        await _svc.Live.StopAsync(_entry, close);
        _status($"{_entry.Info.Strategy} stopped{(close ? " and position closed" : "")}");
        _actionsKey = "";
        Refresh();
    }

    private async Task ResumeAsync()
    {
        if (!await ConfirmRealAsync("Run on a REAL account", $"“{_entry.Info.Strategy}” will trade {_entry.Info.Symbol} on {_entry.Info.Account} with real orders and no individual confirmation.", "Run with real money"))
            return;
        var err = await _svc.Live.ResumeAsync(_entry);
        _status(err ?? $"{_entry.Info.Strategy} running again");
        var fresh = _svc.Live.Entries().FirstOrDefault(x => x.Key == _key);
        if (fresh != null) _entry = fresh;
        _actionsKey = "";
        LoadForm();
        Refresh();
    }

    private async Task ShowOnChartAsync()
    {
        var last = _entry.Ledger.Trades.LastOrDefault();
        var err = await _svc.Live.ShowTradeAsync(_entry, last);
        _status(err ?? (last != null ? $"{_entry.Info.Strategy}'s last trade on the chart" : $"{_entry.Info.Symbol} on the chart"));
    }

    private void OpenTrades() => new LiveTradesWindow(_svc, _status, _key, $"Trades · {_entry.Info.Strategy} · {_entry.Info.Symbol}").Show(this);

    private async Task DeleteAsync()
    {
        bool ok = await ConfirmDialog.ShowAsync(this, "Delete from Live",
            $"“{_entry.Info.Strategy}” ({_entry.Info.Symbol} · {_entry.Info.Account}) is halted{(_entry.ChartStrategy != null ? " and is removed from the chart" : "")} and it stops appearing in Live." +
            "If it has an open position, it is left as it is (close it first if you like).",
            "Delete", danger: true);
        if (!ok) return;
        bool history = _entry.Ledger.Fills.Count > 0 && await ConfirmDialog.ShowAsync(this, "Delete its history too?",
            $"It has {_entry.Ledger.Trades.Count} recorded trade(s). If you delete it, it cannot be recovered; if you keep it and launch the strategy the same way again, it carries on with its history.",
            "Clear the history", danger: true);
        var err = await _svc.Live.DeleteAsync(_entry, history);
        _status(err ?? $"{_entry.Info.Strategy} deleted from Live");
        if (err == null) Close();
    }

    private async Task ApplyRiskAsync()
    {
        decimal size = _size.Value ?? 1, dl = _dailyLoss.Value ?? 0, dd = _maxDd.Value ?? 0;
        if (!await ConfirmRealAsync("Change the risk on a REAL account", $"{_entry.Info.Strategy}: size ×{size.ToString("0.##", Inv)}, max daily loss {dl.ToString("0.##", Inv)}, max drawdown {dd.ToString("0.##", Inv)}.", "Apply"))
            return;
        if (_svc.Live.ApplyRisk(_entry, size, dl, dd, _closeOnLimit.IsChecked == true))
            _status($"Risk for {_entry.Info.Strategy}: size ×{size.ToString("0.##", Inv)}{(dl > 0 ? $" · daily loss {dl.ToString("0.##", Inv)}" : "")}{(dd > 0 ? $" · drawdown {dd.ToString("0.##", Inv)}" : "")}");
        Refresh();
    }

    /// <summary>The chosen market condition: right away, without restarting.</summary>
    private void ApplyConditions()
    {
        if (!_conditions.IsValid) return;
        var (filter, min, max) = _conditions.Value;
        var err = _svc.Live.ApplyContextFilter(_entry, filter, min, max);
        _status(err ?? $"“{_entry.Info.Strategy}” trades {_conditions.Describe()}");
    }

    private async Task ApplyParametersAsync()
    {
        var values = _params.Read(_svc.Live.ParametersOf(_entry));
        string text = _entry.Running
            ? "It restarts with the new parameters (it warms up again with the history). If it has an open position, it keeps managing it with its stop."
            : "The new parameters are saved and it starts with them.";
        if (!await ConfirmDialog.ShowAsync(this, "Apply parameters", text, "Apply and restart")) return;
        if (!await ConfirmRealAsync("Restart on a REAL account", $"“{_entry.Info.Strategy}” will trade {_entry.Info.Symbol} on {_entry.Info.Account} with real orders again.", "Restart with real money")) return;
        var err = await _svc.Live.RestartWithParametersAsync(_entry, values);
        _status(err ?? $"{_entry.Info.Strategy} restarted with the new parameters");
        var fresh = _svc.Live.Entries().FirstOrDefault(x => x.Key == _key);
        if (fresh != null) _entry = fresh;
        _actionsKey = "";
        LoadForm();
        Refresh();
    }
}
