using System.Text;
using System.Text.Json;

namespace TradingHub.App.Services;

/// <summary>A big-trade level as the Big Trades indicator saves it (BigTrades\&lt;instrument&gt;.json).</summary>
internal sealed record SavedLevel(DateTime Time, decimal Price, decimal Volume, bool Buy, bool Touched = false, bool Hidden = false, bool Deleted = false, bool Muted = false, DateTime TouchTime = default, int Outcome = 0);

/// <summary>Alerts raised by the indicators during this run (for the Today report and the phone alerts).</summary>
public static class AlertLog
{
    private static readonly List<(DateTime Time, string Text)> Items = new();

    private static string DayFile(DateTime day) => Path.Combine(AppPaths.Root, "Alerts", $"{day:yyyy-MM-dd}.log");

    public static void Add(DateTime time, string text)
    {
        lock (Items)
        {
            Items.Add((time, text));
            if (Items.Count > 200) Items.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DayFile(time))!);
                File.AppendAllText(DayFile(time), $"{time:yyyy-MM-ddTHH:mm:ss}|{text.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}");
            }
            catch (Exception) { /* the log is a nicety: the alert itself already showed */ }
        }
    }

    /// <summary>The alerts of a local day as saved (they survive restarts), oldest first.</summary>
    public static List<(DateTime Time, string Text)> OnDay(DateTime day)
    {
        var list = new List<(DateTime, string)>();
        try
        {
            if (File.Exists(DayFile(day)))
                foreach (var line in File.ReadLines(DayFile(day)))
                {
                    int bar = line.IndexOf('|');
                    if (bar > 0 && DateTime.TryParse(line[..bar], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)) list.Add((t, line[(bar + 1)..]));
                }
        }
        catch (Exception) { /* a bad file is the same as no alerts */ }

        return list;
    }

    public static List<(DateTime Time, string Text)> Since(DateTime time)
    {
        lock (Items) return Items.Where(i => i.Time >= time).ToList();
    }
}

