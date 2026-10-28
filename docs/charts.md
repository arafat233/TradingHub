[← Back to the index](README.md)

# Charts

All of this happens in the **Trading** section (⌘1). Each chart has its drawing bar on the left and its Chart Trader on
the right; the top bar acts on the active chart (the last one you clicked).

## The chart bar

| Control | What it does |
|---|---|
| Link color | Joins the chart to a color group (see [Linking charts](#linking-charts-by-color)). |
| Instrument (⌘K) | Shows the symbol, the price and the change since the session open. Click to pick another. |
| History | How much history to load. |
| Periods | The favourites in sight and a picker with all of them. |
| Footprint template and ticks per row | Footprint mode only. |
| Chart type | Candles, Bars, Line, Heikin Ashi or Footprint. |
| Indicators | Opens the indicators window (⌘I). |
| Icons | Edit footprint, Chart settings, Strategies on the chart and Chart templates. |
| Layout | How many charts to show in the tab. |
| Panels | Shows or hides the right panel, the Chart Trader, the DOM and the tape; it also opens «Configure DOM…». |
| Separate window and **+** | Pulls the chart into a window (⇧⌘D) or opens a new tab (⌘T). |

If the window is narrow, the bar wraps onto two lines.

## Instrument and history

The instrument picker searches the active connections, with columns for symbol, description, price, 24-hour change and
volume. Enter opens it in the active chart and ⌘Enter in a new tab; the star marks favourites.

The history button offers from 30 minutes up to 5 years, «All the history» and «From a date…». Recent data is loaded
tick by tick, with footprint. Older data arrives as candles from the connection (1-minute; over ranges longer than 400
days, 5 or 15 minutes) and those candles have no footprint. Whatever you downloaded in [Data](data.md) is used tick by
tick. When it finishes, the status bar says how far back footprint goes.

## Periods

The favourite periods are on the bar (by default: 1m, 5m, 15m, 1h, 4h and D). The picker next to it has all of them:

| Group | Periods |
|---|---|
| Seconds | 5s, 10s, 15s, 30s |
| Minutes | 1m, 2m, 3m, 5m, 10m, 15m, 30m |
| Hours | 1h, 2h, 4h, 6h, 12h |
| Days | D (daily), W (weekly), Month |
| Ticks | 100, 250, 500 and 1000 ticks: each candle holds that many trades |
| Volume | Vol 100, 500 and 1000: each candle gathers that much volume |
| Range and others | Range 10 and 20 (candles of N ticks of range), Delta 50 and 100 (the candle closes when delta reaches N), Renko 10 |

- **Right-clicking** a period in the picker adds it to or removes it from the bar. The ones on the bar show in color.
- **Custom**: type a code in the field below and hit Enter. Examples: `M3`, `H2`, `S20`, `Tick500`, `Volume100`,
  `Range20`, `Delta75`, `Renko5`.

## Chart types

**Candles**, **Bars**, **Line**, **Heikin Ashi** and **Footprint**. When you switch to footprint, the candles widen so
the numbers fit.

## Footprint

![Chart in footprint mode with the Delta indicator below, the Chart Trader and the right panel](img/footprint.png)

In footprint mode two more controls appear on the bar:

- **Footprint template**: how each candle is built. It ships «Bid x Ask (classic)», «Volume ladder + profile», «Delta
  ladder + volume profile», «Double ladder (bid | ask)», «Double delta ladder», «Volume (cells)», «Delta (cells)»,
  «Bid / ask histogram», «Per-candle volume profile» and «Bid x Ask + profile». Your own ones appear with a star in
  front. The last option, «Edit columns…», opens the editor.
- **Ticks per row**: Auto (it adapts to the zoom) or from 1 to 1000 ticks.

### Footprint editor

It opens with ⇧⌘F, with the columns icon on the bar or from the right-click menu («Edit footprint…»). The changes show
on the chart immediately.

1. Choose a base in **Start from a template**.
2. In **Columns** (left to right inside each candle), add pieces with the Ladder, Profile, Cells, Spacer buttons and
   the candle one, which adds a «Thin candle». Reorder them with the arrows and remove them with the bin.
3. Pick a column and adjust it on the right:
   - **Type**: Thin candle, Coloured cells, Ladder (numbers), Profile (histogram) or Spacer.
   - **Data**: Volume, Delta, Bid (aggressive sells), Ask (aggressive buys), Bid x Ask or Trades.
   - Relative **width**, **Bar direction** (on the profiles), **Highlight** (strong delta, diagonal imbalances, the
     candle's POC or high volume), **Cell colors** (proportional to volume, by delta, bid / ask, heat map, solid or no
     background), text alignment and numbers over the bars.
   - That column's own **Background colors** and **Text colors** (automatic = the theme's).
4. In **General options**: each candle's totals, a box at the POC, marking imbalances, the imbalance ratio and minimum
   volume (3 times by default), the strong-delta threshold, the minimum volume for highlighting and whether the colors
   are proportional to each candle or to everything visible. Three options make a candle easier to read at a glance:
   - **Mark stacked imbalances**: a bar on the candle's right edge where 3 or more rows in a row are imbalanced the same
     way (green = buyers, in the outer lane; red = sellers, next to it). A single imbalance is easy to miss, a stack is not.
   - **Summary under each candle**: two lines at the bottom of the chart. The top one is the candle's delta as a % of its
     volume, on a background that grows with it (green buyers, red sellers). The bottom one shows its volume against the
     visible average (1.5x is half as much again) and a triangle for the POC: up if it is in the top third of the candle,
     down in the bottom third, a diamond in the middle.
   - **Hide small numbers**: *Hide numbers below* drops the numbers of levels under a fraction of the candle's POC volume
     (0.1 = 10 %), and *Hide small numbers when zoomed out* does it automatically when the rows are small. The POC and the
     imbalanced levels always keep their numbers and the colors still show every level.

   These are reading aids: our own tests found no forward edge in the imbalances or the delta (see `research/flow-study`).
5. Enter a **Template name** and click **Save as template** to use it on other charts.

## Moving around the chart

| Action | Result |
|---|---|
| Wheel | Horizontal zoom around the mouse |
| ⇧ + wheel, or sideways swipe | Scrolls in time |
| Wheel over the price axis, or ⌘ + wheel | Vertical zoom |
| Dragging the price axis / double click | Changes the scale / back to auto scale |
| Dragging the time axis / double click | Changes the candle width / back to the latest |
| Bottom-right button | Appears if you scrolled back: returns to the latest |

**Keeping the price in the middle**: the crosshair button in the chart bar («Keep the price in the middle of the chart», also in the chart
settings, *Price* tab) keeps the last price in the vertical middle while the chart follows the live edge: as the price goes up or down the
price scale moves with it, still showing every visible candle. Off, the scale just fits the visible candles. Dragging the price scale or
the chart vertically (manual scale), or scrolling back to the past, suspends it; a double click on the price axis brings the automatic scale back.

**Candle countdown**: for time candles (seconds, minutes, hours, daily…) a small tag under the last price on the axis shows the time left in the
current candle (`04:12`, `1:02:30`, `2d 05h`), counted with the connection's clock (a replay counts its own time). Candles by ticks, range or
volume have no clock, so no countdown. It can be switched off in the chart settings.

The keys from [Getting started](getting-started.md#keyboard-shortcuts) also work and, in the Chart menu, «Auto scale»
(⌥⌘A) and «Go to last price» (⌘End).

## Multiple charts

- **Layouts**: one chart, two (side by side or one above the other), three in columns, one big plus two on the right,
  one big on top plus two below, four (2 × 2) or six (3 × 2). New slots are filled with copies of the active chart on
  other periods. If you choose a smaller layout, the extras are kept in the tab.
- With several charts, each one has a little bar with its link color, the title and buttons to **enlarge it** (it takes
  over the whole tab until you shrink it), **pull it into a separate window** and **close it**.
- **Separate windows**: the chart leaves with its own bar, its drawings and its Chart Trader, and you can move it to
  another monitor. Inside that window, ⌘W closes it, ⌘I opens indicators and ⌘K the search. «Back to the main window»
  returns it to a tab.
- The right-click menu also offers «Move this chart to a new tab».

### Linking charts by color

Charts with the same color (red, green, blue, yellow, purple or orange) share the instrument, the period and the
crosshair. In the color's menu you choose what gets synced; it applies to every group. On joining a group, the chart
takes the instrument and the period from the ones already there.

## Chart settings

The palette icon (or «Chart settings…» in the right-click menu) opens that chart's colors, in tabs: **Background**,
**Candles**, **Axes**, **Price**, **Footprint** and **Trades**. The grid, the candle data, the last-price line, each
session's start and the thickness and dash of the entry-to-exit lines are there too. Each color has a palette, a code
and an opacity; a dot marks the ones you changed. «Reset everything» goes back to the originals and «Apply to every
chart» copies the look to the rest.

From the right-click menu you also control the **Chart legend** (instrument name, candle values and indicator names) and
**Mark my trades**: a triangle on each execution and a line from the entry to the exit of each trade, including from
previous days.

## Drawings

The left bar has: cursor (Esc), lines (horizontal, horizontal ray, vertical, trend, ray and extended), rectangle or
zone, Fibonacci retracement, long or short position, ruler and text. The little arrow below a group shows its variants.
The last button deletes every drawing on the instrument, without asking.

- With ⇧ held, the point snaps to the candle's high, low, open or close.
- Drawings show on every chart of the same instrument and are saved automatically.
- **Double-clicking** a drawing opens its panel: line (color, thickness, style), fill, text, extend, Fibonacci levels,
  exact prices and visibility (hide, show on one period only, lock). «Use as default» stores the style for the next
  ones of that type; there are also «Duplicate» and «Delete».
- The **long or short position** starts with a 1:2 stop and target; with a quantity entered it shows how much you would
  win or lose and marks whether the candles touch the target or the stop.

**Price alerts**: right click → «Price alert at…» creates a dotted horizontal line. On a horizontal line or a horizontal
ray you can enable «Alert when price touches». When price crosses it, it beeps (if alert sounds are enabled in
Settings), notifies in the status bar and the alert switches off.

## Indicators

The **Indicators** window (⌘I) has three columns:

1. **Library**, with a search box. To add one, pick it and click «Add to the chart» or double-click it.
2. **On the chart**: show or hide, move between the price chart and its own panel, and remove.
3. **Settings** for the chosen indicator, with **Templates**: save its settings, apply a saved one and choose which one
   new ones start with.

Included: Moving average, Bollinger Bands, VWAP with deviations, Volume, Delta, Cumulative delta (CVD), Big Trades,
Session volume profile, RSI and MACD. In the chart legend each indicator has buttons to hide it, open its settings and
remove it; double-clicking the name also opens its settings.

Your own C# indicators are written in **Code → Indicator editor…** (⇧⌘E) or with the braces icon in the library. They
live in `~/Documents/Trading Hub/Indicators`, are compiled automatically on save and appear first, in the «My
indicators» group (marked with a star).

## Chart templates

The bookmarks icon (or «Chart templates» in the right-click menu) lets you:

- **Save this chart as a template…**: it stores the style, the footprint, the colors, what is shown and the
  indicators; not the instrument or the period.
- Apply a template to the chart.
- **For new tabs**: copy the active chart or start from a template.
- **Delete a template**.

## DOM and tape

The **DOM** sits in the right panel and follows the active chart. By default it shows buy orders, bid, price, ask, sell
orders and session volume. In its header you can group prices (×1 to ×1000 ticks), show the execution buttons, center on
price, configure it and pull it into a window. **Configure DOM** picks columns (including session delta and what traded
at the bid and the ask), their order and their width, the row height, the font, auto-centring, «Double click to trade»,
the size from which the book is highlighted, the book bars and the colors.

The **Time and sales tape** lists trades as they arrive; executions from the same aggressive order (same millisecond and
side) are added up into one row. With the wheel you scroll back and the list holds still; a click on the header returns
to the latest. How to trade with the DOM is covered in [Trading](trading.md#trading-with-the-dom).

## Liquidity heatmap

The **flame button** in the chart bar opens the order book as a heatmap in its own window (it can go to another monitor).
Time runs to the right and price goes up; the color of each cell is how much was **resting in the book** at that price at
that moment: near-black for little, then blue, yellow, orange and red for the largest orders. On top you see the best
bid and ask (a pale band), the last price (white), **bubbles** for the biggest trades (green when buyers hit the ask, red
when sellers hit the bid), **icebergs**, a ladder on the right (the live book as bars and the volume traded at each price,
buy green / sell red), the volume traded below and a **CVD** line (cumulative delta since the session start, 00:00 UTC).

- **Wheel**: how much time fits on the screen (30 seconds to 30 minutes). **Ctrl + wheel**, or **dragging on the price
  axis**: the height of each price row.
- **Drag** up or down moves the price range; **double click** (or *Follow price*) follows the price again. **Hover** shows a
  crosshair with the price, the time and how much was resting there. *Pause* freezes the picture (the book keeps recording).
- *Contrast* makes smaller resting orders stand out; the checkboxes switch the bid/ask lines, bubbles, volume, icebergs, CVD
  and ladder on and off. The window remembers its size and position.
- **Icebergs**: a level that had far more traded against it than it ever showed (3x the biggest size it displayed, and
  at least 4x the median level) is boxed with the volume it absorbed: green = resting buy, red = resting sell. It is a
  heuristic (stops and fast market makers can look the same).

### The ladder, the boxes, the dials and the tags

These are defined here so you know exactly what each number is (make the rows taller with Ctrl + wheel to see the numbers):

- **Ladder** (right of the price axis, one row per price): **COB** = size resting in the book right now (red asks, green bids);
  **SVP** = volume traded at that price since the session start, buy green / sell red; **CQC** = how the resting size at that
  price changed over the time on screen (+ added, − pulled or filled); **Delta** = session buy minus sell volume at that price.
  The session starts at 00:00 UTC.
- **Market Vol** (box, bottom left): what traded in the last 10 seconds at the ask (buyers) and at the bid (sellers).
  **Avg. Liquidity**: the average size resting on the first 10 levels of each side, averaged over the time on screen.
- **Dials** (under the ladder): *Hidden Bid* / *Hidden Ask* = volume absorbed by the icebergs found on each side; *Hit Bid* /
  *Hit Ask* = what sellers / buyers traded in the window; *Delta / Depth* = the window's delta divided by the average resting
  size (full scale at ±3); *CVD* = the session CVD, scaled to its range in the window.
- **Blocks**: a big level (at least half the "a lot" size) whose size stays within 10% of itself for 20 seconds or more is covered
  by a pale block with its size: a wall that sits there, as opposed to orders that come and go.
- **Tags at the price**: on the last price, `size (n)` = the size of the last trade and how many trades hit that price in the last
  second; on the best bid and best ask, the size resting there.

The *Stats*, *Gauges* and *Blocks* checkboxes switch the boxes, the dials and the blocks on and off.

## Recording the book and replaying it

With **Settings → Record the order book** on (the default), every instrument you open with a live connection also records its
book to `.vdk` files next to its ticks: 4 snapshots a second, 20 levels per side, one file per UTC day (roughly 20-40 MB a day
per instrument; it only applies to instruments opened after you turn it on). Only the app that holds an instrument's recording
lock writes it.

- **Replay**: in the heatmap window, *Replay a recorded day* plays a recorded day back (speed 1x to 60x, a time-of-day
  slider, *Pause* / *Play*). It reads about an hour and a half around the cursor at a time, so a whole day never has to fit in
  memory. Icebergs, bubbles and CVD work in a replay the same way.
- **Backtests and strategies**: a backtest uses the recorded book for the days it has: the strategy gets `Book`,
  `BookImbalance()` and (with `TrackIcebergs`) `Icebergs`; see [Strategies](strategies.md). A day with no recorded book simply
  has `Book == null`. There is no depth history to download, so only days the app recorded are available.

It needs a connection that sends the order book (Rithmic, dxFeed, Databento, Tradovate, Binance, Bybit or the simulator).
Databento sends 10 levels per side and Alpaca and cTrader only the best bid and ask, so their heatmaps are thinner.

## Volume profile

- The **Session volume profile** indicator draws the profile per session, per week or across all the visible history, of
  volume, delta or bid / ask, with POC, value area (70 % by default) and VAH / VAL lines.
- In the footprint, **Profile**-type columns show each candle's profile.

## Replay

From [Data](data.md#replay) you can replay saved days in a new tab, with speed, pause and jumps, and trade with the
simulated account as if it were live.

## Strategies on the chart

The robot icon puts one of your Backtesting strategies on the chart (how they are written is in
[Strategies](strategies.md)). It marks where it enters and exits, over the history and live, and it can stay as a purely
visual marker or execute on an account. It is configured or removed from the chart legend.

Below the settings, **On the chart's history** shows how it did over whatever the chart has loaded:

- **The real result, in large.** It is computed with that market's commission and slippage, the same ones as the
  backtest.
- **A mini equity curve** with its drawdown.
- **The statistics:** trades, % winners, profit factor, drawdown, average per trade, average win and loss, the largest
  win and the largest loss, streaks, Sharpe, Sortino, recovery, exposure, longs and shorts, commissions and candles per
  trade.
- **Which commission and slippage were used.**
