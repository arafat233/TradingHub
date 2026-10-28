using System.Text;
using TradingHub.Data;
using TradingHub.Market;
using Xunit.Abstractions;

namespace TradingHub.Tests;

/// <summary>
/// Scores <see cref="IcebergDetector"/> against a made-up market where the answer is known. Each episode is one price level on the ask
/// with buyers hitting it as a Poisson stream; the detector sees only the trades and the level size on a 250 ms grid (what the app's
/// depth history gives it). Five kinds of level: a native iceberg (refilled by the exchange, keeps its place), a synthetic one (new order
/// after each fill, back of the queue), an ordinary resting order, a market maker that re-quotes the same size after every fill (looks
/// exactly like a synthetic iceberg, so it is the floor on false positives), and a busy level where unrelated orders keep arriving.
/// The mix and the parameters are my assumptions, so the numbers show how the rules behave, not how often they are right on a real market.
/// </summary>
public class IcebergSimulation(ITestOutputHelper output)
{
    private enum Kind { Native, Synthetic, Ordinary, MarketMaker, Busy }

    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "sim", TickSize = 0.25m, VolumeStep = 1 };
    private static readonly Dictionary<(long, decimal, long), DepthSnapshot> Snaps = new();

    private static DepthSnapshot Snap(long ticks, decimal size, long picture)
    {
        if (Snaps.TryGetValue((ticks, size, picture), out var s)) return s;
        var b = new OrderBookBuilder();
        b.Set(BookSide.Bid, ticks - 4, 5);
        if (size > 0) b.Set(BookSide.Ask, ticks, size);
        return Snaps[(ticks, size, picture)] = b.BuildUpdate(DateTime.UtcNow, 10, true).Snapshot;
    }

    private static double Exp(Random r, double mean) => -mean * Math.Log(1 - r.NextDouble());

    private sealed class Ord(decimal size, bool mine) { public decimal Size = size; public readonly bool Mine = mine; }

    /// <summary>One episode: the trades (ms, volume) and the size shown at the level over time (ms, size).</summary>
    private static (List<(double Ms, decimal Vol)> Trades, List<(double Ms, decimal Size)> Shown, int Refills) Run(Kind kind, Random rng, double tapePerSec)
    {
        const double Length = 120_000;
        var queue = new List<Ord>();         // resting orders at the level, front first; Mine = the order the kind is about
        var trades = new List<(double, decimal)>();
        var shown = new List<(double, decimal)>();
        decimal peak = 0, hidden = 0;
        double mmLife = 0;
        int refills = 0;

        void Behind() { for (int i = rng.Next(0, 3); i > 0; i--) queue.Add(new Ord(rng.Next(5, 30), false)); }
        switch (kind)
        {
            case Kind.Native or Kind.Synthetic:
                peak = new[] { 5, 10, 25, 50 }[rng.Next(4)];
                hidden = peak * rng.Next(3, 20) - peak;
                queue.Add(new Ord(peak, true)); Behind();
                break;
            case Kind.Ordinary:
                queue.Add(new Ord(Math.Max(1, (decimal)Math.Round(Math.Exp(rng.NextDouble() * 2.2 + 1.8))), false)); Behind();
                break;
            case Kind.MarketMaker:
                peak = new[] { 2, 5, 10, 25 }[rng.Next(4)];
                queue.Add(new Ord(peak, true)); Behind();
                mmLife = 20_000 + Exp(rng, 40_000);
                break;
            case Kind.Busy:
                for (int i = rng.Next(2, 6); i > 0; i--) queue.Add(new Ord(rng.Next(3, 40), false));
                break;
        }

        var pending = new List<(double At, Ord Order, bool Front)>(); // refills not yet in the book
        double nextBuyer = Exp(rng, 1000 / tapePerSec), nextArrival = Exp(rng, 1000), t = 0;
        shown.Add((0, queue.Sum(o => o.Size)));
        while (true)
        {
            double nextPending = pending.Count > 0 ? pending.Min(p => p.At) : double.MaxValue;
            double nextOther = kind == Kind.Busy ? nextArrival : double.MaxValue;
            double next = Math.Min(nextBuyer, Math.Min(nextPending, nextOther));
            if (next >= Length) break;
            t = next;
            if (next == nextPending)
            {
                var p = pending.First(x => x.At == nextPending);
                pending.Remove(p);
                if (kind == Kind.MarketMaker && t > mmLife) { shown.Add((t, queue.Sum(o => o.Size))); continue; } // the market maker has gone
                if (p.Front) queue.Insert(0, p.Order); else queue.Add(p.Order);
                refills++;
            }
            else if (next == nextOther)
            {
                queue.Add(new Ord(Math.Max(1, (decimal)Math.Round(Math.Exp(rng.NextDouble() * 2 + 1.5))), false));
                nextArrival = t + Exp(rng, 1000);
            }
            else
            {
                nextBuyer = t + Exp(rng, 1000 / tapePerSec);
                decimal want = Math.Max(1, (decimal)Math.Round(Exp(rng, 4))), got = 0;
                bool mineFilled = false;
                while (want > 0 && queue.Count > 0)
                {
                    var front = queue[0];
                    decimal take = Math.Min(want, front.Size);
                    front.Size -= take; want -= take; got += take;
                    if (front.Size > 0) break;
                    queue.RemoveAt(0);
                    mineFilled |= front.Mine;
                }

                if (got > 0) trades.Add((t, got));
                if (mineFilled && pending.Count == 0)
                {
                    if (kind == Kind.Native && hidden > 0)
                    {
                        decimal size = Math.Min(peak, hidden); hidden -= size;
                        pending.Add((t + Exp(rng, 76), new Ord(size, true), true));      // the exchange refills it and it keeps its place
                    }
                    else if (kind == Kind.Synthetic && hidden > 0)
                    {
                        decimal size = Math.Min(peak, hidden); hidden -= size;
                        pending.Add((t + 100 + 200 * rng.NextDouble(), new Ord(size, true), false)); // the trader's software sends a new order
                    }
                    else if (kind == Kind.MarketMaker)
                    {
                        pending.Add((t + Exp(rng, 100), new Ord(peak, true), false));
                    }
                }
            }

            shown.Add((t, queue.Sum(o => o.Size)));
        }

        return (trades, shown, refills);
    }

    private static DepthSnapshot SnapAt(long ticks, List<(double Ms, decimal Size)> shown, double ms, double gridMs = 250)
    {
        var (size, picture) = SizeAt(shown, ms, gridMs);
        return Snap(ticks, size, picture);
    }

    private static (decimal Size, long Picture) SizeAt(List<(double Ms, decimal Size)> shown, double ms, double gridMs = 250)
    {
        double grid = Math.Floor(ms / gridMs) * gridMs; // the depth history keeps a picture every 250 ms; at a trade the latest one at or before it is used
        int lo = 0, hi = shown.Count - 1, found = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (shown[mid].Ms <= grid) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }

        return (shown[found].Size, (long)(grid / gridMs));
    }

    [Fact]
    public void Score_the_detector_on_a_market_where_the_answer_is_known()
    {
        var sb = new StringBuilder();
        var configs = new (string Name, int MinRefills, bool Ratio, bool Loose)[] { ("ratio only", 0, true, false), ("ratio+refill 3", 3, true, false), ("refill only 3", 3, false, false), ("refill only 4", 4, false, false), ("loose 4", 4, false, true), ("loose 5", 5, false, true) };
        var kinds = Enum.GetValues<Kind>();
        const int Episodes = 150;
        foreach (double tape in new[] { 0.3, 1.0, 4.0 })
        {
            sb.AppendLine($"tape: {tape} aggressive orders per second at the level, {Episodes} episodes per kind");
            sb.AppendLine("kind".PadRight(14) + string.Join("", configs.Select(c => c.Name.PadLeft(20))));
            var flagged = new int[kinds.Length, configs.Length];
            var real = new int[kinds.Length, configs.Length];   // episodes that refilled at least 3 times (the kinds with a refill)
            var realFlagged = new int[kinds.Length, configs.Length];
            for (int ci = 0; ci < configs.Length; ci++)
            {
                var det = new IcebergDetector { MinRefills = configs[ci].MinRefills, RatioRule = configs[ci].Ratio, LooseRefill = configs[ci].Loose, Keep = TimeSpan.FromDays(1) };
                var rng = new Random(1234 + (int)(tape * 10));
                long ticks = 400_000;
                var origin = new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);
                int episode = 0;
                foreach (var kind in kinds)
                {
                    for (int e = 0; e < Episodes; e++, episode++)
                    {
                        ticks++;
                        var (trades, shown, refills) = Run(kind, rng, tape);
                        var start = origin.AddMinutes(episode * 5);
                        foreach (var (ms, vol) in trades)
                            det.OnTrade(start.AddMilliseconds(ms), ticks, vol, true, SnapAt(ticks, shown, ms));
                        bool hit = det.Events.Any(ev => ev.Ticks == ticks);
                        if (hit) flagged[(int)kind, ci]++;
                        if (refills >= 3) { real[(int)kind, ci]++; if (hit) realFlagged[(int)kind, ci]++; }
                    }
                }
            }

            foreach (var kind in kinds)
                sb.AppendLine(kind.ToString().PadRight(14) + string.Join("", Enumerable.Range(0, configs.Length).Select(ci =>
                    (kind is Kind.Native or Kind.Synthetic or Kind.MarketMaker
                        ? $"{100.0 * flagged[(int)kind, ci] / Episodes:0}% ({100.0 * realFlagged[(int)kind, ci] / Math.Max(1, real[(int)kind, ci]):0}% of {real[(int)kind, ci]})"
                        : $"{100.0 * flagged[(int)kind, ci] / Episodes:0}%").PadLeft(20))));
            sb.AppendLine();
        }

        output.WriteLine(sb.ToString());
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "iceberg-simulation.txt"), sb.ToString());
    }

    [Fact]
    public void How_much_a_finer_depth_picture_would_help()
    {
        var sb = new StringBuilder("refill rule only, 4 refills; recall on icebergs that refilled at least 3 times (and false flags on a busy level), by how often the book is recorded\n");
        foreach (double tape in new[] { 1.0, 4.0 })
        {
            sb.AppendLine($"tape {tape}/s");
            foreach (double grid in new[] { 250.0, 100.0, 50.0, 20.0 })
            {
                var det = new IcebergDetector { MinRefills = 4, RatioRule = false, Keep = TimeSpan.FromDays(1) };
                var rng = new Random(99);
                long ticks = 500_000;
                var origin = new DateTime(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);
                int episode = 0, nat = 0, natHit = 0, syn = 0, synHit = 0, busy = 0, busyHit = 0;
                foreach (var kind in new[] { Kind.Native, Kind.Synthetic, Kind.Busy })
                    for (int e = 0; e < 150; e++, episode++)
                    {
                        ticks++;
                        var (trades, shown, refills) = Run(kind, rng, tape);
                        var start = origin.AddMinutes(episode * 5);
                        foreach (var (ms, vol) in trades) det.OnTrade(start.AddMilliseconds(ms), ticks, vol, true, SnapAt(ticks, shown, ms, grid));
                        bool hit = det.Events.Any(ev => ev.Ticks == ticks);
                        if (kind == Kind.Busy) { busy++; if (hit) busyHit++; }
                        else if (refills >= 3) { if (kind == Kind.Native) { nat++; if (hit) natHit++; } else { syn++; if (hit) synHit++; } }
                    }

                sb.AppendLine($"  book every {grid,3:0} ms: native {100.0 * natHit / Math.Max(1, nat):0}% of {nat}, synthetic {100.0 * synHit / Math.Max(1, syn):0}% of {syn}, busy level flagged {100.0 * busyHit / busy:0}%");
            }
        }

        output.WriteLine(sb.ToString());
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "iceberg-simulation.txt"), "\n" + sb);
    }
}
