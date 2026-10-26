using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>A market order (fills with the same time and side added up) that is large for its instrument.</summary>
public readonly record struct BigOrder(DateTime Time, decimal Price, decimal Volume, bool Buy);

/// <summary>
/// Spots large market orders on a live tape: fills with the same time and side are one order, and "large" is above a percentile of
/// the recent orders (so it adapts to the instrument). Nothing is reported until enough orders have been seen to judge.
/// </summary>
public sealed class BigOrderDetector
{
    private readonly Queue<decimal> _recent = new();
    private readonly int _sample, _minOrders;
    private readonly double _percentile;
    private decimal _threshold;
    private int _sinceRecalc;
    private long _ticks;
    private TradeSide _side;
    private decimal _vol, _pv;
    private bool _reported;

    public BigOrderDetector(int sample = 3000, double percentile = 99.7, int minOrders = 200)
    {
        _sample = sample;
        _percentile = percentile;
        _minOrders = minOrders;
    }

    public decimal Threshold => _threshold;

    /// <summary>Feed the trades in order. Returns the order the first moment it grows past the threshold (once per order).</summary>
    public BigOrder? OnTrade(long timeTicks, decimal price, decimal volume, TradeSide side)
    {
        if (side == TradeSide.None || volume <= 0) return null;
        if (timeTicks != _ticks || side != _side)
        {
            Finish();
            _ticks = timeTicks;
            _side = side;
        }

        _vol += volume;
        _pv += price * volume;
        if (_reported || _threshold <= 0 || _vol <= _threshold) return null; // strictly above: the largest usual order sits exactly at the percentile
        _reported = true;
        return new BigOrder(new DateTime(_ticks, DateTimeKind.Utc), _pv / _vol, _vol, side == TradeSide.Buy);
    }

    private void Finish()
    {
        if (_vol > 0)
        {
            _recent.Enqueue(_vol);
            while (_recent.Count > _sample) _recent.Dequeue();
            if (_recent.Count >= _minOrders && (++_sinceRecalc >= 100 || _threshold == 0))
            {
                _sinceRecalc = 0;
                var sorted = _recent.OrderBy(v => v).ToArray();
                _threshold = sorted[Math.Clamp((int)Math.Round(_percentile / 100 * (sorted.Length - 1)), 0, sorted.Length - 1)];
            }
        }

        _vol = _pv = 0;
        _reported = false;
    }
}
