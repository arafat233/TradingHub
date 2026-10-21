[← Back to the index](README.md)

# C# strategies

A Trading Hub strategy is a C# class that inherits from `Strategy`. The same class, unchanged, runs in the backtest, in
the optimization and the walk-forward, placed on a chart and live on an account. Here is the complete API and two
examples that compile as they are.

## Where they live and how they are compiled

- Each strategy is a `.cs` file in `~/Documents/Trading Hub/Strategies`. The folder opens from the strategy menu in
  **Backtesting** («Open strategies folder»), from the **Code** menu or from **Settings → Data**.
- With the folder empty, Trading Hub drops in four examples: *Moving average cross*, *Range breakout*, *Delta
  absorption* and *VWAP reversion*. The app compiles them itself with Roslyn, in memory: you need nothing else.
- They are compiled when you click **Compile** (⌘B) or **Compile and test**, when you open them in the editor, when
  Claude finishes editing them and when you are about to use one that hasn't been compiled yet. **Save** (⌘S) only
  writes the file: if you changed the code, compile before running a backtest, because the last compiled version is
  the one used.
- You need a public, non-abstract class that inherits from `Strategy`, with a parameterless constructor and
  `using TradingHub.Strategies;` (`System`, `System.Linq` and `System.Collections.Generic` are already there). Use one
  per file, with the same name: the backtest history is stored under the class name and, if you rename it, a new one
  starts.
- Compiling applies the same adaptations as for indicators (ATAS namespaces and the `TRADINGHUB` and `CROSS_PLATFORM`
  symbols): see [Indicators](indicators.md).

## Parameters

- `[Strategy("Display name", Description = "…")]` on the class: the name and description the app shows.
- `[Parameter("Name", Description = "…")]` on a public property with `get` and `set`: it appears in the parameter form
  of the backtest, of Live and of the chart. Types: `int`, `long`, `double`, `decimal`, `bool`, enums and `string`.
- `[Optimize(min, max, step)]`, on top of that: it goes into the optimization and the walk-forward already ticked (you
  can still tick any numeric parameter by hand).

Every strategy ships with these management parameters, which you can set in the constructor:

| Property | In the app | Default | What it does |
|---|---|---|---|
| `Quantity` | Quantity | 1 | Quantity of each order if you don't pass another |
| `StopLossTicks` | Stop loss (ticks) | 0 | Automatic stop N ticks from the average price |
| `TakeProfitTicks` | Take profit (ticks) | 0 | Automatic target N ticks away |
| `TrailingStopTicks` | Trailing stop (ticks) | 0 | A stop that follows price N ticks behind |
| `RegimeFilter` | Regime filter | All | Only opens in certain market conditions |
| `MinVolatilityRank`, `MaxVolatilityRank` | Minimum / maximum volatility (percentile) | 0 and 100 | Only opens at that volatility |

## Life cycle