/// <summary>
/// Text report for the Today window: this run's alerts, today's big-trade levels per instrument with the biggest ones
/// price has not come back to, how the remembered levels have behaved so far, and yesterday's recap.
/// </summary>
internal static class TodayReport
{
    public static List<(string Instrument, List<SavedLevel> Levels)> Load()
    {
        var result = new List<(string, List<SavedLevel>)>();
        var dir = Path.Combine(AppPaths.Root, "BigTrades");
        if (!Directory.Exists(dir)) return result;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var rows = (JsonSerializer.Deserialize<List<SavedLevel>>(File.ReadAllText(f)) ?? new()).Where(s => !s.Deleted).ToList();
                if (rows.Count > 0) result.Add((Path.GetFileNameWithoutExtension(f), rows));
            }
            catch (Exception) { /* an unreadable file is left out of the report */ }
        }

        return result;
    }

    /// <summary>
    /// How the levels have behaved: after price came back, HELD (it moved away on the order's side) or BROKE (it went through),
    /// for all of them and for the biggest quarter by size.
    /// </summary>
    public static string Scoreboard(IReadOnlyList<SavedLevel> rows)
    {
        if (rows.Count == 0) return "";
        decimal cut = rows.Select(r => r.Volume).OrderBy(v => v).ElementAt(Math.Min(rows.Count - 1, rows.Count * 3 / 4));
        string Line(string name, IEnumerable<SavedLevel> set)
        {
            var l = set.ToList();
            int held = l.Count(r => r.Outcome == 1), broke = l.Count(r => r.Outcome == 2), undecided = l.Count(r => r.Touched && r.Outcome == 0);
            int never = l.Count(r => !r.Touched);
            string rate = held + broke > 0 ? $"{100.0 * held / (held + broke):0}% held" : "no result yet";
            return $"  - {name}: {l.Count} levels · {held} held · {broke} broke · {undecided} touched, undecided · {never} not touched yet · {rate}";
        }

        return "- Level scoreboard (held = price moved away on the order's side after coming back, broke = it went through):\n"
               + Line("all sizes", rows) + "\n" + Line($"biggest quarter (size ≥ {cut:0.##})", rows.Where(r => r.Volume >= cut));
    }

    private sealed record MarkRec(string Kind, DateTime Time, decimal A, decimal B, bool Flag);

    /// <summary>One line about the day's absorption and stacked-imbalance marks of an instrument ("" if none were saved).</summary>
    public static string MarksLine(string instrument, DateTime localDay)
    {
        try
        {
            var name = string.Concat(instrument.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var file = Path.Combine(AppPaths.Root, "Marks", name + ".json");
            if (!File.Exists(file)) return "";
            var day = (JsonSerializer.Deserialize<List<MarkRec>>(File.ReadAllText(file)) ?? new()).Where(m => m.Time.ToLocalTime().Date == localDay.Date).ToList();
            if (day.Count == 0) return "";
            var abs = day.Where(m => m.Kind == "absorption").ToList();
            var stk = day.Where(m => m.Kind == "stacked").ToList();
            var div = day.Where(m => m.Kind == "divergence").ToList();
            var swp = day.Where(m => m.Kind == "sweep").ToList();
            var parts = new List<string>();
            if (abs.Count > 0) parts.Add($"{abs.Count} absorption candles ({abs.Count(m => m.Flag)} against buyers, {abs.Count(m => !m.Flag)} against sellers)");
            if (stk.Count > 0) parts.Add($"{stk.Count} stacked imbalances ({stk.Count(m => m.Flag)} buy stacks, {stk.Count(m => !m.Flag)} sell stacks)");
            if (div.Count > 0) parts.Add($"{div.Count} delta divergences ({div.Count(m => m.Flag)} bearish, {div.Count(m => !m.Flag)} bullish)");
            if (swp.Count > 0) parts.Add($"{swp.Count} liquidity sweeps ({swp.Count(m => m.Flag)} of highs, {swp.Count(m => !m.Flag)} of lows)");
            return "- " + string.Join(" · ", parts);
        }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// Which of the last 7 days have real ticks on disk, per instrument. A day without them is drawn from 1-minute candles
    /// (4 made-up trades each): fine for price, not for footprint detail.
    /// </summary>
    public static string Coverage()
    {
        var sb = new StringBuilder("DATA COVERAGE, LAST 7 DAYS (real ticks on disk)\n");
        var root = AppPaths.Ticks;
        bool any = false;
        if (Directory.Exists(root))
        {
            foreach (var feed in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(feed) == "sim") continue;
                foreach (var sym in Directory.GetDirectories(feed).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var parts = new List<string>();
                    for (int back = 0; back < 7; back++)
                    {
                        var day = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-back));
                        var f = Path.Combine(sym, $"{day:yyyy-MM-dd}.vtk");
                        parts.Add(File.Exists(f) ? $"{day:dd/MM} {new FileInfo(f).Length / 1024:N0} KB" : $"{day:dd/MM} candles only");
                    }

                    sb.AppendLine($"  {Path.GetFileName(feed)}/{Path.GetFileName(sym)}: " + string.Join(" · ", parts));
                    any = true;
                }
            }
        }

        if (!any) sb.AppendLine("  no recorded ticks yet");
        sb.AppendLine("  (a small file means the app was only open for part of that day; days marked candles only are built from 1-minute candles)");
        return sb.ToString().TrimEnd();
    }

    private sealed record ShadowRec(DateTime LevelTime, DateTime Entry, DateTime Exit, bool Long, decimal EntryPrice, decimal Ticks, string Reason, bool Live);

    /// <summary>The paper trades the Big Trades "shadow trader" made: what fading every first return to a level would have done.</summary>
    public static string ShadowSection()
    {
        var sb = new StringBuilder("SHADOW TRADER (paper: fade the first return to each level, fixed stop and target, fill assumed at the level)\n");
        var dir = Path.Combine(AppPaths.Root, "Shadow");
        bool any = false;
        if (Directory.Exists(dir))
        {
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var rows = JsonSerializer.Deserialize<List<ShadowRec>>(File.ReadAllText(f)) ?? new();
                    if (rows.Count == 0) continue;
                    any = true;
                    sb.AppendLine("  " + Path.GetFileNameWithoutExtension(f).ToUpperInvariant());
                    foreach (var (name, set) in new[] { ("live", rows.Where(r => r.Live).ToList()), ("found in loaded history", rows.Where(r => !r.Live).ToList()) })
                    {
                        if (set.Count == 0) continue;
                        int wins = set.Count(r => r.Ticks > 0);
                        sb.AppendLine($"    {name}: {set.Count} trades · {100.0 * wins / set.Count:0}% winners · net {set.Sum(r => r.Ticks):+0.#;-0.#;0} ticks · average {set.Average(r => r.Ticks):+0.#;-0.#;0} per trade");
                    }

                    foreach (var r in rows.OrderByDescending(r => r.Exit).Take(5))
                        sb.AppendLine($"      {r.Exit.ToLocalTime():dd/MM HH:mm}  {(r.Long ? "long " : "short")} @ {r.EntryPrice}  {r.Ticks:+0.#;-0.#;0} ticks ({r.Reason})");
                }
                catch (Exception) { /* a bad file is left out */ }
            }
        }

        if (!any) sb.AppendLine("  no paper trades yet (they appear when price comes back to a level while a Big Trades indicator is on a chart)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>When the real (or simulated) account trades make money: results by hour of the day and by weekday, from the journal.</summary>
    public static string EdgeSection(AppServices svc)
    {
        var sb = new StringBuilder("EDGE ANALYTICS (your account trades, local time)\n");
        var trades = new List<TradingHub.Trading.AccountTrade>();
        foreach (var a in svc.AccountCenter.Accounts())
            trades.AddRange(TradingHub.Trading.RoundTrips.Analyze(svc.Journal.FillsOf(a.Id)).Trades);
        if (trades.Count < 5) return sb.Append($"  not enough trades yet ({trades.Count}; five or more are needed)").ToString();
        string Line(string label, List<TradingHub.Trading.AccountTrade> t) =>
            $"    {label,-10} {t.Count,4} trades · {100.0 * t.Count(x => x.NetPnl > 0) / t.Count:0}% winners · net {t.Sum(x => x.NetPnl):+0.00;-0.00;0.00}";
        var byHour = trades.GroupBy(t => svc.AccountCenter.ToLocal(t.OpenTime).Hour).OrderBy(g => g.Key).ToList();
        sb.AppendLine("  by hour the trade was opened:");
        foreach (var g in byHour) sb.AppendLine(Line($"{g.Key:00}:00", g.ToList()));
        var best = byHour.Where(g => g.Count() >= 3).OrderByDescending(g => g.Sum(t => t.NetPnl)).FirstOrDefault();
        var worst = byHour.Where(g => g.Count() >= 3).OrderBy(g => g.Sum(t => t.NetPnl)).FirstOrDefault();
        if (best != null && worst != null && best.Key != worst.Key)
            sb.AppendLine($"  best hour {best.Key:00}:00, worst hour {worst.Key:00}:00 (hours with at least 3 trades)");
        sb.AppendLine("  by weekday:");
        foreach (var g in trades.GroupBy(t => svc.AccountCenter.ToLocal(t.OpenTime).DayOfWeek).OrderBy(g => ((int)g.Key + 6) % 7))
            sb.AppendLine(Line(g.Key.ToString()[..3], g.ToList()));
        sb.AppendLine("  Small samples mislead: treat this as a place to look, not as a rule.");
        return sb.ToString().TrimEnd();
    }

    public static string Build(AppServices? svc = null)
    {
        var sb = new StringBuilder();
        var today = DateTime.Now.Date;
        sb.AppendLine($"TODAY · {DateTime.Now:dddd dd/MM/yyyy HH:mm}").AppendLine();

        sb.AppendLine("ALERTS SINCE THIS RUN STARTED");
        var alerts = AlertLog.Since(today);
        if (alerts.Count == 0) sb.AppendLine("  none yet");
        foreach (var a in alerts.TakeLast(15)) sb.AppendLine($"  {a.Time:HH:mm:ss}  {a.Text}");
        sb.AppendLine();

        foreach (var (name, rows) in Load())
        {
            if (name.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) continue;
            var mine = rows.Where(r => r.Time.ToLocalTime().Date == today).ToList();
            sb.AppendLine(name.ToUpperInvariant());
            sb.AppendLine($"  today: {mine.Count} big orders ({mine.Count(r => r.Buy)} buys, {mine.Count(r => !r.Buy)} sells), price came back to {mine.Count(r => r.Touched)}");
            foreach (var r in mine.Where(r => !r.Touched).OrderByDescending(r => r.Volume).Take(5))
                sb.AppendLine($"    not touched: {(r.Buy ? "buy " : "sell")} {r.Volume,8:0.##} @ {r.Price}  ({r.Time.ToLocalTime():HH:mm})");
            var marks = MarksLine(name, today);
            if (marks.Length > 0) sb.AppendLine("  " + marks.TrimStart('-', ' '));
            sb.AppendLine(Scoreboard(rows).Replace("- Level", "  Level"));
            sb.AppendLine();
        }

        sb.AppendLine(ShadowSection()).AppendLine();
        sb.AppendLine(ResearchExtra.RiskSection()).AppendLine();
        if (svc != null) sb.AppendLine(ProcessCoach.Section(svc)).AppendLine();
        sb.AppendLine(LevelTools.HoldSection()).AppendLine();
        sb.AppendLine(LevelTools.ActivitySection()).AppendLine();
        if (svc != null) sb.AppendLine(EdgeSection(svc)).AppendLine();
        sb.AppendLine(Ledger.Section()).AppendLine();
        sb.AppendLine(LevelTools.QualitySection()).AppendLine();
        sb.AppendLine(Coverage()).AppendLine();

        var recap = Path.Combine(TradingHub.Common.Product.Documents, "Recaps", $"{today.AddDays(-1):yyyy-MM-dd}.md");
        if (File.Exists(recap))
        {
            sb.AppendLine("YESTERDAY'S RECAP");
            sb.AppendLine(File.ReadAllText(recap));
        }

        return sb.ToString();
    }
}

