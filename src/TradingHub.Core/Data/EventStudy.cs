using System.Globalization;
using System.Text;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Order-flow events the study can test.</summary>
public enum StudyEvent
{
    /// <summary>Buyers or sellers took 20% or more more of the last five minutes' volume: follow them.</summary>
    Lean,

    /// <summary>A stack of three or more rows where one side took 3× the other (and the other side has none): follow it.</summary>
    StackedImbalance,

    /// <summary>Two minutes of heavy one-sided aggressive volume that did not move price: bet against the aggressor (the order was absorbed).</summary>
    Absorption,

    /// <summary>A trade in the last minute 8× the median recent trade: follow its side.</summary>
    BigTrade,

    /// <summary>Price at a 30-minute high while the last ten minutes' delta is negative (or at a low with positive delta): bet on the divergence.</summary>
    DeltaDivergence,
}

/// <summary>One line of a study: how a group of events did at one horizon, in basis points (1 bp = 0.01%).</summary>
public sealed record StudyRow(int N, double Win, double Gross, double Net, double T);

public sealed record StudyHorizon(int Minutes, StudyRow All, StudyRow Train, StudyRow Test, StudyRow Faded, StudyRow AlwaysLong);

/// <summary>A labelled group of events at the 15-minute horizon (a time of day, a volatility level, a block of days).</summary>
public sealed record StudyGroup(string Label, StudyRow Row);

