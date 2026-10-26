# Insights

The **Insights** section of the right-hand panel (Panels button → Insights) summarises what the tape and the book are doing, in
plain words, with every number and threshold shown. It is **context for you, not a trading signal**: nothing in it has been
shown to make money. The edge meter at the bottom keeps score of that.

## Decision

A verdict (**WAIT**, **BUY SETUP** or **SELL SETUP**) and the six checks behind it. It is a setup only when one side leads and
all six pass. The lean is who took liquidity over the last five minutes (buys minus sells, as a share of the volume); it needs at
least 5% to lean at all. The bar under the verdict shows how strong it is, to the left for sellers and to the right for buyers.

| Check | What is measured | Needs |
|---|---|---|
| Strength | 200 × the buy/sell imbalance of the last 5 minutes (0 to 100) | ≥ 30 |
| Trading activity | the last minute's volume as a percentile of the last 2 hours of minutes | ≥ 50% |
| Driven by trades | of the last 15 minutes, how many candles went the way their buying and selling did | ≥ 55% |
| Target vs costs | a target of one typical 5-minute swing (or your Chart Trader target) against your costs plus the live spread | ≥ 2× costs |
| R:R after costs | (target − costs) ÷ (stop + costs), the stop being half a typical 5-minute swing (so 2:1 before costs) or your Chart Trader stop | ≥ 1.2 |
| 15m trend | the efficiency ratio of the last two hours of 15-minute closes, and its direction | with the lean, ≥ 0.25 |

**Costs** (round trip, in basis points) is the box under the quote: two fees plus slippage. It starts at 8 bp for crypto and 2 bp for
the rest, and the live spread is added on top. Change it to match your broker.

The panel needs 20 minutes of trades on the chart before it says anything ("Learning this market").

## What testing on real tape showed

The thresholds were checked by replaying real ticks minute by minute (ES, NQ and gold from London Strategic Edge, BTCUSDT from
Bybit; 10 weekdays, 12 to 23 October 2026) and looking at where price was 15 minutes after each call, after costs. This is what it
found, and it is worth reading before relying on the panel:

- **A bug it caught:** the first version of the R:R check could never pass (target and stop were 0.6× and 0.5× the range, so the
  ratio was at most 1.2 before any cost). With the target and stop above, setups appear about 2 to 3 a day per market.
- **No edge was found.** The 99 setups the defaults called averaged about -0.3 bp before costs and -2.5 bp after (costs were
  2 bp for futures and 8 bp for BTC). On the first six days they looked slightly positive, on the last four clearly negative:
  that is what noise looks like. A bare lean (every minute with 5%+ imbalance, 10 600 of them) was no better, and trading the
  opposite way was no better either (+0.2 bp).
- **Tuning would only fit noise.** Sweeping 1 215 combinations of the six thresholds, the ones that looked best on the first days
  did worse on the later days. The only groups with a positive average had 2 to 7 setups, which means nothing. So the thresholds were
  left where they are instead of being tuned to those ten days.
- **What this does and does not say:** it says these six checks, on these days and these markets, did not predict the next
  15 minutes beyond what costs take away. It does not say none exist. It is a short sample, so run the test again on more days; the
  edge meter does the same thing live, per market, on your own screen.

To run it again with more days: `INSIGHTS_TUNE=1 INSIGHTS_TUNE_DAYS=30 LSE_API_KEY=… dotnet test --filter InsightsTuning` (the
tape is cached in your temp folder, so repeats are quick).

## Chips

Short facts from the same data: who took the last minute's volume, stacked imbalances (three or more price rows in a row where
buyers took at least 3× what sellers took one row below, or the reverse), who is in control, whether the order book leans the same
way, and whether aggressive volume is being absorbed (a lot of buying or selling over two minutes with no price progress).

## Danger

A toxicity reading (VPIN): the trades are cut into buckets of equal volume (a fiftieth of the last hour), each bucket's imbalance
is measured, and the reading is the average of the last fifty. High means one side has been taking liquidity unusually
one-sidedly, which is when market makers get hurt and prices tend to jump. It is shown as a percentile of that market's own last
day, so it needs 90 minutes of readings before it ranks anything.

## Walls real

