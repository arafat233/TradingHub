[← Back to the index](README.md)

# Getting started

A tour of the main window so you know where everything is. If you haven't installed it yet, start with
[Installation](installation.md).

## The first time

When you open Trading Hub for the first time you land on the **Trading** section with a `SIM-BTC` chart on 5 minutes,
in footprint mode, with the Big Trades and Delta indicators. That instrument comes from the **Simulator**, a simulated
market that works without internet and is there for practice.

Everything you trade goes to the **simulated account**. Nothing moves real money until you enable it by hand,
connection by connection, in [Connections](connections.md).

These connections come pre-created:

| Connection | At the start | What it gives |
|---|---|---|
| Simulator | enabled | simulated SIM-BTC, SIM-ETH, SIM-ES and SIM-NQ |
| Binance Futures | enabled | public crypto data, no account |
| Binance Spot | enabled | public crypto data, no account |
| Bybit | enabled | public data for the USDT perpetuals |
| Rithmic | disabled | CME futures (needs your credentials) |

## The main window

### The sections, in the title bar

Navigation is built into the macOS title bar, next to the window buttons:

| Section | Shortcut | What for |
|---|---|---|
| Trading | ⌘1 | Charts, DOM, time and sales tape and Chart Trader. |
| Account | ⌘5 | Balance, positions, orders, executions, trades and statistics. |
| Backtesting | ⌘2 | C# strategies, backtests, optimization and the Claude chat. |
| Live | ⌘7 | Automated strategies running, portfolios and the «Stop everything» button. |
| Data | ⌘3 | Saved history, downloads, file import, Databento and replay. |
| Connections | ⌘4 | Markets, brokers and accounts. |
| Settings | ⌘, | Appearance, time zone, shortcuts, no-sleep mode and folders. |

With a narrow window (less than 1360 pixels wide) the bar shows only the icons; the name appears on hover. Dragging the
bar moves the window and double-clicking maximizes it.

### To the right of the bar

- **Simulated 0.00 · 10,000.00**: the day's result and the simulated account balance. Click it to go to Account.
- **Sun or moon**: switches to light or dark mode.
- **Connection pill**: the active chart's connection and its status. A click opens a menu with «Reconnect now» and
  «Connections…».

| Status | What it means |
|---|---|
| connected | Data is coming in. |
| connecting… / reconnecting… (attempt N) | It is connecting; after an outage, whatever was missed is backfilled automatically. |
| no data for N min | The connection is still up, but nothing has arrived for a while. |
| market closed · opens (day and time) | Futures outside trading hours: it is not an outage, the data comes back on its own. |
| error / disconnected | Check the connection in Connections. |

If the Mac went to sleep, Trading Hub reconnects on its own when it wakes and backfills whatever was missed.

### The status bar

At the very bottom. On the left you see the latest notice: orders sent, history loads, price alerts and errors (hover to
see it in full). On the right, the active chart's summary: «N trades · N candles · from dd/MM HH:mm», or «Loading
history N%» while it loads.

## The Trading section

