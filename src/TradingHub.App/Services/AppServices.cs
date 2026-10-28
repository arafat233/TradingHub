using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.App.Services;

/// <summary>Platform folders.</summary>
public static class AppPaths
{
    /// <summary>App data. TRADINGHUB_DATA_DIR changes it (the test tools use a separate folder).</summary>
    public static string Root { get; } = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } dir
        ? dir
        : TradingHub.Common.Product.AppData;
    public static string Ticks => Path.Combine(Root, "Ticks");
    public static string WorkspaceFile => Path.Combine(Root, "workspace.json");
    public static string Drawings => Path.Combine(Root, "Drawings");
    public static string Backtests => Path.Combine(Root, "Backtests");
    public static string Accounts => Path.Combine(Root, "Accounts");

    /// <summary>
    /// Folder visible for the user's own indicators (.cs). TRADINGHUB_INDICATORS_DIR changes it (tests); with
    /// TRADINGHUB_DATA_DIR and without it, one inside that folder: a test never writes in the real folder.
    /// </summary>
    public static string UserIndicators { get; } = TestFolder("TRADINGHUB_INDICATORS_DIR", "Indicators") ?? Translated(TradingHub.Common.Product.Documents, "Indicators", "Indicadores");

    /// <summary>Folder visible for the strategies (.cs). TRADINGHUB_STRATEGIES_DIR changes it (tests), same as the indicators.</summary>
    public static string Strategies { get; } = TestFolder("TRADINGHUB_STRATEGIES_DIR", "Strategies") ?? Translated(TradingHub.Common.Product.Documents, "Strategies", "Estrategias");

    private static string? TestFolder(string variable, string name) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dir ? dir
        : Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } data ? Path.Combine(data, name)
        : null;

    /// <summary>
    /// These folders kept their Spanish names when the codebase was translated, so the docs sent people to
    /// «Indicators» while the app only ever read «Indicadores»: a file dropped there did nothing and said nothing.
    /// The English name is the one from now on, and a folder still under the old name is renamed once so that
    /// nothing the user wrote is lost. If both exist the old one is left alone (a rename would have to merge) and
    /// only the new one is read, which is also what someone who just created it by hand expects.
    /// </summary>
    private static string Translated(string root, string name, string legacy)
    {
        string path = Path.Combine(root, name), old = Path.Combine(root, legacy);
        try
        {
            if (!Directory.Exists(path) && Directory.Exists(old)) Directory.Move(old, path);
        }
        catch (Exception)
        {
            return Directory.Exists(old) ? old : path;   // locked or read-only: keep using the old one
        }

        return path;
    }

    /// <summary>Lab reports (Markdown), inside the strategies folder.</summary>
    public static string Reports { get; } = Translated(Strategies, "Reports", "Reportes");
}

/// <summary>An indicator available to add.</summary>
public sealed record IndicatorInfo(Type Type, string Name, string Category, string Source, string Description);

/// <summary>Separate window for a right-panel section (Chart Trader, DOM or tape).</summary>
public sealed class PanelWindowState
{
    public bool Floating { get; set; }
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 720;
}

/// <summary>Indicator catalog: Trading Hub's own, extra DLLs and the user's scripts.</summary>
public sealed class IndicatorCatalog
{
    private readonly List<IndicatorInfo> _items = new();
    private readonly HashSet<Assembly> _scanned = new();

    public IReadOnlyList<IndicatorInfo> Items => _items;
    public event Action? Changed;

    public void Scan(Assembly asm, string source)
    {
        if (!_scanned.Add(asm)) return;
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray()!; }

