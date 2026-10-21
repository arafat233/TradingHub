namespace TradingHub.Data;

/// <summary>Data of one price level inside a candle (one footprint row).</summary>
public struct LevelData
{
    /// <summary>Volume sold aggressively (it hit the bid).</summary>
    public decimal Bid;

    /// <summary>Volume bought aggressively (it hit the ask).</summary>
    public decimal Ask;

    /// <summary>Volume with no known side.</summary>
    public decimal Between;

    /// <summary>Number of trades.</summary>
    public int Trades;

    /// <summary>Time (UTC, ticks) of the last trade at the level.</summary>
    public long LastTimeTicks;

    public readonly decimal Volume => Bid + Ask + Between;
    public readonly decimal Delta => Ask - Bid;
    public readonly bool IsEmpty => Trades == 0;
}

/// <summary>
/// Price levels of a candle, indexed by row (price in ticks / row step).
/// It is an array that grows in both directions: O(1) access and contiguous iteration (fast for drawing).
/// </summary>
public sealed class PriceLevels
{
    private LevelData[] _items = Array.Empty<LevelData>();
    private long _base;          // row of _items[0]
    private long _minRow = long.MaxValue;
    private long _maxRow = long.MinValue;

    public bool IsEmpty => _minRow > _maxRow;
    public long MinRow => _minRow;
    public long MaxRow => _maxRow;

    /// <summary>Number of rows between the lowest and the highest (including rows with no trades).</summary>
    public int Span => IsEmpty ? 0 : (int)(_maxRow - _minRow + 1);

    public ref LevelData GetOrAdd(long row)
    {
        if (_items.Length == 0)
        {
            _items = new LevelData[16];
            _base = row - 8;
        }
        else if (row < _base || row >= _base + _items.Length)
        {
            Grow(row);
        }

        if (row < _minRow) _minRow = row;
        if (row > _maxRow) _maxRow = row;
        return ref _items[row - _base];
    }

    private void Grow(long row)
    {
        long newMin = Math.Min(_base, row);
        long newMax = Math.Max(_base + _items.Length - 1, row);
        long needed = newMax - newMin + 1;
        long size = Math.Max(needed + needed / 2, _items.Length * 2L);
        if (size > int.MaxValue / 64) size = needed; // defensive limit
        var arr = new LevelData[size];
        // margin towards the side that grew
        long pad = size - needed;
        long newBase = row < _base ? newMin - pad : newMin;
        Array.Copy(_items, 0, arr, _base - newBase, _items.Length);
        _items = arr;
        _base = newBase;
    }

    public bool TryGet(long row, out LevelData data)
    {
        long i = row - _base;
        if (i < 0 || i >= _items.Length || row < _minRow || row > _maxRow)
        {
            data = default;
            return false;
        }

        data = _items[i];
        return !data.IsEmpty;
    }

    public LevelData Get(long row)
    {
        TryGet(row, out var d);
        return d;
    }

    /// <summary>Walks the rows with trades, from the lowest price to the highest.</summary>
    public IEnumerable<(long Row, LevelData Data)> Enumerate()
    {
        if (IsEmpty) yield break;
        for (long r = _minRow; r <= _maxRow; r++)
        {
            var d = _items[r - _base];
            if (!d.IsEmpty) yield return (r, d);
        }
    }

    /// <summary>Direct unchecked access for the renderer (r between MinRow and MaxRow).</summary>
    public ref readonly LevelData UnsafeAt(long row) => ref _items[row - _base];

    public void Clear()
    {
        _items = Array.Empty<LevelData>();
        _minRow = long.MaxValue;
        _maxRow = long.MinValue;
    }
}
