using System.Globalization;
using System.Text;
using TradingHub.Strategies;

namespace TradingHub.App.Lab;

/// <summary>What goes into a report: runs, portfolio and the written analysis (from Claude or from the user).</summary>
internal sealed class ReportInput
{
    public string Title { get; set; } = "";
    public string Verdict { get; set; } = "";
    public string? StrategyFile { get; set; }
    public string? StrategyName { get; set; }
    public string? Description { get; set; }
    public StrategyRun? Backtest { get; set; }
    public StrategyRun? Optimization { get; set; }
    public StrategyRun? WalkForward { get; set; }
    public PortfolioResult? Portfolio { get; set; }
    public decimal PortfolioCapital { get; set; } = 10_000;
    public string Summary { get; set; } = "";
    public string Analysis { get; set; } = "";
    public string Risks { get; set; } = "";
    public string NextSteps { get; set; } = "";
    public string Extra { get; set; } = "";
}

/// <summary>
/// Builds a professional Markdown report with every metric, table and chart from the
/// stored runs. The images are referenced as {{assets}}/file.png (ReportStore locates them).
/// </summary>
internal static class ReportBuilder
{
    private static readonly CultureInfo Fmt = CultureInfo.GetCultureInfo("en-US");

    private static string Money(decimal v, bool sign = false) => (sign && v > 0 ? "+" : "") + v.ToString("N2", Fmt);
    private static string Pct(decimal v, bool sign = false) => (sign && v > 0 ? "+" : "") + v.ToString("0.00", Fmt) + " %";
    private static string Pct(double v, bool sign = false) => Pct((decimal)(double.IsFinite(v) ? v : 0), sign);
    private static string Num(double v, int dec = 2) => double.IsFinite(v) ? v.ToString("N" + dec, Fmt) : "—";
    private static string Num(decimal v, int dec = 2) => v.ToString("N" + dec, Fmt);
    private static string Date(DateTime t) => t.ToString("dd/MM/yyyy", Fmt);
    private static string DateTimeText(DateTime t) => t.ToString("dd/MM/yyyy HH:mm", Fmt);
    private static string Ratio(decimal v) => v >= 999 ? "∞" : Num(v);

    /// <summary>CAGR and Calmar only make sense over long periods (annualizing 5 days exaggerates everything).</summary>
    private static string Annualized(TradeAnalytics a, double value, bool percent) =>
        a.Days < 90 ? "— (under 90 days)" : percent ? Pct(value, true) : Num(value);

    private static string Duration(TimeSpan d)
    {
        if (d.TotalMinutes < 1) return $"{d.TotalSeconds:0} s";
        if (d.TotalHours < 1) return $"{d.TotalMinutes:0} min";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h {d.Minutes} min";
        return $"{(int)d.TotalDays} d {d.Hours} h";
    }

