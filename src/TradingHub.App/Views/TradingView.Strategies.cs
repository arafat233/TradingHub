using System.Globalization;
using Avalonia.Controls;
using SkiaSharp;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Data;
using TradingHub.Strategies;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.App.Views;

/// <summary>
/// A strategy placed on a chart. It always marks what it would have done with the chart's history (backtest) and
/// then carries on live: in visual mode with its own simulated account (it does not touch yours) and in execute mode with the
/// chosen account, sending real orders.
/// </summary>
public sealed class ChartStrategy
{
    public required string StrategyId { get; init; }
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public bool Live { get; set; }
    public string AccountId { get; set; } = "sim";

    /// <summary>It was executing and Trading Hub was reopened: it waits for you to resume it (it does not trade on its own at startup).</summary>
    public bool Paused { get; set; }

    public StrategyOverlay Overlay { get; } = new();
    internal LiveStrategyRunner? Runner;
    internal SimBroker? Shadow;
    internal List<ClosedTrade> History = new();
    internal OpenPosition? HistoryOpen;
    internal PerformanceMetrics? HistoryMetrics;

    /// <summary>The equity curve of the history (with commissions and slippage) and the capital it started with.</summary>
    internal List<EquityPoint> HistoryEquity = new();
    internal decimal HistoryCapital;

    /// <summary>The costs the history was computed with (to show them in its settings).</summary>
    internal MarketCosts? HistoryCosts;
    internal List<StrategySignal> BaseSignals = new();
    internal List<TradeLink> BaseLinks = new();
    internal DateTime HistoryFrom = DateTime.MaxValue;
    internal CancellationTokenSource? Cts;
    internal string SignalsKey = "";
    internal bool Busy;

    /// <summary>It was executing and waits for you to resume it: its stored bookkeeping (to draw the open position all the same).</summary>
    internal LiveLedger? SavedLedger;

    /// <summary>What the strategy drew over the history (ranges, levels, lines…).</summary>
    internal StrategyCanvas? HistoryCanvas;
    internal string DrawingsKey = "";
}

public sealed partial class TradingView
{
    private static readonly SKColor[] StrategyColors =
    {
        new(180, 120, 255), new(255, 196, 0), new(0, 200, 255), new(255, 120, 200), new(120, 230, 160), new(255, 150, 80),
    };

    internal StrategyCatalog StrategyCatalog => _svc.Strategies;

    /// <summary>The strategy (file) by its id; it compiles it if needed.</summary>
    private async Task<StrategyFile?> StrategyFileAsync(string id)
    {
        var catalog = _svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        var file = catalog.Files.FirstOrDefault(f => f.Id == id) ?? catalog.Files.FirstOrDefault(f => f.FileName == id);
        if (file == null) return null;
        if (file.Type == null) await Task.Run(() => catalog.Compile(file));
        return file;
    }

    /// <summary>Places a strategy on the chart (it starts in visual mode) and opens its settings.</summary>
    internal async Task AddStrategyAsync(ChartTab chart, StrategyFile file, IReadOnlyDictionary<string, object?>? parameters = null, bool openSettings = true)
    {
        if (chart.Model == null) return;
        if (file.Type == null) await Task.Run(() => _svc.Strategies.Compile(file));
        if (file.Type == null)
        {
            _status($"“{file.FileName}” has errors: fix it in Strategies before putting it on the chart");
            return;
        }

        var cs = new ChartStrategy { StrategyId = file.Id };
        cs.Parameters = parameters != null ? new Dictionary<string, object?>(parameters) : StrategyDefaults(file);
        cs.Overlay.Color = StrategyColors[chart.Strategies.Count % StrategyColors.Length];
        cs.Overlay.Name = file.DisplayName;
        cs.Overlay.Tag = cs;
        chart.Strategies.Add(cs);
        chart.Model.Strategies.Add(cs.Overlay);
        Workspace.Save(this);
        await RunStrategyAsync(chart, cs);
        if (openSettings) OpenStrategySettings(chart, cs);
    }

