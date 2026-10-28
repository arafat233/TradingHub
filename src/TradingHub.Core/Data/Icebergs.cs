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

    /// <summary>How many times it was refilled.</summary>
    public int Refills { get; set; }

    /// <summary>Size shown after each refill (0 = not measured). Tape detection only: Frey and Sandås use it to judge how much is left hidden.</summary>
    public decimal Peak { get; set; }

    /// <summary>The exchange kept the same order id across refills (a native iceberg), as opposed to new orders posted after each fill.</summary>
    public bool Native { get; init; }

    /// <summary>Expected volume still to come, from icebergs of this peak size that finished earlier (0 = no peak, or too little history).</summary>
    public decimal PredictedHidden { get; set; }

    /// <summary>False once more than a whole peak traded since the last refill with no new one (Christensen and Woodmansey's activity test): it ran out or was cancelled.</summary>
    public bool Active { get; set; } = true;
}

public static class IcebergEvents
{
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Order-level icebergs (seen refilling, with real order ids) plus the tape-based ones that are not the same level at about the same
    /// time, so a level that both detectors found is counted once, as the order-level one.
    /// </summary>
    public static IEnumerable<IcebergEvent> Merge(IEnumerable<IcebergEvent> tape, IReadOnlyList<IcebergEvent> orders)
    {
        foreach (var o in orders) yield return o;
        foreach (var t in tape)
            if (!orders.Any(o => o.Ask == t.Ask && o.Ticks == t.Ticks && t.Start <= o.Last + Slack && o.Start <= t.Last + Slack)) yield return t;
    }
}

/// <summary>
/// Guesses how much of an iceberg is still hidden, the way Christensen and Woodmansey and Zotikov and Antonov do: icebergs with the same peak
/// size tend to end at the same total (round numbers), so take the middle of the totals seen so far that are above what already traded.
/// It learns from what it has seen in this session. Only volume that traded is seen, so totals are lower bounds (the papers correct
/// that with Kaplan-Meier; ponytail: add it when a tape cannot tell a cancelled iceberg from a finished one and the bias matters).
/// </summary>
public sealed class HiddenVolumeModel
{
    public const int MinSamples = 5;
    private const int Cap = 200;
    private readonly Dictionary<int, List<decimal>> _totals = new();

    // peaks within 25 % of each other share a bucket, the same tolerance the refill rule uses
    private static int Bucket(decimal peak) => (int)Math.Round(Math.Log((double)peak, 1.25));

    public void Reset() => _totals.Clear();

    public void Learn(decimal peak, decimal total)
    {
        if (peak <= 0 || total < peak) return;
        if (!_totals.TryGetValue(Bucket(peak), out var list)) _totals[Bucket(peak)] = list = new();
        list.Add(total);
        if (list.Count > Cap) list.RemoveAt(0);
    }

    /// <summary>Volume expected after <paramref name="traded"/> has traded; 0 when fewer than <see cref="MinSamples"/> finished icebergs went further than that.</summary>
    public decimal Remaining(decimal peak, decimal traded)
    {
        if (peak <= 0 || !_totals.TryGetValue(Bucket(peak), out var list)) return 0;
        var above = list.Where(t => t > traded).OrderBy(t => t).ToList();
        return above.Count < MinSamples ? 0 : above[above.Count / 2] - traded;
    }
}

