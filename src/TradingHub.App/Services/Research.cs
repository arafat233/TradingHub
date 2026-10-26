using System.Text;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Services;

/// <summary>
/// Research on the recorded ticks: does fading or following the big-order levels pay once fills are made realistic, under which
/// conditions, and does a big order in one market lead another. Built to avoid fooling itself: realistic fills, levels split by
/// time (conditions chosen on the first part, checked on the part kept back), a stricter bar for the number of conditions tried,
/// and a plain verdict when nothing survives.
/// </summary>
internal static class Research
{
    private static readonly int[] Thresholds = { 4, 8, 12, 16, 20, 30, 40, 60, 80 };
    private const int Stop = 20, Target = 40, Slip = 1, FillThrough = 1, AwayTicks = 4;
    private static readonly TimeSpan TouchWindow = TimeSpan.FromDays(2), FollowWindow = TimeSpan.FromHours(4);

    private sealed record Prepared(string Feed, string Symbol, decimal Tick, int Orders, int Touched, int Filled, List<LevelRecord> Records, double OptimisticFade, double OptimisticFadeT, double OptimisticFollow, double OptimisticFollowT);

    private static Prepared? Prepare(string feed, string symbol)
    {
        var t = LevelTools.Load(feed, symbol);
        if (t == null || !t.TicksMeaningful) return null;
        var orders = LevelMiner.FindOrders(t.Trades, t.Tick);
        if (orders.Count < 40) return null;
        var minutes = new Regime.Minutes(t.Times, t.Prices);

        // per UTC day: high and low (in ticks), and when the previous day's high / low was first taken out
        long Day(long ticks) => ticks / TimeSpan.TicksPerDay;
        var hi = new Dictionary<long, long>();
        var lo = new Dictionary<long, long>();
        for (int i = 0; i < t.Times.Length; i++)
        {
            long d = Day(t.Times[i]);
            hi[d] = hi.TryGetValue(d, out var h) ? Math.Max(h, t.Prices[i]) : t.Prices[i];
            lo[d] = lo.TryGetValue(d, out var l) ? Math.Min(l, t.Prices[i]) : t.Prices[i];
        }

        var sweepAt = new Dictionary<long, long>();
        for (int i = 0; i < t.Times.Length; i++)
        {
            long d = Day(t.Times[i]);
            if (sweepAt.ContainsKey(d) || !hi.ContainsKey(d - 1)) continue;
            if (t.Prices[i] >= hi[d - 1] + 2 || t.Prices[i] <= lo[d - 1] - 2) sweepAt[d] = t.Times[i];
        }

        var sizeRank = orders.Select(o => o.Volume).OrderBy(v => v).ToList();
        int iStop = Array.IndexOf(Thresholds, Stop), iTarget = Array.IndexOf(Thresholds, Target);
        var records = new List<LevelRecord>();
        var optFade = new List<double>();
        var optFollow = new List<double>();
        int touched = 0, filled = 0;
        foreach (var o in orders)
        {
            var easy = LevelMiner.Trace(t.Times, t.Prices, o, t.Tick, AwayTicks, Thresholds, TouchWindow, FollowWindow);
            if (easy == null) continue;
            touched++;
            optFade.Add((double)LevelMiner.Simulate(easy, iStop, Stop, iTarget, Target));
            optFollow.Add((double)LevelMiner.SimulateFollow(easy, iStop, Stop, iTarget, Target));
            var real = LevelMiner.Trace(t.Times, t.Prices, o, t.Tick, AwayTicks, Thresholds, TouchWindow, FollowWindow, FillThrough);
            if (real is not { Filled: true }) continue;
            filled++;
            long L = (long)Math.Round(o.Price / t.Tick), d = Day(o.Time.Ticks);
            var (eff, move) = minutes.Before(o.Time.Ticks, 120);
            double dist = hi.TryGetValue(d - 1, out var ph) ? Math.Min(Math.Abs(L - ph), Math.Abs(L - lo[d - 1])) : double.NaN;
            int rank = sizeRank.BinarySearch(o.Volume);
            if (rank < 0) rank = ~rank;
            records.Add(new LevelRecord(o.Time, o.Buy, sizeRank.Count > 1 ? (double)rank / (sizeRank.Count - 1) : 0.5, o.Time.Hour, eff, move, dist,
                sweepAt.TryGetValue(d, out var sw) && o.Time.Ticks > sw,
                (double)LevelMiner.Simulate(real, iStop, Stop, iTarget, Target, Slip), (double)LevelMiner.SimulateFollow(real, iStop, Stop, iTarget, Target, Slip)));
        }

        var (mf, tf) = Data.EdgeFinder.Stats(optFade);
        var (mo, to) = Data.EdgeFinder.Stats(optFollow);
        return new Prepared(feed, symbol, t.Tick, orders.Count, touched, filled, records, mf, tf, mo, to);
    }

    private static string Verdict(double fade, double fadeT, double follow, double followT) =>
        Math.Abs(fadeT) < 2 && Math.Abs(followT) < 2 ? "no clear difference from zero either way"
        : fade > follow ? (fadeT >= 2 ? "FADE favoured" : "follow loses clearly (fade not clearly better than nothing)")
        : followT >= 2 ? "FOLLOW the break favoured" : "fade loses clearly (follow not clearly better than nothing)";

