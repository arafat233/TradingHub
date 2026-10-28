namespace TradingHub.Tests;

/// <summary>
/// A fake Ironbeam service with the shapes of its published API: POST /auth, GET /stream/create, the GET subscribe calls and the stream's
/// WebSocket. It sends a two-level book, some trades (one of them to skip) and an update that only carries the levels that moved.
/// Used by <see cref="IronbeamTests"/> and the connections scene of UiSnapshot.
/// </summary>
internal static class FakeIronbeam
{
    public const string EsSymbol = "XCME:ES.Z26";

    public static FakeHttpServer Server(out List<string> log, bool badLogin = false, bool depthRefused = false)
    {
        var got = new List<string>();
        log = got;
        var s = new FakeHttpServer();
        s.OnHttp = req =>
        {
            lock (got) got.Add($"{req.Method} {req.Path} | {req.Headers.GetValueOrDefault("Authorization")} | {req.Body}");
            if (req.Route == "/v2/auth")
                return badLogin ? (401, "{\"status\":\"ERROR\",\"message\":\"Invalid API key\"}") : (200, "{\"status\":\"OK\",\"token\":\"tok-1\"}");
            if (req.Route == "/v2/stream/create") return (200, "{\"status\":\"OK\",\"streamId\":\"s-1\"}");
            if (req.Route == "/v2/market/trades/subscribe/s-1") return (200, "{\"status\":\"OK\"}");
            if (req.Route == "/v2/market/depths/subscribe/s-1") return depthRefused ? (403, "{\"message\":\"Depth is not enabled for this account\"}") : (200, "{\"status\":\"OK\"}");
            return (404, "{}");
        };
        s.OnSocket = async (path, ws, ct) =>
        {
            lock (got) got.Add("WS " + path);
            // the client subscribes after it connects: wait for both calls before sending data
            await Task.Run(async () =>
            {
                for (int i = 0; i < 100; i++)
                {
                    bool both;
                    lock (got) both = got.Any(l => l.Contains("/market/trades/subscribe/")) && got.Any(l => l.Contains("/market/depths/subscribe/"));
                    if (both) return;
                    await Task.Delay(30, ct);
                }
            }, ct);
            await FakeHttpServer.SendText(ws, "{\"p\":{\"ping\":\"ping\"}}", ct);
            // the book: two bid levels and two ask levels (level 0 is the best)
            await FakeHttpServer.SendText(ws, "{\"d\":[{\"s\":\"" + EsSymbol + "\",\"b\":[{\"l\":0,\"t\":1793008990000,\"s\":0,\"p\":5000.25,\"o\":3,\"sz\":12},{\"l\":1,\"s\":0,\"p\":5000.0,\"o\":5,\"sz\":20}]," +
                                            "\"a\":[{\"l\":0,\"s\":1,\"p\":5000.5,\"o\":2,\"sz\":7},{\"l\":1,\"s\":1,\"p\":5000.75,\"o\":1,\"sz\":9}]}]}", ct);
            await Task.Delay(100, ct);
            // trades: a buy, a sell, one with no side; a settlement price, a cancelled trade and another contract are skipped
            await FakeHttpServer.SendText(ws, "{\"tr\":[" +
                                              "{\"s\":\"" + EsSymbol + "\",\"p\":5000.5,\"sz\":3,\"st\":1793008990000,\"as\":1,\"tid\":1}," +
                                              "{\"s\":\"" + EsSymbol + "\",\"p\":5000.25,\"sz\":2,\"st\":1793008990100,\"as\":2,\"tid\":2}," +
                                              "{\"s\":\"" + EsSymbol + "\",\"p\":5000.25,\"sz\":1,\"st\":1793008990200,\"as\":0,\"tid\":3}," +
                                              "{\"s\":\"" + EsSymbol + "\",\"p\":4999.0,\"sz\":5,\"is\":true}," +
                                              "{\"s\":\"" + EsSymbol + "\",\"p\":5000.0,\"sz\":4,\"clx\":true}," +
                                              "{\"s\":\"XCME:NQ.Z26\",\"p\":1.0,\"sz\":1,\"as\":1}]}", ct);
            await Task.Delay(100, ct);
            // a change that only carries the levels that moved: the best bid is gone, the second ask shrank
            await FakeHttpServer.SendText(ws, "{\"d\":[{\"s\":\"" + EsSymbol + "\",\"b\":[{\"l\":0,\"s\":0,\"p\":5000.25,\"sz\":0}],\"a\":[{\"l\":1,\"s\":1,\"p\":5000.75,\"sz\":4}]}]}", ct);
            while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                if (await FakeHttpServer.ReceiveText(ws, ct) == null) return;
        };
        return s;
    }
}
