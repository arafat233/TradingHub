using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Coinbase;

/// <summary>
/// Public Coinbase Exchange spot data: tape (matches) and level 2 book over WebSocket, history from the REST trades (newest first, paged back).
/// The "side" of a Coinbase trade is the side of the resting (maker) order, so the aggressor is the opposite one.
/// With an API key (<see cref="CoinbaseKeys"/>) it uses the signed-in "full" channel instead and also publishes every order (Level 3): see CoinbaseFeed.Full.cs.
/// </summary>
public sealed partial class CoinbaseFeed : IMarketDataFeed
{
    public const string SpotId = "coinbase-spot";
    private readonly string Rest, Ws;
    private readonly CoinbaseKeys? _keys;
    private const int MaxPages = 300; // 300 x 1000 trades: paging back is slow, so a long range stops here and the rest is recorded as it comes
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <param name="keys">An API key turns on the order-level feed (without one the public level 2 book is used).</param>
    /// <param name="restUrl">Test servers can replace the two addresses.</param>
    public CoinbaseFeed(CoinbaseKeys? keys = null, string? restUrl = null, string? wsUrl = null)
    {
        _keys = keys is { IsComplete: true } ? keys : null;
        Rest = (restUrl ?? "https://api.exchange.coinbase.com").TrimEnd('/');
        Ws = wsUrl ?? "wss://ws-feed.exchange.coinbase.com";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingHub");
    }

    /// <summary>True when the feed publishes every order (an API key was given).</summary>
    public bool HasOrderLevel => _keys != null;

    public string Id => SpotId;
    public string DisplayName => "Coinbase";
    public bool SupportsHistory => true;
    public bool IsLive => true;

    private static decimal Dec(JsonElement e) => decimal.Parse(e.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture);

