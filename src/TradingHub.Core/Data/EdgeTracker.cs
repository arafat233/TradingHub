using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>One setup the decision panel called, and (once 15 minutes have passed) how it turned out after costs, in ticks.</summary>
public sealed class EdgeSetup
{
    public DateTime Time { get; set; }

    /// <summary>+1 buy setup, -1 sell setup.</summary>
    public int Side { get; set; }

    public long EntryTicks { get; set; }

    /// <summary>The result after costs, in ticks (null until scored).</summary>
    public double? ResultTicks { get; set; }
}

/// <summary>
/// Keeps score of the decision panel against the market. Each time it calls a setup (all checks pass) it is written down; 15 minutes later the
/// price at that moment is read from the tape and the result is the move in the setup's direction minus the round-trip cost. The panel then shows
/// how many setups have been scored and how they did, so the user can see whether it has an edge in this market instead of taking it on faith.
/// Until <see cref="Target"/> setups are scored it says it is still learning. It is per instrument and kept in a small JSON file.
/// </summary>
public sealed class EdgeTracker
{
    public const int Target = 120, MinForVerdict = 30;
    public static readonly TimeSpan Horizon = TimeSpan.FromMinutes(15), Cooldown = TimeSpan.FromMinutes(5);

    private readonly string? _path;
    public List<EdgeSetup> Setups { get; private set; } = new();

    /// <param name="path">Where the setups are kept (null = in memory only, for tests).</param>
    public EdgeTracker(string? path)
    {
        _path = path;
        try
        {
            if (path != null && File.Exists(path)) Setups = JsonSerializer.Deserialize<List<EdgeSetup>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception)
        {
            Setups = new(); // an unreadable file: start again rather than stop the panel
        }
    }

    public int Scored => Setups.Count(s => s.ResultTicks != null);
    public int Pending => Setups.Count - Scored;

    /// <summary>Records a new setup when the verdict calls one (not again within <see cref="Cooldown"/> for the same side) and scores the ones that are due.</summary>
    public void Observe(Verdict verdict, Instrument inst, TradeStore store, DateTime now, decimal costBps)
    {
        bool changed = false;
        if (verdict != Verdict.Wait && store.Count > 0)
        {
            int side = verdict == Verdict.BuySetup ? 1 : -1;
            if (!Setups.Any(s => s.Side == side && now - s.Time < Cooldown && now >= s.Time))
            {
                Setups.Add(new EdgeSetup { Time = now, Side = side, EntryTicks = store.GetPriceTicks(store.Count - 1) });
                changed = true;
            }
        }

        foreach (var s in Setups.Where(s => s.ResultTicks == null && s.Time + Horizon <= now))
        {
            int idx = store.LowerBound(s.Time + Horizon) - 1; // the last trade at or before the horizon
            if (idx < 0 || store.LastTime < s.Time + Horizon) continue; // the tape does not reach that moment (yet)
            double cost = (double)(inst.FromTicks(s.EntryTicks) * costBps / 10_000m / inst.TickSize);
            s.ResultTicks = s.Side * (store.GetPriceTicks(idx) - s.EntryTicks) - cost;
            changed = true;
        }

        if (changed) Save();
    }

    /// <summary>Hit rate (results above zero) and average result in ticks over the scored setups.</summary>
    public (decimal HitRate, decimal AvgTicks) Summary()
    {
        var r = Setups.Where(s => s.ResultTicks != null).Select(s => s.ResultTicks!.Value).ToList();
        return r.Count == 0 ? (0, 0) : ((decimal)r.Count(v => v > 0) / r.Count, (decimal)r.Average());
    }

    /// <summary>One line for the panel.</summary>
    public string Text()
    {
        int n = Scored;
        if (n < MinForVerdict) return $"Edge meter learning: {n} of {Target} scored setups on this market{(Pending > 0 ? $" ({Pending} waiting for their 15 minutes)" : "")}.";
        var (hit, avg) = Summary();
        return $"{n} scored setups: {hit:P0} ended in profit, average {avg:+0.0;-0.0} ticks after costs" + (n < Target ? $" (still learning: {Target} needed)." : ".");
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Setups.TakeLast(2000).ToList()));
        }
        catch (Exception)
        {
            // the score still works in memory; the next change tries again
        }
    }
}