| Method | When it is called |
|---|---|
| `OnInit()` | At startup. Create the indicators here (and set `UseFootprint = true` if you use volume by price). |
| `OnBar(Bar bar)` | When each candle closes. It is the normal place to decide. |
| `OnTick(Trade trade)` | On every market trade. It is slower. It needs `using TradingHub.Market;` (`Trade` has `Time`, `Price`, `Volume` and `Side`). |
| `OnOrderFilled(StrategyOrder order)` | When an order is executed. In the backtest it also reports the automatic stops and targets; live, only the orders the strategy sent. |
| `OnSessionStart(DateOnly session)` | When each new session starts (it is not called for the data's first session). |
| `OnStop()` | When the backtest finishes or the strategy is stopped. |

On each candle the market context and the indicators are updated first, and then `OnBar` is called. A candle is
considered closed by the first trade of the next one, and a market order sent in `OnBar` fills with that trade (from
`OnTick`, with the following one). The local time (`LocalTime`, `TimeBetween`, each session's day) comes from the
backtest's **time zone and session start**; live, from those of the strategy's last backtest, and on a chart, from
**Settings → Charts' time zone** with the session at 0 h (if that says «This Mac's time», UTC is used).

## Candle and market data

- `Bars[0]` is the last closed candle and `Bars[1]` the previous one (`Bars.Count`; `Bars.At(i)` counts from the
  first); `Bar` is the last closed one, `FormingBar` the one being formed and `CurrentBar` its number, from 0.
- Each `Bar` carries `Time` (open, UTC), `CloseTime` (its last trade, UTC), `Open`, `High`, `Low`, `Close`, `Volume`,
  `BuyVolume` and `SellVolume` (aggressors against the ask and against the bid), `Delta`, `MaxDelta`, `MinDelta`,
  `Trades`, `IsBullish`, `IsBearish`, `Range`, `Body`, `Typical` and `Index`. `PocPrice` (the price with the most
  volume) and `Levels` (buy and sell volume at each price) need `UseFootprint = true` in `OnInit`.
- `Price` (last price), `Time` (market time, UTC), `LocalTime` (the same time in the session's zone), `TickSize`,
  `Instrument` (`Symbol`, `TickSize`, `PointValue`…), `Period` and `IsBacktest`.
- Position: `Position` (positive = long, negative = short), `IsFlat`, `IsLong`, `IsShort`, `AveragePrice`, `OpenPnL`,
  `NetProfit` (realized result) and `ActiveOrders`.

## Orders

| Method | What it does |
|---|---|
| `EnterLong(qty?, name?)` / `EnterShort(…)` | Enters at market. If it was on the other side, it flips the position; if it was already on that side, it does nothing. |
| `Buy(qty?, name?)` / `Sell(…)` | Market order. |
| `BuyLimit(price, qty?, name?)` / `SellLimit(…)` | Limit order. |
| `BuyStop(price, qty?, name?)` / `SellStop(…)` | Stop order: it triggers on reaching the price and goes out at market. |
| `ClosePosition(name?)` | Cancels the pending orders and closes the position at market. |
| `CancelAll()` · `Cancel(order)` · `Modify(order, price)` | Cancels every pending one, cancels one or changes its price (limits and stops). |

Without a quantity, `Quantity` is used, and prices are rounded to the tick. Each method returns a `StrategyOrder` with
`Id`, `Side`, `Type`, `Quantity`, `Price`, `Name`, `State` (`Active`, `Filled` or `Canceled`), `IsActive`, `FillPrice`,
`FillTime` and `IsProtective` (automatic stop or target). The name of the closing order appears under «Exit by» in the
backtest and, live, goes into the order's comment. How fills are simulated is covered in
[Backtesting](backtesting.md).

## Automatic stop, target and trailing

With `StopLossTicks` or `TakeProfitTicks` greater than 0, every time the position changes a stop and a target are placed
at that distance from the average price, tied together as OCO (if one executes, the other is cancelled).
`TrailingStopTicks` makes the stop follow price when it moves in your favour, never backwards; if `StopLossTicks` is 0,
the stop starts at that distance. `ClosePosition` and `CancelAll` don't touch these orders: they cancel themselves once
the position is flat. Live they are real orders on the account (Trading Hub handles the OCO) and the trailing moves the
stop at most once per second.

## Indicators

They are created in `OnInit` and stored in fields; Trading Hub updates them on its own before each `OnBar`.

| Method | What it is |
|---|---|
| `Sma(n, source)` / `Ema(n, source)` | Simple / exponential average (the exponential one starts from the simple) |
| `Rsi(n = 14, source)` · `Atr(n = 14)` · `StdDev(n, source)` | RSI from 0 to 100 · average true range in price · standard deviation |
| `Highest(n, source = High)` / `Lowest(n, source = Low)` | High / low of the last n candles, the last one included |
| `Bollinger(n = 20, k = 2, source)` | Average (`Middle`) with `Upper`, `Lower`, `UpperAgo(i)` and `LowerAgo(i)` |
| `Macd(12, 26, 9)` | MACD with `Signal` and `Histogram` |
| `Vwap()` · `CumulativeDelta(resetEachSession = true)` | Session VWAP · cumulative delta |
| `Custom("name", bars => value)` | Your own series, computed on each candle |

`source` is a `PriceSource`: `Close` (the default), `Open`, `High`, `Low`, `Typical`, `Median`, `Volume` or `Delta`.
Each indicator is a series: `ind[0]` is the last closed candle's value and `ind[1]` the previous one; it also has
`Value`, `Count` and `IsReady` (it already has enough candles), and it can be used directly as a `decimal`. Since
`Highest` and `Lowest` include the last candle, for a breakout compare the close against `[1]`. Prices and volumes are
`decimal`: convert `double` values with `(decimal)`.

## Helpers

- `CrossAbove(a, b)` / `CrossBelow(a, b)`: series `a` crossed above / below `b` (a series or a number) on the last
  candle. `Ticks(n)`: n ticks in price. `Round(price)`: rounds to the tick.
- `TimeBetween(from, to)`: whether the session's local time is between two `TimeSpan`s (if `from` is larger, it crosses
  midnight). `Log("text")`: goes to the backtest's **Log** (up to 5,000 lines, UTC time) and to **Activity** live.

## Drawings on the chart

They show up when you put the strategy on a chart, over the history and live. Times are UTC (like `bar.Time`) and the
colors are `"#RRGGBB"`, `"#AARRGGBB"` or a name (`green`, `red`, `blue`, `cyan`, `yellow`, `orange`, `purple`, `pink`,
`gray`, `white`); with no color the strategy's own is used. Repeating an id replaces the drawing (handy for stretching a
range candle by candle). There is a cap of 3,000 drawings (the oldest ones go) and of 20 `Plot` lines.

