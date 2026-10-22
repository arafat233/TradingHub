using System.Text.Json.Serialization;

namespace TradingHub.Strategies;

/// <summary>How limit orders are filled in the simulation.</summary>
public enum LimitFillMode
{
    /// <summary>Only if the price trades through the other side of the limit (conservative).</summary>
    TradeThrough,

    /// <summary>As soon as the price touches the limit (optimistic).</summary>
    Touch,
}

/// <summary>Configuration of a run.</summary>
public sealed class BacktestSettings
{
    public string FeedId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public string Period { get; set; } = "M5";
    public decimal InitialCapital { get; set; } = 10_000m;

    /// <summary>Fixed commission per unit/contract and per side.</summary>
    public decimal CommissionPerUnit { get; set; }

    /// <summary>Percentage commission on the notional, per side (e.g. 0.04 = 0.04%).</summary>
    public decimal CommissionPercent { get; set; } = 0.04m;

    /// <summary>Slippage in ticks of market orders and stops.</summary>
    public int SlippageTicks { get; set; } = 1;

    public LimitFillMode LimitFill { get; set; } = LimitFillMode.TradeThrough;

    /// <summary>Session time zone (null = UTC).</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>Session start time in that zone.</summary>
    public TimeSpan SessionStart { get; set; }

    /// <summary>Give the strategy the recorded order book (<see cref="Strategy.Book"/>) when there is one for the range.</summary>
    public bool UseBook { get; set; } = true;

    /// <summary>Close the open position at the end of the backtest.</summary>
    public bool CloseAtEnd { get; set; } = true;

    public BacktestSettings Clone() => (BacktestSettings)MemberwiseClone();
}

/// <summary>A closed trade (entry + exit).</summary>
public sealed record ClosedTrade(
    int Number,
    bool IsLong,
    DateTime EntryTime,
    decimal EntryPrice,
    DateTime ExitTime,
    decimal ExitPrice,
    decimal Quantity,
    decimal Pnl,
    decimal Commission,
    decimal Mae,
    decimal Mfe,
    string EntryName,
    string ExitName,
    int Bars)
{
    public decimal NetPnl => Pnl - Commission;

    /// <summary>
    /// How the market had been on entry (last 24 hours, with the candles the strategy could see): trend efficiency
    /// of the hourly closes (0 = pure range, 1 = clean trend; a random walk gives ~0.2). null if there is no data.
    /// </summary>
    public double? EntryTrend { get; init; }

    /// <summary>Volatility on entry: standard deviation of the hourly returns of the last 24 hours, in %.</summary>
    public double? EntryVolatility { get; init; }

    /// <summary>Percentile of that volatility among the last month's (0 = the lowest, 100 = the highest).</summary>
    public double? EntryVolatilityRank { get; init; }

    /// <summary>Which way the market had moved in the 24 hours before the entry (1 rising, -1 falling).</summary>
    public int? EntryMarketDirection { get; init; }
}

/// <summary>An open position (side, quantity, average price and since when).</summary>
public sealed record OpenPosition(bool IsLong, decimal Quantity, decimal AveragePrice, DateTime EntryTime);

/// <summary>A point on the equity curve.</summary>
public readonly record struct EquityPoint(DateTime Time, decimal Equity, decimal Drawdown);

