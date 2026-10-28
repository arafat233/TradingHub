using System.Reflection;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>Name and description of a strategy.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class StrategyAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; set; } = "";
}

/// <summary>Configurable property of the strategy (it shows up in the parameters panel).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ParameterAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
    public string Description { get; set; } = "";
}

/// <summary>Range to sweep in the optimization (minimum, maximum and step).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OptimizeAttribute(double min, double max, double step) : Attribute
{
    public double Min { get; } = min;
    public double Max { get; } = max;
    public double Step { get; } = step;
}

public enum OrderSide
{
    Buy,
    Sell,
}

public enum StrategyOrderType
{
    Market,
    Limit,
    Stop,
}

public enum StrategyOrderState
{
    Active,
    Filled,
    Canceled,
}

/// <summary>An order from the strategy.</summary>
public sealed class StrategyOrder
{
    public long Id { get; internal init; }
    public OrderSide Side { get; internal init; }
    public StrategyOrderType Type { get; internal init; }
    public decimal Quantity { get; internal set; }
    public decimal Price { get; internal set; }
    public string Name { get; internal init; } = "";
    public StrategyOrderState State { get; internal set; } = StrategyOrderState.Active;
    public DateTime Time { get; internal init; }
    public decimal FillPrice { get; internal set; }
    public DateTime FillTime { get; internal set; }

    /// <summary>Automatic stop/target of the position.</summary>
    public bool IsProtective { get; internal init; }

    internal long SubmittedAt { get; init; }
    internal bool Marketable { get; init; }
    internal string? OcoGroup { get; init; }
    internal object? Live { get; set; }

    /// <summary>Live: real quantity sent to the account (with the portfolio size) and how much has already been booked.</summary>
    internal decimal LiveQuantity { get; set; }
    internal decimal Booked { get; set; }

    public bool IsActive => State == StrategyOrderState.Active;

    public override string ToString() => $"{Name}: {(Side == OrderSide.Buy ? "buy" : "sell")} {Type} {Quantity}{(Type == StrategyOrderType.Market ? "" : " @ " + Price)}";
}

/// <summary>What the strategy needs from the environment (backtest or live).</summary>
internal interface IStrategyHost
{
    Instrument Instrument { get; }
    ChartPeriod Period { get; }
    BarSeries Bars { get; }
    Bar? FormingBar { get; }
    DateTime Time { get; }

    /// <summary>The order book right now (null if there is no depth: a backtest without recorded books, or a feed that sends none).</summary>
    DepthSnapshot? Book { get; }

    /// <summary>Icebergs found so far (only filled when the strategy asked for them with TrackIcebergs).</summary>
    IReadOnlyList<IcebergEvent> Icebergs { get; }
    decimal LastPrice { get; }
    TimeZoneInfo TimeZone { get; }
    SessionSettings Session { get; }
    decimal Position { get; }
    decimal AveragePrice { get; }
    decimal OpenPnL { get; }
    decimal RealizedPnL { get; }
    IReadOnlyList<StrategyOrder> ActiveOrders { get; }
    bool IsBacktest { get; }
    StrategyOrder Submit(OrderSide side, StrategyOrderType type, decimal quantity, decimal price, string name);
    void Cancel(StrategyOrder order);
    void Modify(StrategyOrder order, decimal price);
    void Log(string message);
}

/// <summary>
/// Base class for strategies. Override <see cref="OnInit"/> to create indicators and
/// <see cref="OnBar"/> (close of each candle) or <see cref="OnTick"/> (each trade) to decide.
/// </summary>
public abstract class Strategy
{
    private readonly List<Series> _indicators = new();
    internal IStrategyHost Host = null!;

    // ----------------------------------------------------------------- common parameters

    /// <summary>Default quantity of each order.</summary>
    [Parameter("Quantity", Description = "Contracts or units per order")]
    public decimal Quantity { get; set; } = 1;

    /// <summary>Automatic stop loss in ticks for each position (0 = no stop).</summary>
    [Parameter("Stop loss (ticks)", Description = "0 = no automatic stop")]
    public int StopLossTicks { get; set; }

