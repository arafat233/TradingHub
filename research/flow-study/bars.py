"""
GC.F tick parquet parts -> 5-minute bars with footprint-style flow features.
Usage: python -I bars.py <folder with part-*.parquet> <out.csv>

Side comes from the tick rule on events: all prints with the same timestamp are one sweep (one aggressive order), its direction is the
sign of the price change from the previous event (a zero change falls back to the move inside the sweep, then to the previous direction).
Features per bar: volume, delta, big-trade net, stacked diagonal imbalances, plus open/high/low/close and the quoted spread proxy.
"""
import glob, os, sys
import numpy as np
import pandas as pd
import pyarrow.parquet as pq

BAR_US = 5 * 60 * 1_000_000
BIG = 20          # lots in one sweep that counts as a big trade (top 0.28 % of sweeps, about 7 % of volume)
RATIO = 3.0       # diagonal imbalance ratio
MIN_LEVEL = 3     # lots at the level for an imbalance to count
STACK = 3         # consecutive imbalanced levels = a stacked imbalance
LEVEL = 10        # price levels per point (tick = 0.1)


def stacked(lv: np.ndarray, buy: np.ndarray, sell: np.ndarray):
    """lv sorted ascending, contiguous or not. Returns (# buy stacks, # sell stacks)."""
    # diagonal: buy at L against sell at L-1; sell at L against buy at L+1
    idx = {int(l): i for i, l in enumerate(lv)}
    b_imb = np.zeros(len(lv), bool)
    s_imb = np.zeros(len(lv), bool)
    for i, l in enumerate(lv):
        j = idx.get(int(l) - 1)
        if j is not None and buy[i] >= MIN_LEVEL and buy[i] >= RATIO * max(sell[j], 0.5):
            b_imb[i] = True
        k = idx.get(int(l) + 1)
        if k is not None and sell[i] >= MIN_LEVEL and sell[i] >= RATIO * max(buy[k], 0.5):
            s_imb[i] = True

    def runs(flags):
        n = 0
        run = 0
        prev = None
        for i, f in enumerate(flags):
            if f and (prev is None or int(lv[i]) == prev + 1):
                run += 1
            elif f:
                run = 1
            else:
                run = 0
            prev = int(lv[i]) if f else None
            if run == STACK:
                n += 1
        return n

    return runs(b_imb), runs(s_imb)


def main(folder, out):
    files = sorted(f for f in glob.glob(os.path.join(folder, "part-*.parquet")))
    rows = []
    last_price = np.nan
    last_dir = 0
    for f in files:
        t = pq.read_table(f, columns=["ts", "price", "volume", "ask"]).to_pandas()
        t["ts"] = t["ts"].astype("int64")  # microseconds since the epoch
        t = t.sort_values("ts", kind="stable").reset_index(drop=True)
        ts = t["ts"].to_numpy()
        price = t["price"].to_numpy()
        vol = t["volume"].to_numpy()
        ask = t["ask"].to_numpy()

        # events = prints with the same timestamp
        new = np.empty(len(ts), bool)
        new[0] = True
        new[1:] = ts[1:] != ts[:-1]
        ev = np.cumsum(new) - 1
        first = np.flatnonzero(new)
        last = np.append(first[1:], len(ts)) - 1
        ev_first, ev_last = price[first], price[last]
        ev_vol = np.add.reduceat(vol, first)
        prev_last = np.concatenate([[last_price], ev_last[:-1]])
        d = np.sign(ev_last - prev_last)
        d = np.where(np.isnan(d), 0, d)
        z = d == 0
        d[z] = np.sign(ev_last - ev_first)[z]
        # a zero tick keeps the previous direction
        d = pd.Series(d).replace(0, np.nan).ffill().fillna(last_dir).fillna(0).to_numpy()
        last_price, last_dir = ev_last[-1], d[-1]

        side = d[ev]                       # direction per print
        bar = ts // BAR_US
        ev_bar = bar[first]
        df = pd.DataFrame({"bar": bar, "price": price, "vol": vol, "side": side, "spr": ask - price})
        df["buy"] = np.where(side > 0, vol, 0.0)
        df["sell"] = np.where(side < 0, vol, 0.0)
        agg = df.groupby("bar").agg(open=("price", "first"), high=("price", "max"), low=("price", "min"), close=("price", "last"),
                                    volume=("vol", "sum"), buy=("buy", "sum"), sell=("sell", "sum"), spread=("spr", "median"),
                                    prints=("vol", "size"))

        evdf = pd.DataFrame({"bar": ev_bar, "vol": ev_vol, "dir": d})
        big = evdf[evdf.vol >= BIG]
        agg["big_buy"] = big[big.dir > 0].groupby("bar").vol.sum()
        agg["big_sell"] = big[big.dir < 0].groupby("bar").vol.sum()
        agg["big_n"] = big.groupby("bar").vol.size()

        # footprint: volume per (bar, price level) by side
        df["lv"] = np.round(df["price"] * LEVEL).astype("int64")
        fp = df.groupby(["bar", "lv"])[["buy", "sell"]].sum().reset_index()
        sb, ss = {}, {}
        for b, g in fp.groupby("bar", sort=False):
            g = g.sort_values("lv")
            sb[b], ss[b] = stacked(g["lv"].to_numpy(), g["buy"].to_numpy(), g["sell"].to_numpy())
        agg["stack_buy"] = pd.Series(sb)
        agg["stack_sell"] = pd.Series(ss)
        agg = agg.fillna({"big_buy": 0, "big_sell": 0, "big_n": 0, "stack_buy": 0, "stack_sell": 0})
        agg["part"] = os.path.basename(f)
        rows.append(agg.reset_index())
        print(os.path.basename(f), len(t), "events", len(first), "bars", len(agg), "big share of events %.3f%%" % (100 * (ev_vol >= BIG).mean()), flush=True)

    allb = pd.concat(rows).drop_duplicates("bar", keep="first").sort_values("bar")
    allb["time"] = pd.to_datetime(allb["bar"] * BAR_US, unit="us", utc=True)
    allb.to_csv(out, index=False)
    print("bars", len(allb), allb.time.min(), allb.time.max())


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
