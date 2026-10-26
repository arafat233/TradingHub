using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Services;

/// <summary>
/// More ways of checking whether the big-order levels carry any information: a placebo test, a plain swing-level baseline, a
/// check of how reliable the buy/sell labels are, hypotheses that are locked before they are tested, the effect of scheduled events,
/// and what a bad stretch of paper trading can look like.
/// </summary>
internal static class ResearchExtra
{
    private static readonly int[] DefaultThresholds = { 4, 8, 12, 16, 20, 30, 40, 60, 80 };
    private const int Slip = 1, FillThrough = 1, AwayTicks = 4;
    private static readonly TimeSpan TouchWindow = TimeSpan.FromDays(2), FollowWindow = TimeSpan.FromHours(4);

    // ----------------------------------------------------------------------------------------------- helpers

    /// <summary>Paper results of fading (or following) each level with realistic fills, in ticks. Levels that would not have filled are left out.</summary>
    private static List<double> Results(LevelTools.Ticks t, IEnumerable<MinedLevel> levels, bool follow = false, int stop = 20, int target = 40, int[]? thresholds = null)
    {
        thresholds ??= DefaultThresholds;
        int iStop = Array.IndexOf(thresholds, stop), iTarget = Array.IndexOf(thresholds, target);
        var list = new List<double>();
        foreach (var l in levels)
        {
            var trace = LevelMiner.Trace(t.Times, t.Prices, l, t.Tick, AwayTicks, thresholds, TouchWindow, FollowWindow, FillThrough);
            if (trace is not { Filled: true }) continue;
            list.Add((double)(follow ? LevelMiner.SimulateFollow(trace, iStop, stop, iTarget, target, Slip) : LevelMiner.Simulate(trace, iStop, stop, iTarget, target, Slip)));
        }

        return list;
    }

    private static (double Mean, double T) Stats(List<double> v) => EdgeFinder.Stats(v);

    /// <summary>Welch's t statistic of the difference of two means (a minus b).</summary>
    private static double Welch(List<double> a, List<double> b)
    {
        if (a.Count < 2 || b.Count < 2) return 0;
        double ma = a.Average(), mb = b.Average();
        double va = a.Sum(x => (x - ma) * (x - ma)) / (a.Count - 1), vb = b.Sum(x => (x - mb) * (x - mb)) / (b.Count - 1);
        double se = Math.Sqrt(va / a.Count + vb / b.Count);
        return se <= 1e-12 ? 0 : (ma - mb) / se;
    }

    private static IEnumerable<(string Feed, string Symbol, LevelTools.Ticks Ticks, List<MinedLevel> Orders)> Meaningful(int minOrders = 40)
    {
        foreach (var (feed, symbol) in LevelTools.Instruments())
        {
            var t = LevelTools.Load(feed, symbol);
            if (t == null || !t.TicksMeaningful) continue;
            var orders = LevelMiner.FindOrders(t.Trades, t.Tick);
            if (orders.Count >= minOrders) yield return (feed, symbol, t, orders);
        }
    }

    // ----------------------------------------------------------------------------------------------- 1. placebo