    /// <summary>Automatic take profit in ticks (0 = no target).</summary>
    [Parameter("Take profit (ticks)", Description = "0 = no automatic target")]
    public int TakeProfitTicks { get; set; }

    /// <summary>Stop that follows the price at this distance in ticks (0 = no).</summary>
    [Parameter("Trailing stop (ticks)", Description = "0 = no trailing")]
    public int TrailingStopTicks { get; set; }

    /// <summary>Context filter: it only opens trades in this market condition (exits always go through).</summary>
    [Parameter("Regime filter", Description = "It only opens trades in that market condition (range, mixed or trend — up or down — over the last 24 h). All = no filter")]
    public RegimeFilter RegimeFilter { get; set; } = RegimeFilter.All;

    /// <summary>Context filter: it does not open if the volatility is below this percentile of the last month.</summary>
    [Parameter("Minimum volatility (percentile)", Description = "0 to 100: it opens no trades if the last 24 h volatility is below that percentile of the last month. 0 = no filter")]
    public int MinVolatilityRank { get; set; }

    /// <summary>Context filter: it does not open if the volatility is above this percentile of the last month.</summary>
    [Parameter("Maximum volatility (percentile)", Description = "0 to 100: it opens no trades if the last 24 h volatility is above that percentile of the last month. 100 = no filter")]
    public int MaxVolatilityRank { get; set; } = 100;

    /// <summary>Whether the candles store volume by price (footprint). Enable it in OnInit if you use it.</summary>
    protected internal bool UseFootprint { get; set; }

    /// <summary>Market context (trend or range and volatility), candle by candle.</summary>
    internal MarketContextTracker Context { get; set; } = new();

    /// <summary>What the strategy draws on the chart (ranges, levels, arrows, texts and lines).</summary>
    public StrategyCanvas Canvas { get; } = new();

    // ----------------------------------------------------------------- data

    public Instrument Instrument => Host.Instrument;
    public ChartPeriod Period => Host.Period;

    /// <summary>Closed candles: <c>Bars[0]</c> is the last one, <c>Bars[1]</c> the previous one…</summary>
    public BarSeries Bars => Host.Bars;

    /// <summary>Last closed candle.</summary>
    public Bar Bar => Host.Bars[0];

    /// <summary>Candle currently forming (to use in <see cref="OnTick"/>).</summary>
    public Bar? FormingBar => Host.FormingBar;

    public int CurrentBar => Host.Bars.Count - 1;

    /// <summary>Market time (UTC).</summary>
    public DateTime Time => Host.Time;

    /// <summary>Market time in the session's time zone.</summary>
    public DateTime LocalTime => TimeZoneInfo.ConvertTimeFromUtc(Host.Time, Host.TimeZone);

    public decimal Price => Host.LastPrice;

    // ----------------------------------------------------------------- order book

    /// <summary>
    /// The order book at this moment: live, or the recorded one in a backtest (the book is recorded from the moment an instrument is opened
    /// with "Record the order book" on, 4 snapshots a second, 20 levels per side). null when there is none: always check it.
    /// </summary>
    public DepthSnapshot? Book => Host.Book;

    /// <summary>
    /// (bid size - ask size) / (bid size + ask size) over the first <paramref name="levels"/> levels: +1 = all bids, -1 = all asks,
    /// 0 = balanced or no book.
    /// </summary>
    public decimal BookImbalance(int levels = 5)
    {
        if (Host.Book is not { } b) return 0;
        decimal bid = 0, ask = 0;
        for (int i = 0; i < Math.Min(levels, b.Bids.Length); i++) bid += b.Bids[i].Size;
        for (int i = 0; i < Math.Min(levels, b.Asks.Length); i++) ask += b.Asks[i].Size;
        return bid + ask == 0 ? 0 : (bid - ask) / (bid + ask);
    }

    /// <summary>Set it to true in OnInit to have <see cref="Icebergs"/> filled (it costs a little on every trade, so it is off by default).</summary>
    protected bool TrackIcebergs { get; set; }
    internal bool WantsIcebergs => TrackIcebergs;

