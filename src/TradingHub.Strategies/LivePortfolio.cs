using System.Globalization;
using System.Text.Json;

namespace TradingHub.Strategies;

/// <summary>
/// A fill of a live strategy. <paramref name="Symbol"/> and <paramref name="PointValue"/> say which contract
/// it executed on (for example MNQ even if the strategy watches NQ); older fills do not have them and use the ones from the
/// strategy's instrument.
/// </summary>
public sealed record LiveFill(DateTime Time, bool Buy, decimal Quantity, decimal Price, string? Symbol = null, decimal PointValue = 0);

/// <summary>
/// A live strategy's own bookkeeping: its fills, its position (even if other strategies
/// trade the same instrument on the same account), realized result, closed trades and curve.
/// </summary>
public sealed class LiveLedger
{
    private DateTime _openTime;
    private int _number;

    public LiveLedger(string key, decimal pointValue)
    {
        Key = key;
        PointValue = pointValue <= 0 ? 1 : pointValue;
    }

    public string Key { get; }

    /// <summary>Point value of the strategy's instrument (the one for fills that do not carry their own).</summary>
    public decimal PointValue { get; }
    public List<LiveFill> Fills { get; } = new();

    /// <summary>Position in units of the contract it is open on (<see cref="OpenSymbol"/>).</summary>
    public decimal Position { get; private set; }
    public decimal AveragePrice { get; private set; }
    public decimal Realized { get; private set; }
    public List<ClosedTrade> Trades { get; } = new();

    /// <summary>Contract of the open position (null = the strategy's, or flat).</summary>
    public string? OpenSymbol { get; private set; }

    /// <summary>Point value of the open position.</summary>
    public decimal OpenPointValue { get; private set; }

    public decimal OpenPnL(decimal last) => Position == 0 || last <= 0 ? 0 : (last - AveragePrice) * Position * (OpenPointValue > 0 ? OpenPointValue : PointValue);

    public void Apply(LiveFill f)
    {
        Fills.Add(f);
        decimal pv = f.PointValue > 0 ? f.PointValue : PointValue;
        decimal signed = f.Buy ? f.Quantity : -f.Quantity;
        decimal before = Position;
        if (before == 0 || Math.Sign(before) == Math.Sign(signed))
        {
            if (before == 0)
            {
                _openTime = f.Time;
                OpenSymbol = f.Symbol;
                OpenPointValue = pv;
            }

            decimal total = Math.Abs(before) + f.Quantity;
            AveragePrice = total > 0 ? (AveragePrice * Math.Abs(before) + f.Price * f.Quantity) / total : f.Price;
            Position = before + signed;
            return;
        }

        decimal closing = Math.Min(Math.Abs(before), f.Quantity);
        decimal pnl = (f.Price - AveragePrice) * closing * Math.Sign(before) * (OpenPointValue > 0 ? OpenPointValue : pv);
        Realized += pnl;
        Trades.Add(new ClosedTrade(++_number, before > 0, _openTime, AveragePrice, f.Time, f.Price, closing, pnl, 0, 0, 0, "", "", 0));
        Position = before + signed;
        if (Position == 0)
        {
            AveragePrice = 0;
            OpenSymbol = null;
            OpenPointValue = 0;
        }
        else if (Math.Sign(Position) != Math.Sign(before))
        {
            // it reversed: the leftover opens at the fill price
            AveragePrice = f.Price;
            _openTime = f.Time;
            OpenSymbol = f.Symbol;
            OpenPointValue = pv;
        }
    }

    /// <summary>Equity curve at the close of each trade (it starts at 0).</summary>
    public List<EquityPoint> Curve(decimal capital)
    {
        var res = new List<EquityPoint>();
        decimal eq = capital, peak = capital;
        if (Fills.Count > 0) res.Add(new EquityPoint(Fills[0].Time, capital, 0));
        foreach (var t in Trades)
        {
            eq += t.NetPnl;
            peak = Math.Max(peak, eq);
            res.Add(new EquityPoint(t.ExitTime, eq, peak - eq));
        }

        return res;
    }
}

/// <summary>State of a live strategy (what is stored and read by the lab).</summary>
public sealed class LiveRunnerInfo
{
    public string Key { get; set; } = "";
    public string Strategy { get; set; } = "";
    public string StrategyId { get; set; } = "";
    public string FeedId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Period { get; set; } = "";
    public string Account { get; set; } = "";
    public bool Running { get; set; }
    public DateTime StartedUtc { get; set; }
    public decimal Size { get; set; } = 1;
    public decimal PointValue { get; set; } = 1;

