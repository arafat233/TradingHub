using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;

namespace TradingHub.App.Services;

/// <summary>Follow-ups on the one claim that held (price crosses low volume nodes faster): stability, forward testing, a rule built on it, stops placed inside nodes, and how good the candle-based profile is.</summary>
internal static partial class ResearchAuction
{
    internal sealed record NodeVisit(DateOnly Day, bool Lvn, double LogMinutes);

    private static readonly Dictionary<string, AuctionResult?> NodeCache = new();

    /// <summary>The nodes of the composite of the five sessions before session <paramref name="i"/> (at most three of each kind), or null.</summary>
    internal static AuctionResult? CompositeNodes(string symbol, List<Sess> sessions, int i, decimal size)
    {
        string key = $"{symbol}|{i}|{size}|{sessions.Count}";
        lock (NodeCache)
        {
            if (NodeCache.TryGetValue(key, out var cached)) return cached;
            AuctionResult? result = null;
            if (i >= 5)
            {
                var pool = new Dictionary<decimal, decimal>();
                for (int k = i - 5; k < i; k++)
                    for (int r = 0; r < sessions[k].Input.Rows.Length; r++)
                        pool[sessions[k].Input.Rows[r]] = pool.GetValueOrDefault(sessions[k].Input.Rows[r]) + sessions[k].Input.Volume[r];
                if (pool.Count >= 10)
                {
                    var rows = pool.Keys.OrderBy(r => r).ToList();
                    var full = new List<decimal>();
                    for (decimal r = rows[0]; r <= rows[^1]; r += size) full.Add(r);
                    result = AuctionAnalysis.Analyze(new AuctionInput(full.ToArray(), full.Select(_ => Array.Empty<int>()).ToArray(), full.Select(r => pool.GetValueOrDefault(r)).ToArray(), size), maxNodes: 3);
                }
            }

            return NodeCache[key] = result;
        }
    }

    /// <summary>For every node of the day, how long price stayed inside it on its first visit (log of minutes); only visits that ended count.</summary>
    internal static List<NodeVisit> NodeVisits(ResearchHistory.Series s, List<Sess> sessions, decimal size)
    {
        var visits = new List<NodeVisit>();
        for (int i = 5; i < sessions.Count; i++)
        {
            if (CompositeNodes(s.Symbol, sessions, i, size) is not { } nodes) continue;
            var day = s.Days[sessions[i].DayIndex];
            foreach (var (zones, lvn) in new[] { (nodes.LowVolumeNodes, true), (nodes.HighVolumeNodes, false) })
                foreach (var z in zones)
                {
                    decimal bottom = z.Low, top = z.High + size;
                    if ((top - bottom) / s.Tick < 3) continue;
                    int enter = -1, exit = -1;
                    for (int j = day.From; j <= day.To; j++)
                    {
                        bool overlaps = s.Bars[j].High >= bottom && s.Bars[j].Low <= top;
                        if (enter < 0 && overlaps) enter = j;
                        else if (enter >= 0 && !overlaps) { exit = j; break; }
                    }

                    if (enter >= 0 && exit > enter) visits.Add(new NodeVisit(sessions[i].Day, lvn, Math.Log(exit - enter + 1)));
                }
        }

        return visits;
    }

    private static (double Median, double T) Compare(List<NodeVisit> visits, out double medianLvn, out double medianHvn)
    {
        var a = visits.Where(v => v.Lvn).Select(v => v.LogMinutes).ToList();
        var b = visits.Where(v => !v.Lvn).Select(v => v.LogMinutes).ToList();
        medianLvn = a.Count == 0 ? 0 : Math.Exp(a.OrderBy(x => x).ElementAt(a.Count / 2));
        medianHvn = b.Count == 0 ? 0 : Math.Exp(b.OrderBy(x => x).ElementAt(b.Count / 2));
        if (a.Count < 2 || b.Count < 2) return (0, 0);
        double ma = a.Average(), mb = b.Average();
        double va = a.Sum(x => (x - ma) * (x - ma)) / (a.Count - 1), vb = b.Sum(x => (x - mb) * (x - mb)) / (b.Count - 1);
        return (ma - mb, (ma - mb) / Math.Sqrt(va / a.Count + vb / b.Count));
    }

