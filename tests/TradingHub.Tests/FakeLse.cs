using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TradingHub.Tests;

/// <summary>Fake London Strategic Edge: the websocket and the Data 3.0 REST API as the service's public client talks to them (never the real service).</summary>
internal static class FakeLse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Live ticks carry nanosecond ISO times: "2026-10-26T08:31:38.241000000Z".</summary>
    private static string Ts(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fff", Inv) + "000000Z";

    /// <summary>The service as its public client talks to it: welcome → auth → authenticated (with the symbol list), subscribe → live ticks.</summary>
    /// <summary>With <paramref name="oneConnection"/> the server refuses a second websocket like the free plan does; a refusal is noted as "REJECTED" in <paramref name="received"/>.</summary>
    public static FakeHttpServer Server(out List<string> received, bool oneConnection = false)
    {
        int open = 0;
        var got = new List<string>();
        received = got;
        var s = new FakeHttpServer();
        s.OnHttp = req =>
        {
            if (req.Headers.GetValueOrDefault("x-api-key") != "KEY") return (401, "{\"detail\":\"invalid key\"}");
            if (req.Route == "/v3/usage") return (200, "{\"plan\":\"free\",\"calls_per_minute\":60,\"weekly\":{\"allowance_bytes\":13421772800,\"used_bytes\":14042}}");
            if (req.Route == "/v3/pairs")
            {
                // the archive spells the symbols its own way: EUR_USD in forex, BTCUSDT in crypto, GOLD in several stock markets and futures
                var (folder, symbol) = (req.Query("market"), req.Query("symbol")) switch
                {
                    ("us_stocks" or null, "AAPL") => ("us_stocks", "AAPL"),
                    ("forex" or null, "EUR_USD") => ("forex", "EUR_USD"),
                    ("crypto" or null, "BTCUSDT") => ("crypto", "BTCUSDT"),
                    ("futures" or null, "GOLD") => ("futures", "GOLD"),
                    _ => ("", ""),
                };
                return (200, folder.Length == 0 ? "{\"count\":0,\"pairs\":[],\"total\":0}" : $"{{\"count\":1,\"total\":1,\"pairs\":[{{\"folder\":\"{folder}\",\"symbol\":\"{symbol}\"}}]}}");
            }

            if (req.Route == "/v3/export" && req.Query("timeframe") == "tick")
            {
                if (req.Query("market") != "futures" || req.Query("symbol") != "GOLD") return (400, "{\"message\":\"bad filter\"}");
                var t0 = DateTime.Parse(req.Query("from"), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                // trades and a quote: only the trades are history; the times have 9 decimals like the real export
                string Row(int sec, string price, int size, string kind) => $"\"{t0.AddSeconds(sec):yyyy-MM-dd HH:mm:ss}.123456000\",{price},{size},\"{kind}\"\n";
                return (200, "\"time\",\"price\",\"size\",\"kind\"\n" + Row(1, "100", 2, "trade") + Row(2, "100.1", 0, "quote") + Row(3, "100.2", 3, "trade") + Row(4, "100.1", 1, "trade"));
            }

            if (req.Route == "/v3/export")
            {
                if (req.Query("market") != "us_stocks" || req.Query("symbol") != "AAPL" || req.Query("timeframe") != "5m" || req.Query("format") != "csv") return (400, "{\"message\":\"bad filter\"}");
                var from = DateTime.Parse(req.Query("from"), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                // the first window has candles, the later one nothing stored (like a holiday stretch): the feed skips it
                if (from > new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)) return (502, "{\"code\":\"export-failed\",\"message\":\"Omega has no stored data for this selection\"}");
                var sb = new StringBuilder("\"time\",\"open\",\"high\",\"low\",\"close\",\"volume\",\"trades\"\n");
                for (int i = 0; i < 3; i++)
                    sb.Append(string.Create(Inv, $"\"{from.AddMinutes(5 * i):yyyy-MM-dd HH:mm:ss}\",{200 + i},{201 + i},{199 + i},{200.5 + i},{1000 + i},12\n"));
                return (200, sb.ToString());
            }

            return (404, "{}");
        };
        s.OnSocket = async (path, ws, ct) =>
        {
            await FakeHttpServer.SendText(ws, "{\"type\":\"welcome\"}", ct);
            bool refused = false;
            if (oneConnection && Interlocked.Increment(ref open) > 1) refused = true;
            try
            {
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var text = await FakeHttpServer.ReceiveText(ws, ct);
                if (text == null)
                {
                    try { await ws.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "", ct); } catch (Exception) { } // answer the close like a real server
                    return;
                }

                lock (got) got.Add(text);
                using var doc = JsonDocument.Parse(text);
                string action = doc.RootElement.GetProperty("action").GetString()!;
                if (action == "auth" && refused)
                {
                    lock (got) got.Add("REJECTED");
                    await FakeHttpServer.SendText(ws, "{\"type\":\"error\",\"code\":\"CONNECTION_LIMIT\",\"message\":\"Max 1 concurrent websocket(s) for this API key; 1 already open from this same address. Close it and retry.\"}", ct);
                    return;
                }

                if (action == "auth")
                {
                    bool ok = doc.RootElement.GetProperty("api_key").GetString() == "KEY";
                    await FakeHttpServer.SendText(ws, ok ? "{\"type\":\"authenticated\",\"tier\":\"registered\",\"symbols\":[{\"symbol\":\"AAPL\",\"name\":\"Apple Inc.\",\"category\":\"Stocks\"},{\"symbol\":\"EUR/USD\",\"name\":\"Euro / US Dollar\",\"category\":\"Forex\"},{\"symbol\":\"USD/JPY\",\"name\":\"Dollar Yen\",\"category\":\"Forex\"},{\"symbol\":\"BTC/USD\",\"name\":\"Bitcoin\",\"category\":\"Crypto\"},{\"symbol\":\"XAU/USD\",\"name\":\"Gold\",\"category\":\"Commodities\"},{\"symbol\":\"USB02Y/USD\"}]}" : "{\"type\":\"error\",\"code\":\"INVALID_KEY\",\"message\":\"bad key\"}", ct);
                }
                else if (action == "subscribe")
                {
                    for (int i = 0; i < 3; i++)
                    {
                        await FakeHttpServer.SendText(ws, $"{{\"type\":\"tick\",\"symbol\":\"EUR/USD.ALIAS\",\"price\":{100.10 + i * 0.02:0.00},\"bid\":{100.10 + i * 0.02:0.00},\"ask\":{100.11 + i * 0.02:0.00},\"volume\":1,\"ts\":\"{Ts(DateTime.UtcNow)}\"}}", ct);
                        await Task.Delay(40, ct);
                    }
                }
                else if (action == "ping") await FakeHttpServer.SendText(ws, "{\"type\":\"pong\"}", ct);
            }
            }
            finally
            {
                if (oneConnection) Interlocked.Decrement(ref open);
            }
        };
        return s;
    }

}
