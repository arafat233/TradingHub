namespace TradingHub.Data;

/// <summary>
/// When the PC is running out of memory (an 8 GB machine with a browser and an editor open), the app gives some back instead of waiting to be killed:
/// it shortens how much book history and flow history it keeps in memory, and compacts. It never touches the trades, the recordings or the charts.
/// This is only the decision; the app applies it (see <see cref="Apply"/>).
/// </summary>
public static class MemoryGuard
{
    /// <summary>Memory use of the whole machine, as a share (0 to 1), at which the app starts giving memory back, and the lower share at which it stops.</summary>
    public const double High = 0.92, Low = 0.85;

    /// <summary>The next state: low memory from 92% use on, until it falls back under 85% (so it does not flip on and off around one number).</summary>
    public static bool Decide(double memoryLoad, bool wasLow) => wasLow ? memoryLoad > Low : memoryLoad >= High;

    /// <summary>The machine's memory use now (0 to 1), or null if it cannot be read.</summary>
    public static double? CurrentLoad()
    {
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0 ? (double)info.MemoryLoadBytes / info.TotalAvailableMemoryBytes : null;
    }

    /// <summary>Gives memory back (<paramref name="low"/> true) or returns to the normal amounts (false) for every open instrument, and compacts when giving back.</summary>
    public static void Apply(IEnumerable<InstrumentData> instruments, bool low)
    {
        foreach (var d in instruments)
        {
            d.DepthHistory.SetMaxAge(low ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(30)); // the heatmap keeps 5 minutes of book instead of 30
            d.Insights.Flow.Shrink(low ? TimeSpan.FromHours(4) : TimeSpan.FromHours(26));          // the flow tracker 4 hours instead of a day
        }

        if (low)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }
}