    /// <summary>Contract it executes on, if it is not the one it watches (for example MNQ for an NQ strategy).</summary>
    public string? ExecSymbol { get; set; }
    public decimal Position { get; set; }
    public decimal OpenPnL { get; set; }
    public decimal Realized { get; set; }
    public int Trades { get; set; }

    /// <summary>Where it was launched from (panel, chart or other), account and connection, to relaunch it the same way.</summary>
    public string Source { get; set; } = "panel";
    public string AccountId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public string? TimeZoneId { get; set; }
    public double SessionStartHours { get; set; }

    /// <summary>Risk limits (in money; 0 = no limit) and whether hitting them closes the position.</summary>
    public decimal MaxDailyLoss { get; set; }
    public decimal MaxDrawdown { get; set; }
    public bool CloseOnLimit { get; set; } = true;
    public string? PortfolioId { get; set; }
    public string? StopReason { get; set; }

    /// <summary>Paused (it opens no new trades) and whether it trades real money.</summary>
    public bool Paused { get; set; }
    public bool IsReal { get; set; }
}

/// <summary>A live portfolio: strategies that run together, with their capital and, if you want, a risk agent.</summary>
public sealed class LivePortfolioDef
{
    public const string AutoId = "auto";

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";

    /// <summary>The automatic one: every strategy running live with no portfolio assigned.</summary>
    public bool Auto { get; set; }

    /// <summary>Strategies of the portfolio (key strategy|market|account). In the automatic one it is computed on its own.</summary>
    public List<string> Members { get; set; } = new();

    /// <summary>Reference capital (for percentages, drawdown and Sharpe).</summary>
    public decimal Capital { get; set; } = 25_000m;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public LiveRiskAgentSettings Agent { get; set; } = new();
}

/// <summary>
/// A portfolio's risk agent: Claude looks at how each strategy is doing (result, streak, drawdown, volatility and
/// correlation) and spreads the risk by changing the size of each one. By default it proposes and you approve every change;
/// in automatic mode (only non-real accounts) it applies on its own, always within the caps.
/// </summary>
public sealed class LiveRiskAgentSettings
{
    public bool Enabled { get; set; }

    /// <summary>propose (you approve every change) or auto (it applies on its own; never with real accounts).</summary>
    public string Mode { get; set; } = "propose";

    /// <summary>How many minutes between reviews (if there were new trades) and how many hours between reviews anyway.</summary>
    public int EveryMinutes { get; set; } = 60;
    public int MinNewTrades { get; set; } = 3;
    public int ForceEveryHours { get; set; } = 8;
    public int MaxRunsPerDay { get; set; } = 8;
    public double MaxUsdPerRun { get; set; } = 2;

    /// <summary>Caps on the size it can assign to each strategy.</summary>
    public decimal MinSize { get; set; } = 0.25m;
    public decimal MaxSize { get; set; } = 3m;

    /// <summary>Total risk per day it spreads among the strategies (% of capital) and maximum portfolio drawdown (%).</summary>
    public decimal DailyRiskPercent { get; set; } = 2m;
    public decimal MaxDrawdownPercent { get; set; } = 10m;

    /// <summary>Your own instructions for the agent (for example "prioritize the NQ ones", "nothing above x2").</summary>
    public string Instructions { get; set; } = "";

    /// <summary>Model and effort of the agent (Opus 5.5 with extra effort by default).</summary>
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "xhigh";

    public DateTime? LastRunUtc { get; set; }
    public int TradesAtLastRun { get; set; }
    public string LastSummary { get; set; } = "";
    public string? LastError { get; set; }
    public List<DateTime> RunsUtc { get; set; } = new();
    public double CostTotal { get; set; }
}

