using TradingHub.Market;

namespace TradingHub.Data;

public enum BookEventKind
{
    /// <summary>A big resting order (a wall) appeared.</summary>
    Added,

    /// <summary>A wall disappeared without being traded: it was cancelled.</summary>
    Pulled,

    /// <summary>A wall that appeared and was cancelled within seconds, before price got near it.</summary>
    Flash,

    /// <summary>A wall that trades ate through.</summary>
    Eaten,
}

/// <summary>What happened to one big resting level, in the words of an order-by-order feed (add, cancel, fill) although the book is only price levels.</summary>
public sealed record BookEvent(DateTime Time, BookEventKind Kind, bool Ask, long Ticks, decimal Size)
{
    public string Text(Instrument inst)
    {
        string side = Ask ? "offer" : "bid", price = inst.FromTicks(Ticks).ToString("0.####"), size = Size.ToString("0.##");
        return Kind switch
        {
            BookEventKind.Added => $"Big {side} added: {size} at {price}",
            BookEventKind.Pulled => $"{char.ToUpperInvariant(side[0])}{side[1..]} of {size} at {price} pulled (cancelled, not traded)",
            BookEventKind.Flash => $"{char.ToUpperInvariant(side[0])}{side[1..]} of {size} at {price} flashed: gone within seconds, never traded",
            _ => $"{char.ToUpperInvariant(side[0])}{side[1..]} of {size} at {price} eaten by trades",
        };
    }
}

/// <summary>
/// Reads add / cancel / fill style events out of the Level 2 book and the trades, for feeds that have no order-level (L3) data. Each snapshot is
/// compared with the one before: a level that grew to a wall (an outlier: <see cref="WallFactor"/> times the median level and 3 times the 90th percentile) was added; a wall that lost most of
/// its size was either eaten (trades at that price made up at least half of what it lost) or pulled. It is inferred, not seen: two orders look
/// like one, and a cancel and a re-add between two snapshots cancel out. It never invents a number: every event has a measured size and price.
/// </summary>
public sealed class BookEventDetector
{
    public const double WallFactor = 6;
    private static readonly TimeSpan FlashLife = TimeSpan.FromSeconds(3);
    private const int LevelsWatched = 20;

    private sealed class Wall
    {
        public DateTime Since;
        public decimal Max, Last;
    }

    private readonly Dictionary<(bool Ask, long Ticks), Wall> _walls = new();
    private DateTime _lastTime;

    /// <summary>How long events are kept.</summary>
    public TimeSpan Keep { get; set; } = TimeSpan.FromMinutes(30);

    public List<BookEvent> Events { get; } = new();

    public void Reset()
    {
        _walls.Clear();
        Events.Clear();
        _lastTime = default;
    }

    /// <summary>Reads the snapshots that arrived since the last call. <paramref name="minSize"/> 0 = automatic (6 x the median level).</summary>
    public void Update(DepthHistory hist, TradeStore store, decimal minSize = 0)
    {
        if (hist.Count == 0) return;
        if (hist.LastTime < _lastTime) Reset(); // the history restarted (a replay, a new connection)
        // the history trims its front, so the place is found by time, not kept as an index
        int i = _lastTime == default ? 0 : hist.IndexAtOrBefore(_lastTime) + 1;
        if (i >= hist.Count) return;
        var (prevTime, prev) = i > 0 ? hist[i - 1] : hist[i++];
        for (; i < hist.Count; i++)
        {
            var (t, book) = hist[i];
            Step(prevTime, prev, t, book, store, minSize);
            (prevTime, prev) = (t, book);
        }

        _lastTime = prevTime;
        if (Events.Count > 0 && Events[0].Time < prevTime - Keep) Events.RemoveAll(e => e.Time < prevTime - Keep);
    }

    private void Step(DateTime prevTime, DepthSnapshot prev, DateTime time, DepthSnapshot cur, TradeStore store, decimal minSize)
    {
        decimal wallMin = minSize > 0 ? minSize : AutoMin(cur);
        foreach (bool ask in new[] { true, false })
        {
            var side = ask ? BookSide.Ask : BookSide.Bid;
            var levels = ask ? cur.Asks : cur.Bids;
            var prevLevels = ask ? prev.Asks : prev.Bids;
            for (int k = 0; k < Math.Min(LevelsWatched, levels.Length); k++)
            {
                var l = levels[k];
                if (l.Size < wallMin) continue;
                if (_walls.TryGetValue((ask, l.PriceTicks), out var w)) { w.Max = Math.Max(w.Max, l.Size); w.Last = l.Size; continue; }
                // it is only "added" if the last book covered this price and showed little there; a wall that was already there, or that slid
                // into the window as price moved, is tracked quietly (so its later pull or fill is still reported) but not announced
                bool added = InRange(prevLevels, ask, l.PriceTicks) && prev.GetSize(side, l.PriceTicks) < wallMin / 2;
                _walls[(ask, l.PriceTicks)] = new Wall { Since = added ? time : DateTime.MinValue, Max = l.Size, Last = l.Size };
                if (added) Events.Add(new BookEvent(time, BookEventKind.Added, ask, l.PriceTicks, l.Size));
            }

            foreach (var key in _walls.Keys.Where(k => k.Ask == ask).ToList())
            {
                var w = _walls[key];
                decimal size = cur.GetSize(side, key.Ticks);
                if (size >= 0.3m * w.Max) { w.Last = size; continue; }
                _walls.Remove(key);
                // beyond the far end of the book it only left the window we see: not an event
                if (levels.Length == 0 || (ask ? key.Ticks > levels[^1].PriceTicks : key.Ticks < levels[^1].PriceTicks)) continue;
                decimal lost = w.Last - size, traded = TradedAt(store, prevTime, time, key.Ticks, ask);
                var kind = traded >= 0.5m * lost ? BookEventKind.Eaten : time - w.Since < FlashLife ? BookEventKind.Flash : BookEventKind.Pulled;
                Events.Add(new BookEvent(time, kind, ask, key.Ticks, w.Last));
            }
        }

        if (_walls.Count > 500) foreach (var k in _walls.Where(w => w.Value.Since < time - Keep).Select(w => w.Key).ToList()) _walls.Remove(k);
    }