    /// <summary>Parameters it was configured with in Strategies (or the class's).</summary>
    private static Dictionary<string, object?> StrategyDefaults(StrategyFile file)
    {
        var values = file.Create().GetParameters();
        try
        {
            var path = Path.Combine(AppPaths.Backtests, string.Concat(file.Id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)), "ui.json");
            if (File.Exists(path) && System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)).RootElement.TryGetProperty("Parameters", out var saved))
            {
                foreach (var p in saved.EnumerateObject())
                    if (values.ContainsKey(p.Name)) values[p.Name] = p.Value.Clone();
            }
        }
        catch (Exception)
        {
            // with no stored state: the class's
        }

        return values;
    }

    /// <summary>Stops whatever is running of the strategy (it does not touch the open position).</summary>
    private void StopStrategy(ChartStrategy cs)
    {
        cs.Cts?.Cancel();
        if (cs.Runner != null)
        {
            cs.Runner.Stop(flatten: false);
            _svc.Runners.Remove(cs.Runner);
            cs.Runner = null;
            _svc.SaveLiveState();
        }

        cs.Shadow?.Dispose();
        cs.Shadow = null;
    }

    /// <summary>
    /// Runs the strategy: first over the chart's history (historical marks) and then live. With
    /// <paramref name="historyOnly"/> it only redoes the historical marks (for example if more history was loaded).
    /// </summary>
    internal async Task RunStrategyAsync(ChartTab chart, ChartStrategy cs, bool historyOnly = false)
    {
        if (chart.Model is not { } model) return;
        if (!historyOnly) StopStrategy(cs);
        cs.Cts?.Cancel();
        var cts = cs.Cts = new CancellationTokenSource();
        cs.Busy = true;
        cs.Overlay.Error = null;
        try
        {
            var file = await StrategyFileAsync(cs.StrategyId);
            if (file?.Type == null)
            {
                cs.Overlay.Error = file == null ? "the strategy was not found" : "it has errors";
                cs.Overlay.Status = cs.Overlay.Error;
                return;
            }

            cs.Overlay.Name = file.DisplayName;
            var data = model.Data;
            cs.Overlay.Status = "waiting for the history…";
            model.Invalidate();
            while (data.IsLoadingHistory && !cts.IsCancellationRequested) await Task.Delay(300);
            if (cts.IsCancellationRequested) return;

            // 1) the historical part: a backtest over whatever the chart has loaded
            cs.Overlay.Status = "calculating…";
            model.Invalidate();
            var ticks = TickData.FromStore(data.Trades);
            // with that market's commission and slippage (the same as the backtest's): the result it really would have given
            var costs = MarketInfo.Costs(data.Instrument.FeedId, data.Instrument.Symbol);
            var settings = new BacktestSettings
            {
                FeedId = data.Instrument.FeedId,
                Symbol = data.Instrument.Symbol,
                FromUtc = ticks.FirstTime,
                ToUtc = ticks.LastTime,
                Period = model.Period.Code,
                CommissionPercent = costs.CommissionPercent,
                CommissionPerUnit = costs.CommissionPerUnit,
                SlippageTicks = costs.SlippageTicks,
                TimeZoneId = _svc.Settings.TimeZoneId,
                CloseAtEnd = false,
            };
            var historyStrategy = file.Create();
            historyStrategy.SetParameters(cs.Parameters);
            var result = ticks.Count > 0
                ? await Task.Run(() => new BacktestEngine(historyStrategy, ticks, settings) { ContextHistory = MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc) }.Run(cts.Token), cts.Token)
                : new BacktestResult();
            if (cts.IsCancellationRequested) return;
            if (result.Error != null) cs.Overlay.Error = result.Error;
            cs.History = result.Trades;
            cs.HistoryOpen = result.OpenPosition;
            cs.HistoryMetrics = result.Metrics;
            cs.HistoryEquity = result.Equity;
            cs.HistoryCapital = settings.InitialCapital;
            cs.HistoryCosts = costs;
            cs.HistoryFrom = data.HistoryFrom;
            cs.HistoryCanvas = historyStrategy.Canvas;
            cs.DrawingsKey = "";
            cs.SignalsKey = "";
            if (historyOnly) return;

            // 2) live
            if (cs.Live && cs.Paused)
            {
                cs.Overlay.Status = "live · paused (open the settings to resume)";
                cs.Overlay.Live = false;
                // the position it has open on the account is drawn all the same while it waits
                cs.SavedLedger = _svc.Live.Entries().FirstOrDefault(e => ReferenceEquals(e.ChartStrategy, cs))?.Ledger;
                cs.SignalsKey = "";
                return;
            }

            cs.SavedLedger = null;

            if (!ChartPeriod.TryParse(model.Period.Code, out var period)) period = model.Period;
            var strategy = file.Create();
            strategy.SetParameters(cs.Parameters);
            ITradingAccount account;
            if (cs.Live)
            {
                account = _svc.AccountFor(data, cs.AccountId);
                if (account.IsReal && TopLevel.GetTopLevel(chart.Window ?? (Control)this) is Window w &&
                    !await ConfirmDialog.ShowAsync(w, "Run on a REAL account",
                        $"“{file.DisplayName}” will trade {data.Instrument.Symbol} on {account.Name} with real orders and no individual confirmation. You stop it from the chart legend.",
                        "Run with real money", danger: true))
                {
                    cs.Live = false;
                    account = cs.Shadow = NewShadow(data);
                }
            }
            else
            {
                account = cs.Shadow = NewShadow(data);
            }

            var runner = new LiveStrategyRunner(strategy, data, account, period, _svc.Settings.TimeZoneId)
            {
                Source = "chart",
                AccountId = cs.Live ? cs.AccountId : "",
                ProfileId = chart.FeedId,
                Persist = cs.Live, // the visual ones do not pollute the portfolio
                ProtectOnStart = cs.Live,
            };
            if (cs.Live) LiveCenter.Restore(runner);
            if (cs.Live && _svc.Settings.SmartContracts)
            {
                // smart contract, same as in the panel: with the stored size, if the size asks for fractions of the mini
                // it executes the same exposure on the micro (x0.5 of 1 NQ = 5 MNQ); with an open position it stays on its contract
                var (execInst, ratio) = await _svc.Live.ExecutionContractForAsync(chart.FeedId, data.Instrument, cs.Parameters, runner.SizeMultiplier, strategy, account.Name);
                if (!execInst.Symbol.Equals(data.Instrument.Symbol, StringComparison.OrdinalIgnoreCase))
                {
                    var execStrategy = file.Create();
                    execStrategy.SetParameters(cs.Parameters);
                    account = _svc.AccountFor(_svc.Open(chart.FeedId, execInst), cs.AccountId);
                    runner = new LiveStrategyRunner(execStrategy, data, account, period, _svc.Settings.TimeZoneId)
                    {
                        Source = "chart",
                        AccountId = cs.AccountId,
                        ProfileId = chart.FeedId,
                        Persist = true,
                        ProtectOnStart = true,
                        ExecInstrument = execInst,
                        ExecRatio = ratio,
                    };
                    LiveCenter.Restore(runner);
                }
            }
            else if (cs.HistoryOpen is { } open && account is SimBroker shadow)
            {
                // in visual mode it carries on from where the history left off: if the backtest ended with an open position, its own
                // simulated account opens it and the strategy keeps managing it (the exit is tied to that entry)
                shadow.Market(open.IsLong ? OrderDirections.Buy : OrderDirections.Sell, open.Quantity, "Position from the history");
                if (shadow.Position?.Volume is { } v && v != 0)
                    runner.SeedPosition(new LiveFill(open.EntryTime, open.IsLong, open.Quantity, open.AveragePrice));
            }

            runner.Start();
            cs.Runner = runner;
            if (cs.Live)
            {
                _svc.Runners.RemoveAll(r => !r.IsRunning && !r.IsClosing);
                _svc.Runners.Add(runner);
                _svc.Live.Watch(runner);
                _svc.SaveLiveState();
            }

            cs.Overlay.Live = cs.Live;
            cs.Overlay.Status = cs.Live ? LiveStatus(cs, runner) : "visual";
            _status(cs.Live ? $"{file.DisplayName} executing on {account.Name} over {data.Instrument.Symbol}" : $"{file.DisplayName} on the chart (visual only)");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            cs.Overlay.Error = ex.Message;
            cs.Overlay.Status = "error";
            CrashLog.Write(ex);
        }
        finally
        {
            cs.Busy = false;
            Workspace.Save(this);
            model.Invalidate();
        }
    }

    /// <summary>The visual strategy's own simulated account: it does not mix with yours or with your history.</summary>
    private static SimBroker NewShadow(InstrumentData data) =>
        new(data, new Portfolio { AccountID = "VISUAL", Name = "Visual", BeginBalance = 10_000m, Balance = 10_000m }, new TradingStatistics()) { Name = "Visual" };

    internal void RemoveStrategy(ChartTab chart, ChartStrategy cs)
    {
        bool hadPosition = cs.Live && cs.Runner?.Account.Position.Volume is { } v && v != 0;
        StopStrategy(cs);
        chart.Strategies.Remove(cs);
        chart.Model?.Strategies.Remove(cs.Overlay);
        chart.Model?.Invalidate();
        Workspace.Save(this);
        _status(hadPosition ? $"{cs.Overlay.Name} stopped and removed: the open position is left as it is (close it from the Chart Trader if you like)" : $"{cs.Overlay.Name} removed from the chart");
    }

    /// <summary>The period or the instrument changed: the visual ones are recalculated; the executing ones are paused if the instrument changed.</summary>
    internal void RestartStrategies(ChartTab chart, bool instrumentChanged)
    {
        if (chart.Model == null) return;
        chart.Model.Strategies.Clear();
        foreach (var cs in chart.Strategies)
        {
            chart.Model.Strategies.Add(cs.Overlay);
            if (instrumentChanged && cs.Live) cs.Paused = true;
            _ = RunStrategyAsync(chart, cs);
        }
    }

    /// <summary>
    /// Marks of each strategy: the historical trades and the new ones (from its account), with the open position if there is one.
    /// It is called a few times per second; it is only redone if something changed.
    /// </summary>
    private void UpdateStrategySignals(ChartTab chart)
    {
        if (chart.Model is not { } model || chart.Strategies.Count == 0) return;
        foreach (var cs in chart.Strategies)
        {
            // more history was loaded (the chart starts with little and afterwards fetches the chosen length): the visual ones are redone
            // entirely, so their simulated account follows the position the full history leaves open (before, only the old marks were
            // redone and the open trade did not show up until you hit "Apply"); the executing ones, only the marks
            if (!cs.Busy && model.Data.HistoryFrom < cs.HistoryFrom && !model.Data.IsLoadingHistory && !model.Data.IsExtending)
                _ = RunStrategyAsync(chart, cs, historyOnly: cs.Live);
            if (cs.Live && cs.Runner is { IsRunning: true } running && LiveStatus(cs, running) is var status && status != cs.Overlay.Status)
            {
                cs.Overlay.Status = status;
                model.Invalidate();
            }

            UpdateStrategyDrawings(model, cs);
            var ledger = cs.Runner?.Ledger ?? cs.SavedLedger;
            // the visual one has not taken the history's position yet (for example, with no live price): the history's one is drawn
            var historyOpen = !cs.Live && cs.HistoryOpen != null && (ledger == null || ledger.Fills.Count == 0) ? cs.HistoryOpen : null;
            string key = $"{cs.History.Count}|{ledger?.Fills.Count ?? 0}|{ledger?.Position ?? 0}|{historyOpen?.EntryTime.Ticks ?? 0}";
            if (key == cs.SignalsKey)
            {
                // with an open position, the line from the entry to the current price moves with the market
                if (ledger is { Position: not 0 }) SetOpenLink(model, cs, ledger);
                else if (historyOpen != null) SetOpenLink(model, cs, historyOpen);
                continue;
            }

            cs.SignalsKey = key;
            var inst = model.Instrument;
            var signals = new List<StrategySignal>();
            var links = new List<TradeLink>();
            string Money(decimal v) => v.ToString("+#,##0.##;-#,##0.##;0", CultureInfo.InvariantCulture);
            void AddTrade(ClosedTrade t)
            {
                signals.Add(new StrategySignal(t.EntryTime, t.EntryPrice, t.IsLong, true, t.IsLong ? "L" : "S"));
                signals.Add(new StrategySignal(t.ExitTime, t.ExitPrice, !t.IsLong, false, Money(t.NetPnl)));
                links.Add(new TradeLink(t.EntryTime, t.EntryPrice, t.ExitTime, t.ExitPrice, t.IsLong, t.NetPnl));
            }

            foreach (var t in cs.History) AddTrade(t);
            if (ledger != null)
            {
                foreach (var t in ledger.Trades.ToList()) AddTrade(t);
                // open position: the entry from the last time it was flat
                if (ledger.Position != 0)
                {
                    decimal pos = 0;
                    DateTime? opened = null;
                    foreach (var f in ledger.Fills.ToList())
                    {
                        decimal before = pos;
                        pos += f.Buy ? f.Quantity : -f.Quantity;
                        if (before == 0 || Math.Sign(before) != Math.Sign(pos)) opened = f.Time;
                    }

                    if (opened is { } at) signals.Add(new StrategySignal(at, inst.RoundPrice(ledger.AveragePrice), ledger.Position > 0, true, ledger.Position > 0 ? "L" : "S"));
                }
            }

            if (historyOpen != null)
                signals.Add(new StrategySignal(historyOpen.EntryTime, inst.RoundPrice(historyOpen.AveragePrice), historyOpen.IsLong, true, historyOpen.IsLong ? "L" : "S"));

            cs.BaseSignals = signals;
            cs.BaseLinks = links;
            cs.Overlay.Signals = signals;
            cs.Overlay.Links = links;
            if (ledger is { Position: not 0 }) SetOpenLink(model, cs, ledger);
            else if (historyOpen != null) SetOpenLink(model, cs, historyOpen);
            model.Invalidate();
        }
    }

    /// <summary>
    /// What the strategy draws (its ranges, levels, arrows, texts and lines): the history's part up to where the live
    /// one started and, from there on, whatever it draws live (the same id replaces the history's). It is only
    /// redone if something changed.
    /// </summary>
    private static void UpdateStrategyDrawings(ChartModel model, ChartStrategy cs)
    {
        var history = cs.HistoryCanvas;
        var live = cs.Runner?.StrategyInstance.Canvas;
        string key = $"{history?.Version ?? -1}|{(live != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(live) : 0)}:{live?.Version ?? -1}|{cs.Overlay.Color}";
        if (key == cs.DrawingsKey) return;
        cs.DrawingsKey = key;
        // from when the strategy really runs (before that it warmed up with the history without trading: there the backtest drawings count)
        DateTime liveStart = live != null && cs.Runner is { } running ? running.LiveFrom : DateTime.MaxValue;
        var color = cs.Overlay.Color;

        var byId = new Dictionary<string, StrategyDrawing>();
        var order = new List<string>();
        void AddAll(IEnumerable<StrategyDrawing> list, bool fromHistory)
        {
            foreach (var d in list)
            {
                if (fromHistory && d.Time1 >= liveStart && d.Time1 != DateTime.MinValue) continue;
                if (!byId.ContainsKey(d.Id)) order.Add(d.Id);
                byId[d.Id] = d;
            }
        }

        if (history != null) AddAll(history.Drawings(), true);
        if (live != null) AddAll(live.Drawings(), false);
        cs.Overlay.Shapes = order.Select(id => StrategyDrawingMap.ToShape(byId[id], color)).ToList();

        var lines = new Dictionary<string, (string? Color, int Width, LineStyle Style, List<(DateTime Time, decimal? Value)> Points)>();
        if (history != null)
            foreach (var pl in history.Plots())
                lines[pl.Name] = (pl.Color, pl.Width, pl.Style, pl.Points.Where(x => x.Time < liveStart).ToList());
        if (live != null)
            foreach (var pl in live.Plots())
            {
                var points = lines.TryGetValue(pl.Name, out var prev) ? prev.Points : new List<(DateTime Time, decimal? Value)>();
                points.AddRange(pl.Points);
                lines[pl.Name] = (pl.Color ?? prev.Color, pl.Width, pl.Style, points);
            }

        cs.Overlay.Lines = lines.Select(kv => StrategyDrawingMap.ToLine(kv.Key, kv.Value.Color, kv.Value.Width, kv.Value.Style, kv.Value.Points, color)).ToList();
        model.Invalidate();
    }

    /// <summary>State of an executing one, with how many contracts and of which one each order sends ("live · Simulated account · 1 NQ per order").</summary>
    private string LiveStatus(ChartStrategy cs, LiveStrategyRunner runner)
    {
        decimal units = runner.UnitsFor(LiveCenter.QuantityOf(runner.StrategyInstance.GetParameters()));
        string each = units > 0 ? $"{units.ToString("0.####", CultureInfo.GetCultureInfo("en-US"))} {runner.ExecInstrument.Symbol} per order" : "size at zero";
        return $"live · {runner.Account.Name} · {each}";
    }

    /// <summary>Open position: a dotted line from the entry to the current price, in the color of the open result.</summary>
    private static void SetOpenLink(ChartModel model, ChartStrategy cs, LiveLedger ledger)
    {
        DateTime opened = DateTime.MinValue;
        decimal pos = 0;
        foreach (var f in ledger.Fills.ToList())
        {
            decimal before = pos;
            pos += f.Buy ? f.Quantity : -f.Quantity;
            if (before == 0 || Math.Sign(before) != Math.Sign(pos)) opened = f.Time;
        }

        if (opened == DateTime.MinValue) return;
        SetOpenLink(model, cs, opened, ledger.AveragePrice, ledger.Position > 0, ledger.OpenPnL);
    }

    /// <summary>The position the history leaves open (while the visual one's simulated account has not taken it yet).</summary>
    private static void SetOpenLink(ChartModel model, ChartStrategy cs, OpenPosition open)
    {
        decimal pv = model.Instrument.PointValue > 0 ? model.Instrument.PointValue : 1;
        SetOpenLink(model, cs, open.EntryTime, open.AveragePrice, open.IsLong, last => (last - open.AveragePrice) * open.Quantity * pv * (open.IsLong ? 1 : -1));
    }

    private static void SetOpenLink(ChartModel model, ChartStrategy cs, DateTime opened, decimal average, bool isLong, Func<decimal, decimal> openPnl)
    {
        var data = model.Data;
        if (data.Trades.Count == 0) return;
        decimal last = data.LastPrice;
        var link = new TradeLink(opened, model.Instrument.RoundPrice(average), data.Trades.LastTime, last, isLong, openPnl(last));
        var links = new List<TradeLink>(cs.BaseLinks.Count + 1);
        links.AddRange(cs.BaseLinks);
        links.Add(link);
        cs.Overlay.Links = links;
        model.Invalidate();
    }

    internal void OpenStrategySettings(ChartTab chart, ChartStrategy cs)
    {
        if ((chart.Window ?? TopLevel.GetTopLevel(this) as Window) is not { } owner) return;
        new StrategyChartWindow(this, chart, cs).Show(owner);
    }

    /// <summary>Menu to place a strategy on the chart (the ones from Strategies).</summary>
    internal List<Control> StrategyMenuItems(ChartTab tab)
    {
        var items = new List<Control>();
        var catalog = _svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        foreach (var f in catalog.Files.OrderBy(f => f.DisplayName))
        {
            var mi = new MenuItem { Header = f.DisplayName, Icon = Ui.Icon(Material.Icons.MaterialIconKind.RobotOutline, 16) };
            var file = f;
            mi.Click += (_, _) => _ = AddStrategyAsync(tab, file);
            items.Add(mi);
        }

        if (items.Count == 0) items.Add(new MenuItem { Header = "There are no strategies yet: create one in Strategies", IsEnabled = false });
        if (tab.Strategies.Count > 0)
        {
            items.Add(new Separator());
            foreach (var cs in tab.Strategies.ToList())
            {
                var mi = new MenuItem { Header = $"Configure “{cs.Overlay.Name}” · {cs.Overlay.Status}", Icon = Ui.Icon(Material.Icons.MaterialIconKind.CogOutline, 16) };
                mi.Click += (_, _) => OpenStrategySettings(tab, cs);
                items.Add(mi);
            }
        }

        return items;
    }

    internal void ShowStrategyMenu(ChartTab? tab, Control anchor)
    {
        if (tab?.Model == null) return;
        var flyout = new MenuFlyout { Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight };
        foreach (var i in StrategyMenuItems(tab)) flyout.Items.Add(i);
        flyout.ShowAt(anchor);
    }
}