public sealed record StudyResult(StudyEvent Event, int Days, int TrainDays, int Events, IReadOnlyList<StudyHorizon> Horizons, decimal CostBps,
    IReadOnlyList<StudyGroup> Sessions, IReadOnlyList<StudyGroup> Volatility, IReadOnlyList<StudyGroup> Blocks, int TestsTried)
{
    /// <summary>Two-sided p-value of a t statistic (normal approximation: fine with more than about 30 events).</summary>
    public static double PValue(double t) => Math.Min(1, 2 * (1 - Phi(Math.Abs(t))));

    private static double Phi(double x)
    {
        // Abramowitz and Stegun 7.1.26: the normal distribution function to about 1e-7
        double t = 1 / (1 + 0.2316419 * x), d = 0.3989423 * Math.Exp(-x * x / 2);
        return 1 - d * t * (0.3193815 + t * (-0.3565638 + t * (1.781478 + t * (-1.821256 + t * 1.330274))));
    }

    /// <summary>A plain-text table with the verdict-relevant numbers and the caveats, for people and for Claude.</summary>
    public string Report()
    {
        var inv = CultureInfo.InvariantCulture;
        static string S(double v) => v.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
        string Row(string label, StudyRow r) => r.N == 0 ? $"    {label,-9} no events" : $"    {label,-9} {r.N,5} events  win {r.Win * 100,3:0}%  gross {S(r.Gross),6} bp  net {S(r.Net),6} bp  t {S(r.T),5}";
        var sb = new StringBuilder();
        sb.AppendLine($"Event: {Event} · {Days} days (the first {TrainDays} are 'train', the rest 'test') · {Events} events · costs {CostBps.ToString("0.#", inv)} bp round trip");
        foreach (var h in Horizons)
        {
            sb.AppendLine($"  {h.Minutes} minutes later:");
            sb.AppendLine(Row("all", h.All));
            sb.AppendLine(Row("train", h.Train));
            sb.AppendLine(Row("test", h.Test));
            sb.AppendLine(Row("opposite", h.Faded));
            sb.AppendLine(Row("long always", h.AlwaysLong));
        }

        sb.AppendLine("  The same events at 15 minutes, split three ways (net bp after costs):");
        sb.AppendLine("    by time of day (UTC): " + string.Join(" | ", Sessions.Select(g => g.Row.N == 0 ? $"{g.Label} none" : $"{g.Label} {g.Row.N} events {S(g.Row.Net)} bp")));
        sb.AppendLine("    by volatility (the last hour's range): " + string.Join(" | ", Volatility.Select(g => g.Row.N == 0 ? $"{g.Label} none" : $"{g.Label} {g.Row.N} events {S(g.Row.Net)} bp")));
        sb.AppendLine("    in consecutive blocks of days: " + string.Join(" | ", Blocks.Select(g => g.Row.N == 0 ? $"{g.Label} none" : $"{g.Label} {g.Row.N} events {S(g.Row.Net)} bp")));
        int positive = Blocks.Count(g => g.Row.N > 0 && g.Row.Net > 0), counted = Blocks.Count(g => g.Row.N > 0);
        sb.AppendLine($"    an edge would be positive in most blocks: {positive} of {counted} are.");
        sb.AppendLine("  Is it more than luck? p-value of the 'all' rows (the chance of a result this good from noise alone) and the same corrected for the " + TestsTried + " tests tried (each event x horizon counts as one):");
        foreach (var h in Horizons)
        {
            double p = PValue(h.All.T);
            sb.AppendLine($"    {h.Minutes,2} min: p {p:0.000}, corrected {Math.Min(1, p * TestsTried):0.000}{(h.All.Net > 0 && Math.Min(1, p * TestsTried) < 0.05 ? "  <- survives the correction" : "")}");
        }

        sb.AppendLine("Reading it: 'gross' is the average move in the event's direction before costs, 'net' after them; 't' is the average over its own noise (about ±2 or more is unusual for one test, but this table has several rows and horizons, so some will look good by luck).");
        sb.AppendLine("An idea with an edge shows positive net on 'test' as well as 'train', beats 'opposite' and 'long always', and has enough events. One that is good on 'train' only is noise.");
        return sb.ToString();
    }
}

/// <summary>
/// Tests an order-flow idea against real tape: at every minute it asks whether the event is on, and if so where price was 1, 5, 15 and 30 minutes
/// later, after costs. It reuses the same minute bars, stacked-imbalance and absorption logic as the Insights panel, replays each day without seeing
/// the future (only trades before a minute are known at that minute), and splits the days in time so an idea chosen on the first part is judged on a
/// later part. It reports its own opposite trade and a "long always" baseline, because an event whose reverse works as well has no information.
/// </summary>
public static class EventStudy
{
    public static readonly int[] Horizons = { 1, 5, 15, 30 };
    private const int Warmup = 20;

    private readonly record struct Hit(int Day, DateTime Time, double Vol, int Dir, long Entry, long[] Exits);

    private static StudyRow Summarize(IEnumerable<double> grosses, double cost)
    {
        var g = grosses.ToList();
        if (g.Count == 0) return new StudyRow(0, 0, 0, 0, 0);
        var net = g.Select(v => v - cost).ToList();
        double mean = net.Average(), sd = Math.Sqrt(net.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, net.Count - 1));
        return new StudyRow(net.Count, net.Count(v => v > 0) / (double)net.Count, g.Average(), mean, sd > 0 ? mean / (sd / Math.Sqrt(net.Count)) : 0);
    }

    /// <param name="testsTried">How many events and horizons you have looked at on this data, for the multiple-testing correction (one event reads 4 horizons, so 4 is the least; if you tried all five events pass 20).</param>
    public static StudyResult Run(IReadOnlyList<IReadOnlyList<Trade>> days, Instrument inst, StudyEvent kind, decimal costBps, double trainShare = 0.6, int cooldownMinutes = 5, int testsTried = 0) =>
        RunLazy(days.Count, i => days[i], inst, kind, costBps, trainShare, cooldownMinutes, testsTried);

    /// <summary>The same, reading one day at a time (a month of Bitcoin ticks does not fit in memory at once): only the day being replayed and the one before it are held.</summary>
    public static StudyResult RunLazy(int dayCount, Func<int, IReadOnlyList<Trade>> getDay, Instrument inst, StudyEvent kind, decimal costBps, double trainShare = 0.6, int cooldownMinutes = 5, int testsTried = 0)
    {
        var hits = new List<Hit>();
        var drift = new List<(int Day, long Entry, long[] Exits)>(); // every measured minute, for the "long always" baseline
        var vols = new List<double>();                              // the same minutes' volatility, for the terciles
        IReadOnlyList<Trade>? previous = null;
        for (int di = 0; di < dayCount; di++)
        {
            var today = getDay(di);
            if (today.Count < 200) continue;
            var store = new TradeStore(inst);
            var flow = new FlowTracker();
            if (previous != null)
                foreach (var t in previous) if (t.Time >= today[0].Time.AddHours(-3)) store.Add(t); // the last hours before: warm-up, no future
            previous = today;
            var times = new long[today.Count];
            var prices = new long[today.Count];
            for (int i = 0; i < today.Count; i++) { times[i] = today[i].Time.Ticks; prices[i] = inst.ToTicks(today[i].Price); }

            long PriceAt(DateTime time)
            {
                int i = Array.BinarySearch(times, time.Ticks);
                if (i < 0) i = ~i - 1;
                return i < 0 ? prices[0] : prices[i];
            }

            var lastAt = new Dictionary<int, DateTime>();
            int ti = 0;
            var start = new DateTime(today[0].Time.Ticks - today[0].Time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            var end = today[^1].Time.AddMinutes(-Horizons[^1]);
            for (var minute = start; minute < end; minute = minute.AddMinutes(1))
            {
                while (ti < today.Count && today[ti].Time < minute) store.Add(today[ti++]);
                if (store.Count == 0) continue;
                flow.Update(store);
                if (flow.Bars.Count < Warmup) continue;
                long entry = store.GetPriceTicks(store.Count - 1);
                var exits = Horizons.Select(h => PriceAt(minute.AddMinutes(h))).ToArray();
                drift.Add((di, entry, exits));
                // volatility: the high-to-low range of the last hour's closes, in basis points
                var hour = flow.Bars.TakeLast(60).ToList();
                double vol = (double)(hour.Max(x => x.CloseTicks) - hour.Min(x => x.CloseTicks)) / entry * 10_000.0;
                vols.Add(vol);
                int dir = Detect(kind, store, flow, inst, minute);
                if (dir == 0) continue;
                if (lastAt.TryGetValue(dir, out var last) && minute - last < TimeSpan.FromMinutes(cooldownMinutes)) continue;
                lastAt[dir] = minute;
                hits.Add(new Hit(di, minute, vol, dir, entry, exits));
            }
        }

        int split = Math.Max(1, (int)Math.Round(dayCount * trainShare));
        double cost = (double)costBps;
        var horizons = new List<StudyHorizon>();
        for (int k = 0; k < Horizons.Length; k++)
        {
            int hk = k;
            double Gross(Hit h) => h.Dir * (double)(h.Exits[hk] - h.Entry) / h.Entry * 10_000.0;
            horizons.Add(new StudyHorizon(Horizons[hk],
                Summarize(hits.Select(Gross), cost),
                Summarize(hits.Where(h => h.Day < split).Select(Gross), cost),
                Summarize(hits.Where(h => h.Day >= split).Select(Gross), cost),
                Summarize(hits.Select(h => -Gross(h)), cost),
                Summarize(drift.Select(d => (double)(d.Exits[hk] - d.Entry) / d.Entry * 10_000.0), cost)));
        }

        // the same events at 15 minutes, split by time of day, by volatility level and by block of days
        int i15 = Array.IndexOf(Horizons, 15);
        double G15(Hit h) => h.Dir * (double)(h.Exits[i15] - h.Entry) / h.Entry * 10_000.0;
        var sessions = new[] { ("00-08", 0, 8), ("08-13", 8, 13), ("13-21", 13, 21), ("21-24", 21, 24) }
            .Select(s => new StudyGroup(s.Item1, Summarize(hits.Where(h => h.Time.Hour >= s.Item2 && h.Time.Hour < s.Item3).Select(G15), cost))).ToList();
        var sortedVols = vols.OrderBy(v => v).ToList();
        double lo = sortedVols.Count == 0 ? 0 : sortedVols[sortedVols.Count / 3], hi = sortedVols.Count == 0 ? 0 : sortedVols[sortedVols.Count * 2 / 3];
        var volatility = new List<StudyGroup>
        {
            new("calm", Summarize(hits.Where(h => h.Vol <= lo).Select(G15), cost)),
            new("normal", Summarize(hits.Where(h => h.Vol > lo && h.Vol <= hi).Select(G15), cost)),
            new("wild", Summarize(hits.Where(h => h.Vol > hi).Select(G15), cost)),
        };
        int blockCount = Math.Clamp(dayCount / 2, 1, 4), per = Math.Max(1, (int)Math.Ceiling(dayCount / (double)blockCount));
        var blocks = Enumerable.Range(0, blockCount).Select(bi => new StudyGroup($"days {bi * per + 1}-{Math.Min(dayCount, (bi + 1) * per)}",
            Summarize(hits.Where(h => h.Day >= bi * per && h.Day < (bi + 1) * per).Select(G15), cost))).ToList();

        return new StudyResult(kind, dayCount, split, hits.Count, horizons, costBps, sessions, volatility, blocks, testsTried > 0 ? testsTried : Horizons.Length);
    }

    /// <summary>+1 buy, -1 sell, 0 nothing, as of <paramref name="now"/> (only what had happened before it is used).</summary>
    private static int Detect(StudyEvent kind, TradeStore store, FlowTracker flow, Instrument inst, DateTime now)
    {
        var bars = flow.Bars;
        switch (kind)
        {
            case StudyEvent.Lean:
            {
                var last5 = bars.TakeLast(5).ToList();
                decimal vol = last5.Sum(b => b.Volume);
                if (vol <= 0) return 0;
                decimal imb = last5.Sum(b => b.Delta) / vol;
                return imb >= 0.2m ? 1 : imb <= -0.2m ? -1 : 0;
            }

            case StudyEvent.StackedImbalance:
            {
                var (buy, sell) = DecisionEngine.StackedImbalances(store, inst, now, TimeSpan.FromMinutes(5));
                return buy > 0 && sell == 0 ? 1 : sell > 0 && buy == 0 ? -1 : 0;
            }

            case StudyEvent.Absorption:
            {
                var (b2, s2, n2) = FlowTracker.Tape(store, now, TimeSpan.FromMinutes(2));
                if (n2 < 10) return 0;
                var volumes = bars.Take(bars.Count - 1).TakeLast(120).Select(b => b.Volume).OrderBy(v => v).ToList();
                decimal typical2 = volumes.Count == 0 ? 0 : volumes[volumes.Count / 2] * 2, total = b2 + s2;
                if (typical2 <= 0 || total < 1.5m * typical2 || total <= 0) return 0;
                int i0 = store.LowerBound(now - TimeSpan.FromMinutes(2)), i1 = store.LowerBound(now.AddTicks(1)) - 1;
                long move = store.GetPriceTicks(i1) - store.GetPriceTicks(i0);
                if (b2 / total >= 0.6m && move <= 1) return -1;  // buying that did not lift price: absorbed by sellers
                if (s2 / total >= 0.6m && move >= -1) return 1;  // selling that did not push price down: absorbed by buyers
                return 0;
            }

            case StudyEvent.BigTrade:
            {
                int n = store.Count, from = Math.Max(0, n - 2000);
                if (n - from < 200) return 0;
                var sizes = new List<decimal>(n - from);
                for (int i = from; i < n; i++) sizes.Add(store.GetVolume(i));
                sizes.Sort();
                decimal median = sizes[sizes.Count / 2];
                if (median <= 0) return 0;
                int i0 = store.LowerBound(now - TimeSpan.FromMinutes(1));
                decimal best = 0;
                int dir = 0;
                for (int i = i0; i < n; i++)
                    if (store.GetVolume(i) > best) { best = store.GetVolume(i); dir = store.GetSide(i) == TradeSide.Buy ? 1 : -1; }

                return best >= 8 * median ? dir : 0;
            }

            case StudyEvent.DeltaDivergence:
            {
                if (bars.Count < 31) return 0;
                var last30 = bars.TakeLast(30).ToList();
                long close = bars[^1].CloseTicks;
                decimal delta10 = bars.TakeLast(10).Sum(b => b.Delta);
                if (close >= last30.Max(b => b.CloseTicks) && delta10 < 0) return -1;  // a new high that buyers did not make
                if (close <= last30.Min(b => b.CloseTicks) && delta10 > 0) return 1;   // a new low that sellers did not make
                return 0;
            }

            default:
                return 0;
        }
    }
}