    private static DateTime TimeOf(JsonElement e) =>
        DateTime.Parse(e.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static TradeSide Aggressor(string makerSide) => makerSide == "buy" ? TradeSide.Sell : TradeSide.Buy;

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{Rest}/products", ct).ConfigureAwait(false));
        var list = new List<Instrument>();
        foreach (var p in doc.RootElement.EnumerateArray())
        {
            if (p.GetProperty("status").GetString() != "online" || p.GetProperty("trading_disabled").GetBoolean()) continue;
            string b = p.GetProperty("base_currency").GetString()!, q = p.GetProperty("quote_currency").GetString()!;
            list.Add(new Instrument
            {
                Symbol = p.GetProperty("id").GetString()!,
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = Dec(p.GetProperty("quote_increment")),
                VolumeStep = Dec(p.GetProperty("base_increment")),
                BaseAsset = b,
                QuoteAsset = q,
                Description = $"{b}/{q} spot",
            });
        }

        string[] popular = { "BTC-USD", "ETH-USD", "SOL-USD", "XRP-USD", "BTC-EUR" };
        return list.OrderBy(i => Array.IndexOf(popular, i.Symbol) is var k && k >= 0 ? k : 999).ThenBy(i => i.Symbol).ToList();
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        // pages come newest first: collect back to the start of the range, then hand them over in order
        var pages = new List<List<Trade>>();
        string? after = null;
        DateTime newest = DateTime.MinValue, oldest = DateTime.MaxValue;
        for (int page = 0; page < MaxPages && !ct.IsCancellationRequested; page++)
        {
            using var resp = await _http.GetAsync($"{Rest}/products/{instrument.Symbol}/trades?limit=1000" + (after != null ? "&after=" + after : ""), ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var batch = new List<Trade>();
            bool reachedStart = false;
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var time = TimeOf(t.GetProperty("time"));
                long id = t.GetProperty("trade_id").GetInt64();
                if (newest == DateTime.MinValue) newest = time;
                oldest = time;
                if (time < fromUtc || (fromId.HasValue && id <= fromId.Value)) { reachedStart = true; continue; }
                if (time > toUtc) continue;
                batch.Add(new Trade(id, time, Dec(t.GetProperty("price")), Dec(t.GetProperty("size")), Aggressor(t.GetProperty("side").GetString() ?? "")));
            }

            if (batch.Count > 0) pages.Add(batch);
            progress?.Report(Math.Clamp((newest - oldest).TotalSeconds / Math.Max(1, (newest - fromUtc).TotalSeconds), 0, 0.99));
            if (reachedStart || doc.RootElement.GetArrayLength() < 1000 || !resp.Headers.TryGetValues("cb-after", out var cur)) break;
            after = cur.First();
        }

        for (int i = pages.Count - 1; i >= 0; i--)
        {
            pages[i].Reverse();
            if (!onBatch(pages[i])) return;
        }

        progress?.Report(1);
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        if (_keys != null) return SubscribeFull(instrument, sink, _keys);
        var sub = new LiveSubscription("Coinbase", (st, msg) => sink.OnStatus(instrument, st, msg));
        var book = new OrderBookBuilder();
        DateTime lastTradeTime = DateTime.MinValue, lastPublish = DateTime.MinValue;
        long lastId = 0;

        async Task OnOpen(ClientWebSocket ws, CancellationToken token)
        {
            book.Clear();
            var msg = $"{{\"type\":\"subscribe\",\"product_ids\":[\"{instrument.Symbol}\"],\"channels\":[\"matches\",\"level2_batch\"]}}";
            await ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            if (lastTradeTime > DateTime.MinValue)
            {
                try
                {
                    await LoadTradesAsync(instrument, lastTradeTime, DateTime.UtcNow.AddMinutes(1), lastId, b =>
                    {
                        sink.OnTrades(instrument, b);
                        lastTradeTime = b[^1].Time;
                        lastId = b[^1].Id;
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
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            switch (type)
            {
                case "match":
                case "last_match":
                    long id = root.GetProperty("trade_id").GetInt64();
                    if (id <= lastId) return; // the last_match on subscribing, or already in the backfill
                    var time = TimeOf(root.GetProperty("time"));
                    var trade = new Trade(id, time, Dec(root.GetProperty("price")), Dec(root.GetProperty("size")), Aggressor(root.GetProperty("side").GetString() ?? ""));
                    lastId = id;
                    lastTradeTime = time;
                    sink.OnTrades(instrument, new[] { trade });
                    break;
                case "snapshot":
                    book.Clear();
                    foreach (var b in root.GetProperty("bids").EnumerateArray()) book.Set(BookSide.Bid, instrument.ToTicks(Dec(b[0])), Dec(b[1]));
                    foreach (var a in root.GetProperty("asks").EnumerateArray()) book.Set(BookSide.Ask, instrument.ToTicks(Dec(a[0])), Dec(a[1]));
                    sink.OnDepth(instrument, book.BuildUpdate(DateTime.UtcNow, 500, true));
                    lastPublish = DateTime.UtcNow;
                    break;
                case "l2update":
                    foreach (var c in root.GetProperty("changes").EnumerateArray())
                        book.Set(c[0].GetString() == "buy" ? BookSide.Bid : BookSide.Ask, instrument.ToTicks(Dec(c[1])), Dec(c[2]));
                    var now = DateTime.UtcNow;
                    if ((now - lastPublish).TotalMilliseconds >= 90)
                    {
                        sink.OnDepth(instrument, book.BuildUpdate(now, 500, false));
                        lastPublish = now;
                    }

                    break;
                case "error":
                    throw new InvalidOperationException(root.TryGetProperty("message", out var m) ? m.GetString() : "Coinbase refused the subscription");
            }
        }

        // a quiet product can go a while without a match, but the book updates keep coming: a minute of silence is a dead connection
        _ = Task.Run(() => WsRunner.RunAsync(sub, "Coinbase", new Uri(Ws), OnOpen, OnMessage, TimeSpan.FromSeconds(60)));
        return sub;
    }

    public void Dispose() => _http.Dispose();
}