    /// <summary>
    /// Iceberg orders found so far (levels that absorbed much more than they showed; needs the book and TrackIcebergs). Strategies keep this absorption rule
    /// only, so older backtests give the same results; the charts also use a refill rule.
    /// An iceberg with <c>Ask</c> true is a resting sell (buyers kept hitting it), false a resting buy.
    /// </summary>
    public IReadOnlyList<IcebergEvent> Icebergs => Host.Icebergs;
    /// <summary>
    /// Set it in <c>OnInit</c> to a size to have <see cref="BigTradeLevels"/> filled: every market order (the fills with the
    /// same time and side) of at least that many contracts is remembered, with its average price. 0 = off.
    /// </summary>
    protected decimal TrackBigTrades { get; set; }

    private readonly List<BigTradeLevel> _bigLevels = new();
    private BigTradeLevel? _bigLive;
    private decimal _bigVol, _bigPv;
    private DateTime _bigTime;
    private TradeSide _bigSide;

    /// <summary>Big orders so far, oldest first (the newest 500 are kept). Check <see cref="BigTradeLevel.Touched"/> to skip the used-up ones.</summary>
    public IReadOnlyList<BigTradeLevel> BigTradeLevels => _bigLevels;

    private void TrackBig(in Trade t)
    {
        decimal tick = TickSize;
        if (t.Time != _bigTime || t.Side != _bigSide)
        {
            // a new order: price coming back to an older level uses it up (the order's own fills do not)
            foreach (var l in _bigLevels)
                if (!l.Touched && l.Time < t.Time && Math.Abs(t.Price - l.Price) < Math.Max(tick, 0.0000001m) / 2) l.Touched = true;
            _bigLive = null;
            _bigVol = _bigPv = 0;
            _bigTime = t.Time;
            _bigSide = t.Side;
        }

        if (t.Side == TradeSide.None || t.Volume <= 0) return;
        _bigVol += t.Volume;
        _bigPv += t.Price * t.Volume;
        if (_bigVol < TrackBigTrades) return;
        decimal price = _bigPv / _bigVol;
        if (tick > 0) price = Math.Round(price / tick) * tick;
        if (_bigLive == null)
        {
            _bigLive = new BigTradeLevel(t.Time, price, _bigVol, t.Side == TradeSide.Buy);
            _bigLevels.Add(_bigLive);
            if (_bigLevels.Count > 500) _bigLevels.RemoveAt(0);
        }
        else
        {
            _bigLive.Price = price;
            _bigLive.Volume = _bigVol;
        }
    }

    public decimal TickSize => Host.Instrument.TickSize;
    public bool IsBacktest => Host.IsBacktest;

    // ----------------------------------------------------------------- market context

    /// <summary>Trend efficiency of the last 24 hours (0 = pure range, 1 = clean trend; random is ~0.2). null with no data.</summary>
    public double? MarketTrend => Context.Trend;

    /// <summary>Which way the market moved over those 24 hours: 1 up, -1 down, 0 flat (null with no data).</summary>
    public int? MarketDirection => Context.Direction;

    /// <summary>Net market move over those 24 hours, in %.</summary>
    public double? MarketChange => Context.Change;

    /// <summary>Volatility of the last 24 hours: standard deviation of the hourly returns, in %.</summary>
    public double? MarketVolatility => Context.Volatility;

    /// <summary>Percentile of that volatility among the last month's (0 = the lowest, 100 = the highest).</summary>
    public double? VolatilityRank => Context.VolatilityRank;

    /// <summary>Market condition: Range (efficiency <= 0.15), Mixed or Trend (>= 0.35); Unknown with no data.</summary>
    public MarketRegime Regime => Context.Regime;

    /// <summary>Whether the context filter (RegimeFilter and volatility percentiles) allows opening trades right now.</summary>
    public bool ContextAllowsEntries => !HasContextFilter || Context.Allows(RegimeFilter, MinVolatilityRank, MaxVolatilityRank);

