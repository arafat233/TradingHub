using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>
/// Trades of a range in memory, in compact columns (~25 bytes per tick). It is loaded once
/// and shared by every run of an optimization. <see cref="Slice"/> does not copy.
/// </summary>
public sealed class TickData
{
    private readonly long[] _times;
    private readonly long[] _prices;
    private readonly double[] _volumes;
    private readonly byte[] _sides;

    private TickData(Instrument instrument, long[] times, long[] prices, double[] volumes, byte[] sides, int start, int end, bool compressed = false)
    {
        Instrument = instrument;
        _times = times;
        _prices = prices;
        _volumes = volumes;
        _sides = sides;
        Start = start;
        End = end;
        Compressed = compressed;
    }

    /// <summary>
    /// Consecutive trades at the same price, on the same side and within the same second are merged into one (with the volume
    /// added up): see <see cref="Compress"/>.
    /// </summary>
    public bool Compressed { get; }

    /// <summary>
    /// Merges consecutive trades at the same price, on the same side and within the same second (adding up the volume). Time or
    /// range candles come out identical (open, high, low, close, volume, delta and footprint) and the orders are
    /// filled at the same prices, with far fewer events to walk through. It changes the trade count of each candle:
    /// it is no good for candles by trade count, by volume or by delta, nor for strategies that look at every tick.
    /// </summary>
    public TickData Compress()
    {
        if (Compressed) return this;
        int n = 0;
        for (int i = Start; i < End; i++)
            if (i == Start || !SameRun(i - 1, i)) n++;
        var times = new long[n];
        var prices = new long[n];
        var volumes = new double[n];
        var sides = new byte[n];
        int k = -1;
        for (int i = Start; i < End; i++)
        {
            if (i > Start && SameRun(i - 1, i))
            {
                volumes[k] += _volumes[i];
                continue;
            }

            k++;
            times[k] = _times[i];
            prices[k] = _prices[i];
            volumes[k] = _volumes[i];
            sides[k] = _sides[i];
        }

        return new TickData(Instrument, times, prices, volumes, sides, 0, n, compressed: true) { DepthRoot = DepthRoot };
    }

    private bool SameRun(int a, int b) =>
        _prices[a] == _prices[b] && _sides[a] == _sides[b] && _times[a] / TimeSpan.TicksPerSecond == _times[b] / TimeSpan.TicksPerSecond;

    /// <summary>
    /// Folder with the recorded order books (.vdk) of this instrument, if there are any for the range: a backtest then gives
    /// the strategy <see cref="Strategy.Book"/>. Every run reads them with its own <see cref="DepthCursor"/>.
    /// </summary>
    public string? DepthRoot { get; init; }

    /// <summary>The same ticks, with the recorded books of that folder available if there are any between the first and last tick.</summary>
    public TickData WithDepth(string root) =>
        Count > 0 && DepthCursor.Exists(root, Instrument.FeedId, Instrument.Symbol, FirstTime, LastTime)
            ? new TickData(Instrument, _times, _prices, _volumes, _sides, Start, End, Compressed) { DepthRoot = root }
            : this;

    public Instrument Instrument { get; }
    public int Start { get; }
    public int End { get; }
    public int Count => End - Start;
    public DateTime FirstTime => Count > 0 ? new DateTime(_times[Start], DateTimeKind.Utc) : DateTime.MinValue;
    public DateTime LastTime => Count > 0 ? new DateTime(_times[End - 1], DateTimeKind.Utc) : DateTime.MinValue;
    public long MemoryBytes => (long)_times.Length * 25;

    public long TimeTicks(int i) => _times[i];
    public long PriceTicks(int i) => _prices[i];
    public decimal Volume(int i) => (decimal)_volumes[i];
    public TradeSide Side(int i) => (TradeSide)_sides[i];

    public Trade Get(int i) => new(i, new DateTime(_times[i], DateTimeKind.Utc), Instrument.FromTicks(_prices[i]), (decimal)_volumes[i], (TradeSide)_sides[i]);