        foreach (var t in types)
        {
            if (t.IsAbstract || !typeof(Indicator).IsAssignableFrom(t) || t.GetConstructor(Type.EmptyTypes) == null) continue;
            if (t.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false }) continue;
            string name = Indicator.DefaultName(t);
            string cat = t.GetCustomAttribute<CategoryAttribute>()?.Category ?? IndicatorCategories.Other;
            string desc = "";
            try { desc = t.GetCustomAttribute<System.ComponentModel.DataAnnotations.DisplayAttribute>()?.GetDescription() ?? t.GetCustomAttribute<DescriptionAttribute>()?.Description ?? ""; }
            catch { /* localization resources missing */ }
            _items.RemoveAll(i => i.Type.FullName == t.FullName && i.Source == source);
            _items.Add(new IndicatorInfo(t, name, cat, source, desc));
        }

        _items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Changed?.Invoke();
    }

    public void RemoveSource(string source)
    {
        _items.RemoveAll(i => i.Source == source);
        _scanned.RemoveWhere(a => a.IsCollectible);
        Changed?.Invoke();
    }

    public void ScanFolder(string folder, string source)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var dll in Directory.EnumerateFiles(folder, "*.dll"))
        {
            try { Scan(Assembly.LoadFrom(dll), source); }
            catch (Exception ex) { this.LogWarn($"Could not load {Path.GetFileName(dll)}: {ex.Message}"); }
        }
    }

    public IndicatorInfo? Find(string typeName) => _items.FirstOrDefault(i => i.Type.FullName == typeName) ?? _items.FirstOrDefault(i => i.Type.Name == typeName);

    public Indicator? Create(string typeName)
    {
        var info = Find(typeName);
        return info == null ? null : (Indicator?)Activator.CreateInstance(info.Type);
    }
}

