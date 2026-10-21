using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingHub.Feeds.CTrader;

/// <summary>Connection to the cTrader Open API (IC Markets, Pepperstone, FxPro, Fusion… all with the same API).</summary>
public sealed class CTraderSettings
{
    /// <summary>Real account (live.ctraderapi.com); otherwise, demo.</summary>
    public bool Live { get; set; }

    /// <summary>Application registered at openapi.ctrader.com (free).</summary>
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>The user's access token (OAuth or the application's "Playground" one).</summary>
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";

    /// <summary>cTrader account number (the login you see in cTrader); 0 = the first one of the environment.</summary>
    public long TraderLogin { get; set; }

    /// <summary>Alternative server (tests): ws://127.0.0.1:port</summary>
    public string? Url { get; set; }
}

/// <summary>A symbol of the cTrader account.</summary>
public sealed record CTraderSymbol(long Id, string Name, string Description, int Digits, decimal LotSize, decimal StepVolume, decimal MinVolume);

/// <summary>
/// A single JSON connection (port 5036) to the cTrader Open API for every chart and download: it authenticates the
/// application and the account, keeps the heartbeat every 10 s, matches requests with responses by clientMsgId and distributes
/// the live prices (ProtoOASpotEvent) to each subscription. It reconnects on its own and resubscribes what was there.
/// </summary>
public sealed class CTraderSession : IDisposable
{
    // message types (ProtoOAPayloadType / ProtoPayloadType)
    public const int ErrorRes = 50, Heartbeat = 51;
    public const int AppAuthReq = 2100, AppAuthRes = 2101, AccountAuthReq = 2102, AccountAuthRes = 2103;
    public const int SymbolsListReq = 2114, SymbolsListRes = 2115, SymbolByIdReq = 2116, SymbolByIdRes = 2117;
    public const int SubscribeSpotsReq = 2127, SubscribeSpotsRes = 2128, UnsubscribeSpotsReq = 2129, UnsubscribeSpotsRes = 2130, SpotEvent = 2131;
    public const int GetTrendbarsReq = 2137, GetTrendbarsRes = 2138, OaErrorRes = 2142, GetTickDataReq = 2145, GetTickDataRes = 2146;
    public const int TokenInvalidatedEvent = 2147, ClientDisconnectEvent = 2148, AccountsByTokenReq = 2149, AccountsByTokenRes = 2150;
    public const int RefreshTokenReq = 2173, RefreshTokenRes = 2174;

    private readonly Func<CTraderSettings> _settings;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<long, int> _spots = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _loopCts;
    private long _msgId;
    private DateTime _lastHistory = DateTime.MinValue;
    private readonly CancellationTokenSource _life = new();

    public CTraderSession(Func<CTraderSettings> settings) => _settings = settings;

    public long AccountId { get; private set; }
    public string AccountTitle { get; private set; } = "";
    public bool IsConnected => _ws?.State == WebSocketState.Open && AccountId != 0;
    public IReadOnlyList<CTraderSymbol> Symbols { get; private set; } = Array.Empty<CTraderSymbol>();

    /// <summary>Token renewed by the Open API (to store it in the Keychain).</summary>
    public event Action<string, string>? TokensRefreshed;

    /// <summary>Live price: symbol, bid and ask (in 1/100000), time (Unix ms).</summary>
    public event Action<long, long?, long?, long?>? Spot;

    /// <summary>It dropped or reconnected (for the charts' status).</summary>
    public event Action<bool, string?>? ConnectionChanged;

    private string Url
    {
        get
        {
            var s = _settings();
            return s.Url ?? (s.Live ? "wss://live.ctraderapi.com:5036" : "wss://demo.ctraderapi.com:5036");
        }
    }