    /// <summary>The same fade rules on prices picked at random times near the real orders (same side, same count x3): do real levels beat random ones?</summary>
    public static string Placebo()
    {
        var sb = new StringBuilder("PLACEBO TEST: fading real big-order levels against fading ordinary prices (same rules, realistic fills, stop 20 / target 40)\n");
        sb.AppendLine("Each placebo level takes a real order's side and size but sits at the price of the market 30 minutes to 4 hours earlier or later, three per real order.");
        bool any = false;
        foreach (var (_, symbol, t, orders) in Meaningful())
        {
            any = true;
            var rng = new Random(12345);
            var placebo = new List<MinedLevel>();
            foreach (var o in orders)
                for (int k = 0; k < 3; k++)
                {
                    var when = o.Time.AddMinutes((30 + rng.Next(210)) * (rng.Next(2) == 0 ? 1 : -1));
                    int idx = Array.BinarySearch(t.Times, when.Ticks);
                    if (idx < 0) idx = ~idx;
                    if (idx <= 0 || idx >= t.Times.Length) continue;
                    placebo.Add(new MinedLevel(when, t.Prices[idx] * t.Tick, o.Volume, o.Buy));
                }

            foreach (var (name, follow) in new[] { ("fading", false), ("following", true) })
            {
                var real = Results(t, orders, follow);
                var fake = Results(t, placebo, follow);
                var (mr, tr) = Stats(real);
                var (mf, tf) = Stats(fake);
                double diff = Welch(real, fake);
                string verdict = Math.Abs(diff) < 2 ? "real levels are not different from random prices" : diff > 0 ? "real levels do BETTER than random prices" : "real levels do WORSE than random prices";
                sb.AppendLine($"  {symbol,-8} {name,-9} real {mr:+0.0;-0.0;0.0} ticks/trade over {real.Count} (t {tr:0.0}) Â· placebo {mf:+0.0;-0.0;0.0} over {fake.Count} (t {tf:0.0}) Â· difference t {diff:0.0}: {verdict}");
            }
        }

        if (!any) sb.AppendLine("  not enough recorded data");
        sb.AppendLine("\nIf real levels are no different from random prices, \"a big order was here\" carries no information for this rule, whatever the raw result looks like.");
        return sb.ToString();
    }

    // ----------------------------------------------------------------------------------------------- 2. swing-level baseline

    /// <summary>Plain support and resistance (swing highs and lows) against the big-order levels, with the same rules.</summary>
    public static string SwingBaseline()
    {
        const int W = 30;
        var sb = new StringBuilder($"SWING-LEVEL BASELINE: levels at the highs and lows price turned at (the highest / lowest close within {W} minutes either side, known {W} minutes later)\n");
        bool any = false;
        foreach (var (_, symbol, t, orders) in Meaningful())
        {
            any = true;
            var m = new Regime.Minutes(t.Times, t.Prices);
            var swings = new List<MinedLevel>();
            for (int i = W; i < m.Close.Length - W; i++)
            {
                long c = m.Close[i];
                bool high = true, low = true;
                for (int k = i - W; k <= i + W && (high || low); k++)
                {
                    if (k == i) continue;
                    if (m.Close[k] > c || (m.Close[k] == c && k < i)) high = false;
                    if (m.Close[k] < c || (m.Close[k] == c && k < i)) low = false;
                }

                if (high == low) continue;
                swings.Add(new MinedLevel(new DateTime((m.Start[i] + W) * TimeSpan.TicksPerMinute, DateTimeKind.Utc), c * t.Tick, 0, low));
            }

            var real = Results(t, orders);
            var swing = Results(t, swings);
            var (mr, tr) = Stats(real);
            var (ms, ts) = Stats(swing);
            double diff = Welch(real, swing);
            sb.AppendLine($"  {symbol,-8} fading: big-order levels {mr:+0.0;-0.0;0.0} ticks/trade over {real.Count} (t {tr:0.0}) Â· swing levels {ms:+0.0;-0.0;0.0} over {swing.Count} (t {ts:0.0}) Â· difference t {diff:0.0}: "
                          + (Math.Abs(diff) < 2 ? "big orders add nothing over ordinary swing levels" : diff > 0 ? "big-order levels do better" : "swing levels do better"));
        }

        if (!any) sb.AppendLine("  not enough recorded data");
        return sb.ToString();
    }

    // ----------------------------------------------------------------------------------------------- 3. side accuracy

