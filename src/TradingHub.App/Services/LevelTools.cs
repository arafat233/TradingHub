using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Services;

/// <summary>
/// Tools that work on the recorded ticks and the remembered levels: mining levels from past ticks, the shadow lab (which
/// stop/target pair would have worked best), the learned hold probability and the activity-by-hour table.
/// </summary>
internal static class LevelTools
{
    // distances (in ticks) used by the miner and the lab; index 1 (8 ticks) decides held / broke like the indicator's default
    private static readonly int[] Thresholds = { 4, 8, 12, 16, 20, 30, 40, 60, 80 };
    private const int OutcomeIndex = 1, AwayTicks = 4;
    private static readonly TimeSpan TouchWindow = TimeSpan.FromDays(2), FollowWindow = TimeSpan.FromHours(4);

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static string LevelsFile(string symbol) => Path.Combine(AppPaths.Root, "BigTrades", Safe(symbol) + ".json");

    internal sealed class Ticks
    {
        public string Feed = "", Symbol = "";
        public decimal Tick;
        public long[] Times = Array.Empty<long>();
        public long[] Prices = Array.Empty<long>();
        public List<Trade> Trades = new();
        public int Days;
        public int Spikes, Gaps;

        /// <summary>
        /// Distances here are counted in ticks. Where a tick is a tiny part of the price (BTC at 0.0001, a forex pair at 0.00001) 8 or 20
        /// ticks is far below any real move, so held / broke and stop / target results would be noise. True if 8 ticks are at least half a basis point.
        /// </summary>
        public bool TicksMeaningful => Times.Length == 0 || (double)Tick * 8 / (double)Trades[Trades.Count / 2].Price >= 5e-5;
        public double LargestGapMinutes;
    }

    internal static IEnumerable<(string Feed, string Symbol)> Instruments() =>
        HistorySources.Datasets(AppPaths.Ticks).Where(s => s.FeedId != "sim").Select(s => (s.FeedId, s.Symbol)).Distinct();

    private static readonly object QualityGate = new();
    private static readonly Dictionary<string, string> Quality = new();

    internal static Ticks? Load(string feed, string symbol) => LoadTicks(feed, symbol);

    private static Ticks? LoadTicks(string feed, string symbol)
    {
        var set = HistorySources.Datasets(AppPaths.Ticks).FirstOrDefault(s => s.FeedId == feed && s.Symbol == symbol);
        if (set == null) return null;
        decimal tick = HistorySources.StoredTickSize(AppPaths.Ticks, feed, symbol) ?? 0;
        if (tick <= 0) return null;
        var trades = HistoricalData.Read(AppPaths.Ticks, feed, symbol, set.First.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), set.Last.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToList();
        if (trades.Count == 0) return null;
        // bad prints (a tick that jumps away and straight back) must not become levels or results
        var (clean, problems) = TickSanity.Scan(trades, tick);
        trades = clean;
        int spikes = problems.Count(p => p.Kind == "spike"), gaps = problems.Count(p => p.Kind == "gap");
        double largest = problems.Where(p => p.Kind == "gap").Select(p => double.TryParse(p.Detail.Split(' ')[0], out var m) ? m : 0).DefaultIfEmpty(0).Max();
        lock (QualityGate)
        {
            Quality[$"{feed}/{symbol}"] = $"  {feed}/{symbol}: {trades.Count:N0} ticks in {set.Days.Count} days · {spikes} bad prints removed · {gaps} silences of 10 minutes or more (longest {largest:0} min)";
            try
            {
                File.WriteAllText(Path.Combine(AppPaths.Root, "tick-quality.txt"), string.Join(Environment.NewLine, Quality.Values.OrderBy(v => v)) + Environment.NewLine);
            }
            catch (Exception) { /* a summary is a nicety */ }
        }

        return new Ticks
        {
            Feed = feed,
            Symbol = symbol,
            Tick = tick,
            Trades = trades,
            Times = trades.Select(t => t.Time.Ticks).ToArray(),
            Prices = trades.Select(t => (long)Math.Round(t.Price / tick)).ToArray(),
            Days = set.Days.Count,
            Spikes = spikes,
            Gaps = gaps,
            LargestGapMinutes = largest,
        };
    }