    /// <summary>Sub-range by time (without copying).</summary>
    public TickData Slice(DateTime fromUtc, DateTime toUtc)
    {
        int a = Lower(fromUtc.Ticks), b = Lower(toUtc.Ticks);
        return new TickData(Instrument, _times, _prices, _volumes, _sides, a, b, Compressed) { DepthRoot = DepthRoot };
    }

    private int Lower(long t)
    {
        int lo = Start, hi = End;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_times[mid] < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// Loads an instrument's trades between two instants from disk. With <paramref name="compress"/> it merges them
    /// while loading (like <see cref="Compress"/>), never holding all the originals in memory.
    /// </summary>
    public static TickData Load(string root, Instrument instrument, DateTime fromUtc, DateTime toUtc, IProgress<double>? progress, CancellationToken ct, bool compress = false)
    {
        var days = new List<DateOnly>();
        for (var d = DateOnly.FromDateTime(fromUtc); d <= DateOnly.FromDateTime(toUtc); d = d.AddDays(1)) days.Add(d);

        int n = 0, cap = 1 << 20;
        var times = new long[cap];
        var prices = new long[cap];
        var volumes = new double[cap];
        var sides = new byte[cap];
        for (int k = 0; k < days.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            var list = HistorySources.ReadDay(root, instrument.FeedId, instrument.Symbol, days[k]);
            if (list.Count > 0)
            {
                foreach (var t in list)
                {
                    if (t.Time < fromUtc || t.Time >= toUtc) continue;
                    if (n == cap)
                    {
                        cap *= 2;
                        Array.Resize(ref times, cap);
                        Array.Resize(ref prices, cap);
                        Array.Resize(ref volumes, cap);
                        Array.Resize(ref sides, cap);
                    }

                    // if something ended up out of time order, it is sorted while keeping the id order
                    long ticks = t.Time.Ticks;
                    if (n > 0 && ticks < times[n - 1]) ticks = times[n - 1];
                    long price = instrument.ToTicks(t.Price);
                    byte side = (byte)t.Side;
                    if (compress && n > 0 && prices[n - 1] == price && sides[n - 1] == side && times[n - 1] / TimeSpan.TicksPerSecond == ticks / TimeSpan.TicksPerSecond)
                    {
                        volumes[n - 1] += (double)t.Volume;
                        continue;
                    }

                    times[n] = ticks;
                    prices[n] = price;
                    volumes[n] = (double)t.Volume;
                    sides[n] = side;
                    n++;
                }
            }

            progress?.Report((k + 1.0) / days.Count);
        }

        if (compress && n < times.Length)
        {
            // the compressed ones usually take far less than what was reserved: it is trimmed
            Array.Resize(ref times, n);
            Array.Resize(ref prices, n);
            Array.Resize(ref volumes, n);
            Array.Resize(ref sides, n);
        }

        return new TickData(instrument, times, prices, volumes, sides, 0, n, compress);
    }

    /// <summary>Creates data from a list (tests and synthetic data).</summary>
    /// <summary>Copies the history a chart has loaded (without going through Trade objects).</summary>
    public static TickData FromStore(TradeStore store)
    {
        int n = store.Count;
        var times = new long[n];
        var prices = new long[n];
        var volumes = new double[n];
        var sides = new byte[n];
        for (int i = 0; i < n; i++)
        {
            times[i] = store.GetTimeTicks(i);
            prices[i] = store.GetPriceTicks(i);
            volumes[i] = (double)store.GetVolume(i);
            sides[i] = (byte)store.GetSide(i);
        }

        return new TickData(store.Instrument, times, prices, volumes, sides, 0, n);
    }

    public static TickData FromTrades(Instrument instrument, IReadOnlyList<Trade> trades)
    {
        int n = trades.Count;
        var times = new long[n];
        var prices = new long[n];
        var volumes = new double[n];
        var sides = new byte[n];
        for (int i = 0; i < n; i++)
        {
            times[i] = trades[i].Time.Ticks;
            prices[i] = instrument.ToTicks(trades[i].Price);
            volumes[i] = (double)trades[i].Volume;
            sides[i] = (byte)trades[i].Side;
        }

        return new TickData(instrument, times, prices, volumes, sides, 0, n);
    }
}
