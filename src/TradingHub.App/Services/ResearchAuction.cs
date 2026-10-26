using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;

namespace TradingHub.App.Services;

/// <summary>
/// Tests, on months of 1-minute candles, what the auction-theory (TPO + volume profile) method says: whether poor highs and lows are
/// revisited, tails mark rejection, naked POCs and single prints get touched, price accepted outside the previous value continues, and
/// price moves through low volume nodes faster than through high volume ones. Each claim is compared with a distance-matched control
/// (a price at the same distance from the close, either side), so "price came back" is not mistaken for something special.
/// Also builds the open-location and day-type tables the Auction map indicator shows. Sessions are the New York 08:00-17:00 day;
/// volume is spread evenly over each candle's range (candles do not say where inside the range the volume traded).
/// </summary>
internal static partial class ResearchAuction
{
    private const int StartMinute = 8 * 60, EndMinute = 17 * 60, PeriodMinutes = 30;
    private static readonly int[] Horizons = { 1, 3, 5 };

    internal sealed record Sess(int DayIndex, DateOnly Day, int From, int To, AuctionInput Input, AuctionResult R, decimal Open, decimal Close, decimal High, decimal Low);

    internal static List<Sess> Sessions(ResearchHistory.Series s, out decimal rowSize)
    {
        var spans = new List<(int DayIndex, DateOnly Day, int A, int B)>();
        for (int d = 0; d < s.Days.Count; d++)
        {
            var (day, from, to) = s.Days[d];
            int a = -1, b = -1;
            for (int i = from; i <= to; i++)
                if (s.Minute[i] >= StartMinute && s.Minute[i] < EndMinute) { if (a < 0) a = i; b = i; }
            if (a >= 0 && b - a + 1 >= 300) spans.Add((d, day, a, b));
        }

        var ranges = spans.Select(x => { decimal lo = decimal.MaxValue, hi = decimal.MinValue; for (int i = x.A; i <= x.B; i++) { lo = Math.Min(lo, s.Bars[i].Low); hi = Math.Max(hi, s.Bars[i].High); } return (hi - lo) / s.Tick; }).OrderBy(v => v).ToList();
        int rowTicks = ranges.Count == 0 ? 1 : Math.Max(1, (int)Math.Round((double)ranges[ranges.Count / 2] / 40));
        rowSize = rowTicks * s.Tick;
        var result = new List<Sess>();
        decimal size = rowSize;
        foreach (var (dayIndex, day, a, b) in spans)
        {
            var input = AuctionBuilder.FromCandles(s.Bars, a, b, i => (s.Minute[i] - StartMinute) / PeriodMinutes, size);
            decimal hi = s.Bars[a].High, lo = s.Bars[a].Low;
            for (int i = a; i <= b; i++) { hi = Math.Max(hi, s.Bars[i].High); lo = Math.Min(lo, s.Bars[i].Low); }
            result.Add(new Sess(dayIndex, day, a, b, input, AuctionAnalysis.Analyze(input), s.Bars[a].Open, s.Bars[b].Close, hi, lo));
        }

        return result;
    }

    private static bool Touched(ResearchHistory.Series s, int from, int to, decimal level, bool above)
    {
        for (int i = from; i <= to && i < s.Bars.Count; i++)
            if (above ? s.Bars[i].High >= level : s.Bars[i].Low <= level) return true;
        return false;
    }

    private static int BoundFor(ResearchHistory.Series s, int dayIndex, int horizonDays) => s.Days[Math.Min(dayIndex + horizonDays, s.Days.Count - 1)].To;

    // ----------------------------------------------------------------------------------------------- claims

    private sealed record Instance(Sess S, decimal Level, bool Above, bool Exceed);

