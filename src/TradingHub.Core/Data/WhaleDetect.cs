namespace TradingHub.Data;

/// <summary>Which kind of big order the whale markers show.</summary>
public enum WhaleMode
{
    /// <summary>Any market order (all its fills added up) at or above the size threshold.</summary>
    Aggressive,

    /// <summary>A big order that ate three or more price levels in one go.</summary>
    Sweep,

    /// <summary>A big order that hit near a level that already matters (yesterday's high / low / POC… or a remembered big-trade level).</summary>
    Level,

    /// <summary>One single print at or above the threshold (the order is not added up with its other fills).</summary>
    BigPrint,
}

/// <summary>What the detector needs to know about one market order (all its fills together).</summary>
public readonly record struct OrderShape(decimal Volume, decimal MaxPrint, int Levels, bool NearLevel);

/// <summary>A cluster of whale orders on one side, close in price.</summary>
public sealed record WhaleZone(decimal Low, decimal High, bool Buy, int Count, decimal Money);

/// <summary>Rules for the whale view: which orders count, a sentence for the range on screen, the whales' average price and where they cluster.</summary>
public static class WhaleDetect
{
    public const int SweepLevels = 3;

    /// <summary>The number the threshold is compared with: the single print in <see cref="WhaleMode.BigPrint"/>, else the order's whole size.</summary>
    public static decimal SizeOf(WhaleMode mode, OrderShape o) => mode == WhaleMode.BigPrint ? o.MaxPrint : o.Volume;

    public static bool Passes(WhaleMode mode, OrderShape o, decimal threshold)
    {
        if (o.Volume <= 0 || SizeOf(mode, o) < threshold) return false;
        return mode switch
        {
            WhaleMode.Sweep => o.Levels >= SweepLevels,
            WhaleMode.Level => o.NearLevel,
            _ => true,
        };
    }

    /// <summary>One plain sentence about the whale orders in view. Context, not a signal: it never says what to do.</summary>
    public static string Summary(string since, decimal priceChangePct, int count, decimal buyMoney, decimal sellMoney)
    {
        if (count == 0) return $"In view since {since}: price {priceChangePct:+0.00;-0.00}% · no whale orders in view.";
        decimal net = buyMoney - sellMoney, total = buyMoney + sellMoney;
        string verdict = total <= 0 || Math.Abs(net) / total < 0.25m
            ? "whales are two-sided here — no clear big-player direction"
            : net > 0 ? "whales are net buyers" : "whales are net sellers";
        return $"In view since {since}: price {priceChangePct:+0.00;-0.00}% · whales net {(net < 0 ? "-" : "+")}{LiquidationClusters.Money(Math.Abs(net))} ({count} bubbles) — {verdict}. Context, not a signal.";
    }

    /// <summary>Volume-weighted average price, or null with no volume.</summary>
    public static decimal? AveragePrice(IEnumerable<(decimal Price, decimal Volume)> fills)
    {
        decimal pv = 0, v = 0;
        foreach (var (p, vol) in fills) { pv += p * vol; v += vol; }
        return v > 0 ? pv / v : null;
    }

    /// <summary>
    /// Groups same-side whale orders whose prices chain within <paramref name="reach"/> of each other; a group of at least <paramref name="minCount"/>
    /// is a zone where the whales kept buying (or selling).
    /// </summary>
    public static List<WhaleZone> Zones(IEnumerable<(decimal Price, decimal Money, bool Buy)> orders, decimal reach, int minCount = 3)
    {
        var zones = new List<WhaleZone>();
        foreach (bool buy in new[] { true, false })
        {
            var side = orders.Where(o => o.Buy == buy).OrderBy(o => o.Price).ToList();
            int i = 0;
            while (i < side.Count)
            {
                int j = i;
                while (j + 1 < side.Count && side[j + 1].Price - side[j].Price <= reach) j++;
                if (j - i + 1 >= minCount)
                    zones.Add(new WhaleZone(side[i].Price, side[j].Price, buy, j - i + 1, side.Skip(i).Take(j - i + 1).Sum(o => o.Money)));
                i = j + 1;
            }
        }

        return zones;
    }
}
