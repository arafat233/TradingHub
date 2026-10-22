using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.LondonStrategicEdge;

/// <summary>London Strategic Edge connection: one free API key (https://londonstrategicedge.com/data).</summary>
public sealed class LseSettings
{
    public string ApiKey { get; set; } = "";

    /// <summary>Alternative servers (tests).</summary>
    public string? WsUrl { get; set; }
    public string? ApiUrl { get; set; }
}

/// <summary>
/// Stocks, ETFs, forex, crypto, commodities, indices and futures through London Strategic Edge's free plan: live ticks over a
/// websocket (it can replay the last 24 hours of ticks before going live) and candles over REST from the vault (US stocks back to
/// 2003, FX to 2009, crypto to 2017). Long history comes in as candles (like Binance's), the recent day as real ticks.
/// What the service does not give: the aggressor side of a trade (it comes from the tick rule: the service's "bid" is just the trade price and
/// its "ask" one step above it, so they say nothing about who hit whom), a trade id, the volume of some markets (forex: each tick counts as 1)
/// and the order book.
/// The protocol follows the service's public client (lse-data): auth with {"action":"auth","api_key"}, {"action":"subscribe","symbol","start"?}
/// and ticks as {"type":"tick","symbol","price","bid","ask","volume","ts","replay"}; REST reads send the key in x-api-key.
/// </summary>
public sealed class LseFeed : IMarketDataFeed, IKlineHistory
{
    public const string FeedId = "lse";
    private const string DefaultWs = "wss://data-ws.londonstrategicedge.com";
    private const string DefaultApi = "https://api.londonstrategicedge.com/vault";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly LseSettings _s;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private List<(string Dataset, string Symbol, string Name, DateTime? First, bool Live)>? _catalog;

    // The free plan allows ONE websocket per key at a time ("Max 1 concurrent websocket(s) for this API key"). Every connection this feed opens
    // (the live one of each chart and the history replay) goes through this single slot; the history asks the live ones to step aside first.
    private readonly SemaphoreSlim _slot = new(1, 1);
    private int _historyWaiting;
    private readonly List<LiveSubscription> _lives = new();

    public LseFeed(LseSettings settings)
    {
        _s = settings;
        _http.DefaultRequestHeaders.Add("x-api-key", settings.ApiKey.Trim());
        _http.DefaultRequestHeaders.Add("User-Agent", "trading-hub (+https://londonstrategicedge.com)");
    }

    public string Id => FeedId;
    public string DisplayName => "London Strategic Edge";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public string HistoryNote => "the last 24 h as ticks and older history as candles, free, from London Strategic Edge";
    public string CacheTag => "lse";

    private string Ws => (_s.WsUrl ?? DefaultWs).TrimEnd('/');
    private string Api => (_s.ApiUrl ?? DefaultApi).TrimEnd('/');

