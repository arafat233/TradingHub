using TradingHub.Market;

namespace TradingHub.Feeds.Rithmic;

/// <summary>Front-month contract (ES -> ESZ6) and time of the last known trade.</summary>
internal sealed record RithmicContract(string Symbol, string Exchange, DateTime? LastTrade, DateTime When);

/// <summary>
/// The single quote session of a Rithmic connection. Rithmic allows a single session per user on each
/// plant: a second one cuts the first. That is why every chart (ES, MES, NQ…), cross trading and the
/// contract queries share this connection, and it reconnects just once for all of them.
/// </summary>
internal sealed class RithmicTickerPlant : IDisposable
{
    private sealed class Sub
    {
        public required Instrument Instrument;
        public readonly List<IMarketDataSink> Sinks = new();
        public int Probes;
        public string Symbol = "", Exchange = "";
        public bool Subscribed, Ready;
        public DateTime AskedAt = DateTime.MaxValue, SubscribedAt = DateTime.MaxValue;
        public DateTime? LastTrade;
        public TaskCompletionSource<RithmicContract> ReadyTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long LastId;
        public OrderBookBuilder Book = new();
        public long LastBid = long.MinValue, LastAsk = long.MaxValue;

        /// <summary>Full depth is arriving (the best bid/ask no longer touch the book) / Rithmic refused the depth (it asks without it).</summary>
        public bool HasBook, NoBook, BookFull;

        /// <summary>Resting orders by exchange id (a delete message carries no size) / the account has no order-level entitlement.</summary>
        public readonly Dictionary<long, (bool Ask, long Ticks, decimal Size)> Orders = new();
        public bool NoMbo;

        public int Bits => NoBook ? Rp.TradesQuotes : Rp.TradesQuotesBook;
        public string Key => Symbol + "|" + Exchange;
    }

    private readonly Func<RithmicSettings> _settings;
    private readonly Action<Instrument, RithmicContract> _onContract;
    private readonly object _gate = new();
    private readonly Dictionary<string, Sub> _subs = new();
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _conn;
    private RithmicClient? _client;
    private Task? _loop;
    private long _nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<(bool Ok, string Message)> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RithmicTickerPlant(Func<RithmicSettings> settings, Action<Instrument, RithmicContract> onContract)
    {
        _settings = settings;
        _onContract = onContract;
    }

    public FeedStatus Status { get; private set; } = FeedStatus.Disconnected;
    public string? StatusMessage { get; private set; }

    /// <summary>Times it logged in (for the tests: it has to be a single one per connection).</summary>
    public int Logins { get; private set; }

    public bool IsConnected => Status == FeedStatus.Connected;

    private void Start()
    {
        lock (_gate) _loop ??= Task.Run(RunAsync);
    }

