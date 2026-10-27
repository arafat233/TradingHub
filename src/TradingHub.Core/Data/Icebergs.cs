using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>A price level that kept refilling: it absorbed far more than it ever showed in the book.</summary>
public sealed class IcebergEvent
{
    public DateTime Start { get; init; }
    public DateTime Last { get; set; }
    public long Ticks { get; init; }

    /// <summary>True = resting sell (buyers kept hitting the ask), false = resting buy.</summary>
    public bool Ask { get; init; }

    /// <summary>Everything traded against it so far.</summary>
    public decimal Volume { get; set; }

    /// <summary>How many times it was refilled (order-level detection only).</summary>
    public int Refills { get; set; }

    /// <summary>The exchange kept the same order id across refills (a native iceberg), as opposed to new orders posted after each fill.</summary>
    public bool Native { get; init; }
}

/// <summary>
/// Finds iceberg orders. A trade at a price is compared with what the book showed there just before it. When the volume
/// traded against one level in a short run is more than <see cref="Ratio"/> times the biggest size it ever displayed, and also
/// more than the minimum, the level was refilling: that is an iceberg. It is a heuristic: it also flags stops and fast market makers.
/// The heatmap feeds it from the stores (<see cref="Update"/>); backtests and live strategies feed it trade by trade (<see cref="OnTrade"/>),
/// so both see the same events.
/// </summary>
public sealed class IcebergDetector
{
    public const double Ratio = 3;
    private static readonly TimeSpan RunGap = TimeSpan.FromSeconds(10);

    private sealed class Run
    {
        public DateTime Start, Last;
        public decimal Consumed, MaxShown;
        public IcebergEvent? Event;
    }

    private readonly Dictionary<(bool, long), Run> _runs = new();
    private int _next;
    private DateTime _firstDepth;

    /// <summary>How long events are kept.</summary>
    public TimeSpan Keep { get; set; } = TimeSpan.FromMinutes(30);

    public List<IcebergEvent> Events { get; } = new();

    public void Reset()
    {
        _runs.Clear();
        Events.Clear();
        _next = 0;
        _firstDepth = default;
    }

    /// <summary>Reads the trades that arrived since the last call. <paramref name="minVolume"/> 0 = automatic (4 × the median size of a level in the book).</summary>
    public void Update(DepthHistory hist, TradeStore store, decimal minVolume)
    {
        if (hist.Count == 0) return;
        // the history restarted (a replay, a new connection) or the trades were replaced: start over
        if (hist.FirstTime < _firstDepth || _next > store.Count) Reset();
        if (_next == 0) _next = store.LowerBound(hist.FirstTime);
        _firstDepth = hist.FirstTime;
        for (; _next < store.Count; _next++)
        {
            int i = hist.IndexAtOrBefore(store.GetTime(_next));
            if (i < 0) continue;
            OnTrade(store.GetTime(_next), store.GetPriceTicks(_next), store.GetVolume(_next), store.GetSide(_next) == TradeSide.Buy, hist[i].Book, minVolume);
        }
    }

    /// <summary>One trade, with the book as it was just before it (null = unknown: it is skipped).</summary>
    public void OnTrade(DateTime time, long ticks, decimal vol, bool buy, DepthSnapshot? book, decimal minVolume = 0)
    {
        if (book == null) return;
        bool ask = buy; // buyers hit the ask
        decimal shown = book.GetSize(ask ? BookSide.Ask : BookSide.Bid, ticks);
        if (!_runs.TryGetValue((ask, ticks), out var run) || time - run.Last > RunGap)
            _runs[(ask, ticks)] = run = new Run { Start = time };
        run.Last = time;
        run.Consumed += vol;
        run.MaxShown = Math.Max(run.MaxShown, shown);

        if (run.Event is { } ev)
        {
            ev.Last = time;
            ev.Volume = run.Consumed;
        }
        else if ((double)run.Consumed > Ratio * (double)Math.Max(1, run.MaxShown) && run.Consumed >= (minVolume > 0 ? minVolume : AutoMin(book)))
        {
            run.Event = new IcebergEvent { Start = run.Start, Last = time, Ticks = ticks, Ask = ask, Volume = run.Consumed };
            Events.Add(run.Event);
        }

        // forget what is older than the memory (and the runs that ended long ago)
        if (Events.Count > 0 && Events[0].Last < time - Keep) Events.RemoveAll(e => e.Last < time - Keep);
        if (_runs.Count > 2000) foreach (var k in _runs.Where(r => r.Value.Last < time - Keep).Select(r => r.Key).ToList()) _runs.Remove(k);
    }

    /// <summary>4 x the median size of a level (the median, so one big wall does not push the minimum out of reach).</summary>
    private static decimal AutoMin(DepthSnapshot book)
    {
        var sizes = book.Bids.Concat(book.Asks).Select(l => l.Size).OrderBy(v => v).ToList();
        return sizes.Count == 0 ? 1 : Math.Max(1, 4 * sizes[sizes.Count / 2]);
    }
}