    public static string Claims()
    {
        var sb = new StringBuilder("AUCTION CLAIMS TEST: do the ideas of the TPO + volume profile method hold on your data?\n");
        sb.AppendLine("Each claim is compared with a control: a price at the same distance from the session's close, either side. \"Excess\" is how much more (or less) likely price was to trade there within 1, 3 and 5 trading days.");
        bool any = false;
        foreach (var s in ResearchHistory.Load())
        {
            var sessions = Sessions(s, out var size);
            if (sessions.Count < 80) continue;
            any = true;
            decimal tick = s.Tick;
            var claims = new List<(string Name, string Expect, List<Instance> Items)>
            {
                ("Poor highs are revisited", "revisited more than a matched price", sessions.Where(x => x.R.PoorHigh).Select(x => new Instance(x, x.High, true, false)).ToList()),
                ("Poor lows are revisited", "revisited more than a matched price", sessions.Where(x => x.R.PoorLow).Select(x => new Instance(x, x.Low, false, false)).ToList()),
                ("Tails at the high mark rejection (the high is not exceeded)", "exceeded LESS than a matched price", sessions.Where(x => x.R.TailHigh != null).Select(x => new Instance(x, x.High + tick, true, true)).ToList()),
                ("Tails at the low mark rejection (the low is not exceeded)", "exceeded LESS than a matched price", sessions.Where(x => x.R.TailLow != null).Select(x => new Instance(x, x.Low - tick, false, true)).ToList()),
                ("Naked TPO POCs get touched", "touched more than a matched price", sessions.Select(x => new Instance(x, x.R.TpoPoc + size / 2, x.R.TpoPoc + size / 2 >= x.Close, false)).ToList()),
                ("Naked volume POCs get touched", "touched more than a matched price", sessions.Select(x => new Instance(x, x.R.VolumePoc + size / 2, x.R.VolumePoc + size / 2 >= x.Close, false)).ToList()),
                ("Single prints get filled", "touched more than a matched price", sessions.SelectMany(x => x.R.SinglePrints.Select(z => (x, z))).Where(t => t.z.Low > t.x.Close || t.z.High + size < t.x.Close)
                    .Select(t => t.z.Low > t.x.Close ? new Instance(t.x, t.z.Low, true, false) : new Instance(t.x, t.z.High + size, false, false)).ToList()),
            };

            double bar = 0.05 / (claims.Count * Horizons.Length + 2);
            sb.AppendLine($"\n{s.Symbol} · {sessions.Count} sessions (New York 08:00-17:00) · rows of {size} · significance bar p < {bar:0.0000} (corrected for the {claims.Count * Horizons.Length + 2} comparisons)");
            foreach (var (name, expect, items) in claims)
            {
                if (items.Count < 30) { sb.AppendLine($"  {name}: only {items.Count} cases, too few"); continue; }
                sb.AppendLine($"  {name} (n={items.Count}): claim = {expect}");
                bool wantMore = expect.Contains("MORE", StringComparison.OrdinalIgnoreCase) || expect.Contains("more than");
                int supports = 0, contradicts = 0;
                foreach (int h in Horizons)
                {
                    var real = new List<double>();
                    var control = new List<double>();
                    foreach (var it in items)
                    {
                        decimal d = Math.Abs(it.Level - it.S.Close);
                        int from = it.S.To + 1, to = BoundFor(s, it.S.DayIndex, h);
                        if (to <= from) continue;
                        bool Hit(decimal level, bool above) => Touched(s, from, to, level, above);
                        real.Add(Hit(it.Level, it.Above) ? 1 : 0);
                        control.Add(((Hit(it.S.Close + d, true) ? 1.0 : 0.0) + (Hit(it.S.Close - d, false) ? 1.0 : 0.0)) / 2);
                    }

                    var diff = real.Select((r, i) => r - control[i]).ToList();
                    var (mean, t) = EdgeFinder.Stats(diff);
                    bool pass = EdgeFinder.TwoSidedP(t) < bar;
                    if (pass) { if ((mean > 0) == wantMore) supports++; else contradicts++; }
                    sb.AppendLine($"      within {h} day{(h > 1 ? "s" : " ")}: happened {100 * real.Average():0}% vs matched price {100 * control.Average():0}% · excess {100 * mean:+0;-0;0} points (t {t:0.0}){(pass ? "   <-- " + (mean > 0 ? "MORE than chance" : "LESS than chance") : "")}");
                }

                Ledger.Record($"claim|{s.Symbol}|{name}", $"{s.Symbol}: {name}", supports > 0 && contradicts == 0 ? "supported" : contradicts > 0 ? "not supported" : "not supported",
                    supports > 0 && contradicts == 0 ? $"stood out at {supports} of {Horizons.Length} horizons" : contradicts > 0 ? "the opposite of the claim stood out" : "no horizon stood out from a matched price", $"{items.Count} cases, {sessions.Count} sessions of 1-minute candles");
            }

            sb.AppendLine().AppendLine(Acceptance(s, sessions, size));
            sb.AppendLine().AppendLine(NodeSpeed(s, sessions, size));
        }

        if (!any) sb.AppendLine("\nNo cached 1-minute candles with enough history (open a chart with a long history first).");
        sb.AppendLine("\nA claim that does not stand out here may still be true in some conditions; and one that does stand out is a statement about the past 13 months of one market.");
        return sb.ToString();
    }

    // ----------------------------------------------------------------------------------------------- acceptance outside value