/// <summary>A size change proposed for a strategy.</summary>
public sealed class SizingItem
{
    public string Key { get; set; } = "";
    public string Strategy { get; set; } = "";
    public decimal CurrentSize { get; set; }
    public decimal ProposedSize { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>Proposed limits (null = they are not touched) and the ones it had.</summary>
    public decimal? ProposedMaxDailyLoss { get; set; }
    public decimal? ProposedMaxDrawdown { get; set; }
    public decimal CurrentMaxDailyLoss { get; set; }
    public decimal CurrentMaxDrawdown { get; set; }

    /// <summary>pending, approved o rejected.</summary>
    public string Status { get; set; } = "pending";
}

/// <summary>Proposal of size changes (from the rules or from Claude). Nothing is applied without approval.</summary>
public sealed class SizingProposal
{
    public string Id { get; set; } = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..4];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Source { get; set; } = "rules";
    public string Summary { get; set; } = "";
    public List<SizingItem> Items { get; set; } = new();

    /// <summary>Live portfolio it came from (the risk agent's ones).</summary>
    public string? PortfolioId { get; set; }
    public string? PortfolioName { get; set; }
    public bool IsPending => Items.Any(i => i.Status == "pending");
}

/// <summary>Live metrics of a strategy, for the portfolio.</summary>
public sealed class LiveStats
{
    public LiveRunnerInfo Info { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public decimal Net { get; set; }
    public decimal CurrentDrawdown { get; set; }
    public decimal RecentProfitFactor { get; set; }
    public decimal RecentExpectancy { get; set; }
    public int RecentTrades { get; set; }
}

/// <summary>
/// Persistence of the live portfolio (in {root}/Live): fills per strategy, state of each strategy and
/// size proposals. The app writes the state; Claude's lab reads it and writes proposals.
/// </summary>
public static class LiveStore
{
    public static string Root { get; set; } = Path.Combine(TradingHub.Common.Product.AppData, "Live");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    private static string Clean(string key) => string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '|' ? '_' : c));
    private static string LedgerPath(string key) => Path.Combine(Root, "llenados", Clean(key) + ".jsonl");

    /// <summary>Fills file of a strategy (to know whether it changed).</summary>
    public static string LedgerFile(string key) => LedgerPath(key);

    /// <summary>Clears a strategy's fill history (it stays in the portfolio, with no trades).</summary>
    public static void DeleteFills(string key)
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(LedgerPath(key))) File.Delete(LedgerPath(key));
            }
            catch
            {
                // it is retried
            }
        }
    }
    private static string RunnersPath => Path.Combine(Root, "estrategias.json");
    private static string ProposalsPath => Path.Combine(Root, "propuestas.json");
    private static string PortfoliosPath => Path.Combine(Root, "portafolios.json");
    private static string? _lastRunnersJson;
    private static string? _lastRunnersPath;

    public static void AppendFill(string key, LiveFill f)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath(key))!);
                File.AppendAllText(LedgerPath(key), JsonSerializer.Serialize(f) + "\n");
            }
            catch
            {
                // it is retried with the next fill
            }
        }
    }

    public static LiveLedger LoadLedger(string key, decimal pointValue)
    {
        var l = new LiveLedger(key, pointValue);
        try
        {
            if (File.Exists(LedgerPath(key)))
                foreach (var line in File.ReadLines(LedgerPath(key)))
                    if (line.Length > 0 && JsonSerializer.Deserialize<LiveFill>(line) is { } f) l.Apply(f);
        }
        catch
        {
            // corrupt file: whatever could be read is used
        }

        return l;
    }

    public static List<LiveRunnerInfo> LoadRunners()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(RunnersPath)) return JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new();
            }
            catch
            {
                // it is rebuilt
            }

            return new();
        }
    }

    /// <summary>Stores the state (merged with what is stored: stopped strategies keep their history).</summary>
    public static void SaveRunners(IEnumerable<LiveRunnerInfo> current)
    {
        lock (Gate)
        {
            var all = new Dictionary<string, LiveRunnerInfo>();
            try
            {
                if (File.Exists(RunnersPath))
                    foreach (var r in JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new()) all[r.Key] = r;
            }
            catch
            {
                // corrupt file
            }

            foreach (var r in all.Values) r.Running = false;
            foreach (var r in current) all[r.Key] = r;
            try
            {
                // only if something changed (this is called every few seconds)
                var json = JsonSerializer.Serialize(all.Values.ToList(), Json);
                if (json == _lastRunnersJson && _lastRunnersPath == RunnersPath && File.Exists(RunnersPath)) return;
                Directory.CreateDirectory(Root);
                File.WriteAllText(RunnersPath, json);
                _lastRunnersJson = json;
                _lastRunnersPath = RunnersPath;
            }
            catch
            {
                // it is retried on the next pass
            }
        }
    }

    public static decimal SizeFor(string key) => LoadRunners().FirstOrDefault(r => r.Key == key)?.Size ?? 1;

    /// <summary>What is stored for a strategy (size, limits, portfolio), or null.</summary>
    public static LiveRunnerInfo? InfoFor(string key) => LoadRunners().FirstOrDefault(r => r.Key == key);

    /// <summary>Removes a strategy from the portfolio (and, if asked, clears its fill history).</summary>
    public static void RemoveRunner(string key, bool deleteFills)
    {
        lock (Gate)
        {
            try
            {
                var all = File.Exists(RunnersPath) ? JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new() : new();
                all.RemoveAll(r => r.Key == key);
                Directory.CreateDirectory(Root);
                File.WriteAllText(RunnersPath, JsonSerializer.Serialize(all, Json));
                _lastRunnersJson = null;
                if (deleteFills && File.Exists(LedgerPath(key))) File.Delete(LedgerPath(key));
            }
            catch
            {
                // it is retried
            }
        }
    }

    /// <summary>Updates what is stored for a strategy that is not running (size, limits, portfolio).</summary>
    public static void UpdateRunner(string key, Action<LiveRunnerInfo> change)
    {
        lock (Gate)
        {
            try
            {
                var all = File.Exists(RunnersPath) ? JsonSerializer.Deserialize<List<LiveRunnerInfo>>(File.ReadAllText(RunnersPath)) ?? new() : new();
                var info = all.FirstOrDefault(r => r.Key == key);
                if (info == null) return;
                change(info);
                File.WriteAllText(RunnersPath, JsonSerializer.Serialize(all, Json));
                _lastRunnersJson = null;
            }
            catch
            {
                // it is retried
            }
        }
    }

    /// <summary>Stored live portfolios (the automatic one included, with its configuration).</summary>
    public static List<LivePortfolioDef> LoadPortfolios()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(PortfoliosPath)) return JsonSerializer.Deserialize<List<LivePortfolioDef>>(File.ReadAllText(PortfoliosPath)) ?? new();
            }
            catch
            {
                // corrupt: it is rebuilt
            }

            return new();
        }
    }

    public static void SavePortfolios(List<LivePortfolioDef> list)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Root);
                File.WriteAllText(PortfoliosPath, JsonSerializer.Serialize(list, Json));
            }
            catch
            {
                // it is retried on the next change
            }
        }
    }

    public static List<SizingProposal> LoadProposals()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(ProposalsPath)) return JsonSerializer.Deserialize<List<SizingProposal>>(File.ReadAllText(ProposalsPath)) ?? new();
            }
            catch
            {
                // corrupt
            }

            return new();
        }
    }

    public static void SaveProposals(List<SizingProposal> list)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(ProposalsPath, JsonSerializer.Serialize(list.OrderByDescending(p => p.CreatedUtc).Take(100).ToList(), Json));
        }
    }

    public static void AddProposal(SizingProposal p)
    {
        lock (Gate)
        {
            var list = LoadProposals();
            list.Add(p);
            SaveProposals(list);
        }
    }

    public static DateTime ProposalsStamp => File.Exists(ProposalsPath) ? File.GetLastWriteTimeUtc(ProposalsPath) : DateTime.MinValue;

    /// <summary>Metrics of each stored strategy (live or stopped) from its fills.</summary>
    public static List<LiveStats> Stats(decimal capital = 10_000)
    {
        var res = new List<LiveStats>();
        foreach (var info in LoadRunners())
        {
            var ledger = LoadLedger(info.Key, info.PointValue);
            res.Add(StatsOf(info, ledger, capital));
        }

        return res;
    }

    public static LiveStats StatsOf(LiveRunnerInfo info, LiveLedger ledger, decimal capital)
    {
        var curve = ledger.Curve(capital);
        var m = PerformanceMetrics.Compute(ledger.Trades, curve, capital, true, 0, 0);
        var recent = ledger.Trades.TakeLast(20).ToList();
        decimal gp = recent.Where(t => t.NetPnl > 0).Sum(t => t.NetPnl), gl = -recent.Where(t => t.NetPnl <= 0).Sum(t => t.NetPnl);
        decimal peak = capital, eq = capital;
        foreach (var t in ledger.Trades)
        {
            eq += t.NetPnl;
            peak = Math.Max(peak, eq);
        }

        return new LiveStats
        {
            Info = info,
            Metrics = m,
            Net = ledger.Realized + info.OpenPnL,
            CurrentDrawdown = peak - eq,
            RecentTrades = recent.Count,
            RecentProfitFactor = gl > 0 ? gp / gl : gp > 0 ? 999 : 0,
            RecentExpectancy = recent.Count > 0 ? recent.Average(t => t.NetPnl) : 0,
        };
    }
}

