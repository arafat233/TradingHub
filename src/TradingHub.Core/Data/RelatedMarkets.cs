using System.Globalization;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>How a related market is doing next to the one on the chart. Changes are in basis points (1 bp = 0.01%).</summary>
public sealed record PeerStats(string Symbol, decimal Last, decimal Change5Bp, decimal Change30Bp, decimal Relative30Bp, string Lead)
{
    public string Text()
    {
        static string S(decimal v) => v.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
        return $"{Symbol}  5 min {S(Change5Bp)} bp · 30 min {S(Change30Bp)} bp · this one vs it {S(Relative30Bp)} bp · {Lead}";
    }
}

/// <summary>
/// Which markets move with which, and how to measure it from two tapes (for the Insights panel's "related markets"). Not to be confused with
/// <see cref="CrossMarket"/>, the research study of what one market did after a big order in another.
/// </summary>
public static class RelatedMarkets
{
    private static readonly Dictionary<string, string[]> Futures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ES.F"] = new[] { "NQ.F", "MES.F", "YM.F", "RTY.F" },
        ["NQ.F"] = new[] { "ES.F", "MNQ.F", "YM.F", "RTY.F" },
        ["MES.F"] = new[] { "ES.F", "MNQ.F", "NQ.F" },
        ["MNQ.F"] = new[] { "NQ.F", "MES.F", "ES.F" },
        ["YM.F"] = new[] { "ES.F", "NQ.F", "RTY.F" },
        ["RTY.F"] = new[] { "ES.F", "NQ.F", "YM.F" },
        ["GC.F"] = new[] { "SI.F", "PL.F", "6E.F" },
        ["SI.F"] = new[] { "GC.F", "PL.F", "HG.F" },
        ["CL.F"] = new[] { "NG.F", "HO.F", "RB.F" },
        ["HO.F"] = new[] { "CL.F", "RB.F" },
        ["RB.F"] = new[] { "CL.F", "HO.F" },
        ["6E.F"] = new[] { "6B.F", "GC.F" },
        ["6B.F"] = new[] { "6E.F" },
        ["ZN.F"] = new[] { "ZB.F" },
        ["ZB.F"] = new[] { "ZN.F" },
    };

    /// <summary>The symbols worth watching next to this instrument: the sibling index and metals futures, or Ethereum for Bitcoin and the reverse.</summary>
    public static IReadOnlyList<string> PeersOf(Instrument inst)
    {
        if (Futures.TryGetValue(inst.Symbol, out var list)) return list;
        string s = inst.Symbol;
        if (s.StartsWith("BTC", StringComparison.OrdinalIgnoreCase)) return new[] { "ETH" + s[3..] };
        if (s.StartsWith("ETH", StringComparison.OrdinalIgnoreCase)) return new[] { "BTC" + s[3..] };
        return Array.Empty<string>();
    }

    private static long PriceAt(TradeStore store, DateTime time)
    {
        int i = store.LowerBound(time.AddTicks(1)) - 1;
        return i >= 0 ? store.GetPriceTicks(i) : 0;
    }

    /// <summary>
    /// The peer's 5 and 30 minute moves, the gap between this market's 30 minute move and the peer's, and who leads: both tapes are sampled every
    /// 5 seconds for the last 20 minutes, and the correlation of this market's moves with the peer's moves a few steps earlier (and later) is looked at.
    /// If the best correlation is clearly higher than the one at no delay and at least 0.2, the one that moves first is named; otherwise they just move together.
    /// Null when either tape does not cover the last half hour.
    /// </summary>
    public static PeerStats? Compute(Instrument ownInst, TradeStore own, Instrument peerInst, TradeStore peer, DateTime now)
    {
        if (own.Count == 0 || peer.Count == 0) return null;
        if (own.FirstTime > now.AddMinutes(-30) || peer.FirstTime > now.AddMinutes(-30)) return null;
        long o0 = PriceAt(own, now), o5 = PriceAt(own, now.AddMinutes(-5)), o30 = PriceAt(own, now.AddMinutes(-30));
        long p0 = PriceAt(peer, now), p5 = PriceAt(peer, now.AddMinutes(-5)), p30 = PriceAt(peer, now.AddMinutes(-30));
        if (new[] { o0, o5, o30, p0, p5, p30 }.Any(v => v <= 0)) return null;
        decimal Bp(long a, long b) => (decimal)(a - b) / b * 10_000m;
        decimal own30 = Bp(o0, o30), peer30 = Bp(p0, p30);

        // a 5 second grid of both prices over the last 20 minutes, as returns
        const int step = 5, points = 240, maxLag = 6;
        var oRet = new double[points];
        var pRet = new double[points];
        long po = PriceAt(own, now.AddSeconds(-step * points)), pp = PriceAt(peer, now.AddSeconds(-step * points));
        for (int i = 0; i < points; i++)
        {
            var t = now.AddSeconds(-step * (points - 1 - i));
            long co = PriceAt(own, t), cp = PriceAt(peer, t);
            oRet[i] = po > 0 ? (double)(co - po) / po : 0;
            pRet[i] = pp > 0 ? (double)(cp - pp) / pp : 0;
            (po, pp) = (co, cp);
        }

        double Corr(int lag) // own at i against peer at i - lag: a positive lag means the peer moved first
        {
            var a = new List<double>();
            var b = new List<double>();
            for (int i = 0; i < points; i++)
            {
                int j = i - lag;
                if (j < 0 || j >= points) continue;
                a.Add(oRet[i]);
                b.Add(pRet[j]);
            }

            if (a.Count < 100) return 0;
            double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Count; i++) { sab += (a[i] - ma) * (b[i] - mb); saa += (a[i] - ma) * (a[i] - ma); sbb += (b[i] - mb) * (b[i] - mb); }
            return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0;
        }

        double c0 = Corr(0), best = c0;
        int bestLag = 0;
        for (int lag = -maxLag; lag <= maxLag; lag++)
        {
            double c = Corr(lag);
            if (c > best) (best, bestLag) = (c, lag);
        }

        // with 240 samples a correlation of 0.08 happens by chance among 13 lags, so a leader needs a clearly real one (real index futures sit around 0.5 to 0.8)
        string lead = bestLag != 0 && best >= 0.2 && best >= c0 + 0.05
            ? bestLag > 0 ? $"{peerInst.Symbol} moves first by about {bestLag * step} s (corr {best:0.00})" : $"{ownInst.Symbol} moves first by about {-bestLag * step} s (corr {best:0.00})"
            : c0 >= 0.2 ? $"move together (corr {c0:0.00})" : "little connection right now";
        return new PeerStats(peerInst.Symbol, peerInst.FromTicks(p0), Bp(p0, p5), peer30, own30 - peer30, lead);
    }
}
