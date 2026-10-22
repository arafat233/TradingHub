using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingHub.Tests;

/// <summary>
/// Fake dxFeed server (dxLink over WebSocket, the public specification): SETUP, token, FEED channel with TimeAndSale
/// and DOM channel with order book snapshots. It never touches a real dxFeed account.
/// </summary>
internal sealed class FakeDxLinkServer : IDisposable
{
    private HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public string Uri { get; }
    public string Token { get; set; } = "good-token";

    /// <summary>The only DOM source it has; any other gets an ERROR on that channel.</summary>
    public string DomSource { get; set; } = "glbx";

    /// <summary>Events sent when the TimeAndSale subscription arrives (FULL format).</summary>
    public List<JsonObject> Trades { get; } = new();

    /// <summary>DOM snapshots sent after DOM_SETUP, one after the other.</summary>
    public List<(double[][] Bids, double[][] Asks)> Books { get; } = new();

    /// <summary>Every message received from the client.</summary>
    public List<JsonObject> Received { get; } = new();

    public FakeDxLinkServer()
    {
        var rnd = new Random();
        for (int i = 0; ; i++)
        {
            int port = rnd.Next(45000, 60000);
            try
            {
                _listener.Close();
                _listener = new HttpListener(); // a failed Start (the port is taken by another test) leaves the listener unusable
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Uri = $"ws://127.0.0.1:{port}/dxlink-ws";
                break;
            }
            catch (HttpListenerException) when (i < 20)
            {
            }
        }

        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            var wsc = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
            _ = Task.Run(() => Serve(wsc.WebSocket));
        }
    }

    private static Task Send(WebSocket ws, object msg) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(msg), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task Serve(WebSocket ws)
    {
        var buf = new byte[1 << 16];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, _cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) return;
                var m = JsonNode.Parse(Encoding.UTF8.GetString(buf, 0, r.Count))!.AsObject();
                lock (Received) Received.Add(m);
                int channel = (int)m["channel"]!;
                switch ((string?)m["type"])
                {
                    case "SETUP":
                        await Send(ws, new { type = "SETUP", channel = 0, keepaliveTimeout = 60, acceptKeepaliveTimeout = 60, version = "1.0-fake" });
                        await Send(ws, new { type = "AUTH_STATE", channel = 0, state = "UNAUTHORIZED" });
                        break;
                    case "AUTH":
                        await Send(ws, new { type = "AUTH_STATE", channel = 0, state = (string?)m["token"] == Token ? "AUTHORIZED" : "UNAUTHORIZED" });
                        break;
                    case "CHANNEL_REQUEST" when (string?)m["service"] == "DOM" && (string?)m["parameters"]!["sources"]![0] != DomSource:
                        // what the real demo server answers to a source it does not have
                        await Send(ws, new { type = "ERROR", channel, error = "BAD_ACTION", message = $"Unsupported source: '{(string?)m["parameters"]!["sources"]![0]}'" });
                        await Send(ws, new { type = "CHANNEL_CLOSED", channel });
                        break;
                    case "CHANNEL_REQUEST":
                        await Send(ws, new { type = "CHANNEL_OPENED", channel, service = (string?)m["service"], parameters = m["parameters"] });
                        break;
                    case "FEED_SUBSCRIPTION":
                        await Send(ws, new { type = "FEED_CONFIG", channel, dataFormat = "FULL", aggregationPeriod = 0.1 });
                        await Send(ws, new JsonObject { ["type"] = "FEED_DATA", ["channel"] = channel, ["data"] = new JsonArray(Trades.Select(t => (JsonNode)t.DeepClone()).ToArray()) });
                        break;
                    case "DOM_SETUP":
                        await Send(ws, new { type = "DOM_CONFIG", channel, aggregationPeriod = 0.1, depthLimit = 50, dataFormat = "FULL", orderFields = new[] { "price", "size" } });
                        foreach (var (bids, asks) in Books)
                        {
                            await Send(ws, new
                            {
                                type = "DOM_SNAPSHOT", channel, time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                bids = bids.Select(b => new { price = b[0], size = b[1] }), asks = asks.Select(a => new { price = a[0], size = a[1] }),
                            });
                            await Task.Delay(150);
                        }

                        break;
                }
            }
        }
        catch
        {
            // the client closed: nothing to do
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
        }
    }
}