| Method | Draws |
|---|---|
| `DrawBox(id, from, price1, to, price2, color, fillOpacity = 15, text)` | Rectangle |
| `DrawLine(id, t1, p1, t2, p2, color, width, style)` | Line (`LineStyle.Solid`, `Dashed` or `Dotted`) |
| `DrawHorizontalLine(id, price, from, color, width, style, text)` | A level out to the right edge |
| `DrawArrowUp` / `DrawArrowDown(id, time, price, color, text)` | An arrow with its tip at the price |
| `DrawMarker(id, time, price, shape, color, text)` | `MarkerShape.Circle`, `Square`, `Diamond`, `Cross`, `ArrowUp` or `ArrowDown` |
| `DrawText(id, time, price, text, color)` | Text |
| `Plot("name", value, color, width, style)` | A continuous line candle by candle; a `null` value breaks it |
| `RemoveDrawing(id)` / `ClearDrawings()` | Deletes one or all of them |

## Market context and the context filter

Each strategy measures, using closed candles only, how the market is behaving: with the hourly closes of the last 24
hours or, if its candles are an hour or longer, with its last 25 candles.

| Property | What it is |
|---|---|
| `MarketTrend` | Trend efficiency: \|net move\| / total distance travelled (0 = pure range, 1 = clean trend; random gives ~0.2) |
| `Regime` | `Range` (up to 0.15), `Mixed`, `Trend` (from 0.35) or `Unknown` with no data |
| `MarketDirection` · `MarketChange` | 1 if it rose and −1 if it fell (bullish or bearish) · net move in % |
| `MarketVolatility` · `VolatilityRank` | Standard deviation of the hourly returns, in % · its percentile over the last 30 days (0 = the lowest) |
| `ContextAllowsEntries` | Whether the context filter allows opening right now |

