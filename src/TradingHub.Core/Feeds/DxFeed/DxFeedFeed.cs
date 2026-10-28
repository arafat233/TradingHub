using System.Net.WebSockets;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.DxFeed;

/// <summary>dxFeed connection: the dxLink address and token of your dxFeed plan.</summary>
public sealed class DxFeedSettings
{
    /// <summary>Public demo server (delayed data, no token). The address in the dxLink specification (/dxlink-ws) now answers 400.</summary>
    public const string DemoUrl = "wss://demo.dxfeed.com/market-data/dxlink-ws";

    public string Url { get; set; } = DemoUrl;
    public string Token { get; set; } = "";

    /// <summary>
    /// Order source of the DOM: glbx = CME Globex by price level (the server rejects "GLBX" in upper case; ntv is Nasdaq
    /// TotalView). Your dxFeed plan says which ones it includes.
    /// </summary>
    public string DomSource { get; set; } = "glbx";

    /// <summary>Roots (front month worked out on its own) or exact dxFeed symbols such as /CLZ26:XNYM.</summary>
    public List<string> Products { get; set; } = new() { "ES", "NQ", "MES", "MNQ", "YM", "MYM", "RTY", "M2K", "GC", "MGC" };
}

/// <summary>
/// CME futures from dxFeed over dxLink (WebSocket + JSON): every trade (TimeAndSale, with its aggressor side) and the
/// full order book (DOM service, Level 2). Live only: there is no history download, the chart fills from the moment it connects.
/// </summary>
public sealed class DxFeedFeed : IMarketDataFeed
{
    public const string FeedId = "dxfeed";
    private const int FeedChannel = 1, DomChannel = 3;
    private const string Codes = "FGHJKMNQUVXZ";
    private static readonly string[] TradeFields = { "eventType", "eventSymbol", "time", "price", "size", "aggressorSide", "eventFlags", "spreadLeg", "validTick" };
    private readonly DxFeedSettings _s;

    public DxFeedFeed(DxFeedSettings settings) => _s = settings;

    public string Id => FeedId;
    public string DisplayName => "dxFeed";
    public bool SupportsHistory => false;
    public bool IsLive => true;
    private bool IsDemo => _s.Url.TrimEnd('/') == DxFeedSettings.DemoUrl;

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = new List<Instrument>();
        foreach (var entry in _s.Products.Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (FuturesCatalog.Find(entry.StartsWith('/') ? RootOf(entry) : entry) is not { } p || list.Any(i => i.Symbol == p.Root)) continue;
            list.Add(new Instrument
            {
                Symbol = p.Root, FeedId = FeedId, Exchange = p.Exchange, TickSize = p.Tick, VolumeStep = 1, PointValue = p.PointValue,
                Description = DxSymbol(p.Root, p.Exchange, DateTime.UtcNow) is { } dx ? $"{p.Name} · {dx}" : p.Name,
            });
        }

