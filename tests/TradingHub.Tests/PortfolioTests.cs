using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>Report statistics and strategy portfolios.</summary>
public class PortfolioTests
{
    private static readonly DateTime D0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    private static ClosedTrade T(int n, int day, decimal pnl, bool isLong = true) =>
        new(n, isLong, D0.AddDays(day), 100, D0.AddDays(day).AddHours(2), 100 + pnl, 1, pnl, 0, -Math.Abs(pnl) / 2, Math.Abs(pnl), "e", "s", 10);

    private static PortfolioComponent C(string name, params decimal[] dailyPnl)
    {
        var trades = dailyPnl.Select((p, i) => T(i + 1, i, p)).ToList();
        return new PortfolioComponent
        {
            StrategyName = name,
            Label = name,
            Settings = new BacktestSettings { FeedId = "binance-futures", Symbol = name, FromUtc = D0.Date, ToUtc = D0.Date.AddDays(dailyPnl.Length) },
            Trades = trades,
            Metrics = PerformanceMetrics.Compute(trades, Array.Empty<EquityPoint>(), 10_000, true, 0, 0),
        };
    }

    [Fact]
    public void Trade_analytics_monthly_sides_and_drawdowns()
    {
        var trades = new List<ClosedTrade>
        {
            T(1, 0, 100), T(2, 1, -50, false), T(3, 40, 200), T(4, 41, -300), T(5, 42, -100, false), T(6, 70, 500),
        };
        var m = PerformanceMetrics.Compute(trades, Array.Empty<EquityPoint>(), 10_000, true, 0, 0);
        var a = TradeAnalytics.Compute(trades, Array.Empty<EquityPoint>(), 10_000, m);
        Assert.Equal(4, a.Long.Trades);
        Assert.Equal(2, a.Short.Trades);
        Assert.Equal(350m, a.Long.Net + a.Short.Net);
        var y = Assert.Single(a.Monthly);
        Assert.Equal(50m, y.MonthNet[0]);     // January: +100 −50
        Assert.Equal(-200m, y.MonthNet[1]);   // February: +200 −300 −100
        Assert.Equal(500m, y.MonthNet[2]);    // March
        Assert.Equal(350m, y.YearNet);
        Assert.Equal(6, a.Histogram.Sum(h => h.Count));
        // worst closing drawdown: from 10,250 to 9,850 (-400), recovered by March's
        var dd = a.Drawdowns[0];
        Assert.Equal(400m, dd.Depth);
        Assert.NotNull(dd.Recovery);
        Assert.Equal(500m, a.Best[0].NetPnl);
        Assert.Equal(-300m, a.Worst[0].NetPnl);
    }

    [Fact]
    public void A_portfolio_adds_up_results_and_measures_correlation()
    {
        var a = C("A", 100, -50, 80, -20, 60, -10, 40);
        var copy = C("B", 100, -50, 80, -20, 60, -10, 40);
        var opposite = C("C", -100, 50, -80, 20, -60, 10, -40);
        // with a single strategy, the portfolio and the strategy have the same metrics
        var single = PortfolioAnalyzer.Analyze(new[] { a }, 10_000);
        Assert.Equal(single.Metrics.SharpeRatio, single.ComponentMetrics[0].SharpeRatio, 6);
        Assert.Equal(single.Metrics.MaxDrawdown, single.ComponentMetrics[0].MaxDrawdown);

        var same = PortfolioAnalyzer.Analyze(new[] { a, copy }, 10_000);
        Assert.Equal(1.0, same.Correlation[0, 1], 3);
        Assert.Equal(400m, same.Metrics.NetProfit);

        var hedged = PortfolioAnalyzer.Analyze(new[] { a, opposite }, 10_000);
        Assert.Equal(-1.0, hedged.Correlation[0, 1], 3);
        Assert.Equal(0m, hedged.Metrics.NetProfit);
        Assert.Equal(0m, hedged.Metrics.MaxDrawdown);
        Assert.True(hedged.DrawdownReductionPercent > 99);

        // with weights: half of B
        copy.Weight = 0.5m;
        var weighted = PortfolioAnalyzer.Analyze(new[] { a, copy }, 10_000);
        Assert.Equal(300m, weighted.Metrics.NetProfit);
    }

    [Fact]
    public void The_suggestion_discards_the_correlated_strategies()
    {
        var rnd = new Random(3);
        decimal[] Series(int seed)
        {
            var r = new Random(seed);
            return Enumerable.Range(0, 60).Select(_ => (decimal)Math.Round(r.NextDouble() * 200 - 80, 2)).ToArray();
        }

        var a = C("A", Series(1));
        var clone = C("A bis", a.Trades.Select(t => t.Pnl * 1.1m).ToArray());
        var independent = C("Z", Series(99));
        var candidates = new[] { a, clone, independent };
        var chosen = PortfolioAnalyzer.SuggestUncorrelated(candidates, 10_000, maxComponents: 3, maxCorrelation: 0.5);
        Assert.Contains(2, chosen);
        Assert.False(chosen.Contains(0) && chosen.Contains(1), "it picked two almost identical strategies");
        _ = rnd;
    }
}
