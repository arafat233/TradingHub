using System.Text;
using TradingHub.Data;
using TradingHub.Feeds;

namespace TradingHub.App.Services;

/// <summary>
/// Research on months of 1-minute candles (the cache the charts already keep, 13 months for gold): when trading is feasible at all
/// (range against cost), whether simple time-of-day and session effects exist, and whether simple rules (opening-range breakout,
/// fading the previous day's high / low, sweep reversals, round numbers) pay, judged without fooling itself: realistic costs,
/// findings chosen on the first 60% of days and checked on the last 40%, and a stricter bar for the number of variants tried.
/// </summary>
internal static class ResearchHistory
{
    internal sealed class Series
    {
        public string Symbol = "";
        public decimal Tick, PointValue;
        public List<Kline> Bars = new();
        public DateTime[] Local = Array.Empty<DateTime>(); // New York time of every candle
        public int[] Minute = Array.Empty<int>();          // minutes since local midnight
        public DateOnly[] Day = Array.Empty<DateOnly>();   // trading day: the New York day that starts at 18:00 the evening before
        public List<(DateOnly Day, int From, int To)> Days = new();
    }

    internal static TimeZoneInfo NewYork()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (Exception) { /* try the next name */ }
        return TimeZoneInfo.Utc;
    }

    /// <summary>Candles to a series with New York time, trading days and their spans; null if there are fewer than <paramref name="minDays"/> usable days.</summary>
    internal static Series? Build(string symbol, decimal tick, decimal point, List<Kline> bars, int minDays)
    {
        var tz = NewYork();
        var s = new Series { Symbol = symbol, Tick = tick, PointValue = point, Bars = bars };
        s.Local = new DateTime[bars.Count];
        s.Minute = new int[bars.Count];
        s.Day = new DateOnly[bars.Count];
        for (int i = 0; i < bars.Count; i++)
        {
            var utc = DateTimeOffset.FromUnixTimeMilliseconds(bars[i].OpenTimeMs).UtcDateTime;
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            s.Local[i] = local;
            s.Minute[i] = local.Hour * 60 + local.Minute;
            s.Day[i] = DateOnly.FromDateTime(local.AddHours(6));
        }

        int from = 0;
        for (int i = 1; i <= bars.Count; i++)
            if (i == bars.Count || s.Day[i] != s.Day[from])
            {
                if (i - from >= 300 && s.Day[from].DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) s.Days.Add((s.Day[from], from, i - 1));
                from = i;
            }

        return s.Days.Count >= minDays ? s : null;
    }

    internal static IEnumerable<Series> Load()
    {
        var root = Path.Combine(AppPaths.Root, "Klines");
        if (!Directory.Exists(root)) yield break;
        var tz = NewYork();
        foreach (var feedDir in Directory.GetDirectories(root))
            foreach (var symDir in Directory.GetDirectories(feedDir))
            {
                string feed = Path.GetFileName(feedDir), symbol = Path.GetFileName(symDir);
                if (feed != "lse") continue; // the tick size and contract value of other feeds are not known here
                var bars = KlineHistory.ReadCached(root, feed, symbol);
                if (bars.Count < 20000) continue;
                var (tick, point, _) = TradingHub.Feeds.LondonStrategicEdge.LseFeed.SpecOf("", symbol);
                var s = Build(symbol, tick, point, bars, 60);
                if (s != null) yield return s;
            }
    }

    private static double CostTicks(AppServices? svc, Series s)
    {
        // spread 1 tick + slippage 1 tick + commission converted to ticks
        double commission = (double)(svc?.Settings.CommissionPerRoundTrip ?? 4m);
        double tickValue = (double)(s.Tick * s.PointValue);
        return 2 + (tickValue > 0 ? commission / tickValue : 0);
    }

    private static decimal CommissionTicks(AppServices? svc, Series s)
    {
        decimal tickValue = s.Tick * s.PointValue;
        return tickValue > 0 ? (svc?.Settings.CommissionPerRoundTrip ?? 4m) / tickValue : 0;
    }

    public static string DebugRound(string symbol)
    {
        var sb = new StringBuilder();
        foreach (var s in Load().Where(x => x.Symbol == symbol))
        {
            int step = 10, stop = 40, target = 80, shown = 0, targets = 0, stops = 0, times = 0, total = 0;
            decimal tick = s.Tick, shift = 3.7m;
            foreach (var (day, from, to) in s.Days.Skip(100).Take(20))
            {
                var used = new HashSet<decimal>();
                for (int i = from + 1; i < to; i++)
                {
                    var b = s.Bars[i];
                    decimal prevClose = s.Bars[i - 1].Close;
                    decimal up = Math.Floor((b.High - shift) / step) * step + shift;
                    if (!(prevClose < up && b.High >= up + tick && used.Add(up))) continue;
                    decimal fill = Math.Max(up, b.Open);
                    double gross = CandleSim.Run(s.Bars, i, false, fill, fill + stop * tick, fill - target * tick, 240, tick, 1, firstCandleStopOnly: true);
                    total++;
                    if (gross >= target - 0.5) targets++; else if (gross <= -stop) stops++; else times++;
                    if (shown++ < 8) sb.AppendLine($"  {day} {s.Local[i]:HH:mm} level {up} candle O{b.Open} H{b.High} L{b.Low} C{b.Close} prevClose {prevClose} fill {fill} -> {gross:+0.0;-0.0} ticks");
                }
            }

            sb.AppendLine($"{symbol} shorts at crossed levels: {total} trades · target {targets} ({100.0 * targets / total:0}%) · stop {stops} ({100.0 * stops / total:0}%) · other {times}");
        }

        return sb.ToString();
    }

    /// <summary>Data check: how spiky are the 1-minute candles, and do they reverse? (a spike that reverses can fake a mean-reversion edge)</summary>
    public static string WickCheck()
    {
        var sb = new StringBuilder("CANDLE SPIKE CHECK\n");
        foreach (var s in Load())
        {
            int n = s.Bars.Count;
            var ranges = new List<double>();
            double up = 0, down = 0, big = 0, bigRevert = 0, sumNext = 0;
            for (int i = 1; i < n - 1; i++)
            {
                var b = s.Bars[i];
                double range = (double)((b.High - b.Low) / s.Tick);
                if (range <= 0) continue;
                ranges.Add(range);
                double upper = (double)((b.High - Math.Max(b.Open, b.Close)) / s.Tick), lower = (double)((Math.Min(b.Open, b.Close) - b.Low) / s.Tick);
                up += upper / range;
                down += lower / range;
                if (upper > 0.7 * range && range > 20)
                {
                    big++;
                    // the next candle closes back down by more than half the spike?
                    if ((double)((b.Close - s.Bars[i + 1].Close) / s.Tick) > upper * 0.5) bigRevert++;
                    sumNext += (double)((s.Bars[i + 1].Close - b.Close) / s.Tick);
                }
            }

            ranges.Sort();
            sb.AppendLine($"{s.Symbol}: {n:N0} candles · median range {ranges[ranges.Count / 2]:0} ticks, 99.9th percentile {ranges[(int)(ranges.Count * 0.999)]:0} · average upper wick {100 * up / ranges.Count:0}% / lower {100 * down / ranges.Count:0}% of the range · candles that are mostly an upper spike (range > 20 ticks): {big:N0}, of which {100 * (big == 0 ? 0 : bigRevert / big):0}% reversed by more than half within a candle, next candle moved {(big == 0 ? 0 : sumNext / big):+0.0;-0.0;0.0} ticks on average");
        }

        return sb.ToString();
    }

    // ----------------------------------------------------------------------------------------------- 1. cost map

    public static string CostMap(AppServices? svc)
    {
        var sb = new StringBuilder("COST MAP: typical 30-minute range against what a round trip costs (New York time)\n");
        bool any = false;
        foreach (var s in Load())
        {
            any = true;
            double cost = CostTicks(svc, s);
            var ranges = new List<double>[48];
            for (int i = 0; i < 48; i++) ranges[i] = new List<double>();
            foreach (var (_, from, to) in s.Days)
            {
                int i = from;
                while (i <= to)
                {
                    int bucket = s.Minute[i] / 30;
                    decimal hi = s.Bars[i].High, lo = s.Bars[i].Low;
                    int j = i + 1;
                    while (j <= to && s.Minute[j] / 30 == bucket && j - i < 30) { hi = Math.Max(hi, s.Bars[j].High); lo = Math.Min(lo, s.Bars[j].Low); j++; }
                    if (j - i >= 20) ranges[bucket].Add((double)((hi - lo) / s.Tick));
                    i = j;
                }
            }

            sb.AppendLine($"\n{s.Symbol} · {s.Days.Count} trading days · round trip costs about {cost:0.0} ticks (1 spread + 1 slippage + {CommissionTicks(svc, s):0.0} commission)");
            sb.AppendLine("   NY time   your time   typical 30-min range   range / cost");
            var tzLocal = TimeZoneInfo.Local;
            var tzNy = NewYork();
            foreach (var r in ranges) r.Sort();
            var medians = ranges.Where(r => r.Count >= 20).Select(r => r[r.Count / 2]).OrderBy(v => v).ToList();
            double wide = medians.Count == 0 ? 0 : medians[(int)(medians.Count * 0.75)], thin = medians.Count == 0 ? 0 : medians[(int)(medians.Count * 0.25)];
            for (int b = 0; b < 48; b++)
            {
                if (ranges[b].Count < 20) continue;
                double median = ranges[b][ranges[b].Count / 2];
                var nyTime = DateTime.Today.AddMinutes(b * 30);
                var mine = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(nyTime, DateTimeKind.Unspecified), tzNy, tzLocal);
                double ratio = median / cost;
                sb.AppendLine($"   {nyTime:HH:mm}     {mine:HH:mm}       {median,8:0} ticks         {ratio,5:0.0}x  {new string('#', (int)Math.Min(40, median / (medians.Count == 0 ? 1 : medians[^1]) * 40))}{(median >= wide ? "  <-- widest quarter" : median <= thin ? "  <-- thinnest quarter" : "")}");
            }
        }

        if (!any) sb.AppendLine("  no cached 1-minute candles found (open a chart with a long history first)");
        sb.AppendLine("\nCosts are small next to these ranges for gold, so costs are not what stops small tick-sized stops and targets from paying: noise is. The widest hours give the most room for a target; the thinnest are where a stop is hit by noise soonest. The commission is Settings → Trading.");
        return sb.ToString();
    }

    // ----------------------------------------------------------------------------------------------- 2. seasonality

    private sealed record Obs(DateOnly Day, double Value);

    public static string Seasonality()
    {
        var sb = new StringBuilder("SEASONALITY: do simple time-of-day and session effects exist? (New York time; each test has one observation per trading day)\n");
        bool any = false;
        foreach (var s in Load())
        {
            any = true;
            var tests = new List<(string Name, List<Obs> Obs)>();
            double Move(int a, int b) => (double)((s.Bars[b].Close - s.Bars[a].Open) / s.Tick);
            int At(int from, int to, int minute) { for (int i = from; i <= to; i++) if (s.Minute[i] >= minute && s.Minute[i] < minute + 5) return i; return -1; }
            int LastBefore(int from, int to, int minute) { for (int i = to; i >= from; i--) if (s.Minute[i] < minute) return i; return -1; }

            for (int h = 0; h < 24; h++)
            {
                var o = new List<Obs>();
                foreach (var (day, from, to) in s.Days)
                {
                    int a = At(from, to, h * 60), b = LastBefore(from, to, h * 60 + 60);
                    if (a >= 0 && b > a && s.Minute[b] >= h * 60 + 45) o.Add(new Obs(day, Move(a, b)));
                }

                tests.Add(($"drift in the {h:00}:00 hour", o));
            }

            foreach (DayOfWeek w in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
                tests.Add(($"drift of the whole day on {w}s", s.Days.Where(d => d.Day.DayOfWeek == w).Select(d => new Obs(d.Day, Move(d.From, d.To))).ToList()));

            foreach (var (label, minute) in new[] { ("London open 03:00", 180), ("pre-US 08:00", 480), ("US cash 09:30", 570) })
            {
                var o = new List<Obs>();
                foreach (var (day, from, to) in s.Days)
                {
                    int a = At(from, to, minute), b = At(from, to, minute + 30), c = At(from, to, minute + 90);
                    if (a >= 0 && b > a && c > b) o.Add(new Obs(day, Math.Sign(Move(a, b - 1)) * Move(b, c - 1)));
                }

                tests.Add(($"first 30 minutes after {label}: does the next 60 continue them?", o));
            }

            var prev = new List<Obs>();
            for (int d = 1; d < s.Days.Count; d++) prev.Add(new Obs(s.Days[d].Day, Math.Sign(Move(s.Days[d - 1].From, s.Days[d - 1].To)) * Move(s.Days[d].From, s.Days[d].To)));
            tests.Add(("yesterday's direction: does today continue it?", prev));
            var asia = new List<Obs>();
            foreach (var (day, from, to) in s.Days)
            {
                int a = At(from, to, 18 * 60), asiaEnd = At(from, to, 180), lon = At(from, to, 480);
                // the trading day starts at 18:00 the evening before: its Asian part comes first in the candles
                int first = from;
                if (asiaEnd > first && lon > asiaEnd) asia.Add(new Obs(day, Math.Sign(Move(first, asiaEnd - 1)) * Move(asiaEnd, lon - 1)));
            }

            tests.Add(("Asian session direction: does London (03:00-08:00) continue it?", asia));

            int m = tests.Count;
            double bar = 0.05 / m;
            sb.AppendLine($"\n{s.Symbol} · {s.Days.Count} trading days ({s.Days[0].Day:yyyy-MM-dd} to {s.Days[^1].Day:yyyy-MM-dd}) · {m} tests, so a result needs p < {bar:0.0000} on the first 60% of the days and the same sign on the last 40%");
            var rows = new List<(string Name, int N, double FoundMean, double FoundT, int NK, double KeptMean, double KeptT, bool Pass)>();
            foreach (var (name, obs) in tests)
            {
                var o = obs.OrderBy(x => x.Day).ToList();
                int cut = (int)Math.Round(o.Count * 0.6);
                var (fm, ft) = EdgeFinder.Stats(o.Take(cut).Select(x => x.Value).ToList());
                var (km, kt) = EdgeFinder.Stats(o.Skip(cut).Select(x => x.Value).ToList());
                bool pass = cut >= 40 && o.Count - cut >= 20 && EdgeFinder.TwoSidedP(ft) < bar && Math.Sign(fm) == Math.Sign(km) && Math.Abs(kt) >= 1.0;
                rows.Add((name, o.Count, fm, ft, o.Count - cut, km, kt, pass));
            }

            var pass2 = rows.Where(r => r.Pass).ToList();
            sb.AppendLine($"  {pass2.Count} of {m} held up on the days kept back");
            Ledger.Record($"seasonality|{s.Symbol}", $"{s.Symbol}: time-of-day, weekday and session effects", pass2.Count > 0 ? "supported" : "not supported",
                pass2.Count > 0 ? $"{pass2.Count} of {m} held up: {string.Join("; ", pass2.Select(p => p.Name))}" : $"none of {m} tests held up on the days kept back", $"{s.Days.Count} trading days of 1-minute candles");
            foreach (var r in pass2) sb.AppendLine($"      PASS  {r.Name}: first part {r.FoundMean:+0.0;-0.0;0.0} ticks (t {r.FoundT:0.0}), kept part {r.KeptMean:+0.0;-0.0;0.0} (t {r.KeptT:0.0}) over {r.N} days");
            if (pass2.Count == 0)
                foreach (var r in rows.OrderByDescending(r => Math.Abs(r.FoundT)).Take(5))
                    sb.AppendLine($"      closest: {r.Name}: first part {r.FoundMean:+0.0;-0.0;0.0} (t {r.FoundT:0.0}), kept part {r.KeptMean:+0.0;-0.0;0.0} (t {r.KeptT:0.0}) over {r.N} days");
        }

        if (!any) sb.AppendLine("  no cached 1-minute candles found (open a chart with a long history first)");
        sb.AppendLine("\nA drift is in ticks per day. Costs are not subtracted here: a statistically real drift smaller than a round trip still cannot be traded.");
        return sb.ToString();
    }

    // ----------------------------------------------------------------------------------------------- 3. rule lab

    private sealed record Variant(string Name, Func<Series, AppServices?, List<RuleTrade>> Run);

    public static string RuleLab(AppServices? svc)
    {
        var sb = new StringBuilder("RULE LAB: simple rules on 1-minute candles with costs, judged on days kept back and a stricter bar for the number tried\n");
        sb.AppendLine("Costs: 1 tick slippage on stop-order entries and stops, plus the commission from Settings → Trading. A candle that reaches both stop and target counts as a stop.");
        bool any = false;
        foreach (var s in Load())
        {
            any = true;
            var variants = Variants();
            var verdicts = new List<RuleVerdict>();
            foreach (var v in variants)
            {
                var trades = v.Run(s, svc);
                verdicts.Add(RuleJudge.Judge(v.Name, trades, variants.Count));
            }

            sb.AppendLine($"\n{s.Symbol} · {s.Days.Count} trading days · {variants.Count} variants tried, so the bar is p < {0.05 / variants.Count:0.0000} on the first 60% of the days");
            // a rule about round numbers must beat the same rule on levels moved off the round numbers: if the control does as well, the rule is not about the level
            var notSpecific = new List<string>();
            for (int vi = 0; vi < verdicts.Count; vi++)
            {
                var v = verdicts[vi];
                if (!v.Passes || !v.Name.StartsWith("fade round numbers every 10")) continue;
                string tail = v.Name[v.Name.IndexOf("stop", StringComparison.Ordinal)..];
                var ctrl = verdicts.FirstOrDefault(c => c.Name.StartsWith("CONTROL") && c.Name.EndsWith(tail));
                if (ctrl != null && ctrl.MeanNet >= 0.7 * v.MeanNet)
                {
                    verdicts[vi] = v with { Passes = false };
                    notSpecific.Add($"{v.Name} ({v.MeanNet:+0.0;-0.0;0.0} ticks) — its control on shifted levels averaged {ctrl.MeanNet:+0.0;-0.0;0.0}: the result is not about round numbers");
                }
            }

            // the controls are there to be compared with, they are not findings
            var pass = verdicts.Where(v => v.Passes && !v.Name.StartsWith("CONTROL")).ToList();
            sb.AppendLine($"  {pass.Count} of {variants.Count - verdicts.Count(v => v.Name.StartsWith("CONTROL"))} held up on the days kept back");
            foreach (var n in notSpecific) sb.AppendLine($"      REJECTED  {n}. It passed the statistics, but a result that does not need the level is a quirk of the simulation or the data, not a finding.");
            Ledger.Record($"rule-lab|{s.Symbol}", $"{s.Symbol}: simple rules (opening range, previous-day fades, sweeps, round numbers, node rotation)", pass.Count > 0 ? "supported" : "not supported",
                pass.Count > 0 ? $"{pass.Count} of {variants.Count} held up: {string.Join("; ", pass.Select(p => p.Name))}" : $"none of {variants.Count} variants held up on the days kept back", $"{s.Days.Count} trading days of 1-minute candles");
            foreach (var v in pass) Line(sb, "PASS", v);
            foreach (var v in verdicts.Where(v => !v.Passes && v.Trades >= 30).OrderByDescending(v => v.FoundT).Take(pass.Count == 0 ? 8 : 3)) Line(sb, "closest", v);
            var worst = verdicts.Where(v => v.Trades >= 30).OrderBy(v => v.MeanNet).FirstOrDefault();
            if (worst != null) sb.AppendLine($"  for comparison, the worst variant averaged {worst.MeanNet:+0.0;-0.0;0.0} ticks per trade ({worst.Name}); costs alone are about {CostTicks(svc, s):0.0} ticks per trade");
        }

        if (!any) sb.AppendLine("  no cached 1-minute candles found (open a chart with a long history first)");
        sb.AppendLine("\n\"reversed\" is what the same entries would have averaged traded the other way (costs paid again): a rule with real direction skill shows a large gap between it and the real result.");
        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string tag, RuleVerdict v) =>
        sb.AppendLine($"      {tag,-8} {v.Name}: {v.Trades} trades, {v.MeanNet:+0.0;-0.0;0.0} ticks each after costs (t {v.T:0.0}); first part {v.FoundMean:+0.0;-0.0;0.0} (t {v.FoundT:0.0}), kept part {v.KeptMean:+0.0;-0.0;0.0} (t {v.KeptT:0.0}); reversed {v.ReversedMean:+0.0;-0.0;0.0}");

    private static List<Variant> Variants()
    {
        var list = new List<Variant>();
        foreach (var (label, minute) in new[] { ("London 03:00", 180), ("pre-US 08:00", 480), ("US cash 09:30", 570) })
            foreach (int n in new[] { 15, 30, 60 })
                foreach (double r in new[] { 1.0, 2.0 })
                    list.Add(new Variant($"opening-range breakout {label}, {n} min range, target {r:0.#}x the range", (s, svc) => Orb(s, svc, minute, n, r)));
        foreach (var (stop, target) in new[] { (20, 40), (30, 60), (50, 100) })
            list.Add(new Variant($"fade the previous day's high/low, stop {stop} target {target} ticks", (s, svc) => PrevDayFade(s, svc, stop, target)));
        foreach (int beyond in new[] { 5, 15 })
            foreach (double r in new[] { 1.5, 2.0 })
                list.Add(new Variant($"sweep of the previous day's high/low by {beyond}+ ticks then back inside, target {r:0.#}R", (s, svc) => Sweep(s, svc, beyond, r)));
        foreach (int step in new[] { 10, 25 })
            foreach (var (stop, target) in new[] { (20, 40), (40, 80) })
                list.Add(new Variant($"fade round numbers every {step}, stop {stop} target {target} ticks", (s, svc) => RoundNumber(s, svc, step, stop, target)));
        foreach (var (stop, target) in new[] { (20, 40), (40, 80) })
            list.Add(new Variant($"CONTROL: the same fade of levels every 10 moved 37% off the round numbers, stop {stop} target {target} ticks", (s, svc) => RoundNumber(s, svc, 10, stop, target, 0.37)));
        foreach (double fraction in new[] { 0.5, 1.0 })
            list.Add(new Variant($"leave a high volume node toward the next one, target the next node, stop at {fraction:0.#}x the distance back", (s, svc) => ResearchAuction.NodeRotation(s, svc, fraction)));
        return list;
    }

    private static List<RuleTrade> Orb(Series s, AppServices? svc, int anchor, int minutes, double reward)
    {
        var trades = new List<RuleTrade>();
        decimal tick = s.Tick, comm = CommissionTicks(svc, s);
        foreach (var (day, from, to) in s.Days)
        {
            int a = -1, end = -1;
            for (int i = from; i <= to; i++)
            {
                if (a < 0 && s.Minute[i] >= anchor && s.Minute[i] < anchor + 5) a = i;
                if (a >= 0 && s.Minute[i] >= anchor + minutes) { end = i; break; }
            }

            if (a < 0 || end < 0) continue;
            decimal hi = decimal.MinValue, lo = decimal.MaxValue;
            for (int i = a; i < end; i++) { hi = Math.Max(hi, s.Bars[i].High); lo = Math.Min(lo, s.Bars[i].Low); }
            decimal range = hi - lo;
            if (range < tick * 4) continue;
            for (int i = end; i <= to && s.Minute[i] < anchor + minutes + 180; i++)
            {
                bool up = s.Bars[i].High >= hi, down = s.Bars[i].Low <= lo;
                if (up && down) break; // both sides in one candle: the order is unknown
                if (!up && !down) continue;
                bool isLong = up;
                decimal entry = (isLong ? Math.Max(hi, s.Bars[i].Open) : Math.Min(lo, s.Bars[i].Open)) + (isLong ? tick : -tick); // stop-order entry, one tick of slippage
                decimal stop = isLong ? lo : hi, target = isLong ? entry + range * (decimal)reward : entry - range * (decimal)reward;
                if (i + 1 >= s.Bars.Count) break;
                double gross = CandleSim.Run(s.Bars, i, isLong, entry, stop, target, 240, tick, 1, firstCandleStopOnly: true);
                trades.Add(new RuleTrade(s.Local[i].Date, s.Local[i], gross - (double)comm, 2 + (double)comm));
                break;
            }
        }

        return trades;
    }

    private static List<RuleTrade> PrevDayFade(Series s, AppServices? svc, int stop, int target)
    {
        var trades = new List<RuleTrade>();
        decimal tick = s.Tick, comm = CommissionTicks(svc, s);
        for (int d = 1; d < s.Days.Count; d++)
        {
            var (_, pf, pt) = s.Days[d - 1];
            decimal pdh = decimal.MinValue, pdl = decimal.MaxValue;
            for (int i = pf; i <= pt; i++) { pdh = Math.Max(pdh, s.Bars[i].High); pdl = Math.Min(pdl, s.Bars[i].Low); }
            var (_, from, to) = s.Days[d];
            bool doneHigh = false, doneLow = false;
            for (int i = from; i < to && !(doneHigh && doneLow); i++)
            {
                // a resting order at the level only fills once price trades one tick beyond it (queue position)
                if (!doneHigh && s.Bars[i].High >= pdh + tick)
                {
                    doneHigh = true;
                    decimal fill = Math.Max(pdh, s.Bars[i].Open); // a resting order is filled at the level, or at the open if the candle gapped through it
                    double gross = CandleSim.Run(s.Bars, i, false, fill, fill + stop * tick, fill - target * tick, 240, tick, 1, firstCandleStopOnly: true);
                    trades.Add(new RuleTrade(s.Local[i].Date, s.Local[i], gross - (double)comm, 1 + (double)comm));
                }

                if (!doneLow && s.Bars[i].Low <= pdl - tick)
                {
                    doneLow = true;
                    decimal fill = Math.Min(pdl, s.Bars[i].Open);
                    double gross = CandleSim.Run(s.Bars, i, true, fill, fill - stop * tick, fill + target * tick, 240, tick, 1, firstCandleStopOnly: true);
                    trades.Add(new RuleTrade(s.Local[i].Date, s.Local[i], gross - (double)comm, 1 + (double)comm));
                }
            }
        }

        return trades;
    }

    private static List<RuleTrade> Sweep(Series s, AppServices? svc, int beyondTicks, double reward)
    {
        var trades = new List<RuleTrade>();
        decimal tick = s.Tick, comm = CommissionTicks(svc, s);
        for (int d = 1; d < s.Days.Count; d++)
        {
            var (_, pf, pt) = s.Days[d - 1];
            decimal pdh = decimal.MinValue, pdl = decimal.MaxValue;
            for (int i = pf; i <= pt; i++) { pdh = Math.Max(pdh, s.Bars[i].High); pdl = Math.Min(pdl, s.Bars[i].Low); }
            var (_, from, to) = s.Days[d];
            bool shortDone = false, longDone = false;
            int sweptHigh = -1, sweptLow = -1;
            decimal highest = 0, lowest = 0;
            for (int i = from; i < to - 1; i++)
            {
                var b = s.Bars[i];
                if (!shortDone && b.High >= pdh + beyondTicks * tick) { if (sweptHigh < 0) { sweptHigh = i; highest = b.High; } highest = Math.Max(highest, b.High); }
                if (!longDone && b.Low <= pdl - beyondTicks * tick) { if (sweptLow < 0) { sweptLow = i; lowest = b.Low; } lowest = Math.Min(lowest, b.Low); }
                if (sweptHigh >= 0 && !shortDone)
                {
                    if (b.Close < pdh)
                    {
                        decimal entry = b.Close - tick, stop = highest + tick, tgt = entry - (stop - entry) * (decimal)reward;
                        double gross = CandleSim.Run(s.Bars, i + 1, false, entry, stop, tgt, 240, tick, 1);
                        trades.Add(new RuleTrade(s.Local[i].Date, s.Local[i], gross - (double)comm, 2 + (double)comm));
                        shortDone = true;
                    }
                    else if (i - sweptHigh >= 3) sweptHigh = -1;
                }

                if (sweptLow >= 0 && !longDone)
                {
                    if (b.Close > pdl)
                    {
                        decimal entry = b.Close + tick, stop = lowest - tick, tgt = entry + (entry - stop) * (decimal)reward;
                        double gross = CandleSim.Run(s.Bars, i + 1, true, entry, stop, tgt, 240, tick, 1);
                        trades.Add(new RuleTrade(s.Local[i].Date, s.Local[i], gross - (double)comm, 2 + (double)comm));
                        longDone = true;
                    }
                    else if (i - sweptLow >= 3) sweptLow = -1;
                }
            }
        }

        return trades;
    }

    private static List<RuleTrade> RoundNumber(Series s, AppServices? svc, int step, int stop, int target, double offset = 0)
    {
        decimal shift = (decimal)offset * step; // a placebo: levels moved off the round numbers, everything else the same
        var trades = new List<RuleTrade>();
        decimal tick = s.Tick, comm = CommissionTicks(svc, s);
        foreach (var (_, from, to) in s.Days)
        {
            var used = new HashSet<decimal>();
            for (int i = from + 1; i < to; i++)
            {
                var b = s.Bars[i];
                decimal prevClose = s.Bars[i - 1].Close;
                // a round number crossed upwards from below: fade it short; crossed downwards from above: fade it long (fills only if price trades a tick beyond)
                decimal up = Math.Floor((b.High - shift) / step) * step + shift;
                if (prevClose < up && b.High >= up + tick && used.Add(up))
                {
                    decimal fill = Math.Max(up, b.Open); // filled at the level, or at the open when the candle gapped through it
                    double gross = CandleSim.Run(s.Bars, i, false, fill, fill + stop * tick, fill - target * tick, 240, tick, 1, firstCandleStopOnly: true);
                    trades.Add(new RuleTrade(s.Local[i].Date, s.Local[i], gross - (double)comm, 1 + (double)comm));
                    continue;
                }

                decimal down = Math.Ceiling((b.Low - shift) / step) * step + shift;
                if (prevClose > down && b.Low <= down - tick && used.Add(down))
                {
                    decimal fill = Math.Min(down, b.Open);
                    double gross = CandleSim.Run(s.Bars, i, true, fill, fill - stop * tick, fill + target * tick, 240, tick, 1, firstCandleStopOnly: true);
                    trades.Add(new RuleTrade(s.Local[i].Date, s.Local[i], gross - (double)comm, 1 + (double)comm));
                }
            }
        }

        return trades;
    }
}
