using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Plays back the recorded book and trades of one instrument: a cursor in time that can be moved and that runs at a chosen
/// speed. It keeps one chunk in memory (about an hour and a half around the cursor, loaded on a background thread) and swaps in
/// the next one as the cursor gets near its end, so a whole day never has to fit in memory.
/// The UI thread calls <see cref="Advance"/> from its timer and reads <see cref="History"/> and <see cref="Trades"/>.
/// </summary>
public sealed class DepthReplay
{
    private static readonly TimeSpan Before = TimeSpan.FromMinutes(35), After = TimeSpan.FromMinutes(55), Margin = TimeSpan.FromMinutes(3);

    private sealed record Chunk(DateTime From, DateTime To, DepthHistory History, TradeStore Trades);

    private readonly string _root;
    private readonly Instrument _instrument;
    private volatile Chunk? _ready;
    private Task? _loading;
    private Chunk _chunk;

    public DepthReplay(string root, Instrument instrument, DateTime start)
    {
        _root = root;
        _instrument = instrument;
        Cursor = start;
        _chunk = new Chunk(DateTime.MaxValue, DateTime.MinValue, NewHistory(), new TradeStore(instrument));
        Load(start);
    }

    public DepthHistory History => _chunk.History;
    public TradeStore Trades => _chunk.Trades;

    /// <summary>The detector for this replay (it starts over when a new chunk comes in or the cursor is moved).</summary>
    public IcebergDetector Icebergs { get; } = new();

    /// <summary>The moment being shown (UTC).</summary>
    public DateTime Cursor { get; private set; }

    public bool Playing { get; set; } = true;

    /// <summary>How many times faster than real time it plays.</summary>
    public double Speed { get; set; } = 1;

    /// <summary>True while a chunk is being read and there is nothing to show yet.</summary>
    public bool Loading => _chunk.History.Count == 0 && (_loading is { IsCompleted: false } || _ready != null);

    /// <summary>Moves the cursor (a jump far from what is loaded makes it read another chunk).</summary>
    public void Seek(DateTime time)
    {
        Cursor = time;
        Icebergs.Reset();
        Check();
    }

    /// <summary>Moves the cursor forward by what has passed in real time, at <see cref="Speed"/>, and swaps in a freshly read chunk.</summary>
    public void Advance(TimeSpan elapsed)
    {
        if (_ready is { } r)
        {
            _ready = null;
            _chunk = r;
            Icebergs.Reset();
        }

        if (Playing && _chunk.From != DateTime.MaxValue)
        {
            // it waits at the end of what is loaded until the next chunk arrives
            // (a cursor that was moved outside the loaded chunk stays where it was put: the right chunk is on its way)
            var next = Cursor + TimeSpan.FromTicks((long)(elapsed.Ticks * Speed));
            if (Cursor >= _chunk.From && Cursor <= _chunk.To) Cursor = next > _chunk.To ? _chunk.To : next;
        }

        Check();
    }

    private void Check()
    {
        if (_loading is { IsCompleted: false } || _ready != null) return;
        var c = _chunk;
        // a new chunk is needed when nothing was loaded yet, when the cursor left the loaded one or is near its end,
        // or when it is so near its start that the longest window (30 minutes) would miss data
        if (c.From == DateTime.MaxValue || Cursor < c.From + TimeSpan.FromMinutes(31) || Cursor > c.To - Margin) Load(Cursor);
    }

    private static DepthHistory NewHistory() => new() { Interval = TimeSpan.Zero, MaxAge = TimeSpan.FromDays(3) };

    private void Load(DateTime around)
    {
        if (_loading is { IsCompleted: false }) return;
        var from = around - Before;
        var to = around + After;
        _loading = Task.Run(() =>
        {
            var hist = NewHistory();
            foreach (var (t, b) in DepthFile.ReadRange(_root, _instrument.FeedId, _instrument.Symbol, from, to)) hist.Add(t, b);
            var trades = new TradeStore(_instrument);
            for (var d = DateOnly.FromDateTime(from); d <= DateOnly.FromDateTime(to); d = d.AddDays(1))
                foreach (var t in HistorySources.ReadDay(_root, _instrument.FeedId, _instrument.Symbol, d))
                    if (t.Time >= from && t.Time <= to) trades.Add(t);
            _ready = new Chunk(from, to, hist, trades);
        });
    }

    /// <summary>The first recorded moment of a day (null if there is nothing recorded that day).</summary>
    public static DateTime? FirstTime(string root, Instrument instrument, DateOnly day) =>
        DepthFile.Open(DepthFile.DayPath(root, instrument.FeedId, instrument.Symbol, day)) is { } d && d.TryNext(out var t, out _) ? t : null;
}