    private bool HasContextFilter => RegimeFilter != RegimeFilter.All || MinVolatilityRank > 0 || MaxVolatilityRank < 100;

    // ----------------------------------------------------------------- position

    /// <summary>Position: positive = long, negative = short, 0 = no position.</summary>
    public decimal Position => Host.Position;

    public bool IsFlat => Host.Position == 0;
    public bool IsLong => Host.Position > 0;
    public bool IsShort => Host.Position < 0;
    public decimal AveragePrice => Host.AveragePrice;
    public decimal OpenPnL => Host.OpenPnL;
    public decimal NetProfit => Host.RealizedPnL;
    public IReadOnlyList<StrategyOrder> ActiveOrders => Host.ActiveOrders;

    // ----------------------------------------------------------------- orders

    protected StrategyOrder Buy(decimal? quantity = null, string name = "Buy") => Send(OrderSide.Buy, StrategyOrderType.Market, quantity ?? Quantity, 0, name);
    protected StrategyOrder Sell(decimal? quantity = null, string name = "Sell") => Send(OrderSide.Sell, StrategyOrderType.Market, quantity ?? Quantity, 0, name);
    protected StrategyOrder BuyLimit(decimal price, decimal? quantity = null, string name = "Buy limit") => Send(OrderSide.Buy, StrategyOrderType.Limit, quantity ?? Quantity, Round(price), name);
    protected StrategyOrder SellLimit(decimal price, decimal? quantity = null, string name = "Sell limit") => Send(OrderSide.Sell, StrategyOrderType.Limit, quantity ?? Quantity, Round(price), name);
    protected StrategyOrder BuyStop(decimal price, decimal? quantity = null, string name = "Buy stop") => Send(OrderSide.Buy, StrategyOrderType.Stop, quantity ?? Quantity, Round(price), name);
    protected StrategyOrder SellStop(decimal price, decimal? quantity = null, string name = "Sell stop") => Send(OrderSide.Sell, StrategyOrderType.Stop, quantity ?? Quantity, Round(price), name);

    private DateTime _filterLogged = DateTime.MinValue;

    /// <summary>
    /// Sends the order through the context filter: outside the chosen context only the part that closes what is
    /// open goes out (it never opens or increases); if nothing is left, the order comes back cancelled.
    /// </summary>
    private StrategyOrder Send(OrderSide side, StrategyOrderType type, decimal quantity, decimal price, string name)
    {
        if (ContextAllowsEntries) return Host.Submit(side, type, quantity, price, name);
        decimal pos = Host.Position;
        decimal closing = side == OrderSide.Buy ? (pos < 0 ? Math.Min(quantity, -pos) : 0) : (pos > 0 ? Math.Min(quantity, pos) : 0);
        if (closing < quantity && Host.Time - _filterLogged >= (Host.IsBacktest ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1)))
        {
            _filterLogged = Host.Time;
            Host.Log($"Context filter: not opening trades right now ({Context.Describe()})");
        }

