using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>The metrics of a result when the account passes through zero or ends up negative.</summary>
public class MetricsTests
{
    private static readonly DateTime T0 = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>An equity curve with one point per day starting from the initial balance.</summary>
    private static List<EquityPoint> Curve(decimal capital, params decimal[] dailyPnl)
    {
        var list = new List<EquityPoint> { new(T0, capital, 0) };
        decimal equity = capital;
        for (int i = 0; i < dailyPnl.Length; i++)
        {
            equity += dailyPnl[i];
            list.Add(new EquityPoint(T0.AddDays(i + 1), equity, 0));
        }

        return list;
    }

    private static PerformanceMetrics Metrics(List<EquityPoint> curve) => PerformanceMetrics.Compute(new List<ClosedTrade>(), curve, 10_000, true, 0, 0);

    [Fact]
    public void Losing_until_it_goes_negative_the_Sharpe_is_negative()
    {
        // it loses every day (-500 and -1,500 alternating): the account ends up at -20,000
        var m = Metrics(Curve(10_000, Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? -500m : -1_500m).ToArray()));
        Assert.True(m.SharpeRatio < 0, $"Sharpe {m.SharpeRatio}");
        Assert.True(m.SortinoRatio < 0, $"Sortino {m.SortinoRatio}");
    }

    [Fact]
    public void Recovering_from_negative_the_Sharpe_is_positive()
    {
        // one day it loses 15,000 (ending at -5,000) and then gains 600 or 1,400 per day until finishing at +25,000
        var pnl = new[] { -15_000m }.Concat(Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? 600m : 1_400m)).ToArray();
        var m = Metrics(Curve(10_000, pnl));
        Assert.True(m.SharpeRatio > 0, $"Sharpe {m.SharpeRatio}");
    }

    [Fact]
    public void Above_the_capital_the_return_is_still_measured_on_the_balance()
    {
        // always above the capital: same as before, each day on the previous day's balance
        var pnl = Enumerable.Range(0, 20).Select(i => i % 3 == 2 ? -200m : 500m).ToArray();
        var m = Metrics(Curve(10_000, pnl));
        var equity = new List<double> { 10_000 };
        foreach (var p in pnl) equity.Add(equity[^1] + (double)p);
        // the first day (the one of the initial balance) has no result
        var r = new List<double> { 0 };
        r.AddRange(equity.Skip(1).Select((e, i) => (e - equity[i]) / equity[i]));
        double mean = r.Average(), sd = Math.Sqrt(r.Sum(x => (x - mean) * (x - mean)) / (r.Count - 1));
        Assert.Equal(mean / sd * Math.Sqrt(365), m.SharpeRatio, 6);
    }
}