    private void Wake()
    {
        TaskCompletionSource w;
        lock (_gate)
        {
            w = _wake;
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        w.TrySetResult();
    }

    /// <summary>Drops the connection and logs in again (for example after changing the configuration or when the Mac wakes up).</summary>
    public void Reconnect()
    {
        lock (_gate) _conn?.Cancel();
        Wake();
    }

    public async Task<(bool Ok, string Message)> WaitConnectedAsync(TimeSpan timeout, CancellationToken ct)
    {
        Start();
        Task<(bool, string)> t;
        lock (_gate) t = _connected.Task;
        try
        {
            return await t.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return (false, StatusMessage ?? "Rithmic did not respond in time");
        }
    }

    // ------------------------------------------------------------------ subscriptions

    private Sub GetOrCreate(Instrument instrument)
    {
        if (!_subs.TryGetValue(instrument.Key, out var sub)) _subs[instrument.Key] = sub = new Sub { Instrument = instrument };
        return sub;
    }

    public void Attach(Instrument instrument, IMarketDataSink sink)
    {
        Sub sub;
        lock (_gate)
        {
            sub = GetOrCreate(instrument);
            sub.Sinks.Add(sink);
        }

        Start();
        if (Status == FeedStatus.Connected)
            sink.OnStatus(instrument, FeedStatus.Connected, sub.Subscribed ? $"Rithmic: {sub.Symbol} ({_settings().System})" : StatusMessage);
        else
            sink.OnStatus(instrument, Status == FeedStatus.Disconnected ? FeedStatus.Connecting : Status, StatusMessage ?? "Connecting to Rithmic…");
        _ = KickAsync(sub);
    }

    public void Detach(Instrument instrument, IMarketDataSink sink)
    {
        lock (_gate)
        {
            if (!_subs.TryGetValue(instrument.Key, out var sub)) return;
            sub.Sinks.Remove(sink);
            Cleanup(sub);
        }
    }

    /// <summary>Removes the subscription if nobody is using it any more (and tells Rithmic if the contract was left with no takers).</summary>
    private void Cleanup(Sub sub)
    {
        if (sub.Sinks.Count > 0 || sub.Probes > 0) return;
        _subs.Remove(sub.Instrument.Key);
        if (sub.Subscribed && _client is { } c && !_subs.Values.Any(s => s.Subscribed && s.Key == sub.Key))
        {
            _ = SafeSend(c, MarketData(sub.Symbol, sub.Exchange, 2, sub.Bits), CancellationToken.None);
            if (!sub.NoMbo) _ = SafeSend(c, DepthByOrder(sub.Symbol, sub.Exchange, 2), CancellationToken.None);
        }
    }

    /// <summary>Front-month contract and last trade: from the live subscription if there is one, otherwise a short query.</summary>
    public async Task<RithmicContract> ResolveAsync(Instrument instrument, CancellationToken ct)
    {
        Sub sub;
        lock (_gate)
        {
            sub = GetOrCreate(instrument);
            if (sub.Ready && sub.Sinks.Count > 0) return new RithmicContract(sub.Symbol, sub.Exchange, sub.LastTrade, DateTime.UtcNow);
            sub.Probes++;
        }

        Start();
        _ = KickAsync(sub);
        try
        {
            return await sub.ReadyTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("Rithmic did not reply with the front-month contract: " + (StatusMessage ?? "not connected"));
        }
        finally
        {
            lock (_gate)
            {
                sub.Probes--;
                Cleanup(sub);
            }
        }
    }

    /// <summary>If there is already a session, it requests the front-month contract of a new subscription.</summary>
    private async Task KickAsync(Sub sub)
    {
        RithmicClient? c;
        lock (_gate)
        {
            c = Status == FeedStatus.Connected ? _client : null;
            if (c == null || sub.AskedAt != DateTime.MaxValue) return;
            sub.AskedAt = DateTime.UtcNow;
        }

        await AskFrontMonth(c, sub, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task SafeSend(RithmicClient c, ProtoWriter w, CancellationToken ct)
    {
        try
        {
            await c.SendAsync(w, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // the connection dropped: the main loop reconnects and requests everything again
        }
    }

    private static ProtoWriter MarketData(string symbol, string exchange, int request, int bits) =>
        new ProtoWriter().Int(Rp.TemplateId, Rp.RequestMarketDataUpdate).String(Rp.UserMsg, "md|" + symbol + "|" + exchange)
            .String(Rp.Symbol, symbol).String(Rp.Exchange, exchange).Int(Rp.Request, request).Int(Rp.UpdateBits, bits);

    private static ProtoWriter DepthByOrder(string symbol, string exchange, int request) =>
        new ProtoWriter().Int(Rp.TemplateId, Rp.RequestDepthByOrder).String(Rp.UserMsg, "mbo|" + symbol + "|" + exchange)
            .String(Rp.Symbol, symbol).String(Rp.Exchange, exchange).Int(Rp.Request, request);

    private Task AskFrontMonth(RithmicClient c, Sub sub, CancellationToken ct) =>
        SafeSend(c, new ProtoWriter().Int(Rp.TemplateId, Rp.RequestFrontMonth).String(Rp.UserMsg, "fm|" + sub.Instrument.Key)
            .String(Rp.Symbol, sub.Instrument.Symbol).String(Rp.Exchange, sub.Instrument.Exchange).Bool(Rp.NeedUpdates, false), ct);

    private async Task SubscribeData(RithmicClient c, Sub sub, string symbol, string exchange, CancellationToken ct)
    {
        lock (_gate)
        {
            if (sub.Subscribed) return;
            sub.Symbol = symbol;
            sub.Exchange = exchange;
            sub.Subscribed = true;
            sub.SubscribedAt = DateTime.UtcNow;
        }

        foreach (var sink in Sinks(sub)) sink.OnStatus(sub.Instrument, FeedStatus.Connected, $"Rithmic: {symbol} ({_settings().System})");
        await SafeSend(c, MarketData(symbol, exchange, 1, sub.Bits), ct).ConfigureAwait(false);
        // order-level feed on top of the book; without the entitlement Rithmic refuses it and the Level 2 book carries on alone
        if (!sub.NoMbo && !sub.NoBook) await SafeSend(c, DepthByOrder(symbol, exchange, 1), ct).ConfigureAwait(false);
    }

    private IMarketDataSink[] Sinks(Sub sub)
    {
        lock (_gate) return sub.Sinks.ToArray();
    }

    private Sub[] AllSubs()
    {
        lock (_gate) return _subs.Values.ToArray();
    }

    private void MarkReady(Sub sub)
    {
        RithmicContract c;
        lock (_gate)
        {
            if (sub.Ready) return;
            sub.Ready = true;
            c = new RithmicContract(sub.Symbol, sub.Exchange, sub.LastTrade, DateTime.UtcNow);
        }

        _onContract(sub.Instrument, c);
        sub.ReadyTcs.TrySetResult(c);
    }

    private void Broadcast(FeedStatus status, string? message)
    {
        Status = status;
        StatusMessage = message;
        foreach (var sub in AllSubs())
        foreach (var sink in Sinks(sub))
            sink.OnStatus(sub.Instrument, status, message);
    }

    // ------------------------------------------------------------------ connection

    private async Task WaitWake(TimeSpan max, CancellationToken stop)
    {
        Task w;
        lock (_gate) w = _wake.Task;
        await Task.WhenAny(w, Task.Delay(max, stop)).ConfigureAwait(false);
    }

    private void SetConnected(bool ok, string message)
    {
        TaskCompletionSource<(bool, string)> t;
        lock (_gate) t = _connected;
        t.TrySetResult((ok, message));
    }

    private void ResetConnected()
    {
        lock (_gate)
        {
            if (_connected.Task.IsCompleted) _connected = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private async Task RunAsync()
    {
        int failures = 0;
        var stop = _stop.Token;
        while (!stop.IsCancellationRequested)
        {
            var s = _settings();
            if (string.IsNullOrWhiteSpace(s.User))
            {
                Broadcast(FeedStatus.Error, "Set your Rithmic user in Connections");
                SetConnected(false, StatusMessage!);
                await WaitWake(TimeSpan.FromMinutes(5), stop).ConfigureAwait(false);
                ResetConnected();
                continue;
            }

            var conn = CancellationTokenSource.CreateLinkedTokenSource(stop);
            lock (_gate) _conn = conn;
            var ct = conn.Token;
            var client = new RithmicClient();
            try
            {
                Broadcast(FeedStatus.Connecting, $"Connecting to Rithmic ({s.System})…");
                var aggregated = await RithmicClient.AggregatedQuotesFor(s.Uri, s.System, s.AggregatedQuotes, ct).ConfigureAwait(false);
                await client.ConnectAsync(s.Uri, ct, s.TimeoutMs).ConfigureAwait(false);
                var login = await client.LoginAsync(s.User, s.Password, s.System, s.AppName, s.AppVersion, 1, ct, aggregated).ConfigureAwait(false);
                Logins++;
                if (!login.Ok)
                {
                    // user, password or permission: it does not keep retrying in a loop (it is retried when the configuration changes)
                    Broadcast(FeedStatus.Error, login.Message);
                    SetConnected(false, login.Message);
                    await client.DisposeAsync().ConfigureAwait(false);
                    await WaitWake(TimeSpan.FromMinutes(2), stop).ConfigureAwait(false);
                    ResetConnected();
                    continue;
                }

                failures = 0;
                lock (_gate)
                {
                    _client = client;
                    foreach (var sub in _subs.Values)
                    {
                        sub.Subscribed = false;
                        sub.AskedAt = DateTime.UtcNow;
                        sub.SubscribedAt = DateTime.MaxValue;
                        sub.Book = new OrderBookBuilder();
                        sub.LastBid = long.MinValue;
                        sub.LastAsk = long.MaxValue;
                        sub.HasBook = sub.NoBook = false;
                        sub.Orders.Clear();
                    }
                }

                Broadcast(FeedStatus.Connected, $"Rithmic connected ({s.System})");
                SetConnected(true, login.Message);
                foreach (var sub in AllSubs()) await AskFrontMonth(client, sub, ct).ConfigureAwait(false);

                // a single pending read at a time (two at once break the WebSocket and were forcing reconnects)
                Task<ProtoMessage?>? pending = null;
                while (!ct.IsCancellationRequested && client.IsOpen)
                {
                    await TimersAsync(client, ct).ConfigureAwait(false);
                    pending ??= client.ReceiveAsync(ct);
                    var done = await Task.WhenAny(pending, Task.Delay(1000, ct)).ConfigureAwait(false);
                    if (done != pending) continue;
                    var m = await pending.ConfigureAwait(false);
                    pending = null;
                    if (m == null) break;
                    if (!await HandleAsync(client, m, ct).ConfigureAwait(false)) break;
                }

                if (!ct.IsCancellationRequested) failures++;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // reconnect requested (configuration change, "Reconnect now")
            }
            catch (Exception ex)
            {
                failures++;
                Broadcast(FeedStatus.Reconnecting, $"Reconnecting to Rithmic · attempt {failures} ({ex.Message})");
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_client, client)) _client = null;
                    if (ReferenceEquals(_conn, conn)) _conn = null;
                }

                ResetConnected();
                await client.DisposeAsync().ConfigureAwait(false);
                conn.Dispose();
            }

            if (stop.IsCancellationRequested) break;
            if (Status != FeedStatus.Error) Broadcast(FeedStatus.Reconnecting, failures > 0 ? $"Reconnecting to Rithmic · attempt {failures}" : "Reconnecting to Rithmic…");
            await Task.WhenAny(Task.Delay(LiveSubscription.Backoff(failures), stop), WakeTask()).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        Broadcast(FeedStatus.Disconnected, null);
    }

    private Task WakeTask()
    {
        lock (_gate) return _wake.Task;
    }

    /// <summary>Deadlines: with no contract after 3 s it subscribes to the symbol as is; with no snapshot after 4 s it is considered ready.</summary>
    private async Task TimersAsync(RithmicClient c, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var sub in AllSubs())
        {
            if (!sub.Subscribed && sub.AskedAt != DateTime.MaxValue && now - sub.AskedAt > TimeSpan.FromSeconds(3))
                await SubscribeData(c, sub, sub.Instrument.Symbol, sub.Instrument.Exchange, ct).ConfigureAwait(false);
            else if (sub.Subscribed && !sub.Ready && now - sub.SubscribedAt > TimeSpan.FromSeconds(4))
                MarkReady(sub);
        }
    }

    private Sub[] ByContract(string? symbol, string? exchange)
    {
        if (symbol == null) return Array.Empty<Sub>();
        lock (_gate) return _subs.Values.Where(s => s.Subscribed && s.Symbol == symbol && (exchange == null || s.Exchange == exchange)).ToArray();
    }

    /// <summary>Handles a message; returns false if Rithmic closed the session.</summary>
    private async Task<bool> HandleAsync(RithmicClient c, ProtoMessage m, CancellationToken ct)
    {
        string tag = m.Strings(Rp.UserMsg).FirstOrDefault() ?? "";
        switch ((int)m.Int(Rp.TemplateId))
        {
            case Rp.ResponseFrontMonth when tag.StartsWith("fm|", StringComparison.Ordinal):
            {
                Sub? sub;
                lock (_gate) _subs.TryGetValue(tag[3..], out sub);
                if (sub == null || sub.Subscribed) break;
                bool ok = m.Strings(Rp.RpCode).FirstOrDefault() is null or "0";
                string symbol = ok && m.String(Rp.TradingSymbol) is { Length: > 0 } ts ? ts : sub.Instrument.Symbol;
                string exchange = ok && m.String(RpH.TradingExchange) is { Length: > 0 } te ? te : sub.Instrument.Exchange;
                await SubscribeData(c, sub, symbol, exchange, ct).ConfigureAwait(false);
                break;
            }
            case Rp.ResponseMarketDataUpdate:
            {
                var codes = m.Strings(Rp.RpCode).ToList();
                if (codes.Count == 0 || codes[0] == "0" || !tag.StartsWith("md|", StringComparison.Ordinal)) break;
                var parts = tag.Split('|');
                foreach (var sub in ByContract(parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2)))
                {
                    if (!sub.NoBook)
                    {
                        // maybe the account has no Level 2 permission: asks again without the depth (trades and best bid/ask)
                        lock (_gate)
                        {
                            sub.NoBook = true;
                            sub.Subscribed = false;
                        }

                        await SubscribeData(c, sub, sub.Symbol, sub.Exchange, ct).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var sink in Sinks(sub)) sink.OnStatus(sub.Instrument, FeedStatus.Error, $"Rithmic rejected the subscription to {sub.Symbol}: {string.Join(" ", codes)}");
                    MarkReady(sub);
                }

                break;
            }
            case Rp.LastTrade:
            {
                long ssboe = m.Int(Rp.Ssboe), usecs = m.Int(Rp.Usecs);
                var time = ssboe > 0 ? DateTimeOffset.FromUnixTimeSeconds(ssboe).UtcDateTime.AddTicks(usecs * 10) : (DateTime?)null;
                bool snapshot = m.Int(Rp.IsSnapshot) == 1 || !m.Has(Rp.TradePrice);
                decimal size = m.Int(Rp.TradeSize);
                foreach (var sub in ByContract(m.String(Rp.Symbol), m.String(Rp.Exchange)))
                {
                    if (time != null) sub.LastTrade = time;
                    // the first snapshot says when the last trade was (with the market closed, the history ends there)
                    if (!sub.Ready && (time != null || snapshot)) MarkReady(sub);
                    if (snapshot || size <= 0) continue;
                    decimal price = (decimal)m.Double(Rp.TradePrice);
                    var side = m.Int(Rp.Aggressor) switch { 1 => TradeSide.Buy, 2 => TradeSide.Sell, _ => TradeSide.None };
                    long id = ssboe > 0 ? RithmicFeed.TimeId(ssboe, usecs, ref sub.LastId) : Interlocked.Increment(ref _nextId);
                    var trade = new[] { new Trade(id, time ?? DateTime.UtcNow, price, size, side) };
                    foreach (var sink in Sinks(sub)) sink.OnTrades(sub.Instrument, trade);
                }

                break;
            }
            case Rp.BestBidOffer:
            {
                foreach (var sub in ByContract(m.String(Rp.Symbol), m.String(Rp.Exchange)))
                {
                    if (sub.HasBook) continue; // the full book already carries the best levels
                    var inst = sub.Instrument;
                    DepthUpdate update;
                    lock (sub.Book)
                    {
                        // best bid/ask only: the previous level is cleared when the price changes
                        if (m.Has(Rp.BidPrice))
                        {
                            long bid = inst.ToTicks((decimal)m.Double(Rp.BidPrice));
                            if (sub.LastBid != long.MinValue && sub.LastBid != bid) sub.Book.Set(BookSide.Bid, sub.LastBid, 0);
                            sub.Book.Set(BookSide.Bid, bid, m.Int(Rp.BidSize));
                            sub.LastBid = bid;
                        }

                        if (m.Has(Rp.AskPrice))
                        {
                            long ask = inst.ToTicks((decimal)m.Double(Rp.AskPrice));
                            if (sub.LastAsk != long.MaxValue && sub.LastAsk != ask) sub.Book.Set(BookSide.Ask, sub.LastAsk, 0);
                            sub.Book.Set(BookSide.Ask, ask, m.Int(Rp.AskSize));
                            sub.LastAsk = ask;
                        }

                        sub.Book.Uncross(m.Has(Rp.BidPrice) ? BookSide.Bid : BookSide.Ask);
                        update = sub.Book.BuildUpdate(DateTime.UtcNow, 50);
                    }

                    foreach (var sink in Sinks(sub)) sink.OnDepth(inst, update);
                }

                break;
            }
            case Rp.OrderBook:
            {
                int type = (int)m.Int(Rp.BookUpdateType);
                if (type == Rp.BookNone) break; // no Level 2 for this contract: the best bid/ask keep filling the DOM
                var bidPrices = m.Doubles(Rp.BookBidPrice).ToArray();
                var bidSizes = m.Ints(Rp.BookBidSize).ToArray();
                var askPrices = m.Doubles(Rp.BookAskPrice).ToArray();
                var askSizes = m.Ints(Rp.BookAskSize).ToArray();
                foreach (var sub in ByContract(m.String(Rp.Symbol), m.String(Rp.Exchange)))
                {
                    var inst = sub.Instrument;
                    DepthUpdate? update = null;
                    lock (sub.Book)
                    {
                        // the first depth replaces what the best bid/ask had built
                        if (!sub.HasBook || type == Rp.BookClear)
                        {
                            sub.Book.Clear();
                            sub.HasBook = sub.BookFull = true;
                        }

                        for (int i = 0; i < Math.Min(bidPrices.Length, bidSizes.Length); i++) sub.Book.Set(BookSide.Bid, inst.ToTicks((decimal)bidPrices[i]), bidSizes[i]);
                        for (int i = 0; i < Math.Min(askPrices.Length, askSizes.Length); i++) sub.Book.Set(BookSide.Ask, inst.ToTicks((decimal)askPrices[i]), askSizes[i]);

                        // a BEGIN… MIDDLE… END set is shown once it is complete (half-applied it can look crossed)
                        if (type is not (Rp.BookBegin or Rp.BookMiddle))
                        {
                            update = sub.Book.BuildUpdate(DateTime.UtcNow, 50, sub.BookFull);
                            sub.BookFull = false;
                        }
                    }

                    if (update != null)
                        foreach (var sink in Sinks(sub)) sink.OnDepth(inst, update);
                }

                break;
            }
            case Rp.ResponseDepthByOrder:
            {
                var codes = m.Strings(Rp.RpCode).ToList();
                if (codes.Count == 0 || codes[0] == "0" || !tag.StartsWith("mbo|", StringComparison.Ordinal)) break;
                var parts = tag.Split('|');
                foreach (var sub in ByContract(parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2)))
                    lock (sub.Orders)
                    {
                        sub.NoMbo = true; // no order-level entitlement: not an error, the book keeps working
                        sub.Orders.Clear();
                    }

                break;
            }
            case Rp.DepthByOrder:
            {
                var types = m.Ints(Rp.DboUpdateType).ToArray();
                var sides = m.Ints(Rp.DboTransaction).ToArray();
                var prices = m.Doubles(Rp.DboPrice).ToArray();
                var sizes = m.Ints(Rp.DboSize).ToArray();
                var ids = m.Strings(Rp.DboOrderId).ToArray();
                long ssboe = m.Int(Rp.Ssboe), usecs = m.Int(Rp.Usecs);
                var time = ssboe > 0 ? DateTimeOffset.FromUnixTimeSeconds(ssboe).UtcDateTime.AddTicks(usecs * 10) : DateTime.UtcNow;
                int n = new[] { types.Length, sides.Length, prices.Length, sizes.Length, ids.Length }.Min();
                foreach (var sub in ByContract(m.String(Rp.Symbol), m.String(Rp.Exchange)))
                {
                    var inst = sub.Instrument;
                    var events = new List<OrderEvent>(n);
                    lock (sub.Orders)
                        for (int i = 0; i < n; i++)
                        {
                            if (!long.TryParse(ids[i], out long id)) id = ids[i].GetHashCode();
                            bool ask = sides[i] != Rp.DboBuy;
                            long ticks = inst.ToTicks((decimal)prices[i]);
                            if (types[i] == Rp.DboDelete)
                            {
                                // the message carries no size: what the order still had comes from the last one seen
                                if (sub.Orders.Remove(id, out var gone)) events.Add(new OrderEvent(time, OrderEventKind.Remove, id, gone.Ask, gone.Ticks, gone.Size));
                                continue;
                            }

                            bool known = sub.Orders.ContainsKey(id);
                            sub.Orders[id] = (ask, ticks, sizes[i]);
                            events.Add(new OrderEvent(time, known ? OrderEventKind.Modify : OrderEventKind.Add, id, ask, ticks, sizes[i]));
                        }

                    if (events.Count > 0)
                        foreach (var sink in Sinks(sub)) sink.OnOrderEvents(inst, events.ToArray());
                }

                break;
            }
            case 77: // ForcedLogout: another program logged in with the same user
                Broadcast(FeedStatus.Reconnecting, "Rithmic dropped the session: another program signed in with your user (ATAS or R|Trader connected at the same time?). Retrying…");
                return false;
        }

        return true;
    }

    public void Dispose()
    {
        _stop.Cancel();
        Wake();
    }
}

/// <summary>
/// The single history session of a Rithmic connection: the requests (ticks and candles of any contract)
/// go one at a time over the same connection, so there are never two history sessions cutting each other off.
/// </summary>
internal sealed class RithmicHistoryPlant : IDisposable
{
    private readonly Func<RithmicSettings> _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RithmicClient? _client;
    private int _seq;

