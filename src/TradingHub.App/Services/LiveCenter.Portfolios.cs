using TradingHub.Strategies;

namespace TradingHub.App.Services;

internal sealed partial class LiveCenter
{
    /// <summary>
    /// The portfolios: the automatic one (every strategy running live with no portfolio of yours) and the ones you built. The
    /// automatic one is created on its own the first time there are strategies running with no portfolio, and it stores its configuration.
    /// </summary>
    public List<LivePortfolioView> Portfolios(List<LiveEntry>? entries = null)
    {
        entries ??= Entries();
        var defs = LiveStore.LoadPortfolios();
        var res = new List<LivePortfolioView>();
        var assigned = new HashSet<string>(defs.Where(d => !d.Auto).SelectMany(d => d.Members));
        var free = entries.Where(e => (e.Running || e.WaitingResume) && !assigned.Contains(e.Key)).ToList();
        var auto = defs.FirstOrDefault(d => d.Auto);
        if (free.Count > 0)
        {
            if (auto == null)
            {
                auto = new LivePortfolioDef { Id = LivePortfolioDef.AutoId, Auto = true, Name = "Automatic · all live" };
                defs.Insert(0, auto);
                LiveStore.SavePortfolios(defs);
            }

            auto.Members = free.Select(e => e.Key).ToList();
            res.Add(new LivePortfolioView { Def = auto, Members = free });
        }

        foreach (var d in defs.Where(d => !d.Auto))
            res.Add(new LivePortfolioView { Def = d, Members = d.Members.Select(k => entries.FirstOrDefault(e => e.Key == k)).OfType<LiveEntry>().ToList() });
        return res;
    }

    /// <summary>Stores (creates or updates) a portfolio. A strategy is in a single portfolio of yours at a time.</summary>
    public void SavePortfolio(LivePortfolioDef def)
    {
        var defs = LiveStore.LoadPortfolios();
        if (!def.Auto)
            foreach (var other in defs.Where(d => d.Id != def.Id && !d.Auto))
                other.Members.RemoveAll(def.Members.Contains);
        int i = defs.FindIndex(d => d.Id == def.Id);
        if (i >= 0) defs[i] = def;
        else defs.Add(def);
        LiveStore.SavePortfolios(defs);
        if (def.Auto) return;
        foreach (var key in def.Members) SetPortfolio(key, def.Id);
    }

    /// <summary>Removes a portfolio (its strategies keep running and move to the automatic one).</summary>
    public void DeletePortfolio(string id)
    {
        var defs = LiveStore.LoadPortfolios();
        var def = defs.FirstOrDefault(d => d.Id == id);
        if (def == null || def.Auto) return;
        defs.Remove(def);
        LiveStore.SavePortfolios(defs);
        foreach (var key in def.Members) SetPortfolio(key, null);
    }

    /// <summary>Assigns a strategy to a portfolio (null = none: it stays in the automatic one).</summary>
    public void Assign(string key, string? portfolioId)
    {
        var defs = LiveStore.LoadPortfolios();
        foreach (var d in defs.Where(d => !d.Auto)) d.Members.Remove(key);
        var target = defs.FirstOrDefault(d => d.Id == portfolioId && !d.Auto);
        target?.Members.Add(key);
        LiveStore.SavePortfolios(defs);
        SetPortfolio(key, target?.Id);
    }

    private void SetPortfolio(string key, string? id)
    {
        if (Running(key) is { } r) r.PortfolioId = id;
        LiveStore.UpdateRunner(key, i => i.PortfolioId = id);
        _svc.SaveLiveState();
    }

    /// <summary>Starts every stopped strategy of the portfolio (not the locked ones: whoever launched them manages those).</summary>
    public async Task<List<string>> StartAllAsync(LivePortfolioView p)
    {
        var errors = new List<string>();
        foreach (var m in p.Members.Where(m => !m.Running && !m.Locked))
        {
            var err = await ResumeAsync(m);
            if (err != null) errors.Add($"{m.Info.Strategy} · {m.Info.Symbol}: {err}");
        }

        return errors;
    }

    /// <summary>Stops every strategy of the portfolio (optionally closing their positions).</summary>
    public async Task StopAllAsync(LivePortfolioView p, bool close)
    {
        foreach (var m in p.Members.Where(m => m.Running && !m.Locked)) await StopAsync(m, close);
    }

    /// <summary>
    /// Portfolio numbers from each strategy's closed trades: the daily result of each one, the correlation
    /// between them, the joint curve and the metrics (of each one and of the total), since the first one started.
    /// </summary>
    public static PortfolioResult Analyze(LivePortfolioView p)
    {
        if (p.Members.Count == 0) return new PortfolioResult();
        var now = DateTime.UtcNow;
        var from = p.Members.Select(m => m.Ledger.Fills.Count > 0 ? m.Ledger.Fills[0].Time : now).Min();
        if (from >= now) from = now.AddDays(-1);
        var comps = p.Members.Select(m => new PortfolioComponent
        {
            StrategyId = m.Info.StrategyId,
            StrategyName = m.Info.Strategy,
            Label = $"{m.Info.Strategy} · {m.Info.Symbol}",
            Weight = 1,
            Trades = m.Ledger.Trades.ToList(),
            Settings = new BacktestSettings { FeedId = m.Info.FeedId, Symbol = m.Info.Symbol, Period = m.Info.Period, FromUtc = from, ToUtc = now },
        }).ToList();
        decimal capital = p.Def.Capital > 0 ? p.Def.Capital : 25_000m;
        var res = PortfolioAnalyzer.Analyze(comps, capital);

        // trade-by-trade curve: a drawdown inside a day with many trades counts too (the daily one hides it)
        bool crypto = comps.All(c => c.Settings.FeedId.StartsWith("binance", StringComparison.Ordinal) || c.Settings.FeedId.StartsWith("bybit", StringComparison.Ordinal) || c.Settings.FeedId == "sim");
        var all = comps.SelectMany(c => c.Trades).OrderBy(t => t.ExitTime).ToList();
        var sharpe = res.Metrics.SharpeRatio;
        var sortino = res.Metrics.SortinoRatio;
        res.Trades = all;
        res.Equity = TradeCurve(all, from, capital);
        res.Metrics = PerformanceMetrics.Compute(all, res.Equity, capital, crypto, 0, 0);
        res.Metrics.SharpeRatio = sharpe;
        res.Metrics.SortinoRatio = sortino;
        res.ComponentMetrics = comps.Select(c =>
        {
            var trades = c.Trades.OrderBy(t => t.ExitTime).ToList();
            return PerformanceMetrics.Compute(trades, TradeCurve(trades, from, capital), capital, crypto, 0, 0);
        }).ToList();
        res.SumOfComponentDrawdowns = res.ComponentMetrics.Sum(m => m.MaxDrawdown);
        return res;
    }

    /// <summary>Equity curve at the close of each trade.</summary>
    private static List<EquityPoint> TradeCurve(IReadOnlyList<ClosedTrade> trades, DateTime from, decimal capital)
    {
        var curve = new List<EquityPoint> { new(trades.Count > 0 && trades[0].ExitTime < from ? trades[0].ExitTime : from, capital, 0) };
        decimal eq = capital, peak = capital;
        foreach (var t in trades)
        {
            eq += t.NetPnl;
            peak = Math.Max(peak, eq);
            curve.Add(new EquityPoint(t.ExitTime, eq, peak - eq));
        }

        return curve;
    }
}