    /// <summary>Fade against follow with realistic fills, and the conditions under which either paid (checked on data kept back).</summary>
    public static string RunEdgeFinder()
    {
        var sb = new StringBuilder("EDGE FINDER · default stop 20 / target 40 ticks · realistic fills = order fills only when price trades 1 tick through the level, stops cost 1 tick more\n");
        sb.AppendLine("Conditions are chosen on the first 60% of the levels (by time) and must also hold, same sign, on the last 40%, after a stricter bar for the number of conditions tried.");
        bool any = false;
        foreach (var (feed, symbol) in LevelTools.Instruments())
        {
            var p = Prepare(feed, symbol);
            if (p == null) continue;
            any = true;
            var fade = EdgeFinderStats(p.Records, r => r.FadeTicks);
            var follow = EdgeFinderStats(p.Records, r => r.FollowTicks);
            sb.AppendLine($"\n{symbol} · {p.Orders} big orders · price came back to {p.Touched} · {p.Filled} would have filled · {p.Records.Count(r => !double.IsNaN(r.Efficiency))} with 2 h of history before them");
            sb.AppendLine($"  fading:    realistic {fade.Mean:+0.0;-0.0;0.0} ticks/trade (t {fade.T:0.0})   optimistic {p.OptimisticFade:+0.0;-0.0;0.0} (t {p.OptimisticFadeT:0.0})");
            sb.AppendLine($"  following: realistic {follow.Mean:+0.0;-0.0;0.0} ticks/trade (t {follow.T:0.0})   optimistic {p.OptimisticFollow:+0.0;-0.0;0.0} (t {p.OptimisticFollowT:0.0})");
            sb.AppendLine($"  verdict (realistic fills): {Verdict(fade.Mean, fade.T, follow.Mean, follow.T)}");
            foreach (var (name, results) in new[] { ("fading", Data.EdgeFinder.Evaluate(p.Records, r => r.FadeTicks)), ("following", Data.EdgeFinder.Evaluate(p.Records, r => r.FollowTicks)) })
            {
                var pass = results.Where(s => s.Passes).ToList();
                sb.AppendLine($"  {name}: {pass.Count} of {results.Count} conditions held up on the data kept back");
                foreach (var s in pass) sb.AppendLine($"      PASS  {s.Name}: found on {s.Found} levels {s.FoundMean:+0.0;-0.0;0.0} ticks (t {s.FoundT:0.0}), kept {s.Kept} levels {s.KeptMean:+0.0;-0.0;0.0} (t {s.KeptT:0.0})");
                if (pass.Count == 0)
                    foreach (var s in results.OrderByDescending(s => Math.Abs(s.FoundT)).Take(3))
                        sb.AppendLine($"      closest: {s.Name}: found {s.FoundMean:+0.0;-0.0;0.0} (t {s.FoundT:0.0}) on {s.Found}, kept {s.KeptMean:+0.0;-0.0;0.0} (t {s.KeptT:0.0}) on {s.Kept}");
            }
        }

        if (!any) sb.AppendLine("\nNot enough recorded data (at least 40 big orders in an instrument's ticks). Use Mine levels first, or let the app record more days.");
        sb.AppendLine("\nInstruments where a tick is a tiny part of the price (BTC, forex pairs) are left out: stops and targets in ticks mean nothing there.");
        sb.AppendLine("\nTwo days of ticks is a small sample: a PASS is a lead to keep watching as more days arrive, not a rule to trade.");
        return sb.ToString();
    }

    private static (double Mean, double T) EdgeFinderStats(List<LevelRecord> records, Func<LevelRecord, double> f) => Data.EdgeFinder.Stats(records.Select(f).ToList());

    /// <summary>For every ordered pair of recorded instruments: what the second one's price did after a big order in the first.</summary>
    public static string RunCrossMarket()
    {
        var sb = new StringBuilder("CROSS-MARKET: signed move of the second market's price (basis points) after a big order in the first; buy = up expected, sell = down\n");
        var all = new List<(string Name, LevelTools.Ticks T, List<MinedLevel> Orders)>();
        foreach (var (feed, symbol) in LevelTools.Instruments())
        {
            var t = LevelTools.Load(feed, symbol);
            if (t == null) continue;
            var orders = LevelMiner.FindOrders(t.Trades, t.Tick);
            if (orders.Count >= 30) all.Add((symbol, t, orders));
        }

        int[] horizons = { 5, 30, 120, 600 };
        int tests = Math.Max(1, all.Count * all.Count * horizons.Length);
        double bar = 0.05 / tests;
        int shown = 0;
        foreach (var (nameA, a, ordersA) in all)
            foreach (var (nameB, b, _) in all)
            {
                var prices = b.Trades.Select(x => (double)x.Price).ToArray();
                foreach (var r in Data.CrossMarket.Study(ordersA.Select(o => (o.Time, o.Buy)).ToList(), b.Times, prices, horizons))
                {
                    if (r.Orders < 30) continue;
                    bool pass = Data.EdgeFinder.TwoSidedP(r.T) < bar;
                    if (!pass && !(nameA == nameB && r.Seconds == 30)) continue; // the same market at 30 s is the sanity line; the rest only if they stand out
                    shown++;
                    sb.AppendLine($"  {nameA,-8} -> {nameB,-8} after {r.Seconds,4} s: {r.MeanBps:+0.00;-0.00;0.00} bps over {r.Orders} orders (t {r.T:0.0}){(pass ? "   <-- significant" : "")}{(nameA == nameB ? "   (same market: a sanity check)" : "")}");
                }
            }

        if (all.Count < 2) sb.AppendLine("  needs two or more instruments with recorded ticks and 30+ big orders");
        else if (shown == 0) sb.AppendLine("  nothing to show");
        sb.AppendLine($"\n{all.Count * all.Count * horizons.Length} comparisons were made, so a result needs p below {bar:0.00000} to be listed as significant. Only significant pairs (and the same-market line) are printed.");
        return sb.ToString();
    }
}
