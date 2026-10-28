using System.Globalization;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Strategies;

/// <summary>
/// Hard risk limiter (no AI decides it): checks every order that opens or increases a position before
/// sending it. The ones that reduce the position (exits, stops, closes) always go through.
/// </summary>
public interface ILiveOrderGuard
{
    /// <summary>null = it is sent; otherwise, the reason it is blocked.</summary>
    string? Check(LiveStrategyRunner runner, OrderSide side, decimal quantity, decimal positionAfter);
}

/// <summary>
/// Runs a strategy on live data and sends its orders to an account (simulated or real).
/// Before starting it "warms up" the indicators with the loaded history, without trading. Automatic stops
/// and targets are placed as real orders on the account (emulated OCO).
/// <para>
/// The strategy sees its position in its own units (without the portfolio size): each order sends the difference
/// between the real position that matches the current size and the one it holds, so an exit closes exactly what is open
/// even if the size changed in between. It keeps its own bookkeeping (even if other strategies or you trade the
/// same instrument on the same account) and respects the pause and the risk limits (daily loss and drawdown).
/// </para>
/// Everything runs on the UI thread (the <see cref="InstrumentData.Pump"/> one).
/// </summary>
public sealed class LiveStrategyRunner : IStrategyHost, IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HashSet<long> CanceledOrders = new();
    private readonly Strategy _strategy;
    private readonly InstrumentData _data;
    private IcebergDetector? _icebergs;
    private readonly ITradingAccount _account;
    private readonly ChartPeriod _period;
    private readonly SessionSettings _session;
    private readonly TimeZoneInfo _tz;
    private readonly BarSeries _bars = new();
    private readonly List<StrategyOrder> _orders = new();
    private readonly List<string> _log = new();
    private readonly SynchronizationContext? _sync;
    private BarBuilder _builder = null!;
    private bool _warmup;
    private long _nextId = 1, _lastId = long.MinValue;
    private long _timeTicks, _priceTicks;
    private decimal _lastPos;
    private StrategyOrder? _sl, _tp;
    private decimal _highWater, _lowWater;
    private DateTime _lastTrailUpdate;
    private DateOnly _sessionDay;

    // the strategy's position in its own units (without the portfolio size)
    private decimal _virtual;

    // stopped with "close position": it keeps listening to the account until the close fills
    private bool _closing;
    private bool _accountHooked;
    private int _placing;

    // started with a position in its bookkeeping and is waiting for the account to confirm it
    private bool _awaitingAccount;
    private DateTime _awaitUntil;
    private bool _awaitLogged;

    // risk limits
    private int _tradesSeen = -1;
    private DateOnly _totalsDay;
    private decimal _dayPnl, _peakClosed;
    private DateOnly _limitDay;

    public LiveStrategyRunner(Strategy strategy, InstrumentData data, ITradingAccount account, ChartPeriod period, string? timeZoneId = null, TimeSpan sessionStart = default)
    {
        _strategy = strategy;
        _data = data;
        _account = account;
        _period = period;
        _tz = timeZoneId != null && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var z) ? z : TimeZoneInfo.Utc;
        _session = new SessionSettings { TimeZone = _tz, Start = sessionStart };
        _sync = SynchronizationContext.Current;
    }

    public string StrategyName => Strategy.DisplayName(_strategy.GetType());

    /// <summary>Stable identifier in the portfolio: strategy, instrument and account.</summary>
    public string Key => $"{_strategy.GetType().FullName}|{_data.Instrument.FeedId}:{_data.Instrument.Symbol}|{_account.Name}";

    /// <summary>Size multiplier (1 = exactly what the strategy says). It applies on the next order.</summary>
    public decimal SizeMultiplier { get; set; } = 1;

    /// <summary>
    /// Contract it executes on: the strategy's own, or its micro when the size asks for fractions of the mini
    /// (the strategy keeps watching NQ and the orders go to MNQ). The account the runner receives is this contract's.
    /// </summary>
    public Instrument ExecInstrument
    {
        get => _exec ?? _data.Instrument;
        set => _exec = value;
    }

    private Instrument? _exec;

    /// <summary>Units of the execution contract per unit of the strategy (1 NQ = 10 MNQ).</summary>
    public decimal ExecRatio { get; set; } = 1;

    /// <summary>Has live orders on the account (for example a limit entry waiting).</summary>
    public bool HasWorkingOrders => _orders.Any(o => o.IsActive);

    /// <summary>Executes on a different contract than the one it watches.</summary>
    public bool CrossExecution => !string.Equals(ExecInstrument.Symbol, _data.Instrument.Symbol, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where it was launched from: panel (Live), chart (a chart) or another origin.</summary>
    public string Source { get; set; } = "panel";

    /// <summary>Account and connection it trades with (to relaunch it the same way).</summary>
    public string AccountId { get; set; } = "";
    public string ProfileId { get; set; } = "";

    /// <summary>Risk limits: maximum daily loss and maximum drawdown (in money; 0 = no limit).</summary>
    public decimal MaxDailyLoss { get; set; }
    public decimal MaxDrawdown { get; set; }

    /// <summary>On hitting a limit, besides stopping, it closes the position.</summary>
    public bool CloseOnLimit { get; set; } = true;

    /// <summary>Live portfolio it belongs to (null = none).</summary>
    public string? PortfolioId { get; set; }

    /// <summary>Why it stopped or was halted (risk limit, error…).</summary>
    public string? StopReason { get; set; }

    /// <summary>Stores its fills in the portfolio (a chart's visual ones do not).</summary>
    public bool Persist { get; set; } = true;

    /// <summary>When starting with an inherited position the account confirms, it puts the stop and target back on it.</summary>
    public bool ProtectOnStart { get; set; }

    /// <summary>Paused: it does not open or increase positions; if it has one open, it keeps managing it until it closes.</summary>
    public bool Paused { get; private set; }

    /// <summary>Hit the maximum daily loss: it opens nothing more until the next session.</summary>
    public bool DailyLimitActive => _limitDay != default && _limitDay == _sessionDay;

    /// <summary>Stopped and waiting for its position close to fill.</summary>
    public bool IsClosing => _closing;

    /// <summary>This strategy's own bookkeeping (fills, position, result and trades).</summary>
    public LiveLedger Ledger { get; private set; } = null!;

    /// <summary>Real position of the strategy (on the account, with the size applied).</summary>
    public decimal OwnPosition => Ledger?.Position ?? 0;

    /// <summary>Position in the strategy's units (without the size).</summary>
    public decimal VirtualPosition => _virtual;

    /// <summary>Hit a risk limit (message to warn you).</summary>
    public event Action<LiveStrategyRunner, string>? LimitHit;

    /// <summary>Hard risk limiter (for example one shared by several strategies).</summary>
    public ILiveOrderGuard? Guard { get; set; }

    /// <summary>The limiter blocked an order (with the reason).</summary>
    public event Action<LiveStrategyRunner, string>? GuardBlocked;

    /// <summary>An order filled (text to warn you: what, how many contracts and of which).</summary>
    public event Action<LiveStrategyRunner, string>? Executed;

    /// <summary>
    /// How many units of the execution contract an order of <paramref name="strategyQuantity"/> from the strategy sends
    /// (with the size, the micro if there is one and the minimum of 1). 0 if the size is zero.
    /// </summary>
    public decimal UnitsFor(decimal strategyQuantity) => Math.Abs(Target(Math.Abs(strategyQuantity)));

    /// <summary>Snapshot of the state for the portfolio and the lab.</summary>
    public LiveRunnerInfo Info() => new()
    {
        Key = Key,
        Strategy = StrategyName,
        StrategyId = _strategy.GetType().FullName ?? "",
        FeedId = _data.Instrument.FeedId,
        Symbol = _data.Instrument.Symbol,
        Period = _period.Code,
        Account = _account.Name,
        Running = IsRunning,
        StartedUtc = StartedUtc,
        Size = SizeMultiplier,
        PointValue = _data.Instrument.PointValue,
        ExecSymbol = CrossExecution ? ExecInstrument.Symbol : null,
        Position = Ledger?.Position ?? 0,
        OpenPnL = Ledger?.OpenPnL(_data.LastPrice) ?? 0,
        Realized = Ledger?.Realized ?? 0,
        Trades = Ledger?.Trades.Count ?? 0,
        Source = Source,
        AccountId = AccountId,
        ProfileId = ProfileId,
        Parameters = _strategy.GetParameters(),
        TimeZoneId = _tz == TimeZoneInfo.Utc ? null : _tz.Id,
        SessionStartHours = _session.Start.TotalHours,
        MaxDailyLoss = MaxDailyLoss,
        MaxDrawdown = MaxDrawdown,
        CloseOnLimit = CloseOnLimit,
        PortfolioId = PortfolioId,
        StopReason = StopReason,
        Paused = Paused,
        IsReal = _account.IsReal,
    };

    public Strategy StrategyInstance => _strategy;
    public InstrumentData Data => _data;
    public ITradingAccount Account => _account;
    public bool IsRunning { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public int Fills { get; private set; }
    public int WarmupBars { get; private set; }
    public IReadOnlyList<string> LogLines => _log;

    /// <summary>Since when it is really running (what came before was warm-up with the history, without trading).</summary>
    public DateTime LiveFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>Something changed (to refresh the UI).</summary>
    public event Action? Changed;

    /// <summary>
    /// Changes under which market conditions it opens trades, without restarting: it applies from the next order. Exits
    /// always go through, so whatever it has open it keeps managing.
    /// </summary>
    public void SetContextFilter(RegimeFilter filter, int minRank, int maxRank)
    {
        if (_sync != null && SynchronizationContext.Current != _sync)
        {
            _sync.Post(_ => SetContextFilter(filter, minRank, maxRank), null);
            return;
        }

        _strategy.RegimeFilter = filter;
        _strategy.MinVolatilityRank = Math.Clamp(minRank, 0, 100);
        _strategy.MaxVolatilityRank = Math.Clamp(Math.Max(maxRank, minRank), 0, 100);
        string what = Robustness.DescribeFilter(filter, _strategy.MinVolatilityRank, _strategy.MaxVolatilityRank);
        Log($"Market condition: {(what.Length > 0 ? "trades " + what : "trades in any condition")} · now {_strategy.Context.Describe()}");
        Changed?.Invoke();
    }

    /// <summary>A live strategy started (the app passes it the market's hourly history for its context).</summary>
    public static event Action<LiveStrategyRunner>? Started;

    /// <summary>Days of hourly history its market context started with (0 = warm-up only).</summary>
    public int ContextDays { get; private set; }

    /// <summary>
    /// Builds the market context (24-hour trend and last month's volatility percentile) with the hourly history
    /// the app passes it and afterwards with its own candles: a chart's warm-up usually brings few hours and without
    /// this the context filter would start with no data (and would allow entries in any context). It can be called while running.
    /// </summary>
    public void SeedContext(IReadOnlyList<(DateTime Time, decimal Close)> hourly)
    {
        if (_sync != null && SynchronizationContext.Current != _sync)
        {
            _sync.Post(_ => SeedContext(hourly), null);
            return;
        }

        if (hourly.Count == 0) return;
        bool coarse = _period.IsTimeBased && _period.ApproxDuration >= TimeSpan.FromHours(1);
        var tracker = new MarketContextTracker(coarse);
        // only what comes before its first own candle (from there on its own candles continue, as always)
        var firstHour = _bars.Count > 0 ? new DateTime(_bars.At(0).Time.Ticks - _bars.At(0).Time.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc) : DateTime.MaxValue;
        var before = hourly.Where(h => h.Time < firstHour).OrderBy(h => h.Time).ToList();
        if (before.Count == 0) return;
        tracker.AddHistory(before, _period.IsTimeBased ? _period.ApproxDuration : TimeSpan.FromHours(1));
        foreach (var b in _bars.All) tracker.Add(b.Time, b.Close);
        _strategy.Context = tracker;
        ContextDays = (int)Math.Round((before[^1].Time - before[0].Time).TotalDays);
        Log($"Market context with {ContextDays} days of hourly history: {tracker.Describe()}");
        Changed?.Invoke();
    }

    public void Start()
    {
        if (IsRunning) return;
        Ledger ??= Persist ? LiveStore.LoadLedger(Key, _data.Instrument.PointValue) : new LiveLedger(Key, _data.Instrument.PointValue);
        StopReason = null;
        _strategy.InternalInit(this);
        _builder = new BarBuilder(_data.Instrument, _period, _session, _strategy.UseFootprint);

        // warm-up with the already loaded history: builds candles and indicators without trading
        _warmup = true;
        for (int i = 0; i < _data.Trades.Count; i++) Process(i);
        _warmup = false;
        WarmupBars = _bars.Count;
        // what it drew while warming up does not count: without trading it always believes itself flat and marks breakouts the real run
        // did not take (on the chart, up to here you see the drawings of the history backtest)
        _strategy.Canvas.Clear();
        LiveFrom = _data.Trades.Count > 0 ? _data.Trades.LastTime : DateTime.UtcNow;

        _virtual = ToVirtual(Ledger.Position);
        _lastPos = Ledger.Position;
        _closing = false;
        _data.TradesAppended += OnAppended;
        HookAccount(true);
        IsRunning = true;
        StartedUtc = DateTime.UtcNow;
        Log($"Started on {_account.Name} with {WarmupBars} warm-up candles{(SizeMultiplier != 1 ? $" · size x{Qty(SizeMultiplier)}" : "")}" +
            (CrossExecution ? $" · executes on {ExecInstrument.Symbol} (1 {_data.Instrument.Symbol} = {Qty(ExecRatio)} {ExecInstrument.Symbol})" : ""));
        if (Ledger.Position != 0) CheckInheritedPosition(initial: true);
        Changed?.Invoke();
        Started?.Invoke(this);
    }

    /// <summary>Stops the strategy; optionally closes its position (only its own) and cancels its orders.</summary>
    public void Stop(bool flatten)
    {
        if (!IsRunning) return;
        _data.TradesAppended -= OnAppended;
        foreach (var o in _orders.ToList()) Cancel(o);
        _sl = _tp = null;
        try
        {
            _strategy.InternalStop();
        }
        catch (Exception ex)
        {
            Log("Error in OnStop: " + ex.Message);
        }

        IsRunning = false;
        _awaitingAccount = false;
        if (flatten && Ledger.Position != 0)
        {
            // keeps listening to the account until the close fills (so its bookkeeping ends up flat)
            _closing = true;
            SendClose("Close on stop");
            SyncOrders(); // a simulated one fills instantly
            if (_orders.Count == 0) _closing = false;
        }

        if (!_closing) HookAccount(false);
        Log(flatten ? "Halted and position closed" : "Stopped");
        Changed?.Invoke();
    }

    public void Dispose()
    {
        Stop(flatten: false);
        HookAccount(false);
    }

    /// <summary>Pauses or resumes. While paused it cancels pending entries and opens nothing new.</summary>
    public void SetPaused(bool paused)
    {
        if (Paused == paused) return;
        Paused = paused;
        if (paused)
        {
            foreach (var o in _orders.Where(o => !o.IsProtective && Opens(o.Side)).ToList()) Cancel(o);
            Log("Paused: it opens no new trades (if it has a position, it keeps managing it until it is closed)");
        }
        else Log("Resumed: it trades again");

        Changed?.Invoke();
    }

    /// <summary>
    /// Before starting: gives it an already open position in its bookkeeping (for example the one left by the backtest of the
    /// chart's history in visual mode), so it keeps managing it live and the exit stays tied to that entry.
    /// The account has to hold that position (otherwise it is closed in the bookkeeping on start).
    /// </summary>
    public void SeedPosition(LiveFill fill)
    {
        if (IsRunning) throw new InvalidOperationException("Only before starting.");
        Ledger ??= Persist ? LiveStore.LoadLedger(Key, _data.Instrument.PointValue) : new LiveLedger(Key, _data.Instrument.PointValue);
        Ledger.Apply(fill);
        if (Persist) LiveStore.AppendFill(Key, fill);
    }

    /// <summary>
    /// Starts its bookkeeping from scratch (clears its stored trades). Only with no open position. Returns false if
    /// it has a position.
    /// </summary>
    public bool ResetLedger()
    {
        if (Ledger is { Position: not 0 }) return false;
        if (Persist) LiveStore.DeleteFills(Key);
        Ledger = new LiveLedger(Key, _data.Instrument.PointValue);
        _virtual = 0;
        _lastPos = 0;
        _tradesSeen = -1;
        Log("History cleared: it starts from scratch");
        Changed?.Invoke();
        return true;
    }

    /// <summary>Closes the strategy's position at market and cancels its orders (it keeps running).</summary>
    public void ClosePosition(string why = "Manual close")
    {
        foreach (var o in _orders.ToList()) Cancel(o);
        _sl = _tp = null;
        if (Ledger == null || Ledger.Position == 0) return;
        if (!IsRunning) _closing = true;
        HookAccount(true);
        SendClose(why);
        SyncOrders(); // a simulated one fills instantly
        if (!IsRunning && _orders.Count == 0)
        {
            _closing = false;
            HookAccount(false);
        }

        Changed?.Invoke();
    }

    /// <summary>Session day result (closed + open).</summary>
    public decimal TodayPnL
    {
        get
        {
            if (Ledger == null) return 0;
            RefreshTotals();
            return _dayPnl + Ledger.OpenPnL(_data.LastPrice);
        }
    }

    /// <summary>Current drawdown from the peak of its curve (closed), counting what is open.</summary>
    public decimal CurrentDrawdown
    {
        get
        {
            if (Ledger == null) return 0;
            RefreshTotals();
            return Math.Max(0, _peakClosed - (Ledger.Realized + Ledger.OpenPnL(_data.LastPrice)));
        }
    }

    // ----------------------------------------------------------------- data

    private void OnAppended(int start, int count)
    {
        try
        {
            for (int i = start; i < start + count && IsRunning; i++) Process(i);
            if (!IsRunning) return;
            SyncOrders();
            CheckLimits();
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.Message);
            StopReason = "Error: " + ex.Message;
            Stop(flatten: false);
        }
    }

    private void Process(int i)
    {
        var store = _data.Trades;
        long id = store.GetId(i);
        if (id <= _lastId) return;
        _lastId = id;
        long t = store.GetTimeTicks(i), p = store.GetPriceTicks(i);
        _timeTicks = t;
        var closed = _builder.Roll(t, p);
        if (closed != null)
        {
            _bars.Add(closed);
            _priceTicks = _data.Instrument.ToTicks(closed.Close);
            _strategy.InternalBar(closed);
        }

        var day = _session.SessionDate(new DateTime(t, DateTimeKind.Utc));
        if (day != _sessionDay)
        {
            bool first = _sessionDay == default;
            _sessionDay = day;
            if (!first) _strategy.InternalSession(day);
            if (!_warmup && _limitDay != default && day != _limitDay)
            {
                _limitDay = default;
                StopReason = null;
                Log("New session: it trades again after the daily loss limit");
            }
        }

        _priceTicks = p;
        _builder.Append(t, p, store.GetVolume(i), store.GetSide(i));
        if (_warmup) return;
        Trail(store.GetPrice(i));
        if (_strategy.WantsIcebergs) (_icebergs ??= new IcebergDetector { Keep = TimeSpan.FromHours(4), RatioRule = true, MinRefills = 0 }).OnTrade(store.GetTime(i), store.GetPriceTicks(i), store.GetVolume(i), store.GetSide(i) == TradeSide.Buy, BookNow);
        if (_strategy.WantsTicks) _strategy.InternalTick(store.Get(i));
    }

    private void HookAccount(bool on)
    {
        if (on == _accountHooked) return;
        _accountHooked = on;
        if (on) _account.Changed += OnAccountChanged;
        else _account.Changed -= OnAccountChanged;
    }

    private void OnAccountChanged()
    {
        if (_sync != null && SynchronizationContext.Current != _sync) _sync.Post(_ => SyncOrders(), null);
        else SyncOrders();
    }

    // ----------------------------------------------------------------- inherited position

    /// <summary>
    /// Started with a position in its bookkeeping (Trading Hub was closed with the position open, or restarted with other
    /// parameters). If the account holds it, it keeps managing it; whatever the account does not hold (a fresh simulated one starts
    /// flat, or you closed it by hand) is closed in the bookkeeping at the last price. With an external broker it waits a few
    /// seconds for it to report the position before deciding, and meanwhile sends no orders.
    /// </summary>
    private void CheckInheritedPosition(bool initial)
    {
        decimal own = Ledger.Position;
        if (own == 0)
        {
            _awaitingAccount = false;
            return;
        }

        decimal acc = _account.Position?.Volume ?? 0;
        decimal covered = Math.Sign(acc) == Math.Sign(own) ? Math.Min(Math.Abs(acc), Math.Abs(own)) : 0;
        if (covered == Math.Abs(own))
        {
            _awaitingAccount = false;
            Log($"It carries on with its open position: {(own > 0 ? "long" : "short")} {Qty(Math.Abs(own))} @ {_data.Instrument.FormatPrice(Ledger.AveragePrice)}");
            if (ProtectOnStart) ProtectInherited();
            return;
        }

        if (initial && _account is not SimBroker)
        {
            // the broker can take a while to report positions after connecting
            _awaitingAccount = true;
            _awaitUntil = DateTime.UtcNow.AddSeconds(45);
            _awaitLogged = false;
            Log($"It had a position ({(own > 0 ? "long" : "short")} {Qty(Math.Abs(own))}): it waits for {_account.Name} to confirm it before trading");
            return;
        }

        if (_awaitingAccount && DateTime.UtcNow < _awaitUntil) return;
        _awaitingAccount = false;
        decimal excess = Math.Abs(own) - covered;
        BookSynthetic(own > 0 ? -excess : excess, $"The account does not hold {(covered > 0 ? "all of " : "")}the position the strategy had ({(own > 0 ? "long" : "short")} {Qty(Math.Abs(own))}): " +
                                                   $"{Qty(excess)} was closed in its accounting at the last price");
        _lastPos = Ledger.Position;
        if (ProtectOnStart && Ledger.Position != 0) ProtectInherited();
    }

    /// <summary>
    /// Puts the stop and target back on the inherited position, unless the account already has orders from this
    /// strategy (for example if Trading Hub was killed and they stayed live at the broker): they are never duplicated.
    /// </summary>
    private void ProtectInherited()
    {
        string prefix = StrategyName + ": ";
        bool already;
        lock (CanceledOrders)
            already = _account.ActiveOrders.Any(o => o.Comment.StartsWith(prefix, StringComparison.Ordinal) && o.Type is OrderTypes.Stop or OrderTypes.Limit && !CanceledOrders.Contains(o.Id));
        if (already)
        {
            Log("This strategy already has orders on the account: no new stop or target is set (review them in the Chart Trader)");
            return;
        }

        UpdateProtection();
    }

    // ----------------------------------------------------------------- orders

    private void SyncOrders()
    {
        if ((!IsRunning && !_closing) || _placing > 0) return;
        bool changed = false;
        foreach (var so in _orders.ToList())
        {
            if (so.Live is not Order o) continue;
            bool done = o.Status == OrderStatus.Filled || (o.State == OrderStates.Done && o.Unfilled == 0 && !o.Canceled);
            decimal filled = o.Filled;
            if (done && filled <= 0) filled = so.LiveQuantity;
            filled = Math.Min(filled, so.LiveQuantity);
            if (filled > so.Booked)
            {
                Book(so, filled - so.Booked);
                so.Booked = filled;
                changed = true;
            }

            if (done)
            {
                so.State = StrategyOrderState.Filled;
                so.FillPrice = LastPrice;
                so.FillTime = Time;
                _orders.Remove(so);
                Fills++;
                changed = true;
                Log($"Filled: {Describe(so)}");
                Executed?.Invoke(this, ExecutedText(so, filled));
                if (!so.IsProtective && IsRunning) _strategy.InternalFilled(so);
            }
            else if (o.Canceled || o.State == OrderStates.Failed)
            {
                if (o.State == OrderStates.Failed) Log($"Rejected: {Describe(so)} ({_account.LastError})");
                so.State = StrategyOrderState.Canceled;
                _orders.Remove(so);
                changed = true;
            }
        }

        decimal pos = Ledger.Position;
        if (pos != _lastPos)
        {
            _lastPos = pos;
            if (IsRunning) UpdateProtection();
            changed = true;
        }

        if (_closing && _orders.Count == 0)
        {
            _closing = false;
            HookAccount(false);
        }

        if (_awaitingAccount) CheckInheritedPosition(initial: false);
        if (changed)
        {
            CheckLimits();
            Changed?.Invoke();
        }
    }

    /// <summary>Books what an order filled and updates the position in the strategy's units.</summary>
    private void Book(StrategyOrder so, decimal quantity)
    {
        decimal before = Ledger.Position;
        var fill = new LiveFill(DateTime.UtcNow, so.Side == OrderSide.Buy, quantity, LastPrice > 0 ? LastPrice : _data.LastPrice, ExecInstrument.Symbol, ExecInstrument.PointValue);
        Ledger.Apply(fill);
        if (Persist) LiveStore.AppendFill(Key, fill);
        decimal after = Ledger.Position;
        if (after == 0) _virtual = 0;
        else if (so.IsProtective) _virtual = before != 0 && Math.Sign(before) == Math.Sign(after) ? _virtual * after / before : ToVirtual(after);
        else
        {
            decimal sign = so.Side == OrderSide.Buy ? 1 : -1;
            if (so.LiveQuantity > 0) _virtual += sign * so.Quantity * quantity / so.LiveQuantity;
            if (Math.Sign(_virtual) != Math.Sign(after)) _virtual = ToVirtual(after); // got out of sync: it is rebuilt
        }
    }

    /// <summary>Entry with no order (what the account does not hold): closed at the last price.</summary>
    private void BookSynthetic(decimal signedQuantity, string why)
    {
        if (signedQuantity == 0) return;
        decimal before = Ledger.Position;
        decimal price = _data.LastPrice > 0 ? _data.LastPrice : Ledger.AveragePrice;
        var fill = new LiveFill(DateTime.UtcNow, signedQuantity > 0, Math.Abs(signedQuantity), price, Ledger.OpenSymbol ?? ExecInstrument.Symbol, Ledger.OpenPointValue > 0 ? Ledger.OpenPointValue : ExecInstrument.PointValue);
        Ledger.Apply(fill);
        if (Persist) LiveStore.AppendFill(Key, fill);
        decimal after = Ledger.Position;
        _virtual = after == 0 ? 0 : before != 0 && Math.Sign(before) == Math.Sign(after) ? _virtual * after / before : ToVirtual(after);
        Log(why);
    }

    /// <summary>Sends the close of its own position (never more than what the account holds in that direction).</summary>
    private void SendClose(string why)
    {
        decimal own = Ledger.Position;
        if (own == 0) return;
        decimal acc = _account.Position?.Volume ?? 0;
        decimal closable = Math.Sign(acc) == Math.Sign(own) ? Math.Min(Math.Abs(acc), Math.Abs(own)) : 0;
        decimal excess = Math.Abs(own) - closable;
        if (excess > 0)
            BookSynthetic(own > 0 ? -excess : excess, $"The account did not hold {Qty(excess)} of the strategy's position: it was closed in its accounting at the last price");
        if (closable <= 0) return;
        var so = new StrategyOrder { Id = _nextId++, Side = own > 0 ? OrderSide.Sell : OrderSide.Buy, Type = StrategyOrderType.Market, Quantity = closable, Name = why, Time = Time, IsProtective = true };
        Place(so, closable);
    }

    private void UpdateProtection()
    {
        if (_sl is { IsActive: true }) Cancel(_sl);
        if (_tp is { IsActive: true }) Cancel(_tp);
        _sl = _tp = null;
        decimal pos = Ledger.Position;
        if (pos == 0) return;
        decimal avg = Ledger.AveragePrice;
        decimal qty = Math.Abs(pos);
        var exit = pos > 0 ? OrderSide.Sell : OrderSide.Buy;
        var inst = _data.Instrument;
        _highWater = _lowWater = LastPrice;
        int stopTicks = _strategy.StopLossTicks > 0 ? _strategy.StopLossTicks : _strategy.TrailingStopTicks;
        if (stopTicks > 0)
        {
            decimal d = stopTicks * inst.TickSize;
            _sl = Protective(exit, StrategyOrderType.Stop, qty, inst.RoundPrice(pos > 0 ? avg - d : avg + d), "Stop loss");
        }

        if (_strategy.TakeProfitTicks > 0)
        {
            decimal d = _strategy.TakeProfitTicks * inst.TickSize;
            _tp = Protective(exit, StrategyOrderType.Limit, qty, inst.RoundPrice(pos > 0 ? avg + d : avg - d), "Take profit");
        }
    }

    private StrategyOrder Protective(OrderSide side, StrategyOrderType type, decimal qty, decimal price, string name)
    {
        var so = new StrategyOrder { Id = _nextId++, Side = side, Type = type, Quantity = qty, Price = price, Name = name, Time = Time, IsProtective = true };
        return Place(so, qty);
    }

    private void Trail(decimal price)
    {
        if (_strategy.TrailingStopTicks <= 0 || _sl is not { IsActive: true } || _sl.Live is not Order o) return;
        decimal pos = Ledger.Position;
        decimal dist = _strategy.TrailingStopTicks * _data.Instrument.TickSize;
        decimal? target = null;
        if (pos > 0 && price > _highWater)
        {
            _highWater = price;
            decimal stop = ExecInstrument.RoundPrice(price - dist);
            if (stop > _sl.Price) target = stop;
        }
        else if (pos < 0 && price < _lowWater)
        {
            _lowWater = price;
            decimal stop = ExecInstrument.RoundPrice(price + dist);
            if (stop < _sl.Price) target = stop;
        }

        // no more than one modification per second (exchanges rate-limit requests)
        if (target is { } t && (DateTime.UtcNow - _lastTrailUpdate).TotalSeconds >= 1)
        {
            _lastTrailUpdate = DateTime.UtcNow;
            _sl.Price = t;
            _account.Modify(o, t);
        }
    }

    /// <summary>Sends the account an order with its real quantity.</summary>
    private StrategyOrder Place(StrategyOrder so, decimal liveQuantity)
    {
        so.LiveQuantity = liveQuantity;
        var dir = so.Side == OrderSide.Buy ? OrderDirections.Buy : OrderDirections.Sell;
        string comment = $"{StrategyName}: {so.Name}";
        Order live;
        _placing++; // a simulated one fills (and reports) before returning the order: it is booked afterwards
        try
        {
            live = so.Type switch
            {
                StrategyOrderType.Market => _account.Market(dir, liveQuantity, comment),
                StrategyOrderType.Limit => _account.Limit(dir, liveQuantity, ExecInstrument.RoundPrice(so.Price), comment),
                _ => _account.Stop(dir, liveQuantity, ExecInstrument.RoundPrice(so.Price), comment),
            };
        }
        finally
        {
            _placing--;
        }

        so.Live = live;
        if (live.State == OrderStates.Failed)
        {
            so.State = StrategyOrderState.Canceled;
            Log($"Rejected: {Describe(so)} ({_account.LastError})");
            return so;
        }

        _orders.Add(so);
        Log($"Sent: {Describe(so)}");
        Changed?.Invoke();
        return so;
    }

    /// <summary>
    /// Real size on the account for a strategy position (with the size applied, rounded down to the instrument's
    /// step). If the strategy wants to be in the market it never ends up at zero because of rounding: it goes at least one
    /// unit (for example, 1 contract x 0.5 on a future is 1 contract; before it gave 0 and nothing was traded).
    /// </summary>
    private decimal Target(decimal virtualPosition)
    {
        if (virtualPosition == 0 || SizeMultiplier <= 0) return 0;
        decimal step = ExecInstrument.VolumeStep > 0 ? ExecInstrument.VolumeStep : 1;
        decimal units = Math.Floor(Math.Abs(virtualPosition) * SizeMultiplier * ExecRatio / step + 0.0000001m);
        return Math.Sign(virtualPosition) * Math.Max(1, units) * step;
    }

    /// <summary>The size cannot be applied as is (it falls below one unit and the minimum is sent).</summary>
    public bool SizeBelowMinimum(decimal strategyQuantity)
    {
        decimal step = ExecInstrument.VolumeStep > 0 ? ExecInstrument.VolumeStep : 1;
        return SizeMultiplier > 0 && strategyQuantity > 0 && strategyQuantity * SizeMultiplier * ExecRatio < step;
    }

    private decimal ToVirtual(decimal own) => SizeMultiplier > 0 && ExecRatio > 0 ? Math.Round(own / (SizeMultiplier * ExecRatio), 8) : own;

    /// <summary>Does an order on this side open or increase the strategy's position?</summary>
    private bool Opens(OrderSide side) => _virtual == 0 || Math.Sign(_virtual) == (side == OrderSide.Buy ? 1 : -1);

    private string Describe(StrategyOrder so)
    {
        string type = so.Type switch { StrategyOrderType.Market => "at market", StrategyOrderType.Limit => "limit", _ => "stop" };
        string price = so.Type == StrategyOrderType.Market ? "" : " @ " + _data.Instrument.FormatPrice(so.Price);
        string scale = !so.IsProtective && (so.LiveQuantity != so.Quantity || CrossExecution)
            ? $" (strategy {Qty(so.Quantity)} {_data.Instrument.Symbol} × size {Qty(SizeMultiplier)}{(CrossExecution ? $" = {Qty(so.LiveQuantity)} {ExecInstrument.Symbol}, micro" : "")})"
            : "";
        return $"{so.Name}: {(so.Side == OrderSide.Buy ? "buy" : "sell")} {type} {Qty(so.LiveQuantity)} {ExecInstrument.Symbol}{price}{scale}";
    }

    /// <summary>The notice for a filled order: "Moving average cross bought 5 MNQ at 20,150.25 (micro: equals 0.5 NQ)".</summary>
    private string ExecutedText(StrategyOrder so, decimal filled)
    {
        decimal qty = filled > 0 ? filled : so.LiveQuantity;
        string what = so.IsProtective ? $" ({so.Name.ToLowerInvariant()})" : "";
        string micro = CrossExecution && ExecRatio > 0 ? $" · micro: equals {Qty(qty / ExecRatio)} {_data.Instrument.Symbol}" : "";
        decimal price = _account.LastPrice > 0 ? _account.LastPrice : LastPrice > 0 ? LastPrice : _data.LastPrice;
        return $"“{StrategyName}” {(so.Side == OrderSide.Buy ? "bought" : "sold")} {Qty(qty)} {ExecInstrument.Symbol} at {ExecInstrument.FormatPrice(price)}{what} on {_account.Name}{micro}";
    }

    private static string Qty(decimal q) => q.ToString("0.########", Inv);

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Inv);

    // ----------------------------------------------------------------- risk limits

    private void RefreshTotals()
    {
        var trades = Ledger.Trades;
        if (trades.Count == _tradesSeen && _totalsDay == _sessionDay) return;
        _tradesSeen = trades.Count;
        _totalsDay = _sessionDay;
        decimal eq = 0, peak = 0, day = 0;
        foreach (var t in trades)
        {
            eq += t.NetPnl;
            peak = Math.Max(peak, eq);
            if (_sessionDay != default && _session.SessionDate(DateTime.SpecifyKind(t.ExitTime, DateTimeKind.Utc)) == _sessionDay) day += t.NetPnl;
        }

        _peakClosed = peak;
        _dayPnl = day;
    }

    private void CheckLimits()
    {
        if (!IsRunning || _warmup || (MaxDailyLoss <= 0 && MaxDrawdown <= 0)) return;
        if (MaxDrawdown > 0 && CurrentDrawdown >= MaxDrawdown)
        {
            decimal dd = CurrentDrawdown;
            StopReason = $"It hit the maximum drawdown ({Money(-dd)}; limit {Money(-MaxDrawdown)})";
            string msg = $"{StrategyName}: {StopReason}. Stopped{(CloseOnLimit ? " and closing its position" : "")}.";
            Log(StopReason + (CloseOnLimit ? ": it halts and closes the position" : ": it halts (the position stays open)"));
            Stop(CloseOnLimit);
            LimitHit?.Invoke(this, msg);
            return;
        }

        if (MaxDailyLoss > 0 && !DailyLimitActive)
        {
            decimal today = TodayPnL;
            if (today > -MaxDailyLoss) return;
            _limitDay = _sessionDay;
            StopReason = $"It hit the maximum daily loss ({Money(today)}; limit {Money(-MaxDailyLoss)})";
            Log(StopReason + ": it opens nothing more until the next session" + (CloseOnLimit ? " and closes the position" : ""));
            foreach (var o in _orders.Where(o => !o.IsProtective).ToList()) Cancel(o);
            if (CloseOnLimit)
            {
                foreach (var o in _orders.ToList()) Cancel(o);
                _sl = _tp = null;
                SendClose("Daily limit");
            }

            LimitHit?.Invoke(this, $"{StrategyName}: {StopReason}. It opens nothing more until the next session{(CloseOnLimit ? " and closed its position" : "")}.");
            Changed?.Invoke();
        }
    }

    // ----------------------------------------------------------------- IStrategyHost

    public Instrument Instrument => _data.Instrument;
    public ChartPeriod Period => _period;
    public BarSeries Bars => _bars;
    public Bar? FormingBar => _builder?.Current;
    public DateTime Time => new(_timeTicks, DateTimeKind.Utc);
    DepthSnapshot? IStrategyHost.Book => BookNow;
    private DepthSnapshot? BookNow => _data.Depth is { } d && (d.HasBid || d.HasAsk) ? d : null;
    public IReadOnlyList<IcebergEvent> Icebergs => _icebergs?.Events ?? (IReadOnlyList<IcebergEvent>)Array.Empty<IcebergEvent>();
    public decimal LastPrice => _data.Instrument.FromTicks(_priceTicks);
    public TimeZoneInfo TimeZone => _tz;
    public SessionSettings Session => _session;

    /// <summary>The position the strategy sees: in its own units, without the portfolio size.</summary>
    public decimal Position => _warmup ? 0 : _virtual;

    public decimal AveragePrice => _warmup || Ledger == null ? 0 : Ledger.AveragePrice;

    /// <summary>Open result in the strategy's units.</summary>
    public decimal OpenPnL => _warmup || Ledger == null || Ledger.Position == 0 ? 0 : Ledger.OpenPnL(LastPrice) * _virtual / Ledger.Position;

    public decimal RealizedPnL => _warmup || Ledger == null ? 0 : Ledger.Realized;
    public IReadOnlyList<StrategyOrder> ActiveOrders => _orders.ToList();
    public bool IsBacktest => false;

    /// <summary>
    /// Order from the strategy (quantity in its own units). It sends the difference between what matches on the account with
    /// the current size and what it holds; while paused or with the daily limit hit only the ones that reduce the position are accepted.
    /// </summary>
    public StrategyOrder Submit(OrderSide side, StrategyOrderType type, decimal quantity, decimal price, string name)
    {
        if (quantity <= 0) throw new ArgumentException("The quantity has to be greater than zero.");
        var so = new StrategyOrder { Id = _nextId++, Side = side, Type = type, Quantity = quantity, Price = price, Name = name, Time = Time };
        if (_warmup)
        {
            so.State = StrategyOrderState.Canceled;
            return so;
        }

        if (_awaitingAccount)
        {
            if (!_awaitLogged) Log($"Waiting for {_account.Name} to confirm the inherited position: {name} was not sent");
            _awaitLogged = true;
            so.State = StrategyOrderState.Canceled;
            return so;
        }

        decimal sign = side == OrderSide.Buy ? 1 : -1;
        decimal q = quantity;
        if (Paused || DailyLimitActive)
        {
            // the position is only reduced (a reversal is trimmed to a close)
            if (Opens(side))
            {
                Log($"{(Paused ? "Paused" : "Daily limit hit")}: {name} was not sent");
                so.State = StrategyOrderState.Canceled;
                return so;
            }

            q = Math.Min(q, Math.Abs(_virtual));
        }

        decimal own = Ledger.Position;
        decimal delta = Target(_virtual + sign * q) - own;
        if (delta == 0 || Math.Sign(delta) != Math.Sign(sign))
        {
            Log(SizeMultiplier <= 0 ? $"Size at zero: {name} was not sent" : $"At size ×{Qty(SizeMultiplier)} the order {name} comes out as zero");
            so.State = StrategyOrderState.Canceled;
            return so;
        }

        decimal after = own + delta;
        bool reduces = Math.Abs(after) < Math.Abs(own) && (after == 0 || Math.Sign(after) == Math.Sign(own));
        if (!reduces && Guard?.Check(this, side, Math.Abs(delta), after) is { } why)
        {
            Log($"Risk limiter: {why}. {name} was not sent");
            so.State = StrategyOrderState.Canceled;
            GuardBlocked?.Invoke(this, why);
            return so;
        }

        so.Quantity = q;
        return Place(so, Math.Abs(delta));
    }

    public void Cancel(StrategyOrder order)
    {
        if (!order.IsActive) return;
        if (order.Live is Order o)
        {
            // the ones a strategy cancels (for example when restarting it) do not count as "already protected" even if the broker lags
            lock (CanceledOrders) CanceledOrders.Add(o.Id);
            _account.Cancel(o);
        }

        order.State = StrategyOrderState.Canceled;
        _orders.Remove(order);
    }

    public void Modify(StrategyOrder order, decimal price)
    {
        if (!order.IsActive || order.Live is not Order o || price <= 0) return;
        order.Price = price;
        _account.Modify(o, price);
    }

    public void Log(string message)
    {
        if (_warmup) return;
        _log.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        Changed?.Invoke();
    }
}