    // ------------------------------------------------------------------------------------------------------- mining

    /// <summary>
    /// Reads every recorded instrument's ticks, finds the big orders, follows what price did after coming back to each one, and
    /// merges the result into the saved levels (so the charts, the scoreboard and the shadow trader see days of past data).
    /// </summary>
    public static string Mine()
    {
        var sb = new StringBuilder();
        foreach (var (feed, symbol) in Instruments())
        {
            var t = LoadTicks(feed, symbol);
            if (t == null) continue;
            var orders = LevelMiner.FindOrders(t.Trades, t.Tick);
            var rows = new List<SavedLevel>();
            int touched = 0, held = 0, broke = 0;
            foreach (var o in orders)
            {
                var trace = LevelMiner.Trace(t.Times, t.Prices, o, t.Tick, AwayTicks, Thresholds, TouchWindow, FollowWindow);
                if (trace == null) rows.Add(new SavedLevel(o.Time, o.Price, o.Volume, o.Buy));
                else
                {
                    int outcome = LevelMiner.Outcome(trace, OutcomeIndex);
                    touched++;
                    if (outcome == 1) held++;
                    else if (outcome == 2) broke++;
                    rows.Add(new SavedLevel(o.Time, o.Price, o.Volume, o.Buy, true, TouchTime: trace.TouchTime, Outcome: outcome));
                }
            }

            int added = Merge(symbol, rows);
            sb.AppendLine($"{feed}/{symbol}: {t.Trades.Count:N0} ticks over {t.Days} days · {orders.Count} big orders ({added} new) · price came back to {touched}: {held} held, {broke} broke"
                          + (t.TicksMeaningful ? "" : " (the tick is tiny against the price here, so held / broke at 8 ticks means little)"));
        }

        return sb.Length == 0 ? "No recorded ticks found to mine." : sb.ToString().TrimEnd();
    }

    /// <summary>Adds the mined levels to the saved file; a level already there keeps what the indicator saw, only gaining a result it lacked.</summary>
    private static int Merge(string symbol, List<SavedLevel> mined)
    {
        var path = LevelsFile(symbol);
        var all = File.Exists(path) ? JsonSerializer.Deserialize<List<SavedLevel>>(File.ReadAllText(path)) ?? new() : new List<SavedLevel>();
        var byKey = all.ToDictionary(s => (s.Time, s.Price));
        int added = 0;
        foreach (var m in mined)
        {
            if (!byKey.TryGetValue((m.Time, m.Price), out var have))
            {
                byKey[(m.Time, m.Price)] = m;
                added++;
            }
            else if (have.Outcome == 0 && m.Outcome != 0 || !have.Touched && m.Touched)
                byKey[(m.Time, m.Price)] = have with { Touched = have.Touched || m.Touched, TouchTime = have.TouchTime != default ? have.TouchTime : m.TouchTime, Outcome = have.Outcome != 0 ? have.Outcome : m.Outcome };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(byKey.Values.OrderByDescending(s => s.Time)));
        File.Move(path + ".tmp", path, true);
        return added;
    }

    // ------------------------------------------------------------------------------------------------------- shadow lab

    private static readonly int[] StopGrid = { 8, 12, 16, 20, 30, 40, 60 }, TargetGrid = { 8, 12, 16, 20, 30, 40, 60, 80 };

