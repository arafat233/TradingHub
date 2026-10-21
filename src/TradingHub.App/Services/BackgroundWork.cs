using System.Collections.Concurrent;

namespace TradingHub.App.Services;

/// <summary>
/// Long jobs a restart would cut off (for example downloading months of history, which is paid for per day). While there is
/// one, Trading Hub does not reinstall itself: the upgrade waits for it to finish.
/// </summary>
internal static class BackgroundWork
{
    private static readonly ConcurrentDictionary<int, string> Jobs = new();
    private static int _next;

    /// <summary>Records the job until what it returns is released (with using).</summary>
    public static IDisposable Begin(string what)
    {
        int id = Interlocked.Increment(ref _next);
        Jobs[id] = what;
        return new Token(id);
    }

    /// <summary>What is running right now ("downloading NQ history from Databento").</summary>
    public static IReadOnlyList<string> Active => Jobs.OrderBy(j => j.Key).Select(j => j.Value).ToList();

    private sealed class Token(int id) : IDisposable
    {
        public void Dispose() => Jobs.TryRemove(id, out _);
    }
}