    /// <summary>Whether a price is between the first and the last level of a side of a book (the part of the book that was visible).</summary>
    private static bool InRange(BookLevel[] levels, bool ask, long ticks) =>
        levels.Length > 0 && (ask ? ticks >= levels[0].PriceTicks && ticks <= levels[^1].PriceTicks : ticks <= levels[0].PriceTicks && ticks >= levels[^1].PriceTicks);

    /// <summary>Volume that traded against that side of the book at that price between two snapshots (buyers hit the ask, sellers the bid).</summary>
    private static decimal TradedAt(TradeStore store, DateTime from, DateTime to, long ticks, bool ask)
    {
        decimal sum = 0;
        int i1 = store.LowerBound(to.AddTicks(1));
        for (int i = store.LowerBound(from.AddTicks(1)); i < i1; i++)
            if (store.GetPriceTicks(i) == ticks && (store.GetSide(i) == TradeSide.Buy) == ask) sum += store.GetVolume(i);
        return sum;
    }

    /// <summary>
    /// A wall is an outlier: at least 6 x the median level and 3 x the 90th-percentile level (the median and the percentile, so one big wall does
    /// not push the minimum out of reach, and a book of many similar quotes does not turn every quote into a wall).
    /// </summary>
    private static decimal AutoMin(DepthSnapshot book)
    {
        var sizes = book.Bids.Concat(book.Asks).Select(l => l.Size).OrderBy(v => v).ToList();
        if (sizes.Count == 0) return 1;
        return Math.Max(Math.Max((decimal)WallFactor * sizes[sizes.Count / 2], 3 * sizes[(int)(0.9 * (sizes.Count - 1))]), 0.000001m);
    }
}

/// <summary>Several events of the same kind and side that happened together (a market maker re-quoting a whole ladder), shown as one line.</summary>
public sealed record BookEventGroup(DateTime Time, BookEventKind Kind, bool Ask, int Count, long MinTicks, long MaxTicks, decimal TotalSize)
{
    public string Text(Instrument inst)
    {
        if (Count == 1) return new BookEvent(Time, Kind, Ask, MinTicks, TotalSize).Text(inst);
        string side = Ask ? "offers" : "bids", range = $"{inst.FromTicks(MinTicks):0.####}–{inst.FromTicks(MaxTicks):0.####}", size = TotalSize.ToString("0.##");
        return Kind switch
        {
            BookEventKind.Added => $"{Count} big {side} added at {range} ({size} in all)",
            BookEventKind.Pulled => $"{Count} {side} pulled at {range}, cancelled not traded ({size} in all)",
            BookEventKind.Flash => $"{Count} {side} flashed at {range}: gone within seconds, never traded ({size} in all)",
            _ => $"{Count} {side} eaten by trades at {range} ({size} in all)",
        };
    }
}

/// <summary>
/// Makes the book-event list readable on busy markets. Events of the same kind and side within <see cref="Window"/> of the first of them are one
/// line (a market maker moving a ladder of orders shows as "5 offers pulled at 80496–80512", not five lines), and lines much smaller than the
/// biggest of the recent ones are dropped, so what is left is the orders that matter.
/// </summary>
public static class BookEventGrouper
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1.5);

    /// <param name="minShare">A line is kept only if it holds at least this share of the biggest line in <paramref name="events"/> (0 keeps all).</param>
    public static List<BookEventGroup> Group(IEnumerable<BookEvent> events, decimal minShare = 0.2m)
    {
        var groups = new List<BookEventGroup>();
        foreach (var e in events.OrderBy(e => e.Time))
        {
            if (groups.Count > 0 && groups[^1] is { } g && g.Kind == e.Kind && g.Ask == e.Ask && e.Time - g.Time <= Window)
                groups[^1] = g with { Count = g.Count + 1, MinTicks = Math.Min(g.MinTicks, e.Ticks), MaxTicks = Math.Max(g.MaxTicks, e.Ticks), TotalSize = g.TotalSize + e.Size };
            else groups.Add(new BookEventGroup(e.Time, e.Kind, e.Ask, 1, e.Ticks, e.Ticks, e.Size));
        }

        if (minShare > 0 && groups.Count > 0)
        {
            decimal biggest = groups.Max(g => g.TotalSize);
            groups = groups.Where(g => g.TotalSize >= minShare * biggest).ToList();
        }

        return groups;
    }
}
