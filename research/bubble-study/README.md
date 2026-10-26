# Bubble study — does a big trade mark a level?

A measurement harness, not a strategy. It classifies every large market order from the footprint of the
candle it landed in, then follows the price that order marked to see whether the market respected it.

The taxonomy comes from [Skydweller's "Why The Same Bubble Reverses The Market Once And Does Nothing The
Next Time"](https://www.youtube.com/watch?v=GUVgptvqzsk): a big trade is a punch, the bubble says somebody
swung, and the footprint says whether it landed. The video presents it as a way of *reading* order flow,
so this measures it as a reading — whether the level it marks holds — and never places an order.

## The classification

Eight patterns on the cheat sheet collapse into six that cover every case. The sheet splits the two body
cases by candle colour and draws the wick cases on both sheets; it also leaves out the counter-directional
wicks (a bubble in the low of a green candle), which are the same phenomenon it names elsewhere.

| Class | Where the print is | What it means |
|---|---|---|
| `Acceptance` | body, with the close | effort = result, the aggressor got through |
| `AbsorptionContinuation` | body, against the close | effort, no result: the other side held |
| `AbsorptionContrarianTop` | top rows, a buyer, nothing above | the move's own driver ran out |
| `RejectionTop` | top rows, a seller, wick built by selling | someone answered at the high |
| `AbsorptionContrarianBottom` | bottom rows, a seller, nothing below | mirror of the above |
| `RejectionBottom` | bottom rows, a buyer, wick built by buying | mirror of the above |

Absorption and rejection need different proof. Absorption is the aggressor reaching the edge and nobody
following. Rejection is the wick being *built* by the other side — "blocked" cannot be asked of it, because
a seller at the high has the whole body below them.

## Parameters

Every threshold is relative, so the same settings travel between instruments. Absolute ones do not: eight
ticks is most of a GC candle and nothing at all on BTC.

| Parameter | Default | Notes |
|---|---|---|
| `BigPercentile` | 99.7 | percentile of recent orders that counts as big (matches `BigOrderDetector`) |
| `BigSample` | 3000 | orders in the rolling window; thin hours span more wall clock, which is wanted |
| `RowTicks` | 1 | ticks per footprint row. Right for GC; BTC needs ~40 to be a readable ladder |
| `ExtremeRows` | 2 | rows from the candle's edge that still count as "at the top/bottom" |
| `FollowThrough` | 0.05 | volume past the print over volume in its row, under which nobody followed |
| `LevelBand` | 0.5 | distance to a prior-day level, as a fraction of average candle range |
| `Horizon` | 12 | bars the level is followed for |
| `Placebos` | 3 | random-price controls emitted per event |

## The two controls, and why both are needed

Each event also emits placebo levels tracked identically (`placebo` column: `0` real, `1` random, `2` edge).

1. **Random price in the same candle.** Controls for market state.
2. **The candle's own high or low, with no big trade attached.** Controls for geometry.

The second one is the one that matters, and leaving it out produces a false positive. The wick readings sit
at the extreme by construction, and a random price sits mid-candle; extremes are mechanically more
"respected" because price is already on one side of them. Measured against random prices the wick patterns
look excellent. Measured against the bare extreme they disappear.

## What it found (BTC, 7 days, M5 — not conclusive)

```
                               n     real   randPx   edge    vs rand   vs edge
  AbsorptionContrarianTop      50     0.44    1.38   0.39     -0.94     +0.05
  RejectionTop                147     0.44    0.86   0.41     -0.42     +0.03
  AbsorptionContrarianBottom   54     0.62    1.34   0.62     -0.72     +0.01
  RejectionBottom             130     0.67    1.12   0.64     -0.45     +0.03
```

The statistic is `max_beyond / max_held`: how far price ran past the level against how far it ran on the
side the reading says. Lower is better.

Every wick pattern ties the bare candle extreme or loses to it. On this data the location of a large
aggressive order carries no information about level integrity beyond what the high or low already carries.

This tests one leg of a three-legged claim. The video says bubble **and** a pre-existing wall **and** the
next candle confirming; the wall subsample here is n=16. Seven days of BTC also yields only 50–147 events
per wick pattern, which leaves room for a real but modest effect. It is a null result on a narrow test, not
a refutation.

## Running it

In the app: copy `BubbleStudy.cs` into `~/Documents/Trading Hub/Strategies` and compile (⌘B). It writes one
CSV per run to `~/Documents/Trading Hub/Studies`.

Headless, over recorded ticks:

```bash
dotnet build src/TradingHub.Strategies -c Release
dotnet run --project research/bubble-study/runner -- <feed> <symbol> <tickSize> <from> <to> [rowTicks]
dotnet run --project research/bubble-study/runner -- bybit-linear BTCUSDT 0.1 2026-10-19 2026-10-26 40
```

## Next

- **GC via Databento** (trades back to 2010, real aggressor side, continuous contract). Thousands of wick
  events instead of fifty, on the market the concept was built for.
- **Test the conjunction** rather than the bubble alone: pattern + prior-day level + next-candle close.
- **Icebergs.** `TrackIcebergs` needs the recorded order book, which only exists going forward from the
  moment recording is switched on — Databento is trades-only, so this history cannot be bought later.
  "The rest is still coming" is a more mechanical claim than "this level holds" and may test better.
