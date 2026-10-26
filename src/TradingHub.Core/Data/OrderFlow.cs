using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Turns real order-level events (a feed that publishes every order: Bitfinex, Coinbase with a key) into the same add / pulled / flash / eaten
/// list the heatmap shows when it only has price levels (<see cref="BookEventDetector"/>), but measured, not inferred: it is one real order
/// that was added or cancelled, with its own id. A "big" order is an outlier among the sizes of the orders seen recently (at least 6 x their
/// median and 3 x their 90th percentile), so it adapts to each market. Feeds that cannot say why an order left (Remove) are classified here:
/// trades at its price in the second around it that add up to at least half its size mean it was filled, otherwise it was cancelled.
/// </summary>
public sealed class OrderFlowDetector
{
    private const int Samples = 1000, MinSamples = 200;
    private static readonly TimeSpan FlashLife = TimeSpan.FromSeconds(3), Settle = TimeSpan.FromSeconds(1);

    private sealed class Order
    {
        public DateTime Since;
        public long Ticks;
        public bool Ask;
        public decimal Size;
    }

    private readonly Dictionary<long, Order> _big = new();
    private readonly List<(DateTime Time, Order Order)> _pending = new();
    private readonly Queue<decimal> _sizes = new();
    private decimal _min;
    private int _sinceMin;

    /// <summary>True once the feed has sent any order event (the window then prefers this list to the inferred one).</summary>
    public bool Active { get; private set; }

    /// <summary>The size an order needs to count as big (0 = still learning what is usual).</summary>
    public decimal BigSize => _min;

    /// <summary>How long events are kept.</summary>
    public TimeSpan Keep { get; set; } = TimeSpan.FromMinutes(30);

    public List<BookEvent> Events { get; } = new();

    public void Reset()
    {
        _big.Clear();
        _pending.Clear();
        _sizes.Clear();
        Events.Clear();
        _min = 0;
        _sinceMin = 0;
        Active = false;
    }

    public void Apply(IReadOnlyList<OrderEvent> events)
    {
        foreach (var e in events)
        {
            Active = true;
            switch (e.Kind)
            {
                case OrderEventKind.Add:
                    Learn(e.Size);
                    Track(e);
                    break;
                case OrderEventKind.Modify:
                    if (_big.TryGetValue(e.OrderId, out var o)) { o.Ticks = e.PriceTicks; o.Size = e.Size; }
                    else Track(e); // an order amended up to a big size is a new big order
                    break;
                default:
                    if (!_big.Remove(e.OrderId, out var gone)) break;
                    gone.Size = e.Size > 0 ? e.Size : gone.Size;
                    if (e.Kind == OrderEventKind.Remove) _pending.Add((e.Time, gone));
                    else Close(e.Time, gone, e.Kind == OrderEventKind.Fill);
                    break;
            }
        }

        if (Events.Count > 0 && events.Count > 0)
        {
            var cutoff = events[^1].Time - Keep;
            if (Events[0].Time < cutoff) Events.RemoveAll(x => x.Time < cutoff);
        }
    }

    /// <summary>Decides, for the orders that left without a reason, whether the trades around them filled them (waits a second so the trades have arrived).</summary>
    public void Resolve(TradeStore store, DateTime now)
    {
        if (_pending.Count == 0) return;
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            var (time, order) = _pending[i];
            if (now - time < Settle) continue;
            _pending.RemoveAt(i);
            decimal traded = TradedAround(store, time, order.Ticks, order.Ask);
            Close(time, order, traded >= 0.5m * order.Size);
        }

        Events.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    private void Track(OrderEvent e)
    {
        if (_min <= 0 || e.Size < _min) return;
        _big[e.OrderId] = new Order { Since = e.Time, Ticks = e.PriceTicks, Ask = e.Ask, Size = e.Size };
        Events.Add(new BookEvent(e.Time, BookEventKind.Added, e.Ask, e.PriceTicks, e.Size));
    }

    private void Close(DateTime time, Order o, bool filled) =>
        Events.Add(new BookEvent(time, filled ? BookEventKind.Eaten : time - o.Since < FlashLife ? BookEventKind.Flash : BookEventKind.Pulled, o.Ask, o.Ticks, o.Size));

    /// <summary>Volume that traded at that price on the side that hits this order (buyers hit the ask) in the second either side of a moment.</summary>
    private static decimal TradedAround(TradeStore store, DateTime time, long ticks, bool ask)
    {
        decimal sum = 0;
        int i1 = store.LowerBound((time + Settle).AddTicks(1));
        for (int i = store.LowerBound(time - Settle); i < i1; i++)
            if (store.GetPriceTicks(i) == ticks && (store.GetSide(i) == TradeSide.Buy) == ask) sum += store.GetVolume(i);
        return sum;
    }

    /// <summary>Keeps the sizes of the last orders and, every 100, recomputes what counts as big.</summary>
    private void Learn(decimal size)
    {
        if (size <= 0) return;
        _sizes.Enqueue(size);
        if (_sizes.Count > Samples) _sizes.Dequeue();
        if (_sizes.Count < MinSamples || (++_sinceMin < 100 && _min > 0)) return;
        _sinceMin = 0;
        var sorted = _sizes.OrderBy(v => v).ToArray();
        _min = Math.Max(6 * sorted[sorted.Length / 2], 3 * sorted[(int)(0.9 * (sorted.Length - 1))]);
    }
}
