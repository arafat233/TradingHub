using TradingHub.Data;
using TradingHub.Market;
using Xunit.Abstractions;

namespace TradingHub.Tests;

/// <summary>Runs the tape iceberg finder over stored LSE ticks and prints what it flags. Set TAPEICE_ROOT (the Ticks folder), TAPEICE_SYMBOL, TAPEICE_TICK.</summary>
public class TapeIcebergLive(ITestOutputHelper output)
{
    [Fact]
    public void Prints_the_icebergs_found_in_stored_ticks()
    {
        var root = Environment.GetEnvironmentVariable("TAPEICE_ROOT");
        if (root == null) return;
        var symbol = Environment.GetEnvironmentVariable("TAPEICE_SYMBOL") ?? "GC.F";
        var tick = decimal.Parse(Environment.GetEnvironmentVariable("TAPEICE_TICK") ?? "0.1", System.Globalization.CultureInfo.InvariantCulture);
        var days = Directory.GetFiles(Path.Combine(root, "lse", symbol), "*.vtk").OrderBy(f => f).TakeLast(5).ToList();
        foreach (var f in days)
        {
            var d = new TapeIcebergDetector();
            var all = new List<IcebergEvent>();
            int n = 0;
            foreach (var t in TickFile.Read(f))
            {
                n++;
                d.OnTrade(t.Time, (long)Math.Round(t.Price / tick), t.Volume, t.Side == TradeSide.Buy);
                if (d.Events.Count > all.Count) all.AddRange(d.Events.Skip(all.Count));
            }

            output.WriteLine($"{Path.GetFileNameWithoutExtension(f)}: {n:N0} ticks, {all.Count} icebergs ({all.Count(e => e.Ask)} resting sells, {all.Count(e => !e.Ask)} resting buys)");
            foreach (var e in all.Take(6)) output.WriteLine($"   {e.Start:HH:mm:ss} {(e.Ask ? "SELL" : "BUY ")} at {e.Ticks * tick} absorbed {e.Volume} over {(e.Last - e.Start).TotalSeconds:0}s");
        }
    }
}