/// <summary>General preferences.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(AppPaths.Root, "settings.json");

    public string? TimeZoneId { get; set; }
    public bool ConfirmRealOrders { get; set; } = true;

    /// <summary>Favorite strategies (by file name): they go at the very top of the Backtesting list.</summary>
    public List<string> FavoriteStrategies { get; set; } = new();

    /// <summary>Platform appearance: "dark" (black) or "light" (white).</summary>
    public string ThemeMode { get; set; } = "dark";

    /// <summary>Chart template new tabs start with (null = they copy the active chart).</summary>
    public string? DefaultChartTemplate { get; set; }

    /// <summary>Template each new indicator starts with (indicator type -> template name).</summary>
    public Dictionary<string, string> DefaultIndicatorTemplates { get; set; } = new();

    /// <summary>Balance the simulated account starts with (it can be changed in the Account section).</summary>
    public decimal SimStartBalance { get; set; } = 10_000m;
    public bool SoundAlerts { get; set; } = true;

    public bool RecordLiveData { get; set; } = true;

    /// <summary>Instruments opened at startup with no chart, only to record them ("feedId|symbol").</summary>
    public List<string> AutoRecord { get; set; } = new();

    /// <summary>Also record the order book (about 20-40 MB per day per instrument) for heatmap replay and backtests on the book.</summary>
    public bool RecordDepth { get; set; } = true;

    /// <summary>The daily recap also gets a short comment written by Claude (it uses the user's Claude Code session).</summary>
    public bool RecapComment { get; set; }

    /// <summary>Send indicator alerts to the user's own Telegram bot (needs the token in the credential store and a chat id). Off by default.</summary>
    public bool TelegramAlerts { get; set; }

    public string TelegramChatId { get; set; } = "";

    /// <summary>Every indicator alert also saves a picture of the chart into the journal.</summary>
    public bool AlertSnapshots { get; set; }

    /// <summary>Alerts are also read out loud with the Windows voice.</summary>
    public bool SpokenAlerts { get; set; }

    /// <summary>A briefing (events, levels near price, scoreboard) is made at the times below. Off by default.</summary>
    public bool BriefingsOn { get; set; }

    /// <summary>Local times of the briefings, "HH:mm" separated by commas.</summary>
    public string BriefingTimes { get; set; } = "08:00,13:00";

    /// <summary>Mine the day's levels and rerun the research every night (the app has to be open). Off by default.</summary>
    public bool ResearchNightly { get; set; }

    /// <summary>Local time of the nightly research, "HH:mm".</summary>
    public string ResearchTime { get; set; } = "03:00";

    /// <summary>Money the risk calculator is allowed to lose on one trade.</summary>
    public decimal RiskPerTrade { get; set; } = 100m;

    /// <summary>Commission for a round trip of one contract, in money. The research tools turn it into ticks to count it as a cost.</summary>
    public decimal CommissionPerRoundTrip { get; set; } = 4m;

    /// <summary>The Telegram bot also answers read-only commands from the user's own chat (needs phone alerts on). Off by default.</summary>
    public bool TelegramCommands { get; set; }


    /// <summary>No-sleep mode: the Mac does not go to sleep while there are live strategies or agents working.</summary>
    public bool KeepAwake { get; set; } = true;

    /// <summary>Keep going with the lid closed (it asks for your administrator password) and, if you want, only on the charger.</summary>
    public bool KeepAwakeLidClosed { get; set; }
    public bool LidClosedOnlyOnPower { get; set; } = true;
    public double WindowWidth { get; set; } = 1600;
    public double WindowHeight { get; set; } = 960;
    public List<string> Favorites { get; set; } = new() { "binance-futures:BTCUSDT", "binance-futures:ETHUSDT", "sim:SIM-BTC" };

    /// <summary>Favorite periods: the ones visible on the chart's toolbar (the rest, in its picker).</summary>
    public List<string> FavoritePeriods { get; set; } = new() { "M1", "M5", "M15", "H1", "H4", "Daily" };

    // Trading's right panel
    public bool ShowSidePanel { get; set; } = true;
    public bool ShowTicket { get; set; } = true;
    public bool ShowDom { get; set; } = true;
    public bool ShowTape { get; set; } = true;
    public bool ShowInsights { get; set; } = true;
    public bool WatchRelated { get; set; }

    /// <summary>The price levels the user marked (Insights panel), by instrument key.</summary>
    public Dictionary<string, List<decimal>> MyLevels { get; set; } = new();

    // alerts from the Insights panel (see InsightAlertEngine): they go out through the same path as every alert (log, Telegram, speech, sound)
    public bool AlertToxicity { get; set; } = true;
    public bool AlertWalls { get; set; } = true;
    public decimal AlertWallUsd { get; set; } = 1_000_000;
    public bool AlertOptions { get; set; } = true;
    public bool AlertLiquidations { get; set; } = true;
    public decimal AlertLiquidationUsd { get; set; } = 1_000_000;
    public bool AlertSetup { get; set; }

    /// <summary>Round-trip cost in basis points that the Insights decision must beat (0 = automatic: 8 for crypto, 2 for the rest).</summary>
    public decimal InsightsCostBps { get; set; }
    public double SidePanelWidth { get; set; } = 340;

    /// <summary>The per-chart Chart Trader has already been shown (the first time the general ticket is hidden).</summary>
    public bool ChartTraderIntroduced { get; set; }

    /// <summary>Relative height of each right-panel section (ticket, dom, tape) when they are together.</summary>
    public Dictionary<string, double> SideWeights { get; set; } = new();

    /// <summary>Chart Trader, DOM or tape in a separate window: if it was out, where and at what size.</summary>
    public Dictionary<string, PanelWindowState> PanelWindows { get; set; } = new();

    /// <summary>Closing the window leaves Trading Hub running in the menu bar (with its panel); Cmd+Q closes it for good.</summary>
    public bool KeepInMenuBar { get; set; } = true;

    /// <summary>
    /// Smart contract: if a strategy's size asks for fractions of a mini (1 NQ x 0.5), it executes on the micro with the
    /// same exposure (5 MNQ). With whole minis, on the mini.
    /// </summary>
    public bool SmartContracts { get; set; } = true;

    /// <summary>On opening Trading Hub, the live strategies that were running when it closed start again on their own.</summary>
    public bool AutoResumeLive { get; set; } = true;

    /// <summary>…the ones on real accounts too (otherwise those wait for you to resume them).</summary>
    public bool AutoResumeLiveReal { get; set; }

    /// <summary>Execution buttons below the DOM (market, limits, cancel, close…).</summary>
    public bool ShowDomButtons { get; set; } = true;

    /// <summary>DOM in a separate window (older version: it now goes in <see cref="PanelWindows"/>).</summary>
    public bool DomFloating { get; set; }
    public int DomWindowX { get; set; } = -1;
    public int DomWindowY { get; set; } = -1;
    public double DomWindowWidth { get; set; } = 360;
    public double DomWindowHeight { get; set; } = 720;

    /// <summary>Claude chat in a separate window: position and size.</summary>
    public int ClaudeWindowX { get; set; } = -1;
    public int ClaudeWindowY { get; set; } = -1;
    public double ClaudeWindowWidth { get; set; } = 460;
    public double ClaudeWindowHeight { get; set; } = 760;

    /// <summary>Charts linked by color: what is synchronized inside each group.</summary>
    public bool LinkSymbol { get; set; } = true;
    public bool LinkPeriod { get; set; } = true;
    public bool LinkCrosshair { get; set; } = true;

    /// <summary>Default style of each kind of drawing (saved by "use as default").</summary>
    public Dictionary<string, TradingHub.Charting.Drawings.ChartDrawing> DrawingDefaults { get; set; } = new();

    /// <summary>Keyboard shortcuts: action -> shortcut ("" = unassigned; if missing, the factory one).</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>DOM settings (columns, sizes, colors).</summary>
    public TradingHub.Charting.Dom.DomSettings Dom { get; set; } = new();

    /// <summary>Liquidity heatmap settings (time on screen, contrast, what it shows).</summary>
    public TradingHub.Charting.Heatmap.HeatmapSettings Heatmap { get; set; } = new();

    /// <summary>Automatic stop and targets per instrument (instrument key).</summary>
    public Dictionary<string, BracketTemplate> Brackets { get; set; } = new();

    public BracketTemplate BracketTemplateFor(string instrumentKey)
    {
        if (!Brackets.TryGetValue(instrumentKey, out var t)) Brackets[instrumentKey] = t = new BracketTemplate();
        return t;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new TradingHub.Charting.Dom.ColorJsonConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
                s.Dom ??= new TradingHub.Charting.Dom.DomSettings();
                s.Dom.Normalize();
                s.Brackets ??= new();
                s.Hotkeys ??= new();
                s.DrawingDefaults ??= new();
                return s;
            }
        }
        catch
        {
            // corrupt file: default values
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}