        if (closing > 0) return Host.Submit(side, type, closing, price, name);
        return new StrategyOrder { Side = side, Type = type, Quantity = quantity, Price = price, Name = name + " (context filter)", State = StrategyOrderState.Canceled, Time = Host.Time };
    }

    /// <summary>Enters long at market (if it was short, it reverses the position).</summary>
    protected void EnterLong(decimal? quantity = null, string name = "Long")
    {
        decimal q = quantity ?? Quantity;
        if (Position < 0) q += -Position;
        if (Position <= 0) Buy(q, name);
    }

    /// <summary>Enters short at market (if it was long, it reverses the position).</summary>
    protected void EnterShort(decimal? quantity = null, string name = "Short")
    {
        decimal q = quantity ?? Quantity;
        if (Position > 0) q += Position;
        if (Position >= 0) Sell(q, name);
    }

    /// <summary>Closes the position at market and cancels the pending entry orders.</summary>
    protected void ClosePosition(string name = "Close")
    {
        foreach (var o in Host.ActiveOrders.Where(o => !o.IsProtective).ToList()) Host.Cancel(o);
        if (Position > 0) Sell(Position, name);
        else if (Position < 0) Buy(-Position, name);
    }

    protected void Cancel(StrategyOrder order) => Host.Cancel(order);

    protected void CancelAll()
    {
        foreach (var o in Host.ActiveOrders.Where(o => !o.IsProtective).ToList()) Host.Cancel(o);
    }

    protected void Modify(StrategyOrder order, decimal price) => Host.Modify(order, Round(price));

    // ----------------------------------------------------------------- indicators

    protected T Add<T>(T series) where T : Series
    {
        _indicators.Add(series);
        return series;
    }

    /// <summary>The indicators it created (to measure the warm-up).</summary>
    internal IReadOnlyList<Series> Indicators => _indicators;

    protected Sma Sma(int period, PriceSource source = PriceSource.Close) => Add(new Sma(period, source));
    protected Ema Ema(int period, PriceSource source = PriceSource.Close) => Add(new Ema(period, source));
    protected Rsi Rsi(int period = 14, PriceSource source = PriceSource.Close) => Add(new Rsi(period, source));
    protected Atr Atr(int period = 14) => Add(new Atr(period));
    protected Highest Highest(int period, PriceSource source = PriceSource.High) => Add(new Highest(period, source));
    protected Lowest Lowest(int period, PriceSource source = PriceSource.Low) => Add(new Lowest(period, source));
    protected StdDev StdDev(int period, PriceSource source = PriceSource.Close) => Add(new StdDev(period, source));
    protected Bollinger Bollinger(int period = 20, decimal k = 2, PriceSource source = PriceSource.Close) => Add(new Bollinger(period, k, source));
    protected Macd Macd(int fast = 12, int slow = 26, int signal = 9) => Add(new Macd(fast, slow, signal));
    protected Vwap Vwap() => Add(new Vwap(t => Host.Session.SessionDate(t)));
    protected CumulativeDelta CumulativeDelta(bool resetEachSession = true) => Add(new CumulativeDelta(resetEachSession ? t => Host.Session.SessionDate(t) : null));

    /// <summary>Custom series computed on each candle (for example, the difference between two averages).</summary>
    protected CustomSeries Custom(string name, Func<BarSeries, decimal> calc) => Add(new CustomSeries(name, calc));

    // ----------------------------------------------------------------- helpers

    protected static bool CrossAbove(Series a, Series b) => a.Count > 1 && b.Count > 1 && a[1] <= b[1] && a[0] > b[0];
    protected static bool CrossBelow(Series a, Series b) => a.Count > 1 && b.Count > 1 && a[1] >= b[1] && a[0] < b[0];
    protected static bool CrossAbove(Series a, decimal level) => a.Count > 1 && a[1] <= level && a[0] > level;
    protected static bool CrossBelow(Series a, decimal level) => a.Count > 1 && a[1] >= level && a[0] < level;

    /// <summary>Distance of N ticks in price.</summary>
    protected decimal Ticks(int n) => n * Host.Instrument.TickSize;

    protected decimal Round(decimal price) => Host.Instrument.RoundPrice(price);

    /// <summary>Whether the session's local time is between two times of day (e.g. 09:30 and 16:00).</summary>
    protected bool TimeBetween(TimeSpan from, TimeSpan to)
    {
        var t = LocalTime.TimeOfDay;
        return from <= to ? t >= from && t < to : t >= from || t < to;
    }

    protected void Log(string message) => Host.Log(message);

    // ----------------------------------------------------------------- chart drawings

    /// <summary>
    /// Rectangle (for example the opening range): from <paramref name="from"/> to <paramref name="to"/> and between two prices.
    /// Repeating the id replaces it (useful for stretching it candle by candle). Color "#RRGGBB" or a name ("green", "red"…).
    /// </summary>
    protected void DrawBox(string id, DateTime from, decimal price1, DateTime to, decimal price2, string? color = null, int fillOpacity = 15, string? text = null) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.Box, Time1 = from, Price1 = price1, Time2 = to, Price2 = price2, Color = color, FillOpacity = Math.Clamp(fillOpacity, 0, 100), Text = text ?? "" });

    /// <summary>Line from one point (time, price) to another.</summary>
    protected void DrawLine(string id, DateTime time1, decimal price1, DateTime time2, decimal price2, string? color = null, int width = 1, LineStyle style = LineStyle.Solid) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.Line, Time1 = time1, Price1 = price1, Time2 = time2, Price2 = price2, Color = color, Width = Math.Clamp(width, 1, 6), Style = style });

    /// <summary>Horizontal level from <paramref name="from"/> (or from the start) to the right edge, with an optional label.</summary>
    protected void DrawHorizontalLine(string id, decimal price, DateTime? from = null, string? color = null, int width = 1, LineStyle style = LineStyle.Dashed, string? text = null) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.HorizontalLine, Time1 = from ?? DateTime.MinValue, Price1 = price, Price2 = price, Color = color, Width = Math.Clamp(width, 1, 6), Style = style, Text = text ?? "", ExtendRight = true });

    /// <summary>Arrow pointing up with its tip at the price (for example an upside breakout), with optional text.</summary>
    protected void DrawArrowUp(string id, DateTime time, decimal price, string? color = null, string? text = null) => DrawMarker(id, time, price, MarkerShape.ArrowUp, color, text);

    /// <summary>Arrow pointing down with its tip at the price.</summary>
    protected void DrawArrowDown(string id, DateTime time, decimal price, string? color = null, string? text = null) => DrawMarker(id, time, price, MarkerShape.ArrowDown, color, text);

    /// <summary>Marker at a price: arrow, circle, square, diamond or cross, with optional text.</summary>
    protected void DrawMarker(string id, DateTime time, decimal price, MarkerShape shape = MarkerShape.Circle, string? color = null, string? text = null) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.Marker, Time1 = time, Price1 = price, Time2 = time, Price2 = price, Color = color, Shape = shape, Text = text ?? "" });

    /// <summary>Text at a point on the chart.</summary>
    protected void DrawText(string id, DateTime time, decimal price, string text, string? color = null) =>
        Canvas.Set(new StrategyDrawing { Id = id, Kind = DrawingKind.Text, Time1 = time, Price1 = price, Time2 = time, Price2 = price, Color = color, Text = text });

    /// <summary>
    /// Continuous line traced candle by candle (call it in OnBar): for example Plot("Ceiling", _max[0]). null breaks the line
    /// (for example outside the session). Each name is a different line.
    /// </summary>
    protected void Plot(string name, decimal? value, string? color = null, int width = 1, LineStyle style = LineStyle.Solid) =>
        Canvas.Plot(name, Host.Bars.Count > 0 ? Host.Bars[0].Time : Host.Time, value, color, Math.Clamp(width, 1, 6), style);

    /// <summary>Removes a drawing by its id.</summary>
    protected void RemoveDrawing(string id) => Canvas.Remove(id);

    /// <summary>Removes every drawing and line.</summary>
    protected void ClearDrawings() => Canvas.Clear();

    // ----------------------------------------------------------------- lifecycle

    /// <summary>At the start: create the indicators here.</summary>
    protected virtual void OnInit()
    {
    }

    /// <summary>On the close of each candle.</summary>
    protected virtual void OnBar(Bar bar)
    {
    }

    /// <summary>On each market trade (slower: use it only if you need it).</summary>
    protected virtual void OnTick(Trade trade)
    {
    }

    /// <summary>When an order fills.</summary>
    protected virtual void OnOrderFilled(StrategyOrder order)
    {
    }

    /// <summary>At the start of a new session (trading day).</summary>
    protected virtual void OnSessionStart(DateOnly session)
    {
    }

    /// <summary>At the end (end of the backtest or when the strategy is stopped).</summary>
    protected virtual void OnStop()
    {
    }

    // ----------------------------------------------------------------- called by the engine

    internal bool WantsTicks { get; private set; }

    /// <summary>The strategy looks at every trade (OnTick), not just the candles.</summary>
    public static bool UsesTicks(Type t)
    {
        var m = t.GetMethod(nameof(OnTick), BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(Trade) });
        return m != null && m.DeclaringType != typeof(Strategy);
    }

    internal void InternalInit(IStrategyHost host)
    {
        Host = host;
        _indicators.Clear();
        Context = new MarketContextTracker(host.Period.IsTimeBased && host.Period.ApproxDuration >= TimeSpan.FromHours(1));
        Canvas.Clear();
        WantsTicks = UsesTicks(GetType());
        OnInit();
        if (TrackBigTrades > 0) WantsTicks = true;
    }

    internal void InternalBar(Bar bar)
    {
        Context.Add(bar.Time, bar.Close);
        foreach (var s in _indicators) s.Update(Host.Bars);
        OnBar(bar);
    }

    internal void InternalTick(in Trade t)
    {
        if (TrackBigTrades > 0) TrackBig(t);
        OnTick(t);
    }
    internal void InternalFilled(StrategyOrder o) => OnOrderFilled(o);
    internal void InternalSession(DateOnly d) => OnSessionStart(d);
    internal void InternalStop() => OnStop();

    // ----------------------------------------------------------------- metadata

    public static string DisplayName(Type t) => t.GetCustomAttribute<StrategyAttribute>()?.Name ?? t.Name;
    public static string Description(Type t) => t.GetCustomAttribute<StrategyAttribute>()?.Description ?? "";

    /// <summary>Configurable properties (with [Parameter]).</summary>
    public static IEnumerable<PropertyInfo> Parameters(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<ParameterAttribute>() != null)
            .OrderBy(p => p.DeclaringType == typeof(Strategy) ? 1 : 0)
            .ThenBy(p => p.MetadataToken);

    public static string ParameterName(PropertyInfo p) => p.GetCustomAttribute<ParameterAttribute>()?.Name ?? p.Name;

    /// <summary>Current values of the parameters.</summary>
    public Dictionary<string, object?> GetParameters() => Parameters(GetType()).ToDictionary(p => p.Name, p => p.GetValue(this));

    /// <summary>Assigns parameters by name (it converts numeric types).</summary>
    public void SetParameters(IReadOnlyDictionary<string, object?> values)
    {
        foreach (var p in Parameters(GetType()))
        {
            if (!values.TryGetValue(p.Name, out var v) || v == null) continue;
            try
            {
                object converted = v switch
                {
                    System.Text.Json.JsonElement je => ConvertJson(je, p.PropertyType),
                    _ when p.PropertyType.IsEnum => Enum.Parse(p.PropertyType, v.ToString()!),
                    _ => Convert.ChangeType(v, p.PropertyType, System.Globalization.CultureInfo.InvariantCulture),
                };
                p.SetValue(this, converted);
            }
            catch
            {
                // incompatible value: the current one is kept
            }
        }
    }

    private static object ConvertJson(System.Text.Json.JsonElement je, Type t)
    {
        if (t == typeof(int)) return je.GetInt32();
        if (t == typeof(long)) return je.GetInt64();
        if (t == typeof(double)) return je.GetDouble();
        if (t == typeof(decimal)) return je.GetDecimal();
        if (t == typeof(bool)) return je.GetBoolean();
        if (t.IsEnum) return Enum.Parse(t, je.ValueKind == System.Text.Json.JsonValueKind.String ? je.GetString()! : je.GetInt32().ToString());
        return je.GetString() ?? "";
    }
}

/// <summary>A big market order remembered by <c>TrackBigTrades</c>: when, at what average price, how many contracts, and which side hit.</summary>
public sealed class BigTradeLevel
{
    public DateTime Time { get; }
    public decimal Price { get; internal set; }
    public decimal Volume { get; internal set; }
    /// <summary>True if the aggressor bought.</summary>
    public bool Buy { get; }
    /// <summary>Price has traded back to the level since the order.</summary>
    public bool Touched { get; internal set; }

    internal BigTradeLevel(DateTime time, decimal price, decimal volume, bool buy)
    {
        Time = time;
        Price = price;
        Volume = volume;
        Buy = buy;
    }
}