Of the big resting orders that left in the last 10 minutes, the share that were **traded through** instead of cancelled. Low
means they mostly vanish before they are hit (spoofing-like behaviour). On feeds that publish every order (Bitfinex; Coinbase
with an API key) it is measured from real orders; elsewhere it is inferred from the order book and says so.

## Edge meter

Each time the panel calls a setup it is written down (in `Insights` in the app's data folder, one file per instrument). Fifteen
minutes later the price at that moment is read from the tape, and the result is the move in the setup's direction minus the round
trip cost, in ticks. After 30 scored setups the panel shows how many ended in profit and the average result; it keeps saying
"learning" until 120. A replay never adds to the score.

## Levels

- **Options** (BTC and ETH, from Deribit's free public data, refreshed every 5 minutes): the **call wall** and **put wall** are the
  strikes within 12% of the price with the most call and put open interest above and below it; **max pain** is the strike where the
  biggest expiry of the next month would pay out least; **gamma flip** is the price where the market's net dealer gamma changes sign,
  assuming dealers are long the calls and short the puts (the usual convention, not something measured): above it dealer hedging
  tends to damp moves, below it to feed them.
- **Liquidations** (Bybit only, which publishes them): the price band where most longs and most shorts were forced out in the last
  6 hours.

The same levels are drawn on the chart by the indicator **Market levels (options walls, liquidations)**.

## Chart additions

- **Big Trades** bubbles now say what the order is worth in money (`$1.2M`: price × size × the contract's value) above the bubble.
  Setting *Bubble label* switches back to the size.
- **Whale net (big orders, money)** is a strip under the chart: per candle, the big buy orders minus the big sell orders, in money. A
  big order is one in the top 1% by size of the recent orders, and a sweep that keeps eating levels counts once.

## Testing an idea of your own

The lab tool **event_study** (ask Claude in the Strategies page, or any MCP client) tests an order-flow idea on the ticks you have stored: at every
minute it checks whether the event is on, and reports where price was 1, 5, 15 and 30 minutes later, in basis points, before and after costs. The
events are `lean`, `stacked_imbalance`, `absorption`, `big_trade` and `delta_divergence`. Each result is split into an earlier "train" part and a
later "test" part, and set beside the opposite trade and an always-long baseline, because an event whose opposite works as well has no information.
It only uses what had happened before each minute. The default costs are 8 bp for crypto and 2 bp for the rest.

A few days of ticks says little: the more days it has, the more the numbers mean (see the connection's *Keep the tick history* setting for
London Strategic Edge futures). Try several events and horizons and some will look good by luck, so judge on the test rows.

## Whale view (added 2026-10-26)

- **Big Trades > Detect**: Aggressive (any big market order), Sweep (3+ price levels in one order), Level (near yesterday's levels or a remembered level), Big print (one single fill). The summary line at the top of the chart says what the whales did in view; "Whale average price" and "Whale zones" draw their average prices and clusters.
- **Whale net > Compare with everyone else**: the crowd's running net next to the whales' (stretched to the whales' size: compare the shape).
- **Insights panel**: big bid / big ask with the distance in basis points, "Pulled N×" (a price where big orders were cancelled repeatedly), and for crypto a Futures and sentiment block (Bybit funding, open interest and its 24 h change, basis, long/short accounts; alternative.me fear and greed).
- **Market levels** now also draws estimated liquidation prices (`LiquidationProjection`: the biggest orders of the last 6 h taken as 10x-100x positions). It is a guess about leverage nobody can see.
- **Views** button in the chart toolbar jumps between chart, footprint, heatmap, whales, volume profile, levels and tape (adding the indicators a view needs), and opens the Guide.

## Alerts

Settings → **Insight alerts** can tell you, through the same path as every other alert (the status bar, the Today log, Telegram and speech if
they are on, and the sound), when: the tape turns **toxic**; a **big resting order** (worth at least the amount you set) is pulled, flashes or is
traded through; the price reaches an **options level** (within 0.05% of a call wall, put wall, max pain or gamma flip); a **liquidation cluster** worth
at least the amount you set forms; or, if you switch it on, the panel calls a **setup**. Each alert fires when its condition starts, not while it
lasts, and waits out a cooldown (30 minutes for toxicity and options levels, 15 for liquidations) before it can fire again.

## Related markets

Switch on *Watch related markets* at the bottom of the panel and it opens the sibling markets (ES with NQ, MES, YM and RTY; gold with silver,
platinum and the euro; crude oil with natural gas, heating oil and gasoline; Bitcoin with Ethereum, and the reverse) and shows each one's 5 and
30 minute move, the gap between its 30 minute move and this market's, and **who moves first**: both are sampled every 5 seconds for 20 minutes and the
correlation of this market's moves with the other's a few steps earlier or later is compared. A leader is named only when that correlation is
clearly real (at least 0.2) and better than at no delay; otherwise they "move together". Each watched market is a live subscription that stays until the
app closes (the free London Strategic Edge plan allows 60 calls a minute, shared by every futures chart).

## Replay and recent calls

The panel works on a replay or the simulator too, and under the edge meter it lists the **last five calls**: when, which way, at what price, and how
each turned out 15 minutes later after costs (or that it is still waiting). On a replay or the simulator they are scored in their own memory-only score
("this replay only"), so a replayed day can be used to judge the panel without touching the saved meter of the real instrument.

## Fill quality

The lab tool **fill_quality** compares each fill of an account with the price of the last trade a second before it on your recorded ticks, adds the
commissions, and reports slippage and cost overall and by hour, instrument and side, ending with the **round-trip cost in basis points** to put in the
costs box. A simulated account fills at the tape's price, so it shows about zero: it is for real accounts.

## Low memory

When the PC's memory use passes 92% the app gives some back (the heatmap keeps 5 minutes of book instead of 30, the flow tracker 4 hours instead of a
day, and the memory is compacted), says so in the status bar, and returns to normal once use falls under 85%. It never touches the trades, the
recordings or the charts.

## Paused when the data is old

If nothing has traded for much longer than that market's usual gap (ten times the median gap of its last hundred trades, at least 15 and at most 300
seconds), the decision card says **PAUSED** and is dimmed: the numbers are old, so no call is made, none is scored by the edge meter and no alert is sent.
A replay or the simulator is never paused.

## More evidence

Open **All evidence** under the chips to see the six checks, then:

- **Large traders**: over the last 30 minutes, what the biggest prints (top 0.1% of the last 20 000 by money, at least five times the median) bought and
  sold, and what everyone else did net. Under the checks a sentence says whether they **agree or are against** the panel's lean. Whales are not smarter
  than the crowd, so this is a conflict flag, not a reason to trade.
- **Chips**: a *possible iceberg* (a lot traded into one price while the book showed little there), *pulled* orders (the same level cancelled repeatedly),
  and **Γ+ / Γ−**, whether the price is above or below the options gamma flip (Bitcoin and Ethereum).
- **Session numbers**, in the panel and on the chart toolbar: the last minute's delta, the money delta since the UTC day began (CVD) and the price with the
  most volume today (POC).

## Gamma view

The **Gamma** button (Bitcoin and Ethereum) opens open interest by strike, puts to the left and calls to the right, with each strike's gamma exposure as a share
of the largest and the max pain and the walls marked. Dealers are assumed long the calls and short the puts: that is a convention, not something measured.

## Your own levels

Type a price in *add your level* in the panel and press Enter. It is drawn on the chart by the Market levels indicator (with a tag on the price axis), listed
in the panel (✕ removes it) and you are alerted when the price touches it (within 0.05%, at most once every ten minutes).

## On the chart

- **Levels** button on the toolbar: choose which families of levels are drawn (options, liquidations, estimated liquidation prices, your own), whether each
  is tagged on the price axis, and how Big Trades decides what is a whale (aggressive, sweep, level, big print).
- **Big-order profile**: switch on *Second column: only what is on the chart* to add a column of whale volume per price for the visible candles, next to the one
  for the last days.
- **DOM**: the new **Cxl** column (off by default, switch it on in the DOM settings) adds up size that left the book without trading at each price this session.
  It is an estimate: a removal is counted as cancelled only after subtracting what traded at that price since the previous update.
- **Whale net**: *Compare with everyone else* draws the crowd's running net on its own scale, stretched to the whale total's, so only the direction compares.
