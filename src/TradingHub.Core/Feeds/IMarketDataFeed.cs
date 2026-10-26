using TradingHub.Market;

namespace TradingHub.Feeds;

public enum FeedStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error,
}

/// <summary>Receiver of live data. The feeds call it from THEIR own threads.</summary>
public interface IMarketDataSink
{
    void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades);
    void OnDepth(Instrument instrument, DepthUpdate update);
    void OnStatus(Instrument instrument, FeedStatus status, string? message);

    /// <summary>Order-level (Level 3) changes, from feeds that publish individual orders. Most sinks do not need them.</summary>
    void OnOrderEvents(Instrument instrument, IReadOnlyList<OrderEvent> events) { }

    /// <summary>Forced closings of leveraged positions, from feeds that publish them (Bybit).</summary>
    void OnLiquidations(Instrument instrument, IReadOnlyList<TradingHub.Data.Liquidation> liquidations) { }
}

/// <summary>A market data provider (exchange, simulator, replay...).</summary>
public interface IMarketDataFeed : IDisposable
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Whether it can load historical trades.</summary>
    bool SupportsHistory { get; }

    /// <summary>Whether the data comes from a real market (to warn in the UI).</summary>
    bool IsLive { get; }

    /// <summary>"Now" for this feed (a replay returns the playback clock).</summary>
    DateTime Now => DateTime.UtcNow;

    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct);

    /// <summary>
    /// Loads historical trades in chronological order, in batches. If <paramref name="fromId"/> is present
    /// it is used to continue from a known trade (more precise than the time).
    /// <paramref name="onBatch"/> returns false to stop the load.
    /// </summary>
    Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Subscribes to live trades and depth. Dispose to unsubscribe.</summary>
    IDisposable Subscribe(Instrument instrument, IMarketDataSink sink);
}

/// <summary>
/// Exchange candle (1, 5 or 15 minutes) with the aggressive buy volume, to load long history
/// (months or years) in seconds without downloading every trade.
/// </summary>
public readonly record struct Kline(long OpenTimeMs, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal BuyVolume, bool HasBuyVolume);

/// <summary>Feeds that provide history in candles (fast). The history loader uses it for whatever is not in ticks.</summary>
public interface IKlineHistory
{
    /// <summary>First instant with data for the instrument (for "the whole history"), or null if it is not known.</summary>
    Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct);

    /// <summary>
    /// Candles of <paramref name="minutes"/> minutes (1, 5 or 15) in chronological order, in batches.
    /// <paramref name="endPrice"/> is the price following the range (the simulator uses it to splice).
    /// </summary>
    Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Candle cache tag: changing it discards the old cache (for example if the way they are built changed).</summary>
    string CacheTag => "";
}

/// <summary>
/// Feeds with a market that closes (futures): they say when the last trade was, so with the market closed the
/// history shows the last session instead of an empty chart.
/// </summary>
public interface ISessionAwareFeed
{
    Task<DateTime?> LastTradeTimeAsync(Instrument instrument, CancellationToken ct);
}

/// <summary>Registry of available feeds.</summary>
public sealed class FeedRegistry
{
    private readonly Dictionary<string, Func<IMarketDataFeed>> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IMarketDataFeed> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Id, string Name)> _list = new();

    public void Register(string id, string displayName, Func<IMarketDataFeed> factory)
    {
        _factories[id] = factory;
        _list.Add((id, displayName));
    }

    public IReadOnlyList<(string Id, string Name)> Available => _list;

    public IMarketDataFeed Get(string id)
    {
        if (_instances.TryGetValue(id, out var f)) return f;
        if (!_factories.TryGetValue(id, out var factory)) throw new KeyNotFoundException($"Unknown feed: {id}");
        f = factory();
        _instances[id] = f;
        return f;
    }

    public void DisposeAll()
    {
        foreach (var f in _instances.Values)
        {
            try { f.Dispose(); } catch { /* ignorar */ }
        }

        _instances.Clear();
    }
}

/// <summary>24 h statistics of an instrument (to sort and show the picker).</summary>
public sealed record TickerStats(string Symbol, decimal LastPrice, decimal ChangePercent, decimal QuoteVolume, decimal High, decimal Low);

/// <summary>Feeds that can provide price, change and 24 h volume of every instrument they have.</summary>
public interface ITickerStatsProvider
{
    Task<IReadOnlyDictionary<string, TickerStats>> GetTickerStatsAsync(CancellationToken ct);
}
