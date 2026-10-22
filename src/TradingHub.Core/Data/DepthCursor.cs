using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Walks forward through the recorded books of an instrument (the .vdk day files) and answers "what was the book at this
/// moment". It reads one snapshot at a time, so a backtest over weeks needs the memory of one day file, not of all of them.
/// Every run (every thread of an optimization) has its own cursor. Asking for an earlier time starts over.
/// </summary>
public sealed class DepthCursor
{
    /// <summary>A book older than this is not shown (there was a gap in the recording: better no book than a stale one).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly string _root, _feedId, _symbol;
    private DepthFile.Decoder? _decoder;
    private DateOnly _opened;
    private (DateTime Time, DepthSnapshot Book)? _cur, _next;

    public DepthCursor(string root, string feedId, string symbol)
    {
        _root = root;
        _feedId = feedId;
        _symbol = symbol;
    }

    /// <summary>The book at that moment (the last snapshot at or before it), or null if there is none recent enough.</summary>
    public DepthSnapshot? At(DateTime time)
    {
        if (_cur is { } c && time < c.Time)
        {
            _decoder = null;
            _opened = default;
            _cur = _next = null;
        }

        while (true)
        {
            if (_next == null && !Fetch(time)) break;
            if (_next!.Value.Time > time) break;
            _cur = _next;
            _next = null;
        }

        return _cur is { } cur && time - cur.Time <= MaxAge ? cur.Book : null;
    }

    private bool Fetch(DateTime time)
    {
        while (true)
        {
            if (_decoder != null && _decoder.TryNext(out var t, out var book))
            {
                _next = (t, book);
                return true;
            }

            var want = DateOnly.FromDateTime(time);
            if (_opened >= want) return false; // everything up to that day has been read
            _opened = _opened == default ? want : _opened.AddDays(1);
            _decoder = DepthFile.Open(DepthFile.DayPath(_root, _feedId, _symbol, _opened));
        }
    }

    /// <summary>Whether there is any recorded book for the instrument between two instants (the backtest only uses the book if so).</summary>
    public static bool Exists(string root, string feedId, string symbol, DateTime fromUtc, DateTime toUtc)
    {
        var from = DateOnly.FromDateTime(fromUtc);
        var to = DateOnly.FromDateTime(toUtc);
        return DepthFile.AvailableDays(root, feedId, symbol).Any(d => d >= from && d <= to);
    }
}