    private static string Cell(object? v) => (v switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    }).Replace("|", "/");

    private static void Table(StringBuilder sb, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        sb.AppendLine("|" + string.Concat(headers.Select(_ => "---|")));
        foreach (var r in rows) sb.AppendLine("| " + string.Join(" | ", r.Select(x => x.Replace("|", "/"))) + " |");
        sb.AppendLine();
    }

    private static void KeyValues(StringBuilder sb, IEnumerable<(string Key, string Value)> rows) =>
        Table(sb, new[] { "Item", "Value" }, rows.Select(r => (IReadOnlyList<string>)new[] { r.Key, r.Value }));

    private static TimeZoneInfo Zone(BacktestSettings s)
    {
        if (string.IsNullOrEmpty(s.TimeZoneId)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(s.TimeZoneId);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static string VerdictText(string v) => v switch
    {
        "winner" => "WINNER",
        "promising" => "PROMISING",
        "loser" => "LOSER",
        "discarded" => "DISCARDED",
        _ => "",
    };

    public static (string Markdown, List<(string Name, byte[] Png)> Images) Build(ReportInput x)
    {
        var sb = new StringBuilder();
        var images = new List<(string, byte[])>();
        var main = x.Backtest;
        var s = main?.Settings ?? x.WalkForward?.Settings ?? x.Optimization?.Settings;

        // ----------------------------------------------------------------- header
        sb.AppendLine($"# {x.Title}");
        sb.AppendLine();
        var meta = new List<string>();
        if (x.StrategyName != null) meta.Add($"**Strategy:** {x.StrategyName}{(x.StrategyFile != null ? $" ({x.StrategyFile}.cs)" : "")}");
        if (s != null)
        {
            meta.Add($"**Instrument:** {s.Symbol} ({s.FeedId})");
            meta.Add($"**Candles:** {s.Period}");
            meta.Add($"**Period:** {Date(s.FromUtc)} – {Date(s.ToUtc.AddSeconds(-1))}");
        }
        else if (x.Portfolio != null)
        {
            meta.Add($"**Portfolio:** {x.Portfolio.Components.Count} strategies");
            meta.Add($"**Period:** {Date(x.Portfolio.From)} – {Date(x.Portfolio.To)}");
        }

        meta.Add($"**Generated:** {DateTimeText(DateTime.Now)} with Trading Hub");
        sb.AppendLine("> " + string.Join(" · ", meta));
        sb.AppendLine();
        if (VerdictText(x.Verdict) is { Length: > 0 } vt)
        {
            sb.AppendLine($"> **Verdict: {vt}**{(x.Summary.Length > 0 ? " — " + x.Summary.Trim().Replace("\n", " ") : "")}");
            sb.AppendLine();
        }

        // main subject of the statistics: the backtest or the portfolio
        List<ClosedTrade>? trades = main?.Trades;
        List<EquityPoint>? equity = main?.Equity;
        PerformanceMetrics? m = main?.Metrics;
        decimal capital = s?.InitialCapital ?? x.PortfolioCapital;
        if (main == null && x.Portfolio != null)
        {
            trades = x.Portfolio.Trades;
            equity = x.Portfolio.Equity;
            m = x.Portfolio.Metrics;
            capital = x.PortfolioCapital;
        }

        TradeAnalytics? a = null;
        if (trades != null && m != null)
            a = TradeAnalytics.Compute(trades, equity ?? new List<EquityPoint>(), capital, m, s != null ? Zone(s) : TimeZoneInfo.Utc,
                s?.FromUtc ?? x.Portfolio?.From, s?.ToUtc ?? x.Portfolio?.To);

        int section = 0;
        string H2(string title) => $"## {++section}. {title}";

        // ----------------------------------------------------------------- 1. executive summary
        sb.AppendLine(H2("Executive summary"));
        sb.AppendLine();
        // the summary goes in the verdict box; if there is no verdict, here
        if (x.Summary.Length > 0 && VerdictText(x.Verdict).Length == 0)
        {
            sb.AppendLine(x.Summary.Trim());
            sb.AppendLine();
        }

        if (m != null && a != null)
        {
            var wf = x.WalkForward?.WalkForward;
            var mc = main?.MonteCarlo;
            var pairs = new List<(string, string)>
            {
                ("Net profit", Money(m.NetProfit, true)),
                ("Total return", Pct(m.ReturnPercent, true)),
                ("CAGR (annualized)", Annualized(a, a.CagrPercent, true)),
                ("Profit factor", Ratio(m.ProfitFactor)),
                ("Sharpe", Num(m.SharpeRatio)),
                ("Sortino", Num(m.SortinoRatio)),
                ("Maximum drawdown", $"{Money(m.MaxDrawdown)} ({Pct(m.MaxDrawdownPercent)})"),
                ("Calmar", Annualized(a, a.Calmar, false)),
                ("Trades", m.TotalTrades.ToString(Fmt)),
                ("Win rate", Pct(m.WinRate)),
                ("Expectancy per trade", Money(m.Expectancy, true)),
                ("SQN", Num(a.Sqn)),
            };
            if (wf != null)
            {
                pairs.Add(("Walk-forward efficiency", Num(wf.WalkForwardEfficiency)));
                pairs.Add(("Winning windows (WF)", Pct(wf.ProfitableWindowsPercent)));
            }

            if (mc != null)
            {
                pairs.Add(("Drawdown p95 (Monte Carlo)", Money(mc.DrawdownP95)));
                pairs.Add(("Risk of ruin", Pct(mc.RuinProbabilityPercent)));
            }

            var rows = new List<IReadOnlyList<string>>();
            for (int i = 0; i < pairs.Count; i += 2)
                rows.Add(new[] { pairs[i].Item1, pairs[i].Item2, i + 1 < pairs.Count ? pairs[i + 1].Item1 : "", i + 1 < pairs.Count ? pairs[i + 1].Item2 : "" });
            Table(sb, new[] { "Indicator", "Value", "Indicator", "Value" }, rows);

            if (equity is { Count: > 1 })
            {
                var extra = x.Portfolio != null && main == null ? PortfolioCurves(x.Portfolio, capital) : null;
                images.Add(("capital.png", ReportCharts.Equity(equity.Select(p => (p.Time, p.Equity)).ToList(), capital, main == null ? "Portfolio equity curve" : "Equity curve and drawdown", extra)));
                sb.AppendLine("![Equity curve and drawdown]({{assets}}/capital.png)");
                sb.AppendLine();
            }
        }

        // ----------------------------------------------------------------- 2. strategy
        if (main != null || x.StrategyName != null)
        {
            sb.AppendLine(H2("Strategy"));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(x.Description))
            {
                sb.AppendLine(x.Description.Trim());
                sb.AppendLine();
            }

            var parms = main?.Parameters ?? x.WalkForward?.Parameters;
            if (parms is { Count: > 0 })
            {
                sb.AppendLine("**Parameters used**");
                sb.AppendLine();
                Table(sb, new[] { "Parameter", "Value" }, parms.Select(kv => (IReadOnlyList<string>)new[] { kv.Key, Cell(kv.Value) }));
            }

            var hash = main?.CodeHash ?? x.WalkForward?.CodeHash;
            if (!string.IsNullOrEmpty(hash)) sb.AppendLine($"Code version: `{hash[..Math.Min(12, hash.Length)]}` (it is kept in the strategy's history).\n");
        }

        // ----------------------------------------------------------------- 3. data and assumptions
        if (main != null && s != null)
        {
            sb.AppendLine(H2("Data and assumptions"));
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Instrument", $"{s.Symbol} ({s.FeedId})"),
                ("Candles", s.Period),
                ("From", DateTimeText(s.FromUtc) + " UTC"),
                ("To", DateTimeText(s.ToUtc) + " UTC"),
                ("Calendar days", Num((s.ToUtc - s.FromUtc).TotalDays, 1)),
                ("Ticks processed", main.Ticks.ToString("N0", Fmt)),
                ("Candles processed", main.Bars.ToString("N0", Fmt)),
                ("Initial capital", Money(s.InitialCapital)),
                ("Commission", s.CommissionPercent > 0 ? $"{s.CommissionPercent.ToString("0.####", Fmt)} % of the notional per side" : $"{Money(s.CommissionPerUnit)} per unit"),
                ("Slippage", $"{s.SlippageTicks} tick(s) on market orders and stops"),
                ("Limit orders", s.LimitFill == LimitFillMode.TradeThrough ? "they fill only if price trades through them" : "they fill as soon as price touches"),
                ("Session time zone", string.IsNullOrEmpty(s.TimeZoneId) ? "UTC" : s.TimeZoneId),
                ("Session start", s.SessionStart.ToString(@"hh\:mm", Fmt)),
                ("Position at the end", s.CloseAtEnd ? "it is closed when the period ends" : "left open"),
                ("Computation time", $"{Num(main.Seconds, 1)} s"),
            });
        }

        // ----------------------------------------------------------------- 4. performance
        if (m != null && a != null)
        {
            sb.AppendLine(H2("Performance"));
            sb.AppendLine();
            sb.AppendLine("### Profitability");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Net profit", Money(m.NetProfit, true)),
                ("Gross profit", Money(m.GrossProfit)),
                ("Gross loss", Money(-m.GrossLoss)),
                ("Commissions paid", Money(m.Commission)),
                ("Capital final", Money(a.FinalEquity)),
                ("Total return", Pct(m.ReturnPercent, true)),
                ("CAGR (annualized)", Annualized(a, a.CagrPercent, true)),
                ("Profit factor", Ratio(m.ProfitFactor)),
                ("Expectancy per trade", Money(m.Expectancy, true)),
                ("Days with trades", a.TradingDays.ToString(Fmt)),
                ("Average result per day traded", Money(a.AverageDailyPnl, true)),
                ("Best day", Money(a.BestDay, true)),
                ("Worst day", Money(a.WorstDay, true)),
                ("Winning days", Pct(a.ProfitableDaysPercent)),
            });

            sb.AppendLine("### Trades");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Trades", m.TotalTrades.ToString(Fmt)),
                ("Winners / losers", $"{m.Winners} / {m.Losers}"),
                ("Win rate", Pct(m.WinRate)),
                ("Average result", Money(m.AverageTrade, true)),
                ("Average of the winners", Money(m.AverageWin, true)),
                ("Average of the losers", Money(m.AverageLoss, true)),
                ("Average win / loss ratio", Num(m.PayoffRatio)),
                ("Largest winner", Money(m.LargestWin, true)),
                ("Largest loser", Money(m.LargestLoss, true)),
                ("Maximum winning streak", m.MaxConsecutiveWins.ToString(Fmt)),
                ("Maximum losing streak", m.MaxConsecutiveLosses.ToString(Fmt)),
                ("Standard deviation per trade", Money(a.TradeStdDev)),
                ("Trades per day", Num(a.TradesPerDay)),
                ("Average / median / maximum duration", $"{Duration(a.AverageDuration)} / {Duration(a.MedianDuration)} / {Duration(a.MaxDuration)}"),
                ("Average duration winners / losers", $"{Duration(a.AverageWinDuration)} / {Duration(a.AverageLossDuration)}"),
                ("Average candles winners / losers", $"{Num(a.AverageBarsWin, 1)} / {Num(a.AverageBarsLoss, 1)}"),
                ("Time in the market", m.ExposurePercent > 0 ? Pct(m.ExposurePercent) : "—"),
            });

            sb.AppendLine("### Risk");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Maximum drawdown", $"{Money(m.MaxDrawdown)} ({Pct(m.MaxDrawdownPercent)})"),
                ("Longest drawdown (trade closes)", $"{Num(a.MaxDrawdownDays, 1)} days"),
                ("Longest period without new highs", $"{Num(a.LongestFlatDays, 1)} days"),
                ("Recovery factor", Ratio(m.RecoveryFactor)),
                ("Sharpe (annualized)", Num(m.SharpeRatio)),
                ("Sortino (annualized)", Num(m.SortinoRatio)),
                ("Calmar", Annualized(a, a.Calmar, false)),
                ("Ulcer index", Num(a.UlcerIndex)),
                ("SQN (system quality)", Num(a.Sqn)),
                ("Kelly", Pct(a.KellyPercent)),
                ("Average MAE (adverse excursion)", Money(a.AverageMae)),
                ("Average MFE (favourable excursion)", Money(a.AverageMfe)),
                ("Edge ratio (MFE / MAE)", Num(a.EdgeRatio)),
            });

            sb.AppendLine("### Longs and shorts");
            sb.AppendLine();
            var all = new BucketStat { Label = "Total" };
            foreach (var t in trades!) all.Add(t);
            var cols = new[] { a.Long, a.Short, all };
            Table(sb, new[] { "", "Longs", "Shorts", "Total" }, new IReadOnlyList<string>[]
            {
                new[] { "Trades" }.Concat(cols.Select(c => c.Trades.ToString(Fmt))).ToArray(),
                new[] { "Win rate" }.Concat(cols.Select(c => Pct(c.WinRate))).ToArray(),
                new[] { "Net result" }.Concat(cols.Select(c => Money(c.Net, true))).ToArray(),
                new[] { "Profit factor" }.Concat(cols.Select(c => Ratio(c.ProfitFactor))).ToArray(),
                new[] { "Average" }.Concat(cols.Select(c => Money(c.Average, true))).ToArray(),
                new[] { "Largest winner" }.Concat(cols.Select(c => Money(c.LargestWin, true))).ToArray(),
                new[] { "Largest loser" }.Concat(cols.Select(c => Money(c.LargestLoss, true))).ToArray(),
            });

            // ----------------------------------------------------------------- 5. monthly
            if (a.Monthly.Count > 0)
            {
                sb.AppendLine(H2("Monthly performance"));
                sb.AppendLine();
                images.Add(("monthly.png", ReportCharts.Monthly(a.Monthly)));
                sb.AppendLine("![Monthly performance]({{assets}}/monthly.png)");
                sb.AppendLine();
                string[] months = { "Ene", "Feb", "Mar", "Abr", "May", "Jun", "Jul", "Ago", "Sep", "Oct", "Nov", "Dic" };
                Table(sb, new[] { "Year" }.Concat(months).Append("Year").ToArray(),
                    a.Monthly.Select(y => (IReadOnlyList<string>)new[] { y.Year.ToString(CultureInfo.InvariantCulture) }
                        .Concat(y.MonthPercent.Select(v => v.HasValue ? Pct(v.Value, true) : ""))
                        .Append(Pct(y.YearPercent, true)).ToArray()));
            }

            // ----------------------------------------------------------------- 6. trades
            sb.AppendLine(H2("Trade analysis"));
            sb.AppendLine();
            if (a.Histogram.Count > 0)
            {
                images.Add(("distribution.png", ReportCharts.Histogram(a.Histogram)));
                sb.AppendLine("![Result distribution]({{assets}}/distribution.png)");
                sb.AppendLine();
            }

            if (a.ByHour.Count > 0)
            {
                sb.AppendLine($"### By entry hour ({(s == null || string.IsNullOrEmpty(s.TimeZoneId) ? "UTC" : s.TimeZoneId)})");
                sb.AppendLine();
                Table(sb, new[] { "Time", "Trades", "Win rate", "Net", "Profit factor", "Average" },
                    a.ByHour.Select(b => (IReadOnlyList<string>)new[] { b.Label, b.Trades.ToString(Fmt), Pct(b.WinRate), Money(b.Net, true), Ratio(b.ProfitFactor), Money(b.Average, true) }));
            }

            if (a.ByWeekday.Count > 0)
            {
                sb.AppendLine("### By weekday");
                sb.AppendLine();
                Table(sb, new[] { "Day", "Trades", "Win rate", "Net", "Profit factor", "Average" },
                    a.ByWeekday.Select(b => (IReadOnlyList<string>)new[] { b.Label, b.Trades.ToString(Fmt), Pct(b.WinRate), Money(b.Net, true), Ratio(b.ProfitFactor), Money(b.Average, true) }));
            }

            IReadOnlyList<string> TradeRow(ClosedTrade t) => new[]
            {
                t.Number.ToString(Fmt), t.IsLong ? "Long" : "Short", DateTimeText(t.EntryTime), DateTimeText(t.ExitTime),
                t.EntryPrice.ToString("0.########", Fmt), t.ExitPrice.ToString("0.########", Fmt), Money(t.NetPnl, true), Money(t.Mae), Money(t.Mfe), t.Bars.ToString(Fmt),
            };
            string[] tradeHeaders = { "#", "Side", "Entry", "Exit", "Entry price", "Exit price", "Result", "MAE", "MFE", "Candles" };
            if (a.Best.Count > 0)
            {
                sb.AppendLine("### Best trades");
                sb.AppendLine();
                Table(sb, tradeHeaders, a.Best.Select(TradeRow));
                sb.AppendLine("### Worst trades");
                sb.AppendLine();
                Table(sb, tradeHeaders, a.Worst.Select(TradeRow));
            }

            // ----------------------------------------------------------------- 7. drawdowns
            if (a.Drawdowns.Count > 0)
            {
                sb.AppendLine(H2("Main drawdowns"));
                sb.AppendLine();
                Table(sb, new[] { "#", "Start", "Trough", "Recovery", "Depth", "%", "Days" },
                    a.Drawdowns.Select((d, i) => (IReadOnlyList<string>)new[]
                    {
                        (i + 1).ToString(Fmt), DateTimeText(d.Start), DateTimeText(d.Trough), d.Recovery is { } r ? DateTimeText(r) : "not recovered",
                        Money(-d.Depth), Pct(-d.DepthPercent), Num(d.Days, 1),
                    }));
            }
        }

        // ----------------------------------------------------------------- 8. optimization
        if (x.Optimization?.Optimization is { } opt && opt.Rows.Count > 0)
        {
            sb.AppendLine(H2("Optimization"));
            sb.AppendLine();
            var enabled = opt.Ranges.Where(r => r.Enabled).ToList();
            KeyValues(sb, new[]
            {
                ("Optimized metric", opt.Metric.ToString()),
                ("Combinations tested", opt.Combinations.ToString("N0", Fmt)),
                ("Profitable combinations", Pct(100.0 * opt.Rows.Count(r => r.Metrics.NetProfit > 0) / opt.Rows.Count)),
                ("Time", $"{Num(opt.Seconds, 1)} s"),
            }.Concat(enabled.Select(r => ($"Range of {r.Label}", $"{Num(r.Min, 4)} to {Num(r.Max, 4)} in steps of {Num(r.Step, 4)}"))));

            sb.AppendLine("### Best combinations");
            sb.AppendLine();
            var names = enabled.Select(r => r.Name).ToList();
            Table(sb, new[] { "#" }.Concat(enabled.Select(r => r.Label)).Concat(new[] { "Net", "PF", "Sharpe", "Max DD", "Ops", "Win rate" }).ToArray(),
                opt.Rows.Take(10).Select((r, i) => (IReadOnlyList<string>)new[] { (i + 1).ToString(Fmt) }
                    .Concat(names.Select(n => Cell(r.Parameters.GetValueOrDefault(n))))
                    .Concat(new[] { Money(r.Metrics.NetProfit, true), Ratio(r.Metrics.ProfitFactor), Num(r.Metrics.SharpeRatio), Money(r.Metrics.MaxDrawdown), r.Metrics.TotalTrades.ToString(Fmt), Pct(r.Metrics.WinRate) })
                    .ToArray()));

            sb.AppendLine("### Sensitivity per parameter");
            sb.AppendLine();
            sb.AppendLine("The average of every combination tested with each value: an even plateau is a sign of robustness; an isolated peak, of overfitting.");
            sb.AppendLine();
            foreach (var r in enabled)
            {
                sb.AppendLine($"**{r.Label}**");
                sb.AppendLine();
                var groups = opt.Rows.GroupBy(row => Cell(row.Parameters.GetValueOrDefault(r.Name)))
                    .OrderBy(g => double.TryParse(g.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0);
                Table(sb, new[] { "Value", "Combinations", "Profitable", "Average net", "Average Sharpe" },
                    groups.Select(g => (IReadOnlyList<string>)new[]
                    {
                        g.Key, g.Count().ToString(Fmt), Pct(100.0 * g.Count(z => z.Metrics.NetProfit > 0) / g.Count()),
                        Money(g.Average(z => z.Metrics.NetProfit), true), Num(g.Average(z => z.Metrics.SharpeRatio)),
                    }));
            }
        }

        // ----------------------------------------------------------------- 9. walk-forward
        if (x.WalkForward?.WalkForward is { } w && w.Windows.Count > 0)
        {
            sb.AppendLine(H2("Walk-forward (out-of-sample validation)"));
            sb.AppendLine();
            var om = w.OutSampleMetrics;
            KeyValues(sb, new[]
            {
                ("Windows", $"{w.Windows.Count} · {w.InSampleDays} days of optimization and {w.OutSampleDays} of testing ({(w.Anchored ? "anchored" : "rolling")})"),
                ("Metric", w.Metric.ToString()),
                ("Walk-forward efficiency", Num(w.WalkForwardEfficiency)),
                ("Winning windows", Pct(w.ProfitableWindowsPercent)),
                ("Out-of-sample net", Money(om.NetProfit, true)),
                ("Out-of-sample profit factor", Ratio(om.ProfitFactor)),
                ("Out-of-sample Sharpe", Num(om.SharpeRatio)),
                ("Maximum out-of-sample drawdown", $"{Money(om.MaxDrawdown)} ({Pct(om.MaxDrawdownPercent)})"),
                ("Out-of-sample trades", om.TotalTrades.ToString(Fmt)),
                ("Out-of-sample win rate", Pct(om.WinRate)),
            });
            Table(sb, new[] { "Window", "Optimization", "Test", "Chosen parameters", "Net (optim.)", "Net (test)", "Test ops", "Efficiency" },
                w.Windows.Select(win => (IReadOnlyList<string>)new[]
                {
                    win.Number.ToString(Fmt), $"{Date(win.InSampleFrom)} – {Date(win.InSampleTo)}", $"{Date(win.OutSampleFrom)} – {Date(win.OutSampleTo)}",
                    string.Join(", ", win.BestParameters.Select(kv => $"{kv.Key}={Cell(kv.Value)}")),
                    Money(win.InSample.NetProfit, true), Money(win.OutSample.NetProfit, true), win.OutSample.TotalTrades.ToString(Fmt), Num(win.Efficiency),
                }));
            if (w.OutSampleEquity.Count > 1)
            {
                images.Add(("walkforward.png", ReportCharts.Equity(w.OutSampleEquity.Select(p => (p.Time, p.Equity)).ToList(), w.OutSampleEquity[0].Equity, "Out-of-sample equity (chained)")));
                sb.AppendLine("![Out-of-sample equity]({{assets}}/walkforward.png)");
                sb.AppendLine();
            }

            // how stable the parameters were across windows
            var paramNames = w.Windows.SelectMany(win => win.BestParameters.Keys).Distinct().ToList();
            var varying = paramNames.Where(p => w.Windows.Select(win => Cell(win.BestParameters.GetValueOrDefault(p))).Distinct().Count() > 1).ToList();
            if (varying.Count > 0)
            {
                sb.AppendLine("### Parameter stability");
                sb.AppendLine();
                Table(sb, new[] { "Parameter", "Chosen values (times)" }, varying.Select(p => (IReadOnlyList<string>)new[]
                {
                    p, string.Join(" · ", w.Windows.GroupBy(win => Cell(win.BestParameters.GetValueOrDefault(p))).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})")),
                }));
            }
        }

        // ----------------------------------------------------------------- 10. Monte Carlo
        if (main?.MonteCarlo is { } mcr && mcr.Runs > 0)
        {
            sb.AppendLine(H2("Monte Carlo"));
            sb.AppendLine();
            sb.AppendLine($"The {main.Trades?.Count ?? 0} trades were reshuffled at random {mcr.Runs:N0} times to see which drawdowns are possible with the same edge.");
            sb.AppendLine();
            KeyValues(sb, new[]
            {
                ("Simulations", mcr.Runs.ToString("N0", Fmt)),
                ("Drawdown mediano", Money(mcr.DrawdownP50)),
                ("Drawdown p95", Money(mcr.DrawdownP95)),
                ("Drawdown p99", Money(mcr.DrawdownP99)),
                ("Worst simulated drawdown", Money(mcr.WorstDrawdown)),
                ("Risk of ruin (drawdown ≥ 50 % of capital)", Pct(mcr.RuinProbabilityPercent)),
                ("Suggested capital (2 × p99 drawdown)", Money(mcr.DrawdownP99 * 2)),
            });
            if (mcr.SampleCurves.Count > 0)
            {
                images.Add(("montecarlo.png", ReportCharts.Curves(mcr.SampleCurves.Select(cv => (IReadOnlyList<decimal>)cv).ToList(), capital, "Monte Carlo: curves with the trades in a different order")));
                sb.AppendLine("![Monte Carlo]({{assets}}/montecarlo.png)");
                sb.AppendLine();
            }
        }

        // ----------------------------------------------------------------- 11. portfolio
        if (x.Portfolio is { } pf && pf.Components.Count > 0)
        {
            sb.AppendLine(H2("Portfolio"));
            sb.AppendLine();
            if (pf.NoOverlap) sb.AppendLine("> The strategies do not share dates: the union of the periods was combined (the correlation is not reliable).\n");
            Table(sb, new[] { "#", "Strategy", "Instrument", "Weight", "Net", "Sharpe", "Max DD", "Ops" },
                pf.Components.Select((c, i) => (IReadOnlyList<string>)new[]
                {
                    (i + 1).ToString(Fmt), c.StrategyName, $"{c.Settings.Symbol} {c.Settings.Period}", Num(c.Weight),
                    Money(pf.ComponentMetrics[i].NetProfit, true), Num(pf.ComponentMetrics[i].SharpeRatio), Money(pf.ComponentMetrics[i].MaxDrawdown), pf.ComponentMetrics[i].TotalTrades.ToString(Fmt),
                }));
            KeyValues(sb, new[]
            {
                ("Common period", $"{Date(pf.From)} – {Date(pf.To)}"),
                ("Portfolio net", Money(pf.Metrics.NetProfit, true)),
                ("Portfolio Sharpe / average of the strategies", $"{Num(pf.Metrics.SharpeRatio)} / {Num(pf.AverageComponentSharpe)}"),
                ("Portfolio drawdown / sum of the drawdowns", $"{Money(pf.Metrics.MaxDrawdown)} / {Money(pf.SumOfComponentDrawdowns)}"),
                ("Drawdown reduction from diversifying", Pct(pf.DrawdownReductionPercent)),
                ("Average / maximum correlation", $"{Num(pf.AverageCorrelation)} / {Num(pf.MaxCorrelation)}"),
            });
            var labels = pf.Components.Select(c => c.Label).ToList();
            images.Add(("correlation.png", ReportCharts.Correlation(labels, pf.Correlation)));
            sb.AppendLine("![Correlation]({{assets}}/correlation.png)");
            sb.AppendLine();
            if (main != null)
            {
                images.Add(("portfolio.png", ReportCharts.Equity(pf.Equity.Select(p => (p.Time, p.Equity)).ToList(), x.PortfolioCapital, "Portfolio curve", PortfolioCurves(pf, x.PortfolioCapital))));
                sb.AppendLine("![Portfolio curve]({{assets}}/portfolio.png)");
                sb.AppendLine();
            }
        }

        // ----------------------------------------------------------------- written analysis
        void Text(string title, string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            sb.AppendLine(H2(title));
            sb.AppendLine();
            sb.AppendLine(body.Trim());
            sb.AppendLine();
        }

        Text("Analysis", x.Analysis);
        Text("Risks", x.Risks);
        Text("Next steps", x.NextSteps);
        if (!string.IsNullOrWhiteSpace(x.Extra))
        {
            sb.AppendLine(x.Extra.Trim());
            sb.AppendLine();
        }

        // ----------------------------------------------------------------- glossary
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("### Glossary");
        sb.AppendLine();
        foreach (var (k, v) in new[]
                 {
                     ("Profit factor", "gross profit divided by gross loss. Above 1.5 is good; above 2, very good."),
                     ("Sharpe / Sortino", "average daily return over its volatility (Sortino only penalizes the downside), annualized."),
                     ("CAGR", "equivalent compound annual growth rate."),
                     ("Calmar", "CAGR divided by the maximum drawdown in %."),
                     ("Drawdown", "the fall from the equity curve's previous high."),
                     ("SQN", "System Quality Number (Van Tharp): the average result over its standard deviation, times the square root of the trade count (max. 100). Above 2 is good."),
                     ("Ulcer index", "the depth and duration of the drawdowns (lower is better)."),
                     ("Kelly", "the fraction of capital that maximizes growth given the win rate and the win/loss ratio (in practice a fraction of it is used)."),
                     ("MAE / MFE", "the most each trade went against / in favour before it closed."),
                     ("Walk-forward efficiency", "out-of-sample performance over the optimization's (per day). Above 0.5 suggests it is not overfitted."),
                     ("Monte Carlo", "reshuffles the trades at random to estimate the drawdowns possible with the same edge."),
                 })
            sb.AppendLine($"- **{k}:** {v}");
        sb.AppendLine();
        sb.AppendLine("> Results simulated on historical data, with commissions and slippage. They do not guarantee future results.");
        return (sb.ToString(), images);
    }

    private static List<(string Label, IReadOnlyList<(DateTime Time, decimal Equity)> Points)> PortfolioCurves(PortfolioResult pf, decimal capital)
    {
        var res = new List<(string, IReadOnlyList<(DateTime, decimal)>)>();
        for (int i = 0; i < pf.Components.Count; i++)
        {
            decimal eq = capital;
            var pts = new List<(DateTime, decimal)> { (pf.From, capital) };
            for (int k = 0; k < pf.Days.Count; k++)
            {
                eq += pf.DailyPnl[i][k];
                pts.Add((pf.Days[k].ToDateTime(new TimeOnly(23, 59)), eq));
            }

            res.Add((pf.Components[i].Label, pts));
        }

        return res;
    }
}
