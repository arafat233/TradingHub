using System.Globalization;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.LondonStrategicEdge;
using TradingHub.Market;
using Xunit.Abstractions;

namespace TradingHub.Tests;

/// <summary>
/// Tunes the Insights decision thresholds on REAL tape instead of by guesswork. It downloads ticks (London Strategic Edge for ES, NQ and GC with
/// LSE_API_KEY; Bybit's public daily files for BTCUSDT) into a temp cache, replays them minute by minute through <see cref="DecisionEngine.Measure"/>,
/// and for every threshold combination asks what happened 15 minutes after each setup, after costs. The days are split in time: the thresholds are
/// chosen on the first part and judged on the later part they have never seen. It only runs when INSIGHTS_TUNE is set:
/// <c>$env:INSIGHTS_TUNE='1'; $env:LSE_API_KEY='…'; dotnet test --filter InsightsTuning --logger "console;verbosity=detailed"</c>.
/// </summary>
public class InsightsTuning(ITestOutputHelper output)
{
    private static readonly string Cache = Path.Combine(Path.GetTempPath(), "tradinghub-tuning");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed record Obs(int DayIndex, DateTime Time, Measures M, long Entry, long Exit15, long Exit5, long Exit30);

    private sealed record Series(string Name, Instrument Inst, decimal CostBps, List<Obs> Obs);

    private void Log(string s)
    {
        output.WriteLine(s);
        File.AppendAllText(Path.Combine(Cache, "tuning.log"), s + Environment.NewLine);
    }

    private static List<Trade> LoadDay(Instrument inst, DateOnly day) => HistorySources.ReadDay(Cache, inst.FeedId, inst.Symbol, day);

    [Fact]
    public async Task InsightsTuning_replay_real_tape_and_sweep_the_thresholds()
    {
        if (Environment.GetEnvironmentVariable("INSIGHTS_TUNE") is not { Length: > 0 }) return;
        Directory.CreateDirectory(Cache);
        int dayCount = int.TryParse(Environment.GetEnvironmentVariable("INSIGHTS_TUNE_DAYS"), out var n) ? n : 10;
        var days = new List<DateOnly>();
        for (var d = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1); days.Count < dayCount; d = d.AddDays(-1))
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days.Add(d);
        days.Reverse();
        Log($"=== {DateTime.Now:u}: {dayCount} weekdays {days[0]} .. {days[^1]}");

        var specs = new List<(string Name, Instrument Inst, decimal Cost)>();
        string? key = Environment.GetEnvironmentVariable("LSE_API_KEY");
        LseFeed? lse = string.IsNullOrWhiteSpace(key) ? null : new LseFeed(new LseSettings { ApiKey = key });
        if (lse != null)
        {
            var list = await lse.GetInstrumentsAsync(CancellationToken.None);
            foreach (var sym in new[] { "ES.F", "NQ.F", "GC.F" }) specs.Add((sym, list.First(i => i.Symbol == sym), 2m));
        }

        using var bybit = new BybitFeed();
        specs.Add(("BTCUSDT", (await bybit.GetInstrumentsAsync(CancellationToken.None)).First(i => i.Symbol == "BTCUSDT"), 8m));

