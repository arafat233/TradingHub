using System.Net.WebSockets;
using System.Text.Json;

namespace TradingHub.Feeds;

/// <summary>
/// The reconnecting WebSocket loop the simple JSON feeds share: connect, let the feed subscribe (and backfill), read messages,
/// reconnect with backoff if it drops or goes quiet. Bybit has its own copy of this with a few more bells.
/// </summary>
internal static class WsRunner
{
    /// <param name="onOpen">Runs on every (re)connection, after connecting: send the subscriptions, reset per-connection state, backfill.</param>
    /// <param name="onMessage">One parsed JSON message.</param>
    /// <param name="stale">No message for this long = the connection is dead, reconnect.</param>
    public static async Task RunAsync(LiveSubscription liveSub, string name, Uri uri, Func<ClientWebSocket, CancellationToken, Task> onOpen,
        Action<JsonElement> onMessage, TimeSpan stale)
    {
        var ct = liveSub.Token;
        int failures = 0, requested = 0;
        bool everConnected = false;
        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            var conn = liveSub.BeginConnection();
            bool receivedAny = false;
            string reason = "the connection dropped";
            try
            {
                if (!everConnected) liveSub.SetChannel("data", FeedStatus.Connecting, $"Connecting to {name}…");
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(uri, connectCts.Token).ConfigureAwait(false);
                }

                await onOpen(ws, conn.Token).ConfigureAwait(false);
                liveSub.SetChannel("data", FeedStatus.Connected, null);
                everConnected = true;
                conn.CancelAfter(stale);

                var buf = new byte[1 << 16];
                var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buf, conn.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        reason = "the server closed the connection";
                        break;
                    }

                    receivedAny = true;
                    failures = 0;
                    conn.CancelAfter(stale);
                    using var doc = JsonDocument.Parse(ms.ToArray());
                    onMessage(doc.RootElement);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = liveSub.WasRequested(ref requested) ? "reconnection requested" : receivedAny ? "no data was arriving" : "it did not respond in time";
                if (!receivedAny) failures++;
            }
            catch (Exception ex)
            {
                failures++;
                reason = ex.Message;
            }
            finally
            {
                liveSub.EndConnection(conn);
            }

            if (ct.IsCancellationRequested) break;
            var wait = LiveSubscription.Backoff(failures);
            liveSub.SetChannel("data", FeedStatus.Reconnecting, failures == 0
                ? $"Reconnecting ({reason})…"
                : $"Reconnecting in {wait.TotalSeconds:0} s · attempt {failures} ({reason})");
            await Task.Delay(wait, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        liveSub.SetChannel("data", FeedStatus.Disconnected, null);
    }
}