/// <summary>
/// Simple sizing rules: more for the ones performing (high recent profit factor and no heavy drawdown), less for
/// the ones that are not, and a pause for the ones exceeding their worst drawdown on a bad streak. It only proposes: the user applies.
/// </summary>
public static class LiveSizing
{
    public const int MinTrades = 10;
    public const decimal MaxSize = 3, MinSize = 0.25m;

    public static SizingProposal SuggestByRules(IReadOnlyList<LiveStats> stats)
    {
        var p = new SizingProposal { Source = "rules" };
        foreach (var s in stats.Where(s => s.Info.Running))
        {
            decimal size = s.Info.Size;
            string name = $"{s.Info.Strategy} · {s.Info.Symbol}";
            if (s.Metrics.TotalTrades < MinTrades) continue; // little sample: it is not touched
            decimal pf = s.RecentProfitFactor;
            decimal maxDd = s.Metrics.MaxDrawdown;
            decimal typicalLoss = Math.Abs(s.Metrics.AverageLoss);
            // "deep": close to the worst drawdown and to at least three typical losses (a short streak is normal)
            bool deepDd = maxDd > 0 && s.CurrentDrawdown >= maxDd * 0.9m && s.CurrentDrawdown >= typicalLoss * 3 && s.CurrentDrawdown > 0;
            bool shallowDd = s.CurrentDrawdown <= Math.Max(maxDd * 0.5m, typicalLoss * 2);
            if (pf < 0.7m && deepDd && s.RecentTrades >= 15)
                p.Items.Add(new SizingItem { Key = s.Info.Key, Strategy = name, CurrentSize = size, ProposedSize = 0, Reason = $"Pause: the PF of the last {s.RecentTrades} trades is {pf:0.00} and it is at its worst drawdown ({s.CurrentDrawdown:N2})." });
            else if (pf < 0.9m || deepDd)
                p.Items.Add(new SizingItem { Key = s.Info.Key, Strategy = name, CurrentSize = size, ProposedSize = Math.Max(MinSize, Math.Round(size * 0.5m, 2)), Reason = $"Halve it: recent PF {pf:0.00}{(deepDd ? ", drawdown close to the maximum" : "")}." });
            else if (pf >= 1.5m && s.RecentExpectancy > 0 && shallowDd && size < MaxSize)
                p.Items.Add(new SizingItem { Key = s.Info.Key, Strategy = name, CurrentSize = size, ProposedSize = Math.Min(MaxSize, Math.Round(size * 1.25m, 2)), Reason = $"Raise by 25 %: recent PF {pf:0.00}, expectancy {s.RecentExpectancy:N2} per trade and a low drawdown." });
        }

        p.Summary = p.Items.Count == 0
            ? "No changes: the strategies with enough trades are within expectations (or still have a small sample)."
            : $"{p.Items.Count} change(s) suggested by the rules (the PF of the last 20 trades and the drawdown).";
        return p;
    }
}

/// <summary>MAE and MFE of live trades (in money, as in the backtest), with the prices between the entry and the exit.</summary>
public static class TradeExcursion
{
    public static (decimal Mae, decimal Mfe) Compute(ClosedTrade t, IEnumerable<decimal> prices, decimal pointValue)
    {
        decimal sign = t.IsLong ? 1 : -1, mae = 0, mfe = 0;
        foreach (var p in prices)
        {
            if (p <= 0) continue;
            decimal exc = (p - t.EntryPrice) * sign;
            if (exc > mfe) mfe = exc;
            if (exc < mae) mae = exc;
        }

        // the exit is also a price it went through
        decimal last = (t.ExitPrice - t.EntryPrice) * sign;
        mfe = Math.Max(mfe, last);
        mae = Math.Min(mae, last);
        decimal pv = pointValue <= 0 ? 1 : pointValue;
        return (mae * t.Quantity * pv, mfe * t.Quantity * pv);
    }
}
