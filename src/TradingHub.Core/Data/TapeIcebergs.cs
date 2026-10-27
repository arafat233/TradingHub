using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Iceberg finder for feeds with no order book (LSE futures: ticks only). With no displayed size to compare against, it looks
/// for what a refilling order leaves in the tape: many prints at one price in a short run, a total far above what a level normally
/// absorbs (median of the recent runs), and the price not trading through the level meanwhile.
/// More false positives than <see cref="IcebergDetector"/> (stops and busy levels look the same); it is context, not a signal.
/// Produces the same <see cref="IcebergEvent"/>s.
/// </summary>
public sealed class TapeIcebergDetector
{
    public int MinPrints { get; set; } = 8;

    /// <summary>A run must absorb this many times the median run.</summary>
    public double Ratio { get; set; } = 5;

    /// <summary>Closed runs needed before anything is flagged (the median is meaningless before that).</summary>
    public int Warmup { get; set; } = 50;

    public TimeSpan Keep { get; set; } = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RunGap = TimeSpan.FromSeconds(10);

    private sealed class Run
    {
        public DateTime Start, Last;
        public decimal Consumed;
        public int Prints;
        public bool Broken;
        public IcebergEvent? Event;
    }

    private readonly Dictionary<(bool, long), Run> _runs = new();
    private readonly Queue<decimal> _closed = new();
    private decimal _median;
    private long _lastBuy = long.MinValue, _lastSell = long.MaxValue;
    private int _sinceSweep;

    public List<IcebergEvent> Events { get; } = new();

    public void Reset()
    {
        _runs.Clear();
        _closed.Clear();
        Events.Clear();
        _median = 0;
        _dirty = false;
        _lastBuy = long.MinValue;
        _lastSell = long.MaxValue;
    }

    /// <summary>One trade in time order. <paramref name="buy"/> = the buyer was the aggressor (tick rule is fine).</summary>
    public void OnTrade(DateTime time, long ticks, decimal vol, bool buy)
    {
        // price trading through a level breaks the runs below it (buys) or above it (sells)
        if (buy && ticks > _lastBuy)
        {
            foreach (var (k, r) in _runs) if (k.Item1 && k.Item2 < ticks) r.Broken = true;
        }
        else if (!buy && ticks < _lastSell)
        {
            foreach (var (k, r) in _runs) if (!k.Item1 && k.Item2 > ticks) r.Broken = true;
        }

        if (buy) _lastBuy = ticks; else _lastSell = ticks;

        bool ask = buy; // buyers hit the ask: a resting seller
        if (!_runs.TryGetValue((ask, ticks), out var run) || time - run.Last > RunGap)
        {
            if (run != null) Close(run);
            _runs[(ask, ticks)] = run = new Run { Start = time };
        }

        run.Last = time;
        run.Consumed += vol;
        run.Prints++;

        if (run.Event is { } ev)
        {
            ev.Last = time;
            ev.Volume = run.Consumed;
        }
        else if (!run.Broken && run.Prints >= MinPrints && _closed.Count >= Warmup && (double)run.Consumed > Ratio * (double)Median())
        {
            run.Event = new IcebergEvent { Start = run.Start, Last = time, Ticks = ticks, Ask = ask, Volume = run.Consumed };
            Events.Add(run.Event);
        }

        if (++_sinceSweep >= 1000) Sweep(time);
    }

    private void Close(Run r)
    {
        _closed.Enqueue(r.Consumed);
        if (_closed.Count > 300) _closed.Dequeue();
        _dirty = true;
    }

    private bool _dirty;

    /// <summary>Median of the recent runs; recomputed only when a candidate needs it (most runs never get that far).</summary>
    private decimal Median()
    {
        if (_dirty)
        {
            var sorted = _closed.OrderBy(v => v).ToArray();
            _median = sorted[sorted.Length / 2];
            _dirty = false;
        }

        return _median;
    }

    /// <summary>Closes the runs that ended (so the median counts levels that were never revisited) and drops old events.</summary>
    private void Sweep(DateTime now)
    {
        _sinceSweep = 0;
        foreach (var k in _runs.Where(r => now - r.Value.Last > RunGap).Select(r => r.Key).ToList())
        {
            Close(_runs[k]);
            _runs.Remove(k);
        }

        Events.RemoveAll(e => e.Last < now - Keep);
    }
}
