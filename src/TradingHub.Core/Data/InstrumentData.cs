using System.Collections.Concurrent;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// All of an instrument's data: trades (history + live), book and connection state.
/// The feeds write from their own threads into queues; the UI calls <see cref="Pump"/> on its thread to
/// apply the changes and fire the events. Several charts can share the same instance.
/// </summary>
public sealed class InstrumentData : IMarketDataSink, IDisposable
{
    private readonly ConcurrentQueue<Trade> _incoming = new();
    private readonly ConcurrentQueue<BookChange> _depthChanges = new();
    private volatile DepthSnapshot? _latestDepth;
    private volatile bool _depthFull;
    private volatile bool _historyMerged;
    private List<Trade>? _historyToMerge;
    private readonly object _historyLock = new();
    private IDisposable? _subscription;
    private CancellationTokenSource? _cts;
    private TickRecorder? _recorder;
    private DepthRecorder? _depthRecorder;
    private FeedStatus _status = FeedStatus.Disconnected;
    private string? _statusMessage;
    private volatile bool _statusDirty;

    public InstrumentData(Instrument instrument, IMarketDataFeed feed)
    {
        Instrument = instrument;
        Feed = feed;
        Trades = new TradeStore(instrument);
    }

    public Instrument Instrument { get; }
    public IMarketDataFeed Feed { get; }
    public TradeStore Trades { get; }
    public DepthSnapshot Depth { get; private set; } = DepthSnapshot.Empty;

    /// <summary>The book over the last minutes (for the liquidity heatmap); it only has data on feeds that send depth.</summary>
    public DepthHistory DepthHistory { get; } = new();

    /// <summary>Big orders added, pulled, flashed and eaten, from feeds that publish individual orders (Level 3); empty on the others.</summary>
    public OrderFlowDetector OrderFlow { get; } = new();

    /// <summary>Icebergs seen in the real order events (empty on feeds that do not send them).</summary>
    public OrderIcebergDetector OrderIcebergs { get; } = new() { Keep = TimeSpan.FromHours(4) };

    /// <summary>The liquidations of the last 24 hours (feeds that publish them), oldest first.</summary>
    public List<Liquidation> Liquidations { get; } = new();

    private readonly ConcurrentQueue<Liquidation> _liquidations = new();
    private DateTime _liqMarksAt;
    private DateTime _liqEstAt;

    private InsightsState? _insights;

    /// <summary>The Insights panel's state for this instrument (flow tracker, edge meter), made when first asked for.</summary>
    public InsightsState Insights => _insights ??= new InsightsState(this);

    private readonly ConcurrentQueue<OrderEvent> _orderEvents = new();

    public FeedStatus Status => _status;
    public string? StatusMessage => _statusMessage;
    public bool IsLoadingHistory { get; private set; }
    public double HistoryProgress { get; private set; }
    public string? HistoryError { get; private set; }

    /// <summary>Since when there is history requested/loaded (UTC).</summary>
    public DateTime HistoryFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>Whether it is fetching older history in the background.</summary>
    public bool IsExtending { get; private set; }

    private List<Trade>? _olderToMerge;
    private DateTime _pendingFrom;

    /// <summary>The market's last trade when it is closed (the history is built up to there).</summary>
    private DateTime? _anchor;

    public decimal LastPrice => Trades.Count > 0 ? Trades.GetPrice(Trades.Count - 1) : 0m;
    public decimal BestBid => Depth.HasBid ? Instrument.FromTicks(Depth.BestBidTicks) : 0m;
    public decimal BestAsk => Depth.HasAsk ? Instrument.FromTicks(Depth.BestAskTicks) : 0m;

    /// <summary>New trades added to the store (start index, count). UI thread.</summary>
    public event Action<int, int>? TradesAppended;

    /// <summary>The history finished loading (the charts must be rebuilt). UI thread.</summary>
    public event Action? HistoryLoaded;

    /// <summary>The book changed. UI thread.</summary>
    public event Action<DepthUpdate>? DepthUpdated;

    public event Action? StatusChanged;

    /// <summary>Folder the ticks are recorded into (null = do not record).</summary>
    public string? RecordingRoot { get; set; }

    /// <summary>Whether it records what arrives live (the folder keeps being read even if it does not record).</summary>
    public bool RecordLive { get; set; } = true;

