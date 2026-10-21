[← Back to the index](README.md)

# Settings

**Settings** (⌘,) holds Trading Hub's general preferences. Each change is saved immediately. What belongs to each
connection is in [Connections](connections.md), what belongs to each chart in [Charts](charts.md), each instrument's
stop and targets in [Trading](trading.md) and each portfolio's risk agent in [Live](live.md).

## General

| Option | Default | What it does |
|---|---|---|
| Appearance | Dark | **Dark** (black) or **Light** (white) across the whole platform. It is also switched with the sun-and-moon button on the top bar. Each chart's own colors are kept. |
| Charts' time zone | This Mac's time | The time on the time axis, the tape and the sessions. The usual ones are at the top (Buenos Aires, New York, Chicago, London, Madrid and UTC) and then all of them. It is also the zone for strategies placed on a chart: with «This Mac's time», they use UTC. |
| Alert sounds | Enabled | Price alerts, Big Trades and indicator notices make a sound. |

## Trading

| Option | Default | What it does |
|---|---|---|
| Confirm real orders | Enabled | Before sending a manual order (Chart Trader, DOM or shortcuts) to a real account, it asks for confirmation. It stacks with each connection's option: the order goes out without asking only if both are off. It does not affect live strategies. |
| Simulated account | — | Shows the starting balance (it is changed in the **Account** section). The simulator's orders fill using the book queue and the real tape. |

## Live strategies

| Option | Default | What it does |
|---|---|---|
| Keep running in the menu bar when the window is closed | Enabled | Closing the window does not close Trading Hub: it stays in the Mac's menu bar with the live strategies working, and its icon opens a panel with the strategies and the positions in real time. To close it completely, ⌘Q or «Quit Trading Hub» in that panel. |
| Resume them automatically when Trading Hub opens | Enabled | The ones that were running when Trading Hub closed (or the Mac shut down) start again a few seconds after opening, with their parameters, size and limits; the paused ones come back paused. Off, they wait for you to resume them from Live. |
| Real accounts too | Off | Off (the recommended setting), the ones trading real money wait for you to resume them. It can only be enabled if the previous one is enabled. |
| Pick the contract automatically (minis or micros) | Enabled | If a futures strategy's size calls for a fraction of the mini (for example 1 NQ × 0.5), the orders go to the micro with the same exposure (5 MNQ), while watching the same chart. With whole minis it uses the mini. With an open position it stays on the same contract and switches once it is flat. |

## No-sleep mode

| Option | Default | What it does |
|---|---|---|
| Keep the Mac awake | Enabled | While there are live strategies or the risk agent is working, the Mac does not sleep from inactivity (the display may still turn off). It asks for no password and releases itself once nothing is running. |
| Keep going with the lid closed | Off | When you close the lid, macOS sleeps everything. With this enabled, whenever something is running Trading Hub asks for the administrator password (once per launch) and the Mac keeps working with the lid closed as long as Trading Hub is open. It is undone on its own when Trading Hub closes, even if it closes abruptly. |
| Only on the charger | Enabled | On battery, the Mac sleeps when you close the lid even if the previous option is enabled. It can only be touched if the previous one is enabled. |

Below you see the status: whether the Mac is awake and why, and whether the closed-lid mode is active. With the lid
closed the Mac doesn't ventilate as well: don't put it in a bag like that.

Under the hood, the first one uses a macOS power assertion, the way a video app does. The closed-lid one runs a helper
with administrator permissions that leaves `pmset disablesleep` at 1 while a marker exists in the data folder and
Trading Hub is open (with «Only on the charger», only while it is plugged in) and sets it back to 0 when it finishes.

## Keyboard shortcuts

They work in the Trading section and in charts in separate windows, on the active chart. While you are typing in a text
field only the ones using ⌃, ⌥ or ⌘, or a function key, respond.

| Group | Actions (with their default shortcut) |
|---|---|
| Trading | Buy at market, Sell at market, Buy limit at the bid, Sell limit at the ask, Close the position, Flip the position, Cancel every order, Close 25 %, Close 50 %, Stop to breakeven, Set the template's stop and targets, Quantity: next preset and Quantity: previous preset. **They ship unassigned** on purpose. |
| Chart | Period 1 minute (⌥1), 5 minutes (⌥2), 15 minutes (⌥3), 1 hour (⌥4), 4 hours (⌥5) and daily (⌥6); candle, footprint and Heikin Ashi styles; auto scale; go to the last price; draw a horizontal line (⌥H); cursor (drop the tool). |
| Windows | One-chart layout, four-chart layout, next tab (⌃Tab) and previous (⌃⇧Tab), pull the chart into a window, show or hide the DOM, the chart's Chart Trader (⌥T) and the right panel. |

To change one, click its button and press the combination: Esc cancels and ⌫ leaves it unassigned. If another action was
already using that combination, it is taken from it and you are told. **Restore the defaults** puts everything back to
the start. On real accounts, the trading shortcuts still ask for confirmation if «Confirm real orders» is enabled.

## Data

| Option | What it does |
|---|---|
| Record incoming live data | Enabled by default. It stores every incoming trade so charts reopen instantly, and for replay and backtests. |
| Data folder | `~/Library/Application Support/Trading Hub`, with **Open** |
| Own indicators (C#) | `~/Documents/Trading Hub/Indicators`, with **Open** |
| Strategies (C#) | `~/Documents/Trading Hub/Strategies`, with **Open** |

More about what is stored in [Data](data.md) and [Privacy](privacy.md).

## About

At the end you find the Trading Hub version, the .NET one and the author credit, with a link to their profile.

## Where they are stored

The settings go into `settings.json`, in the data folder. That file also stores what you choose elsewhere in the app:
the window size, the favourite instruments and periods, Trading's right panel, the DOM configuration, each instrument's
stop and targets, each drawing's default style, the templates new charts and indicators start with, and the position of
the Claude chat window. If the file gets corrupted, Trading Hub starts with the factory values.
