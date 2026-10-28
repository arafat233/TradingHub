using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Ironbeam;

/// <summary>Ironbeam connection: your login (user, password and the API key of your account) and which server it talks to.</summary>
public sealed class IronbeamSettings
{
    public const string LiveUrl = "https://live.ironbeamapi.com/v2";
    public const string DemoUrl = "https://demo.ironbeamapi.com/v2";

    /// <summary>The demo server (simulated money, same data) or the live one.</summary>
    public bool Demo { get; set; } = true;

    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string ApiKey { get; set; } = "";

    /// <summary>Another gateway (the tests point it at a fake server). Empty = the demo or live address.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Roots (front month worked out on its own) or exact Ironbeam symbols such as XNYM:CL.Z26.</summary>
    public List<string> Products { get; set; } = new() { "ES", "NQ", "MES", "MNQ", "YM", "MYM", "RTY", "M2K", "GC", "MGC" };

    public string Base => (string.IsNullOrWhiteSpace(BaseUrl) ? Demo ? DemoUrl : LiveUrl : BaseUrl).TrimEnd('/');
}

/// <summary>
/// CME futures from Ironbeam's API (REST + WebSocket, JSON): every trade with its aggressor side and the order book by price level (Level 2).
/// You sign in with user, password and API key, ask for a stream, open its WebSocket and subscribe to the symbol. Live only: its trade history
/// call returns at most 100 trades, so there is no history download and the chart fills from the moment it connects.
/// The field names come from Ironbeam's published API and a client library; the depth semantics (whole book or only the levels that changed)
/// are not documented, so each message is applied level by level.
/// </summary>
public sealed class IronbeamFeed : IMarketDataFeed
{
    public const string FeedId = "ironbeam";
    private readonly IronbeamSettings _s;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public IronbeamFeed(IronbeamSettings settings) => _s = settings;

    public string Id => FeedId;
    public string DisplayName => "Ironbeam";
    public bool SupportsHistory => false;
    public bool IsLive => true;

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = new List<Instrument>();
        foreach (var entry in _s.Products.Select(x => x.Trim()).Where(x => x.Length > 0))
        {
            if (FuturesCatalog.Find(entry.Contains(':') ? RootOf(entry) : entry) is not { } p || list.Any(i => i.Symbol == p.Root)) continue;
            list.Add(new Instrument
            {
                Symbol = p.Root, FeedId = FeedId, Exchange = p.Exchange, TickSize = p.Tick, VolumeStep = 1, PointValue = p.PointValue,
                Description = IronSymbol(p.Root, p.Exchange, DateTime.UtcNow) is { } sym ? $"{p.Name} · {sym}" : p.Name,
            });
        }