        return Task.FromResult<IReadOnlyList<Instrument>>(list);
    }

    // ------------------------------------------------------------------ symbols

    /// <summary>/GCZ26:XCEC -> GC.</summary>
    public static string RootOf(string dxSymbol)
    {
        var s = dxSymbol.TrimStart('/').Split(':')[0].TrimEnd("0123456789".ToCharArray());
        return s.Length > 1 && Codes.Contains(s[^1]) ? s[..^1] : s;
    }

    private static string Mic(string exchange) => FuturesContracts.Mic(exchange);

    /// <summary>
    /// The dxFeed symbol to use: an exact one from Products, or the front month (see <see cref="FuturesContracts.FrontMonth"/>).
    /// </summary>
    public string? DxSymbol(string root, string exchange, DateTime utc)
    {
        var exact = _s.Products.FirstOrDefault(x => x.Trim().StartsWith('/') && RootOf(x.Trim()).Equals(root, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Trim();
        if (FuturesContracts.FrontMonth(root, utc) is not var (year, month)) return null;
        return $"/{root}{Codes[month - 1]}{year % 100:00}:{Mic(exchange)}";
    }

    // ------------------------------------------------------------------ live

    /// <summary>No history (SupportsHistory is false): the chart starts with what arrives live.</summary>
    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("dxFeed", (st, msg) => sink.OnStatus(instrument, st, msg));
        _ = Task.Run(() => RunAsync(instrument, sink, sub));
        return sub;
    }

    private async Task RunAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription live)
    {
        var ct = live.Token;
        int failures = 0, requested = 0;
        long lastId = 0;
        while (!ct.IsCancellationRequested)
        {
            string? symbol = DxSymbol(instrument.Symbol, instrument.Exchange, DateTime.UtcNow);
            if (symbol == null)
            {
                live.SetChannel("data", FeedStatus.Error, $"Type the exact dxFeed symbol of {instrument.Symbol} in Products (for example /{instrument.Symbol}Z26:{Mic(instrument.Exchange)})");
                return;
            }

            using var ws = new ClientWebSocket();
            var conn = live.BeginConnection();
            var sendLock = new SemaphoreSlim(1, 1);
            string reason = "the connection dropped";
            bool any = false;
            try
            {
                live.SetChannel("data", FeedStatus.Connecting, $"Connecting to dxFeed ({symbol})…");
                using (var c = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                {
                    c.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(new Uri(_s.Url), c.Token).ConfigureAwait(false);
                }

                async Task Send(object msg)
                {
                    await sendLock.WaitAsync(conn.Token).ConfigureAwait(false);
                    try
                    {
                        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(msg), WebSocketMessageType.Text, true, conn.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        sendLock.Release();
                    }
                }

                await Send(new { type = "SETUP", channel = 0, keepaliveTimeout = 60, acceptKeepaliveTimeout = 60, version = "1.0-TradingHub/1.0" }).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(_s.Token)) await Send(new { type = "AUTH", channel = 0, token = _s.Token.Trim() }).ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    // the server closes the connection if nothing arrives within its keepalive (60 s)
                    while (!conn.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), conn.Token).ConfigureAwait(false);
                        await Send(new { type = "KEEPALIVE", channel = 0 }).ConfigureAwait(false);
                    }
                }).ContinueWith(_ => { });

                var buf = new byte[1 << 16];
                var ms = new MemoryStream();
                var book = new OrderBookBuilder();
                int unauthorized = 0;
                bool channels = false;
                while (ws.State == WebSocketState.Open && !conn.IsCancellationRequested)
                {
                    conn.CancelAfter(TimeSpan.FromSeconds(90));
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buf, conn.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        reason = "dxFeed closed the connection";
                        break;
                    }

                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var m = doc.RootElement;
                    switch (m.TryGetProperty("type", out var t) ? t.GetString() : null)
                    {
                        case "ERROR" when Int(m, "channel") == DomChannel:
                            // no Level 2 (source not in the plan…): the trades keep coming, the reason is kept for the connection test
                            DomError = Str(m, "message") ?? Str(m, "error");
                            break;
                        case "ERROR":
                            throw new InvalidOperationException($"dxFeed: {Str(m, "error")} {Str(m, "message")}".Trim());
                        case "AUTH_STATE" when Str(m, "state") == "UNAUTHORIZED":
                            // the first one only says a token is needed; another one after sending it means it was rejected
                            if (string.IsNullOrWhiteSpace(_s.Token)) throw new InvalidOperationException("This dxFeed server needs a token (Connections → dxFeed)");
                            if (++unauthorized > 1) throw new InvalidOperationException("dxFeed rejected the token");
                            break;
                        case "AUTH_STATE" when Str(m, "state") == "AUTHORIZED" && !channels:
                            channels = true;
                            failures = 0;
                            live.SetChannel("data", FeedStatus.Connected, null);
                            await Send(new { type = "CHANNEL_REQUEST", channel = FeedChannel, service = "FEED", parameters = new { contract = "AUTO" } }).ConfigureAwait(false);
                            await Send(new { type = "CHANNEL_REQUEST", channel = DomChannel, service = "DOM", parameters = new { symbol, sources = new[] { _s.DomSource.Trim() } } }).ConfigureAwait(false);
                            break;
                        case "CHANNEL_OPENED" when Int(m, "channel") == FeedChannel:
                            await Send(new { type = "FEED_SETUP", channel = FeedChannel, acceptAggregationPeriod = 0.1, acceptDataFormat = "FULL", acceptEventFields = new { TimeAndSale = TradeFields } }).ConfigureAwait(false);
                            await Send(new { type = "FEED_SUBSCRIPTION", channel = FeedChannel, add = new[] { new { symbol, type = "TimeAndSale" } } }).ConfigureAwait(false);
                            break;
                        case "CHANNEL_OPENED" when Int(m, "channel") == DomChannel:
                            await Send(new { type = "DOM_SETUP", channel = DomChannel, acceptAggregationPeriod = 0.1, acceptDepthLimit = 50, acceptDataFormat = "FULL", acceptOrderFields = new[] { "price", "size" } }).ConfigureAwait(false);
                            break;
                        case "FEED_DATA":
                        {
                            any = true;
                            var trades = new List<Trade>();
                            // ponytail: FULL format only (the one requested); a server that forces COMPACT sends arrays that are skipped here
                            foreach (var e in m.GetProperty("data").EnumerateArray())
                            {
                                if (e.ValueKind != JsonValueKind.Object || Str(e, "eventType") != "TimeAndSale") continue;
                                if ((Int(e, "eventFlags") & 0x2) != 0 || Bool(e, "spreadLeg") == true || Bool(e, "validTick") == false) continue; // removed, spread leg or not a real tick
                                if (Num(e, "price") is not { } price || Num(e, "size") is not { } size || size <= 0 || Num(e, "time") is not { } ms0) continue;
                                var time = DateTime.UnixEpoch.AddMilliseconds(ms0);
                                var side = Str(e, "aggressorSide") switch { "BUY" => TradeSide.Buy, "SELL" => TradeSide.Sell, _ => TradeSide.None };
                                trades.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, instrument.RoundPrice((decimal)price), (decimal)size, side));
                            }

                            if (trades.Count > 0) sink.OnTrades(instrument, trades);
                            break;
                        }
                        case "DOM_SNAPSHOT":
                        {
                            any = true;
                            // each snapshot is the whole book (the levels that are gone are simply not in it)
                            book.Clear();
                            foreach (var (side, name) in new[] { (BookSide.Bid, "bids"), (BookSide.Ask, "asks") })
                                if (m.TryGetProperty(name, out var levels) && levels.ValueKind == JsonValueKind.Array)
                                    foreach (var l in levels.EnumerateArray())
                                        if (Num(l, "price") is { } p && Num(l, "size") is { } s && s > 0)
                                            book.Set(side, instrument.ToTicks((decimal)p), (decimal)s);
                            var time = Num(m, "time") is { } tm ? DateTime.UnixEpoch.AddMilliseconds(tm) : DateTime.UtcNow;
                            sink.OnDepth(instrument, book.BuildUpdate(time, 50, true));
                            break;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = live.WasRequested(ref requested) ? "reconnection requested" : "it did not respond in time";
                if (!any) failures++;
            }
            catch (Exception ex)
            {
                failures++;
                reason = ex.Message;
            }
            finally
            {
                live.EndConnection(conn);
            }

            if (ct.IsCancellationRequested) break;
            var wait = LiveSubscription.Backoff(failures);
            live.SetChannel("data", FeedStatus.Reconnecting, $"Reconnecting in {wait.TotalSeconds:0} s ({reason})");
            await Task.Delay(wait, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        live.SetChannel("data", FeedStatus.Disconnected, null);
    }

    /// <summary>Why the last DOM request was refused (null = no refusal).</summary>
    public string? DomError { get; private set; }

    /// <summary>Connects with the first product, waits for dxFeed to accept the session and says whether Level 2 arrives.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        var instruments = await GetInstrumentsAsync(ct).ConfigureAwait(false);
        if (instruments.Count == 0) return (false, "Products has no CME future Trading Hub knows (for example NQ, ES, GC).");
        var inst = instruments[0];
        DomError = null;
        var connected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var depth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new StatusSink((st, msg) =>
        {
            if (st == FeedStatus.Connected) connected.TrySetResult(null);
            else if (st is FeedStatus.Error or FeedStatus.Reconnecting) connected.TrySetResult(msg ?? "dxFeed did not accept the connection");
        }, () => depth.TrySetResult());
        using var sub = Subscribe(inst, sink);
        try
        {
            if (await connected.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false) is { } error) return (false, error);
        }
        catch (TimeoutException)
        {
            return (false, "dxFeed did not answer in 20 s");
        }

        string head = $"Connected to dxFeed{(IsDemo ? " demo server (delayed data)" : "")}: {DxSymbol(inst.Symbol, inst.Exchange, DateTime.UtcNow)}.";
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!depth.Task.IsCompleted && DomError == null && DateTime.UtcNow < end) await Task.Delay(100, ct).ConfigureAwait(false);
        return (true, head + (depth.Task.IsCompleted ? $" Level 2 ({_s.DomSource}) arriving."
            : DomError != null ? $" Level 2 not available: {DomError}."
            : $" No Level 2 yet from {_s.DomSource} (market closed, or depth not included in your plan)."));
    }

    private sealed class StatusSink(Action<FeedStatus, string?> onStatus, Action onDepth) : IMarketDataSink
    {
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
        }

        public void OnDepth(Instrument instrument, DepthUpdate update) => onDepth();

        public void OnStatus(Instrument instrument, FeedStatus status, string? message) => onStatus(status, message);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
    private static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>A dxLink number: "NaN" or "Infinity" come as text and count as missing.</summary>
    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? v.GetDouble() : null;

    public void Dispose()
    {
    }
}
