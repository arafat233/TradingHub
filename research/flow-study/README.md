# Can footprint and big-trade features call buy, sell or wait? (GC.F, 5-minute bars)

Walk-forward test on 13 months of GC.F ticks (19 Sep 2025 to 16 Oct 2026, 71k five-minute bars). Thresholds are fixed on the first months
(to 30 Apr 2026) and applied unchanged to the last months (1 May to 16 Oct 2026).

Run: `python -I bars.py <folder with the part-*.parquet files> gc_bars_5m.csv` then `python -I evaluate.py gc_bars_5m.csv report.txt`.
The parquet files are the original LSE downloads in `%APPDATA%\Trading Hub\Research\gc-ticks`; copy them to a separate folder first.

## Features (per 5-minute bar)
- **Side** of each trade by the tick rule on sweeps (prints with the same timestamp are one aggressive order). The data has no real aggressor flag.
- **Delta**: buy minus sell volume, divided by volume. **Big trades**: net of sweeps of 20+ lots (top 0.28 % of sweeps, about 7 % of volume).
- **Stacked imbalance**: 3 or more consecutive price levels where buys at a level are at least 3x the sells one level below (or the reverse), minimum 3 lots.
- **Vote**: delta, big trades and stacked imbalance each say buy, sell or nothing; 2 of 3 (or all 3) agreeing = buy or sell, else wait.

## Result
No signal beat trading costs, and no signal had a gross edge that was stable between the train and test months. See `report-gc-5min.txt`.
- Hit rates 47-50 %. Gross average move per trade between -0.3 and +0.4 points against a round-trip cost of about 0.35 points.
- Best-looking case: all three agreeing, 60-minute hold, test months: gross +0.39 points (t = 1.0), net about 0. Not significant.
- The few large t-statistics (5-minute horizon, test months only) are negative and were not present in the train months.
- Vote at 15 minutes by month: 5 of 14 months positive gross, 0 of 14 positive after costs.
- Sanity check: the features are real. Their correlation with the SAME bar's move is 0.43 (delta), 0.48 (stacked imbalance), 0.32 (big trades);
  with the NEXT bar's move it is about 0 (-0.002 to -0.008).

## Limits
- Tick-rule sides are noisy; real exchange aggressor flags could give cleaner imbalances.
- Entry is at the bar close with no delay; cost is the quoted-spread proxy of the entry bar plus 0.05 points (assumptions).
- No book or order-level data, so depth, absorption and iceberg signals are not tested. One instrument, one regime (gold in a strong trend).
- The first download file hit the 2.5M-row cap and covers only about the first 15 days of its window; windows spanning gaps are dropped.
