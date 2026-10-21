[← Back to the index](README.md)

# Live

The **Live** section (⌘7) gathers every strategy running in real time, whether it was launched from here, from
Backtesting or from a chart. It has the **Strategies**, **Market**, **Portfolios**, **Trades** and **Proposals** tabs,
and at the top the **New live strategy** and **Stop everything** buttons. Always test on the simulated account first.

## Accounts: simulated, test and real

- The **simulated account** is always there. Its orders fill using the book queue and the market's real tape.
- Binance testnet, Tradovate demo and Rithmic practice accounts don't trade real money either.
- **Real** accounts only appear if you enabled «Allow real orders» on that connection and they show in red, with
  «— REAL» (see [Connections](connections.md#real-accounts-and-test-accounts)).
- When you launch or manually resume a strategy on a real account, Trading Hub asks you to confirm once. After that it
  sends its orders without confirming each one: safety comes from the size and the risk limits. Automatic resume on
  open does not ask; that is why it ships off for real accounts.

## Launching a strategy

### From Backtesting or from Live

1. In **Backtesting → Backtest**, the «Take it live» card, click **Run live…** (it carries over the strategy and its
   parameters), or in **Live** click **New live strategy**.
2. **What and where**: the strategy, the instrument, the account and the candles (M1, M2, M3, M5, M10, M15, M30, H1, H4
   or its backtest's period).
3. **Parameters**: they come from whatever you last configured in its backtest.
4. **Market condition**: in which market it opens trades (range, mixed, uptrend, downtrend) and at what volatility (low,
   normal, high against the last month). It is the strategy's context filter.
5. **Risk**: size (×), maximum daily loss and maximum drawdown (in money; 0 = no limit), and whether hitting a limit
   also closes its position.
6. **Portfolio**: one of yours or none (it goes to the automatic one).
7. **Start live**.

The zone and the session start are its backtest's: they define the «day» of the maximum daily loss. Before trading, the
strategy builds its candles and indicators from the history that instrument already has loaded (the connection's hours
of history, or whatever a chart loaded), without sending orders, and its market context is completed with the last
month's hourly data. If it needs more candles than are loaded, its indicators take a while to be ready. The same
strategy cannot run twice on the same instrument and the same account. In futures, a notice says how many contracts and
of which one each order sends.

### From a chart

On the chart bar, the robot button lists your strategies (you can also use **Put on the active chart** from
Backtesting). The strategy starts **visual only**: it marks where it would have entered and exited over the chart's
history (a backtest without commissions or slippage) and carries on live with its own simulated account, «Visual»,
which doesn't touch yours. In its settings (from the same menu or the chart legend) you choose **Run live with an
account**, the account and **Apply**. From then on it appears in Live with «Chart» as its origin and is managed like the
rest. If you change the chart's instrument, the executing ones pause; if you remove it from the chart, it stops and its
position is left as it is. More in [Charts](charts.md#strategies-on-the-chart).

## Size and smart contract

- The **size** multiplies the strategy's orders (1 = as you programmed it; 0 = it opens nothing new). The strategy still
  sees its position in its own units: each order sends the difference between what the current size calls for and what
  it holds, so an exit closes exactly what is open even if you changed the size in between.
- It is rounded down to the instrument's step, but if the strategy wants to be in the market it sends at least one unit.
- **Smart contract** (in Settings, enabled): in futures with a micro (NQ → MNQ, ES → MES, YM → MYM, RTY → M2K,
  GC → MGC, CL → MCL, among others), if the size calls for a fraction of the mini, the orders go to the micro with the
  same exposure: ×0.5 of 1 NQ is 5 MNQ. With whole minis it uses the mini. With an open position it stays on its
  contract and switches once it is flat (it restarts itself, with the same accounting).
- In the table, the detail card, the menu bar and the chart legend you always see how many contracts and of which one
  each order sends, for example «5 MNQ · micro».

## Risk limits and pausing

| Limit | On hitting it |
|---|---|
| Maximum daily loss | When what is closed in the session's day plus what is open reaches −limit, it cancels its pending entries and opens nothing else until the next session («Stopped for today»). |
| Maximum drawdown | When the fall from its curve's peak (counting what is open) reaches the limit, it stops. |

With «On hitting a limit, also close its position» (enabled by default) it also closes whatever it has open. Each limit
hit leaves a notice above the table and in the status bar. **Pausing** is different: it opens no new positions, cancels
its pending entries and keeps managing whatever is open until it is closed.

## Strategies: the table and the detail card

At the top: **Result**, **Today**, how many are **Live**, **Closed trades** (click to see them all) and **Pending
changes**. The table shows strategy, status (with «paused by context» when its filter won't let it open), size and
contract, position, today, net, trades, profit factor, current drawdown and limits. Clicking one opens its card:

- **Actions**: Pause or Resume, Close position, Stop, Stop and close, Start again (or Resume on the chart), View on the
  chart, View trades, Start from scratch (it clears its history, only when flat) and Delete.
- **Risk**: size and limits, with **Apply risk** (the new size takes effect from the next order).
- **Market condition**: it applies right away, without restarting, and shows whether it would open trades in the
  current market.
- **Parameters**: **Apply and restart** (it warms up again with the history; if it has a position, it keeps managing
  it).
- **Portfolio** and **Activity** (what it is doing and what it writes with `Log`).

Each strategy keeps its own accounting, even if it shares the account with others or with your manual trades. If it
starts again with a position in its accounting and the account has it, it keeps managing it and puts the stop and the
target back (without duplicating them if they were already at the broker); if the account doesn't have it, it closes it
in its accounting at the last price. With an external broker it waits up to 45 seconds for the position to be reported
before deciding.

**Stop everything** is the emergency button: after confirming, it halts every live strategy (this section's and the
charts'), cancels their orders and closes their positions at market.

## Trades

Every trade closed live: strategy, market, side, entry and exit with their price, duration, quantity, points, result,
MAE, MFE and «Capture» (how much of the best moment it took). You filter them by strategy, sort them (recent, old, best
or worst result, worst MAE) and clicking one shows it on the chart. Bear in mind that each strategy's accounting records
every fill at the market's last price at that moment (not the one the broker reports) and does not deduct commissions.

## Portfolios

- The **automatic** one («Automatic · all live») gathers the ones running without a portfolio of yours. With **New
  portfolio** you build others with a name, reference capital (25,000 by default) and strategies; each strategy belongs
  to only one portfolio of yours.
- Each one shows result (closed and open), trades, profit factor, maximum drawdown, Sharpe (with more than a week of
  data), average correlation among its strategies, the joint curve, a table per strategy and the correlation matrix of
  the daily result (red: they win and lose on the same days; green: they offset each other).
- Buttons: **Add a new strategy**, **Start all** (it confirms if there are real accounts), **Stop all** (the positions
  are left as they are), **Edit** and **Delete** (its strategies carry on and move to the automatic one).

## The risk agent

Each portfolio can have a risk agent: Claude looks at the result, the recent streak, the drawdown against its history,
the volatility of the daily result and each strategy's correlation, and allocates the risk by proposing each one's size
and, if needed, its maximum daily loss and its maximum drawdown.

| Option | Default |
|---|---|
| Mode | **Proposes**: you approve each change. **Automatic**: it applies on its own, and only if no strategy in the portfolio trades a real account |
| Daily risk (% of capital) and the portfolio's maximum drawdown (%) | 2 % and 10 % |
| Minimum and maximum size per strategy | ×0.25 and ×3 (0 so it opens nothing new) |
| Review every (min) | 60 (10 at the least), if there were at least 3 new trades, and every 8 hours even if there weren't |
| Model and effort | Opus 5.5 with Extra effort; also Sonnet 5.5, Haiku 5.5 or Fable 5.1, from Low to Maximum |
| Instructions | Free text: «prioritize the NQ ones», «never above ×2 in crypto»… |

**Review now** runs it on the spot. It does at most 8 automatic reviews a day, each with 12 minutes and a US$ 2 usage
cap (if your Claude Code supports that option); it has no tools, only the numbers Trading Hub hands it. What it proposes
stays within your caps and appears below the portfolio and in **Proposals**. The agent's card shows its last review, the
summary and the accumulated estimated cost. How it connects to your Claude account: [Claude](claude-assistant.md).

## Proposals

The suggested size changes wait for your approval, one by one or with **Approve all**; the ones on real accounts ask for
confirmation and take effect from the next order. They come from three places:

- **Suggest with rules**: with 10 trades or more and looking at the profit factor of the last 20, it proposes pausing
  (×0) if it is below 0.7 with the drawdown near its worst and at least 15 recent trades, halving it if it is below 0.9
  or the drawdown is near its worst, and raising it 25 % (up to ×3) if it is 1.5 or more with positive expectancy and
  little drawdown.
- **Ask Claude for analysis**: it opens the lab in Backtesting and asks it to review the live book and propose.
- Each portfolio's **risk agent**.

## Market

For each market of the running strategies: its **condition** (range, mixed or uptrend or downtrend, with the efficiency
of the last 24 hours and how long it has been that way), its **volatility** (the last month's percentile), a strip with
the last 48 hours and which strategies trade there and whether their filter lets them open. At the end, a table says in
which conditions each strategy trades, with the current one marked. It is recomputed every 30 seconds with the last
month's hourly closes, using the same measure as the context filters.

## On closing the window and on opening

- **Menu bar**: with the option enabled (it is by default), closing the window leaves Trading Hub running in the Mac's
  menu bar, with the strategies working. Its menu shows the live strategies and the open positions, and the panel has
  more detail. To close it completely: ⌘Q or «Quit Trading Hub». See
  [Getting started](getting-started.md#the-menu-bar-icon).
- **Automatic resume**: a few seconds after opening, the strategies that were running when it closed (or when the Mac
  shut down) start again on their own, with their parameters, size and limits; the paused ones come back paused. The
  ones on real accounts wait for you to resume them, unless you enable that option too. A notice sums up what was
  resumed.
- **No-sleep mode**: while there are live strategies or the risk agent is working, the Mac does not sleep from
  inactivity, and if you enable it, it keeps going with the lid closed. See [Settings](settings.md).