    /// <summary>Month by month: is the low volume node effect there in most months, or does it come from a few?</summary>
    internal static string NodeStability(ResearchHistory.Series s, List<Sess> sessions, decimal size)
    {
        var visits = NodeVisits(s, sessions, size);
        var sb = new StringBuilder("  month by month (typical minutes inside a low volume node / a high volume node, and how many visits):\n");
        int months = 0, faster = 0;
        foreach (var g in visits.GroupBy(v => (v.Day.Year, v.Day.Month)).OrderBy(g => g.Key))
        {
            int l = g.Count(v => v.Lvn), h = g.Count(v => !v.Lvn);
            if (l < 8 || h < 8) continue;
            Compare(g.ToList(), out var ml, out var mh);
            months++;
            if (ml < mh) faster++;
            sb.AppendLine($"      {g.Key.Year}-{g.Key.Month:00}: {ml,4:0} min ({l}) vs {mh,4:0} min ({h})  {(ml < mh ? "low volume faster" : "low volume slower")}");
        }

        sb.Append(months == 0 ? "      not enough visits per month" : $"      low volume nodes were faster in {faster} of {months} months");
        string status = months < 6 ? "too few cases" : faster >= months * 0.75 ? "supported" : faster <= months * 0.5 ? "not supported" : "mixed";
        Ledger.Record($"lvn-stability|{s.Symbol}", $"{s.Symbol}: low volume nodes are crossed faster, month by month", status, months == 0 ? "no month had enough visits" : $"faster in {faster} of {months} months", $"{months} months of 1-minute candles");
        return sb.ToString();
    }

