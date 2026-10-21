[← Back to the index](README.md)

# Backtesting

The **Backtesting** section (⌘2) gathers everything to do with testing. At the top you pick the strategy and below you
have the **Code**, **Backtest**, **Optimization**, **Walk-forward**, **History**, **Portfolio** and **Reports** tabs.
How a strategy is written is covered in [Strategies](strategies.md); what runs in real time, in [Live](live.md).

## Choosing the data

The backtest walks, one by one, through the real trades that are stored: whatever was recorded while you had charts open
and whatever you downloaded or imported in [Data](data.md). With nothing stored, the list is empty.

- **Instrument (stored data)** shows `SYMBOL · source · N days`, and below it the range and how much space it takes.
- Futures that also have [Databento](data.md#databento) history appear three times:

  | Entry | What it uses |
  |---|---|
  | **Rithmic + Databento** | The combined one: Rithmic leads and Databento only fills the minutes where Rithmic has no trades |
  | **Rithmic only** | Only what was recorded or downloaded from Rithmic |
  | **Databento** | Only what was downloaded from Databento |

  The same goes for Tradovate («Tradovate + Databento», «Tradovate only»).
- **From** and **To** (the last day is included in full), with the quick ranges **1 week**, **1 month**, **3 months**,
  **6 months**, **1 year** and **All**, counted backwards from the last stored day (All starts at the first).
- When you change instrument the range jumps to the last 7 stored days, and the commission % to 0.04 if it is crypto
  (Binance or Bybit) or to 0 otherwise.

**Candle period**: a code like `M5`. There are buttons for `M1`, `M5`, `M15`, `H1`, `H4`, `D1`, `Tick500` and
`Range20`. Case doesn't matter and it accepts the chart bar's form too: `d1`, `D`, `1D` and `Daily` are the same thing,
just like `5m` and `M5`, `1h` and `H1`, or `Weekly` and `W1`.

| Code | Candle type |
|---|---|
| `S30`, `M5`, `H1`, `D1` or `Daily`, `W1` or `Weekly`, `Mo1` or `Monthly` | Time-based (seconds, minutes, hours, days, weeks, months) |
| `Tick500` | Every 500 trades |
| `Volume1000` | Every 1,000 of volume |
| `Range20` | Every time the range exceeds 20 ticks |
| `Delta300` | When the candle's delta reaches ±300 |

Renko is not available in backtests.

## Execution: costs and rules

| Field | Default | What it does |
|---|---|---|
| Starting capital | 10,000 | The base for the % return, the % drawdown and the Sharpe |
| Commission % per side | 0.04 in crypto, 0 otherwise | A percentage of the notional (quantity × price × point value) on each entry and each exit |
| Fixed commission per unit | 0 | Per contract or unit and per side; it is added to the percentage one (in futures, use this one) |
| Slippage (ticks) | 1 | Ticks against you on market orders and on stops |
| Limit orders | Only if price trades through | The alternative is «As soon as it touches (optimistic)» |
| Session zone and start (h) | UTC, 0 | They define `LocalTime`, `TimeBetween`, each session's day (VWAP, cumulative delta, `OnSessionStart`) and the hourly statistics |

The available zones are UTC, Buenos Aires, New York, Chicago (CME) and London. The **Parameters** card holds the
strategy's own and, separately, the position-management ones. What you configure is saved per strategy: those parameters
are the ones the strategy later picks up when placed on a chart, and [Live](live.md) also uses the candles and the
session.

### How orders are filled

| Order | It fills |
|---|---|
| Market | With the trade following the order, plus or minus the slippage. If `OnBar` sent it, with the first trade of the next candle. |
| Limit | At the limit price when a trade goes through it (or touches it, in optimistic mode). If price was already on the other side when it was sent, it fills like a market order, never worse than the limit. |
| Stop | When a trade reaches the price, at that trade's price plus or minus the slippage (with a gap, worse). |
| Automatic stop and target | As a stop and as a limit, tied together as OCO. |

No order fills with the trade that triggered it. At the end of the range, the open position is closed at the last price
(«End of the backtest»). The result is in the instrument's currency: price difference × quantity × point value (on NQ,
20 per point). The market context starts with the previous stored month, as it does live.