    private static string Acceptance(ResearchHistory.Series s, List<Sess> sessions, decimal size)
    {
        var sb = new StringBuilder("ACCEPTANCE OUTSIDE THE PREVIOUS VALUE: price held beyond the previous session's volume value area for 2 periods (1 hour)\n");
        var rows = new List<(string State, bool ClosedBeyond, double Continuation)>();
        for (int i = 1; i < sessions.Count; i++)
        {
            var prev = sessions[i - 1];
            if (sessions[i].DayIndex - prev.DayIndex > 3) continue; // across a holiday gap the "previous" session is stale
            var cur = sessions[i];
            decimal refAbove = prev.R.VolumeVah + size, refBelow = prev.R.VolumeVal;
            foreach (bool above in new[] { true, false })
            {
                decimal level = above ? refAbove : refBelow;
                var periods = new List<(decimal Close, decimal Beyond)>();
                decimal total = 0;
                int slotCount = (EndMinute - StartMinute) / PeriodMinutes;
                for (int k = 0; k < slotCount; k++)
                {
                    int a = -1, b = -1;
                    decimal beyond = 0;
                    for (int j = cur.From; j <= cur.To; j++)
                    {
                        if ((s.Minute[j] - StartMinute) / PeriodMinutes != k) continue;
                        if (a < 0) a = j;
                        b = j;
                        var bar = s.Bars[j];
                        total += bar.Volume;
                        decimal range = bar.High - bar.Low;
                        decimal share = range <= 0 ? ((above ? bar.High >= level : bar.Low < level) ? 1m : 0m) : above ? Math.Clamp((bar.High - level) / range, 0, 1) : Math.Clamp((level - bar.Low) / range, 0, 1);
                        beyond += bar.Volume * share;
                    }

                    if (a < 0) continue;
                    periods.Add((s.Bars[b].Close, beyond));
                    var r = TradingHub.Data.Acceptance.Evaluate(above, level, periods, total);
                    if (r.State is "accepted" or "drifting")
                    {
                        decimal closeNow = s.Bars[b].Close, sessionClose = cur.Close;
                        bool closedBeyond = above ? sessionClose > level : sessionClose < level;
                        double move = (double)((sessionClose - closeNow) / s.Tick) * (above ? 1 : -1);
                        rows.Add((r.State, closedBeyond, move));
                        break;
                    }
                }
            }
        }

        foreach (var state in new[] { "accepted", "drifting" })
        {
            var set = rows.Where(r => r.State == state).ToList();
            if (set.Count == 0) { sb.AppendLine($"  {state}: no cases"); continue; }
            int closed = set.Count(r => r.ClosedBeyond);
            var (lo, hi) = AuctionStats.Wilson(closed, set.Count);
            var (mean, t) = EdgeFinder.Stats(set.Select(r => r.Continuation).ToList());
            sb.AppendLine($"  {state}: {set.Count} cases · closed the session still beyond the level {100.0 * closed / set.Count:0}% (95% range {100 * lo:0}-{100 * hi:0}%) · from that moment on price moved {mean:+0;-0;0} ticks on average in the direction of the break (t {t:0.0})");
        }

        sb.AppendLine("  The method says accepted should continue and drifting should slide back: a clear gap between the two lines supports it.");
        return sb.ToString().TrimEnd();
    }

    // ----------------------------------------------------------------------------------------------- nodes

    private static string NodeSpeed(ResearchHistory.Series s, List<Sess> sessions, decimal size)
    {
        var sb = new StringBuilder("LOW VOLUME NODES: does price move through them faster than through high volume nodes? (nodes from the previous 5 sessions pooled; time spent in the zone on its first visit the next trading day)\n");
        var visits = NodeVisits(s, sessions, size);
        int l = visits.Count(v => v.Lvn), h = visits.Count(v => !v.Lvn);
        if (l < 30 || h < 30)
        {
            Ledger.Record($"claim|{s.Symbol}|lvn-speed", $"{s.Symbol}: price crosses low volume nodes faster than high volume nodes", "too few cases", $"{l} and {h} visits", "1-minute candles");
            return sb.Append($"  not enough visits (low volume nodes {l}, high volume nodes {h})").ToString();
        }

        var (_, t) = Compare(visits, out var ml, out var mh);
        bool supported = t <= -2 && ml < mh;
        sb.AppendLine($"  low volume nodes: {l} visits, typical time inside {ml:0} min · high volume nodes: {h} visits, {mh:0} min · difference in log time t {t:0.0}"
                      + (Math.Abs(t) < 2 ? "  (no clear difference)" : t < 0 ? "  <-- price does pass through low volume nodes faster" : "  <-- price lingers MORE in low volume nodes"));
        Ledger.Record($"claim|{s.Symbol}|lvn-speed", $"{s.Symbol}: price crosses low volume nodes faster than high volume nodes", supported ? "supported" : "not supported", $"{ml:0} vs {mh:0} minutes inside, t {t:0.0}", $"{l + h} node visits, {sessions.Count} sessions of 1-minute candles");
        sb.AppendLine(NodeStability(s, sessions, size));
        sb.Append(NodeForward(s, supported));
        return sb.ToString().TrimEnd();
    }