They can be `null` while history is missing (7 closes; the percentile, at least a day). The backtest and the live
strategy start with whatever hourly closes from the previous month are stored (live, also with the exchange's candles),
so the context usually exists from the very first candle. The **context filter** needs no code: it is the `RegimeFilter`,
`MinVolatilityRank` and `MaxVolatilityRank` parameters. `RegimeFilter` accepts any combination of range, mixed, uptrend
and downtrend: `All`, `RangeOnly`, `NoTrend`, `TrendOnly`, `NoRange`, `UpTrendOnly`, `DownTrendOnly`, `MixedOnly`,
`RangeOrUpTrend`, `RangeOrDownTrend`, `MixedOrUpTrend`, `MixedOrDownTrend`, `NoDownTrend`, `NoUpTrend` and `NoMixed`.
Outside that context the strategy neither opens nor grows positions: only the part of each order that closes what is
open goes through and the rest comes back cancelled («Context filter: not opening trades right now» in the log). Exits
always go through and, with no data to judge on, the filter does not block. Live it is changed without restarting: see
[Live](live.md).

## Example 1: moving average cross with a time window

```csharp
using TradingHub.Strategies;

[Strategy("Moving average cross with hours", Description = "Long when the fast average crosses above the slow one and short the other way round, only inside the time window.")]
public class MaCrossHours : Strategy
{
    [Parameter("Fast average"), Optimize(5, 30, 5)] public int Fast { get; set; } = 10;
    [Parameter("Slow average"), Optimize(20, 100, 10)] public int Slow { get; set; } = 40;
    [Parameter("From (local hour)")] public int FromHour { get; set; } = 9;
    [Parameter("To (local hour)")] public int ToHour { get; set; } = 16;

    private Ema _fast = null!, _slow = null!;

    // automatic stop 40 ticks from the average price and target at 80, in OCO
    public MaCrossHours() { StopLossTicks = 40; TakeProfitTicks = 80; }

    protected override void OnInit() { _fast = Ema(Fast); _slow = Ema(Slow); }

    protected override void OnBar(Bar bar)
    {
        if (!_slow.IsReady) return;
        Plot("Fast", _fast[0], "cyan"); // both averages on the chart, candle by candle
        Plot("Slow", _slow[0], "orange");

        // outside the time window it holds no position and opens nothing
        if (!TimeBetween(new TimeSpan(FromHour, 0, 0), new TimeSpan(ToHour, 0, 0)))
        {
            if (!IsFlat) ClosePosition("End of the window");
        }
        else if (CrossAbove(_fast, _slow)) EnterLong(); // if it was short, it flips the position
        else if (CrossBelow(_fast, _slow)) EnterShort();
    }
}
```

## Example 2: opening range breakout

It measures the range of the first minutes after the open and trades it with a stop order on each side. Try it on an
index future with the session zone set to **New York** (9:30 open).

```csharp
using TradingHub.Strategies;

[Strategy("Opening range breakout", Description = "Measures the range of the first minutes and enters with stop orders when it breaks. Closes everything at the closing hour.")]
public class OpeningRangeBreakout : Strategy
{
    [Parameter("Opening hour (local)")] public int OpenHour { get; set; } = 9;
    [Parameter("Opening minute")] public int OpenMinute { get; set; } = 30;
    [Parameter("Range minutes"), Optimize(15, 60, 15)] public int RangeMinutes { get; set; } = 30;
    [Parameter("Closing hour (local)")] public int CloseHour { get; set; } = 16;
    [Parameter("Margin over the range (ticks)"), Optimize(0, 8, 2)] public int Margin { get; set; } = 2;

    private DateOnly _day;
    private DateTime _rangeStart;
    private decimal _top, _bottom;
    private bool _hasRange, _ordersPlaced;
    private StrategyOrder? _buy, _sell;

    // with no StopLossTicks, the stop starts 60 ticks away and then follows the price
    public OpeningRangeBreakout() => TrailingStopTicks = 60;

    protected override void OnBar(Bar bar)
    {
        // candle open in local time: Time is UTC and LocalTime, the same time in the session's zone
        DateTime opened = bar.Time + (LocalTime - Time);
        var open = new TimeSpan(OpenHour, OpenMinute, 0);
        var rangeEnd = open + new TimeSpan(0, RangeMinutes, 0);
        var close = new TimeSpan(CloseHour, 0, 0);
        var now = LocalTime.TimeOfDay;

        var day = DateOnly.FromDateTime(opened);
        if (day != _day) // new day: nothing is left over from the previous one
        {
            CancelAll();
            if (!IsFlat) ClosePosition("Day close");
            (_day, _top, _bottom, _hasRange, _ordersPlaced) = (day, decimal.MinValue, decimal.MaxValue, false, false);
        }

        // 1) the range: the candles that open between the open and the end of the range
        if (opened.TimeOfDay >= open && opened.TimeOfDay < rangeEnd)
        {
            if (!_hasRange) _rangeStart = bar.Time;
            _hasRange = true;
            _top = Math.Max(_top, bar.High);
            _bottom = Math.Min(_bottom, bar.Low);
            DrawBox($"range-{day:yyyyMMdd}", _rangeStart, _top, bar.CloseTime, _bottom, "blue", 15, "range");
        }

        // 2) once the range is closed, one stop order on each side (just once per day)
        if (_hasRange && !_ordersPlaced && now >= rangeEnd && now < close && IsFlat)
        {
            _buy = BuyStop(_top + Ticks(Margin), name: "Break up");
            _sell = SellStop(_bottom - Ticks(Margin), name: "Break down");
            _ordersPlaced = true;
            Log($"Day's range: {_bottom} to {_top}");
        }

        bool levels = _ordersPlaced && now < close; // the range levels, while the day lasts
        Plot("Top", levels ? _top : (decimal?)null, "green");
        Plot("Bottom", levels ? _bottom : (decimal?)null, "red");

        // 3) at the closing hour nothing is left open
        if (now >= close) { CancelAll(); if (!IsFlat) ClosePosition("Day close"); }
    }

    protected override void OnOrderFilled(StrategyOrder order)
    {
        if (order != _buy && order != _sell) return;
        var other = order == _buy ? _sell : _buy; // it entered on one side: the other is cancelled
        if (other is { IsActive: true }) Cancel(other);
        if (order.Side == OrderSide.Buy) DrawArrowUp($"in-{order.Id}", order.FillTime, order.FillPrice, "green", "breakout");
        else DrawArrowDown($"in-{order.Id}", order.FillTime, order.FillPrice, "red", "breakout");
    }
}
```

`bar.Time + (LocalTime - Time)` converts the candle's open into the session's local time, so the range doesn't depend on
when the next candle's first trade arrives. The two stop orders sit waiting and, when one executes, `OnOrderFilled`
cancels the other; the trailing and the day close protect the position.

## The editor (Backtesting → Code)

Top left you pick the strategy or create one with **New strategy…** (templates: Moving average cross, Range breakout,
Delta absorption, VWAP reversion or Empty); next to it you see «Compiled · N ms» or the number of errors. The list has a
search box and a **Favourites** section at the top: each strategy's star adds or removes it, without closing the list.
The editor highlights C# and lists the errors below (clicking one jumps to the line). Buttons: **Save** (⌘S),
**Compile** (⌘B), **Compile and test** (compiles and runs the backtest), **Reference** (an API summary) and the bin,
which deletes the file and keeps its history. On the right is the Claude panel: whatever it writes appears in the
editor.

## Letting Claude write it

In the Claude panel you describe what you want, for example «a breakout of the first 30 minutes' range with an ATR
stop». Claude Code creates or edits the file in the strategies folder, Trading Hub compiles it when it finishes and, if
it compiles, offers you **Test in a backtest**; if not, **Ask it to fix them**. With **Lab** enabled it also runs
backtests, optimizations and walk-forwards on its own and leaves a report. The details are in
[Claude](claude-assistant.md).
