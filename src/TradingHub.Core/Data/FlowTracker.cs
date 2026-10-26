using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>One minute of the tape: prices and the volume that bought (hit the ask) and sold (hit the bid).</summary>
public sealed record MinuteBar(DateTime Start, long OpenTicks, long HighTicks, long LowTicks, long CloseTicks, decimal Buy, decimal Sell)
{
    public decimal Volume => Buy + Sell;
    public decimal Delta => Buy - Sell;
}

/// <summary>
/// Reads the trade store once, as it grows, into one-minute bars (kept for <see cref="Keep"/>) and a toxicity reading. The decision panel and the
/// danger gauge ask it instead of scanning thousands of trades again every refresh. Everything runs on the UI thread, like the other detectors.
/// Toxicity is VPIN (Easley, López de Prado and O'Hara): the trades are cut into buckets of equal volume (a fiftieth of the last hour), each bucket's
/// imbalance is |buys - sells| / volume, and the reading is the mean over the last fifty buckets. High means one side has been taking liquidity
/// very one-sidedly, which is when market makers get hurt and prices jump. It is shown as a percentile of the last day so each market is judged
/// against its own usual level.
/// </summary>
public sealed class FlowTracker
{
    private const int BucketCount = 50;
    private readonly List<MinuteBar> _bars = new();
    private int _next;
    private DateTime _firstTime;

    // the volume bucket being filled and the imbalances of the closed ones
    private decimal _bucketBuy, _bucketSell, _bucketSize;
    private readonly Queue<decimal> _imbalances = new();
    private readonly List<(DateTime Time, decimal Vpin)> _history = new();
    private DateTime _lastSample;

    // the session (the UTC day): delta, delta in money and the volume at each price, for the headline numbers
    private DateOnly _day;
    private decimal _sessionDelta, _sessionMoney;
    private readonly Dictionary<long, decimal> _profile = new();

    /// <summary>Buy volume minus sell volume since the UTC day began.</summary>
    public decimal SessionDelta => _sessionDelta;

    /// <summary>The same in money (price x size x the contract's value).</summary>
    public decimal SessionDeltaMoney => _sessionMoney;

    /// <summary>The price (in ticks) with the most volume since the UTC day began, or null before any trade.</summary>
    public long? SessionPocTicks => _profile.Count == 0 ? null : _profile.MaxBy(kv => kv.Value).Key;

    public TimeSpan Keep { get; set; } = TimeSpan.FromHours(26);

    /// <summary>Closed and current minutes, oldest first.</summary>
    public IReadOnlyList<MinuteBar> Bars => _bars;

    /// <summary>The toxicity now (0 to 1), or null while there is not enough tape for fifty buckets.</summary>
    public decimal? Vpin => _imbalances.Count >= BucketCount ? _imbalances.Average() : null;

    /// <summary>Where the toxicity stands among its own readings of the last day (0 to 1), or null until there are 90 minutes of readings.</summary>
    public decimal? VpinPercentile
    {
        get
        {
            // ranked only once there are 90 minutes of readings: before that "its last day" would be a claim about a few minutes
            if (Vpin is not { } v || _history.Count < 30 || _history[^1].Time - _history[0].Time < TimeSpan.FromMinutes(90)) return null;
            int below = 0;
            foreach (var h in _history) if (h.Vpin <= v) below++;
            return (decimal)below / _history.Count;
        }
    }

    /// <summary>Keeps less (or more) minute history from now on, and drops the older minutes at once (the memory guard uses it).</summary>
    public void Shrink(TimeSpan keep)
    {
        Keep = keep;
        if (_bars.Count == 0) return;
        var cut = _bars[^1].Start - keep;
        int n = _bars.FindIndex(b => b.Start >= cut);
        if (n > 0) _bars.RemoveRange(0, n);
    }

    public void Reset()
    {
        _bars.Clear();
        _imbalances.Clear();
        _history.Clear();
        _next = 0;
        _bucketBuy = _bucketSell = _bucketSize = 0;
        _firstTime = default;
        _lastSample = default;
        _day = default;
        _sessionDelta = _sessionMoney = 0;
        _profile.Clear();
    }