        return Task.FromResult<IReadOnlyList<Instrument>>(list);
    }

    // ------------------------------------------------------------------ symbols

    /// <summary>XCME:ES.U26 -> ES.</summary>
    public static string RootOf(string ironSymbol)
    {
        var s = ironSymbol.Contains(':') ? ironSymbol[(ironSymbol.IndexOf(':') + 1)..] : ironSymbol;
        int dot = s.IndexOf('.');
        return dot > 0 ? s[..dot] : s;
    }

    /// <summary>The Ironbeam symbol to use: an exact one from Products, or the front month (XCME:ES.U26).</summary>
    public string? IronSymbol(string root, string exchange, DateTime utc)
    {
        var exact = _s.Products.FirstOrDefault(x => x.Contains(':') && RootOf(x.Trim()).Equals(root, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.Trim();
        if (FuturesContracts.FrontMonth(root, utc) is not var (year, month)) return null;
        return $"{FuturesContracts.Mic(exchange)}:{root}.{FuturesContracts.MonthCodes[month - 1]}{year % 100:00}";
    }

    // ------------------------------------------------------------------ REST

    /// <summary>Signs in and returns the token (every call after it carries it as a Bearer header).</summary>
    private async Task<string> AuthAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_s.Username) || string.IsNullOrWhiteSpace(_s.Password) || string.IsNullOrWhiteSpace(_s.ApiKey))
            throw new InvalidOperationException("Fill in your Ironbeam user, password and API key (Connections → Ironbeam)");
        var body = JsonSerializer.Serialize(new { username = _s.Username.Trim(), password = _s.Password, apikey = _s.ApiKey.Trim() });
        using var resp = await _http.PostAsync(_s.Base + "/auth", new StringContent(body, Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        string? token = null, message = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            token = Str(doc.RootElement, "token");
            message = Str(doc.RootElement, "message") ?? Str(doc.RootElement, "error");
        }
        catch (JsonException)
        {
            // not JSON: the status code and the start of the text will have to do
        }

        if (resp.IsSuccessStatusCode && !string.IsNullOrEmpty(token)) return token;
        string why = message ?? (text.Length > 120 ? text[..120] : text);
        throw new InvalidOperationException(resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.BadRequest
            ? $"Ironbeam rejected the user, password or API key{(why.Length > 0 ? $" ({why})" : "")}"
            : $"Ironbeam sign-in failed: {(int)resp.StatusCode} {why}".Trim());
    }

    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _s.Base + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            string? msg = null;
            try { using var d = JsonDocument.Parse(text); msg = Str(d.RootElement, "message") ?? Str(d.RootElement, "error"); }
            catch (JsonException) { /* not JSON */ }
            throw new InvalidOperationException($"Ironbeam {path.Split('?')[0]}: {(int)resp.StatusCode} {msg ?? (text.Length > 100 ? text[..100] : text)}".Trim());
        }

        return JsonDocument.Parse(text.Length == 0 ? "{}" : text);
    }

    // ------------------------------------------------------------------ live

    /// <summary>No history (SupportsHistory is false): the chart starts with what arrives live.</summary>
    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("Ironbeam", (st, msg) => sink.OnStatus(instrument, st, msg));
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
            string? symbol = IronSymbol(instrument.Symbol, instrument.Exchange, DateTime.UtcNow);
            if (symbol == null)
            {
                live.SetChannel("data", FeedStatus.Error, $"Type the exact Ironbeam symbol of {instrument.Symbol} in Products (for example {FuturesContracts.Mic(instrument.Exchange)}:{instrument.Symbol}.Z26)");
                return;
            }

            using var ws = new ClientWebSocket();
            var conn = live.BeginConnection();
            string reason = "the connection dropped";
            bool any = false;
            try
            {
                live.SetChannel("data", FeedStatus.Connecting, $"Connecting to Ironbeam ({symbol})…");
                string token = await AuthAsync(conn.Token).ConfigureAwait(false);
                string streamId;
                using (var doc = await GetAsync("/stream/create", token, conn.Token).ConfigureAwait(false))
                    streamId = Str(doc.RootElement, "streamId") ?? throw new InvalidOperationException("Ironbeam did not give a stream id");

                // every connection needs its own stream; the token goes in the address because a WebSocket cannot always send headers
                var wsUri = new Uri(_s.Base.Replace("https://", "wss://").Replace("http://", "ws://") + $"/stream/{streamId}?token={Uri.EscapeDataString(token)}");
                using (var c = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                {
                    c.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(wsUri, c.Token).ConfigureAwait(false);
                }

                string sym = Uri.EscapeDataString(symbol).Replace("%3A", ":");
                using (await GetAsync($"/market/trades/subscribe/{streamId}?symbols={sym}", token, conn.Token).ConfigureAwait(false)) { }
                try
                {
                    using (await GetAsync($"/market/depths/subscribe/{streamId}?symbols={sym}", token, conn.Token).ConfigureAwait(false)) { }
                }
                catch (InvalidOperationException ex)
                {
                    // no Level 2 on this account: the trades keep coming, the reason is kept for the connection test
                    DepthError = ex.Message;
                }

                live.SetChannel("data", FeedStatus.Connected, null);
                failures = 0;

                var buf = new byte[1 << 16];
                var ms = new MemoryStream();
                var bids = new Dictionary<int, (long Ticks, decimal Size)>();
                var asks = new Dictionary<int, (long Ticks, decimal Size)>();
                var book = new OrderBookBuilder();
                while (ws.State == WebSocketState.Open && !conn.IsCancellationRequested)
                {
                    conn.CancelAfter(TimeSpan.FromSeconds(60)); // the server pings every 5 s: silence for a minute means the connection is dead
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
                        reason = "Ironbeam closed the connection";
                        break;
                    }

                    using var msg = JsonDocument.Parse(ms.ToArray());
                    var m = msg.RootElement;
                    if (m.ValueKind != JsonValueKind.Object) continue;

                    if (m.TryGetProperty("r", out var reset))
                    {
                        // the account changed or the trading day opened or closed: the stream has to be created again
                        reason = Str(reset, "message") ?? "Ironbeam reset the stream";
                        break;
                    }

                    if (m.TryGetProperty("tr", out var trs) && trs.ValueKind == JsonValueKind.Array)
                    {
                        any = true;
                        var trades = new List<Trade>();
                        foreach (var e in trs.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.Object || !SameSymbol(Str(e, "s"), symbol)) continue;
                            if (Bool(e, "is") == true || Bool(e, "clx") == true) continue; // a settlement price or a cancelled trade
                            if (Num(e, "p") is not { } price || Num(e, "sz") is not { } size || size <= 0) continue;
                            var time = Num(e, "st") is { } ms0 && ms0 > 0 ? DateTime.UnixEpoch.AddMilliseconds(ms0) : DateTime.UtcNow;
                            var side = (int)Int(e, "as") switch { 1 => TradeSide.Buy, 2 => TradeSide.Sell, _ => TradeSide.None };
                            trades.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, instrument.RoundPrice((decimal)price), (decimal)size, side));
                        }

                        if (trades.Count > 0) sink.OnTrades(instrument, trades);
                    }

                    if (m.TryGetProperty("d", out var depths) && depths.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in depths.EnumerateArray())
                        {
                            if (d.ValueKind != JsonValueKind.Object || !SameSymbol(Str(d, "s"), symbol)) continue;
                            any = true;
                            DateTime? time = null;
                            Apply(instrument, d, "b", bids, ref time);
                            Apply(instrument, d, "a", asks, ref time);
                            // ponytail: a busy contract sends a few hundred messages a second and each one builds a book of twenty levels,
                            // which is cheap; add a minimum gap between pictures if the heatmap ever shows it is not
                            book.Clear();
                            foreach (var (ticks, size) in bids.Values) book.Set(BookSide.Bid, ticks, size);
                            foreach (var (ticks, size) in asks.Values) book.Set(BookSide.Ask, ticks, size);
                            sink.OnDepth(instrument, book.BuildUpdate(time ?? DateTime.UtcNow, 50, true));
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
                // a refused login will not fix itself: say so and stop asking
                if (ex.Message.StartsWith("Ironbeam rejected", StringComparison.Ordinal) || ex.Message.StartsWith("Fill in", StringComparison.Ordinal))
                {
                    live.EndConnection(conn);
                    live.SetChannel("data", FeedStatus.Error, ex.Message);
                    return;
                }
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

    /// <summary>
    /// Applies the levels of one side. Each level says which row of the book it is (0 = best); one with no size is gone. This works whether a
    /// message carries the whole book or only the levels that changed.
    /// </summary>
    private static void Apply(Instrument instrument, JsonElement depth, string name, Dictionary<int, (long Ticks, decimal Size)> side, ref DateTime? time)
    {
        if (!depth.TryGetProperty(name, out var levels) || levels.ValueKind != JsonValueKind.Array) return;
        int position = 0;
        foreach (var l in levels.EnumerateArray())
        {
            if (l.ValueKind != JsonValueKind.Object) continue;
            int row = l.TryGetProperty("l", out _) ? (int)Int(l, "l") : position;
            position++;
            double? size = Num(l, "sz") ?? Num(l, "is");
            if (Num(l, "p") is not { } price || size is not { } s || s <= 0) side.Remove(row);
            else side[row] = (instrument.ToTicks((decimal)price), (decimal)s);
            if (Num(l, "t") is { } ms && ms > 0) time = DateTime.UnixEpoch.AddMilliseconds(ms);
        }
    }

    private static bool SameSymbol(string? a, string b) => a != null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why the last Level 2 subscription was refused (null = no refusal).</summary>
    public string? DepthError { get; private set; }

    /// <summary>Signs in, subscribes to the first product and says whether trades and Level 2 arrive.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        var instruments = await GetInstrumentsAsync(ct).ConfigureAwait(false);
        if (instruments.Count == 0) return (false, "Products has no CME future Trading Hub knows (for example ES, NQ, GC).");
        var inst = instruments[0];
        DepthError = null;
        var connected = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var depth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new StatusSink((st, msg) =>
        {
            if (st == FeedStatus.Connected) connected.TrySetResult(null);
            else if (st is FeedStatus.Error or FeedStatus.Reconnecting) connected.TrySetResult(msg ?? "Ironbeam did not accept the connection");
        }, () => depth.TrySetResult());
        using var sub = Subscribe(inst, sink);
        try
        {
            if (await connected.Task.WaitAsync(TimeSpan.FromSeconds(25), ct).ConfigureAwait(false) is { } error) return (false, error);
        }
        catch (TimeoutException)
        {
            return (false, "Ironbeam did not answer in 25 s");
        }

        string head = $"Signed in to Ironbeam{(_s.Demo ? " demo server" : "")}: {IronSymbol(inst.Symbol, inst.Exchange, DateTime.UtcNow)}.";
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!depth.Task.IsCompleted && DepthError == null && DateTime.UtcNow < end) await Task.Delay(100, ct).ConfigureAwait(false);
        return (true, head + (depth.Task.IsCompleted ? " Level 2 arriving."
            : DepthError != null ? $" Level 2 not available: {DepthError}."
            : " No Level 2 yet (market closed, or depth is not enabled for your account)."));
    }

    private sealed class StatusSink(Action<FeedStatus, string?> onStatus, Action onDepth) : IMarketDataSink
    {
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
        {
        }

        public void OnDepth(Instrument instrument, DepthUpdate update) => onDepth();

        public void OnStatus(Instrument instrument, FeedStatus status, string? message) => onStatus(status, message);
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
    private static bool? Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? v.GetDouble() : null;

    public void Dispose() => _http.Dispose();
}
