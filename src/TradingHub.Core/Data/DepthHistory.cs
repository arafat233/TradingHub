using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// The order book over time (what a liquidity heatmap draws): a snapshot every <see cref="Interval"/>, kept for
/// <see cref="MaxAge"/>. The snapshots are immutable and already built by the feed, so keeping one costs a reference.
/// It lives only in memory and starts when the instrument is opened: there is no depth history to download.
/// Everything runs on the UI thread (the one that calls <see cref="InstrumentData.Pump"/>).
/// </summary>
public sealed class DepthHistory
{
    private readonly List<(DateTime Time, DepthSnapshot Book)> _items = new();
    private int _sinceTrim;

    /// <summary>Minimum time between two kept snapshots.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How far back it is kept.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(30);

    public int Count => _items.Count;
    public DateTime FirstTime => _items.Count > 0 ? _items[0].Time : DateTime.MinValue;
    public DateTime LastTime => _items.Count > 0 ? _items[^1].Time : DateTime.MinValue;

    public (DateTime Time, DepthSnapshot Book) this[int index] => _items[index];

    /// <summary>Keeps the snapshot if enough time has passed since the last one (and it has something in it).</summary>
    public void Add(DateTime time, DepthSnapshot book)
    {
        if (!book.HasBid && !book.HasAsk) return;
        if (_items.Count > 0)
        {
            var last = _items[^1].Time;
            if (time < last) _items.Clear(); // the clock went back (a replay was restarted): the old picture no longer applies
            else if (time - last < Interval) return;
        }

        _items.Add((time, book));
        // trimming moves the whole list, so it is done every so often and not on every snapshot
        if (++_sinceTrim >= 200) Trim(time);
    }

    private void Trim(DateTime now)
    {
        _sinceTrim = 0;
        int cut = IndexAtOrBefore(now - MaxAge);
        if (cut > 0) _items.RemoveRange(0, cut);
    }

    /// <summary>Keeps less (or more) book history from now on, and drops what is older at once (the memory guard uses it).</summary>
    public void SetMaxAge(TimeSpan age)
    {
        MaxAge = age;
        if (_items.Count > 0) Trim(_items[^1].Time);
    }

    /// <summary>Index of the last snapshot at or before <paramref name="time"/> (-1 if every one is later).</summary>
    public int IndexAtOrBefore(DateTime time)
    {
        int lo = 0, hi = _items.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_items[mid].Time <= time)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    /// <summary>The book as it was at that time (the last snapshot at or before it), or null if there was none yet.</summary>
    public DepthSnapshot? At(DateTime time)
    {
        int i = IndexAtOrBefore(time);
        return i >= 0 ? _items[i].Book : null;
    }

    public void Clear()
    {
        _items.Clear();
        _sinceTrim = 0;
    }
}