        // ---- 1. the tape (cached: a day is downloaded once)
        foreach (var (name, inst, _) in specs)
            foreach (var day in days)
            {
                if (HistorySources.HasDay(Cache, inst.FeedId, inst.Symbol, day)) continue;
                List<Trade>? trades = null;
                try
                {
                    if (inst.FeedId == LseFeed.FeedId)
                    {
                        trades = new List<Trade>();
                        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                        await lse!.LoadTradesAsync(inst, from, from.AddDays(1), null, b => { trades.AddRange(b); return true; }, null, CancellationToken.None);
                    }
                    else trades = await PublicArchives.DownloadDayAsync(inst, day, null, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Log($"  {name} {day}: {ex.GetType().Name}: {ex.Message}");
                }

                if (trades is { Count: > 0 }) HistoricalData.WriteDay(Cache, inst, day, trades, replace: true);
                Log($"  {name} {day}: {(trades?.Count ?? 0):N0} trades");
            }

        // ---- 2. replay: at every minute, what the six checks measured and where price was 5, 15 and 30 minutes later
        var all = new List<Series>();
        foreach (var (name, inst, cost) in specs)
        {
            var obs = new List<Obs>();
            List<Trade>? prev = null;
            for (int di = 0; di < days.Count; di++)
            {
                var today = LoadDay(inst, days[di]);
                if (today.Count < 500) continue;
                var store = new TradeStore(inst);
                var flow = new FlowTracker();
                if (prev != null) foreach (var t in prev.Where(t => t.Time >= today[0].Time.AddHours(-3))) store.Add(t); // warm-up: the last hours before
                prev = today;
                var times = today.Select(t => t.Time.Ticks).ToArray();
                var prices = today.Select(t => inst.ToTicks(t.Price)).ToArray();
                long PriceAt(DateTime time)
                {
                    int i = Array.BinarySearch(times, time.Ticks);
                    if (i < 0) i = ~i - 1;
                    return i < 0 ? prices[0] : prices[i];
                }

                int ti = 0;
                var start = new DateTime(today[0].Time.Ticks - today[0].Time.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
                var end = today[^1].Time.AddMinutes(-30);
                for (var minute = start; minute < end; minute = minute.AddMinutes(1))
                {
                    while (ti < today.Count && today[ti].Time < minute) store.Add(today[ti++]);
                    if (store.Count == 0) continue;
                    flow.Update(store);
                    var m = DecisionEngine.Measure(new InsightInput(inst, flow, store, DepthSnapshot.Empty, minute, cost));
                    if (m == null) continue;
                    long entry = store.GetPriceTicks(store.Count - 1);
                    obs.Add(new Obs(di, minute, m, entry, PriceAt(minute.AddMinutes(15)), PriceAt(minute.AddMinutes(5)), PriceAt(minute.AddMinutes(30))));
                }
            }

            all.Add(new Series(name, inst, cost, obs));
            Log($"replayed {name}: {obs.Count:N0} minutes measured");
        }

        // ---- 3. the sweep
        int split = (int)Math.Round(days.Count * 0.6);
        Log($"train = the first {split} days, test = the last {days.Count - split} (never used to choose)");

        (int N, double Hit, double Gross, double Net, double T) Eval(DecisionThresholds t, Func<Obs, int>? side, Func<int, bool> dayOk, Func<Series, Obs, long>? exit = null)
        {
            var nets = new List<double>();
            var grosses = new List<double>();
            foreach (var s in all)
            {
                var lastAt = new Dictionary<int, DateTime>();
                foreach (var o in s.Obs)
                {
                    if (!dayOk(o.DayIndex)) continue;
                    int dir = side != null ? side(o) : (o.M.IsSetup(t) ? o.M.LeanOf(t) : 0);
                    if (dir == 0) continue;
                    if (lastAt.TryGetValue(dir, out var last) && o.Time - last < TimeSpan.FromMinutes(5)) continue; // the edge meter's cooldown
                    lastAt[dir] = o.Time;
                    long ex = exit != null ? exit(s, o) : o.Exit15;
                    double gross = dir * (double)(ex - o.Entry) / o.Entry * 10_000.0; // both are tick counts: the ratio is already the move
                    grosses.Add(gross);
                    nets.Add(gross - (double)s.CostBps);
                }
            }

            if (nets.Count == 0) return (0, 0, 0, 0, 0);
            double mean = nets.Average(), sd = Math.Sqrt(nets.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, nets.Count - 1));
            return (nets.Count, nets.Count(v => v > 0) / (double)nets.Count, grosses.Average(), mean, sd > 0 ? mean / (sd / Math.Sqrt(nets.Count)) : 0);
        }

        static string S(double v) => v.ToString("+0.0;-0.0;0.0", Inv).PadLeft(6);
        string Fmt((int N, double Hit, double Gross, double Net, double T) r) => r.N == 0 ? "no setups" : $"{r.N,5} setups, {r.Hit:P0} win, gross {S(r.Gross)} bp, net {S(r.Net)} bp (t {S(r.T)})";

        // what the market does after ANY lean (no checks at all): the baseline the checks must beat
        var lean = new DecisionThresholds();
        Func<Obs, int> leanOnly = o => o.M.LeanOf(lean);
        Log("");
        Log("BASELINE (every lean of 5%+, no checks, 15 min later):");
        Log($"  train: {Fmt(Eval(lean, leanOnly, d => d < split))}");
        Log($"  test:  {Fmt(Eval(lean, leanOnly, d => d >= split))}");
        Log("CONTRARIAN baseline (the same leans, traded the other way):");
        Func<Obs, int> contra = o => -o.M.LeanOf(lean);
        Log($"  train: {Fmt(Eval(lean, contra, d => d < split))}");
        Log($"  test:  {Fmt(Eval(lean, contra, d => d >= split))}");

        var def = DecisionThresholds.Default;
        Log("");
        Log("DEFAULT thresholds (what the panel uses now):");
        Log($"  train: {Fmt(Eval(def, null, d => d < split))}");
        Log($"  test:  {Fmt(Eval(def, null, d => d >= split))}");
        foreach (var s in all)
        {
            int setups = s.Obs.Count(o => o.M.IsSetup(def));
            Log($"  {s.Name}: {setups} measured-minutes pass all six ({s.Obs.Count} minutes measured)");
            // how often each check passes on its own (among the minutes that have a lean): the one that never passes is the one blocking everything
            var leaning = s.Obs.Where(o => o.M.LeanOf(def) != 0).ToList();
            if (leaning.Count == 0) continue;
            var names = leaning[0].M.Checks(def).Select(c => c.Name).ToList();
            Log("      pass rate of each check among minutes with a lean: " + string.Join(" · ", names.Select((nm, i) => $"{nm} {leaning.Count(o => o.M.Checks(def)[i].Pass) / (double)leaning.Count:P0}")));
        }

        var grid = new List<DecisionThresholds>();
        foreach (var st in new[] { 15m, 20m, 30m, 40m, 50m })
            foreach (var act in new[] { 0.3m, 0.5m, 0.7m })
                foreach (var dr in new[] { 0.5m, 0.55m, 0.65m })
                    foreach (var cm in new[] { 1m, 2m, 3m })
                        foreach (var rr in new[] { 1.0m, 1.2m, 1.5m })
                            foreach (var tr in new[] { 0.15m, 0.25m, 0.4m })
                                grid.Add(new DecisionThresholds(0.05m, st, act, dr, cm, rr, tr));
        var scored = grid.Select(t => (T: t, Train: Eval(t, null, d => d < split))).Where(x => x.Train.N >= 40).ToList();
        Log("");
        Log($"SWEEP: {grid.Count} combinations, {scored.Count} produced at least 40 training setups.");
        var top = scored.OrderByDescending(x => x.Train.Gross).Take(8).ToList();
        Log("Top 8 by GROSS edge on the training days (before costs: does it carry information at all?), then the same on the unseen test days:");
        foreach (var (t, tr) in top)
        {
            var te = Eval(t, null, d => d >= split);
            Log($"  strength {t.Strength:0} activity {t.Activity:0.#} driven {t.Driven:0.##} costs× {t.CostMultiple:0} R:R {t.RewardRisk:0.0} trend {t.Trend:0.00}");
            Log($"      train {Fmt(tr)}");
            Log($"      test  {Fmt(te)}");
        }

        // how the number of setups moves with each threshold on its own, and the average gross edge: which checks carry information
        Log("");
        Log("ONE AT A TIME from the defaults (gross bp on train+test pooled, setups):");
        void One(string label, Func<decimal, DecisionThresholds> make, params decimal[] values)
        {
            foreach (var v in values)
            {
                var r = Eval(make(v), null, _ => true);
                Log($"  {label} {v,5}: {r.N,5} setups, win {r.Hit:P0}, gross {S(r.Gross)} bp, net {S(r.Net)} bp");
            }
        }

        One("strength ", v => def with { Strength = v }, 0, 15, 30, 45, 60, 80);
        One("activity ", v => def with { Activity = v }, 0, 0.3m, 0.5m, 0.7m, 0.9m);
        One("driven   ", v => def with { Driven = v }, 0, 0.5m, 0.55m, 0.65m, 0.8m);
        One("costs x  ", v => def with { CostMultiple = v }, 0, 1, 2, 3, 5);
        One("R:R      ", v => def with { RewardRisk = v }, 0, 1.0m, 1.2m, 1.5m, 2m);
        One("trend    ", v => def with { Trend = v }, 0, 0.15m, 0.25m, 0.4m, 0.6m);
        Log("");
        Log("Horizons for the default setups (5, 15, 30 minutes), gross bp pooled:");
        foreach (var (label, ex) in new (string, Func<Series, Obs, long>)[] { ("5 min", (s, o) => o.Exit5), ("15 min", (s, o) => o.Exit15), ("30 min", (s, o) => o.Exit30) })
            Log($"  {label}: {Fmt(Eval(def, null, _ => true, ex))}");
        Log($"=== done {DateTime.Now:u}");
    }
}
