[← Back to the index](README.md)

# Trading

You trade from the **Trading** section: with the Chart Trader, with the DOM, from the chart or with keyboard shortcuts.
Each chart trades with whichever account you pick for it; out of the box that is the **Simulated account**.

## Choosing the account

Each chart remembers its account. You pick it in the Chart Trader, under **Account**. The Simulated account is always
there; the rest appear depending on the chart's instrument and your [connections](connections.md):

| Chart | Accounts that appear |
|---|---|
| Any | Simulated account |
| Binance Futures or Spot | Your Binance account for that connection: «(testnet)» or «— REAL» |
| Rithmic futures | Your Rithmic accounts: «(practice)» or «— REAL» |
| CME futures from Rithmic or Tradovate | Your Tradovate accounts: «(demo)» or «— REAL» |

Rithmic accounts appear once their order session finishes connecting; until then the Chart Trader says why they are not
there yet. With a real account selected, the Chart Trader shows «REAL ACCOUNT» in orange.

## The Chart Trader

![The chart's Chart Trader and, on the right, the panel with the DOM, its execution buttons and the tape](img/trade.png)

It lives in two places that show the same thing:

- **On each chart**, on the right. It folds away with the little arrow or with ⌥T, and each chart only trades its own.
- **In the right panel**, above the DOM. It follows the active chart («Trades on the active chart: …»). If you pull it
  into a separate window, closing it returns it to the panel.

From top to bottom:

| Part | What it does |
|---|---|
| Account | Where this chart's orders go. |
| Cross trading | Execute on another instrument (see [Cross trading](#cross-trading)). |
| Quantity | With quick buttons: 1, 2, 5 and 10 if the instrument trades in whole units; fractions in crypto. |
| Buy MKT / Sell MKT | At market. |
| Buy Ask / Sell Ask | Limit at the ask price (the price shows below each button). Buying at the ask fills right away, with a price cap; selling at the ask waits on the offer. |
| Buy Bid / Sell Bid | Limit at the bid price. Buying at the bid waits on the demand; selling at the bid fills right away, with a price cap. |
| Cancel buys / Cancel sells | Cancels that side's orders. |
| Cancel all / Reverse | Cancels every order / flips the position. |
| Breakeven | Moves the stop to the entry price. |
| Close 25 % / 50 % / 75 % | Closes that share at market; the stop and the targets adjust themselves. |
| Close position | Closes everything at market and cancels the orders. |
| Result | Open PnL and the position («LONG 2 @ price», «Flat · day's result»). |
| Automatic SL / TP | Stop and targets (see [Stop and targets](#stop-and-targets)). |

The result and the position are shown below the buttons, so their changes don't shift the buttons around.

## Order types

| Type | How it is sent | On the simulated account |
|---|---|---|
| Market | MKT, Close, Reverse, partials | Fills at the book's best bid or ask (or at the last price if there is no book). |
| Limit | Ask / Bid, a click on the DOM, a right click on the chart | Fills if price trades through your price, or if more volume traded at your price than you had ahead of you in the queue. |
| Stop | A click on the DOM beyond the price (see the table below), a right click on the chart | It triggers when the price trades and fills at market. |

The stop loss and the targets can only reduce the position, never flip it.

## Trading with the DOM

| Action | Result |
|---|---|
| Click in the Bid column (or Buy orders) | Buy limit at that price; if it is at the ask or above, a buy stop. |
| Click in the Ask column (or Sell orders) | Sell limit at that price; if it is at the bid or below, a sell stop. |
| Right click or ⌥ click | Cancels your orders at that price. |
| Dragging one of your orders | Moves it to another price. |
| Wheel / center button | Scrolls the ladder / back to the price. |

The quantity is the active chart's. If you enable **Double click to trade** in Configure DOM, a single click no longer
sends orders (cancelling still works with one click). The DOM's question-mark icon sums up these gestures.

Below the DOM are the **execution buttons** (shown or hidden from its header): quantity with quick buttons, Buy MKT,
Sell MKT, B. Bid, B. Ask, S. Bid, S. Ask, Cancel, Reverse and Close. They act on the active chart.

## Trading from the chart

- **Right click** on a price: «Buy limit» and «Sell stop» if it is below the last price, «Sell limit» and «Buy stop» if
  it is above, with the Chart Trader's quantity. Also «Cancel every order».
- **Dragging** an order's line moves its price.
- **Right click on one of your orders**: cancel it; on a target, «Target quantity» (25, 50, 75 or 100 % of the
  position); on the stop, «Stop to breakeven»; on a pending entry order, «Attach the template's stop and targets».
- **With an open position**, the right-click menu offers «Close half at market», «Close everything at market», «Stop to
  breakeven» and «Set the template's stop and targets».

## Stop and targets

The Chart Trader's **Automatic SL / TP** section keeps one template per instrument (the same one in the chart's panel
and in the right-hand one):

1. Turn on the **Automatic SL / TP** switch.
2. Set the **SL** in ticks from the entry.
3. Set the **TP** in ticks. With **+ target** you add up to 3 partial targets, each with its percentage of the position
   (100 % is split automatically; each one's × removes it).
4. **Breakeven at TP1** (ticked by default) moves the stop to the entry when the first target fills.

With the template active, opening a position sets the stop and the targets automatically, in OCO: when one closes the
position, whatever is left gets cancelled. On a limit or stop entry order, they stay attached and are sent when it
fills. If you grow the position, they cover everything open; if you close part of it, they adjust to what is left.

**Apply to the position** replaces the open position's stop and targets with the template's.

They can also be set by hand on the chart: drag the position's or a pending order's SL / TP buttons, or drag from the
position's line (towards profit creates a target; towards loss, the stop).

## Cross trading

You watch one instrument and the orders go to another — for example, you watch ES and execute on MES. In the Chart
Trader click **Cross trading**, search for the instrument (the ones on the same underlying come first) and pick it;
«None» goes back to trading the chart's one. The orders, the stop and the targets go to the chosen instrument with the
prices exactly as they are. If the two prices differ, the Chart Trader warns «careful: different prices».

## Trading shortcuts

In **Settings → Keyboard shortcuts**, the **Trading** group, you can assign: buy and sell at market, buy limit at the
bid, sell limit at the ask, close the position, flip it, cancel every order, close 25 % or 50 %, stop to breakeven, set
the template's stop and targets, and move to the next or previous quick quantity button. They ship unassigned. They act
on the active chart and don't fire while you are typing in a text field (unless the shortcut uses ⌘, ⌥, ⌃ or a function
key).

## The Account section

**Account** (⌘5) shows one account at a time. The button at the top opens the list, grouped by connection: the
Simulated account and your Rithmic and Tradovate accounts.

Cards: **Balance**, **Day's result**, **Open PnL**, **Realized today**, **Total result** (on the simulated one) or
**Available to trade** (on the broker ones), and **Active orders**. On broker accounts the balance is whatever Rithmic
or Tradovate reports.

| Tab | Contents |
|---|---|
| Positions | Contract, side, quantity, average price, open PnL, realized today and a «Close» button. |
| Orders | Today's, active or finished, with their status and a «Cancel» button. |
| Executions | Each execution with time, contract, side, quantity, price and commission. |
| Trades | Each closed trade: entry, exit, duration and result. |
| Statistics | Today, 7 days, 30 days or everything: balance curve, net result, % winners, profit factor, maximum drawdown, expectancy, streaks, longs and shorts, and each day's result. |

The position and order rows don't move around: new ones are added at the bottom, so a click doesn't land on the
neighbouring row. The history is stored on your Mac and survives closing the app.

On the Simulated account you can change the **Starting balance** and use **Reset the simulated account**: it goes back
to the starting balance and begins a new history (the previous one is kept separately). To reset you have to be flat,
with no positions or orders.

## Safety

- **Nothing real by default.** Each connection has its real-orders box, off out of the gate. Without it, the real
  account does not appear for trading (see [Connections](connections.md#real-accounts-and-test-accounts)).
- **No confirmations on test accounts.** On the simulated account and on demo, practice or testnet accounts, each click
  sends the order instantly.
- **Confirmation on real accounts.** By default, every order to a real account opens the «Confirm REAL order»
  confirmation with the details and the account. The same happens when moving an order, closing, reversing, cancelling
  from the buttons and using partials or breakeven. To avoid it, both options have to be off: the connection's one
  («Ask for confirmation before each real order» on Binance, «Confirm each real order» on Rithmic) and **Settings →
  Trading → Confirm real orders**. The Tradovate connection does not have that box, so its real orders always ask for
  confirmation. In Account, «Close» and «Cancel» on a real account also ask for confirmation.
- **Maximum quantity per order.** On real accounts, an order larger than the connection's maximum is blocked and the
  status bar says «Order blocked».
- **Strategy limits.** Automated strategies have a maximum daily loss and a maximum drawdown (0 = no limit), and
  **Live** has **Stop everything**, which halts them all, cancels their orders and closes their positions.
- **Visible warnings.** Real accounts are marked in orange with «— REAL» or «REAL ACCOUNT», in the Chart Trader and in
  Account.