/// <summary>
/// Finds iceberg orders. A trade at a price is compared with what the book showed there just before it. When the volume
/// traded against one level in a short run is more than <see cref="Ratio"/> times the biggest size it ever displayed, and also
/// more than the minimum, the level was refilling: that is an iceberg. So is a level whose displayed size traded out and reappeared at the
/// same size <see cref="MinRefills"/> times (the Frey and Sandås rule); that also gives the peak size. It is a heuristic: it also flags stops and fast market makers.
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
        public decimal Consumed, MaxShown, BookShown, VolSince, Peak, SinceRefill;
        public DepthSnapshot? Book;
        public int Refills;
        public IcebergEvent? Event;
    }

    /// <summary>A level whose displayed size fully traded and came back at the same size this many times is an iceberg, whatever the ratio says (0 = off).</summary>
    public int MinRefills { get; set; } = 4;

    /// <summary>
    /// Also count a level that is simply bigger than it should be after trades (what it showed minus what traded), not only one that was cleared and came back.
    /// It finds icebergs with other orders queued behind them, which the strict rule cannot see, but unrelated orders arriving at a busy level look the same
    /// (in the simulation it flags half of the busy levels). Off by default.
    /// </summary>
    public bool LooseRefill { get; set; }

    /// <summary>The absorption rule (volume far above anything displayed). On a busy level where unrelated orders keep arriving it fires for no reason; off, only the refill rule flags.</summary>
    public bool RatioRule { get; set; }

    private readonly Dictionary<(bool, long), Run> _runs = new();
    private int _next;
    private DateTime _firstDepth;

    /// <summary>How long events are kept.</summary>
    public TimeSpan Keep { get; set; } = TimeSpan.FromMinutes(30);

    public List<IcebergEvent> Events { get; } = new();

    /// <summary>Learns from the icebergs that finished and predicts the hidden rest of the ones in progress.</summary>
    public HiddenVolumeModel Hidden { get; } = new();

    private int _sinceSweep;

    public void Reset()
    {
        _runs.Clear();
        Events.Clear();
        Hidden.Reset();
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
        {
            if (run != null) Finish(run);
            _runs[(ask, ticks)] = run = new Run { Start = time };
        }
        run.Last = time;
        run.Consumed += vol;
        run.MaxShown = Math.Max(run.MaxShown, shown);

        // A refill: the level is bigger than it should be. It should show what the last book picture showed minus what traded since;
        // anything above that was added. The same amount (within 25 %) again and again is a peak being refilled, a different amount starts over,
        // a small one is noise. Trades that share one book picture are not compared with each other, the picture cannot have changed.
        if (run.Book != null && !ReferenceEquals(book, run.Book))
        {
            bool cleared = run.VolSince >= run.BookShown; // everything that was displayed traded
            decimal added = shown - Math.Max(0, run.BookShown - run.VolSince);
            if (added > 0 && (cleared || LooseRefill))
            {
                if (run.Peak == 0 || Math.Abs(added - run.Peak) > run.Peak / 4 && added >= run.Peak / 2) { run.Peak = added; run.Refills = 1; run.SinceRefill = 0; }
                else if (Math.Abs(added - run.Peak) <= run.Peak / 4) { run.Refills++; run.SinceRefill = 0; }
            }

            run.VolSince = 0;
        }

        if (!ReferenceEquals(book, run.Book)) { run.Book = book; run.BookShown = shown; }
        run.VolSince += vol;
        run.SinceRefill += vol;
        bool active = run.Peak == 0 || run.SinceRefill <= run.Peak * 1.25m; // more than a whole peak traded and no refill: it is over

        if (run.Event is { } ev)
        {
            ev.Last = time;
            ev.Volume = run.Consumed;
            ev.Refills = run.Refills;
            ev.Peak = run.Peak;
            ev.Active = active;
            ev.PredictedHidden = active ? Hidden.Remaining(run.Peak, run.Consumed) : 0;
        }
        else if ((RatioRule && (double)run.Consumed > Ratio * (double)Math.Max(1, run.MaxShown) && run.Consumed >= (minVolume > 0 ? minVolume : AutoMin(book)))
                 || (MinRefills > 0 && run.Refills >= MinRefills))
        {
            run.Event = new IcebergEvent { Start = run.Start, Last = time, Ticks = ticks, Ask = ask, Volume = run.Consumed, Refills = run.Refills, Peak = run.Peak, Active = active, PredictedHidden = active ? Hidden.Remaining(run.Peak, run.Consumed) : 0 };
            Events.Add(run.Event);
        }

        // forget what is older than the memory (and the runs that ended long ago); the ones that ended teach the hidden-volume model
        if (Events.Count > 0 && Events[0].Last < time - Keep) Events.RemoveAll(e => e.Last < time - Keep);
        if (++_sinceSweep >= 500 || _runs.Count > 2000)
        {
            _sinceSweep = 0;
            foreach (var k in _runs.Where(r => time - r.Value.Last > RunGap).Select(r => r.Key).ToList())
            {
                Finish(_runs[k]);
                _runs.Remove(k);
            }
        }
    }

    private void Finish(Run r)
    {
        if (r.Event != null) Hidden.Learn(r.Peak, r.Consumed);
    }

    /// <summary>4 x the median size of a level (the median, so one big wall does not push the minimum out of reach).</summary>
    private static decimal AutoMin(DepthSnapshot book)
    {
        var sizes = book.Bids.Concat(book.Asks).Select(l => l.Size).OrderBy(v => v).ToList();
        return sizes.Count == 0 ? 1 : Math.Max(1, 4 * sizes[sizes.Count / 2]);
    }
}
