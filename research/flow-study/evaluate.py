"""
Do footprint/big-trade features call buy, sell or wait on GC.F? Walk-forward on 5-minute bars.
Usage: python -I evaluate.py <gc_bars_5m.csv> <report.txt>

Thresholds are fixed on the TRAIN months and applied unchanged to the TEST months. A signal puts us long (+1), short (-1) or out (0) at the
close of the bar that produced it; the result is the price change over the next H bars. Overlapping trades are removed (one position at a
time, held H bars). Costs: the quoted spread proxy of the entry bar plus 0.05 points of commission, round trip (an assumption).
"""
import sys
import numpy as np
import pandas as pd

BARS = 5  # minutes per bar
HORIZONS = {"5 min": 1, "15 min": 3, "30 min": 6, "60 min": 12}
SPLIT = pd.Timestamp("2026-05-01", tz="UTC")
COMMISSION = 0.05
MIN_VOLUME = 20


def load(path):
    b = pd.read_csv(path, parse_dates=["time"]).sort_values("time").reset_index(drop=True)
    b = b[b.volume >= MIN_VOLUME].reset_index(drop=True)
    b["ret"] = b.close - b.open
    b["dnorm"] = (b.buy - b.sell) / b.volume
    b["bignet"] = b.big_buy - b.big_sell
    b["stack"] = b.stack_buy - b.stack_sell
    # forward price changes, only when the bars in between all exist
    t = b.time.to_numpy("datetime64[m]").astype("int64")
    for name, h in HORIZONS.items():
        fut = b.close.shift(-h)
        ok = (pd.Series(t).shift(-h) - pd.Series(t)) == h * BARS
        b["fwd_" + name] = (fut - b.close).where(ok)
    b["cost"] = b.spread.fillna(b.spread.median()).clip(upper=3.0) + COMMISSION
    return b


def trades(b, pos, col, h):
    """One position at a time. Returns gross, cost arrays of the taken trades."""
    p = pos.to_numpy()
    f = b[col].to_numpy()
    c = b.cost.to_numpy()
    gross, cost = [], []
    free = 0
    for i in range(len(p)):
        if i < free or p[i] == 0 or np.isnan(f[i]):
            continue
        gross.append(p[i] * f[i])
        cost.append(c[i])
        free = i + h
    return np.array(gross), np.array(cost)


def stats(g, c):
    n = len(g)
    if n < 5:
        return f"n={n:5d}  (too few)"
    net = g - c
    t = g.mean() / (g.std(ddof=1) / np.sqrt(n)) if g.std(ddof=1) > 0 else 0
    tn = net.mean() / (net.std(ddof=1) / np.sqrt(n))
    return f"n={n:5d}  hit={100 * (g > 0).mean():4.1f}%  gross={g.mean():+.3f}  t={t:+5.2f}  net={net.mean():+.3f}  t_net={tn:+5.2f}"


def main(path, out):
    b = load(path)
    train, test = b[b.time < SPLIT].reset_index(drop=True), b[b.time >= SPLIT].reset_index(drop=True)
    lines = []

    def P(s=""):
        lines.append(s)
        print(s)

    P(f"bars: {len(b)}  train {len(train)} ({train.time.min().date()} to {train.time.max().date()})  test {len(test)} ({test.time.min().date()} to {test.time.max().date()})")
    P(f"median cost per round trip (spread proxy + commission): {b.cost.median():.2f} points; median 5-min move {b.ret.abs().median():.2f}; median 60-min move {b['fwd_60 min'].abs().median():.2f}")
    P()

    # thresholds from TRAIN only
    q = {k: (train[k].quantile(0.10), train[k].quantile(0.20), train[k].quantile(0.80), train[k].quantile(0.90)) for k in ("dnorm", "ret")}

    def signals(d):
        s = {}
        s["delta extreme (top/bottom 10%)"] = pd.Series(np.where(d.dnorm >= q["dnorm"][3], 1, np.where(d.dnorm <= q["dnorm"][0], -1, 0)), index=d.index)
        s["big trades net (>=1 big sweep)"] = pd.Series(np.sign(d.bignet) * (d.bignet.abs() >= 20), index=d.index).astype(int)
        s["stacked imbalance"] = pd.Series(np.sign(d["stack"]), index=d.index).astype(int)
        s["bar momentum (top/bottom 10%)"] = pd.Series(np.where(d.ret >= q["ret"][3], 1, np.where(d.ret <= q["ret"][0], -1, 0)), index=d.index)
        c1 = np.where(d.dnorm >= q["dnorm"][2], 1, np.where(d.dnorm <= q["dnorm"][1], -1, 0))
        c2 = np.sign(d.bignet) * (d.bignet.abs() >= 20)
        c3 = np.sign(d["stack"])
        vote = c1 + c2 + c3
        s["vote: >=2 of 3 agree"] = pd.Series(np.where(vote >= 2, 1, np.where(vote <= -2, -1, 0)), index=d.index)
        s["vote: all 3 agree"] = pd.Series(np.where(vote == 3, 1, np.where(vote == -3, -1, 0)), index=d.index)
        return s

    st, se = signals(train), signals(test)
    for name in st:
        share = {k: 100 * (se[name] == k).mean() for k in (1, 0, -1)}
        P(f"== {name}   (test bars: buy {share[1]:.1f}%  wait {share[0]:.1f}%  sell {share[-1]:.1f}%)")
        for hn, h in HORIZONS.items():
            col = "fwd_" + hn
            P(f"   {hn:>6}  TRAIN {stats(*trades(train, st[name], col, h))}")
            P(f"   {'':>6}  TEST  {stats(*trades(test, se[name], col, h))}")
        P()

    # what "wait" looks like: average forward move by class on the test months, all bars (overlapping, so no t-stat)
    P("Average signed forward move by class, TEST months, every bar (points, gross; positive buy = price rose after a buy call)")
    for name in ("vote: >=2 of 3 agree", "delta extreme (top/bottom 10%)"):
        for hn in HORIZONS:
            col = "fwd_" + hn
            r = {k: test.loc[se[name] == k, col].mean() for k in (1, 0, -1)}
            P(f"   {name:34s} {hn:>6}:  after BUY {r[1]:+.3f}   after WAIT {r[0]:+.3f}   after SELL {r[-1]:+.3f}")
    P()

    # monthly consistency of the vote at 15 minutes (gross), whole sample with the train thresholds
    allsig = signals(b)["vote: >=2 of 3 agree"]
    g, c = [], []
    month = []
    p = allsig.to_numpy(); f = b["fwd_15 min"].to_numpy(); cc = b.cost.to_numpy(); free = 0
    for i in range(len(p)):
        if i < free or p[i] == 0 or np.isnan(f[i]):
            continue
        month.append(b.time[i].strftime("%Y-%m")); g.append(p[i] * f[i]); c.append(cc[i]); free = i + 3
    m = pd.DataFrame({"m": month, "g": g, "c": c}).groupby("m").agg(n=("g", "size"), gross=("g", "mean"), cost=("c", "mean"))
    m["net"] = m.gross - m.cost
    P("Vote >=2 of 3, 15-minute hold, by month (thresholds from the train months):")
    P(m.round(3).to_string())
    P(f"months with positive gross: {(m.gross > 0).sum()} of {len(m)};  with positive net: {(m.net > 0).sum()} of {len(m)}")
    open(out, "w", encoding="utf8").write("\n".join(lines))


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
