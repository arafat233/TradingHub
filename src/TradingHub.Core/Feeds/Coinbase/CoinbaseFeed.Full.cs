using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;
using TradingHub.Market;

namespace TradingHub.Feeds.Coinbase;

/// <summary>A Coinbase Exchange API key (view permission is enough): the three values Coinbase shows when it is created.</summary>
public sealed record CoinbaseKeys(string Key, string Secret, string Passphrase)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(Key) && !string.IsNullOrWhiteSpace(Secret) && !string.IsNullOrWhiteSpace(Passphrase);
}

/// <summary>
/// The order-level (Level 3) side of the Coinbase feed. Coinbase only publishes every order (the "full" channel) to signed-in clients, so this is used
/// when an API key is configured; without one the feed keeps using the public level 2 book (<see cref="CoinbaseFeed.Subscribe"/>).
/// The book is built the way Coinbase documents: the websocket messages are queued, a public REST snapshot of every order (level 3, with its
/// sequence number) is fetched, the queued messages up to that sequence are dropped and the rest applied; after that every message must carry the
/// next sequence number, and a gap makes the feed reconnect and start over.
/// </summary>
public sealed partial class CoinbaseFeed
{
    /// <summary>The signature Coinbase wants in the subscribe message: base64( HMAC-SHA256( base64-decoded secret, timestamp + "GET" + "/users/self/verify" ) ).</summary>
    public static string Sign(string secretBase64, string timestamp)
    {
        using var h = new HMACSHA256(Convert.FromBase64String(secretBase64.Trim()));
        return Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(timestamp + "GET" + "/users/self/verify")));
    }

    /// <summary>
    /// Checks the key the way the feed will use it: a signed subscribe to the "full" channel, then the first answer (an error names the problem;
    /// "subscriptions" means the key is accepted). Nothing is read after that.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestKeysAsync(CancellationToken ct)
    {
        if (_keys == null) return (false, "Fill in the API key, secret and passphrase.");
        using var ws = new ClientWebSocket();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await ws.ConnectAsync(new Uri(Ws), cts.Token).ConfigureAwait(false);
        string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string msg;
        try
        {
            msg = JsonSerializer.Serialize(new { type = "subscribe", product_ids = new[] { "BTC-USD" }, channels = new[] { "full" }, signature = Sign(_keys.Secret, ts), key = _keys.Key.Trim(), passphrase = _keys.Passphrase.Trim(), timestamp = ts });
        }
        catch (FormatException)
        {
            return (false, "The API secret is not valid: Coinbase shows it as a base64 text, copy it whole.");
        }

        await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
        var buf = new byte[1 << 16];
        for (int i = 0; i < 5; i++)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return (false, "Coinbase closed the connection.");
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buf, 0, r.Count));
            string type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type == "error") return (false, "Coinbase refused the key: " + (doc.RootElement.TryGetProperty("reason", out var rs) ? rs.GetString() : doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "unknown error") + " Check that the key, secret and passphrase are all from Coinbase Exchange (exchange.coinbase.com), not the Coinbase app.");
            if (type == "subscriptions") return (true, "Key accepted: the order-level (Level 3) feed is on.");
        }

        return (false, "Coinbase did not answer.");
    }

    private IDisposable SubscribeFull(Instrument instrument, IMarketDataSink sink, CoinbaseKeys keys)
    {
        var sub = new LiveSubscription("Coinbase", (st, msg) => sink.OnStatus(instrument, st, msg));
        var book = new OrderBookBuilder();
        var orders = new Dictionary<string, (long Id, long Ticks, bool Ask, decimal Size)>();
        var level = new Dictionary<(bool Ask, long Ticks), decimal>();
        var events = new List<OrderEvent>();
        var queued = new List<JsonElement>();
        var gate = new object();
        bool synced = false;
        long lastSeq = 0, nextId = 1, lastTradeId = 0;
        DateTime lastTradeTime = DateTime.MinValue, lastPublish = DateTime.MinValue;
        Exception? failed = null;

        void Adjust(bool ask, long ticks, decimal delta)
        {
            level.TryGetValue((ask, ticks), out var old);
            decimal now = Math.Max(0, old + delta);
            if (now == 0) level.Remove((ask, ticks)); else level[(ask, ticks)] = now;
            book.Set(ask ? BookSide.Ask : BookSide.Bid, ticks, now);
        }

        void Publish(bool full)
        {
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

        void Add(string orderId, decimal price, decimal size, bool ask, DateTime time, bool announce)
        {
            long ticks = instrument.ToTicks(price);
            long id = nextId++;
            orders[orderId] = (id, ticks, ask, size);
            Adjust(ask, ticks, size);
            if (announce) events.Add(new OrderEvent(time, OrderEventKind.Add, id, ask, ticks, size));
        }

        // one message of the full channel, already known to be the next in sequence
        void Apply(JsonElement m)
        {
            string type = m.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            DateTime time = m.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.String ? TimeOf(tm) : DateTime.UtcNow;
            string orderId = m.TryGetProperty("order_id", out var oi) ? oi.GetString() ?? "" : "";
            switch (type)
            {
                case "open": // a limit order rests on the book
                    if (!orders.ContainsKey(orderId))
                        Add(orderId, Dec(m.GetProperty("price")), Dec(m.GetProperty("remaining_size")), m.GetProperty("side").GetString() == "sell", time, announce: true);
                    break;
                case "done": // it left the book: filled or cancelled (an order that never rested, a market order, is not in the map)
                    if (orders.Remove(orderId, out var gone))
                    {
                        Adjust(gone.Ask, gone.Ticks, -gone.Size);
                        bool filled = m.TryGetProperty("reason", out var rs) && rs.GetString() == "filled";
                        events.Add(new OrderEvent(time, filled ? OrderEventKind.Fill : OrderEventKind.Cancel, gone.Id, gone.Ask, gone.Ticks, gone.Size));
                    }

                    break;
                case "match": // a trade; "side" is the maker's, so the aggressor is the other one
                {
                    long tradeId = m.GetProperty("trade_id").GetInt64();
                    decimal size = Dec(m.GetProperty("size"));
                    if (tradeId > lastTradeId)
                    {
                        lastTradeId = tradeId;
                        lastTradeTime = time;
                        sink.OnTrades(instrument, new[] { new Trade(tradeId, time, Dec(m.GetProperty("price")), size, Aggressor(m.GetProperty("side").GetString() ?? "")) });
                    }

                    string maker = m.GetProperty("maker_order_id").GetString() ?? "";
                    if (orders.TryGetValue(maker, out var mo))
                    {
                        decimal left = Math.Max(0, mo.Size - size);
                        Adjust(mo.Ask, mo.Ticks, left - mo.Size);
                        orders[maker] = mo with { Size = left };
                        if (left > 0) events.Add(new OrderEvent(time, OrderEventKind.Modify, mo.Id, mo.Ask, mo.Ticks, left)); // the "done" that follows reports a full fill
                    }

                    break;
                }

                case "change": // an order was resized (self-trade prevention or an amend)
                    if (orders.TryGetValue(orderId, out var co) && m.TryGetProperty("new_size", out var ns))
                    {
                        decimal size = Dec(ns);
                        Adjust(co.Ask, co.Ticks, size - co.Size);
                        orders[orderId] = co with { Size = size };
                        events.Add(new OrderEvent(time, OrderEventKind.Modify, co.Id, co.Ask, co.Ticks, size));
                    }

                    break;
                case "error":
                    throw new InvalidOperationException(m.TryGetProperty("message", out var em) ? em.GetString() + (m.TryGetProperty("reason", out var rr) ? ": " + rr.GetString() : "") : "Coinbase refused the subscription");
            }
        }

        // the snapshot of every order, then the queued messages after it
        async Task Sync(CancellationToken token)
        {
            try
            {
                using var resp = await _http.GetAsync($"{Rest}/products/{instrument.Symbol}/book?level=3", token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                lock (gate)
                {
                    book.Clear();
                    orders.Clear();
                    level.Clear();
                    events.Clear();
                    var snapshotTime = DateTime.UtcNow;
                    foreach (var (name, ask) in new[] { ("bids", false), ("asks", true) })
                        foreach (var o in doc.RootElement.GetProperty(name).EnumerateArray())
                            Add(o[2].GetString() ?? "", Dec(o[0]), Dec(o[1]), ask, snapshotTime, announce: false);
                    lastSeq = doc.RootElement.GetProperty("sequence").GetInt64();
                    foreach (var q in queued)
                        if (q.TryGetProperty("sequence", out var s) && s.GetInt64() > lastSeq) Next(q);
                    queued.Clear();
                    synced = true;
                    Publish(full: true);
                }
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // the snapshot could not be fetched or the queued messages did not follow it: say why, wait (the level 3 snapshot is big and
                // rate limited, so a failing one must not be asked for in a loop) and start the connection over
                failed = ex;
                sub.SetChannel("data", FeedStatus.Reconnecting, "Coinbase: " + ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), token).ContinueWith(_ => { }).ConfigureAwait(false);
                sub.Reconnect();
            }
        }

        // each message must follow the last one; a gap means something was lost and the book can no longer be trusted
        void Next(JsonElement m)
        {
            if (!m.TryGetProperty("sequence", out var s)) { Apply(m); return; }
            long seq = s.GetInt64();
            if (seq <= lastSeq) return;
            if (seq != lastSeq + 1) throw new InvalidOperationException($"Coinbase messages were lost (expected {lastSeq + 1}, got {seq})");
            lastSeq = seq;
            Apply(m);
        }

        async Task OnOpen(ClientWebSocket ws, CancellationToken token)
        {
            lock (gate)
            {
                synced = false;
                failed = null;
                queued.Clear();
                book.Clear();
                orders.Clear();
                level.Clear();
                events.Clear();
            }

            string ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            var msg = JsonSerializer.Serialize(new
            {
                type = "subscribe",
                product_ids = new[] { instrument.Symbol },
                channels = new[] { "full", "heartbeat" }, // a quiet product still sends heartbeats, so silence means a dead connection
                signature = Sign(keys.Secret, ts),
                key = keys.Key.Trim(),
                passphrase = keys.Passphrase.Trim(),
                timestamp = ts,
            });
            await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            _ = Sync(token);
            if (lastTradeTime > DateTime.MinValue)
            {
                try
                {
                    // what traded while the connection was down (the snapshot does not carry trades)
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
            if (failed != null) throw failed;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type == "error") throw new InvalidOperationException((root.TryGetProperty("message", out var em) ? em.GetString() : "Coinbase refused the subscription") + (root.TryGetProperty("reason", out var rr) ? ": " + rr.GetString() : ""));
            if (type is "subscriptions" or "heartbeat" or "") return;
            lock (gate)
            {
                if (!synced) { queued.Add(root.Clone()); return; }
                Next(root);
                Publish(full: false);
            }
        }

        // a busy product never goes quiet for long, and heartbeats are not part of the full channel: a minute of silence is a dead connection
        _ = Task.Run(() => WsRunner.RunAsync(sub, "Coinbase", new Uri(Ws), OnOpen, OnMessage, TimeSpan.FromSeconds(60)));
        return sub;
    }
}