    /// <summary>The claim locked at the time it was first seen supported, checked only on days recorded since (from ticks), once there are enough.</summary>
    internal static string NodeForward(ResearchHistory.Series history, bool supportedNow)
    {
        string id = $"lvn-speed|{history.Symbol}";
        if (!Ledger.IsLocked(id) && !supportedNow) return "  forward test: the claim is not supported yet, so it has not been locked";
        var locked = Ledger.LockTime(id);
        var ticks = LevelTools.Load("lse", history.Symbol);
        if (ticks == null) return $"  forward test: locked {locked.ToLocalTime():dd/MM/yyyy}; no recorded ticks for {history.Symbol} to test it on";
        var bars = new List<Kline>();
        long minute = long.MinValue;
        foreach (var t in ticks.Trades)
        {
            long m = t.Time.Ticks / TimeSpan.TicksPerMinute;
            long ms = new DateTimeOffset(new DateTime(m * TimeSpan.TicksPerMinute, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            if (m != minute) { bars.Add(new Kline(ms, t.Price, t.Price, t.Price, t.Price, t.Volume, 0, false)); minute = m; }
            else { var b = bars[^1]; bars[^1] = new Kline(b.OpenTimeMs, b.Open, Math.Max(b.High, t.Price), Math.Min(b.Low, t.Price), t.Price, b.Volume + t.Volume, 0, false); }
        }

        var series = ResearchHistory.Build(history.Symbol, history.Tick, history.PointValue, bars, 6);
        if (series == null) return $"  forward test: locked {locked.ToLocalTime():dd/MM/yyyy}; only {ticks.Days} recorded day(s) so far, six sessions are needed before the first test day";
        var sessions = Sessions(series, out var size);
        var after = NodeVisits(series, sessions, size).Where(v => v.Day >= DateOnly.FromDateTime(locked.ToLocalTime())).ToList();
        int l = after.Count(v => v.Lvn), h = after.Count(v => !v.Lvn);
        Compare(after, out var ml, out var mh);
        var (_, t2) = Compare(after, out _, out _);
        string result = l < 30 || h < 30 ? $"waiting for data: {l} low volume and {h} high volume visits since the lock (30 of each are needed)"
            : $"{l} low volume visits ({ml:0} min) and {h} high volume visits ({mh:0} min), t {t2:0.0}: " + (t2 <= -2 ? "the effect holds on new data" : "it does not hold on new data");
        Ledger.Record($"lvn-forward|{history.Symbol}", $"{history.Symbol}: low volume nodes are crossed faster (forward test, locked {locked.ToLocalTime():dd/MM/yyyy})", l < 30 || h < 30 ? "too few cases" : t2 <= -2 ? "supported" : "not supported", result, "ticks recorded after the lock");
        return "  forward test (locked " + locked.ToLocalTime().ToString("dd/MM/yyyy") + "): " + result;
    }

    // ----------------------------------------------------------------------------------------------- a rule built on the claim

    /// <summary>
    /// Leave a high volume node through its edge toward the next high volume node: enter in that direction, target the near edge of the next
    /// node, stop at a fraction of the distance back. One trade per node per day. Nodes are those of the previous five sessions' composite.
    /// </summary>
    internal static List<RuleTrade> NodeRotation(ResearchHistory.Series s, AppServices? svc, double stopFraction)
    {
        var trades = new List<RuleTrade>();
        var sessions = Sessions(s, out var size);
        decimal tick = s.Tick, tickValue = s.Tick * s.PointValue;
        decimal comm = tickValue > 0 ? (svc?.Settings.CommissionPerRoundTrip ?? 4m) / tickValue : 0;
        for (int i = 5; i < sessions.Count; i++)
        {
            if (CompositeNodes(s.Symbol, sessions, i, size) is not { } nodes || nodes.HighVolumeNodes.Count < 2) continue;
            var hvn = nodes.HighVolumeNodes.Select(z => (Bottom: z.Low, Top: z.High + size)).OrderBy(z => z.Bottom).ToList();
            var used = new HashSet<int>();
            var sess = sessions[i];
            for (int j = sess.From + 1; j <= sess.To - 1; j++)
            {
                decimal prev = s.Bars[j - 1].Close, now = s.Bars[j].Close;
                for (int k = 0; k < hvn.Count; k++)
                {
                    if (used.Contains(k) || !(prev >= hvn[k].Bottom && prev <= hvn[k].Top)) continue;
                    bool up = now > hvn[k].Top, down = now < hvn[k].Bottom;
                    if (!up && !down) continue;
                    var next = up ? hvn.Where(z => z.Bottom > hvn[k].Top).Cast<(decimal Bottom, decimal Top)?>().FirstOrDefault() : hvn.Where(z => z.Top < hvn[k].Bottom).Cast<(decimal Bottom, decimal Top)?>().LastOrDefault();
                    if (next == null) continue;
                    decimal entry = now + (up ? tick : -tick), target = up ? next.Value.Bottom : next.Value.Top;
                    decimal distance = Math.Abs(target - entry) / tick;
                    if (distance < 10 || distance > 200 || (up ? target <= entry : target >= entry)) continue;
                    used.Add(k);
                    decimal stop = up ? entry - (target - entry) * (decimal)stopFraction : entry + (entry - target) * (decimal)stopFraction;
                    double gross = CandleSim.Run(s.Bars, j + 1, up, entry, stop, target, 240, tick, 1);
                    trades.Add(new RuleTrade(s.Local[j].Date, s.Local[j], gross - (double)comm, 2 + (double)comm));
                    break;
                }
            }
        }

        return trades;
    }

    // ----------------------------------------------------------------------------------------------- stops inside nodes

    /// <summary>Do stops placed inside a low volume node get hit more, and slip further, than stops in a high volume node or elsewhere?</summary>
    public static string StopsInNodes()
    {
        var sb = new StringBuilder("STOPS AND VOLUME NODES: a stop 20 or 40 ticks from the entry (either direction), by where the stop sits relative to the nodes of the previous five sessions' composite\n");
        sb.AppendLine("Entries every 30 minutes of the New York 08:00-17:00 session; \"hit\" = price reached the stop within 60 minutes; \"overshoot\" = how far past the stop the candle that hit it went (ticks).");
        bool any = false;
        foreach (var s in ResearchHistory.Load())
        {
            var sessions = Sessions(s, out var size);
            if (sessions.Count < 80) continue;
            any = true;
            var rows = new List<(string Zone, bool Hit, double Overshoot)>();
            for (int i = 5; i < sessions.Count; i++)
            {
                if (CompositeNodes(s.Symbol, sessions, i, size) is not { } nodes) continue;
                var sess = sessions[i];
                for (int j = sess.From; j <= sess.To - 60; j += 30)
                {
                    decimal entry = s.Bars[j].Close;
                    foreach (int ticksAway in new[] { 20, 40 })
                        foreach (bool longTrade in new[] { true, false })
                        {
                            decimal stop = longTrade ? entry - ticksAway * s.Tick : entry + ticksAway * s.Tick;
                            string zone = nodes.LowVolumeNodes.Any(z => stop >= z.Low && stop <= z.High + size) ? "low volume node"
                                : nodes.HighVolumeNodes.Any(z => stop >= z.Low && stop <= z.High + size) ? "high volume node" : "elsewhere";
                            bool hit = false;
                            double over = 0;
                            for (int k = j + 1; k <= j + 60 && k < s.Bars.Count; k++)
                                if (longTrade ? s.Bars[k].Low <= stop : s.Bars[k].High >= stop)
                                {
                                    hit = true;
                                    over = (double)((longTrade ? stop - s.Bars[k].Low : s.Bars[k].High - stop) / s.Tick);
                                    break;
                                }

                            rows.Add((zone, hit, over));
                        }
                }
            }

            sb.AppendLine($"\n{s.Symbol}");
            foreach (var zone in new[] { "low volume node", "high volume node", "elsewhere" })
            {
                var set = rows.Where(r => r.Zone == zone).ToList();
                if (set.Count == 0) continue;
                var hits = set.Where(r => r.Hit).Select(r => r.Overshoot).OrderBy(v => v).ToList();
                sb.AppendLine($"  stop in a {zone,-17}: {set.Count,6} stops · hit {100.0 * hits.Count / set.Count:0.0}% · overshoot typical {(hits.Count == 0 ? 0 : hits[hits.Count / 2]):0.0} ticks, average {(hits.Count == 0 ? 0 : hits.Average()):0.0}");
            }

            var lvn = rows.Where(r => r.Zone == "low volume node").ToList();
            var other = rows.Where(r => r.Zone != "low volume node").ToList();
            if (lvn.Count >= 50 && other.Count >= 50)
            {
                double p1 = lvn.Count(r => r.Hit) / (double)lvn.Count, p2 = other.Count(r => r.Hit) / (double)other.Count;
                double pooled = (lvn.Count(r => r.Hit) + other.Count(r => r.Hit)) / (double)(lvn.Count + other.Count);
                double z = (p1 - p2) / Math.Sqrt(pooled * (1 - pooled) * (1.0 / lvn.Count + 1.0 / other.Count));
                var oa = lvn.Where(r => r.Hit).Select(r => r.Overshoot).ToList();
                var ob = other.Where(r => r.Hit).Select(r => r.Overshoot).ToList();
                var (mo, to) = oa.Count > 2 && ob.Count > 2 ? (oa.Average() - ob.Average(), Welch(oa, ob)) : (0.0, 0.0);
                string verdict = Math.Abs(z) < 2 && Math.Abs(to) < 2 ? "no clear difference: a stop inside a low volume node is not worse" : z >= 3 || to >= 3 ? "stops inside low volume nodes ARE worse (hit more or slip further)" : z >= 2 || to >= 2 ? "borderline: possibly worse, but not enough after allowing for the number of comparisons" : "stops inside low volume nodes do better, or the difference is borderline";
                sb.AppendLine($"  low volume vs the rest: hit rate difference {100 * (p1 - p2):+0.0;-0.0;0.0} points (z {z:0.0}); overshoot difference {mo:+0.0;-0.0;0.0} ticks (t {to:0.0}) → {verdict}");
                Ledger.Record($"stops-in-lvn|{s.Symbol}", $"{s.Symbol}: stops inside low volume nodes are hit more or slip further", z >= 3 || to >= 3 ? "supported" : "not supported", $"hit {100 * (p1 - p2):+0.0;-0.0;0.0} points (z {z:0.0}), overshoot {mo:+0.0;-0.0;0.0} ticks (t {to:0.0})", $"{rows.Count:N0} simulated stops");
            }
        }

        if (!any) sb.AppendLine("\nNo cached 1-minute candles with enough history.");
        sb.AppendLine("\nThese are simulated stops on 1-minute candles, not your orders.");
        return sb.ToString();
    }

    private static double Welch(List<double> a, List<double> b)
    {
        double ma = a.Average(), mb = b.Average();
        double va = a.Sum(x => (x - ma) * (x - ma)) / (a.Count - 1), vb = b.Sum(x => (x - mb) * (x - mb)) / (b.Count - 1);
        double se = Math.Sqrt(va / a.Count + vb / b.Count);
        return se <= 1e-12 ? 0 : (ma - mb) / se;
    }

    // ----------------------------------------------------------------------------------------------- how good is the candle profile

    /// <summary>Compares the candle-based profile (volume spread evenly over each candle's range) with the real volume by price from recorded ticks.</summary>
    public static string ProfileAccuracy()
    {
        var sb = new StringBuilder("PROFILE ACCURACY: the candle-based profile (volume spread evenly over each 1-minute candle's range) against real volume by price from the recorded ticks\n");
        var tz = ResearchHistory.NewYork();
        bool any = false;
        foreach (var (feed, symbol) in LevelTools.Instruments())
        {
            var t = LevelTools.Load(feed, symbol);
            if (t == null || !t.TicksMeaningful) continue;
            var bySession = t.Trades.GroupBy(x => TimeZoneInfo.ConvertTimeFromUtc(x.Time, tz)).Where(g => true).ToList();
            var days = t.Trades.Select(x => (Local: TimeZoneInfo.ConvertTimeFromUtc(x.Time, tz), Trade: x))
                .Where(x => x.Local.TimeOfDay >= TimeSpan.FromHours(8) && x.Local.TimeOfDay < TimeSpan.FromHours(17))
                .GroupBy(x => x.Local.Date).Where(g => (g.Last().Local - g.First().Local).TotalHours >= 6).ToList();
            foreach (var day in days)
            {
                var trades = day.Select(x => x.Trade).ToList();
                var bars = new List<Kline>();
                var localMinute = new List<int>();
                long minute = long.MinValue;
                foreach (var x in day)
                {
                    long m = x.Trade.Time.Ticks / TimeSpan.TicksPerMinute;
                    long ms = new DateTimeOffset(new DateTime(m * TimeSpan.TicksPerMinute, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
                    if (m != minute) { bars.Add(new Kline(ms, x.Trade.Price, x.Trade.Price, x.Trade.Price, x.Trade.Price, x.Trade.Volume, 0, false)); localMinute.Add(x.Local.Hour * 60 + x.Local.Minute); minute = m; }
                    else { var b = bars[^1]; bars[^1] = new Kline(b.OpenTimeMs, b.Open, Math.Max(b.High, x.Trade.Price), Math.Min(b.Low, x.Trade.Price), x.Trade.Price, b.Volume + x.Trade.Volume, 0, false); }
                }

                decimal lo = trades.Min(x => x.Price), hi = trades.Max(x => x.Price);
                int rowTicks = Math.Max(1, (int)Math.Round((double)((hi - lo) / t.Tick) / 40));
                decimal size = rowTicks * t.Tick;
                var candle = AuctionBuilder.FromCandles(bars, 0, bars.Count - 1, i => (localMinute[i] - 480) / 30, size);
                // real volume by price on the same rows
                var truth = new decimal[candle.Rows.Length];
                foreach (var x in trades)
                {
                    int r = (int)(Math.Floor(x.Price / size) - Math.Floor(candle.Rows[0] / size));
                    if (r >= 0 && r < truth.Length) truth[r] += x.Volume;
                }

                var real = AuctionAnalysis.Analyze(new AuctionInput(candle.Rows, candle.Slots, truth, size));
                var approx = AuctionAnalysis.Analyze(candle);
                double mt = (double)truth.Average(), mc = (double)candle.Volume.Average();
                double cov = truth.Zip(candle.Volume, (a, b) => ((double)a - mt) * ((double)b - mc)).Sum();
                double var1 = truth.Sum(a => ((double)a - mt) * ((double)a - mt)), var2 = candle.Volume.Sum(b => ((double)b - mc) * ((double)b - mc));
                double corr = var1 <= 0 || var2 <= 0 ? 0 : cov / Math.Sqrt(var1 * var2);
                bool Overlap(List<Zone> a, List<Zone> b) => a.Any(x => b.Any(y => x.Low <= y.High && y.Low <= x.High));
                any = true;
                sb.AppendLine($"  {symbol} {day.Key:yyyy-MM-dd}: {truth.Length} rows of {size} · volume by row, correlation {corr:0.00} · volume POC off by {(approx.VolumePoc - real.VolumePoc) / size:+0;-0;0} rows · value area edges off by {(approx.VolumeVah - real.VolumeVah) / size:+0;-0;0} / {(approx.VolumeVal - real.VolumeVal) / size:+0;-0;0} rows · "
                              + $"high volume nodes {(Overlap(approx.HighVolumeNodes, real.HighVolumeNodes) ? "overlap" : "do not overlap")}, low volume nodes {(Overlap(approx.LowVolumeNodes, real.LowVolumeNodes) ? "overlap" : "do not overlap")}");
            }
        }

        if (!any) sb.AppendLine("  needs recorded ticks covering at least six hours of a New York 08:00-17:00 session");
        sb.AppendLine("\nA high correlation and POC/value-area edges off by a row or two say the candle approximation is good enough for the claims test.");
        return sb.ToString();
    }
}
