using System.Globalization;
using Avalonia.Threading;
using TradingHub.App.Controls;
using TradingHub.App.Views;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>A live strategy seen from a single place, no matter who launched it.</summary>
internal sealed class LiveEntry
{
    public required string Key { get; init; }

    /// <summary>panel (Live section), chart (a chart) or another origin.</summary>
    public required string Source { get; init; }

    public required LiveRunnerInfo Info { get; set; }
    public LiveStrategyRunner? Runner { get; init; }
    public ChartTab? Chart { get; init; }
    public ChartStrategy? ChartStrategy { get; init; }

    /// <summary>Its bookkeeping (in memory if it is running; otherwise, the stored one).</summary>
    public LiveLedger Ledger { get; set; } = null!;

    public bool Running => Runner?.IsRunning == true;

    /// <summary>Locked: risk and parameters are not changed from Live.</summary>
    public bool Locked => IsManaged(Source);

    /// <summary>Managed by someone else and not touched from Live (in this version, none).</summary>
    public static bool IsManaged(string source) => false;

    /// <summary>On a chart, waiting for you to resume it (Trading Hub was reopened).</summary>
    public bool WaitingResume => ChartStrategy is { Live: true, Paused: true } && Runner is not { IsRunning: true };

    public bool Paused => Runner?.Paused ?? Info.Paused;

    public string SourceLabel => Source == "chart" ? "Chart" : "Live";

    public string StateLabel =>
        Running ? Runner!.DailyLimitActive ? "Halted for today" : Paused ? "Paused" : "Running"
        : WaitingResume ? "Waiting for you to resume it"
        : Runner?.IsClosing == true ? "Closing the position"
        : "Stopped";

    public decimal OpenPnL => Running ? Ledger.OpenPnL(Runner!.Data.LastPrice) : 0;
    public decimal Net => Ledger.Realized + OpenPnL;
}

/// <summary>A strategy's next order: how many contracts, of which one and whether it is the micro (with its mini equivalent).</summary>
internal readonly record struct OrderPlan(decimal Units, string Symbol, string SignalSymbol, bool Micro, bool AtMinimum, decimal Equivalent)
{
    private static readonly System.Globalization.CultureInfo Fmt = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    /// <summary>"5 MNQ", "1 NQ" or "at zero".</summary>
    public string Text => Units <= 0 ? "at zero" : $"{Units.ToString("0.####", Fmt)} {Symbol}";

    /// <summary>Note: "micro · equals 0.5 NQ", "minimum: it does not reach 1 contract" or empty.</summary>
    public string Detail =>
        Units <= 0 ? "it opens no new trades"
        : Micro ? $"micro · equals {Equivalent.ToString("0.####", Fmt)} {SignalSymbol}"
        : AtMinimum ? "minimum: the size does not reach 1 contract"
        : "";
}

/// <summary>What is needed to launch a live strategy from the Live section.</summary>
internal sealed class LiveLaunch
{
    public required StrategyFile File { get; init; }
    public required string ProfileId { get; init; }
    public required Instrument Instrument { get; init; }
    public required string AccountId { get; init; }
    public required ChartPeriod Period { get; init; }
    public Dictionary<string, object?> Parameters { get; init; } = new();
    public string? TimeZoneId { get; init; }
    public TimeSpan SessionStart { get; init; }
    public decimal Size { get; init; } = 1;
    public decimal MaxDailyLoss { get; init; }
    public decimal MaxDrawdown { get; init; }
    public bool CloseOnLimit { get; init; } = true;
    public string? PortfolioId { get; init; }
    public bool Paused { get; init; }
}

/// <summary>A portfolio with its strategies already resolved.</summary>
internal sealed class LivePortfolioView
{
    public required LivePortfolioDef Def { get; init; }
    public required List<LiveEntry> Members { get; init; }
    public bool HasReal => Members.Any(m => m.Info.IsReal);
}

