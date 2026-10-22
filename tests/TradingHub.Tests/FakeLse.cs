using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TradingHub.Tests;

/// <summary>Fake London Strategic Edge: the websocket and the REST vault as the service's public client talks to them (never the real service).</summary>
internal static class FakeLse
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Epoch(DateTime t) => ((t - DateTime.UnixEpoch).TotalSeconds).ToString("0.000", Inv);

    private static string Ts(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fff", Inv) + "Z";

    /// <summary>The service as its public client talks to it: welcome → auth → authenticated, subscribe (with "start" it replays, then goes live).</summary>
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
            if (req.Route == "/catalog")
                return (200, "[{\"dataset\":\"stocks\",\"symbol\":\"AAPL\",\"name\":\"Apple Inc.\",\"ticks\":123,\"first_tick\":\"2003-09-10 13:30:00\",\"last_tick\":\"2026-10-20 20:00:00\"}," +
                             "{\"dataset\":\"fx\",\"symbol\":\"EUR/USD\",\"name\":\"Euro / US Dollar\",\"first_tick\":\"2009-05-01 00:00:00\"},{\"dataset\":\"fx\",\"symbol\":\"USD/JPY\",\"name\":\"Dollar Yen\"},{\"dataset\":\"futures\",\"symbol\":\"ES.F\",\"name\":\"S&P 500 Futures\",\"live\":1},{\"dataset\":\"futures\",\"symbol\":\"FDAX\",\"name\":\"DAX Futures\",\"live\":0},{\"dataset\":\"credit_indices\",\"symbol\":\"CDX\",\"name\":\"credit\"},{\"dataset\":\"fx_derivatives\",\"symbol\":\"EURUSD1M\",\"name\":\"fwd\"}," +
                             "{\"dataset\":\"crypto\",\"symbol\":\"BTC/USD\",\"name\":\"Bitcoin\",\"first_tick\":\"2017-01-01 00:00:00\"}," +
                             "{\"dataset\":\"economics\",\"symbol\":\"cpi_yoy\",\"name\":\"US CPI\"},{\"dataset\":\"bonds\",\"symbol\":\"US10Y\",\"name\":\"US 10y\"},{\"dataset\":\"options\",\"symbol\":\"AAPL251219C00300000\",\"name\":\"opt\"}]");
            if (req.Route == "/usage") return (200, "{\"used\":3,\"limit\":10}");
            if (req.Route == "/candles")
            {
                if (req.Query("symbol") != "AAPL" || req.Query("timeframe") != "5m") return (400, "{\"detail\":\"bad filter\"}");
                // two pages: 5000 rows from the requested start, then 3 (the feed asks for the next page from just after the last row)
                var start = DateTime.Parse(req.Query("start"), Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                bool second = start > new DateTime(2026, 1, 6, 0, 0, 0, DateTimeKind.Utc);
                int n = second ? 3 : 5000;
                var from = second ? start : new DateTime(2026, 1, 5, 14, 30, 0, DateTimeKind.Utc);
                var sb = new StringBuilder("[");
                for (int i = 0; i < n; i++)
                    sb.Append(i > 0 ? "," : "").Append(string.Create(Inv, $"{{\"ts\":\"{from.AddMinutes(5 * i):yyyy-MM-dd HH:mm:ss}\",\"open\":{200 + i % 7},\"high\":{201 + i % 7},\"low\":{199 + i % 7},\"close\":\"{200.5 + i % 7}\",\"volume\":{1000 + i}}}"));
                return (200, sb.Append(']').ToString());
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
                    await FakeHttpServer.SendText(ws, ok ? "{\"type\":\"authenticated\",\"tier\":\"free\",\"symbols\":[]}" : "{\"type\":\"error\",\"code\":\"INVALID_KEY\",\"message\":\"bad key\"}", ct);
                }
                else if (action == "subscribe")
                {
                    var now = DateTime.UtcNow;
                    if (doc.RootElement.TryGetProperty("start", out _))
                    {
                        await FakeHttpServer.SendText(ws, "{\"type\":\"replay_started\"}", ct);
                        // like the real service, "bid" is the trade price and "ask" one step above it: they say nothing about the side, only the price moves do
                        await FakeHttpServer.SendText(ws, $"{{\"type\":\"tick\",\"symbol\":\"EUR/USD\",\"price\":100.00,\"bid\":100.00,\"ask\":100.01,\"volume\":2,\"ts\":{Epoch(now.AddMinutes(-50))},\"replay\":true}}", ct);
                        await FakeHttpServer.SendText(ws, $"{{\"type\":\"tick\",\"symbol\":\"EUR/USD\",\"price\":100.05,\"bid\":100.05,\"ask\":100.06,\"volume\":3,\"ts\":{Epoch(now.AddMinutes(-40))},\"replay\":true}}", ct);
                        await FakeHttpServer.SendText(ws, $"{{\"type\":\"tick\",\"symbol\":\"EUR/USD\",\"price\":99.95,\"bid\":99.95,\"ask\":99.96,\"ts\":{Epoch(now.AddMinutes(-30))},\"replay\":true}}", ct);
                        await FakeHttpServer.SendText(ws, $"{{\"type\":\"tick\",\"symbol\":\"EUR/USD\",\"price\":99.94,\"ts\":{Epoch(now.AddMinutes(-20))},\"replay\":true}}", ct);
                        await FakeHttpServer.SendText(ws, "{\"type\":\"replay_complete\"}", ct);
                    }

                    for (int i = 0; i < 3; i++)
                    {
                        await FakeHttpServer.SendText(ws, $"{{\"type\":\"tick\",\"symbol\":\"EUR/USD.ALIAS\",\"price\":{100.10 + i * 0.02:0.00},\"bid\":{100.10 + i * 0.02:0.00},\"ask\":{100.11 + i * 0.02:0.00},\"volume\":1,\"ts\":\"{Ts(DateTime.UtcNow)}\",\"replay\":false}}", ct);
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