## Results

Each backtest is kept in the **History** with the exact code that was used. At the top you see **Net result** (and its %
of capital), **Profit factor**, **Winners**, **Max drawdown** (as a % and with the recovery factor), **Sharpe** (and
Sortino) and **Expectancy**, plus the equity curve. Below, five tabs:

- **Trades**: number, side, entry and exit with their price (in your Mac's time), quantity, net result, MAE and MFE
  (the most it went against and in favour), candles and «Exit by» (the name of the order that closed it).
- **Metrics**: 28 numbers, among them commissions, return, average per trade, streaks, average candles, time in the
  market, the result of longs and shorts, simulated ticks and candles.
- **By market regime**, **Monte Carlo** and **Log** (whatever the strategy wrote with `Log`, in UTC time, up to 5,000
  lines), explained below.

| Metric | How it is computed |
|---|---|
| Profit factor | Gross profit / gross loss (∞ if there were no losses) |
| Winners | Trades with a net result greater than 0, over the total |
| Expectancy | % winners × average win + % losers × average loss |
| Maximum drawdown | The largest fall from a peak of the equity curve (including what is open, at each candle's close); the % is against that peak |
| Recovery factor | Net result / maximum drawdown |
| Sharpe and Sortino | Mean of the daily returns / their standard deviation (Sortino only counts the negative days), × √252, or × √365 in crypto |

### By market regime

Each trade records what the market looked like on entry, with the same measures as the strategies' context: range,
mixed, uptrend or downtrend (the efficiency of the last 24 hours) and low, normal or high volatility (up to the 33rd
percentile of the last month, between 33 and 67, or from 67 up). The table gives trades, net, profit factor, % winners
and average for each condition, and flags «small sample» when there are very few.

Below it you pick conditions and click **Re-run only in these conditions**. It is not a slice of the previous backtest:
it is a new one with the context filter, where the strategy decides on each candle with what was known at that point
(exits always go through), just like live. At the top the two runs are compared. Careful: if you pick the conditions by
looking at these very results, the filter is fitted to that data; validate it with walk-forward or on another period.

### Monte Carlo

**Simulate 1,000 random orderings** reshuffles the trades 1,000 times and measures each path's drawdown: the typical one
(median), the one of 1 in every 20 paths (95th percentile), the extreme one (99th percentile) and the worst seen, plus
the **risk of ruin** (paths with a drawdown of at least 50 % of the capital). The chart shows 40 of those paths.

## History

You filter by **All**, **Backtests**, **Optimizations**, **Walk-forward** or the star (favourites). Each run carries its
metrics, the curve, the parameters and the configuration, with an editable note. Buttons: star, delete, **View code**
(the exact version), **Report** (builds the professional report in Reports) and **Use parameters** (copies them into the
backtest and runs it). It is stored in `~/Library/Application Support/Trading Hub/Backtests`, one folder per strategy.

## Optimization

1. In **What to test** you tick the parameters to sweep with their minimum, maximum and step (the ones with
   `[Optimize]` come ticked already). You can see how many combinations there are: over 3,000 it warns you and over
   20,000 it won't run.
2. **Sort by**: Result / drawdown (the default), Net result, Profit factor (capped at 100), Sharpe, Sortino, Recovery
   factor, Expectancy or % winners.
3. **Minimum trades** (10): combinations with fewer are left without a metric, so luck doesn't get rewarded.
4. **Optimize**. It uses the data and the execution settings from the Backtest tab and tries every combination (a grid),
   in parallel across all cores but one.

**Optimizing the timeframe.** At the end of **What to test** sits **Candle period**: you tick it and pick the periods to
try (`M1`, `M5`, `M15`, `M30`, `H1`, `H4` and `D1`). Each parameter combination is tested on every ticked period: the
tick-by-tick history is loaded once and the candles are built for each period. In the table each row states its period,
and **Use in Backtest** copies that too. In the walk-forward the period is chosen in each window like any other
parameter, and the overfitting measurement counts each period as one more trial.

The table shows up to 500 rows sorted by the metric. **Use in Backtest** (or a double click) copies the parameters and
runs the backtest. The best row is usually a lucky peak: validate with walk-forward. Optimizations and walk-forwards are
also kept in the History.

## Walk-forward

It optimizes on a window (in sample), tests whatever was chosen on the following days, which the optimization never saw
(out of sample), moves on and repeats. What counts is the chained out-of-sample result.

- **In sample** (14 days) and **Out of sample** (7 days). **Rolling**: the optimization window has a fixed length and
  advances. **Anchored**: it always starts on the first day and grows. Before running you see how many windows come out.
- It uses the parameters, the metric and the minimum trades from Optimization, and the data and costs from Backtest.
- **Parameter search**: **Automatic** (a grid up to 300 combinations, Bayesian beyond that), **Full grid** or
  **Bayesian**. The Bayesian one measures 80 combinations per window.

Results: **Out of sample** (net, trades and profit factor), **WF efficiency**, **Winning windows**, **Out-of-sample
drawdown**, **Overfitting** and **Paths (CPCV)**, the out-of-sample curve with each window marked (W1, W2…) and a table
per window with its dates, the chosen parameters, the in- and out-of-sample net, and its efficiency.

### How it works inside

1. **One run per combination.** Each combination is run once over the whole history, with warm indicators and the market
   context as it is live, and each window is measured by slicing that result (the trades that entered in the window and
   the equity hour by hour). No window uses data from after it.
2. **Tick compression.** Consecutive trades at the same price, on the same side and in the same second are merged into
   one with the volume added up. Time and range candles come out identical (open, high, low, close, volume, delta and
   footprint) and orders fill at the same prices, with far fewer events. It is used in the optimization and the
   walk-forward only with time or range candles and if the strategy does not use `OnTick`; the only thing that changes
   is `Bar.Trades`, each candle's trade count.
3. **Grid or Bayesian.** The grid tries everything and lets you see the whole map. The Bayesian one (TPE,
   *Tree-structured Parzen Estimator*) starts with well-spread points (a Latin hypercube of between 8 and 30, a quarter
   of the budget) and then proposes in batches: it separates the best 20 % of what was measured, estimates for each
   parameter which values are frequent among the good ones (`l(x)`) and among the bad ones (`g(x)`), and tries where
   `l(x)/g(x)` is highest.
4. **A plateau, not a peak.** In each window the choice is made by the average with the neighbours: for each
   combination, `score = (sum of its metric and that of the neighbours one grid step away + median) / (neighbours + 2)`.
   The median pulls combinations with few measured neighbours towards the middle, and a neighbour with no metric counts
   worse than the worst one. A combination that wins alone among losing neighbours is almost always luck; a whole zone
   that wins is not.
5. **Efficiency.** `out-of-sample daily profit / in-sample daily profit`, summing every window (and the same per
   window). Above 0.5 is a good sign; 0 if the in-sample didn't make money.
6. **PBO with CSCV** (probability of backtest overfitting with combinatorially symmetric cross-validation). With the
   daily result of every combination that was run, the history is split into 10 blocks (fewer if there are few days) and
   every way of choosing half as «in» is tried (252): in each one the best Sharpe there is taken and its rank `r` in the
   other half is looked at (1 = the worst of `N`). With `ω = r / (N + 1)`, it lands in the bottom half if
   `ln(ω / (1 − ω)) ≤ 0`; **PBO** is the proportion of times that happens. Trading Hub also shows how often the chosen
   one **made money** in the other half (the **Overfitting** card: green with under 20 % losses, red from 50 %),
   because a high PBO with everything winning is a plateau, not a problem. It needs 4 combinations and 20 days.
7. **CPCV with embargo** (combinatorial purged cross-validation). The history is split into 8 consecutive groups (fewer
   if there are few days); for each pair of test groups (28 pairs) the combination is chosen by plateau using the
   remaining days, dropping those within the **embargo** of the test groups (a trade's typical duration, between 1 and
   5 days), and it is measured on those two groups. Since each group is a test group in 7 pairs, 7 complete
   out-of-sample paths come out, each with its net, Sharpe and drawdown. The **Paths (CPCV)** card is green if at least
   70 % win and the median is positive, and red if the median doesn't win.

## The deflated Sharpe and the validation

Trying many variants makes the best one look good by chance. The **deflated Sharpe** measures the probability that the
edge is real once you discount how many things were tried. With the out-of-sample per-trade Sharpe (`SR` = mean /
standard deviation of the net result), its skew `γ3`, its kurtosis `γ4` (3 in a normal) and `n` trades:

```
PSR(SR*) = Φ( (SR − SR*) · √(n − 1) / √(1 − γ3·SR + (γ4 − 1)/4 · SR²) )
SR*      = √(max(V, 1/n)) · E[max Z_N],   E[max Z_N] ≈ (1 − γ)·Φ⁻¹(1 − 1/N) + γ·Φ⁻¹(1 − 1/(N·e)),   γ ≈ 0.5772
```

`N` is the number of configurations tried, `V` the variance of their Sharpes and `Φ` the cumulative normal: `SR*` is the
Sharpe that the best of `N` edgeless strategies would show out of luck.

The validation is a set of fixed rules (`ValidationGate` in `TradingHub.Strategies`) that judge the out-of-sample data
only:

| Test | Passes | Doubtful | Fails |
|---|---|---|---|
| Sample | ≥ 30 trades | | fewer |
| Result | net > 0 | | net ≤ 0 |
| Profit factor | ≥ 1.2 | ≥ 1.0 | < 1.0 |
| Walk-forward efficiency | ≥ 0.5 | ≥ 0.3 | < 0.3 |
| Consistency (winning windows) | ≥ 60 % | ≥ 40 % | < 40 % |
| Windows | ≥ 4 | fewer | |
| Stability (spread of the chosen parameters / range width) | ≤ 0.35 | more | |
| Plateau (neighbours of the best one that win) | ≥ 50 % | fewer | |
| Robustness (combinations that win) | ≥ 25 % | fewer | |
| Overfitting (the chosen one loses in the other half) | < 20 % | < 50 % | ≥ 50 % |
| CPCV paths | ≥ 70 % win | fewer | median ≤ 0 |
| Deflated Sharpe | ≥ 90 % | ≥ 50 % | < 50 % |
| Decay (with 30 trades or more) | it holds up | the last third yields less than half of the first, or t ≤ −1.5 | the first made money, the last loses and t ≤ −2 |
| Regimes (groups with at least 8 trades and 15 %) | wins in all of them | loses in one | |

With any «fails» the verdict is **fails**; with any doubtful, **doubtful**; otherwise, **passes**. If it wins out of
sample and only the trade-count-dependent tests fail, the verdict is **needs more sample**, with how many times more
trades would be needed (projecting the deflated Sharpe with the same edge, up to 8 times).

In this version, the Walk-forward tab shows the efficiency, the winning windows, the overfitting and the paths (the last
two with this table's thresholds). The full validation, with the deflated Sharpe, the decay and the regimes, lives in the
library, covered by the tests, ready to use from code (see [Development](development.md)).

## Portfolio

It combines stored backtests from several strategies or instruments: **Add** (you pick the run), **Equal**, **Risk
parity** (each one weighs the inverse of its daily result's volatility) or **Manual** weights, and the capital. It shows
the group's metrics, the curve, the correlation matrix of the daily results (below 0.5 is preferable) and how much the
drawdown drops compared with the sum of the individual ones. **Suggest a combination** searches your strategies' latest
backtests for the combination with a correlation under 0.5 that improves the Sharpe the most (up to 5). **Report**
builds the portfolio's report.

## Reports

Markdown reports with every metric, table and chart: they are built by **Report** from the History or the Portfolio, or
by Claude with the lab (with its verdict and its analysis). They are stored in
`~/Documents/Trading Hub/Strategies/Reports`, shown as a sheet, and **Export PDF / HTML** opens them in the browser to
print them or save them as PDF. See [Claude](claude-assistant.md).
