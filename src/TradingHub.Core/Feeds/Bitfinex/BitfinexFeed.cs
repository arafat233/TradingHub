using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Bitfinex;

/// <summary>
/// Public Bitfinex spot data, free and with no key: the tape, the level 2 book AND the raw order-by-order book (Level 3: every order has an id),
/// over one websocket; history from the REST trades. The raw book arrives as [order id, price, amount] (amount &gt; 0 = bid, &lt; 0 = ask; price 0
/// = the order is gone, without saying whether it was filled or cancelled: <see cref="TradingHub.Data.OrderFlowDetector"/> tells them apart with the trades).
/// The side of a trade is the taker's (amount &gt; 0 = buyer). Prices have 5 significant digits, which gives each pair its tick size.
/// </summary>
public sealed class BitfinexFeed : IMarketDataFeed
{
    public const string FeedId = "bitfinex";
    private const int PageSize = 10_000, MaxPages = 60; // 60 x 10 000 trades; the REST allows about 30 calls a minute
    private readonly string _rest, _ws;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Test servers can replace the two addresses.</summary>
    public BitfinexFeed(string? restUrl = null, string? wsUrl = null)
    {
        _rest = (restUrl ?? "https://api-pub.bitfinex.com/v2").TrimEnd('/');
        _ws = wsUrl ?? "wss://api-pub.bitfinex.com/ws/2";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingHub");
    }

    public string Id => FeedId;
    public string DisplayName => "Bitfinex";
    public bool SupportsHistory => true;
    public bool IsLive => true;

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (e.TryGetDecimal(out var d) ? d : (decimal)e.GetDouble()) : 0;

    private static DateTime TimeOfMs(long ms) => DateTime.UnixEpoch.AddMilliseconds(ms);