    /// <summary>
    /// Downloads one day of Binance BTCUSDT futures trades (public files, with the real aggressor side), measures how often the tick
    /// rule gets it right on them, and compares the per-minute buying and selling pressure of your London Strategic Edge BTC ticks with the real one.
    /// </summary>
    public static string SideAccuracy()
    {
        var sb = new StringBuilder("SIDE ACCURACY: how reliable are buy / sell labels guessed from price changes?\n");
        try
        {
            var set = HistorySources.Datasets(AppPaths.Ticks).FirstOrDefault(s => s.FeedId == "lse" && s.Symbol == "BTC_USD");
            if (set == null) return sb.AppendLine("  needs recorded London Strategic Edge BTC_USD ticks (open a BTC_USD chart for a day or more first).").ToString();
            // the latest whole day that is over and recorded
            var day = set.Days.Select(d => d.Day).Where(d => d < DateOnly.FromDateTime(DateTime.UtcNow)).OrderByDescending(d => d).FirstOrDefault();
            if (day == default) return sb.AppendLine("  no finished day of BTC_USD ticks recorded yet.").ToString();
            var inst = new Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m, PointValue = 1m };
            var binance = PublicArchives.DownloadDayAsync(inst, day, null, CancellationToken.None).GetAwaiter().GetResult();
            if (binance == null || binance.Count == 0) return sb.AppendLine($"  Binance has not published {day:yyyy-MM-dd} yet (the file appears the next morning UTC).").ToString();
            sb.AppendLine($"  day {day:yyyy-MM-dd}: {binance.Count:N0} Binance trades with the real side");

            var r = TickRuleAudit.Evaluate(binance);
            sb.AppendLine($"  tick rule on Binance, per trade: right {100 * r.Accuracy:0.0}% (when the price changed {100 * r.AccuracyWhenPriceMoved:0.0}%, when it did not {100 * r.AccuracyWhenUnchanged:0.0}%)");
            sb.AppendLine($"  per order (fills with the same time and side): right {100 * r.OrderAccuracy:0.0}% of {r.Orders:N0}; the largest 1% ({r.BigOrders}): {100 * r.BigOrderAccuracy:0.0}%");

            // per minute: net buying pressure, real (Binance) against guessed (London Strategic Edge tick rule and Binance tick rule)
            var lse = HistoricalData.Read(AppPaths.Ticks, "lse", "BTC_USD", day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToList();
            if (lse.Count > 0)
            {
                var real = PerMinute(binance, tickRule: false);
                var guessBinance = PerMinute(binance, tickRule: true);
                var guessLse = PerMinute(lse, tickRule: false); // the London Strategic Edge files already hold tick-rule sides
                sb.AppendLine($"  per minute, net buying pressure: Binance guessed vs Binance real: correlation {Correlate(real, guessBinance):0.00} Â· London Strategic Edge vs Binance real: {Correlate(real, guessLse):0.00}");
                sb.AppendLine("    (1.00 = the same story, 0 = unrelated. The second number is also lowered by the two venues being different markets.)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  could not complete: " + ex.Message);
        }

        sb.AppendLine("\nThis measures the tick rule on BTC. Futures such as gold have many trades at an unchanged price, which makes the guess worse, not better.");
        return sb.ToString();
    }

    private static Dictionary<long, double> PerMinute(IReadOnlyList<Trade> trades, bool tickRule)
    {
        var map = new Dictionary<long, double>();
        decimal last = 0;
        var guess = TradeSide.Buy;
        foreach (var t in trades)
        {
            if (last != 0 && t.Price > last) guess = TradeSide.Buy;
            else if (last != 0 && t.Price < last) guess = TradeSide.Sell;
            last = t.Price;
            var side = tickRule ? guess : t.Side;
            if (side == TradeSide.None) continue;
            long minute = t.Time.Ticks / TimeSpan.TicksPerMinute;
            map[minute] = map.GetValueOrDefault(minute) + (side == TradeSide.Buy ? 1 : -1) * (double)t.Volume;
        }

        return map;
    }

    private static double Correlate(Dictionary<long, double> a, Dictionary<long, double> b)
    {
        var keys = a.Keys.Intersect(b.Keys).ToList();
        if (keys.Count < 10) return 0;
        double ma = keys.Average(k => a[k]), mb = keys.Average(k => b[k]);
        double cov = keys.Sum(k => (a[k] - ma) * (b[k] - mb)), va = keys.Sum(k => (a[k] - ma) * (a[k] - ma)), vb = keys.Sum(k => (b[k] - mb) * (b[k] - mb));
        return va <= 0 || vb <= 0 ? 0 : cov / Math.Sqrt(va * vb);
    }

    // ----------------------------------------------------------------------------------------------- 4. hypotheses, locked before they are tested

    private sealed record Hyp(string Key, string Instrument, bool Fade, string Side, int HourFrom, int HourTo, double MinSizePercentile, int Stop, int Target, string Note);

    private static string HypothesesFile => Path.Combine(AppPaths.Root, "hypotheses.txt");

    private static string LockFile => Path.Combine(AppPaths.Root, "hypotheses.lock.json");

    public static string EnsureHypothesesFile()
    {
        if (!File.Exists(HypothesesFile))
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(HypothesesFile,
                "# Hypotheses to test going forward. One per line, fields separated by |\n" +
                "#   instrument | fade or follow | buy, sell or both | UTC hours from-to or any | size>=0.67 (rank among the instrument's big orders) or any | stop/target in ticks | note\n" +
                "# Example (not a claim, just the format):\n" +
                "#   GC.F | fade | buy | 12-16 | size>=0.67 | 20/40 | afternoon buy levels\n" +
                "# The first time a line is evaluated it is LOCKED with the time: only orders that happen after that time are used, so the test cannot\n" +
                "# be tuned on data you have already seen. Changing a line makes it a new hypothesis with a new lock time.\n");
        }

        return HypothesesFile;
    }