    public RithmicHistoryPlant(Func<RithmicSettings> settings)
    {
        _settings = settings;
    }

    /// <summary>Times it logged in (for the tests).</summary>
    public int Logins { get; private set; }

    /// <summary>Runs a request with the history session (each one with its own tag so responses do not get mixed).</summary>
    public async Task RunAsync(Func<RithmicClient, string, CancellationToken, Task> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_client is not { IsOpen: true })
            {
                await DropAsync().ConfigureAwait(false);
                var s = _settings();
                if (string.IsNullOrWhiteSpace(s.User)) throw new InvalidOperationException("Set your Rithmic user in Connections");
                var c = new RithmicClient();
                try
                {
                    await c.ConnectAsync(s.Uri, ct, s.TimeoutMs).ConfigureAwait(false);
                    var login = await c.LoginAsync(s.User, s.Password, s.System, s.AppName, s.AppVersion, 3, ct).ConfigureAwait(false);
                    Logins++;
                    if (!login.Ok) throw new InvalidOperationException("Rithmic history: " + login.Message);
                    _client = c;
                }
                catch
                {
                    await c.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            string tag = "h" + Interlocked.Increment(ref _seq);
            try
            {
                await work(_client, tag, ct).ConfigureAwait(false);
            }
            catch
            {
                // mid-request (cancelled or dropped): the session is closed so no leftovers reach the next one
                await DropAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropAsync()
    {
        var c = _client;
        _client = null;
        if (c != null) await c.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Closes the session (next time it logs in with the new configuration).</summary>
    public void Reset()
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DropAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    public void Dispose() => Reset();
}