    // ----------------------------------------------------------------------------------------------- open location and day types

    private static string OpenTypeOf(Sess prev, Sess cur, decimal size)
    {
        decimal vah = prev.R.VolumeVah + size, val = prev.R.VolumeVal;
        return cur.Open > vah ? "above" : cur.Open < val ? "below" : "inside";
    }

    public static string OpenAndDayTypes()
    {
        var sb = new StringBuilder("OPEN LOCATION AND DAY TYPES: what happened in past sessions, by where the session opened relative to the previous session's volume value area\n");
        bool any = false;
        foreach (var s in ResearchHistory.Load())
        {
            var sessions = Sessions(s, out var size);
            if (sessions.Count < 80) continue;
            any = true;
            decimal tick = s.Tick;
            var stats = new AuctionStats { Symbol = s.Symbol, Sessions = sessions.Count, From = sessions[0].Day.ToString("yyyy-MM-dd"), To = sessions[^1].Day.ToString("yyyy-MM-dd"), Basis = "New York 08:00-17:00 sessions built from 1-minute candles; volume spread evenly over each candle's range" };
            var ibTicks = sessions.Select(x => (double)((x.R.IbHigh + size - x.R.IbLow) / tick)).OrderBy(v => v).ToList();
            stats.IbCuts = new[] { ibTicks[ibTicks.Count / 3], ibTicks[ibTicks.Count * 2 / 3] };
            for (int i = 1; i < sessions.Count; i++)
            {
                var prev = sessions[i - 1];
                var cur = sessions[i];
                if (cur.DayIndex - prev.DayIndex > 3) continue;
                string type = OpenTypeOf(prev, cur, size);
                decimal vah = prev.R.VolumeVah + size, val = prev.R.VolumeVal, vpoc = prev.R.VolumePoc + size / 2;
                var bucket = Get(stats.Open, type);
                bucket.N++;
                bool TouchedPrice(decimal level) => Touched(s, cur.From, cur.To, level, true) && Touched(s, cur.From, cur.To, level, false);
                bool closeInside = cur.Close >= val && cur.Close <= vah;
                if (type == "inside")
                {
                    Add(bucket, "touchedPreviousPoc", TouchedPrice(vpoc));
                    decimal far = Math.Abs(cur.Open - val) < Math.Abs(vah - cur.Open) ? vah : val;
                    Add(bucket, "touchedFarEdgeOfValue", far == vah ? cur.High >= vah : cur.Low <= val);
                    Add(bucket, "closedInsideValue", closeInside);
                }
                else
                {
                    bool above = type == "above";
                    Add(bucket, "tradedBackIntoValue", above ? cur.Low <= vah : cur.High >= val);
                    Add(bucket, "closedBackInsideValue", closeInside);
                    Add(bucket, "closedStillBeyond", above ? cur.Close > vah : cur.Close < val);
                    var acc = FirstAcceptance(s, cur, above, above ? vah : val);
                    if (acc != null) Bump(bucket, acc);
                }

                // the day type, with the size of the first hour
                double ib = (double)((cur.R.IbHigh + size - cur.R.IbLow) / tick);
                string ibKey = ib <= stats.IbCuts[0] ? "small" : ib >= stats.IbCuts[1] ? "large" : "medium";
                foreach (var key in new[] { $"{type}|{ibKey}", $"any|{ibKey}", type, "all" })
                {
                    var b = Get(stats.DayTypes, key);
                    b.N++;
                    Bump(b, "shape" + cur.R.Shape);
                    Bump(b, cur.R.ExtendedUp && cur.R.ExtendedDown ? "extendedBoth" : cur.R.ExtendedUp ? "extendedUp" : cur.R.ExtendedDown ? "extendedDown" : "inBalanceInsideIb");
                    decimal pos = cur.High > cur.Low ? (cur.Close - cur.Low) / (cur.High - cur.Low) : 0.5m;
                    Bump(b, pos >= 0.67m ? "closedTop" : pos <= 0.33m ? "closedBottom" : "closedMiddle");
                }
            }

            stats.Save();
            sb.AppendLine($"\n{s.Symbol} · {sessions.Count} sessions {stats.From} to {stats.To} · saved for the Auction map indicator");
            foreach (var type in new[] { "inside", "above", "below" })
            {
                if (!stats.Open.TryGetValue(type, out var b)) continue;
                sb.AppendLine($"  opened {type} the previous value: {b.N} sessions ({100.0 * b.N / sessions.Count:0}%)");
                foreach (var (key, count) in b.Counts.OrderBy(k => k.Key))
                {
                    var (lo, hi) = AuctionStats.Wilson(count, b.N);
                    sb.AppendLine($"      {Pretty(key)}: {100.0 * count / b.N:0}% (95% range {100 * lo:0}-{100 * hi:0}%, {count} of {b.N})");
                }
            }

            sb.AppendLine($"  day types by the size of the first hour (small ≤ {stats.IbCuts[0]:0} ticks, large ≥ {stats.IbCuts[1]:0}):");
            foreach (var key in new[] { "any|small", "any|medium", "any|large" })
            {
                if (!stats.DayTypes.TryGetValue(key, out var b)) continue;
                sb.AppendLine($"      {key.Substring(4)} first hour ({b.N} sessions): D {100 * b.Rate("shapeD"):0}% · P {100 * b.Rate("shapeP"):0}% · b {100 * b.Rate("shapeb"):0}% · trend {100 * b.Rate("shapeTrend"):0}% · extended up {100 * b.Rate("extendedUp"):0}% / down {100 * b.Rate("extendedDown"):0}% / both {100 * b.Rate("extendedBoth"):0}% / neither {100 * b.Rate("inBalanceInsideIb"):0}%");
            }
        }

        if (!any) sb.AppendLine("\nNo cached 1-minute candles with enough history (open a chart with a long history first).");
        sb.AppendLine("\nThe Auction map shows the matching lines for the live session. The sessions here are New York 08:00-17:00; if your chart's session differs, read them as a guide only.");
        return sb.ToString();
    }

