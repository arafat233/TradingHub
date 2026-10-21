namespace TradingHub.Feeds;

/// <summary>Subscription that can force an immediate reconnect (whatever was missed backfills itself).</summary>
public interface IReconnectable
{
    void Reconnect();
}

/// <summary>
/// Control of a live subscription with one or more channels (for example the tape and the book, which on
/// Binance Futures go over separate connections). It merges the channels' state into a single one for the UI
/// (the worst one wins) and allows dropping the current connections so they reconnect right away.
/// </summary>
public sealed class LiveSubscription : IDisposable, IReconnectable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<CancellationTokenSource> _connections = new();
    private readonly Dictionary<string, (FeedStatus Status, string? Message)> _channels = new();
    private readonly Action<FeedStatus, string?> _report;
    private readonly string _name;
    private FeedStatus _lastStatus = (FeedStatus)(-1);
    private string? _lastMessage;

    public LiveSubscription(string name, Action<FeedStatus, string?> report)
    {
        _name = name;
        _report = report;
    }

    /// <summary>Cancelled when the subscription is closed.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>Token of one connection: cancelled when the subscription is closed or when a reconnect is requested.</summary>
    public CancellationTokenSource BeginConnection()
    {
        var c = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        lock (_gate) _connections.Add(c);
        return c;
    }

    public void EndConnection(CancellationTokenSource c)
    {
        lock (_gate) _connections.Remove(c);
        c.Dispose();
    }

    private int _manual;

    /// <summary>True (once per channel and request) if the last drop was requested by the user.</summary>
    public bool WasRequested(ref int seen) => Volatile.Read(ref _manual) != seen && (seen = Volatile.Read(ref _manual)) >= 0;

    /// <summary>Drops the current connections: each channel reconnects right away and backfills the gap.</summary>
    public void Reconnect()
    {
        Interlocked.Increment(ref _manual);
        lock (_gate)
        {
            foreach (var c in _connections)
            {
                try
                {
                    c.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    /// <summary>State of one channel; the UI receives the worst of them all.</summary>
    public void SetChannel(string channel, FeedStatus status, string? message)
    {
        FeedStatus overall;
        string? msg;
        lock (_gate)
        {
            _channels[channel] = (status, message);
            var worst = _channels.Values.MaxBy(v => Rank(v.Status));
            overall = worst.Status;
            msg = overall == FeedStatus.Connected ? _name + " connected" : worst.Message;
            if (overall == _lastStatus && msg == _lastMessage) return;
            _lastStatus = overall;
            _lastMessage = msg;
        }

        _report(overall, msg);
    }

    private static int Rank(FeedStatus s) => s switch
    {
        FeedStatus.Connected => 0,
        FeedStatus.Disconnected => 1,
        FeedStatus.Connecting => 2,
        FeedStatus.Reconnecting => 3,
        _ => 4,
    };

    /// <summary>Wait between attempts: the first one right away and then longer each time (up to 30 s).</summary>
    public static TimeSpan Backoff(int failures) =>
        failures <= 0 ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, failures - 1)));

    public void Dispose() => _cts.Cancel();
}