/// <summary>Account available to trade an instrument.</summary>
public sealed record AccountOption(string Id, string Name, bool IsReal);

/// <summary>Shared application services.</summary>
public sealed class AppServices
{
    private readonly Dictionary<string, SimBroker> _simBrokers = new();
    private readonly Dictionary<string, ITradingAccount> _realBrokers = new();
    private readonly Dictionary<string, TradingHub.Feeds.Rithmic.RithmicTradingSession> _rithmicSessions = new();
    private readonly Dictionary<string, TradingHub.Feeds.Tradovate.TradovateTradingSession> _tradovateSessions = new();

    /// <summary>Tradovate accounts of a connection (it syncs orders, positions and balances in the background).</summary>
    public TradingHub.Feeds.Tradovate.TradovateTradingSession TradovateSessionFor(ConnectionProfile p)
    {
        if (_tradovateSessions.TryGetValue(p.Id, out var s)) return s;
        var feed = (TradingHub.Feeds.Tradovate.TradovateFeed)Connections.GetFeed(p.Id);
        s = new TradingHub.Feeds.Tradovate.TradovateTradingSession(feed.Connection);
        _tradovateSessions[p.Id] = s;
        // every fill is recorded in the account's history (balance, trades and statistics)
        s.FillReceived += f => Journal.Add(TradovateAccountId(p, f.AccountId),
            new AccountFill("tv-" + f.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), f.Time, f.Symbol, f.Buy, f.Quantity, f.Price,
                AccountCenter.PointValueOf(f.Symbol), 0, f.OrderId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        s.Start();
        return s;
    }

    /// <summary>Id of a Tradovate account in Trading Hub: tradovate|connection|account.</summary>
    public static string TradovateAccountId(ConnectionProfile p, long account) => $"tradovate|{p.Id}|{account}";

    /// <summary>CME future (useful for trading it with a Tradovate account even if the chart belongs to another connection).</summary>
    private static bool IsCmeFuture(Instrument inst) =>
        (inst.FeedId is TradingHub.Feeds.Rithmic.RithmicFeed.FeedId or TradingHub.Feeds.Tradovate.TradovateFeed.FeedId) && TradingHub.Feeds.Rithmic.RithmicFeed.SpecFor(inst.Symbol.Split(' ', ':')[0]) != null;

    /// <summary>Rithmic order session of a connection (it connects in the background the first time).</summary>
    public TradingHub.Feeds.Rithmic.RithmicTradingSession RithmicSessionFor(ConnectionProfile p)
    {
        if (_rithmicSessions.TryGetValue(p.Id, out var s)) return s;
        s = new TradingHub.Feeds.Rithmic.RithmicTradingSession(Connections.RithmicSettingsOf(p));
        _rithmicSessions[p.Id] = s;
        // every fill is recorded in the account's history (balance, trades and statistics)
        s.FillReceived += f => Journal.Add(RithmicAccountId(p, f.Account),
            new AccountFill(f.Id, f.Time, f.Symbol, f.IsBuy, f.Quantity, f.Price, AccountCenter.PointValueOf(f.Symbol), 0, f.BasketId));
        s.Start();
        return s;
    }

    /// <summary>State of the Rithmic order session for an instrument (null if it is not a Rithmic one).</summary>
    public string? RithmicStatusFor(InstrumentData data)
    {
        var p = Connections.Enabled.FirstOrDefault(x => x.IsRithmic && x.MarketDataId == data.Instrument.FeedId && !string.IsNullOrWhiteSpace(x.RithmicUser));
        if (p == null) return null;
        var s = RithmicSessionFor(p);
        return s.IsConnected ? null : "Rithmic: " + s.StatusText;
    }

    /// <summary>Id of a Rithmic account in Trading Hub: rithmic|connection|account.</summary>
    public static string RithmicAccountId(ConnectionProfile p, string account) => $"rithmic|{p.Id}|{account}";

    /// <summary>Big market orders on every open instrument (the Big orders window shows them).</summary>
    public BigOrderFeed BigOrders { get; } = new();

    /// <summary>How late live ticks arrive, per open instrument.</summary>
    public LatencyMonitor Latency { get; } = new();

    public AppServices()
    {
        Portfolio.BeginBalance = Portfolio.Balance = Settings.SimStartBalance;
        Markets = new MarketConditions(this);
        // every strategy that starts live builds its market context with the last month (not just with the warm-up)
        TradingHub.Strategies.LiveStrategyRunner.Started += OnRunnerStarted;
        // the Rithmic order session follows the new configuration without opening another one (a second would cut the first)
        Connections.Reconfigured += p =>
        {
            if (p.IsRithmic && _rithmicSessions.TryGetValue(p.Id, out var session)) session.Reconfigure(Connections.RithmicSettingsOf(p));
            // Tradovate: the order session uses the feed's connection, which is rebuilt with the new data
            if (p.Kind == ConnectionKind.Tradovate)
            {
                if (_tradovateSessions.Remove(p.Id, out var tv)) tv.Dispose();
                foreach (var key in _realBrokers.Keys.Where(k => k.StartsWith($"tradovate|{p.Id}|", StringComparison.Ordinal)).ToList())
                {
                    (_realBrokers[key] as IDisposable)?.Dispose();
                    _realBrokers.Remove(key);
                }
            }
        };
        Directory.CreateDirectory(AppPaths.Root);
        Directory.CreateDirectory(AppPaths.UserIndicators);
        Directory.CreateDirectory(AppPaths.Strategies);
        Hub.RecordingRoot = AppPaths.Ticks;
        KlineHistory.CacheRoot = Path.Combine(AppPaths.Root, "Klines");
        // TRADINGHUB_LIVE_DIR: alternative portfolio folder (tests)
        TradingHub.Strategies.LiveStore.Root = Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR") is { Length: > 0 } live ? live : Path.Combine(AppPaths.Root, "Live");
        // the ones running when Trading Hub closed (the periodic save marks them stopped right away): to resume them on opening
        RunningAtLastClose = TradingHub.Strategies.LiveStore.LoadRunners().Where(i => i.Running && !LiveEntry.IsManaged(i.Source)).ToList();
        Hub.RecordLive = Settings.RecordLiveData;
        Hub.RecordDepth = Settings.RecordDepth;
        StatsProvider.SetSource(Statistics);
        Catalog.Scan(typeof(TradingHub.Indicators.Native.BigTrades).Assembly, "Trading Hub");
        // the technical indicators, if they ship in this version (the open source one does not include them)
        try
        {
            Catalog.Scan(System.Reflection.Assembly.Load("TradingHub.Indicators.Technical"), "Technical");
        }
        catch (Exception ex)
        {
            this.LogInfo($"No extra technical indicators: {ex.Message}");
        }
        Catalog.ScanFolder(Path.Combine(AppContext.BaseDirectory, "Indicators"), "Extra");
        Catalog.ScanFolder(Path.Combine(AppPaths.Root, "Indicators"), "Extra");
    }

    public AppSettings Settings { get; } = AppSettings.Load();
    public ConnectionManager Connections { get; } = new();
    public MarketDataHub Hub { get; } = new();

    /// <summary>Condition of each market (range or trend and volatility), the same measure the context filters use.</summary>
    internal MarketConditions Markets { get; }

    private void OnRunnerStarted(TradingHub.Strategies.LiveStrategyRunner runner) => _ = Markets.SeedAsync(runner);
    public Portfolio Portfolio { get; } = new() { AccountID = "SIM-1", Name = "Simulated account", BeginBalance = 10_000m, Balance = 10_000m };

    /// <summary>Stored fills of each account (they survive closing Trading Hub).</summary>
    public AccountJournal Journal { get; } = new(AppPaths.Accounts);

    private AccountCenter? _accountCenter;
    public AccountCenter AccountCenter => _accountCenter ??= new AccountCenter(this);
    public TradingStatistics Statistics { get; } = new();
    public TradingStatisticsProvider StatsProvider { get; } = new();
    public IndicatorCatalog Catalog { get; } = new();
    public UserScripts? Scripts { get; set; }

    private StrategyCatalog? _strategies;

    /// <summary>The user's strategies (created when entering the section).</summary>
    public StrategyCatalog Strategies => _strategies ??= new StrategyCatalog();

    /// <summary>Strategies running live.</summary>
    public List<TradingHub.Strategies.LiveStrategyRunner> Runners { get; } = new();


    /// <summary>No-sleep mode (power assertion and, if you enable it, keep going with the lid closed).</summary>
    internal SleepGuard Sleep { get; } = new();

    /// <summary>Why the Mac has to stay awake right now (or null if nothing is running).</summary>
    internal string? AwakeReason()
    {
        int live = Runners.Count(r => r.IsRunning);
        bool agents = _live?.RiskAgentBusy ?? false;
        bool waiting = false;
        if (live == 0 && !agents && !waiting) return null;
        var parts = new List<string>();
        if (live > 0) parts.Add($"{live} live strategy(ies)");
        if (agents) parts.Add("agents working");
        return string.Join(" y ", parts);
    }

    private LiveCenter? _live;

    /// <summary>Every live strategy in a single place (panel and charts), their portfolios and the risk agent.</summary>
    internal LiveCenter Live => _live ??= new LiveCenter(this);

    /// <summary>Stores the live portfolio's state (Claude's lab reads it). It only writes if something changed.</summary>
    /// <summary>Live strategies (from the panel and from the charts) that were running when Trading Hub closed.</summary>
    internal List<TradingHub.Strategies.LiveRunnerInfo> RunningAtLastClose { get; set; } = new();

    private bool _closingDown;

    public void SaveLiveState()
    {
        // on closing it is saved once, with whatever was running: afterwards the strategies are stopped and the periodic
        // save (which goes on for a few moments) would mark them stopped, and on opening they would not resume on their own
        if (_closingDown) return;
        var infos = Runners.Where(r => r.Persist).Select(r => r.Info()).ToList();
        TradingHub.Strategies.LiveStore.SaveRunners(infos.GroupBy(i => i.Key).Select(g => g.OrderByDescending(i => i.Running).First()));
    }

    /// <summary>Applies an approved size: to the strategy if it is running and to what is stored for next time.</summary>
    public void ApplySize(string key, decimal size) => Live.ApplySize(key, size);

    /// <summary>At least the whole current day (since midnight in the chosen time zone), so a chart opened at 3 pm has the morning too.</summary>
    public TimeSpan HistoryFor(string profileId)
    {
        var configured = TimeSpan.FromHours(Math.Clamp(Connections.Find(profileId)?.HistoryHours ?? 1.5, 0.1, 24 * 60));
        try
        {
            var tz = Settings.TimeZoneId is { Length: > 0 } id ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.Local;
            var sinceMidnight = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz).TimeOfDay + TimeSpan.FromMinutes(5);
            return configured > sinceMidnight ? configured : sinceMidnight;
        }
        catch (Exception)
        {
            return configured;
        }
    }