![The Trading section with two charts, each one's Chart Trader and the right panel with the DOM and the tape](img/trading.png)

1. **Chart bar**, at the top: instrument, history, periods, chart type, indicators, footprint, colors, strategies,
   templates, layout, panels, separate window and new tab. It is all explained in [Charts](charts.md).
2. **Tabs**: the strip appears once there is more than one. Each tab can hold 1 to 6 charts.
3. **The chart**, with its drawing bar on the left and its own Chart Trader on the right (it folds away with the little
   arrow).
4. **Right panel**: Chart Trader, DOM and time and sales tape, one below the other. The width is changed by dragging the
   edge and each section's height with the splitters. Each section can be hidden with its cross or pulled out into a
   separate window.
5. **Replay bar**, at the bottom: it appears when the active chart is a replay (see [Data](data.md#replay)).

To trade, see [Trading](trading.md).

## The simulated account

- It starts with a balance of 10,000. You change it in **Account → Starting balance**.
- It works with any market: the Simulator or real data from Binance, Bybit, Rithmic and the other connections.
- It fills orders against the real market: market orders at the best bid or ask; limits, when price trades through your
  price or when more volume traded at your price than you had ahead of you in the queue when you sent it; stops, when
  the trigger price trades, and then they fill at market.
- The balance and the history are saved: they don't go back to zero when you close the app. To start from scratch, use
  «Reset the simulated account» in Account.

## Opening a chart

1. In Trading, click the instrument picker (the first button on the chart bar) or hit ⌘K.
2. At the top choose the connection, or **Favourites** to see only the starred ones.
3. Type to search (BTC, ETH, NQ…). The arrow keys move through the list.
4. Enter or a double click opens it in the active chart. ⌘Enter opens it in a new tab.
5. The star on each row marks it as a favourite.

When it opens, the recent history is loaded (how much is up to each connection). To see further back use the history
button on the bar. A new tab opens with ⌘T or the **+** button: it copies the active chart, unless you have chosen a
template for new tabs. ⌘W closes the active chart; at least one tab always remains.

To see real markets with your accounts, add them in [Connections](connections.md).

## The Mac menu

With Trading Hub in front, the macOS menu bar has these menus:

| Menu | Options |
|---|---|
| File | New chart (⌘T), Close chart (⌘W), Find instrument… (⌘K), Save workspace (⌘S), Open data folder |
| Chart | Indicators… (⌘I), Edit footprint… (⇧⌘F), Auto scale (⌥⌘A), Go to last price (⌘End), Layout, Pull chart into a window (⇧⌘D), Panels, Configure DOM… |
| Go | The seven sections, with their shortcuts |
| Code | Indicator editor… (⇧⌘E), Open indicators folder, Recompile indicators, Strategy backtesting…, Live strategies…, Open strategies folder |

The workspace (tabs, charts, layouts and panels) is also saved automatically on close and restored on open.

## The menu-bar icon

Trading Hub leaves an icon in the top right, in the macOS menu bar. Clicking it opens a menu with:

- the active chart's connection;
- **Live strategies (N)**, with each one's size, position and result;
- **Open positions (N)**;
- «Show the live panel…», «Open Trading Hub» and «Quit Trading Hub».

The live panel shows the strategies and the positions in real time, with the same buttons to open or exit.

**Closing is not quitting.** The window's red button hides it and Trading Hub keeps running in the menu bar, with the
strategies working. The icon and this behaviour depend on **Settings → Live strategies → Keep running in the menu bar
when the window is closed**, which is on by default; if you turn it off, the icon disappears and closing the window
closes the app. To quit completely use ⌘Q or «Quit Trading Hub». If the window is hidden, clicking the Dock icon brings
it back.

## Keyboard shortcuts

Beyond the menu ones, **Settings → Keyboard shortcuts** has configurable shortcuts. They work in Trading and in the
separate chart windows, on the active chart. To change one, click its button and press the combination; Esc cancels and
⌫ leaves it unassigned. «Restore the defaults» goes back to the originals.

| Default shortcut | Action |
|---|---|
| ⌥1 to ⌥6 | Period of 1 minute, 5 minutes, 15 minutes, 1 hour, 4 hours and daily |
| ⌥H | Draw a horizontal line |
| ⌥T | Show or hide the chart's Chart Trader |
| ⌃Tab / ⌃⇧Tab | Next / previous tab |

The trading shortcuts (buy, sell, close, reverse, cancel, partials, breakeven and others) ship unassigned on purpose:
everyone picks their own. See [Trading](trading.md#trading-shortcuts).

On the chart these also work:

| Key | Action |
|---|---|
| Esc | Back to the cursor, cancels dragging an order or removes a trade's highlight |
| ⌫ or Del | Deletes the selected drawings |
| Home or End | Back to the latest with auto scale |
| ← / → | Scrolls the chart |
| + / − | Makes the candles bigger or smaller |

## Settings worth a look

- **General**: appearance (dark or light), the charts' time zone and alert sounds.
- **Trading**: «Confirm real orders» (see [Trading](trading.md#safety)).
- **Live strategies**: keep running in the menu bar, resume strategies on open and pick the contract alone (minis or
  micros).
- **No-sleep mode**: the Mac does not sleep while there are live strategies; optionally, it keeps going with the lid
  closed.
- **Data**: «Record incoming live data» and the links to the folders.

## What's next

- [Connections](connections.md): adding markets and brokers.
- [Charts](charts.md): periods, footprint, drawings, indicators and templates.
- [Trading](trading.md): Chart Trader, DOM, orders, stop and targets, and the Account section.
- [Data](data.md): saved history, downloads, import, Databento and replay.