    /// <summary>Also record the order book (.vdk files) when the feed sends depth.</summary>
    public bool RecordDepth { get; set; } = true;

    /// <summary>Levels per side that are recorded.</summary>
    public int RecordDepthLevels { get; set; } = 20;

    // ----------------------------------------------------------------- startup

    /// <summary>Subscribes to the live feed and loads history from <paramref name="historyFrom"/>.</summary>
    public void Start(DateTime historyFrom)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _historyMerged = false;
        IsLoadingHistory = true;
        HistoryFrom = historyFrom;
        HistoryProgress = 0;
        HistoryError = null;
        SetStatus(FeedStatus.Connecting, "Connecting…");

        if (Feed.IsLive && RecordingRoot != null && RecordLive)
            _recorder = new TickRecorder(RecordingRoot, Instrument);

        // the book too (the heatmap can then be replayed and strategies can be backtested on it); it shares the ticks' lock
        if (_recorder is { IsActive: true } && RecordDepth)
            _depthRecorder = new DepthRecorder(RecordingRoot!, Instrument) { Levels = RecordDepthLevels };

        // 1) live first (it accumulates while the history loads)
        try
        {
            _subscription = Feed.Subscribe(Instrument, this);
        }
        catch (Exception ex)
        {
            SetStatus(FeedStatus.Error, ex.Message);
        }