    private static List<Hyp> ParseHypotheses()
    {
        var list = new List<Hyp>();
        foreach (var raw in File.ReadLines(EnsureHypothesesFile()))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split('|').Select(x => x.Trim()).ToArray();
            if (p.Length < 6) continue;
            try
            {
                int from = 0, to = 24;
                if (!p[3].Equals("any", StringComparison.OrdinalIgnoreCase))
                {
                    var h = p[3].Split('-');
                    from = int.Parse(h[0], CultureInfo.InvariantCulture);
                    to = int.Parse(h[1], CultureInfo.InvariantCulture);
                }

                double size = p[4].Equals("any", StringComparison.OrdinalIgnoreCase) ? 0 : double.Parse(p[4].Replace("size>=", ""), CultureInfo.InvariantCulture);
                var st = p[5].Split('/');
                int stop = int.Parse(st[0], CultureInfo.InvariantCulture), target = int.Parse(st[1], CultureInfo.InvariantCulture);
                string side = p[2].ToLowerInvariant();
                bool fade = !p[1].StartsWith("follow", StringComparison.OrdinalIgnoreCase);
                string key = $"{p[0].ToLowerInvariant()}|{(fade ? "fade" : "follow")}|{side}|{from}-{to}|{size:0.###}|{stop}/{target}";
                list.Add(new Hyp(key, p[0], fade, side, from, to, size, stop, target, p.Length > 6 ? p[6] : ""));
            }
            catch (Exception) { /* a line that does not follow the format is ignored */ }
        }

