namespace TradingHub.Market;

public enum BookSide : byte
{
    Bid = 0,
    Ask = 1,
}

/// <summary>A book level: price (in ticks) and size.</summary>
public readonly record struct BookLevel(long PriceTicks, decimal Size);

/// <summary>Change of a book level (absolute size; 0 = level removed).</summary>
public readonly record struct BookChange(BookSide Side, long PriceTicks, decimal Size);

/// <summary>
/// Immutable snapshot of the order book. The feeds generate it on their own thread and the UI consumes it
/// without locks. Bids sorted from the highest price to the lowest, asks from the lowest to the highest.
/// </summary>
public sealed class DepthSnapshot
{
    public static readonly DepthSnapshot Empty = new(Array.Empty<BookLevel>(), Array.Empty<BookLevel>(), DateTime.MinValue);

    public BookLevel[] Bids { get; }
    public BookLevel[] Asks { get; }
    public DateTime Time { get; }

    public DepthSnapshot(BookLevel[] bids, BookLevel[] asks, DateTime time)
    {
        Bids = bids;
        Asks = asks;
        Time = time;
    }

    public bool HasBid => Bids.Length > 0;
    public bool HasAsk => Asks.Length > 0;
    public long BestBidTicks => Bids.Length > 0 ? Bids[0].PriceTicks : long.MinValue;
    public long BestAskTicks => Asks.Length > 0 ? Asks[0].PriceTicks : long.MaxValue;
    public decimal BestBidSize => Bids.Length > 0 ? Bids[0].Size : 0;
    public decimal BestAskSize => Asks.Length > 0 ? Asks[0].Size : 0;

    /// <summary>Size at an exact price (binary search).</summary>
    public decimal GetSize(BookSide side, long priceTicks)
    {
        var arr = side == BookSide.Bid ? Bids : Asks;
        int lo = 0, hi = arr.Length - 1;
        bool descending = side == BookSide.Bid;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            long p = arr[mid].PriceTicks;
            if (p == priceTicks) return arr[mid].Size;
            bool goRight = descending ? p > priceTicks : p < priceTicks;
            if (goRight) lo = mid + 1; else hi = mid - 1;
        }
        return 0;
    }

    /// <summary>Sum of sizes in the range [fromTicks, toTicks] (inclusive) on one side.</summary>
    public decimal SumRange(BookSide side, long fromTicks, long toTicks)
    {
        if (fromTicks > toTicks) (fromTicks, toTicks) = (toTicks, fromTicks);
        decimal sum = 0;
        var arr = side == BookSide.Bid ? Bids : Asks;
        foreach (var l in arr)
        {
            if (l.PriceTicks >= fromTicks && l.PriceTicks <= toTicks) sum += l.Size;
        }
        return sum;
    }
}

/// <summary>A depth update: the full snapshot plus the changes that produced it.</summary>
public sealed class DepthUpdate
{
    public required DepthSnapshot Snapshot { get; init; }
    public IReadOnlyList<BookChange> Changes { get; init; } = Array.Empty<BookChange>();
    public bool IsFullRefresh { get; init; }
}

/// <summary>
/// Mutable book maintained by the feeds (a single thread). It generates <see cref="DepthSnapshot"/>.
/// </summary>
public sealed class OrderBookBuilder
{
    private readonly SortedDictionary<long, decimal> _bids = new(Comparer<long>.Create((a, b) => b.CompareTo(a)));
    private readonly SortedDictionary<long, decimal> _asks = new();
    private readonly List<BookChange> _pending = new();

    public int BidCount => _bids.Count;
    public int AskCount => _asks.Count;

    public void Clear()
    {
        _bids.Clear();
        _asks.Clear();
        _pending.Clear();
    }

    public void Set(BookSide side, long priceTicks, decimal size)
    {
        var dict = side == BookSide.Bid ? _bids : _asks;
        if (size <= 0)
        {
            if (dict.Remove(priceTicks)) _pending.Add(new BookChange(side, priceTicks, 0));
        }
        else
        {
            if (!dict.TryGetValue(priceTicks, out var old) || old != size)
            {
                dict[priceTicks] = size;
                _pending.Add(new BookChange(side, priceTicks, size));
            }
        }
    }

    /// <summary>Removes crossed levels (it can happen with feeds that send bid/ask separately).</summary>
    public void Uncross(BookSide aggressorSideUpdated)
    {
        if (_bids.Count == 0 || _asks.Count == 0) return;
        long bestBid = _bids.Keys.First();
        long bestAsk = _asks.Keys.First();
        if (bestBid < bestAsk) return;
        if (aggressorSideUpdated == BookSide.Bid)
        {
            foreach (var k in _asks.Keys.TakeWhile(k => k <= bestBid).ToList()) Set(BookSide.Ask, k, 0);
        }
        else
        {
            foreach (var k in _bids.Keys.TakeWhile(k => k >= bestAsk).ToList()) Set(BookSide.Bid, k, 0);
        }
    }

    public DepthUpdate BuildUpdate(DateTime time, int maxLevelsPerSide = 1000, bool fullRefresh = false)
    {
        var bids = new BookLevel[Math.Min(maxLevelsPerSide, _bids.Count)];
        int i = 0;
        foreach (var kv in _bids)
        {
            if (i >= bids.Length) break;
            bids[i++] = new BookLevel(kv.Key, kv.Value);
        }

        var asks = new BookLevel[Math.Min(maxLevelsPerSide, _asks.Count)];
        i = 0;
        foreach (var kv in _asks)
        {
            if (i >= asks.Length) break;
            asks[i++] = new BookLevel(kv.Key, kv.Value);
        }

        var changes = _pending.ToArray();
        _pending.Clear();
        return new DepthUpdate { Snapshot = new DepthSnapshot(bids, asks, time), Changes = changes, IsFullRefresh = fullRefresh };
    }
}
