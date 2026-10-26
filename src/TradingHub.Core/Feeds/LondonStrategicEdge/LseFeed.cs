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
/// Stocks, ETFs, forex, crypto, commodities and indices through London Strategic Edge's free plan: live ticks over a websocket and
/// candles and tick history over the Data 3.0 REST API (download.londonstrategicedge.com/v3). The service removed the websocket's tick replay
/// and the websocket sends no live futures (only the last stored tick, from the previous close). The free archive, though, holds them in its "futures"
/// market (GOLD, SP500...) in real time, to the second: <see cref="Futures"/> maps the usual names to those series, and a futures chart gets its
/// live ticks by polling the tick export every few seconds (<see cref="PollAsync"/>) instead of the websocket.
/// What the service does not give: the aggressor side of a trade (it comes from the tick rule: the service's "bid" is just the trade price and
/// its "ask" one step above it, so they say nothing about who hit whom), a trade id, the volume of some markets (forex: each tick counts as 1)
/// and the order book.
/// The protocol follows the service's public client (lse-data 3.0): auth with {"action":"auth","api_key"} (the answer lists the symbols),
/// {"action":"subscribe","symbol"} and ticks as {"type":"tick","symbol","price","bid","ask","volume","ts"}; REST reads send the key in X-API-Key.
/// </summary>
public sealed class LseFeed : IMarketDataFeed, IKlineHistory
{
    public const string FeedId = "lse";
    private const string DefaultWs = "wss://data-ws.londonstrategicedge.com";
    private const string DefaultApi = "https://download.londonstrategicedge.com";
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
        _http.DefaultRequestHeaders.Add("X-API-Key", settings.ApiKey.Trim());
        _http.DefaultRequestHeaders.Add("User-Agent", "trading-hub (+https://londonstrategicedge.com)");
    }

    public string Id => FeedId;
    public string DisplayName => "London Strategic Edge";
    public bool SupportsHistory => true;
    public bool IsLive => true;
    public string HistoryNote => "ticks and candles, free, from London Strategic Edge";
    public string CacheTag => "lse";

    private string Ws => (_s.WsUrl ?? DefaultWs).TrimEnd('/');
    private string Api => (_s.ApiUrl ?? DefaultApi).TrimEnd('/');

    // ===================================================================== REST

    /// <summary>The service's "nothing stored for this window" answers: an empty slice of history, not a failure.</summary>
    private sealed class NoDataException(string message) : Exception(message);

    private async Task<(int Status, string Body)> SendAsync(string path, CancellationToken ct)
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
                // a dropped connection or a slow answer is worth another try
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
                string msg = body.TrimStart().StartsWith('<') ? resp.ReasonPhrase ?? "" : body, code = "";
                try
                {
                    using var err = JsonDocument.Parse(body);
                    if (err.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        code = Str(err.RootElement, "code");
                        foreach (var k in new[] { "message", "detail", "error" })
                            if (err.RootElement.TryGetProperty(k, out var m) && m.ValueKind == JsonValueKind.String) { msg = m.GetString() ?? body; break; }
                    }
                }
                catch (JsonException)
                {
                }

                if (msg.Length > 300) msg = msg[..300];
                if (code is "data-unavailable" or "date-not-trading" || msg.Contains("no stored data", StringComparison.OrdinalIgnoreCase)) throw new NoDataException(msg);
                throw new HttpRequestException(resp.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "London Strategic Edge rejected the API key (or your plan does not include that data): " + msg,
                    System.Net.HttpStatusCode.TooManyRequests => "London Strategic Edge says the allowance is used up: " + msg,
                    _ => $"London Strategic Edge replied {(int)resp.StatusCode}: {msg}",
                });
            }

            return ((int)resp.StatusCode, body);
        }
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct) => JsonDocument.Parse((await SendAsync(path, ct).ConfigureAwait(false)).Body);

    /// <summary>A CSV export: streamed at once, or (a big one) prepared as a job that is polled and then fetched.</summary>
    private async Task<string> ExportAsync(string path, CancellationToken ct)
    {
        var (status, body) = await SendAsync(path, ct).ConfigureAwait(false);
        if (status != 202) return body;
        string id;
        using (var job = JsonDocument.Parse(body)) id = Str(job.RootElement, "job_id");
        for (int i = 0; i < 150; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            using var info = await GetAsync("/v3/jobs/" + id, ct).ConfigureAwait(false);
            switch (Str(info.RootElement, "status"))
            {
                case "ready":
                    string url = Str(info.RootElement, "download_url");
                    return (await SendAsync(url.Length > 0 ? url : $"/v3/jobs/{id}/file", ct).ConfigureAwait(false)).Body;
                case "failed" or "expired":
                    throw new HttpRequestException("London Strategic Edge export " + Str(info.RootElement, "status") + ": " + Str(info.RootElement, "error"));
            }
        }

        throw new TimeoutException("London Strategic Edge is still preparing the export");
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

    // ===================================================================== instruments

    /// <summary>The futures the app knows by their usual names, and the series of the service's "futures" market that holds each one.</summary>
    private static readonly (string Symbol, string Series, string Name, decimal Tick, decimal PointValue)[] Futures =
    {
        // the tick sizes are the smallest price step seen in an hour of each series' real ticks; the values are the contracts' (US dollars per 1.0 of price)
        ("ES.F", "SP500", "E-mini S&P 500", 0.25m, 50m),
        ("NQ.F", "NASDAQ100", "E-mini Nasdaq-100", 0.25m, 20m),
        ("YM.F", "DOW", "E-mini Dow", 1m, 5m),
        ("RTY.F", "RUSSELL2000", "E-mini Russell 2000", 0.1m, 50m),
        ("MES.F", "MICROSP500", "Micro E-mini S&P 500", 0.25m, 5m),
        ("MNQ.F", "MICRONASDAQ100", "Micro E-mini Nasdaq-100", 0.25m, 2m),
        ("GC.F", "GOLD", "Gold", 0.1m, 100m),
        ("SI.F", "SILVER", "Silver", 0.005m, 5000m),
        ("HG.F", "COPPER", "Copper", 0.0005m, 25000m),
        ("PL.F", "PLATINUM", "Platinum", 0.1m, 50m),
        ("CL.F", "CRUDEOIL", "Crude Oil (WTI)", 0.01m, 1000m),
        ("NG.F", "NATGAS", "Natural Gas", 0.001m, 10000m),
        ("HO.F", "HEATINGOIL", "Heating Oil", 0.0001m, 42000m),
        ("RB.F", "GASOLINE", "RBOB Gasoline", 0.0001m, 42000m),
        ("6E.F", "EURUSD", "Euro FX", 0.00005m, 125000m),
        ("6B.F", "GBPUSD", "British Pound", 0.0001m, 62500m),
        ("6J.F", "JPYUSD", "Japanese Yen", 0.0000005m, 12500000m),
        ("6A.F", "AUDUSD", "Australian Dollar", 0.00005m, 100000m),
        ("6C.F", "CADUSD", "Canadian Dollar", 0.00005m, 100000m),
        ("6S.F", "CHFUSD", "Swiss Franc", 0.00005m, 125000m),
        ("ZC.F", "CORN", "Corn", 0.25m, 50m),
        ("ZS.F", "SOYBEANS", "Soybeans", 0.25m, 50m),
        ("ZW.F", "WHEAT", "Wheat", 0.25m, 50m),
        ("LE.F", "LIVECATTLE", "Live Cattle", 0.025m, 400m),
        ("HE.F", "LEANHOGS", "Lean Hogs", 0.025m, 400m),
        ("ZN.F", "US10Y", "10-Year T-Note", 0.015625m, 1000m),
        ("ZB.F", "US30Y", "30-Year T-Bond", 0.03125m, 1000m),
    };

    /// <summary>The usual futures names this feed knows (ES.F, CL.F...).</summary>
    public static IReadOnlyList<string> FuturesSymbols => Futures.Select(f => f.Symbol).ToList();

    /// <summary>A futures instrument built from the table, with no network (null for a name that is not in it).</summary>
    public static Instrument? FuturesInstrument(string symbol)
    {
        var f = Array.Find(Futures, x => string.Equals(x.Symbol, symbol.Trim(), StringComparison.OrdinalIgnoreCase));
        if (f.Symbol == null) return null;
        return new Instrument { Symbol = f.Symbol, FeedId = FeedId, Exchange = "futures", TickSize = f.Tick, VolumeStep = 1m, PointValue = f.PointValue, QuoteAsset = "USD", Description = f.Name + " Futures" };
    }

    /// <summary>The tradable classes (the symbol list also carries a few uncategorised bond yields).</summary>
    private static bool Tradable(string category) => category is "stocks" or "etfs" or "forex" or "crypto" or "commodities" or "indices";

    /// <summary>
    /// The catalog has no tick size, so it is a sensible one for the class (forex 5 decimals, JPY pairs 3, crypto 4, the rest 2) and the
    /// real one for the futures it carries that we know. Futures prices arrive with float noise (7729.999999999999): the tick size rounds it away.
    /// </summary>
    public static (decimal Tick, decimal PointValue, decimal VolumeStep) SpecOf(string dataset, string symbol)
    {
        var d = dataset.ToLowerInvariant();
        foreach (var f in Futures)
            if (f.Symbol == symbol) return (f.Tick, f.PointValue, 1m);
        switch (symbol)
        {
            case "FDAX": case "FDAX.F": return (0.5m, 25m, 1m);
            case "FESX": case "FESX.F": return (1m, 10m, 1m);
            case "FGBL": case "FGBL.F": case "FGBM": case "FGBM.F": return (0.01m, 1000m, 1m);
        }

        if (d.Contains("forex") || d == "fx") return (symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase) ? 0.001m : 0.00001m, 1m, 1m);
        if (d.Contains("crypto")) return (0.0001m, 1m, 0.00000001m);
        return (0.01m, 1m, 1m);
    }

    public static decimal TickSizeOf(string dataset, string symbol) => SpecOf(dataset, symbol).Tick;

    /// <summary>The live symbols: the service lists them in its answer to the websocket auth (one short connection, through the shared slot).</summary>
    private async Task<List<(string Dataset, string Symbol, string Name, DateTime? First, bool Live)>> CatalogAsync(CancellationToken ct)
    {
        if (_catalog != null) return _catalog;
        if (string.IsNullOrWhiteSpace(_s.ApiKey)) throw new InvalidOperationException("The London Strategic Edge API key is missing (it is free at londonstrategicedge.com/data).");
        Interlocked.Increment(ref _historyWaiting);
        try
        {
            lock (_lives) foreach (var l in _lives.ToList()) l.Reconnect(); // the live ones step aside and come back on their own
            await _slot.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        return _catalog = await ReadSymbolsAsync(ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex) when (attempt < 4 && IsConcurrencyError(ex.Message))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2.5), ct).ConfigureAwait(false); // the server may still be noticing a closed connection
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

    private async Task<List<(string Dataset, string Symbol, string Name, DateTime? First, bool Live)>> ReadSymbolsAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(60));
        await ws.ConnectAsync(new Uri(Ws), idle.Token).ConfigureAwait(false);
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        await AuthenticateAsync(ws, ms, buf, idle.Token).ConfigureAwait(false);
        var list = new List<(string, string, string, DateTime?, bool)>();
        while (true)
        {
            var text = await ReceiveAsync(ws, ms, buf, idle.Token).ConfigureAwait(false) ?? throw new InvalidOperationException("London Strategic Edge closed the connection");
            using var doc = JsonDocument.Parse(text);
            var m = doc.RootElement;
            string type = Str(m, "type");
            if (type == "error") throw new InvalidOperationException("London Strategic Edge: " + (Str(m, "message").Length > 0 ? Str(m, "message") : Str(m, "code")));
            if (type != "authenticated") continue;
            if (m.TryGetProperty("symbols", out var syms) && syms.ValueKind == JsonValueKind.Array)
                foreach (var r in syms.EnumerateArray())
                {
                    string symbol = Str(r, "symbol"), category = Str(r, "category").ToLowerInvariant();
                    if (symbol.Length > 0 && Tradable(category)) list.Add((category, symbol, Str(r, "name"), null, true));
                }

            break;
        }

        try
        {
            if (ws.State == WebSocketState.Open)
            {
                using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", closing.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }

        foreach (var f in Futures)
            if (!list.Any(l => l.Item2 == f.Symbol)) list.Add(("futures", f.Symbol, f.Name + " Futures", null, true));
        return list;
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
    /// Tick history from the archive's tick export, an hour a request (a busy future is about 15 000 trades an hour). The websocket's own
    /// replay was removed (REPLAY_REMOVED). The export's "kind" says trade or quote: only trades are kept, and the side comes from the tick rule.
    /// </summary>
    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (toUtc > DateTime.UtcNow) toUtc = DateTime.UtcNow;
        if (toUtc <= fromUtc) { progress?.Report(1); return; }
        var (market, symbol) = await ResolveAsync(instrument, ct).ConfigureAwait(false);
        long lastId = 0;
        decimal last = 0;
        var side = TradeSide.Buy;
        double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        for (var a = fromUtc; a < toUtc && !ct.IsCancellationRequested; a += TimeSpan.FromHours(1))
        {
            var b = a.AddHours(1) < toUtc ? a.AddHours(1) : toUtc;
            string csv;
            try
            {
                csv = await ExportAsync($"/v3/export?market={market}&symbol={Uri.EscapeDataString(symbol)}&timeframe=tick&from={Q(a)}&to={Q(b)}&format=csv&data=candles", ct).ConfigureAwait(false);
            }
            catch (NoDataException)
            {
                progress?.Report(Math.Min(1, (b - fromUtc).TotalSeconds / total));
                continue; // nothing stored for this hour (market closed, or ticks not kept for this instrument)
            }

            var batch = new List<Trade>();
            foreach (var (time, price, size) in ParseTicks(csv, instrument))
                batch.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, price, size, HistoryBuilder.TickRule(price, ref last, ref side)));

            if (batch.Count > 0 && !onBatch(batch)) return;
            progress?.Report(Math.Min(1, (b - fromUtc).TotalSeconds / total));
        }

        progress?.Report(1);
    }

    /// <summary>The trades of a tick export: "time","price","size","kind" with UTC times that have 9 decimals; quotes are skipped, a missing size counts as 1.</summary>
    internal static List<(DateTime Time, decimal Price, decimal Size)> ParseTicks(string csv, Instrument instrument)
    {
        var list = new List<(DateTime, decimal, decimal)>();
        using var reader = new StringReader(csv);
        var header = reader.ReadLine()?.Split(',').Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
        int it = header?.IndexOf("time") ?? -1, ip = header?.IndexOf("price") ?? -1, iz = header?.IndexOf("size") ?? -1, ik = header?.IndexOf("kind") ?? -1;
        if (it < 0 || ip < 0) return list;
        for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
        {
            var f = line.Split(',');
            if (f.Length <= ip || (ik >= 0 && f.Length > ik && f[ik].Trim().Trim('"') != "trade")) continue;
            if (!DateTime.TryParse(f[it].Trim().Trim('"'), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)) continue;
            decimal price = decimal.TryParse(f[ip].Trim().Trim('"'), NumberStyles.Float, Inv, out var pr) ? instrument.RoundPrice(pr) : 0;
            decimal size = iz >= 0 && f.Length > iz && decimal.TryParse(f[iz].Trim().Trim('"'), NumberStyles.Float, Inv, out var sz) ? sz : 0;
            if (price > 0) list.Add((time, price, size > 0 ? size : 1));
        }

        return list;
    }

    private int _pollers;

    /// <summary>
    /// Live ticks for a future: the websocket has none, but the tick export is current to the second, so it is asked every few seconds for what came
    /// after the last trade seen (trades that share the last timestamp are not counted twice). The free plan allows 60 calls a minute: each chart
    /// polls every 2.5 s, and more charts slow every poll down (1.4 s per chart) so together they stay inside it.
    /// </summary>
    private async Task PollAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription live)
    {
        var ct = live.Token;
        Interlocked.Increment(ref _pollers);
        try
        {
            live.SetChannel("data", FeedStatus.Connecting, "Connecting to London Strategic Edge…");
            var (market, symbol) = await ResolveAsync(instrument, ct).ConfigureAwait(false);
            var last = DateTime.UtcNow.AddSeconds(-20);
            int seenAtLast = 0, failures = 0;
            long lastId = 0;
            decimal lastPrice = 0;
            var side = TradeSide.Buy;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var from = new DateTime(last.Ticks - last.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
                    string csv = await ExportAsync($"/v3/export?market={market}&symbol={Uri.EscapeDataString(symbol)}&timeframe=tick&from={Q(from)}&to={Q(DateTime.UtcNow.AddSeconds(5))}&format=csv&data=candles", ct).ConfigureAwait(false);
                    var fresh = new List<Trade>();
                    int atLast = 0;
                    foreach (var (time, price, size) in ParseTicks(csv, instrument))
                    {
                        if (time < last) continue;
                        if (time == last && ++atLast <= seenAtLast) continue; // already delivered
                        fresh.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, price, size, HistoryBuilder.TickRule(price, ref lastPrice, ref side)));
                    }

                    if (fresh.Count > 0)
                    {
                        var newest = fresh[^1].Time;
                        seenAtLast = newest == last ? seenAtLast + fresh.Count : fresh.Count(t => t.Time == newest);
                        last = newest;
                        sink.OnTrades(instrument, fresh);
                    }

                    live.SetChannel("data", FeedStatus.Connected, null);
                    failures = 0;
                }
                catch (NoDataException)
                {
                    live.SetChannel("data", FeedStatus.Connected, null); // a quiet moment: nothing traded yet
                    failures = 0;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (ex is HttpRequestException { Message: var m } && m.Contains("rejected the API key"))
                    {
                        live.SetChannel("data", FeedStatus.Error, m); // a bad key never gets better by retrying
                        return;
                    }

                    failures++;
                    live.SetChannel("data", FeedStatus.Reconnecting, $"London Strategic Edge: {ex.Message} (retrying)");
                }

                double wait = Math.Max(2.5, Volatile.Read(ref _pollers) * 1.4) * (failures > 0 ? Math.Min(6, failures + 1) : 1);
                await Task.Delay(TimeSpan.FromSeconds(wait), ct).ContinueWith(_ => { }).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            live.SetChannel("data", FeedStatus.Error, ex.Message);
        }
        finally
        {
            Interlocked.Decrement(ref _pollers);
            live.SetChannel("data", FeedStatus.Disconnected, null);
        }
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("London Strategic Edge", (st, msg) => sink.OnStatus(instrument, st, msg));
        if (instrument.Exchange == "futures")
        {
            _ = Task.Run(() => PollAsync(instrument, sink, sub)); // no websocket for futures: see PollAsync
            return sub;
        }

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

    /// <summary>The service does not say how far back an instrument goes.</summary>
    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);

    private readonly Dictionary<string, (string Market, string Symbol)> _resolved = new();

    /// <summary>
    /// The websocket and the REST archive spell some symbols differently (BTC/USD streams, BTCUSDT is stored; EUR/USD streams, EUR_USD is stored):
    /// the archive is asked for the likely spelling in the class's market, then for the symbol anywhere (never the paid or option markets).
    /// </summary>
    private async Task<(string Market, string Symbol)> ResolveAsync(Instrument instrument, CancellationToken ct)
    {
        string sym = instrument.Symbol;
        lock (_resolved) if (_resolved.TryGetValue(sym, out var known)) return known;
        var tries = new List<(string? Market, string Symbol)>();
        switch (instrument.Exchange.ToLowerInvariant())
        {
            case "forex": tries.Add(("forex", sym.Replace('/', '_'))); break;
            case "crypto": tries.Add(("crypto", sym.Split('/')[0] + "USDT")); break;
            case "commodities": tries.Add(("commodities", sym)); break;
            case "indices": tries.Add(("indices", sym)); break;
            case "futures": tries.Add(("futures", Array.Find(Futures, f => f.Symbol == sym).Series ?? sym)); break;
            default: tries.Add(("us_stocks", sym)); break;
        }

        tries.Add((null, sym));
        foreach (var (market, candidate) in tries)
        {
            using var doc = await GetAsync($"/v3/pairs?{(market != null ? "market=" + market + "&" : "")}symbol={Uri.EscapeDataString(candidate)}&limit=20", ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("pairs", out var pairs) || pairs.ValueKind != JsonValueKind.Array) continue;
            foreach (var p in pairs.EnumerateArray())
            {
                string folder = Str(p, "folder");
                if (folder.Length == 0 || folder is "cme" or "nasdaq" or "us_options" || !string.Equals(Str(p, "symbol"), candidate, StringComparison.OrdinalIgnoreCase)) continue;
                lock (_resolved) return _resolved[sym] = (folder, Str(p, "symbol"));
            }
        }

        throw new InvalidOperationException($"London Strategic Edge has no stored history for {sym}.");
    }

    private static string Q(DateTime utc) => Uri.EscapeDataString(DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss", Inv));

    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (toUtc <= fromUtc) return;
        var (market, symbol) = await ResolveAsync(instrument, ct).ConfigureAwait(false);
        int m = minutes >= 15 ? 15 : minutes >= 5 ? 5 : 1;
        // about 20 000 rows a request: small enough for the service to stream the answer instead of preparing a job (60 calls a minute allowed)
        var step = TimeSpan.FromDays(m == 1 ? 14 : m == 5 ? 70 : 200);
        double total = Math.Max(1, (toUtc - fromUtc).TotalSeconds);
        for (var a = fromUtc; a < toUtc && !ct.IsCancellationRequested; a += step)
        {
            var b = a + step < toUtc ? a + step : toUtc;
            // adjusted=0: prices as they traded, like the live ticks (the default would restate old stock prices for splits)
            string csv;
            try
            {
                csv = await ExportAsync($"/v3/export?market={market}&symbol={Uri.EscapeDataString(symbol)}&timeframe={m}m&from={Q(a)}&to={Q(b)}&format=csv&adjusted=0&data=candles", ct).ConfigureAwait(false);
            }
            catch (NoDataException)
            {
                progress?.Report(Math.Min(1, (b - fromUtc).TotalSeconds / total));
                continue; // nothing stored for this window (weekend, holiday, before the listing)
            }

            var batch = ParseCandles(csv);
            if (batch.Count > 0 && !onBatch(batch)) return;
            progress?.Report(Math.Min(1, (b - fromUtc).TotalSeconds / total));
        }

        progress?.Report(1);
    }

    /// <summary>"time","open","high","low","close","volume","trades" with UTC times as "yyyy-MM-dd HH:mm:ss".</summary>
    internal static List<Kline> ParseCandles(string csv)
    {
        var batch = new List<Kline>();
        using var reader = new StringReader(csv);
        var header = reader.ReadLine()?.Split(',').Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToList();
        if (header == null) return batch;
        int it = header.IndexOf("time"), io = header.IndexOf("open"), ih = header.IndexOf("high"), il = header.IndexOf("low"), ic = header.IndexOf("close"), iv = header.IndexOf("volume");
        if (it < 0 || ic < 0) return batch;
        decimal Num(string[] f, int i) => i >= 0 && i < f.Length && decimal.TryParse(f[i].Trim().Trim('"'), NumberStyles.Float, Inv, out var d) ? d : 0;
        for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
        {
            var f = line.Split(',');
            if (f.Length <= ic || !DateTime.TryParse(f[it].Trim().Trim('"'), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)) continue;
            decimal c = Num(f, ic);
            if (c <= 0) continue;
            batch.Add(new Kline(new DateTimeOffset(time).ToUnixTimeMilliseconds(), Num(f, io), Num(f, ih), Num(f, il), c, Math.Max(1, Num(f, iv)), 0, false));
        }

        return batch;
    }

    /// <summary>Tests the key against the usage endpoint (it uses none of the allowance up) and says what is left of the plan in plain words.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("/v3/usage", ct).ConfigureAwait(false);
        var u = doc.RootElement;
        if (u.ValueKind != JsonValueKind.Object) return (true, "API key OK.");
        static string Bytes(decimal b) => b >= 1e9m ? $"{b / 1e9m:0.#} GB" : b >= 1e6m ? $"{b / 1e6m:0.#} MB" : $"{b / 1e3m:0.#} KB";
        var parts = new List<string>();
        if (Str(u, "plan").Length > 0) parts.Add(Str(u, "plan") + " plan");
        if (u.TryGetProperty("weekly", out var w) && w.ValueKind == JsonValueKind.Object && w.TryGetProperty("allowance_bytes", out _))
            parts.Add($"{Bytes(Dec(w, "used_bytes"))} of {Bytes(Dec(w, "allowance_bytes"))} downloaded this week");
        if (u.TryGetProperty("calls_per_minute", out _)) parts.Add($"{Dec(u, "calls_per_minute"):0} calls a minute");
        return (true, "API key OK. " + (parts.Count > 0 ? string.Join(" · ", parts) + "." : ""));
    }

    public void Dispose() => _http.Dispose();
}
