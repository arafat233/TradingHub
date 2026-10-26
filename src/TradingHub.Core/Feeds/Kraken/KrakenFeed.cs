using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Kraken;

/// <summary>Public Kraken spot data: tape and book over WebSocket v2, history from the REST trades (1000 per call, by time).</summary>
public sealed class KrakenFeed : IMarketDataFeed
{
    public const string SpotId = "kraken-spot";
    private const string Rest = "https://api.kraken.com/0/public";
    private const string Ws = "wss://ws.kraken.com/v2";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public string Id => SpotId;
    public string DisplayName => "Kraken";
    public bool SupportsHistory => true;
    public bool IsLive => true;

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? decimal.Parse(e.GetString() ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture)
        : e.GetDecimal();

    private static DateTime FromUnix(double seconds) => DateTime.UnixEpoch.AddTicks((long)(seconds * TimeSpan.TicksPerSecond));

    /// <summary>The REST asset list names bitcoin "XBT"; the WebSocket and the REST trades also take "BTC", so the app uses that.</summary>
    private static string SymbolOf(string wsname) => wsname.StartsWith("XBT/", StringComparison.Ordinal) ? "BTC/" + wsname[4..] : wsname;

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{Rest}/AssetPairs", ct).ConfigureAwait(false));
        var list = new List<Instrument>();
        foreach (var p in doc.RootElement.GetProperty("result").EnumerateObject())
        {
            var s = p.Value;
            if (!s.TryGetProperty("wsname", out var ws) || s.GetProperty("status").GetString() != "online") continue;
            string sym = SymbolOf(ws.GetString()!);
            var parts = sym.Split('/');
            decimal tick = s.TryGetProperty("tick_size", out var ts) ? Dec(ts) : (decimal)Math.Pow(10, -s.GetProperty("pair_decimals").GetInt32());
            list.Add(new Instrument
            {
                Symbol = sym,
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = tick,
                VolumeStep = (decimal)Math.Pow(10, -s.GetProperty("lot_decimals").GetInt32()),
                BaseAsset = parts[0],
                QuoteAsset = parts.Length > 1 ? parts[1] : "",
                Description = $"{sym} spot",
            });
        }

        string[] popular = { "BTC/USD", "ETH/USD", "SOL/USD", "XRP/USD", "BTC/EUR" };
        return list.OrderBy(i => Array.IndexOf(popular, i.Symbol) is var k && k >= 0 ? k : 999).ThenBy(i => i.Symbol).ToList();
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        // Kraken's "since" is a time in nanoseconds; every answer carries the "last" value to continue from
        long since = (fromUtc.ToUniversalTime() - DateTime.UnixEpoch).Ticks * 100;
        double fromSec = (fromUtc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds, toSec = (toUtc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
        while (!ct.IsCancellationRequested)
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"{Rest}/Trades?pair={Uri.EscapeDataString(instrument.Symbol)}&since={since}&count=1000", ct).ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.GetProperty("error").GetArrayLength() > 0) throw new InvalidOperationException(root.GetProperty("error")[0].GetString());
            var result = root.GetProperty("result");
            var batch = new List<Trade>();
            foreach (var p in result.EnumerateObject())
            {
                if (p.Name == "last") continue;
                foreach (var t in p.Value.EnumerateArray())
                {
                    var time = FromUnix(t[2].GetDouble());
                    long id = t[6].GetInt64();
                    if (time > toUtc || (fromId.HasValue && id <= fromId.Value)) continue;
                    batch.Add(new Trade(id, time, Dec(t[0]), Dec(t[1]), t[3].GetString() == "b" ? TradeSide.Buy : TradeSide.Sell));
                }
            }

            if (batch.Count > 0 && !onBatch(batch)) return;
            long last = long.Parse(result.GetProperty("last").GetString()!, CultureInfo.InvariantCulture);
            double lastSec = last / 1e9;
            progress?.Report(Math.Clamp((lastSec - fromSec) / Math.Max(1, toSec - fromSec), 0, 1));
            // the end of the range, or the end of the data (a short answer, or no progress)
            if (lastSec >= toSec || last <= since || batch.Count == 0) break;
            since = last;
        }

        progress?.Report(1);
    }

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("Kraken", (st, msg) => sink.OnStatus(instrument, st, msg));
        var book = new OrderBookBuilder();
        DateTime lastTradeTime = DateTime.MinValue, lastPublish = DateTime.MinValue;
        long lastId = 0;
        string sym = instrument.Symbol;

        async Task OnOpen(ClientWebSocket ws, CancellationToken token)
        {
            book.Clear();
            string Msg(string channel, string extra) => $"{{\"method\":\"subscribe\",\"params\":{{\"channel\":\"{channel}\",\"symbol\":[\"{sym}\"]{extra}}}}}";
            await ws.SendAsync(Encoding.UTF8.GetBytes(Msg("trade", ",\"snapshot\":false")), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            await ws.SendAsync(Encoding.UTF8.GetBytes(Msg("book", ",\"depth\":100")), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
            // fill what was missed during a drop
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
            if (!root.TryGetProperty("channel", out var chEl) || !root.TryGetProperty("data", out var data)) return;
            string channel = chEl.GetString() ?? "";
            if (channel == "trade")
            {
                var trades = new List<Trade>();
                foreach (var t in data.EnumerateArray())
                {
                    long id = t.TryGetProperty("trade_id", out var tid) ? tid.GetInt64() : 0;
                    var time = DateTime.Parse(t.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                    if (id != 0 && id <= lastId) continue; // already came in the backfill
                    trades.Add(new Trade(id, time, Dec(t.GetProperty("price")), Dec(t.GetProperty("qty")), t.GetProperty("side").GetString() == "buy" ? TradeSide.Buy : TradeSide.Sell));
                }

                if (trades.Count == 0) return;
                lastTradeTime = trades[^1].Time;
                lastId = Math.Max(lastId, trades[^1].Id);
                sink.OnTrades(instrument, trades);
            }
            else if (channel == "book")
            {
                bool snapshot = root.GetProperty("type").GetString() == "snapshot";
                if (snapshot) book.Clear();
                foreach (var d in data.EnumerateArray())
                {
                    foreach (var b in d.GetProperty("bids").EnumerateArray()) book.Set(BookSide.Bid, instrument.ToTicks(Dec(b.GetProperty("price"))), Dec(b.GetProperty("qty")));
                    foreach (var a in d.GetProperty("asks").EnumerateArray()) book.Set(BookSide.Ask, instrument.ToTicks(Dec(a.GetProperty("price"))), Dec(a.GetProperty("qty")));
                }

                var now = DateTime.UtcNow;
                if (snapshot || (now - lastPublish).TotalMilliseconds >= 90)
                {
                    sink.OnDepth(instrument, book.BuildUpdate(now, 500, snapshot));
                    lastPublish = now;
                }
            }
        }

        // a heartbeat arrives every second while subscribed, so a minute of silence is a dead connection
        _ = Task.Run(() => WsRunner.RunAsync(sub, "Kraken", new Uri(Ws), OnOpen, OnMessage, TimeSpan.FromSeconds(60)));
        return sub;
    }

    public void Dispose() => _http.Dispose();
}