/// <summary>Performance metrics.</summary>
public sealed class PerformanceMetrics
{
    public decimal NetProfit { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal GrossLoss { get; set; }
    public decimal Commission { get; set; }
    public decimal ProfitFactor { get; set; }
    public int TotalTrades { get; set; }
    public int Winners { get; set; }
    public int Losers { get; set; }
    public decimal WinRate { get; set; }
    public decimal AverageTrade { get; set; }
    public decimal AverageWin { get; set; }
    public decimal AverageLoss { get; set; }
    public decimal PayoffRatio { get; set; }
    public decimal LargestWin { get; set; }
    public decimal LargestLoss { get; set; }
    public int MaxConsecutiveWins { get; set; }
    public int MaxConsecutiveLosses { get; set; }
    public decimal MaxDrawdown { get; set; }
    public decimal MaxDrawdownPercent { get; set; }
    public decimal RecoveryFactor { get; set; }
    public double SharpeRatio { get; set; }
    public double SortinoRatio { get; set; }
    public decimal ReturnPercent { get; set; }
    public decimal AverageBarsInTrade { get; set; }
    public decimal LongNetProfit { get; set; }
    public decimal ShortNetProfit { get; set; }
    public decimal ExposurePercent { get; set; }
    public decimal Expectancy { get; set; }

    /// <summary>Value of the metric chosen to optimize.</summary>
    public double Fitness(FitnessMetric m) => m switch
    {
        FitnessMetric.ProfitFactor => TotalTrades == 0 ? 0 : (double)Math.Min(ProfitFactor, 100),
        FitnessMetric.Sharpe => SharpeRatio,
        FitnessMetric.Sortino => SortinoRatio,
        FitnessMetric.RecoveryFactor => (double)RecoveryFactor,
        FitnessMetric.NetProfitOverDrawdown => MaxDrawdown > 0 ? (double)(NetProfit / MaxDrawdown) : (double)NetProfit,
        FitnessMetric.WinRate => (double)WinRate,
        FitnessMetric.Expectancy => (double)Expectancy,
        _ => (double)NetProfit,
    };

    public static PerformanceMetrics Compute(IReadOnlyList<ClosedTrade> trades, IReadOnlyList<EquityPoint> equity, decimal capital, bool crypto, long barsInMarket, long totalBars)
    {
        var m = new PerformanceMetrics { TotalTrades = trades.Count };
        int run = 0, maxW = 0, maxL = 0;
        decimal sumBars = 0;
        foreach (var t in trades)
        {
            decimal p = t.NetPnl;
            m.NetProfit += p;
            m.Commission += t.Commission;
            if (t.IsLong) m.LongNetProfit += p;
            else m.ShortNetProfit += p;
            sumBars += t.Bars;
            if (p > 0)
            {
                m.Winners++;
                m.GrossProfit += p;
                m.LargestWin = Math.Max(m.LargestWin, p);
                run = run > 0 ? run + 1 : 1;
                maxW = Math.Max(maxW, run);
            }
            else
            {
                m.Losers++;
                m.GrossLoss += -p;
                m.LargestLoss = Math.Min(m.LargestLoss, p);
                run = run < 0 ? run - 1 : -1;
                maxL = Math.Max(maxL, -run);
            }
        }

        m.MaxConsecutiveWins = maxW;
        m.MaxConsecutiveLosses = maxL;
        m.ProfitFactor = m.GrossLoss > 0 ? m.GrossProfit / m.GrossLoss : m.GrossProfit > 0 ? 999 : 0;
        m.WinRate = trades.Count > 0 ? 100m * m.Winners / trades.Count : 0;
        m.AverageTrade = trades.Count > 0 ? m.NetProfit / trades.Count : 0;
        m.AverageWin = m.Winners > 0 ? m.GrossProfit / m.Winners : 0;
        m.AverageLoss = m.Losers > 0 ? -m.GrossLoss / m.Losers : 0;
        m.PayoffRatio = m.AverageLoss != 0 ? m.AverageWin / -m.AverageLoss : 0;
        m.Expectancy = trades.Count > 0 ? m.WinRate / 100 * m.AverageWin + (1 - m.WinRate / 100) * m.AverageLoss : 0;
        m.AverageBarsInTrade = trades.Count > 0 ? sumBars / trades.Count : 0;
        m.ReturnPercent = capital > 0 ? 100 * m.NetProfit / capital : 0;
        m.ExposurePercent = totalBars > 0 ? 100m * barsInMarket / totalBars : 0;

        // drawdown over the equity curve
        decimal peak = capital, maxDd = 0, maxDdPct = 0;
        foreach (var e in equity)
        {
            peak = Math.Max(peak, e.Equity);
            decimal dd = peak - e.Equity;
            if (dd > maxDd) maxDd = dd;
            if (peak > 0 && dd / peak > maxDdPct) maxDdPct = dd / peak;
        }

        m.MaxDrawdown = maxDd;
        m.MaxDrawdownPercent = 100 * maxDdPct;
        m.RecoveryFactor = maxDd > 0 ? m.NetProfit / maxDd : m.NetProfit > 0 ? 999 : 0;

        // Sharpe and Sortino with daily returns. Below the initial capital the return is measured on the capital: with
        // the account negative (or near zero) dividing by the balance flips the sign (winning looked like losing) or blows up
        var daily = new List<double>();
        if (equity.Count > 1)
        {
            DateOnly day = DateOnly.FromDateTime(equity[0].Time);
            double start = (double)capital, prev = start, last = (double)equity[0].Equity;
            double Return() => Math.Max(prev, start) is var basis && basis > 0 ? (last - prev) / basis : 0;
            foreach (var e in equity)
            {
                var d = DateOnly.FromDateTime(e.Time);
                if (d != day)
                {
                    daily.Add(Return());
                    prev = last;
                    day = d;
                }

                last = (double)e.Equity;
            }

            daily.Add(Return());
        }

        if (daily.Count > 1)
        {
            double mean = daily.Average();
            double sd = Math.Sqrt(daily.Sum(r => (r - mean) * (r - mean)) / (daily.Count - 1));
            double downside = Math.Sqrt(daily.Where(r => r < 0).Sum(r => r * r) / daily.Count);
            double ann = Math.Sqrt(crypto ? 365 : 252);
            m.SharpeRatio = sd > 0 ? mean / sd * ann : 0;
            m.SortinoRatio = downside > 0 ? mean / downside * ann : 0;
        }

        return m;
    }
}

public enum FitnessMetric
{
    NetProfit,
    ProfitFactor,
    Sharpe,
    Sortino,
    RecoveryFactor,
    NetProfitOverDrawdown,
    WinRate,
    Expectancy,
}

/// <summary>Result of a backtest.</summary>
public sealed class BacktestResult
{
    public string Strategy { get; set; } = "";
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public BacktestSettings Settings { get; set; } = new();
    public PerformanceMetrics Metrics { get; set; } = new();
    public List<ClosedTrade> Trades { get; set; } = new();
    public List<EquityPoint> Equity { get; set; } = new();
    public List<string> Log { get; set; } = new();
    public long Ticks { get; set; }
    public int Bars { get; set; }
    public double Seconds { get; set; }
    public string? Error { get; set; }

    /// <summary>Position left open at the end (only without "close at the end"; the chart uses it to keep managing it live).</summary>
    [JsonIgnore] public OpenPosition? OpenPosition { get; set; }

    [JsonIgnore] public bool Ok => Error == null;

    /// <summary>Reduces the equity curve to at most N points (to store and draw).</summary>
    public static List<EquityPoint> Downsample(IReadOnlyList<EquityPoint> points, int max)
    {
        if (points.Count <= max) return points.ToList();
        var res = new List<EquityPoint>(max + 2);
        double step = points.Count / (double)max;
        for (int i = 0; i < max; i++)
        {
            // it keeps the worst point of each stretch so drawdowns are not hidden
            int a = (int)(i * step), b = Math.Min(points.Count, (int)((i + 1) * step));
            var worst = points[a];
            for (int k = a; k < b; k++)
                if (points[k].Drawdown > worst.Drawdown) worst = points[k];
            res.Add(worst);
        }

        res.Add(points[^1]);
        return res;
    }
}