/// <summary>
/// Every live strategy in a single place: the ones from the Live panel and the ones executing from a chart. From here they are managed (size, limits, parameters, pause,
/// stop, delete), the live portfolios are built and each portfolio's risk agent runs.
/// </summary>
internal sealed partial class LiveCenter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppServices _svc;
    private readonly HashSet<LiveStrategyRunner> _watched = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, (long Length, LiveLedger Ledger)> _ledgers = new();
    private readonly Dictionary<string, (decimal Mae, decimal Mfe, bool Exact)> _excursions = new();
    private DateTime _lastTick;
    private DateTime _proposalsStamp;

    public LiveCenter(AppServices svc)
    {
        _svc = svc;
        RiskAgent = new LiveRiskAgent(svc, this);
    }

    /// <summary>The Trading section (for the charts' strategies and to show trades).</summary>
    public Func<TradingView?>? Trading { get; set; }

    /// <summary>The portfolios' risk agent.</summary>
    public LiveRiskAgent RiskAgent { get; }

    /// <summary>The risk agent is reviewing some portfolio.</summary>
    public bool RiskAgentBusy => RiskAgent.AnyRunning;

    /// <summary>Important notices (limit hit, agent proposals) for the status bar.</summary>
    public event Action<string>? Notice;

    /// <summary>Latest notices (shown by the Live section).</summary>
    public List<(DateTime Time, string Text)> Notices { get; } = new();

    internal void Notify(string text)
    {
        Notices.Add((DateTime.Now, text));
        if (Notices.Count > 50) Notices.RemoveAt(0);
        Notice?.Invoke(text);
    }

    // ===================================================================== log

    /// <summary>Gives a strategy that is running again whatever it had stored (size, limits, pause and portfolio).</summary>
    public static void Restore(LiveStrategyRunner r)
    {
        var saved = LiveStore.InfoFor(r.Key);
        if (saved == null) return;
        r.SizeMultiplier = saved.Size;
        r.MaxDailyLoss = saved.MaxDailyLoss;
        r.MaxDrawdown = saved.MaxDrawdown;
        r.CloseOnLimit = saved.CloseOnLimit;
        r.PortfolioId ??= saved.PortfolioId;
        if (saved.Paused) r.SetPaused(true);
    }

    /// <summary>Subscribes to a strategy's notices (once): limits hit and filled orders.</summary>
    public void Watch(LiveStrategyRunner r)
    {
        if (!_watched.Add(r)) return;
        r.LimitHit += (_, msg) => Dispatcher.UIThread.Post(() =>
        {
            Notify("⚠ " + msg);
            _svc.SaveLiveState();
        });
        r.Executed += (_, msg) => Dispatcher.UIThread.Post(() => Notify(msg));
    }

    /// <summary>The one running with that key (from any origin).</summary>
    public LiveStrategyRunner? Running(string key) =>
        _svc.Runners.FirstOrDefault(r => r.Key == key && r.IsRunning)
        ;

    private LiveLedger LedgerOf(string key, decimal pointValue)
    {
        long length = 0;
        try
        {
            var f = new FileInfo(LiveStore.LedgerFile(key));
            length = f.Exists ? f.Length : 0;
        }
        catch
        {
            // no file
        }

        if (_ledgers.TryGetValue(key, out var c) && c.Length == length) return c.Ledger;
        var l = LiveStore.LoadLedger(key, pointValue);
        _ledgers[key] = (length, l);
        return l;
    }

    /// <summary>All of them: the ones running (panel and charts) and the stored ones that are stopped.</summary>
    public List<LiveEntry> Entries(bool includeStopped = true)
    {
        var list = new List<LiveEntry>();
        var seen = new HashSet<string>();
        var stored = LiveStore.LoadRunners();

        // 1) charts: the ones executing and the ones waiting for you to resume them
        if (Trading?.Invoke() is { } tv)
        {
            foreach (var chart in tv.AllCharts)
            {
                foreach (var cs in chart.Strategies.Where(c => c.Live))
                {
                    if (cs.Runner is { Persist: true } r && seen.Add(r.Key))
                    {
                        list.Add(new LiveEntry { Key = r.Key, Source = "chart", Info = r.Info(), Runner = r, Chart = chart, ChartStrategy = cs, Ledger = r.Ledger });
                        continue;
                    }

                    if (cs.Runner is { IsRunning: true }) continue;
                    var inst = chart.Model?.Instrument;
                    var info = stored.FirstOrDefault(i => i.StrategyId == cs.StrategyId && inst != null && i.FeedId == inst.FeedId && i.Symbol == inst.Symbol &&
                                                         (i.AccountId == cs.AccountId || (i.AccountId.Length == 0 && !LiveEntry.IsManaged(i.Source))));
                    string key = info?.Key ?? $"chart|{cs.StrategyId}|{inst?.FeedId}:{chart.Symbol}|{cs.AccountId}";
                    if (!seen.Add(key)) continue;
                    info ??= new LiveRunnerInfo
                    {
                        Key = key,
                        Strategy = cs.Overlay.Name,
                        StrategyId = cs.StrategyId,
                        FeedId = inst?.FeedId ?? chart.FeedId,
                        Symbol = chart.Symbol,
                        Period = chart.Model?.Period.Code ?? "",
                        Account = cs.AccountId,
                        AccountId = cs.AccountId,
                        Source = "chart",
                        PointValue = inst?.PointValue ?? 1,
                    };
                    list.Add(new LiveEntry { Key = key, Source = "chart", Info = info, Runner = cs.Runner, Chart = chart, ChartStrategy = cs, Ledger = LedgerOf(key, info.PointValue) });
                }
            }
        }

        // 2) the panel ones (and the chart ones that are no longer on a chart)
        foreach (var r in _svc.Runners.Where(r => r.Persist))
        {
            if (!r.IsRunning && !r.IsClosing && !includeStopped) continue;
            if (!seen.Add(r.Key)) continue;
            list.Add(new LiveEntry { Key = r.Key, Source = r.Source == "chart" ? "panel" : r.Source, Info = r.Info(), Runner = r, Ledger = r.Ledger });
        }


        // 4) stored and stopped
        if (includeStopped)
        {
            foreach (var info in stored)
            {
                if (seen.Contains(info.Key)) continue;
                seen.Add(info.Key);
                info.Running = false;
                list.Add(new LiveEntry { Key = info.Key, Source = info.Source == "chart" ? "chart" : "panel", Info = info, Ledger = LedgerOf(info.Key, info.PointValue) });
            }
        }

        return list;
    }

    public LiveEntry? Find(string key) => Entries().FirstOrDefault(e => e.Key == key);

    // ===================================================================== management

    /// <summary>Changes size and limits (of a running one or of what is stored). Locked ones are not touched.</summary>
    public bool ApplyRisk(LiveEntry e, decimal size, decimal maxDailyLoss, decimal maxDrawdown, bool closeOnLimit)
    {
        if (e.Locked) return false;
        size = Math.Max(0, size);
        var r = e.Runner ?? Running(e.Key);
        if (r != null)
        {
            r.SizeMultiplier = size;
            r.MaxDailyLoss = Math.Max(0, maxDailyLoss);
            r.MaxDrawdown = Math.Max(0, maxDrawdown);
            r.CloseOnLimit = closeOnLimit;
            r.Log($"Risk changed: size ×{size.ToString("0.##", Inv)} · max daily loss {(maxDailyLoss > 0 ? maxDailyLoss.ToString("0.##", Inv) : "no limit")} · max drawdown {(maxDrawdown > 0 ? maxDrawdown.ToString("0.##", Inv) : "no limit")}");
            _svc.SaveLiveState();
        }

        LiveStore.UpdateRunner(e.Key, i =>
        {
            i.Size = size;
            i.MaxDailyLoss = Math.Max(0, maxDailyLoss);
            i.MaxDrawdown = Math.Max(0, maxDrawdown);
            i.CloseOnLimit = closeOnLimit;
        });
        return true;
    }

    /// <summary>Applies an approved size (from a proposal or from the table's buttons).</summary>
    public void ApplySize(string key, decimal size)
    {
        var e = Entries().FirstOrDefault(x => x.Key == key);
        if (e == null)
        {
            LiveStore.UpdateRunner(key, i => i.Size = size);
            return;
        }

        if (e.Locked) return;
        ApplyRisk(e, size, e.Runner?.MaxDailyLoss ?? e.Info.MaxDailyLoss, e.Runner?.MaxDrawdown ?? e.Info.MaxDrawdown, e.Runner?.CloseOnLimit ?? e.Info.CloseOnLimit);
    }

    /// <summary>Pauses or resumes (it keeps receiving the market; while paused it opens no new trades).</summary>
    public void SetPaused(LiveEntry e, bool paused)
    {
        if (e.Locked) return;
        if (e.Runner is { IsRunning: true } r) r.SetPaused(paused);
        LiveStore.UpdateRunner(e.Key, i => i.Paused = paused);
        _svc.SaveLiveState();
    }

    /// <summary>Closes the strategy's position at market (it keeps running; unless it is paused it will not re-enter on its own).</summary>
    public void ClosePosition(LiveEntry e)
    {
        var r = e.Runner ?? Running(e.Key);
        r?.ClosePosition("Manual close");
        _svc.SaveLiveState();
    }

    /// <summary>Stops it (optionally closing its position). A chart's one goes back to visual mode.</summary>
    public async Task StopAsync(LiveEntry e, bool close)
    {

        var r = e.Runner;
        if (r is { IsRunning: true })
        {
            r.StopReason = "You stopped it";
            r.Stop(close);
        }

        if (e.ChartStrategy is { } cs && e.Chart is { } chart && Trading?.Invoke() is { } tv)
        {
            cs.Live = false;
            cs.Paused = false;
            await tv.RunStrategyAsync(chart, cs);
        }

        _svc.SaveLiveState();
    }

    /// <summary>Runs again: a chart's one resumes on its chart; a panel one, with what was stored.</summary>
    public async Task<string?> ResumeAsync(LiveEntry e)
    {

        if (e.ChartStrategy is { } cs && e.Chart is { } chart && Trading?.Invoke() is { } tv)
        {
            cs.Live = true;
            cs.Paused = false;
            await tv.RunStrategyAsync(chart, cs);
            return cs.Overlay.Error;
        }

        var launch = await LaunchFromInfoAsync(e.Info);
        if (launch.Error != null) return launch.Error;
        var (runner, message) = await LaunchAsync(launch.Spec!);
        return runner == null ? message : null;
    }

    /// <summary>
    /// On opening Trading Hub: restarts the strategies that were running when it closed (if enabled in Settings; the ones on
    /// real accounts only if you enabled that too), with their parameters, size, limits and pause.
    /// </summary>
    public async Task ResumeAtStartAsync()
    {
        var s = _svc.Settings;
        var was = _svc.RunningAtLastClose;
        _svc.RunningAtLastClose = new(); // only once per launch
        if (!s.AutoResumeLive || was.Count == 0) return;
        var byKey = was.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.First());
        var resumed = new List<string>();
        var waiting = new List<string>();
        var failed = new List<string>();
        foreach (var e in Entries().Where(e => byKey.ContainsKey(e.Key) && !e.Running && !e.Locked).ToList())
        {
            var info = byKey[e.Key];
            string name = $"“{info.Strategy}” {info.Symbol}";
            if ((info.IsReal || e.Info.IsReal) && !s.AutoResumeLiveReal)
            {
                waiting.Add(name);
                continue;
            }

            try
            {
                var error = await ResumeAsync(e);
                if (error != null)
                {
                    failed.Add($"{name}: {error}");
                    continue;
                }

                if (info.Paused && Running(e.Key) is { } r) r.SetPaused(true);
                resumed.Add(name);
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                failed.Add($"{name}: {ex.Message}");
            }
        }

        var parts = new List<string>();
        if (resumed.Count > 0) parts.Add($"Resumed {resumed.Count} live strategy(ies) that were running when Trading Hub closed: {string.Join(", ", resumed)}");
        if (waiting.Count > 0) parts.Add($"Waiting for you to resume them (real account): {string.Join(", ", waiting)}");
        if (failed.Count > 0) parts.Add($"Could not resume {string.Join("; ", failed)}");
        if (parts.Count > 0) Notify(string.Join(" · ", parts));
        _svc.SaveLiveState();
    }

    /// <summary>Rebuilds how a stored strategy was launched (null if something is missing, with the reason).</summary>
    public async Task<(LiveLaunch? Spec, string? Error)> LaunchFromInfoAsync(LiveRunnerInfo info)
    {
        var catalog = _svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        var file = catalog.Files.FirstOrDefault(f => f.Id == info.StrategyId) ?? catalog.Files.FirstOrDefault(f => f.FileName == info.StrategyId.Split('.').Last());
        if (file == null) return (null, $"The strategy “{info.Strategy}” was not found in Backtesting.");
        if (file.Type == null) await Task.Run(() => catalog.Compile(file));
        if (file.Type == null) return (null, $"“{info.Strategy}” has compilation errors.");
        string profileId = info.ProfileId.Length > 0 ? info.ProfileId : _svc.Connections.Enabled.FirstOrDefault(p => p.MarketDataId == info.FeedId)?.Id ?? "";
        if (profileId.Length == 0) return (null, $"There is no active connection for {info.FeedId}.");
        // the ones stored by an older version have no account: the simulated one is recognized by name
        string accountId = info.AccountId.Length > 0 ? info.AccountId : info.Account == "Simulated account" ? "sim" : "";
        if (accountId.Length == 0) return (null, "The account it traded on is unknown (it comes from an older version): launch it again from “New live strategy”.");
        var instruments = await InstrumentPicker.InstrumentsAsync(_svc, profileId);
        var inst = instruments.FirstOrDefault(i => i.Symbol.Equals(info.Symbol, StringComparison.OrdinalIgnoreCase));
        if (inst == null) return (null, $"{info.Symbol} is not on the connection.");
        if (!ChartPeriod.TryParse(info.Period, out var period)) period = ChartPeriod.M5;
        var parameters = info.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value);
        string? timeZone = info.TimeZoneId;
        var session = TimeSpan.FromHours(info.SessionStartHours);
        if (parameters.Count == 0)
        {
            // stored by an older version (with no parameters): the ones from its Backtest, which are the ones it had been launched with
            var saved = SavedBacktest(file, _svc.Settings.TimeZoneId);
            parameters = saved.Parameters;
            if (info.AccountId.Length == 0)
            {
                timeZone = saved.TimeZoneId;
                session = saved.SessionStart;
            }
        }

        return (new LiveLaunch
        {
            File = file,
            ProfileId = profileId,
            Instrument = inst,
            AccountId = accountId,
            Period = period,
            Parameters = parameters,
            TimeZoneId = timeZone,
            SessionStart = session,
            Size = info.Size,
            MaxDailyLoss = info.MaxDailyLoss,
            MaxDrawdown = info.MaxDrawdown,
            CloseOnLimit = info.CloseOnLimit,
            PortfolioId = info.PortfolioId,
            Paused = info.Paused,
        }, null);
    }

    private sealed class SavedUi
    {
        public BacktestSettings? Settings { get; set; }
        public Dictionary<string, System.Text.Json.JsonElement> Parameters { get; set; } = new();
    }

    /// <summary>
    /// The last thing configured in a strategy's Backtest: parameters (the missing ones, from the class), candles and
    /// session. It is what "New live strategy" uses and what is taken from one stored by an older version.
    /// </summary>
    public static (Dictionary<string, object?> Parameters, string? Period, string? TimeZoneId, TimeSpan SessionStart) SavedBacktest(StrategyFile file, string? defaultTimeZone)
    {
        var values = file.Type != null ? file.Create().GetParameters() : new Dictionary<string, object?>();
        string? period = null, timeZone = defaultTimeZone;
        var session = TimeSpan.Zero;
        try
        {
            var path = Path.Combine(AppPaths.Backtests, string.Concat(file.Id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)), "ui.json");
            if (File.Exists(path))
            {
                var opts = new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
                var saved = System.Text.Json.JsonSerializer.Deserialize<SavedUi>(File.ReadAllText(path), opts);
                if (saved != null)
                {
                    foreach (var (k, v) in saved.Parameters)
                        if (values.ContainsKey(k)) values[k] = v.Clone();
                    if (saved.Settings is { } st)
                    {
                        if (!string.IsNullOrWhiteSpace(st.Period)) period = st.Period;
                        timeZone = st.TimeZoneId;
                        session = st.SessionStart;
                    }
                }
            }
        }
        catch (Exception)
        {
            // with no stored state: the ones from the class
        }

        return (values, period, timeZone, session);
    }

    /// <summary>The parameters a Live strategy runs (or would run) with.</summary>
    /// <summary>
    /// Under which market conditions it opens trades (the strategy's context filter): it applies right away, without restarting,
    /// and is stored for when it resumes. Locked ones are not. null if it went fine.
    /// </summary>
    public string? ApplyContextFilter(LiveEntry e, RegimeFilter filter, int minRank, int maxRank)
    {
        if (e.Locked) return "It is locked: its market condition cannot be changed from here.";
        if (e.Runner is { IsRunning: true } r) r.SetContextFilter(filter, minRank, maxRank);
        void Put(IDictionary<string, object?> p)
        {
            p["RegimeFilter"] = (int)filter;
            p["MinVolatilityRank"] = Math.Clamp(minRank, 0, 100);
            p["MaxVolatilityRank"] = Math.Clamp(Math.Max(maxRank, minRank), 0, 100);
        }

        // what is stored to resume it (and, if it belongs to a chart, its configuration on the chart)
        Put(e.Info.Parameters);
        if (e.ChartStrategy is { } cs) Put(cs.Parameters);
        if (e.Runner is not { IsRunning: true })
        {
            var infos = LiveStore.LoadRunners().Where(i => i.Key != e.Info.Key).Append(e.Info).ToList();
            LiveStore.SaveRunners(infos);
        }

        _svc.SaveLiveState();
        string what = Robustness.DescribeFilter(filter, minRank, maxRank);
        Notice?.Invoke($"“{e.Info.Strategy}” trades {(what.Length > 0 ? what : "in any market condition")}");
        return null;
    }

    public Dictionary<string, object?> ParametersOf(LiveEntry e)
    {
        if (e.Runner is { } r) return r.StrategyInstance.GetParameters();
        if (e.ChartStrategy is { } cs) return new Dictionary<string, object?>(cs.Parameters);
        if (e.Info.Parameters.Count > 0) return e.Info.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value);
        var catalog = _svc.Strategies;
        if (catalog.Files.Count == 0) catalog.Refresh();
        var file = catalog.Files.FirstOrDefault(f => f.Id == e.Info.StrategyId);
        return file != null ? SavedBacktest(file, _svc.Settings.TimeZoneId).Parameters : new Dictionary<string, object?>();
    }

    /// <summary>Launches a live strategy (the real-account confirmation is asked by the window beforehand).</summary>
    public async Task<(LiveStrategyRunner? Runner, string Message)> LaunchAsync(LiveLaunch spec)
    {
        if (spec.File.Type == null) await Task.Run(() => _svc.Strategies.Compile(spec.File));
        if (spec.File.Type == null) return (null, $"“{spec.File.FileName}” has errors: fix it in Backtesting.");
        var data = _svc.Open(spec.ProfileId, spec.Instrument);
        for (int i = 0; i < 240 && data.IsLoadingHistory; i++) await Task.Delay(500);
        var strategy = spec.File.Create();
        strategy.SetParameters(spec.Parameters);
        var (execInst, ratio) = await ExecutionContractAsync(spec, strategy, data);
        var execData = ReferenceEquals(execInst, spec.Instrument) ? data : _svc.Open(spec.ProfileId, execInst);
        var account = _svc.AccountFor(execData, spec.AccountId);
        var runner = new LiveStrategyRunner(strategy, data, account, spec.Period, spec.TimeZoneId, spec.SessionStart)
        {
            ExecInstrument = execInst,
            ExecRatio = ratio,
            Source = "panel",
            AccountId = spec.AccountId,
            ProfileId = spec.ProfileId,
            SizeMultiplier = spec.Size,
            MaxDailyLoss = spec.MaxDailyLoss,
            MaxDrawdown = spec.MaxDrawdown,
            CloseOnLimit = spec.CloseOnLimit,
            PortfolioId = spec.PortfolioId,
            ProtectOnStart = true,
        };
        if (Running(runner.Key) != null) return (null, $"“{runner.StrategyName}” is already running on {spec.Instrument.Symbol} with {account.Name}.");
        try
        {
            runner.Start();
        }
        catch (Exception ex)
        {
            return (null, "Could not start: " + ex.Message);
        }

        if (spec.Paused) runner.SetPaused(true);
        _svc.Runners.RemoveAll(r => r.Key == runner.Key && !r.IsRunning && !r.IsClosing);
        _svc.Runners.Add(runner);
        Watch(runner);
        _svc.SaveLiveState();
        if (MarketInfo.IsFutures(spec.Instrument.FeedId))
        {
            decimal units = runner.UnitsFor(QuantityOf(spec.Parameters));
            Notify(runner.CrossExecution
                ? $"“{runner.StrategyName}” trades micros: each order goes with {units:0.####} {execInst.Symbol} (equal to {units / ratio:0.####} {spec.Instrument.Symbol})"
                : $"“{runner.StrategyName}” trades {units:0.####} {execInst.Symbol} per order");
        }

        string where = runner.CrossExecution ? $"{spec.Instrument.Symbol}, executes on {execInst.Symbol}" : spec.Instrument.Symbol;
        return (runner, $"{runner.StrategyName} running live on {account.Name} ({where})");
    }

    // ===================================================================== smart contract

    /// <summary>
    /// How a strategy's next order comes out: how many contracts and of which one (the strategy's quantity x the size,
    /// in whole contracts, on the micro if the smart contract picked it). It is what is shown everywhere for
    /// so there is never any doubt whether it trades 5 NQ or 5 MNQ.
    /// </summary>
    internal OrderPlan PlanOf(LiveEntry e)
    {
        if (e.Runner is { } r)
        {
            decimal q = QuantityOf(r.StrategyInstance.GetParameters());
            return new OrderPlan(r.UnitsFor(q), r.ExecInstrument.Symbol, r.Instrument.Symbol, r.CrossExecution, r.SizeMultiplier > 0 && r.SizeBelowMinimum(q),
                r.CrossExecution && r.ExecRatio > 0 ? r.UnitsFor(q) / r.ExecRatio : 0);
        }

        // stopped: what it would send when running again (with an open position, it stays on the contract it is on)
        var info = e.Info;
        decimal qty = QuantityOf(info.Parameters), size = info.Size;
        var (symbol, ratio) = PreferredContract(info.FeedId, info.Symbol, qty, size);
        if (e.Ledger.Position != 0 && e.Ledger.OpenSymbol is { } open && !open.Equals(symbol, StringComparison.OrdinalIgnoreCase))
        {
            symbol = open;
            ratio = open.Equals(info.Symbol, StringComparison.OrdinalIgnoreCase) ? 1 : RatioOf(info.Symbol, open);
        }

        decimal step = MarketInfo.VolumeStep(info.FeedId, symbol) is var st && st > 0 ? st : 1;
        decimal exact = Math.Abs(qty) * size * ratio;
        decimal units = size <= 0 ? 0 : Math.Max(1, Math.Floor(exact / step + 0.0000001m)) * step;
        bool micro = !symbol.Equals(info.Symbol, StringComparison.OrdinalIgnoreCase);
        return new OrderPlan(units, symbol, info.Symbol, micro, size > 0 && exact < step, micro && ratio > 0 ? units / ratio : 0);
    }

    /// <summary>Quantity per order the strategy asks for (its Quantity parameter, or 1).</summary>
    internal static decimal QuantityOf(IReadOnlyDictionary<string, object?> parameters) =>
        parameters.TryGetValue("Quantity", out var q) && Robustness.ToDouble(q) is { } d && d > 0 ? (decimal)d : 1;

    /// <summary>
    /// Smart contract: on a future with a micro, if the size asks for fractions of the mini (1 NQ x 0.5) it executes on the
    /// micro with the same exposure (5 MNQ); with whole minis, on the mini. Returns the contract and how many of that contract
    /// one of the strategy's watched contract is worth.
    /// </summary>
    internal (string Symbol, decimal Ratio) PreferredContract(string feed, string symbol, decimal strategyQuantity, decimal size)
    {
        if (!_svc.Settings.SmartContracts || !MarketInfo.IsFutures(feed) || MarketInfo.MicroOf(symbol) is not { } micro) return (symbol, 1);
        decimal exposure = Math.Abs(strategyQuantity) * size;
        if (size <= 0 || (exposure >= 1 && exposure == Math.Floor(exposure))) return (symbol, 1);
        return (micro, RatioOf(symbol, micro));
    }

    private static decimal RatioOf(string symbol, string exec)
    {
        decimal a = FuturesCatalog.Find(symbol)?.PointValue ?? 0, b = FuturesCatalog.Find(exec)?.PointValue ?? 0;
        return a > 0 && b > 0 ? a / b : 1;
    }

    /// <summary>
    /// Which contract it executes on when launched: the preferred one for its size, unless it has an open position (it stays on
    /// the contract it is on, and only changes once it is flat).
    /// </summary>
    private Task<(Instrument Instrument, decimal Ratio)> ExecutionContractAsync(LiveLaunch spec, Strategy strategy, InstrumentData data) =>
        ExecutionContractForAsync(spec.ProfileId, spec.Instrument, spec.Parameters, spec.Size, strategy, _svc.AccountFor(data, spec.AccountId).Name);

    /// <summary>
    /// Which contract a strategy (from the panel or from a chart) executes on with that size: the preferred one (the micro if the size
    /// asks for fractions of the mini), unless it has an open position, in which case it stays on the contract it is on.
    /// </summary>
    internal async Task<(Instrument Instrument, decimal Ratio)> ExecutionContractForAsync(string profileId, Instrument signal, IReadOnlyDictionary<string, object?> parameters,
        decimal size, Strategy strategy, string accountName)
    {
        var (symbol, ratio) = PreferredContract(signal.FeedId, signal.Symbol, QuantityOf(parameters), size);
        string key = $"{strategy.GetType().FullName}|{signal.FeedId}:{signal.Symbol}|{accountName}";
        var ledger = LiveStore.LoadLedger(key, signal.PointValue);
        if (ledger.Position != 0)
        {
            symbol = ledger.OpenSymbol ?? signal.Symbol;
            ratio = symbol.Equals(signal.Symbol, StringComparison.OrdinalIgnoreCase) ? 1 : RatioOf(signal.Symbol, symbol);
        }

        if (symbol.Equals(signal.Symbol, StringComparison.OrdinalIgnoreCase)) return (signal, 1);
        try
        {
            var list = await InstrumentPicker.InstrumentsAsync(_svc, profileId);
            if (list.FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase)) is { } found) return (found, ratio);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        return (signal, 1); // the connection does not offer it: on the same contract
    }

    private readonly Dictionary<string, DateTime> _contractSwitchTried = new();

    /// <summary>
    /// The size changed and another contract is now better (for example from x0.5 to x1: from the micro to the mini): if the strategy is
    /// flat and has no orders, it is restarted on the new contract (with the same bookkeeping).
    /// </summary>
    private void CheckContracts()
    {
        if (!_svc.Settings.SmartContracts) return;
        foreach (var r in _svc.Runners.Where(r => r is { IsRunning: true, Persist: true } && r.Source is "panel" or "chart").ToList())
        {
            if (r.Ledger.Position != 0 || r.HasWorkingOrders || r.IsClosing) continue;
            var (symbol, _) = PreferredContract(r.Instrument.FeedId, r.Instrument.Symbol, QuantityOf(r.StrategyInstance.GetParameters()), r.SizeMultiplier);
            if (symbol.Equals(r.ExecInstrument.Symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (_contractSwitchTried.TryGetValue(r.Key, out var last) && (DateTime.UtcNow - last).TotalMinutes < 5) continue;
            _contractSwitchTried[r.Key] = DateTime.UtcNow;
            if (Find(r.Key) is not { } e) continue;
            Notify($"“{r.StrategyName}” with size ×{r.SizeMultiplier:0.##}: it now executes on {symbol} (it was flat, so it restarted)");
            _ = RestartWithParametersAsync(e, r.StrategyInstance.GetParameters());
        }
    }

    /// <summary>Restarts with other parameters (it keeps the same bookkeeping and, if it had a position, it keeps managing it).</summary>
    public async Task<string?> RestartWithParametersAsync(LiveEntry e, Dictionary<string, object?> parameters)
    {
        if (e.Locked) return "It is locked: its parameters cannot be changed from here.";
        if (e.ChartStrategy is { } cs && e.Chart is { } chart && Trading?.Invoke() is { } tv)
        {
            cs.Parameters = new Dictionary<string, object?>(parameters);
            cs.Paused = false;
            await tv.RunStrategyAsync(chart, cs);
            return cs.Overlay.Error;
        }

        var launch = await LaunchFromInfoAsync(e.Info);
        if (launch.Error != null) return launch.Error;
        var old = e.Runner;
        if (old is { IsRunning: true })
        {
            old.StopReason = "Restarted with new parameters";
            old.Stop(flatten: false);
        }

        var spec = launch.Spec!;
        var (runner, message) = await LaunchAsync(new LiveLaunch
        {
            File = spec.File,
            ProfileId = spec.ProfileId,
            Instrument = spec.Instrument,
            AccountId = spec.AccountId,
            Period = spec.Period,
            Parameters = parameters,
            TimeZoneId = spec.TimeZoneId,
            SessionStart = spec.SessionStart,
            Size = old?.SizeMultiplier ?? spec.Size,
            MaxDailyLoss = old?.MaxDailyLoss ?? spec.MaxDailyLoss,
            MaxDrawdown = old?.MaxDrawdown ?? spec.MaxDrawdown,
            CloseOnLimit = old?.CloseOnLimit ?? spec.CloseOnLimit,
            PortfolioId = old?.PortfolioId ?? spec.PortfolioId,
            Paused = old?.Paused ?? spec.Paused,
        });
        return runner == null ? message : null;
    }

    /// <summary>Clears a strategy's trade history and starts from scratch (only with no open position).</summary>
    public string? ResetHistory(LiveEntry e)
    {
        if (e.Locked) return "It is locked: its history cannot be cleared from here.";
        if (e.Runner is { } r && (r.IsRunning || r.IsClosing))
        {
            if (!r.ResetLedger()) return "It has an open position: close it first.";
        }
        else LiveStore.DeleteFills(e.Key); // stopped: it is cleared even if its bookkeeping says it had a position (the account rules)

        _ledgers.Remove(e.Key);
        LiveStore.UpdateRunner(e.Key, i =>
        {
            i.Position = 0;
            i.OpenPnL = 0;
            i.Realized = 0;
            i.Trades = 0;
        });
        _svc.SaveLiveState();
        return null;
    }

    /// <summary>Deletes a strategy from Live (stopping it if it was running; with <paramref name="history"/>, its history too).</summary>
    public async Task<string?> DeleteAsync(LiveEntry e, bool history)
    {
        if (e.Locked) return "It is locked: it cannot be deleted from here.";
        if (e.Runner is { IsRunning: true } r)
        {
            r.StopReason = "Deleted";
            r.Stop(flatten: false);
        }

        if (e.ChartStrategy is { } cs && e.Chart is { } chart && Trading?.Invoke() is { } tv) tv.RemoveStrategy(chart, cs);
        _svc.Runners.RemoveAll(x => x.Key == e.Key && !x.IsClosing);
        LiveStore.RemoveRunner(e.Key, history);
        _ledgers.Remove(e.Key);
        // from the portfolios as well
        var defs = LiveStore.LoadPortfolios();
        foreach (var d in defs) d.Members.Remove(e.Key);
        LiveStore.SavePortfolios(defs);
        await Task.CompletedTask;
        return null;
    }

    // ===================================================================== kill switch

    /// <summary>
    /// Halt everything: every live strategy (panel and charts), cancelling their orders and closing their positions.
    /// </summary>
    public async Task<int> KillAllAsync(string reason)
    {
        int stopped = 0;

        foreach (var e in Entries(includeStopped: false).Where(x => x.Running && !x.Locked))
        {
            await StopAsync(e, close: true);
            stopped++;
        }

        Notify($"⛔ You stopped everything ({stopped} strategy(ies)): {reason}");
        _svc.SaveLiveState();
        return stopped;
    }

    // ===================================================================== trades

    /// <summary>Stable identifier of a closed trade (for the excursion cache).</summary>
    public static string TradeId(string key, ClosedTrade t) => $"{key}#{t.Number}#{t.EntryTime.Ticks}";

    /// <summary>
    /// MAE and MFE of live trades: with the prices in memory if the instrument is open, or with the ones stored
    /// on disk. With no data for that moment it is left uncalculated (Exact = false).
    /// </summary>
    public async Task<Dictionary<string, (decimal Mae, decimal Mfe, bool Exact)>> ExcursionsAsync(IReadOnlyList<(LiveEntry Entry, ClosedTrade Trade)> items)
    {
        var result = new Dictionary<string, (decimal, decimal, bool)>();
        var pending = new List<(string Id, string Feed, string Symbol, decimal PointValue, ClosedTrade Trade)>();
        foreach (var (e, t) in items)
        {
            string id = TradeId(e.Key, t);
            if (_excursions.TryGetValue(id, out var cached))
            {
                result[id] = cached;
                continue;
            }

            // in memory if the instrument is open (the bookkeeping times come from the Mac and the market's
            // can arrive a few seconds late: if the market's last trade does not reach the exit, it is not stored)
            var data = _svc.Hub.Items.FirstOrDefault(d => d.Instrument.FeedId == e.Info.FeedId && d.Instrument.Symbol == e.Info.Symbol);
            if (data is { Trades.Count: > 0 } && data.Trades.FirstTime <= t.EntryTime.AddSeconds(5) && data.Trades.LastTime >= t.ExitTime.AddSeconds(-60))
            {
                var store = data.Trades;
                int i0 = store.LowerBound(t.EntryTime.AddSeconds(-2)), i1 = store.LowerBound(t.ExitTime.AddSeconds(2));
                var ex = TradeExcursion.Compute(t, Prices(store, i0, i1), e.Info.PointValue);
                result[id] = (ex.Mae, ex.Mfe, true);
                if (store.LastTime >= t.ExitTime.AddSeconds(2)) _excursions[id] = (ex.Mae, ex.Mfe, true);
            }
            else pending.Add((id, e.Info.FeedId, e.Info.Symbol, e.Info.PointValue, t));
        }

        if (pending.Count == 0) return result;
        var root = AppPaths.Ticks;
        var computed = await Task.Run(() =>
        {
            var res = new List<(string Id, decimal Mae, decimal Mfe, bool Exact)>();
            foreach (var p in pending)
            {
                try
                {
                    var prices = HistoricalData.Read(root, p.Feed, p.Symbol, p.Trade.EntryTime, p.Trade.ExitTime.AddSeconds(1)).Select(x => x.Price).ToList();
                    if (prices.Count == 0)
                    {
                        res.Add((p.Id, 0, 0, false));
                        continue;
                    }

                    var ex = TradeExcursion.Compute(p.Trade, prices, p.PointValue);
                    res.Add((p.Id, ex.Mae, ex.Mfe, true));
                }
                catch
                {
                    res.Add((p.Id, 0, 0, false));
                }
            }

            return res;
        });
        foreach (var c in computed)
        {
            result[c.Id] = (c.Mae, c.Mfe, c.Exact);
            if (c.Exact) _excursions[c.Id] = (c.Mae, c.Mfe, true);
        }

        return result;
    }

    private static IEnumerable<decimal> Prices(TradeStore store, int from, int to)
    {
        for (int i = from; i < to && i < store.Count; i++) yield return store.GetPrice(i);
    }

    /// <summary>Shows a trade on a chart (opens one for the instrument if there is none).</summary>
    public async Task<string?> ShowTradeAsync(LiveEntry e, ClosedTrade? t)
    {
        if (Trading?.Invoke() is not { } tv) return "Open the Trading section first.";
        string profileId = e.Info.ProfileId.Length > 0 ? e.Info.ProfileId : _svc.Connections.Enabled.FirstOrDefault(p => p.MarketDataId == e.Info.FeedId)?.Id ?? e.Info.FeedId;
        if (!ChartPeriod.TryParse(e.Info.Period, out var period)) period = ChartPeriod.M5;
        return await tv.ShowTradeAsync(profileId, e.Info.Symbol, period, e.Chart, t, $"{e.Info.Strategy}");
    }

    // ===================================================================== clock

    /// <summary>From the app's clock (a few times per second): each portfolio's risk agent.</summary>
    public void Tick()
    {
        var now = DateTime.UtcNow;
        // Claude (the Backtesting lab) left size proposals: it is reported once
        var stamp = LiveStore.ProposalsStamp;
        if (stamp != _proposalsStamp)
        {
            bool first = _proposalsStamp == default;
            _proposalsStamp = stamp;
            if (!first && LiveStore.LoadProposals().Any(p => p.Source == "claude" && p.IsPending && p.CreatedUtc.ToUniversalTime() > now.AddMinutes(-10)))
                Notify("Claude proposed size changes: review them in Live → Proposals");
        }

        if ((now - _lastTick).TotalSeconds < 20) return;
        _lastTick = now;
        try
        {
            CheckContracts();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        try
        {
            RiskAgent.Tick();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}
