using System.Globalization;
using System.Text;
using System.Text.Json;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>
/// Risk agent of a live portfolio: Claude (Opus, extra effort) looks at how each strategy is doing -result,
/// recent streak, drawdown against its own history, volatility of the daily result and correlation with the others- and spreads
/// the risk: the size of each one and, if needed, its maximum daily loss and maximum drawdown, within the caps you
/// set. By default it leaves a proposal and you approve every change; in automatic mode (only if none of them trades a
/// real account) it applies them on its own. The locked ones (the ones someone else manages) are left untouched.
/// </summary>
internal sealed class LiveRiskAgent
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly AppServices _svc;
    private readonly LiveCenter _center;
    private readonly Dictionary<string, string> _activity = new();
    private readonly Dictionary<string, CancellationTokenSource> _running = new();

    public LiveRiskAgent(AppServices svc, LiveCenter center)
    {
        _svc = svc;
        _center = center;
    }

    /// <summary>Goes up with every change (to refresh the view).</summary>
    public int Version { get; private set; }

    public bool IsRunning(string portfolioId) => _running.ContainsKey(portfolioId);

    public bool AnyRunning => _running.Count > 0;

    /// <summary>What it is doing with that portfolio (or null).</summary>
    public string? Activity(string portfolioId) => _activity.GetValueOrDefault(portfolioId);

    public void Cancel(string portfolioId)
    {
        if (_running.TryGetValue(portfolioId, out var cts)) cts.Cancel();
    }

    private void Touch() => Version++;

    /// <summary>Checks which portfolios are due for a review (if there were new trades or a lot of time passed).</summary>
    public void Tick()
    {
        if (!LiveStore.LoadPortfolios().Any(d => d.Agent.Enabled)) return;
        var entries = _center.Entries();
        foreach (var p in _center.Portfolios(entries))
        {
            var a = p.Def.Agent;
            if (!a.Enabled || IsRunning(p.Def.Id)) continue;
            if (!p.Members.Any(m => m.Running && !m.Locked)) continue;
            if (RunsToday(a) >= a.MaxRunsPerDay) continue;
            int trades = p.Members.Sum(m => m.Ledger.Trades.Count);
            var now = DateTime.UtcNow;
            bool due = a.LastRunUtc is not { } last
                ? trades >= a.MinNewTrades
                : (now - last).TotalMinutes >= Math.Max(10, a.EveryMinutes) &&
                  (trades - a.TradesAtLastRun >= a.MinNewTrades || (now - last).TotalHours >= Math.Max(1, a.ForceEveryHours));
            if (due) _ = RunAsync(p.Def.Id, manual: false);
        }
    }

    public static int RunsToday(LiveRiskAgentSettings a) => a.RunsUtc.Count(t => t.ToLocalTime().Date == DateTime.Now.Date);

    /// <summary>Runs the agent on a portfolio. Returns null if it went fine, or the reason.</summary>
    public async Task<string?> RunAsync(string portfolioId, bool manual)
    {
        if (IsRunning(portfolioId)) return "It is already reviewing that portfolio.";
        var p = _center.Portfolios().FirstOrDefault(x => x.Def.Id == portfolioId);
        if (p == null) return "The portfolio was not found.";
        var a = p.Def.Agent;
        var adjustable = p.Members.Where(m => !m.Locked && (m.Running || m.WaitingResume)).ToList();
        if (adjustable.Count == 0) return "There are no strategies to adjust.";
        if (!manual && RunsToday(a) >= a.MaxRunsPerDay) return "It hit today's review cap.";
        if (ClaudeCodeBridge.FindCli() == null) return "Claude Code was not found on this Mac.";

        var cts = new CancellationTokenSource();
        _running[portfolioId] = cts;
        _activity[portfolioId] = "Reviewing the portfolio's risk…";
        Touch();
        try
        {
            var prompt = BuildPrompt(p, adjustable);
            var dir = Path.Combine(LiveStore.Root, "agente-riesgo", p.Def.Id);
            Directory.CreateDirectory(dir);
            var agent = new ClaudeAgent();
            var req = new ClaudeAgentRequest
            {
                Role = "live-risk",
                Prompt = prompt,
                SystemPrompt = SystemPrompt,
                WorkDir = dir,
                Tools = Array.Empty<string>(),
                LabTools = Array.Empty<string>(),
                PermissionMode = "dontAsk",
                Model = string.IsNullOrWhiteSpace(a.Model) ? ClaudeCodeBridge.Model : a.Model,
                Effort = string.IsNullOrWhiteSpace(a.Effort) ? ClaudeCodeBridge.Effort : a.Effort,
                MaxUsd = a.MaxUsdPerRun,
                Timeout = TimeSpan.FromMinutes(12),
            };
            var res = await agent.RunAsync(req, cts.Token);
            a.LastRunUtc = DateTime.UtcNow;
            a.TradesAtLastRun = p.Members.Sum(m => m.Ledger.Trades.Count);
            a.RunsUtc.Add(DateTime.UtcNow);
            a.RunsUtc.RemoveAll(t => t < DateTime.UtcNow.AddDays(-7));
            a.CostTotal += res.CostUsd;
            if (!res.Ok || res.Json is not { } json)
            {
                a.LastError = res.Error ?? "The agent did not return the requested JSON block.";
                if ((res.Error ?? "").Contains("or newer is required", StringComparison.OrdinalIgnoreCase))
                    a.LastError = "Your Claude Code is too old for Opus 5.5: open the Claude app (it updates itself) or run “claude update”.";
                Save(p.Def);
                _center.Notify($"⚠ Risk agent for “{p.Def.Name}”: {a.LastError}");
                return a.LastError;
            }

            a.LastError = null;
            var proposal = Parse(json, p, adjustable);
            a.LastSummary = proposal.Summary;
            Save(p.Def);
            if (proposal.Items.Count == 0)
            {
                if (manual) _center.Notify($"Risk agent for “{p.Def.Name}”: no changes. {Short(proposal.Summary, 160)}");
                return null;
            }

            bool auto = a.Mode == "auto" && !p.HasReal && adjustable.All(m => !m.Info.IsReal);
            if (auto)
            {
                foreach (var item in proposal.Items)
                {
                    var e = adjustable.First(m => m.Key == item.Key);
                    _center.ApplyRisk(e, item.ProposedSize, item.ProposedMaxDailyLoss ?? item.CurrentMaxDailyLoss, item.ProposedMaxDrawdown ?? item.CurrentMaxDrawdown,
                        e.Runner?.CloseOnLimit ?? e.Info.CloseOnLimit);
                    item.Status = "approved";
                }

                proposal.Source = "agent (automatic)";
                LiveStore.AddProposal(proposal);
                _center.Notify($"The risk agent adjusted {proposal.Items.Count} strategy(ies) in “{p.Def.Name}” (automatic mode, simulated accounts).");
            }
            else
            {
                LiveStore.AddProposal(proposal);
                _center.Notify($"The risk agent proposes {proposal.Items.Count} change(s) in “{p.Def.Name}”: review them in Live → Portfolios.");
            }

            return null;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            a.LastError = ex.Message;
            Save(p.Def);
            return ex.Message;
        }
        finally
        {
            _running.Remove(portfolioId);
            _activity.Remove(portfolioId);
            cts.Dispose();
            Touch();
        }
    }

    private static void Save(LivePortfolioDef def)
    {
        var defs = LiveStore.LoadPortfolios();
        var stored = defs.FirstOrDefault(d => d.Id == def.Id);
        if (stored == null) defs.Add(def);
        else stored.Agent = def.Agent;
        LiveStore.SavePortfolios(defs);
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string N(decimal v) => v.ToString("0.##", Inv);

    // ===================================================================== request
    internal const string SystemPrompt = """
        You are the risk manager of a portfolio of algorithmic strategies running live in Trading Hub (the
        trading platform of an individual trader). There is no human in the conversation: decide with professional judgement and explain.

        YOUR JOB: spread the risk among the portfolio's strategies dynamically and intelligently, so the
        portfolio makes as much as possible per unit of risk without going over budget. The tools you have: the
        SIZE of each strategy (multiplier on its orders: 1 = as it was programmed, 0 = it opens no new trades)
        and its LIMITS (maximum daily loss and maximum drawdown, in money; on hitting them the strategy halts or stops).

        How you think (like the risk manager of a quantitative fund):
        - Sample: with few trades (fewer than ~20) the numbers are noise. Stay close to 1x and make small changes. Do not chase
          short streaks, neither up nor down.
        - Even risk: strategies with a more volatile daily result get less size (inverse-volatility style
          weights), unless the evidence of an edge is strong.
        - Correlation: if two strategies move together (daily result correlation > 0.6) they are almost the same
          bet: their combined size has to come down.
        - Degradation: current drawdown near or above its worst historical drawdown, with a bad recent profit factor and
          enough sample, is a sign the edge is gone: cut hard or set 0 (pause opening) explaining why.
        - Budget: the sum of the maximum daily losses should not exceed the portfolio's daily risk, and the
          portfolio's drawdown should not get close to the maximum allowed. If it does, bring the risk down across the board.
        - Gradual changes: no more than +/-50% of the current size per review, except to halt one that degraded.
        - Whole contracts: orders go in whole contracts. The size multiplies the quantity the strategy
          programmed, and I tell you which contracts each size lands on. On a future with a micro (NQ->MNQ, ES->MES: 1 mini = 10
          micros) and "smart contract" active, a fraction of the mini executes in micros with the same exposure
          (x0.5 of 1 NQ = 5 MNQ), so you can fine-tune in tenths. With no micro, anything that does not reach 1 contract still
          sends 1 (the minimum): going below that does not lower the risk; to reduce further, set 0 and explain it.
        - Always respect the size caps you are given and the trader's instructions.
        - Never make up numbers: use only the data I give you. If nothing needs to change, change nothing.

        Answer in English, clear and concrete: 3 to 8 lines of analysis and at the end ONE single ```json block:
        ```json
        {"summary":"a 1 to 3 sentence summary of how the portfolio is doing and what you are doing",
         "allocations":[{"key":"the strategy's exact key","size":1.25,"max_daily_loss":250,"max_drawdown":1500,
                         "reason":"why, with the numbers that justify it"}]}
        ```
        Include in allocations only the strategies that change (size or limits). max_daily_loss and max_drawdown are
        optional: set them only if you change them (0 = no limit). Valid JSON, no comments.
        [[ROL:live-risk]]
        """;

    private string BuildPrompt(LivePortfolioView p, List<LiveEntry> adjustable)
    {
        var a = p.Def.Agent;
        decimal capital = p.Def.Capital > 0 ? p.Def.Capital : 25_000m;
        var analysis = LiveCenter.Analyze(p);
        var sb = new StringBuilder();
        sb.AppendLine($"Portfolio “{p.Def.Name}” · reference capital {N(capital)} · date {DateTime.Now:yyyy-MM-dd HH:mm} (local time).");
        sb.AppendLine($"Budget: total daily risk {N(a.DailyRiskPercent)} % of capital ({N(capital * a.DailyRiskPercent / 100)}), maximum portfolio drawdown {N(a.MaxDrawdownPercent)} % ({N(capital * a.MaxDrawdownPercent / 100)}).");
        sb.AppendLine($"Size caps per strategy: between {N(a.MinSize)} and {N(a.MaxSize)} (or 0 so it opens no new trades).");
        if (!string.IsNullOrWhiteSpace(a.Instructions)) sb.AppendLine($"Trader instructions: {a.Instructions.Trim()}");
        if (a.LastSummary.Length > 0 && a.LastRunUtc is { } last) sb.AppendLine($"Your previous review ({last.ToLocalTime():dd/MM HH:mm}): {a.LastSummary}");
        sb.AppendLine();
        var m = analysis.Metrics;
        sb.AppendLine($"PORTFOLIO (closed trades): net {N(m.NetProfit)} · {m.TotalTrades} trades · PF {N(Math.Min(m.ProfitFactor, 99))} · max drawdown {N(m.MaxDrawdown)} ·" +
                      $"Sharpe {m.SharpeRatio.ToString("0.00", Inv)} · open now {N(p.Members.Sum(x => x.OpenPnL))}");
        sb.AppendLine();
        sb.AppendLine("STRATEGIES:");
        for (int i = 0; i < p.Members.Count; i++)
        {
            var e = p.Members[i];
            var stats = LiveStore.StatsOf(e.Info, e.Ledger, capital);
            var sm = stats.Metrics;
            bool canChange = adjustable.Contains(e);
            var r = e.Runner;
            decimal size = r?.SizeMultiplier ?? e.Info.Size;
            decimal dl = r?.MaxDailyLoss ?? e.Info.MaxDailyLoss, dd = r?.MaxDrawdown ?? e.Info.MaxDrawdown;
            double days = e.Ledger.Fills.Count > 0 ? (DateTime.UtcNow - e.Ledger.Fills[0].Time).TotalDays : 0;
            sb.AppendLine($"- key: {e.Key}");
            sb.AppendLine($"  {e.Info.Strategy} · {e.Info.Symbol} {e.Info.Period} · account {e.Info.Account}{(e.Info.IsReal ? " (REAL)" : " (simulated)")} · {e.StateLabel}" +
                          $"{(canChange ? "" : $" · you CANNOT change it ({(e.Locked ? "it is locked" : "it is stopped")})")}");
            sb.AppendLine($"  size ×{N(size)} · max daily loss {(dl > 0 ? N(dl) :"no limit")} · max drawdown {(dd > 0 ? N(dd) :"no limit")} · position {N(e.Ledger.Position)} · open {N(e.OpenPnL)}");
            sb.AppendLine("  " + ContractLine(e, size));
            sb.AppendLine($"  {sm.TotalTrades} trades in {days:0.#} days · net {N(stats.Net)} · PF {N(Math.Min(sm.ProfitFactor, 99))} · win rate {sm.WinRate:0} % ·" +
                          $"avg/trade {N(sm.TotalTrades > 0 ? e.Ledger.Trades.Average(t => t.NetPnl) : 0)} · average win {N(sm.AverageWin)} · average loss {N(sm.AverageLoss)}");
            sb.AppendLine($"  current drawdown {N(stats.CurrentDrawdown)} · worst drawdown {N(sm.MaxDrawdown)} · last {stats.RecentTrades}: PF {N(Math.Min(stats.RecentProfitFactor, 99))}, average {N(stats.RecentExpectancy)}" +
                          $"{(r != null ? $" · today's result {N(r.TodayPnL)}" : "")}");
            if (i < analysis.DailyPnl.Count)
            {
                var daily = analysis.DailyPnl[i];
                var vals = daily.Where(v => v != 0).ToList();
                double vol = vals.Count > 1 ? Math.Sqrt(vals.Sum(v => Math.Pow((double)v - vals.Average(x => (double)x), 2)) / (vals.Count - 1)) : 0;
                sb.AppendLine($"  daily result (last {Math.Min(20, daily.Length)} days): {string.Join(" ", daily.TakeLast(20).Select(N))} · daily volatility {vol.ToString("0.##", Inv)}");
            }
        }

        if (p.Members.Count > 1 && analysis.Correlation.GetLength(0) == p.Members.Count)
        {
            sb.AppendLine();
            sb.AppendLine("CORRELATION of the daily result (Pearson):");
            for (int i = 0; i < p.Members.Count; i++)
                for (int j = i + 1; j < p.Members.Count; j++)
                    sb.AppendLine($"  {p.Members[i].Info.Strategy}·{p.Members[i].Info.Symbol} ↔ {p.Members[j].Info.Strategy}·{p.Members[j].Info.Symbol}: {analysis.Correlation[i, j].ToString("0.00", Inv)}");
        }

        sb.AppendLine();
        sb.AppendLine("Decide the risk allocation and return the JSON block (only with the ones that change).");
        return sb.ToString();
    }

    /// <summary>Which contracts the orders land on with the current size and with the neighbouring ones (so it reasons in whole contracts).</summary>
    private string ContractLine(LiveEntry e, decimal size)
    {
        decimal qty = LiveCenter.QuantityOf(e.Info.Parameters);
        string Of(decimal s)
        {
            if (s <= 0) return "0 (does not open)";
            var (symbol, ratio) = _center.PreferredContract(e.Info.FeedId, e.Info.Symbol, qty, s);
            decimal units = Math.Floor(qty * s * ratio + 0.0000001m);
            return $"×{N(s)} = {N(Math.Max(1, units))} {symbol}{(units < 1 ? " (minimum)" : "")}";
        }

        string micro = MarketInfo.IsFutures(e.Info.FeedId) && MarketInfo.MicroOf(e.Info.Symbol) is { } m
            ? _svc.Settings.SmartContracts ? $"it has a micro ({m}): fractions of the mini go in micros" : $"it has a micro ({m}) but the smart contract option is off"
            : "no micro: less than 1 contract sends 1";
        var steps = new[] { size * 0.5m, size, size * 1.5m }.Select(v => Math.Round(v, 2)).Distinct();
        return $"contracts: the strategy asks for {N(qty)} {e.Info.Symbol} per order · {micro} · {string.Join(" · ", steps.Select(Of))}";
    }

    /// <summary>Reads what the agent returned and leaves it as a proposal (within the caps; only the ones it can touch).</summary>
    internal static SizingProposal Parse(JsonElement json, LivePortfolioView p, List<LiveEntry> adjustable)
    {
        var a = p.Def.Agent;
        var proposal = new SizingProposal
        {
            Source = "agente",
            PortfolioId = p.Def.Id,
            PortfolioName = p.Def.Name,
            Summary = json.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? Short(s.GetString() ?? "", 600) : "",
        };
        if (!json.TryGetProperty("allocations", out var allocs) || allocs.ValueKind != JsonValueKind.Array) return proposal;
        foreach (var el in allocs.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            string key = el.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
            var e = adjustable.FirstOrDefault(x => x.Key == key);
            if (e == null) continue;
            decimal current = e.Runner?.SizeMultiplier ?? e.Info.Size;
            decimal curDl = e.Runner?.MaxDailyLoss ?? e.Info.MaxDailyLoss, curDd = e.Runner?.MaxDrawdown ?? e.Info.MaxDrawdown;
            decimal size = Num(el, "size") ?? current;
            size = size <= 0 ? 0 : Math.Clamp(Math.Round(size, 2), a.MinSize, a.MaxSize);
            decimal? dl = Num(el, "max_daily_loss") is { } d1 ? Math.Max(0, Math.Round(d1, 2)) : null;
            decimal? dd = Num(el, "max_drawdown") is { } d2 ? Math.Max(0, Math.Round(d2, 2)) : null;
            if (dl == curDl) dl = null;
            if (dd == curDd) dd = null;
            if (size == current && dl == null && dd == null) continue;
            proposal.Items.Add(new SizingItem
            {
                Key = key,
                Strategy = $"{e.Info.Strategy} · {e.Info.Symbol}",
                CurrentSize = current,
                ProposedSize = size,
                CurrentMaxDailyLoss = curDl,
                CurrentMaxDrawdown = curDd,
                ProposedMaxDailyLoss = dl,
                ProposedMaxDrawdown = dd,
                Reason = el.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? Short(r.GetString() ?? "", 500) : "",
            });
        }

        return proposal;
    }

    private static decimal? Num(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Float, Inv, out var ds)) return ds;
        return null;
    }
}
