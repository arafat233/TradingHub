# Detecting iceberg orders

An iceberg is a limit order with a price, a total size and a **peak** (display size). Only the peak rests in the book. When the peak
is fully traded the next tranche is shown at the same price, until the order is filled or cancelled. Nothing in a market-data feed says
"this is an iceberg": it is always an inference, and how good it is depends on what the feed gives you.

This page sums up what the research says, what each of our detectors does with it, and where the limits are. The detectors only give
context; none of the papers shows an iceberg flag that predicts price.

## Two kinds, and why it matters

| | Native (exchange-managed) | Synthetic (trader's software) |
|---|---|---|
| Who refills | The matching engine | The trader's own algo or broker software |
| Order id | Stays the same across refills | A new order each time |
| Queue priority | CME keeps the first peak's priority; Xetra gives each new peak a new time stamp | Every refill goes to the back of the queue |
| Trade vs resting size | A trade can be bigger than the size shown (it takes hidden volume) | Never: each tranche is an ordinary order |
| Refill delay | Exchange latency only: Christensen and Woodmansey measured **76 ms on average (sd 98 ms)** on CME ES | Exchange latency plus the algo's round trip: Zotikov and Antonov use **0.3 s** |
| Detection | Close to exact with order ids | Always a heuristic |

## What each data level lets you see

- **Market by order (L3: ids, adds, modifies, fills).** Native icebergs are unambiguous (same id refilled, or a trade larger than the
  resting size). Synthetic ones are found by a new order of the same size, same side and price, right after a fill.
  Zotikov and Antonov's finite-state machine does this on CME; our `OrderIcebergDetector` follows it.
- **Market by price (L2: size per level) plus trades.** There are no ids, so you rely on two signals:
  1. *Absorption*: a level trades far more than it ever showed.
  2. *Refill*: the displayed size is taken out by a trade and the same size reappears at once. Frey and Sandås, and Christensen and
     Woodmansey, use this. Our `IcebergDetector` does both.
- **Trades only (no book).** The only signal is many prints at one price in a short run with the price not trading through.
  Our `TapeIcebergDetector` does this. It has the most false positives.

## The refill rule in detail (Christensen and Woodmansey 2013, CME ES)

After a trade, the exchange sends the trade, then a book update for the reduced size, then the refreshed peak. The initial detection
signal is an order or level update that:

1. appears within `dt` of the trade (they measured the delay and set `dt` to about mean plus 3 sd = 369 ms),
2. has a size that is an increase,
3. is on the same side as the traded order and at the traded price (a buy trade takes from the ask side),
4. is at the best price (trades only happen there; an iceberg left behind in the depth is found only if the price moves away).

The size of the refreshed peak depends on the trade size relative to the peak (max show) Ψ:

- trade smaller than Ψ: the refresh is at least the trade size,
- trade equal to Ψ or a multiple: **no refresh message** at all (the displayed size is unchanged),
- trade larger than Ψ: the refresh is the **remainder** after dividing by Ψ, so the first refresh is not the peak,
- the last tranche is smaller when the total is not a multiple of Ψ.

**Active test.** An iceberg stays active while the volume traded at its side and price since the last tranche is no more than Ψ
(plus the latency allowance). Beyond that with no refresh it has ended: either finished or cancelled. If only one tranche was ever
seen, it is dropped as a coincidence (a false positive). The chance that every tranche is a coincidence shrinks with each one,
which is why they require at least two.

**Size prediction.** Peaks and totals are not random: sizes cluster on round numbers (15, 25, 50, 100). They learn the joint
distribution of peak and total size from the previous days (Gaussian kernel estimate), leaving out icebergs that were cancelled,
and predict the total of a new iceberg from its peak. Zotikov and Antonov replace this with a Kaplan-Meier estimate, which keeps the
cancelled ones as censored data (more than half of synthetic icebergs end that way).

## What the literature measures (mostly equities, 2002-2011 data)

| Paper | Venue and data | Finding |
|---|---|---|
| Frey and Sandås (QJF 2017) | Xetra, DAX stocks, icebergs identified in the data | Detector from public data, scored on the best quote: correctly sees an iceberg in 6-9 % of observations, falsely sees one in about 1 %, misses 3-5 %. Hidden volume predicted from the peak and executed peaks. Detected icebergs attract more market orders. Limit orders lose money in books with icebergs |
| Christensen and Woodmansey (J. Trading 2013) | CME ES, 2011 | Icebergs are 3.2 % of limit orders, 12 % of traded volume. Mean total 62, mean peak 5 |
| Zotikov and Antonov (2019) | CME ES, one week, full order depth | Native 4 % of traded volume, synthetic 3.3-14.3 %. Median total 6 in both. Synthetic prediction accuracy about 69 % (mostly true negatives) |
| De Winne and D'Hondt (2007) | Euronext Paris | Over 45 % of depth at the top five levels hidden. Detecting hidden depth makes traders more aggressive |
| Bessembinder, Panayides, Venkataraman (JFE 2009) | Euronext Paris | 18 % of orders have hidden size, 44 % of order volume. Hidden orders fill less often and take longer. Exposing size raises execution cost |
| Hautsch and Huang (2012) | NASDAQ ITCH | Hidden orders can sit inside the spread; their location is predictable from the visible state. NASDAQ reports executions against non-displayed orders |
| Esser and Mönch (2007) | Model | Optimal peak size when each new peak loses time priority; estimates about 8 % of volume is hidden |
| Fleming et al. (2018) | US Treasuries, BrokerTec | Only about 2 % of volume executes against hidden depth; there is usually none, but when there is, it is large |

The estimates range from 2 % to over 45 % of depth because they measure different things (depth, orders or volume) on different
markets. Moro et al. (2009) use "hidden orders" for large orders split into many pieces by one member, which is a different thing
from an exchange iceberg.

## Our detectors

| Detector | Data it needs | Rule | Main weakness |
|---|---|---|---|
| `OrderIcebergDetector` | Order events with fills and cancels (Databento MBO, Coinbase `full`) | Native: same id refilled. Synthetic: new order of about the same size (±25 %) within 0.3 s of a fill, 3+ refills | Needs a feed that says fill vs cancel. Not connected to the UI yet |
| `IcebergDetector` | L2 book snapshots plus trades | Volume above 3x the largest size ever shown, or the displayed size cleared and back at the same size 3 times. Tracks `Peak`, `Refills`, `Active`, and `PredictedHidden` | Snapshots every 250 ms are slower than the 76 ms refill. Market makers re-quoting look the same |
| `TapeIcebergDetector` | Trades only | At least 8 prints at one level absorbing 5x the usual run | No displayed size to compare with |

`HiddenVolumeModel` predicts the rest of an iceberg from the earlier ones of the same peak seen in the session. It needs at least
5 earlier icebergs that went further than the current one, and it stops once the iceberg is no longer active.

## What the rules do on a made-up market (simulation)

`tests/TradingHub.Tests/IcebergSimulation.cs` scores `IcebergDetector` on a market where the answer is known: one price level per
episode with buyers hitting it as a Poisson stream, seen only through the trades and a level size recorded every 250 ms. There are
five kinds of level: a native iceberg (exchange refills, keeps its place), a synthetic one (new order each time, back of the queue),
an ordinary order, a market maker that re-quotes the same size after every fill, and a busy level where unrelated orders keep
arriving. The mix, sizes and speeds are my assumptions, so read the numbers as how the rules behave, not as accuracy on a real market.
Recall below is among icebergs that refilled at least 3 times. Rules: "ratio" = absorbed more than 3x what was shown; "refill" = the
displayed size traded out and came back at the same size N times; "loose" = also count a level that is bigger than it should be.

| Tape (buyers/s) | Rule | Native found | Synthetic found | Market maker flagged | Busy level flagged |
|---|---|---|---|---|---|
| 1 | ratio only | 80 % | 79 % | 81 % | 0 % |
| 1 | ratio + refill 3 (old default) | 84 % | 98 % | 98 % | 0 % |
| 1 | refill only, 4 (new default) | 55 % | 86 % | 96 % | 0 % |
| 1 | loose refill, 4 | 89 % | 91 % | 98 % | 51 % |
| 4 | ratio only | 84 % | 84 % | 99 % | 100 % |
| 4 | ratio + refill 3 (old default) | 87 % | 95 % | 100 % | 100 % |
| 4 | refill only, 4 (new default) | 65 % | 87 % | 100 % | 21 % |
| 4 | loose refill, 4 | 83 % | 87 % | 100 % | 81 % |

What this shows:

- **A market maker that re-quotes the same size cannot be told apart from a synthetic iceberg** by trades and level size. Every rule
  flags them at the same rate, so a flag only means "something keeps refilling this level".
- **The ratio rule is the weak point on busy levels.** It only compares cumulative volume with what was displayed, so a level that
  keeps absorbing flow because new orders keep arriving is flagged (100 % at 4 buyers/s). Requiring a refill pattern instead cuts that to
  21 % at the cost of finding fewer native icebergs.
- **Native icebergs are the hardest from L2.** Other orders queued behind them mean the level never empties, so the strict refill rule
  does not fire. The loose rule finds them (89 % at 1 buyer/s) but also flags half of the busy levels. Only order ids separate the two.
- **A finer book picture helps a little.** Recording the level every 20-50 ms instead of 250 ms adds a few points of recall and does
  not change the false positives much (`DepthHistory.Interval`).
- **Ordinary orders are never flagged** in any setting.

Settings on `IcebergDetector`: `MinRefills`, `RatioRule`, `LooseRefill`. The charts (heatmap, insights panel) use the refill rule only:
`MinRefills` 4, `RatioRule` off, `LooseRefill` off, because of the busy-level false positives above. Strategies that set `TrackIcebergs`
keep the old absorption rule only (`RatioRule` on, `MinRefills` 0), so earlier backtests give the same results. Turn `LooseRefill` on to look
for icebergs with orders queued behind them.

## Limits

- A flagged level is "absorbed more than it showed and kept refilling", not proof of an iceberg. Only order ids on a feed that
  separates fills from cancels come close to proof, and then only for native icebergs.
- Nothing in these papers shows that a detected iceberg predicts the next price move. Frey and Sandås find the benefit is to the
  iceberg's owner; detected icebergs mostly attract counterparties.
- Parameters (`dt`, size tolerance, minimum refills) were fitted to ES or to Xetra stocks. Other markets need their own values.
- Hidden orders on other venues (NASDAQ non-displayed orders, dark pools, CME implied orders that are not broadcast) are outside
  this method.

## References

- Christensen, H. and Woodmansey, R. (2013). Prediction of Hidden Liquidity in the Limit Order Book of GLOBEX Futures. *Journal of Trading* 8(3).
- Zotikov, D. and Antonov, A. (2019). CME Iceberg Order Detection and Prediction. arXiv:1909.09495.
- Frey, S. and Sandås, P. (2017). The Impact of Iceberg Orders in Limit Order Books. *Quarterly Journal of Finance* 7(3).
- De Winne, R. and D'Hondt, C. (2007). Hide-and-Seek in the Market: Placing and Detecting Hidden Orders. *Review of Finance* 11.
- Bessembinder, H., Panayides, M. and Venkataraman, K. (2009). Hidden liquidity. *Journal of Financial Economics* 94.
- Hautsch, N. and Huang, R. (2012). On the Dark Side of the Market: Identifying and Analyzing Hidden Order Placements. SSRN 2004231.
- Esser, A. and Mönch, B. (2007). The navigation of an iceberg: the optimal use of hidden orders. *Finance Research Letters* 4(2).
- Hasbrouck, J. and Saar, G., as cited by Christensen and Woodmansey: hidden orders are 3 % of submitted limit orders but 12 % of executions, and over 25 % of limit orders are cancelled within two seconds (possibly "pinging" for hidden orders). I have not read this paper.
- Moro, E. et al. (2009). Market impact and trading profile of hidden orders in stock markets. *Physical Review E* 80.
- Fleming, M., Mizrach, B. and Nguyen, G. (2018). The microstructure of a US Treasury ECN: the BrokerTec platform. *Journal of Financial Markets* 40.