    private static string Pretty(string key) => key switch
    {
        "touchedPreviousPoc" => "price traded through the previous volume POC",
        "touchedFarEdgeOfValue" => "price reached the far edge of the previous value",
        "closedInsideValue" => "the session closed inside the previous value",
        "tradedBackIntoValue" => "price traded back to the value edge",
        "closedBackInsideValue" => "the session closed back inside the previous value (rejected)",
        "closedStillBeyond" => "the session closed still beyond the previous value",
        "acceptedAfterTwoPeriods" => "was ACCEPTED (2 periods beyond with volume building)",
        "driftedAfterTwoPeriods" => "DRIFTED (2 periods beyond without the volume)",
        _ => key,
    };

    private static AuctionStats.Bucket Get(Dictionary<string, AuctionStats.Bucket> map, string key) => map.TryGetValue(key, out var b) ? b : map[key] = new AuctionStats.Bucket();

    private static void Add(AuctionStats.Bucket b, string key, bool yes)
    {
        if (!b.Counts.ContainsKey(key)) b.Counts[key] = 0;
        if (yes) b.Counts[key]++;
    }

    private static void Bump(AuctionStats.Bucket b, string key) => b.Counts[key] = b.Counts.GetValueOrDefault(key) + 1;

    private static string? FirstAcceptance(ResearchHistory.Series s, Sess cur, bool above, decimal level)
    {
        var periods = new List<(decimal Close, decimal Beyond)>();
        decimal total = 0;
        for (int k = 0; k < (EndMinute - StartMinute) / PeriodMinutes; k++)
        {
            int b = -1;
            decimal beyond = 0;
            for (int j = cur.From; j <= cur.To; j++)
            {
                if ((s.Minute[j] - StartMinute) / PeriodMinutes != k) continue;
                b = j;
                var bar = s.Bars[j];
                total += bar.Volume;
                decimal range = bar.High - bar.Low;
                decimal share = range <= 0 ? ((above ? bar.High >= level : bar.Low < level) ? 1m : 0m) : above ? Math.Clamp((bar.High - level) / range, 0, 1) : Math.Clamp((level - bar.Low) / range, 0, 1);
                beyond += bar.Volume * share;
            }

            if (b < 0) continue;
            periods.Add((s.Bars[b].Close, beyond));
            var r = TradingHub.Data.Acceptance.Evaluate(above, level, periods, total);
            if (r.State == "accepted") return "acceptedAfterTwoPeriods";
            if (r.State == "drifting") return "driftedAfterTwoPeriods";
        }

        return null;
    }
}