    /// <summary>Bitfinex prices have 5 significant digits (80 469, 2 332.6, 0.12345): the last of them is the tick.</summary>
    public static decimal TickFor(decimal price) =>
        price <= 0 ? 0.00000001m : (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)price)) - 4);

    private static (string Base, string Quote) Split(string pair) =>
        pair.Contains(':') ? (pair[..pair.IndexOf(':')], pair[(pair.IndexOf(':') + 1)..]) : pair.Length == 6 ? (pair[..3], pair[3..]) : (pair, "USD");

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        using var pairsDoc = JsonDocument.Parse(await _http.GetStringAsync($"{_rest}/conf/pub:list:pair:exchange", ct).ConfigureAwait(false));
        var exchange = pairsDoc.RootElement[0].EnumerateArray().Select(p => p.GetString() ?? "").ToHashSet();
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{_rest}/tickers?symbols=ALL", ct).ConfigureAwait(false));
        var list = new List<(Instrument I, decimal Value)>();
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            string sym = t[0].GetString() ?? "";
            if (!sym.StartsWith('t') || !exchange.Contains(sym[1..])) continue;
            decimal last = Dec(t[7]), vol = Dec(t[8]);
            if (last <= 0) continue;
            var (b, q) = Split(sym[1..]);
            list.Add((new Instrument
            {
                Symbol = sym[1..],
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = TickFor(last),
                VolumeStep = 0.00000001m,
                BaseAsset = b,
                QuoteAsset = q,
                Description = $"{b}/{q} spot",
            }, last * vol));
        }

        string[] popular = { "BTCUSD", "ETHUSD", "SOLUSD", "XRPUSD", "BTCEUR" };
        return list.OrderBy(x => Array.IndexOf(popular, x.I.Symbol) is var k && k >= 0 ? k : 999).ThenByDescending(x => x.Value).Select(x => x.I).ToList();
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        long start = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long end = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        long lastId = fromId ?? -1;
        for (int page = 0; page < MaxPages && !ct.IsCancellationRequested && start < end; page++)
        {
            var body = await GetWithRetryAsync($"{_rest}/trades/t{Uri.EscapeDataString(instrument.Symbol)}/hist?limit={PageSize}&start={start}&end={end}&sort=1", ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            var batch = new List<Trade>();
            long lastMs = start;
            int rows = 0;
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                rows++;
                long id = r[0].GetInt64(), ms = r[1].GetInt64();
                lastMs = ms;
                decimal amount = Dec(r[2]), price = Dec(r[3]);
                if (id <= lastId || price <= 0) continue;
                lastId = id;
                batch.Add(new Trade(id, TimeOfMs(ms), price, Math.Abs(amount), amount > 0 ? TradeSide.Buy : TradeSide.Sell));
            }

            if (batch.Count > 0 && !onBatch(batch)) return;
            progress?.Report(Math.Clamp((double)(lastMs - new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()) / Math.Max(1, end - new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeMilliseconds()), 0, 0.99));
            if (rows < PageSize) break;
            start = lastMs; // inclusive: trades that share the last millisecond are not lost, the ids already seen are skipped
            await Task.Delay(2200, ct).ConfigureAwait(false);
        }

        progress?.Report(1);
    }

    private async Task<string> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode == 429 && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); // the public REST asks for a pause
                continue;
            }

            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("Bitfinex", (st, msg) => sink.OnStatus(instrument, st, msg));
        var book = new OrderBookBuilder();
        var orders = new Dictionary<long, (long Ticks, bool Ask, decimal Size)>();
        var level = new Dictionary<(bool Ask, long Ticks), decimal>(); // the sum of the orders at each price: the level 2 book
        var events = new List<OrderEvent>();
        long bookChan = -1, tradeChan = -1, lastTradeId = 0;
        DateTime lastTradeTime = DateTime.MinValue, lastPublish = DateTime.MinValue;

        void Adjust(bool ask, long ticks, decimal delta)
        {
            level.TryGetValue((ask, ticks), out var old);
            decimal now = Math.Max(0, old + delta);
            if (now == 0) level.Remove((ask, ticks)); else level[(ask, ticks)] = now;
            book.Set(ask ? BookSide.Ask : BookSide.Bid, ticks, now);
        }

        // one order: price 0 = gone; an id already known = amended; otherwise a new order (a snapshot only fills the book, it is not news)
        void ApplyOrder(JsonElement o, DateTime time, bool announce)
        {
            long id = o[0].GetInt64();
            decimal price = Dec(o[1]), amount = Dec(o[2]);
            if (price == 0)
            {
                if (!orders.Remove(id, out var gone)) return;
                Adjust(gone.Ask, gone.Ticks, -gone.Size);
                if (announce) events.Add(new OrderEvent(time, OrderEventKind.Remove, id, gone.Ask, gone.Ticks, gone.Size));
                return;
            }

            bool ask = amount < 0;
            decimal size = Math.Abs(amount);
            long ticks = instrument.ToTicks(price);
            bool known = orders.TryGetValue(id, out var old);
            if (known) Adjust(old.Ask, old.Ticks, -old.Size);
            orders[id] = (ticks, ask, size);
            Adjust(ask, ticks, size);
            if (announce) events.Add(new OrderEvent(time, known ? OrderEventKind.Modify : OrderEventKind.Add, id, ask, ticks, size));
        }

        void Publish(bool full)
        {
            // the order events always go on at once (they carry their own times); only the book picture is limited to about 10 a second
            if (events.Count > 0)
            {
                sink.OnOrderEvents(instrument, events.ToArray());
                events.Clear();
            }

            var now = DateTime.UtcNow;
            if (!full && (now - lastPublish).TotalMilliseconds < 90) return;
            lastPublish = now;
            sink.OnDepth(instrument, book.BuildUpdate(now, 500, full));
        }

        async Task OnOpen(ClientWebSocket ws, CancellationToken token)
        {
            book.Clear();
            orders.Clear();
            level.Clear();
            events.Clear();
            bookChan = tradeChan = -1;
            async Task Send(string json) => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            await Send("{\"event\":\"conf\",\"flags\":32768}").ConfigureAwait(false); // 32768 = a millisecond timestamp at the end of every message
            await Send($"{{\"event\":\"subscribe\",\"channel\":\"book\",\"symbol\":\"t{instrument.Symbol}\",\"prec\":\"R0\",\"freq\":\"F0\",\"len\":\"250\"}}").ConfigureAwait(false);
            await Send($"{{\"event\":\"subscribe\",\"channel\":\"trades\",\"symbol\":\"t{instrument.Symbol}\"}}").ConfigureAwait(false);
            if (lastTradeTime > DateTime.MinValue)
            {
                try
                {
                    // what traded while the connection was down
                    await LoadTradesAsync(instrument, lastTradeTime, DateTime.UtcNow.AddMinutes(1), lastTradeId, b =>
                    {
                        sink.OnTrades(instrument, b);
                        lastTradeTime = b[^1].Time;
                        lastTradeId = b[^1].Id;
                        return true;
                    }, null, token).ConfigureAwait(false);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    // best effort
                }
            }
        }

        void OnMessage(JsonElement root)
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                string ev = root.TryGetProperty("event", out var e) ? e.GetString() ?? "" : "";
                if (ev == "subscribed")
                {
                    long chan = root.GetProperty("chanId").GetInt64();
                    if (root.GetProperty("channel").GetString() == "book") bookChan = chan; else tradeChan = chan;
                }
                else if (ev == "error")
                {
                    throw new InvalidOperationException(root.TryGetProperty("msg", out var m) ? m.GetString() : "Bitfinex refused the subscription");
                }
                else if (ev == "info" && root.TryGetProperty("code", out var code) && code.GetInt32() == 20051)
                {
                    throw new InvalidOperationException("Bitfinex asked clients to reconnect");
                }

                return;
            }

            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2) return;
            long ch = root[0].GetInt64();
            var payload = root[1];
            if (payload.ValueKind == JsonValueKind.String && payload.GetString() != "te") return; // hb, tu
            var last = root[root.GetArrayLength() - 1];
            var time = root.GetArrayLength() > 2 && last.ValueKind == JsonValueKind.Number ? TimeOfMs(last.GetInt64()) : DateTime.UtcNow;

            if (ch == bookChan)
            {
                if (payload.ValueKind != JsonValueKind.Array || payload.GetArrayLength() == 0) return;
                if (payload[0].ValueKind == JsonValueKind.Array)
                {
                    book.Clear();
                    orders.Clear();
                    level.Clear();
                    foreach (var o in payload.EnumerateArray()) ApplyOrder(o, time, announce: false);
                    Publish(full: true);
                }
                else
                {
                    ApplyOrder(payload, time, announce: true);
                    Publish(full: false);
                }
            }
            else if (ch == tradeChan && payload.ValueKind == JsonValueKind.String && root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.Array)
            {
                var t = root[2]; // [id, ms, amount, price]
                long id = t[0].GetInt64();
                if (id <= lastTradeId) return;
                decimal amount = Dec(t[2]), price = Dec(t[3]);
                var tt = TimeOfMs(t[1].GetInt64());
                lastTradeId = id;
                lastTradeTime = tt;
                sink.OnTrades(instrument, new[] { new Trade(id, tt, price, Math.Abs(amount), amount > 0 ? TradeSide.Buy : TradeSide.Sell) });
            }
        }

        // heartbeats come every 15 s on both channels: a minute of silence is a dead connection
        _ = Task.Run(() => WsRunner.RunAsync(sub, "Bitfinex", new Uri(_ws), OnOpen, OnMessage, TimeSpan.FromSeconds(60)));
        return sub;
    }

    public void Dispose() => _http.Dispose();
}
