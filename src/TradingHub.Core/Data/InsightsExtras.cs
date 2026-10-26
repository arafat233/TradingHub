using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>What the biggest prints did over a window, in money, and what everybody else did.</summary>
public sealed record WhaleFlowResult(decimal Buy, decimal Sell, int Prints, decimal Threshold, decimal OthersNet)
{
    public decimal Net => Buy - Sell;
}

/// <summary>
/// Large traders against the crowd, from the tape alone: a print is "large" when its money value is in the top 0.1% of the last 20 000 trades (and at
/// least five times the median one), so it adapts to each market. Over the window it adds up what those prints bought and sold, and what every other
/// trade did. It is the panel's own cheap version of the Big Trades indicator's whale flow, so the two can be set side by side.
/// </summary>
public static class WhaleFlow
{
    public static WhaleFlowResult? Compute(TradeStore store, Instrument inst, DateTime now, TimeSpan window)
    {
        int i1 = store.LowerBound(now.AddTicks(1)), i0 = store.LowerBound(now - window);
        if (i1 - i0 < 200) return null;
        int from = Math.Max(0, i1 - 20_000);
        var sizes = new List<decimal>(i1 - from);
        for (int i = from; i < i1; i++) sizes.Add(store.GetPrice(i) * store.GetVolume(i) * inst.PointValue);
        sizes.Sort();
        decimal threshold = Math.Max(sizes[(int)(0.999 * (sizes.Count - 1))], 5 * sizes[sizes.Count / 2]);
        decimal buy = 0, sell = 0, others = 0;
        int prints = 0;
        for (int i = i0; i < i1; i++)
        {
            decimal money = store.GetPrice(i) * store.GetVolume(i) * inst.PointValue;
            bool isBuy = store.GetSide(i) == TradeSide.Buy;
            if (money >= threshold)
            {
                prints++;
                if (isBuy) buy += money; else sell += money;
            }
            else others += isBuy ? money : -money;
        }

        return new WhaleFlowResult(buy, sell, prints, threshold, others);
    }

    /// <summary>Whether the large traders' direction agrees with a lean (+1 buy, -1 sell, 0 none), as a sentence; null when there is nothing to say.</summary>
    public static string? VersusLean(WhaleFlowResult w, int lean)
    {
        if (w.Prints == 0 || Math.Abs(w.Net) < 2 * w.Threshold) return null; // too little to call a direction
        int whaleSign = w.Net > 0 ? 1 : -1;
        if (lean == 0) return null;
        return whaleSign == lean
            ? $"Large traders agree with the lean ({(whaleSign > 0 ? "net buying" : "net selling")})."
            : $"Large traders are against the lean: net {(whaleSign > 0 ? "buying" : "selling")} while the panel leans {(lean > 0 ? "buy" : "sell")}.";
    }
}

/// <summary>Whether the data is too old to say anything: the decision then says so instead of showing a call made from stale numbers.</summary>
public static class StaleGuard
{
    /// <summary>
    /// Seconds since the last trade if that is more than the market's usual gap allows (ten times the median gap between its last hundred trades,
    /// but at least 15 and at most 300 seconds), or null. A replay or the simulator is never stale.
    /// </summary>
    public static int? StaleSeconds(TradeStore store, DateTime now, bool live)
    {
        if (!live || store.Count < 20) return null;
        double since = (now - store.LastTime).TotalSeconds;
        int n = Math.Min(100, store.Count - 1);
        var gaps = new List<double>(n);
        for (int i = store.Count - n; i < store.Count; i++) gaps.Add((store.GetTime(i) - store.GetTime(i - 1)).TotalSeconds);
        gaps.Sort();
        double limit = Math.Clamp(10 * gaps[gaps.Count / 2], 15, 300);
        return since > limit ? (int)since : null;
    }
}

/// <summary>Above or below the gamma flip: the sign of dealers' net gamma, which decides whether their hedging tends to damp or feed price moves.</summary>
public static class GammaRegime
{
    public static (int Sign, string Chip, string Tip)? Of(decimal price, decimal? flip)
    {
        if (flip is not { } f || price <= 0) return null;
        return price >= f
            ? (1, "Γ+ above the gamma flip", $"Price is above the gamma flip ({f:0.####}): dealers' hedging tends to damp moves here (assuming they are long the calls and short the puts).")
            : (-1, "Γ− below the gamma flip", $"Price is below the gamma flip ({f:0.####}): dealers' hedging tends to feed moves here (assuming they are long the calls and short the puts).");
    }
}

/// <summary>The session's headline numbers (the session is the UTC day): the last minute's delta, the money delta since the day began, and the price with the most volume.</summary>
public sealed record SessionStats(decimal CandleDelta, decimal DeltaMoney, decimal? PocPrice);