    /// <summary>
    /// Fades every saved level on its first return with each stop/target pair and prints the total result in ticks, from the ticks on disk.
    /// The best of 56 pairs picked on the same data is always optimistic: look for a broad good area, not for the single best cell.
    /// </summary>
    public static string ShadowLab()
    {
        var sb = new StringBuilder("SHADOW LAB: net ticks of fading the first return to each level (rows = stop, columns = target)\n");
        bool any = false;
        foreach (var (feed, symbol) in Instruments())
        {
            var file = LevelsFile(symbol);
            if (!File.Exists(file)) continue;
            var t = LoadTicks(feed, symbol);
            if (t == null) continue;
            if (!t.TicksMeaningful)
            {
                sb.AppendLine($"\n{symbol}: skipped, a tick ({t.Tick}) is too small a part of the price for stops and targets in ticks to mean anything.");
                continue;
            }

            var levels = (JsonSerializer.Deserialize<List<SavedLevel>>(File.ReadAllText(file)) ?? new()).Where(l => !l.Deleted).ToList();
            // the thresholds the grid needs, as indexes into the traced ones
            var all = StopGrid.Concat(TargetGrid).Concat(Thresholds).Distinct().OrderBy(v => v).ToArray();
            var traces = new List<LevelTrace>();
            foreach (var l in levels)
                if (LevelMiner.Trace(t.Times, t.Prices, new MinedLevel(l.Time, l.Price, l.Volume, l.Buy), t.Tick, AwayTicks, all, TouchWindow, FollowWindow) is { } tr) traces.Add(tr);
            if (traces.Count < 10)
            {
                sb.AppendLine($"\n{symbol}: only {traces.Count} levels have a first return in the recorded ticks (of {levels.Count} saved): too few for a table.");
                continue;
            }

            any = true;
            var cells = new decimal[StopGrid.Length, TargetGrid.Length];
            for (int s = 0; s < StopGrid.Length; s++)
                for (int g = 0; g < TargetGrid.Length; g++)
                {
                    int si = Array.IndexOf(all, StopGrid[s]), ti = Array.IndexOf(all, TargetGrid[g]);
                    cells[s, g] = traces.Sum(tr => LevelMiner.Simulate(tr, si, StopGrid[s], ti, TargetGrid[g]));
                }

            decimal best = decimal.MinValue;
            int bs = 0, bg = 0;
            for (int s = 0; s < StopGrid.Length; s++)
                for (int g = 0; g < TargetGrid.Length; g++)
                    if (cells[s, g] > best) { best = cells[s, g]; bs = s; bg = g; }

            sb.AppendLine($"\n{symbol} · {traces.Count} levels with a first return (of {levels.Count} saved) · {t.Days} days of ticks");
            sb.Append("   stop \\ target");
            foreach (var tp in TargetGrid) sb.Append($"{tp,8}");
            sb.AppendLine();
            for (int s = 0; s < StopGrid.Length; s++)
            {
                sb.Append($"   {StopGrid[s],13}");
                for (int g = 0; g < TargetGrid.Length; g++) sb.Append($"{cells[s, g],7:0}{(s == bs && g == bg ? "*" : " ")}");
                sb.AppendLine();
            }

            sb.AppendLine($"   best: stop {StopGrid[bs]} / target {TargetGrid[bg]} = {best:+0;-0;0} ticks ({best / traces.Count:+0.0;-0.0;0.0} per trade); the default 20 / 40 = {cells[Array.IndexOf(StopGrid, 20), Array.IndexOf(TargetGrid, 40)]:+0;-0;0} ticks");
        }

        if (!any && sb.ToString().Count(c => c == '\n') <= 1) sb.AppendLine("\nNo saved levels with recorded ticks yet. Use \"Mine levels\" first.");
        sb.AppendLine("\nAssumes a fill at the level price and that the stop wins when one tick path reaches both; unresolved trades are marked after 4 hours. Choosing the best of 56 pairs on the same data flatters it.");
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------------- learned hold probability

    private static decimal TickOf(IReadOnlyList<SavedLevel> rows)
    {
        // levels sit on the tick grid: the smallest gap between two different prices is a multiple of it, good enough for distances
        var prices = rows.Select(r => r.Price).Distinct().OrderBy(p => p).ToList();
        decimal min = decimal.MaxValue;
        for (int i = 1; i < prices.Count; i++) min = Math.Min(min, prices[i] - prices[i - 1]);
        return min == decimal.MaxValue || min <= 0 ? 1m : min;
    }

    public static string HoldSection()
    {
        var sb = new StringBuilder("LEARNED HOLD PROBABILITY (fitted on your own levels whose result is known)\n");
        bool any = false;
        foreach (var (name, rows) in TodayReport.Load())
        {
            if (name.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) continue;
            var samples = rows.Select(r => new LevelSample(r.Time, r.Price, r.Volume, r.Buy)).ToList();
            var labelled = rows.Where(r => r.Outcome != 0).Select(r => (new LevelSample(r.Time, r.Price, r.Volume, r.Buy), r.Outcome == 1)).ToList();
            var model = HoldModel.Train(labelled, samples, TickOf(rows));
            if (model == null)
            {
                sb.AppendLine($"  {name}: not enough results yet ({labelled.Count} resolved levels; 30 are needed, with both held and broke)");
                continue;
            }

            any = true;
            string check = model.TestedOn > 0
                ? $"on the {model.TestedOn} most recent results it had not seen it was right {100 * model.Accuracy:0}% of the time (always guessing the more common result: {100 * Math.Max(model.TestHoldRate, 1 - model.TestHoldRate):0}%)"
                : "too few results to hold any out for a check";
            sb.AppendLine($"  {name}: trained on {model.TrainedOn} results; {check}");
            if (!model.HasSkill)
            {
                sb.AppendLine("      verdict: no better than guessing on results it had not seen, so no probabilities are shown (and the chart keeps the plain quality score).");
                continue;
            }

            foreach (var r in rows.Where(r => !r.Touched && !r.Hidden).Select(r => (Row: r, P: model.Predict(new LevelSample(r.Time, r.Price, r.Volume, r.Buy)))).OrderByDescending(x => x.P).Take(5))
                sb.AppendLine($"      {(r.Row.Buy ? "buy " : "sell")} {r.Row.Volume,8:0.##} @ {r.Row.Price}  hold probability {100 * r.P:0}%  ({r.Row.Time.ToLocalTime():dd/MM HH:mm})");
        }

        if (!any && sb.ToString().Count(c => c == '\n') <= 1) sb.AppendLine("  no levels yet");
        sb.AppendLine("  A model on a few dozen results is a rough guide only; it does not know about news or regime changes.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>The data-quality lines saved the last time ticks were read (mining, the lab, the edge finder); reading them here costs nothing.</summary>
    public static string QualitySection()
    {
        var file = Path.Combine(AppPaths.Root, "tick-quality.txt");
        return "TICK QUALITY (as of the last time ticks were mined or studied; spikes are dropped before levels are built)\n"
               + (File.Exists(file) ? File.ReadAllText(file).TrimEnd() : "  not checked yet (use Mine levels, Shadow lab or Edge finder)");
    }

    // ------------------------------------------------------------------------------------------------------- activity by hour

    public static string ActivitySection()
    {
        var sb = new StringBuilder("WHEN BIG ORDERS APPEAR (all saved levels, local time)\n");
        bool any = false;
        foreach (var (name, rows) in TodayReport.Load())
        {
            if (name.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase) || rows.Count < 20) continue;
            any = true;
            var byHour = rows.GroupBy(r => r.Time.ToLocalTime().Hour).ToDictionary(g => g.Key, g => g.Count());
            int max = byHour.Values.Max();
            sb.AppendLine($"  {name}  ({rows.Count} orders)");
            for (int h = 0; h < 24; h++)
            {
                byHour.TryGetValue(h, out int n);
                sb.AppendLine($"    {h:00}:00 {new string('#', (int)Math.Round(30.0 * n / max)),-30} {n}");
            }
        }

        if (!any) sb.AppendLine("  not enough saved levels yet (20 or more per instrument)");
        return sb.ToString().TrimEnd();
    }
}
