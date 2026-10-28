using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Iceberg finder for order-by-order feeds (Databento MBO on CME, Level 3), after the method in Zotikov and Antonov, "CME Iceberg Order
/// Detection and Prediction" (Quantitative Finance 2021). Two kinds:
/// native = the same order id is refilled (its size goes back up after being reduced by trades);
/// synthetic = right after an order is filled, a new order of about the same size appears at the same price and side, again and again.
/// Unlike <see cref="TapeIcebergDetector"/> it sees the real orders, so a refill is observed instead of guessed. Only the total size is
/// still an estimate (what was filled so far). Hidden size that is cancelled before it trades is never seen.
/// </summary>
public sealed class OrderIcebergDetector
{
    /// <summary>
    /// A new order must appear within this time after a fill to count as its refill (synthetic). Zotikov and Antonov use 0.3 s on CME; Christensen and
    /// Woodmansey measured the exchange's own refills at 76 ms on average (sd 98 ms), so 0.3 s also covers a client's round trip.
    /// </summary>
    public TimeSpan Reload { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Allowed difference between the size of the filled order and the one that replaces it.</summary>
    public double SizeTolerance { get; set; } = 0.25;

    public int MinSyntheticRefills { get; set; } = 3;
    public int MinNativeReloads { get; set; } = 2;
    public TimeSpan Keep { get; set; } = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ChainIdle = TimeSpan.FromSeconds(5);

    private sealed class Chain
    {
        public DateTime Start, Last;
        public decimal Total;
        public int Refills;
        public decimal Peak;
        public bool Native;
        public IcebergEvent? Event;
    }

    private sealed class Ord
    {
        public bool Ask;
        public long Ticks;
        public decimal Size, Filled;
        public bool Reduced;
        public Chain? Chain;
    }

    private readonly Dictionary<long, Ord> _orders = new();
    private readonly Dictionary<(bool, long), (DateTime Time, decimal Clip, Chain Chain)> _lastFill = new();
    private int _sweep;

    public List<IcebergEvent> Events { get; } = new();

    public void Reset()
    {
        _orders.Clear();
        _lastFill.Clear();
        Events.Clear();
    }

    public void OnEvent(OrderEvent e)
    {
        switch (e.Kind)
        {
            case OrderEventKind.Add:
                Add(e);
                break;
            case OrderEventKind.Modify:
                Modify(e);
                break;
            case OrderEventKind.Fill:
                End(e, filled: true);
                break;
            case OrderEventKind.Cancel:
            case OrderEventKind.Remove:
                End(e, filled: false);
                break;
        }

        if (++_sweep >= 5000) Sweep(e.Time);
    }

    private void Add(OrderEvent e)
    {
        var o = new Ord { Ask = e.Ask, Ticks = e.PriceTicks, Size = e.Size };
        _orders[e.OrderId] = o;
        if (!_lastFill.TryGetValue((e.Ask, e.PriceTicks), out var lf)) return;
        // synthetic: a replacement of about the same size, right after a fill
        bool soon = e.Time - lf.Time <= Reload && e.Time >= lf.Time;
        bool alike = lf.Clip > 0 && Math.Abs((double)(e.Size - lf.Clip)) <= SizeTolerance * (double)lf.Clip;
        if (!soon || !alike) return;
        o.Chain = lf.Chain;
        lf.Chain.Refills++;
        if (lf.Chain.Peak == 0) lf.Chain.Peak = lf.Clip;
        lf.Chain.Last = e.Time;
        Flag(lf.Chain, e.Time, e.Ask, e.PriceTicks, MinSyntheticRefills);
        _lastFill.Remove((e.Ask, e.PriceTicks));
    }

    private void Modify(OrderEvent e)
    {
        if (!_orders.TryGetValue(e.OrderId, out var o)) return;
        if (e.PriceTicks != o.Ticks)
        {
            // a price change is a new order for this purpose
            _orders[e.OrderId] = new Ord { Ask = e.Ask, Ticks = e.PriceTicks, Size = e.Size };
            return;
        }

        if (e.Size < o.Size)
        {
            decimal dec = o.Size - e.Size;
            o.Filled += dec;
            o.Reduced = true;
            if (o.Chain != null) { o.Chain.Total += dec; o.Chain.Last = e.Time; Touch(o.Chain); }
        }
        else if (e.Size > o.Size && o.Reduced)
        {
            // native refill: the same order got its display size back after trades
            o.Chain ??= new Chain { Start = e.Time, Total = o.Filled, Native = true };
            o.Chain.Refills++;
            if (o.Chain.Peak == 0) o.Chain.Peak = e.Size;
            o.Chain.Last = e.Time;
            Flag(o.Chain, e.Time, o.Ask, o.Ticks, MinNativeReloads);
        }

        o.Size = e.Size;
    }

    private void End(OrderEvent e, bool filled)
    {
        if (!_orders.Remove(e.OrderId, out var o)) return;
        if (!filled) return;
        o.Filled += o.Size;
        var chain = o.Chain ?? new Chain { Start = e.Time };
        if (o.Chain != null) chain.Total += o.Size;
        else chain.Total = o.Filled;
        chain.Last = e.Time;
        Touch(chain);
        _lastFill[(o.Ask, o.Ticks)] = (e.Time, o.Filled, chain);
    }

    private static void Touch(Chain c)
    {
        if (c.Event is { } ev)
        {
            ev.Last = c.Last;
            ev.Volume = c.Total;
            ev.Refills = c.Refills;
            ev.Peak = c.Peak;
        }
    }

    private void Flag(Chain c, DateTime time, bool ask, long ticks, int min)
    {
        Touch(c);
        if (c.Event != null || c.Refills < min) return;
        c.Event = new IcebergEvent { Start = c.Start, Last = time, Ticks = ticks, Ask = ask, Volume = c.Total, Refills = c.Refills, Peak = c.Peak, Native = c.Native };
        Events.Add(c.Event);
    }

    private void Sweep(DateTime now)
    {
        _sweep = 0;
        foreach (var k in _lastFill.Where(x => now - x.Value.Time > ChainIdle).Select(x => x.Key).ToList()) _lastFill.Remove(k);
        Events.RemoveAll(e => e.Last < now - Keep);
    }
}