    /// <summary>Takes in the trades that arrived since the last call.</summary>
    public void Update(TradeStore store)
    {
        if (store.Count == 0) return;
        // the store was replaced (history merged, a new instrument): start over
        if (_next > store.Count || (_firstTime != default && store.FirstTime != _firstTime)) Reset();
        _firstTime = store.FirstTime;
        // the first time only the part that can matter: a chart can hold millions of trades and this runs on the UI thread
        if (_next == 0) _next = store.LowerBound(store.LastTime - Keep);
        for (; _next < store.Count; _next++)
        {
            var t = store.GetTime(_next);
            long ticks = store.GetPriceTicks(_next);
            decimal vol = store.GetVolume(_next);
            bool buy = store.GetSide(_next) == TradeSide.Buy;
            var minute = new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            if (_bars.Count == 0 || _bars[^1].Start < minute)
            {
                _bars.Add(new MinuteBar(minute, ticks, ticks, ticks, ticks, 0, 0));
                if (_bars.Count > 1 && _bars[0].Start < minute - Keep) _bars.RemoveAt(0);
            }

            var bar = _bars[^1];
            if (bar.Start > minute) continue; // an old trade arriving late (a history merge): the bars stay in order
            _bars[^1] = bar with
            {
                HighTicks = Math.Max(bar.HighTicks, ticks),
                LowTicks = Math.Min(bar.LowTicks, ticks),
                CloseTicks = ticks,
                Buy = bar.Buy + (buy ? vol : 0),
                Sell = bar.Sell + (buy ? 0 : vol),
            };
            Bucket(t, vol, buy);

            var day = DateOnly.FromDateTime(t);
            if (day != _day)
            {
                _day = day;
                _sessionDelta = _sessionMoney = 0;
                _profile.Clear();
            }

            decimal signed = buy ? vol : -vol;
            _sessionDelta += signed;
            _sessionMoney += signed * store.Instrument.FromTicks(ticks) * store.Instrument.PointValue;
            _profile[ticks] = _profile.GetValueOrDefault(ticks) + vol;
        }
    }

    private void Bucket(DateTime time, decimal vol, bool buy)
    {
        if (_bucketSize <= 0) _bucketSize = Math.Max(vol * 20, 0.000001m); // a first guess until a closed hour exists
        decimal left = vol;
        while (left > 0)
        {
            decimal room = _bucketSize - (_bucketBuy + _bucketSell), part = Math.Min(room, left);
            if (buy) _bucketBuy += part; else _bucketSell += part;
            left -= part;
            if (_bucketBuy + _bucketSell < _bucketSize) break;
            _imbalances.Enqueue(Math.Abs(_bucketBuy - _bucketSell) / _bucketSize);
            if (_imbalances.Count > BucketCount) _imbalances.Dequeue();
            _bucketBuy = _bucketSell = 0;
            // a bucket is a fiftieth of the last hour of volume: it follows how busy the market is
            decimal hour = 0;
            for (int i = _bars.Count - 1; i >= 0 && _bars[i].Start > time.AddHours(-1); i--) hour += _bars[i].Volume;
            if (hour > 0) _bucketSize = Math.Max(hour / BucketCount, 0.000001m);
        }

        if (Vpin is { } v && time - _lastSample >= TimeSpan.FromMinutes(1))
        {
            _lastSample = time;
            _history.Add((time, v));
            if (_history.Count > 1600) _history.RemoveRange(0, _history.Count - 1440);
        }
    }

    /// <summary>Volume that bought and sold in the window ending at <paramref name="now"/>, from the store (for the last minute or two, finer than the bars).</summary>
    public static (decimal Buy, decimal Sell, int Count) Tape(TradeStore store, DateTime now, TimeSpan window)
    {
        decimal buy = 0, sell = 0;
        int i0 = store.LowerBound(now - window), i1 = store.LowerBound(now.AddTicks(1));
        for (int i = i0; i < i1; i++)
            if (store.GetSide(i) == TradeSide.Buy) buy += store.GetVolume(i); else sell += store.GetVolume(i);
        return (buy, sell, i1 - i0);
    }
}