    /// <summary>Opens (or reuses) the data of an instrument of a connection.</summary>
    public InstrumentData Open(string profileId, Instrument instrument) =>
        Hub.GetOrStart(Connections.GetFeed(profileId), instrument, HistoryFor(profileId));

    /// <summary>Opens every instrument of <see cref="AppSettings.AutoRecord"/> (a few seconds after start: the connections need to be up). The recorder runs while the data is open.</summary>
    public async Task StartBackgroundRecordingAsync()
    {
        foreach (var key in Settings.AutoRecord.ToList()) await OpenForRecordingAsync(key);
    }

    /// <summary>Opens one "feedId|symbol" entry; returns what went wrong, or null.</summary>
    public async Task<string?> OpenForRecordingAsync(string key)
    {
        var parts = key.Split('|', 2);
        if (parts.Length != 2) return "bad entry " + key;
        string? error = null;
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                var profile = Connections.Find(parts[0]);
                if (profile is not { Enabled: true }) return $"the connection {parts[0]} is not enabled";
                var instruments = await Controls.InstrumentPicker.InstrumentsAsync(this, profile.Id);
                var inst = instruments.FirstOrDefault(i => i.Symbol.Equals(parts[1], StringComparison.OrdinalIgnoreCase));
                if (inst == null) return $"{parts[1]} is not in {profile.Name}";
                Open(profile.Id, inst);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
            }
        }

        return error;
    }

    /// <summary>Opens a replay of stored data (nothing is recorded or saved in the workspace).</summary>
    public InstrumentData OpenReplay(Instrument source, DateTime startUtc, DateTime endUtc, TimeSpan preload)
    {
        var feed = new ReplayFeed(AppPaths.Ticks, source, startUtc, endUtc);
        return Hub.GetOrStart(feed, feed.Instrument, preload);
    }

    public SimBroker SimBrokerFor(InstrumentData data)
    {
        if (!_simBrokers.TryGetValue(data.Instrument.Key, out var b))
        {
            b = new SimBroker(data, Portfolio, Statistics);
            _simBrokers[data.Instrument.Key] = b;
            var inst = data.Instrument;
            b.NewMyTrade += t => Journal.Add(AccountCenter.SimId, new AccountFill(Guid.NewGuid().ToString("N"), t.Time, inst.Symbol,
                t.OrderDirection == OrderDirections.Buy, t.Volume, t.Price, inst.PointValue > 0 ? inst.PointValue : 1, t.Commission, t.Order?.Id.ToString() ?? ""));
        }

        return b;
    }

    public IEnumerable<SimBroker> SimBrokers => _simBrokers.Values;

    /// <summary>Every account opened in this session (simulated and real): to know whether anything is still open before restarting.</summary>
    internal IEnumerable<ITradingAccount> OpenAccounts() => _simBrokers.Values.Cast<ITradingAccount>().Concat(_realBrokers.Values);

    private readonly Dictionary<ITradingAccount, BracketManager> _brackets = new(ReferenceEqualityComparer.Instance);

    /// <summary>Stop/targets of an account (one per account and instrument), with the instrument's template.</summary>
    public BracketManager BracketsFor(ITradingAccount account)
    {
        if (!_brackets.TryGetValue(account, out var m))
        {
            m = new BracketManager(account);
            _brackets[account] = m;
            BracketsCreated?.Invoke(m);
        }

        m.Template = Settings.BracketTemplateFor(account.Instrument.Key);
        return m;
    }

    /// <summary>An account's stop/target handler was created (to listen to its notices).</summary>
    public event Action<BracketManager>? BracketsCreated;

    /// <summary>Accounts this instrument can be traded with.</summary>
    /// <summary>The running trade copier, if any (one at a time).</summary>
    public TradingHub.Trading.TradeCopier? Copier { get; set; }

    public IEnumerable<AccountOption> AccountsFor(InstrumentData data)
    {
        yield return new AccountOption("sim", "Simulated account", false);
        foreach (var p in Connections.Enabled.Where(p => p.SupportsTrading && p.MarketDataId == data.Instrument.FeedId && !string.IsNullOrWhiteSpace(p.ApiKey)))
        {
            if (p.Testnet) yield return new AccountOption(p.Id, $"{p.Name} (testnet)", false);
            else if (p.AllowRealOrders) yield return new AccountOption(p.Id, $"{p.Name} — REAL", true);
        }

        // Tradovate accounts: they trade any CME future, from a Rithmic chart too
        if (IsCmeFuture(data.Instrument))
            foreach (var p in Connections.Enabled.Where(p => p.Kind == ConnectionKind.Tradovate && !string.IsNullOrWhiteSpace(p.TradovateUser)))
            {
                var session = TradovateSessionFor(p);
                foreach (var a in session.Accounts)
                {
                    if (session.IsDemo) yield return new AccountOption(TradovateAccountId(p, a.Id), $"{a.Name} · Tradovate (demo)", false);
                    else if (p.AllowRealOrders) yield return new AccountOption(TradovateAccountId(p, a.Id), $"{a.Name} · Tradovate — REAL", true);
                }
            }

        // Rithmic accounts (they show up when the order session finishes connecting)
        foreach (var p in Connections.Enabled.Where(p => p.IsRithmic && p.MarketDataId == data.Instrument.FeedId && !string.IsNullOrWhiteSpace(p.RithmicUser)))
        {
            var session = RithmicSessionFor(p);
            foreach (var a in session.Accounts)
            {
                if (session.IsPractice) yield return new AccountOption(RithmicAccountId(p, a.AccountId), $"{a.AccountId} · Rithmic (practice)", false);
                else if (p.AllowRealOrders) yield return new AccountOption(RithmicAccountId(p, a.AccountId), $"{a.AccountId} · Rithmic — REAL", true);
            }
        }
    }

    public ITradingAccount AccountFor(InstrumentData data, string accountId)
    {
        if (accountId == "sim") return SimBrokerFor(data);
        string key = accountId + "|" + data.Instrument.Key;
        if (_realBrokers.TryGetValue(key, out var b)) return b;
        if (accountId.StartsWith("tradovate|", StringComparison.Ordinal))
        {
            var parts = accountId.Split('|');
            var tp = Connections.Find(parts[1]) ?? throw new KeyNotFoundException(accountId);
            var session = TradovateSessionFor(tp);
            long id = long.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
            var acc = session.Accounts.FirstOrDefault(a => a.Id == id) ?? new TradingHub.Feeds.Tradovate.TradovateAccount(id, parts[2], true);
            var tb = new TradingHub.Feeds.Tradovate.TradovateBroker(data, session, acc, $"{acc.Name} · Tradovate{(session.IsDemo ? " (demo)" : " — REAL")}");
            _realBrokers[key] = tb;
            return tb;
        }

        if (accountId.StartsWith("rithmic|", StringComparison.Ordinal))
        {
            var parts = accountId.Split('|');
            var rp = Connections.Find(parts[1]) ?? throw new KeyNotFoundException(accountId);
            var session = RithmicSessionFor(rp);
            var feed = (TradingHub.Feeds.Rithmic.RithmicFeed)Connections.GetFeed(rp.Id);
            var rb = new TradingHub.Feeds.Rithmic.RithmicBroker(data, session, parts[2], $"{parts[2]} · Rithmic{(session.IsPractice ? " (practice)" : " — REAL")}",
                ct => feed.ContractAsync(data.Instrument, ct));
            _realBrokers[key] = rb;
            return rb;
        }

        var p = Connections.Find(accountId) ?? throw new KeyNotFoundException(accountId);
        b = new BinanceBroker(data, Connections.BinanceSettingsOf(p), p.Testnet ? $"{p.Name} (testnet)" : $"{p.Name} — REAL");
        _realBrokers[key] = b;
        return b;
    }

    public void Shutdown()
    {
        try
        {
            SaveLiveState();
            _closingDown = true;
            TradingHub.Strategies.LiveStrategyRunner.Started -= OnRunnerStarted;
            Sleep.Dispose();
            foreach (var r in Runners) r.Stop(flatten: false);
            foreach (var b in _realBrokers.Values) (b as IDisposable)?.Dispose();
            foreach (var rs in _rithmicSessions.Values) rs.Dispose();
            foreach (var ts in _tradovateSessions.Values) ts.Dispose();
            Hub.Dispose();
            Connections.DisposeAll();
        }
        catch (Exception ex)
        {
            this.LogError("Error while closing", ex);
        }
    }
}