/// <summary>
/// Exports the remembered big-trade levels so they can be used outside Trading Hub: a Pine Script for TradingView that draws the
/// levels price has not come back to, and a CSV of everything. Files go to Documents\Trading Hub\Exports.
/// </summary>
internal static class LevelExport
{
    private const int MaxLevels = 150; // TradingView allows 500 lines and 500 labels per script

    public static (string Folder, string Summary) Run()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var dir = Path.Combine(TradingHub.Common.Product.Documents, "Exports");
        Directory.CreateDirectory(dir);
        var summary = new List<string>();
        foreach (var (name, rows) in TodayReport.Load())
        {
            if (name.StartsWith("SIM-", StringComparison.OrdinalIgnoreCase)) continue;
            string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var csv = new StringBuilder("time_utc,side,size,price,touched,outcome\n");
            foreach (var r in rows.OrderByDescending(r => r.Time))
                csv.AppendLine($"{r.Time:yyyy-MM-dd HH:mm:ss},{(r.Buy ? "buy" : "sell")},{r.Volume.ToString(inv)},{r.Price.ToString(inv)},{(r.Touched ? 1 : 0)},{(r.Outcome == 1 ? "held" : r.Outcome == 2 ? "broke" : "")}");
            File.WriteAllText(Path.Combine(dir, safe + "-levels.csv"), csv.ToString());

            var open = rows.Where(r => !r.Touched && !r.Hidden).OrderByDescending(r => r.Volume).Take(MaxLevels).ToList();
            var pine = new StringBuilder();
            pine.AppendLine("//@version=5");
            pine.AppendLine($"indicator(\"Trading Hub levels {name}\", overlay = true, max_lines_count = 500, max_labels_count = 500)");
            pine.AppendLine($"// Big-trade levels from Trading Hub that price had not come back to when exported ({DateTime.Now:yyyy-MM-dd HH:mm}).");
            pine.AppendLine("// These are futures prices. On spot or another contract month the prices differ by the basis: put the difference in the input below.");
            pine.AppendLine("basis = input.float(0.0, \"Shift all levels by\")");
            pine.AppendLine(open.Count == 0 ? "plot(na)" : "if barstate.islast");
            foreach (var r in open)
            {
                string price = r.Price.ToString(inv), col = r.Buy ? "color.blue" : "color.red", text = $"{(r.Buy ? "B" : "S")} {r.Volume.ToString("0.##", inv)}";
                pine.AppendLine($"    line.new(bar_index, {price} + basis, bar_index + 1, {price} + basis, extend = extend.both, color = color.new({col}, 20), width = 1)");
                pine.AppendLine($"    label.new(bar_index, {price} + basis, \"{text}\", style = label.style_label_left, textcolor = color.white, color = color.new({col}, 40), size = size.small)");
            }

            File.WriteAllText(Path.Combine(dir, safe + "-levels.pine"), pine.ToString());
            summary.Add($"{name}: {open.Count} levels in the Pine script, {rows.Count} in the CSV");
        }

        return (dir, summary.Count == 0 ? "No saved levels to export yet." : string.Join("\n", summary) + "\nIn TradingView: Pine Editor, paste the .pine file, Add to chart.");
    }
}