    // ===================================================================== REST

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.ApiKey)) throw new InvalidOperationException("The London Strategic Edge API key is missing (it is free at londonstrategicedge.com/data).");
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage resp;
            string body;
            try
            {
                resp = await _http.GetAsync(Api + path, ct).ConfigureAwait(false);
                body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // the catalog is several MB: a dropped connection or a slow answer is worth another try
                await Task.Delay(TimeSpan.FromSeconds(2 + attempt * 3), ct).ConfigureAwait(false);
                continue;
            }

            using var _ = resp;
            if ((int)resp.StatusCode == 429 && attempt < 5)
            {
                await Task.Delay(TimeSpan.FromSeconds(4 + attempt * 6), ct).ConfigureAwait(false);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                string msg = body.TrimStart().StartsWith('<') ? resp.ReasonPhrase ?? "" : body;
                try
                {
                    using var err = JsonDocument.Parse(body);
                    foreach (var k in new[] { "detail", "message", "error" })
                        if (err.RootElement.ValueKind == JsonValueKind.Object && err.RootElement.TryGetProperty(k, out var m) && m.ValueKind == JsonValueKind.String) { msg = m.GetString() ?? body; break; }
                }
                catch (JsonException)
                {
                }

                if (msg.Length > 300) msg = msg[..300];
                throw new HttpRequestException(resp.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "London Strategic Edge rejected the API key (or your plan does not include that data): " + msg,
                    System.Net.HttpStatusCode.TooManyRequests => "London Strategic Edge says the hourly allowance is used up: " + msg,
                    _ => $"London Strategic Edge replied {(int)resp.StatusCode}: {msg}",
                });
            }

            return JsonDocument.Parse(body);
        }
    }

    private static decimal Dec(JsonElement e, string name, decimal fallback = 0)
    {
        if (!e.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDecimal(),
            JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Float, Inv, out var d) => d,
            _ => fallback,
        };
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>The server sends ISO times, or "yyyy-MM-dd HH:mm:ss" in the vault's rows: both are UTC.</summary>
    private static DateTime Time(string s) => DateTime.Parse(s, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    /// <summary>A tick's time: the live ones carry an ISO string, the replayed ones Unix seconds with decimals (both seen on the real service).</summary>
    private static DateTime TimeOf(JsonElement m)
    {
        if (m.TryGetProperty("ts", out var v))
        {
            if (v.ValueKind == JsonValueKind.Number)
            {
                double d = v.GetDouble();
                if (d > 1e11) d /= 1000; // milliseconds
                return DateTime.UnixEpoch.AddTicks((long)(d * TimeSpan.TicksPerSecond));
            }

            if (v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } str) return Time(str);
        }

        return DateTime.UtcNow;
    }

    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss", Inv);

    // ===================================================================== instruments

    /// <summary>The tradable classes (the vault also holds economic series, bond yields and option contracts).</summary>
    private static bool Tradable(string dataset)
    {
        var d = dataset.ToLowerInvariant();
        return d.Length > 0 && !d.Contains("econom") && !d.Contains("bond") && !d.Contains("option") && !d.Contains("rate") && !d.Contains("yield") && !d.Contains("macro") && !d.Contains("credit") && !d.Contains("deriv");
    }

    /// <summary>
    /// The catalog has no tick size, so it is a sensible one for the class (forex 5 decimals, JPY pairs 3, crypto 4, the rest 2) and the
    /// real one for the futures it carries that we know. Futures prices arrive with float noise (7729.999999999999): the tick size rounds it away.
    /// </summary>
    public static (decimal Tick, decimal PointValue, decimal VolumeStep) SpecOf(string dataset, string symbol)
    {
        var d = dataset.ToLowerInvariant();
        switch (symbol)
        {
            case "ES.F": return (0.25m, 50m, 1m);
            case "NQ.F": return (0.25m, 20m, 1m);
            case "GC.F": return (0.1m, 100m, 1m);
            case "SI.F": return (0.005m, 5000m, 1m);
            case "FDAX": case "FDAX.F": return (0.5m, 25m, 1m);
            case "FESX": case "FESX.F": return (1m, 10m, 1m);
            case "FGBL": case "FGBL.F": case "FGBM": case "FGBM.F": return (0.01m, 1000m, 1m);
        }

        if (d.Contains("forex") || d == "fx") return (symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase) ? 0.001m : 0.00001m, 1m, 1m);
        if (d.Contains("crypto")) return (0.0001m, 1m, 0.00000001m);
        return (0.01m, 1m, 1m);
    }

    public static decimal TickSizeOf(string dataset, string symbol) => SpecOf(dataset, symbol).Tick;

    private async Task<List<(string Dataset, string Symbol, string Name, DateTime? First, bool Live)>> CatalogAsync(CancellationToken ct)
    {
        if (_catalog != null) return _catalog;
        using var doc = await GetAsync("/catalog", ct).ConfigureAwait(false);
        var list = new List<(string, string, string, DateTime?, bool)>();
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            string symbol = Str(r, "symbol"), dataset = Str(r, "dataset");
            if (symbol.Length == 0 || !Tradable(dataset)) continue;
            string first = Str(r, "first_tick");
            // "live": 1 = the service streams it, 0 = history only
            bool live = !(r.TryGetProperty("live", out var lv) && lv.ValueKind == JsonValueKind.Number && lv.GetInt32() == 0);
            list.Add((dataset, symbol, Str(r, "name"), first.Length > 0 && DateTime.TryParse(first, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var f) ? f : null, live));
        }

        return _catalog = list;
    }

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var cat = await CatalogAsync(ct).ConfigureAwait(false);
        string[] popular = { "ES.F", "NQ.F", "BTC/USD", "ETH/USD", "EUR/USD", "NAS100/USD", "SPY", "QQQ", "AAPL", "NVDA", "TSLA" };
        return cat.Select(c =>
        {
            var spec = SpecOf(c.Dataset, c.Symbol);
            return new Instrument
            {
                Symbol = c.Symbol,
                FeedId = FeedId,
                Exchange = c.Dataset,
                TickSize = spec.Tick,
                VolumeStep = spec.VolumeStep,
                PointValue = spec.PointValue,
                QuoteAsset = c.Symbol.Contains('/') ? c.Symbol[(c.Symbol.IndexOf('/') + 1)..] : "USD",
                Description = (c.Name.Length > 0 ? c.Name : c.Symbol) + (c.Live ? "" : " (history only)"),
            };
        }).OrderBy(i => Array.IndexOf(popular, i.Symbol) is var k && k >= 0 ? k : 999).ThenBy(i => i.Exchange).ThenBy(i => i.Symbol).ToList();
    }

    // ===================================================================== websocket

    private static Task SendAsync(ClientWebSocket ws, object message, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, ct);

    /// <summary>One text message (null if the server closed).</summary>
    private static async Task<string?> ReceiveAsync(ClientWebSocket ws, MemoryStream ms, byte[] buf, CancellationToken ct)
    {
        ms.SetLength(0);
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>The server greets with {"type":"welcome"}; the client authenticates after it (as the official client does).</summary>
    private async Task AuthenticateAsync(ClientWebSocket ws, MemoryStream ms, byte[] buf, CancellationToken ct)
    {
        var hello = await ReceiveAsync(ws, ms, buf, ct).ConfigureAwait(false) ?? throw new InvalidOperationException("London Strategic Edge closed the connection");
        using (var doc = JsonDocument.Parse(hello))
            if (Str(doc.RootElement, "type") == "error") throw new InvalidOperationException("London Strategic Edge: " + Str(doc.RootElement, "message"));
        await SendAsync(ws, new { action = "auth", api_key = _s.ApiKey.Trim() }, ct).ConfigureAwait(false);
    }

    private static bool Fatal(string code) => code is "INVALID_KEY" or "MISSING_KEY" or "QUOTA_EXCEEDED";

    private static bool IsConcurrencyError(string message) => message.Contains("concurrent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Last 24 hours of ticks. The one websocket the free plan allows is shared: the live subscriptions drop their connection (they reconnect
    /// on their own when this is done, and the replay covers what they missed), the replay uses the slot, and a "concurrent websocket" refusal,
    /// which can come for a moment after another connection closed, is retried.
    /// </summary>
    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref _historyWaiting);
        try
        {
            bool dropped;
            lock (_lives)
            {
                dropped = _lives.Count > 0;
                foreach (var l in _lives.ToList()) l.Reconnect();
            }

            await _slot.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // the server frees the slot when it notices the dropped connection, which can take a moment (a refusal is retried below anyway)
                if (dropped) await Task.Delay(400, ct).ConfigureAwait(false);
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await ReplayAsync(instrument, fromUtc, toUtc, onBatch, progress, ct).ConfigureAwait(false);
                        return;
                    }
                    catch (InvalidOperationException ex) when (attempt < 4 && IsConcurrencyError(ex.Message))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                _slot.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _historyWaiting);
        }
    }

    private async Task ReplayAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        var floor = DateTime.UtcNow.AddHours(-24);
        if (fromUtc < floor) fromUtc = floor;
        if (toUtc > DateTime.UtcNow) toUtc = DateTime.UtcNow;
        if (toUtc <= fromUtc) return;
        if (string.IsNullOrWhiteSpace(_s.ApiKey)) throw new InvalidOperationException("The London Strategic Edge API key is missing.");
        using var ws = new ClientWebSocket();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(30));
        await ws.ConnectAsync(new Uri(Ws), idle.Token).ConfigureAwait(false);
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        long lastId = 0;
        decimal last = 0;
        var side = TradeSide.Buy;
        var batch = new List<Trade>();
        double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        bool stop = false;
        await AuthenticateAsync(ws, ms, buf, idle.Token).ConfigureAwait(false);
        while (!stop && ws.State == WebSocketState.Open)
        {
            var text = await ReceiveAsync(ws, ms, buf, idle.Token).ConfigureAwait(false);
            if (text == null) break;
            idle.CancelAfter(TimeSpan.FromSeconds(30));
            using var doc = JsonDocument.Parse(text);
            var m = doc.RootElement;
            switch (Str(m, "type"))
            {
                case "authenticated":
                    await SendAsync(ws, new { action = "subscribe", symbol = instrument.Symbol, start = Iso(fromUtc) }, idle.Token).ConfigureAwait(false);
                    break;
                case "error":
                    throw new InvalidOperationException("London Strategic Edge: " + (Str(m, "message").Length > 0 ? Str(m, "message") : Str(m, "code")));
                case "replay_complete":
                    stop = true;
                    break;
                case "tick":
                {
                    // a tick that is not a replay means the history is over and the live stream started
                    if (!(m.TryGetProperty("replay", out var rp) && rp.ValueKind == JsonValueKind.True)) { stop = true; break; }
                    var time = TimeOf(m); // no symbol check: it is the only subscription, and the server may use an alias (ES.F ticks say SP500.F)
                    if (time > toUtc) { stop = true; break; }
                    if (time < fromUtc) break;
                    decimal price = instrument.RoundPrice(Dec(m, "price")), vol = Dec(m, "volume");
                    if (price <= 0) break;
                    batch.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, price, vol > 0 ? vol : 1, HistoryBuilder.TickRule(price, ref last, ref side)));
                    if (batch.Count >= 5000)
                    {
                        if (!onBatch(batch)) return;
                        progress?.Report(Math.Min(1, (batch[^1].Time - fromUtc).TotalSeconds / total));
                        batch = new List<Trade>();
                    }

                    break;
                }
            }
        }

        if (batch.Count > 0 && !onBatch(batch)) return;
        progress?.Report(1);
        try
        {
            // a full close (not just our half): the server only frees the one connection the plan allows when it has seen it end
            if (ws.State == WebSocketState.Open)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closing.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("London Strategic Edge", (st, msg) => sink.OnStatus(instrument, st, msg));
        lock (_lives) _lives.Add(sub);
        _ = Task.Run(() => RunAsync(instrument, sink, sub));
        return new LseSubscription(this, sub);
    }

    private sealed class LseSubscription(LseFeed feed, LiveSubscription inner) : IDisposable, IReconnectable
    {
        public void Reconnect() => inner.Reconnect();

        public void Dispose()
        {
            lock (feed._lives) feed._lives.Remove(inner);
            inner.Dispose();
        }
    }

    /// <summary>Waits for the one websocket slot (a history replay that is waiting goes first).</summary>
    private async Task<bool> AcquireSlotAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                while (Volatile.Read(ref _historyWaiting) > 0) await Task.Delay(150, ct).ConfigureAwait(false);
                await _slot.WaitAsync(ct).ConfigureAwait(false);
                if (Volatile.Read(ref _historyWaiting) == 0) return true;
                _slot.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription live)
    {
        var ct = live.Token;
        int failures = 0, requested = 0;
        bool ever = false;
        long lastId = 0;
        decimal last = 0;
        var lastSide = TradeSide.Buy;
        while (!ct.IsCancellationRequested)
        {
            if (!await AcquireSlotAsync(ct).ConfigureAwait(false)) break;
            using var ws = new ClientWebSocket();
            var conn = live.BeginConnection();
            string reason = "the connection dropped";
            bool any = false;
            try
            {
                if (!ever) live.SetChannel("data", FeedStatus.Connecting, "Connecting to London Strategic Edge…");
                using (var c = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                {
                    c.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(new Uri(Ws), c.Token).ConfigureAwait(false);
                }

                var buf = new byte[1 << 16];
                var ms = new MemoryStream();
                await AuthenticateAsync(ws, ms, buf, conn.Token).ConfigureAwait(false);
                // keepalive: the server drops idle connections (a failure here is the main loop's to notice)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!conn.IsCancellationRequested && ws.State == WebSocketState.Open)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), conn.Token).ConfigureAwait(false);
                            await SendAsync(ws, new { action = "ping" }, conn.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception)
                    {
                    }
                });
                while (ws.State == WebSocketState.Open && !conn.IsCancellationRequested)
                {
                    var text = await ReceiveAsync(ws, ms, buf, conn.Token).ConfigureAwait(false);
                    if (text == null)
                    {
                        reason = "London Strategic Edge closed the connection";
                        break;
                    }

                    using var doc = JsonDocument.Parse(text);
                    var m = doc.RootElement;
                    switch (Str(m, "type"))
                    {
                        case "authenticated":
                            await SendAsync(ws, new { action = "subscribe", symbol = instrument.Symbol }, conn.Token).ConfigureAwait(false);
                            live.SetChannel("data", FeedStatus.Connected, null);
                            ever = true;
                            failures = 0;
                            break;
                        case "error":
                        {
                            string msg = Str(m, "message").Length > 0 ? Str(m, "message") : Str(m, "code");
                            bool concurrent = IsConcurrencyError(msg);
                            if (!concurrent && (Fatal(Str(m, "code")) || !ever))
                            {
                                // a bad key or an used-up allowance never gets better by retrying
                                live.SetChannel("data", FeedStatus.Error, "London Strategic Edge: " + msg);
                                return;
                            }

                            // another program or chart holds the one websocket the free plan allows: it is retried until it is free
                            throw new InvalidOperationException(concurrent ? "the free plan allows only 1 websocket per key at a time: close whatever else is using the key. " + msg : msg);
                        }

                        case "tick":
                        {
                            any = true;
                            decimal price = instrument.RoundPrice(Dec(m, "price")), vol = Dec(m, "volume");
                            if (price <= 0) break;
                            var time = TimeOf(m);
                            sink.OnTrades(instrument, new[] { new Trade(HistoryBuilder.TimeId(time, ref lastId), time, price, vol > 0 ? vol : 1, HistoryBuilder.TickRule(price, ref last, ref lastSide)) });
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
                _slot.Release();
            }

            if (ct.IsCancellationRequested) break;
            var wait = LiveSubscription.Backoff(failures);
            live.SetChannel("data", FeedStatus.Reconnecting, $"Reconnecting in {wait.TotalSeconds:0} s ({reason})");
            await Task.Delay(wait, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        live.SetChannel("data", FeedStatus.Disconnected, null);
    }

    // ===================================================================== candles (long history)

    public async Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct)
    {
        var cat = await CatalogAsync(ct).ConfigureAwait(false);
        return cat.FirstOrDefault(c => c.Symbol == instrument.Symbol && (instrument.Exchange.Length == 0 || c.Dataset == instrument.Exchange)).First;
    }

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (toUtc <= fromUtc) return;
        int m = minutes >= 15 ? 15 : minutes >= 5 ? 5 : 1;
        var start = fromUtc;
        double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        while (start < toUtc && !ct.IsCancellationRequested)
        {
            var path = $"/candles?symbol={Uri.EscapeDataString(instrument.Symbol)}&timeframe={m}m&order=asc&limit=5000&start={Uri.EscapeDataString(Iso(start))}&end={Uri.EscapeDataString(Iso(toUtc))}"
                       + (instrument.Exchange.Length > 0 ? "&dataset=" + Uri.EscapeDataString(instrument.Exchange) : "");
            using var doc = await GetAsync(path, ct).ConfigureAwait(false);
            var batch = new List<Kline>();
            DateTime lastTime = start;
            int rows = 0;
            foreach (var b in doc.RootElement.EnumerateArray())
            {
                rows++;
                string ts = Str(b, "ts").Length > 0 ? Str(b, "ts") : Str(b, "timestamp");
                if (ts.Length == 0) continue;
                var time = Time(ts);
                lastTime = time;
                decimal o = Dec(b, "open"), h = Dec(b, "high"), l = Dec(b, "low"), c = Dec(b, "close");
                if (c <= 0) continue;
                batch.Add(new Kline(new DateTimeOffset(time).ToUnixTimeMilliseconds(), o, h, l, c, Math.Max(1, Dec(b, "volume")), 0, false));
            }

            if (batch.Count > 0)
            {
                if (!onBatch(batch)) return;
                progress?.Report(Math.Min(1, (lastTime - fromUtc).TotalSeconds / total));
            }

            if (rows < 5000 || lastTime <= start) break; // the last page, or no progress (it would loop forever)
            start = lastTime.AddSeconds(1);
        }

        progress?.Report(1);
    }

    /// <summary>Tests the key against the allowance endpoint (it uses none of it up) and says what is left of the plan in plain words.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("/usage", ct).ConfigureAwait(false);
        var u = doc.RootElement;
        if (u.ValueKind != JsonValueKind.Object) return (true, "API key OK.");
        static string Bytes(decimal b) => b >= 1e9m ? $"{b / 1e9m:0.#} GB" : b >= 1e6m ? $"{b / 1e6m:0.#} MB" : $"{b / 1e3m:0.#} KB";
        var parts = new List<string>();
        if (u.TryGetProperty("bytes_used_month", out _)) parts.Add($"{Bytes(Dec(u, "bytes_used_month"))} of {Bytes(Dec(u, "bytes_cap_month"))} used this month");
        if (u.TryGetProperty("exports_cap_hour", out _)) parts.Add($"{Dec(u, "exports_this_hour"):0} of {Dec(u, "exports_cap_hour"):0} downloads this hour");
        if (u.TryGetProperty("calls_per_minute", out _)) parts.Add($"{Dec(u, "calls_per_minute"):0} calls a minute, {Dec(u, "max_rows_per_request"):0} rows a call");
        return (true, "API key OK. " + (parts.Count > 0 ? string.Join(" · ", parts) + "." : ""));
    }

    public void Dispose() => _http.Dispose();
}