        return list;
    }

    /// <summary>Locks any hypothesis seen for the first time, then tests each one only on orders that happened after its lock.</summary>
    public static string Hypotheses()
    {
        var sb = new StringBuilder("HYPOTHESES (forward test: only orders after the lock time count)\n");
        var hyps = ParseHypotheses();
        if (hyps.Count == 0) return sb.AppendLine($"  none written yet. Add lines to {HypothesesFile}").AppendLine("  (Settings â†’ Data has a button to open it.)").ToString();
        var locks = File.Exists(LockFile) ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(LockFile)) ?? new() : new Dictionary<string, DateTime>();
        bool changed = false;
        foreach (var h in hyps)
            if (!locks.ContainsKey(h.Key)) { locks[h.Key] = DateTime.UtcNow; changed = true; }
        if (changed) File.WriteAllText(LockFile, JsonSerializer.Serialize(locks));

        double bar = 0.05 / hyps.Count; // several hypotheses: the bar rises so that luck does not pass one of them
        var cache = new Dictionary<string, (LevelTools.Ticks T, List<MinedLevel> Orders)?>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hyps)
        {
            if (!cache.TryGetValue(h.Instrument, out var data))
            {
                var inst = LevelTools.Instruments().FirstOrDefault(i => string.Equals(i.Symbol, h.Instrument, StringComparison.OrdinalIgnoreCase));
                var t = inst.Symbol == null ? null : LevelTools.Load(inst.Feed, inst.Symbol);
                data = t == null ? null : (t, LevelMiner.FindOrders(t.Trades, t.Tick));
                cache[h.Instrument] = data;
            }

            DateTime lockedAt = locks[h.Key];
            sb.AppendLine($"  {h.Instrument} Â· {(h.Fade ? "fade" : "follow")} Â· {h.Side} Â· {(h.HourFrom == 0 && h.HourTo == 24 ? "any hour" : $"{h.HourFrom:00}-{h.HourTo:00} UTC")} Â· size â‰¥ {h.MinSizePercentile:0.##} Â· stop {h.Stop} / target {h.Target}   {(h.Note.Length > 0 ? "(" + h.Note + ")" : "")}");
            sb.AppendLine($"      locked {lockedAt.ToLocalTime():dd/MM HH:mm}");
            if (data == null) { sb.AppendLine("      no recorded ticks for this instrument"); continue; }
            var (ticks, orders) = data.Value;
            var sizes = orders.Select(o => o.Volume).OrderBy(v => v).ToList();
            var chosen = orders.Where(o => o.Time >= lockedAt
                && (h.Side == "both" || (h.Side == "buy") == o.Buy)
                && o.Time.Hour >= h.HourFrom && o.Time.Hour < h.HourTo
                && (sizes.Count <= 1 || (double)sizes.BinarySearch(o.Volume).Let(i => i < 0 ? ~i : i) / (sizes.Count - 1) >= h.MinSizePercentile)).ToList();
            var thresholds = DefaultThresholds.Concat(new[] { h.Stop, h.Target }).Distinct().OrderBy(v => v).ToArray();
            var results = Results(ticks, chosen, !h.Fade, h.Stop, h.Target, thresholds);
            var (mean, t2) = Stats(results);
            string status = results.Count < 30
                ? $"waiting for data: {results.Count} forward trades so far (30 are needed before it says anything)"
                : EdgeFinder.TwoSidedP(t2) < bar && mean > 0 ? $"SUPPORTED so far: {mean:+0.0;-0.0;0.0} ticks/trade over {results.Count} (t {t2:0.0}, bar p < {bar:0.####})"
                : $"not supported so far: {mean:+0.0;-0.0;0.0} ticks/trade over {results.Count} (t {t2:0.0}, bar p < {bar:0.####})";
            sb.AppendLine("      " + status);
        }

        sb.AppendLine("\nOrders found after the lock need ticks recorded after it: keep the app open on the instrument (or let the catch-up download it).");
        return sb.ToString();
    }

    private static TOut Let<TIn, TOut>(this TIn value, Func<TIn, TOut> f) => f(value);

    // ----------------------------------------------------------------------------------------------- 7. event impact atlas

    /// <summary>For each event in the events file that falls inside recorded ticks: how far price moved in the next 5, 30 and 60 minutes, against the usual move.</summary>
    public static string EventAtlas()
    {
        var sb = new StringBuilder("EVENT IMPACT ATLAS: move in ticks from 1 minute before the event, against the usual move over the same time\n");
        var past = TradingHub.Indicators.Native.EventCalendar.All().Where(e => e.Utc < DateTime.UtcNow).ToList();
        if (past.Count == 0)
            return sb.AppendLine($"  no past events in your events file. Add past dates (one per line, UTC) to:\n  {TradingHub.Indicators.Native.EventCalendar.EnsureFile()}").ToString();
        int[] minutes = { 5, 30, 60 };
        bool any = false;
        foreach (var (feed, symbol) in LevelTools.Instruments())
        {
            var t = LevelTools.Load(feed, symbol);
            if (t == null || !t.TicksMeaningful) continue;
            var usual = minutes.Select(m => EventImpact.TypicalAbsMove(t.Times, t.Prices, m)).ToArray();
            foreach (var group in past.GroupBy(e => e.Name))
            {
                var rows = group.Select(e => EventImpact.Measure(t.Times, t.Prices, e.Utc, minutes)).Where(r => r != null).Select(r => r!).ToList();
                if (rows.Count == 0) continue;
                any = true;
                sb.Append($"  {symbol,-8} {group.Key} ({rows.Count} of {group.Count()} events inside the recorded ticks):");
                for (int i = 0; i < minutes.Length; i++)
                {
                    double avgAbs = rows.Average(r => Math.Abs(r[i]));
                    sb.Append($"   {minutes[i]} min: {avgAbs:0} ticks{(usual[i] > 0 ? $" ({avgAbs / usual[i]:0.0}x usual)" : "")}");
                }

                sb.AppendLine();
            }
        }

        if (!any) sb.AppendLine("  none of the events fall inside the recorded ticks (they cover only the days the app was recording).");
        sb.AppendLine("\n\"Usual\" is the median absolute move over the same time at random moments in the recorded data. A handful of events is a hint, not a statistic.");
        return sb.ToString();
    }

    // ----------------------------------------------------------------------------------------------- 8. risk and decay of the paper trader

    private sealed record ShadowRec(DateTime LevelTime, DateTime Entry, DateTime Exit, bool Long, decimal EntryPrice, decimal Ticks, string Reason, bool Live);

    public static string RiskSection()
    {
        var sb = new StringBuilder("RISK AND DECAY OF THE PAPER TRADER (optimistic fills: real results would be lower)\n");
        var dir = Path.Combine(AppPaths.Root, "Shadow");
        bool any = false;
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var rows = (JsonSerializer.Deserialize<List<ShadowRec>>(File.ReadAllText(f)) ?? new()).OrderBy(r => r.Exit).Select(r => (double)r.Ticks).ToList();
                    if (rows.Count < 30) continue;
                    any = true;
                    var risk = RiskSim.Bootstrap(rows, 100);
                    sb.AppendLine($"  {Path.GetFileNameWithoutExtension(f).ToUpperInvariant()} Â· {rows.Count} trades Â· over 100 trades drawn from these results: the worst fall from a peak was {risk.DrawdownMedian:0} ticks in the typical run and {risk.Drawdown95:0} in the worst 1 in 20; the total ended between {risk.EndP5:+0;-0;0} and {risk.EndP95:+0;-0;0} (middle {risk.EndMedian:+0;-0;0}); {100 * risk.ProbabilityOfLoss:0}% of runs ended below zero");
                    if (rows.Count >= 70)
                    {
                        var (recent, prior, t) = RiskSim.Decay(rows, 50);
                        sb.AppendLine($"      latest 50 trades {recent:+0.0;-0.0;0.0} ticks each vs {prior:+0.0;-0.0;0.0} before (t {t:0.0}){(t <= -2 ? "  <-- the recent stretch is clearly worse" : "")}");
                    }
                }
                catch (Exception) { /* a bad file is left out */ }
            }

        if (!any) sb.AppendLine("  needs 30 or more paper trades of an instrument");
        return sb.ToString().TrimEnd();
    }
}