        // 2) history: local files + whatever gaps are left, from the feed
        _ = Task.Run(async () =>
        {
            List<Trade> all;
            try
            {
                var from = historyFrom;
                if (Feed is ISessionAwareFeed session)
                {
                    // market closed (night, weekend): the same length of history, ending at the last trade
                    try
                    {
                        var last = await session.LastTradeTimeAsync(Instrument, ct).ConfigureAwait(false);
                        if (last is { } lt && Feed.Now - lt > TimeSpan.FromMinutes(10))
                        {
                            _anchor = lt;
                            from = lt - (Feed.Now - historyFrom);
                            HistoryFrom = from;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // without the time of the last trade it loads as usual
                    }
                }

                all = await LoadRangeAsync(from, Feed.Now, long.MaxValue, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                HistoryError = ex.Message;
                all = new List<Trade>();
            }

            lock (_historyLock) _historyToMerge = all;
        }, ct);
    }

    /// <summary>
    /// How far back ticks are requested from the exchange when they are missing. Anything older (and with no ticks on disk)
    /// is filled in with exchange candles: that way months or years load in seconds, as on any platform.
    /// </summary>
    public TimeSpan TickWindow { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Maximum real trades read from disk per load (what does not fit is filled in with candles).</summary>
    public static int MaxDiskTrades { get; set; } = 30_000_000;

    /// <summary>Since when there are real ticks (before that, the history comes from exchange candles).</summary>
    public DateTime RealTicksFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>
    /// Reads what was recorded/downloaded between <paramref name="from"/> and <paramref name="to"/> and fills the gaps:
    /// the recent part with exchange ticks (they are recorded so they are not requested again) and the old part with candles.
    /// </summary>
    private async Task<List<Trade>> LoadRangeAsync(DateTime from, DateTime to, long maxIdExclusive, CancellationToken ct)
    {
        var real = new List<Trade>();
        if (RecordingRoot != null && Feed.IsLive)
        {
            // from the newest to the oldest, up to the memory cap
            var chunks = new List<List<Trade>>();
            long count = 0;
            foreach (var day in EachDay(from, to).Reverse())
            {
                var path = TickFile.DayPath(RecordingRoot, Instrument.FeedId, Instrument.Symbol, day);
                if (!File.Exists(path)) continue;
                var list = TickFile.Read(path).Where(t => t.Time >= from && t.Time < to && t.Id < maxIdExclusive).ToList();
                if (count > 0 && count + list.Count > MaxDiskTrades) break;
                chunks.Add(list);
                count += list.Count;
            }

            chunks.Reverse();
            foreach (var c in chunks) real.AddRange(c);
            real = TickFile.SortAndDedupe(real);
        }

        if (!Feed.SupportsHistory) return real;
        var gaps = FindGaps(real, from, to, tailById: maxIdExclusive == long.MaxValue);
        if (gaps.Count == 0) return real;

        // the recent part as ticks; the old part as candles (if the feed has them)
        var klineSource = Feed as IKlineHistory;
        // the ticks cover the last market hours (with the market closed, before the last trade)
        DateTime tickCut = klineSource != null ? (_anchor ?? Feed.Now) - TickWindow : DateTime.MinValue;
        var tickGaps = new List<(DateTime From, DateTime To, long? FromId)>();
        var klineGaps = new List<(DateTime From, DateTime To)>();
        foreach (var (gFrom, gTo, fromId) in gaps)
        {
            if (gTo <= tickCut) klineGaps.Add((gFrom, gTo));
            else if (gFrom >= tickCut) tickGaps.Add((gFrom, gTo, fromId));
            else
            {
                klineGaps.Add((gFrom, tickCut));
                tickGaps.Add((tickCut, gTo, null));
            }
        }

        int parts = tickGaps.Count + (klineGaps.Count > 0 ? 1 : 0);
        int part = 0;
        var fetched = new List<Trade>();
        foreach (var (gFrom, gTo, fromId) in tickGaps)
        {
            int index = part++;
            var progress = new Progress<double>(p => HistoryProgress = (index + Math.Clamp(p, 0, 1)) / parts);
            await Feed.LoadTradesAsync(Instrument, gFrom, gTo, fromId, batch =>
            {
                foreach (var t in batch)
                    if (t.Id < maxIdExclusive && t.Time >= from && t.Time <= to) fetched.Add(t);
                return !ct.IsCancellationRequested;
            }, progress, ct).ConfigureAwait(false);
        }

        if (fetched.Count > 0)
        {
            _recorder?.WriteHistory(fetched);
            real.AddRange(fetched);
            real = TickFile.SortAndDedupe(real);
        }

        if (real.Count > 0) RealTicksFrom = real[0].Time < RealTicksFrom ? real[0].Time : RealTicksFrom;
        if (klineGaps.Count == 0 || klineSource == null) return real;

        // candles only where there are no real ticks
        var windows = KlineHistory.Subtract(klineGaps, KlineHistory.Coverage(real));
        if (windows.Count == 0) return real;
        var kFrom = windows[0].From;
        var kTo = windows[^1].To;
        int minutes = KlineHistory.MinutesFor(kTo - kFrom);
        decimal endPrice = real.Count > 0 ? real[0].Price : Trades.Count > 0 ? Trades.GetPrice(0) : 0;
        int kIndex = part;
        var kProgress = new Progress<double>(p => HistoryProgress = (kIndex + Math.Clamp(p, 0, 1)) / parts);
        var klines = await KlineHistory.LoadAsync(klineSource, Instrument, kFrom, kTo, minutes, endPrice, kProgress, ct).ConfigureAwait(false);
        var synthetic = new List<Trade>(klines.Count * 4);
        KlineHistory.Synthesize(klines, Instrument, minutes, windows, synthetic);
        return KlineHistory.MergeByTime(synthetic, real);
    }

    /// <summary>Ranges missing in <paramref name="local"/> (sorted) to cover [from, to).</summary>
    public static List<(DateTime From, DateTime To, long? FromId)> FindGaps(IReadOnlyList<Trade> local, DateTime from, DateTime to, bool tailById)
    {
        var gaps = new List<(DateTime, DateTime, long?)>();
        if (local.Count == 0)
        {
            gaps.Add((from, to, null));
            return gaps;
        }

        if (local[0].Time - from > TimeSpan.FromMinutes(1)) gaps.Add((from, local[0].Time, null));
        for (int i = 1; i < local.Count; i++)
        {
            if (local[i].Time - local[i - 1].Time > TimeSpan.FromMinutes(3))
                gaps.Add((local[i - 1].Time, local[i].Time, null));
        }

        // too many cuts (illiquid instrument): better to request everything at once
        if (gaps.Count > 20)
        {
            gaps.Clear();
            gaps.Add((from, to, null));
            return gaps;
        }

        if (to - local[^1].Time > TimeSpan.FromSeconds(1))
            gaps.Add((local[^1].Time, to, tailById ? local[^1].Id + 1 : null));
        return gaps;
    }

    /// <summary>
    /// Fetches older history up to <paramref name="from"/> (if needed): first from the recorded/downloaded
    /// files and whatever is missing from the feed. When it finishes, the charts are rebuilt.
    /// </summary>
    public bool ExtendHistory(DateTime from)
    {
        if (from >= HistoryFrom || IsLoadingHistory || IsExtending) return false;
        var ct = _cts?.Token ?? CancellationToken.None;
        DateTime until = Trades.Count > 0 ? Trades.FirstTime : Feed.Now;
        // if the first thing available is candle history (negative ids), the ids are no good for cutting
        long firstId = Trades.Count > 0 && Trades.GetId(0) > 0 ? Trades.GetId(0) : long.MaxValue;
        IsExtending = true;
        HistoryProgress = 0;
        _pendingFrom = from;
        _ = Task.Run(async () =>
        {
            var older = new List<Trade>();
            try
            {
                older = await LoadRangeAsync(from, until, firstId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HistoryError = ex.Message;
            }

            lock (_historyLock) _olderToMerge = older;
        }, ct);
        return true;
    }

    private long _lastDataAt;
    private int _tradedSinceAnchor;

    /// <summary>When the last live data arrived (trade or book), in real UTC time.</summary>
    public DateTime LastDataAt => new(Interlocked.Read(ref _lastDataAt), DateTimeKind.Utc);

    /// <summary>On opening, the market was closed (the last trade was old) and it has not traded again yet.</summary>
    public bool MarketClosed => _anchor != null && Volatile.Read(ref _tradedSinceAnchor) == 0;

    /// <summary>Last known trade when the market was closed on opening.</summary>
    public DateTime? ClosedAt => _anchor;

    /// <summary>
    /// Forces an immediate reconnect (for example when the Mac wakes up). The feeds that support it drop
    /// the connection and backfill what was missed; the rest just resubscribe.
    /// </summary>
    public void Reconnect()
    {
        if (_subscription is IReconnectable r)
        {
            r.Reconnect();
            return;
        }

        if (_subscription == null || !Feed.IsLive) return;
        _subscription.Dispose();
        try
        {
            _subscription = Feed.Subscribe(Instrument, this);
        }
        catch (Exception ex)
        {
            SetStatus(FeedStatus.Error, ex.Message);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _subscription?.Dispose();
        _subscription = null;
        _recorder?.Dispose();
        _recorder = null;
        _depthRecorder?.Dispose();
        _depthRecorder = null;
    }

    private static IEnumerable<DateOnly> EachDay(DateTime fromUtc, DateTime toUtc)
    {
        for (var d = DateOnly.FromDateTime(fromUtc); d <= DateOnly.FromDateTime(toUtc); d = d.AddDays(1))
            yield return d;
    }

    // ----------------------------------------------------------------- IMarketDataSink (feed threads)

    void IMarketDataSink.OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
    {
        foreach (var t in trades) _incoming.Enqueue(t);
        _recorder?.WriteLive(trades);
        _lastDataAt = DateTime.UtcNow.Ticks;
        if (trades.Count > 0 && _anchor != null) Volatile.Write(ref _tradedSinceAnchor, 1);
    }

    void IMarketDataSink.OnDepth(Instrument instrument, DepthUpdate update)
    {
        _lastDataAt = DateTime.UtcNow.Ticks;
        _latestDepth = update.Snapshot;
        if (update.IsFullRefresh) _depthFull = true;
        foreach (var c in update.Changes) _depthChanges.Enqueue(c);
        // keeps the queue from growing without limit if the UI is stuck
        while (_depthChanges.Count > 50_000) _depthChanges.TryDequeue(out _);
    }

    void IMarketDataSink.OnStatus(Instrument instrument, FeedStatus status, string? message) => SetStatus(status, message);

    void IMarketDataSink.OnLiquidations(Instrument instrument, IReadOnlyList<Liquidation> liquidations)
    {
        foreach (var l in liquidations) _liquidations.Enqueue(l);
        while (_liquidations.Count > 20_000) _liquidations.TryDequeue(out _);
    }

    void IMarketDataSink.OnOrderEvents(Instrument instrument, IReadOnlyList<OrderEvent> events)
    {
        _lastDataAt = DateTime.UtcNow.Ticks;
        foreach (var e in events) _orderEvents.Enqueue(e);
        while (_orderEvents.Count > 100_000) _orderEvents.TryDequeue(out _); // the UI is stuck: keep the newest
    }

    private void SetStatus(FeedStatus status, string? message)
    {
        _status = status;
        _statusMessage = message;
        _statusDirty = true;
    }

    // ----------------------------------------------------------------- UI thread

    /// <summary>Applies what was received. Call it periodically from the UI thread.</summary>
    public void Pump()
    {
        if (_statusDirty)
        {
            _statusDirty = false;
            StatusChanged?.Invoke();
        }

        if (!_historyMerged)
        {
            List<Trade>? hist;
            lock (_historyLock)
            {
                hist = _historyToMerge;
                _historyToMerge = null;
            }

            if (hist != null)
            {
                Trades.Clear();
                foreach (var t in hist) Trades.Add(t);
                _historyMerged = true;
                IsLoadingHistory = false;
                HistoryProgress = 1;
                DrainIncoming(skipKnown: true, raise: false);
                HistoryLoaded?.Invoke();
                StatusChanged?.Invoke();
            }
        }
        else
        {
            List<Trade>? older;
            lock (_historyLock)
            {
                older = _olderToMerge;
                _olderToMerge = null;
            }

            if (older != null)
            {
                // rebuilds the store: old history + what was already there
                if (older.Count > 0)
                {
                    var current = new List<Trade>(Trades.Count);
                    for (int i = 0; i < Trades.Count; i++) current.Add(Trades.Get(i));
                    Trades.Clear();
                    foreach (var t in older) Trades.Add(t);
                    foreach (var t in current) Trades.Add(t);
                }

                HistoryFrom = _pendingFrom;
                IsExtending = false;
                HistoryProgress = 1;
                if (older.Count > 0) HistoryLoaded?.Invoke();
                StatusChanged?.Invoke();
            }

            DrainIncoming(skipKnown: true, raise: true);
        }

        if (!_orderEvents.IsEmpty)
        {
            var batch = new List<OrderEvent>();
            while (_orderEvents.TryDequeue(out var oe)) batch.Add(oe);
            OrderFlow.Apply(batch);
            foreach (var oe in batch) OrderIcebergs.OnEvent(oe);
        }

        OrderFlow.Resolve(Trades, Feed.Now);

        if (!_liquidations.IsEmpty)
        {
            while (_liquidations.TryDequeue(out var lq)) Liquidations.Add(lq);
            var cut = DateTime.UtcNow.AddHours(-24);
            if (Liquidations.Count > 0 && Liquidations[0].Time < cut) Liquidations.RemoveAll(l => l.Time < cut);
        }

        // the clusters go on the shared board for the chart overlay and the panel (at most every 10 s)
        if (Liquidations.Count > 0 && DateTime.UtcNow - _liqMarksAt > TimeSpan.FromSeconds(10))
        {
            _liqMarksAt = DateTime.UtcNow;
            MarketLevels.Set(Instrument.Key, "liquidations", LiquidationClusters.Marks(Liquidations, Instrument, DateTime.UtcNow, TimeSpan.FromHours(6)));
        }

        // estimated liquidation prices from where the big orders went in (a scan of the last hours: once a minute is plenty)
        if (DateTime.UtcNow - _liqEstAt > TimeSpan.FromMinutes(1) && Trades.Count > 1000)
        {
            _liqEstAt = DateTime.UtcNow;
            MarketLevels.Set(Instrument.Key, "liq-estimate", LiquidationProjection.Marks(Trades, Instrument, Feed.Now, TimeSpan.FromHours(6)));
        }

        var depth = _latestDepth;
        if (depth != null && !ReferenceEquals(depth, Depth))
        {
            Depth = depth;
            var changes = new List<BookChange>();
            while (_depthChanges.TryDequeue(out var c)) changes.Add(c);
            bool full = _depthFull;
            _depthFull = false;
            DepthHistory.Add(Feed.Now, depth);
            _depthRecorder?.Offer(Feed.Now, depth);
            DepthUpdated?.Invoke(new DepthUpdate { Snapshot = depth, Changes = changes, IsFullRefresh = full });
        }
    }

    private void DrainIncoming(bool skipKnown, bool raise)
    {
        if (_incoming.IsEmpty) return;
        int start = Trades.Count;
        long lastId = Trades.LastId;
        long lastTicks = Trades.Count > 0 ? Trades.GetTimeTicks(Trades.Count - 1) : long.MinValue;
        while (_incoming.TryDequeue(out var t))
        {
            // it was already there: known id and not newer than the last stored one. If an odd id ended up ahead
            // (corrupt file), the genuinely new data is recognized by time and the live feed is not lost.
            if (skipKnown && t.Id <= lastId && t.Time.Ticks <= lastTicks) continue;
            Trades.Add(t);
            lastId = Math.Max(lastId, t.Id);
            if (t.Time.Ticks > lastTicks) lastTicks = t.Time.Ticks;
        }

        int added = Trades.Count - start;
        if (added > 0 && raise) TradesAppended?.Invoke(start, added);
    }

    public void Dispose() => Stop();
}

/// <summary>Records ticks to disk on a separate thread (it does not block the UI).</summary>
public sealed class TickRecorder : IDisposable
{
    private readonly string _root;
    private readonly Instrument _instrument;
    private readonly BlockingCollection<Trade> _queue = new(new ConcurrentQueue<Trade>());
    private readonly Thread? _thread;
    private readonly FileStream? _lock;
    private TickFile.Writer? _writer;
    private DateOnly _day;

    public TickRecorder(string root, Instrument instrument)
    {
        _root = root;
        _instrument = instrument;
        // a single process records each instrument: if another Trading Hub is already recording it, this one only reads
        _lock = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol);
        if (_lock == null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "recorder-" + instrument.Symbol };
        _thread.Start();
    }

    /// <summary>False if another process is already recording this instrument (nothing is written).</summary>
    public bool IsActive => _lock != null;

    public void WriteLive(IReadOnlyList<Trade> trades)
    {
        if (_lock == null) return;
        foreach (var t in trades) _queue.TryAdd(t);
    }

    public void WriteHistory(IReadOnlyList<Trade> trades)
    {
        if (_lock == null) return;
        foreach (var t in trades) _queue.TryAdd(t);
    }

    private void Run()
    {
        try
        {
            foreach (var t in _queue.GetConsumingEnumerable())
            {
                var day = DateOnly.FromDateTime(t.Time);
                if (_writer == null || day != _day)
                {
                    _writer?.Dispose();
                    _day = day;
                    _writer = new TickFile.Writer(TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day), _instrument);
                }

                _writer.Write(t);
                if (_queue.Count == 0) _writer.Flush();
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _writer?.Dispose();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread?.Join(2000);
        _lock?.Dispose();
    }
}

/// <summary>Manages the open <see cref="InstrumentData"/> instances (one per feed+symbol).</summary>
public sealed class MarketDataHub : IDisposable
{
    private readonly Dictionary<string, InstrumentData> _items = new();

    public string? RecordingRoot { get; set; }

    /// <summary>Whether new instruments record the live feed.</summary>
    public bool RecordLive { get; set; } = true;

    /// <summary>Also record the order book (.vdk files) when the feed sends depth.</summary>
    public bool RecordDepth { get; set; } = true;

    /// <summary>Levels per side that are recorded.</summary>
    public int RecordDepthLevels { get; set; } = 20;

    public IReadOnlyCollection<InstrumentData> Items => _items.Values;

    public InstrumentData GetOrStart(IMarketDataFeed feed, Instrument instrument, TimeSpan history)
    {
        if (_items.TryGetValue(instrument.Key, out var d)) return d;
        // exchange ticks for the recent part; the older part is filled in with candles (fast even for years)
        var window = history < TimeSpan.FromHours(2) ? TimeSpan.FromHours(2) : history > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : history;
        d = new InstrumentData(instrument, feed) { RecordingRoot = RecordingRoot, RecordLive = RecordLive, RecordDepth = RecordDepth, RecordDepthLevels = RecordDepthLevels, TickWindow = window };
        _items[instrument.Key] = d;
        d.Start(feed.Now - history);
        return d;
    }

    public void Release(InstrumentData data)
    {
        // the data is kept for as long as the app is open (switching charts is instant)
    }

    public void PumpAll()
    {
        foreach (var d in _items.Values) d.Pump();
    }

    /// <summary>Reconnects every live instrument (the Mac woke up, the internet came back...).</summary>
    public void ReconnectAll()
    {
        foreach (var d in _items.Values) d.Reconnect();
    }

    public void Dispose()
    {
        foreach (var d in _items.Values) d.Dispose();
        _items.Clear();
    }
}