    // ===================================================================== connection

    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            await ConnectAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        var s = _settings();
        if (string.IsNullOrWhiteSpace(s.ClientId) || string.IsNullOrWhiteSpace(s.ClientSecret))
            throw new InvalidOperationException("Your cTrader application's Client ID and Secret are missing (they are created for free at openapi.ctrader.com).");
        if (string.IsNullOrWhiteSpace(s.AccessToken))
            throw new InvalidOperationException("The cTrader access token is missing: click “Connect with cTrader” or paste the one from your application's Playground.");
        Drop("reconnecting");
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await ws.ConnectAsync(new Uri(Url), connectCts.Token).ConfigureAwait(false);
        }

        _ws = ws;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        var loop = _loopCts.Token;
        _ = Task.Run(() => ReceiveLoopAsync(ws, loop));
        _ = Task.Run(() => HeartbeatLoopAsync(ws, loop));

        await RawRequestAsync(AppAuthReq, new JsonObject { ["clientId"] = s.ClientId.Trim(), ["clientSecret"] = s.ClientSecret.Trim() }, ct).ConfigureAwait(false);
        var accounts = await RawRequestAsync(AccountsByTokenReq, new JsonObject { ["accessToken"] = s.AccessToken.Trim() }, ct).ConfigureAwait(false);
        var list = accounts.TryGetProperty("ctidTraderAccount", out var arr) ? arr.EnumerateArray().ToList() : new List<JsonElement>();
        var match = list.Where(a => (a.TryGetProperty("isLive", out var il) && il.ValueKind == JsonValueKind.True) == s.Live).ToList();
        if (match.Count == 0)
            throw new InvalidOperationException(list.Count == 0
                ? "The token has no authorized cTrader accounts."
                : $"The token has {list.Count} account(s) but none {(s.Live ? "real" : "demo")}: change the environment on the connection.");
        var chosen = s.TraderLogin > 0 ? match.FirstOrDefault(a => Long(a, "traderLogin") == s.TraderLogin) : match[0];
        if (chosen.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Account {s.TraderLogin} was not found on the token. Accounts {(s.Live ? "real" : "demo")}: {string.Join(", ", match.Select(a => Long(a, "traderLogin")))}.");
        AccountId = Long(chosen, "ctidTraderAccountId");
        AccountTitle = $"{(chosen.TryGetProperty("brokerTitleShort", out var bt) ? bt.GetString() : "cTrader")} {Long(chosen, "traderLogin")}";
        await RawRequestAsync(AccountAuthReq, new JsonObject { ["ctidTraderAccountId"] = AccountId, ["accessToken"] = s.AccessToken.Trim() }, ct).ConfigureAwait(false);
        await LoadSymbolsAsync(ct).ConfigureAwait(false);

        // whatever was subscribed before the drop
        var ids = _spots.Keys.ToList();
        if (ids.Count > 0) await SendSpotsAsync(SubscribeSpotsReq, ids, ct).ConfigureAwait(false);
        ConnectionChanged?.Invoke(true, null);
    }

    private async Task LoadSymbolsAsync(CancellationToken ct)
    {
        var res = await RequestAsync(SymbolsListReq, new JsonObject { ["ctidTraderAccountId"] = AccountId }, ct).ConfigureAwait(false);
        var light = res.TryGetProperty("symbol", out var arr) ? arr.EnumerateArray().Where(x => !x.TryGetProperty("enabled", out var en) || en.ValueKind != JsonValueKind.False).ToList() : new();
        var names = light.ToDictionary(x => Long(x, "symbolId"), x => (Name: Str(x, "symbolName"), Desc: Str(x, "description")));
        var symbols = new List<CTraderSymbol>();
        // details (digits, lot size, volume step) in batches of 200
        foreach (var chunk in names.Keys.Chunk(200))
        {
            var ids = new JsonArray(chunk.Select(id => (JsonNode)id).ToArray());
            var det = await RequestAsync(SymbolByIdReq, new JsonObject { ["ctidTraderAccountId"] = AccountId, ["symbolId"] = ids }, ct).ConfigureAwait(false);
            if (!det.TryGetProperty("symbol", out var syms)) continue;
            foreach (var x in syms.EnumerateArray())
            {
                long id = Long(x, "symbolId");
                if (!names.TryGetValue(id, out var n)) continue;
                symbols.Add(new CTraderSymbol(id, n.Name, n.Desc, (int)Long(x, "digits", 5), Long(x, "lotSize", 100) / 100m,
                    Math.Max(0.01m, Long(x, "stepVolume", 100) / 100m), Long(x, "minVolume", 100) / 100m));
            }
        }

        Symbols = symbols;
    }

    private void Drop(string why)
    {
        bool was = _ws != null;
        try
        {
            _loopCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _ws?.Abort();
            _ws?.Dispose();
        }
        catch
        {
            // already closed
        }

        _ws = null;
        AccountId = 0;
        foreach (var p in _pending)
            if (_pending.TryRemove(p.Key, out var tcs))
                tcs.TrySetException(new IOException("The connection to cTrader dropped (" + why + ")"));
        if (was) ConnectionChanged?.Invoke(false, why);
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                await SendAsync(ws, new JsonObject { ["payloadType"] = Heartbeat, ["payload"] = new JsonObject() }, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // the drop is detected by the read
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1 << 16];
        var ms = new MemoryStream();
        string why = "the connection dropped";
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    why = "cTrader closed the connection";
                    break;
                }

                Dispatch(ms.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            why = ex.Message;
        }

        if (ReferenceEquals(ws, _ws)) Drop(why);
    }

    private void Dispatch(byte[] data)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(data);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        int type = (int)Long(root, "payloadType");
        var payload = root.TryGetProperty("payload", out var p) ? p : default;
        string id = root.TryGetProperty("clientMsgId", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() ?? "" : "";
        switch (type)
        {
            case Heartbeat:
                return;
            case SpotEvent:
                long symbol = Long(payload, "symbolId");
                long? bid = Has(payload, "bid") ? Long(payload, "bid") : null;
                long? ask = Has(payload, "ask") ? Long(payload, "ask") : null;
                long? ts = Has(payload, "timestamp") ? Long(payload, "timestamp") : null;
                Spot?.Invoke(symbol, bid, ask, ts);
                return;
            case TokenInvalidatedEvent:
                Drop("cTrader invalidated the token: connect the account again");
                return;
            case ClientDisconnectEvent:
                Drop("cTrader dropped the session: " + Str(payload, "reason"));
                return;
        }

        if (id.Length > 0 && _pending.TryRemove(id, out var tcs))
        {
            if (type is ErrorRes or OaErrorRes)
                tcs.TrySetException(new CTraderException(Str(payload, "errorCode"), Str(payload, "description")));
            else tcs.TrySetResult(payload.ValueKind == JsonValueKind.Undefined ? root : payload);
        }
    }

    private async Task SendAsync(ClientWebSocket ws, JsonObject message, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Request that does not require the account to be authenticated (for the authentication itself).</summary>
    private async Task<JsonElement> RawRequestAsync(int type, JsonObject payload, CancellationToken ct)
    {
        var ws = _ws ?? throw new IOException("No connection to cTrader");
        string id = "v" + Interlocked.Increment(ref _msgId).ToString(CultureInfo.InvariantCulture);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(ws, new JsonObject { ["clientMsgId"] = id, ["payloadType"] = type, ["payload"] = payload }, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var reg = timeout.Token.Register(() => tcs.TrySetCanceled());
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("cTrader did not respond in time");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Request with the account already authenticated (it connects if needed).</summary>
    public async Task<JsonElement> RequestAsync(int type, JsonObject payload, CancellationToken ct)
    {
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        return await RawRequestAsync(type, payload, ct).ConfigureAwait(false);
    }

    /// <summary>History request: one at a time and with a pause between requests (the Open API rate-limits the history ones).</summary>
    public async Task<JsonElement> HistoryRequestAsync(int type, JsonObject payload, CancellationToken ct)
    {
        await _historyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = TimeSpan.FromMilliseconds(260) - (DateTime.UtcNow - _lastHistory);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await RequestAsync(type, payload, ct).ConfigureAwait(false);
                }
                catch (CTraderException ex) when (ex.Code is "REQUEST_FREQUENCY_EXCEEDED" or "BLOCKED_PAYLOAD_TYPE" && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1 + attempt), ct).ConfigureAwait(false);
                }
                finally
                {
                    _lastHistory = DateTime.UtcNow;
                }
            }
        }
        finally
        {
            _historyGate.Release();
        }
    }

    // ===================================================================== live prices

    public async Task SubscribeAsync(long symbolId, CancellationToken ct)
    {
        int n = _spots.AddOrUpdate(symbolId, 1, (_, v) => v + 1);
        await EnsureConnectedAsync(ct).ConfigureAwait(false);
        if (n == 1) await SendSpotsAsync(SubscribeSpotsReq, new[] { symbolId }, ct).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(long symbolId)
    {
        int n = _spots.AddOrUpdate(symbolId, 0, (_, v) => Math.Max(0, v - 1));
        if (n > 0) return;
        _spots.TryRemove(symbolId, out _);
        if (!IsConnected) return;
        try
        {
            await SendSpotsAsync(UnsubscribeSpotsReq, new[] { symbolId }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // it no longer matters
        }
    }

    private async Task SendSpotsAsync(int type, IEnumerable<long> ids, CancellationToken ct)
    {
        var payload = new JsonObject { ["ctidTraderAccountId"] = AccountId, ["symbolId"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()) };
        try
        {
            await RawRequestAsync(type, payload, ct).ConfigureAwait(false);
        }
        catch (CTraderException ex) when (ex.Code is "ALREADY_SUBSCRIBED" or "NOT_SUBSCRIBED")
        {
        }
    }

    /// <summary>Renews the token with the refresh token (valid for 30 days); reports it so it can be stored.</summary>
    public async Task<bool> RefreshTokenAsync(CancellationToken ct)
    {
        var s = _settings();
        if (string.IsNullOrWhiteSpace(s.RefreshToken) || _ws == null) return false;
        var res = await RawRequestAsync(RefreshTokenReq, new JsonObject { ["refreshToken"] = s.RefreshToken }, ct).ConfigureAwait(false);
        string access = Str(res, "accessToken"), refresh = Str(res, "refreshToken");
        if (access.Length == 0) return false;
        TokensRefreshed?.Invoke(access, refresh.Length > 0 ? refresh : s.RefreshToken);
        return true;
    }

    // ===================================================================== JSON helpers

    internal static bool Has(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    internal static long Long(JsonElement e, string key, long def = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return def;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt64(out var l) ? l : (long)v.GetDouble(),
            JsonValueKind.String => long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : def,
            JsonValueKind.True => 1,
            _ => def,
        };
    }

    internal static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    public void Dispose()
    {
        _life.Cancel();
        Drop("closed");
    }
}

/// <summary>cTrader Open API error (code and description).</summary>
public sealed class CTraderException(string code, string description) : Exception(string.IsNullOrEmpty(description) ? $"cTrader: {code}" : $"cTrader: {description} ({code})")
{
    public string Code { get; } = code;
}
