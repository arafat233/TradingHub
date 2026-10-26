using System.Text;
using TradingHub.Feeds;

namespace TradingHub.Data;

/// <summary>One trade of a rule being tested: the result in ticks after costs, and the cost that was included (to see what it was before).</summary>
public sealed record RuleTrade(DateTime Day, DateTime Entry, double NetTicks, double CostTicks);

/// <summary>Candle-based simulation of a single trade, with conservative order assumptions.</summary>
public static class CandleSim
{
    /// <summary>
    /// Follows a trade through 1-minute candles from <paramref name="start"/>. The stop wins when a candle reaches both the stop and the
    /// target; the stop and the time exit cost <paramref name="slipTicks"/> more; the target is a limit order and costs nothing more.
    /// With <paramref name="firstCandleStopOnly"/> the first candle (the one where the entry order filled) can hit the stop but not the target.
    /// Returns the result in ticks before commission.
    /// </summary>
    public static double Run(IReadOnlyList<Kline> bars, int start, bool isLong, decimal entry, decimal stop, decimal target, int maxBars, decimal tick, int slipTicks, bool firstCandleStopOnly = false)
    {
        int last = Math.Min(bars.Count - 1, start + maxBars);
        for (int i = start; i <= last; i++)
        {
            var b = bars[i];
            bool stopHit = isLong ? b.Low <= stop : b.High >= stop;
            // the candle in which the order filled: what price did after the fill is unknown, so it can only cost (stop), never pay (target)
            bool targetHit = !(firstCandleStopOnly && i == start) && (isLong ? b.High >= target : b.Low <= target);
            if (stopHit) return (double)((isLong ? stop - entry : entry - stop) / tick) - slipTicks;
            if (targetHit) return (double)((isLong ? target - entry : entry - target) / tick);
        }

        var close = bars[last].Close;
        return (double)((isLong ? close - entry : entry - close) / tick) - slipTicks;
    }
}

/// <summary>Judges a set of rule trades: split by time into a part to find things on and a part kept back, with a stricter bar for how many variants were tried.</summary>
public sealed record RuleVerdict(string Name, int Trades, double MeanNet, double T, double FoundMean, double FoundT, double KeptMean, double KeptT, double ReversedMean, bool Passes);

public static class RuleJudge
{
    /// <param name="variantsTried">How many variants were tested in all: the significance bar is divided by this.</param>
    public static RuleVerdict Judge(string name, IReadOnlyList<RuleTrade> trades, int variantsTried, double alpha = 0.05)
    {
        var ordered = trades.OrderBy(t => t.Entry).ToList();
        // trades on the same day share one price path (a rule that trades every level fires dozens of overlapping trades): they are not independent,
        // so the statistics use one number per day, the day's total result, and the split is by days
        var days = ordered.GroupBy(t => t.Day).OrderBy(g => g.Key).Select(g => g.Sum(x => x.NetTicks)).ToList();
        var (_, t) = EdgeFinder.Stats(days);
        double mean = ordered.Count == 0 ? 0 : ordered.Average(x => x.NetTicks);
        int cut = (int)Math.Round(days.Count * 0.6);
        var (mf, tf) = EdgeFinder.Stats(days.Take(cut).ToList());
        var (mk, tk) = EdgeFinder.Stats(days.Skip(cut).ToList());
        double reversed = ordered.Count == 0 ? 0 : ordered.Average(x => -(x.NetTicks + x.CostTicks) - x.CostTicks);
        bool passes = cut >= 30 && days.Count - cut >= 20 && mf > 0 && mk > 0 && EdgeFinder.TwoSidedP(tf) < alpha / Math.Max(1, variantsTried) && tk >= 1.0;
        // the means shown are per trade; the t statistics are from the daily totals
        int firstTrades = ordered.Count(x => x.Day <= ordered.GroupBy(y => y.Day).OrderBy(g => g.Key).ElementAtOrDefault(Math.Max(0, cut - 1))?.Key);
        double perTradeFound = firstTrades == 0 ? 0 : ordered.Take(firstTrades).Average(x => x.NetTicks);
        double perTradeKept = ordered.Count - firstTrades == 0 ? 0 : ordered.Skip(firstTrades).Average(x => x.NetTicks);
        return new RuleVerdict(name, ordered.Count, mean, t, perTradeFound, tf, perTradeKept, tk, reversed, passes);
    }
}

/// <summary>Plain-language verdict card for a list of trades from any backtest.</summary>
public static class StrategyVerdict
{
    public sealed record Result(string Light, string Text);

    /// <param name="trades">Per trade: gross profit before costs (<c>Pnl</c>), the costs paid (<c>Cost</c>), and when it was entered.</param>
    public static Result Build(IReadOnlyList<(DateTime Time, double Pnl, double Cost)> trades)
    {
        var sb = new StringBuilder();
        if (trades.Count < 30)
            return new Result("grey", $"Only {trades.Count} trades: too few to say anything (30 is the least; 100 or more is better).");
        var ordered = trades.OrderBy(t => t.Time).ToList();
        var net = ordered.Select(t => t.Pnl - t.Cost).ToList();
        var gross = ordered.Select(t => t.Pnl).ToList();
        var (mean, t) = EdgeFinder.Stats(net);
        var (gm, gt) = EdgeFinder.Stats(gross);
        double reversed = ordered.Average(x => -x.Pnl - x.Cost);
        double doubled = ordered.Average(x => x.Pnl - 2 * x.Cost);
        int half = ordered.Count / 2;
        double first = net.Take(half).Average(), second = net.Skip(half).Average();
        var risk = RiskSim.Bootstrap(net, Math.Min(100, ordered.Count));

        bool edgeReal = gt >= 2;                  // the direction of the trades matters, before costs
        bool pays = mean > 0 && t >= 2;           // and it survives costs
        bool stable = first > 0 && second > 0;
        bool costRobust = doubled > 0;
        sb.AppendLine($"Trades {ordered.Count} · average after costs {mean:+0.00;-0.00;0.00} per trade (t {t:0.0}) · average before costs {gm:+0.00;-0.00;0.00} (t {gt:0.0})");
        sb.AppendLine($"  Direction placebo: the same entries traded the other way would have averaged {reversed:+0.00;-0.00;0.00} per trade. A real edge shows a big gap between the two; luck shows little.");
        sb.AppendLine($"  Cost stress: with costs doubled the average is {doubled:+0.00;-0.00;0.00} per trade{(costRobust ? "" : "  <-- the edge does not survive higher costs")}.");
        sb.AppendLine($"  Stability: first half {first:+0.00;-0.00;0.00}, second half {second:+0.00;-0.00;0.00}{(stable ? "" : "  <-- one half loses")}.");
        sb.AppendLine($"  Risk (100 trades drawn from these results): typical worst fall {risk.DrawdownMedian:0.0}, bad case {risk.Drawdown95:0.0}; {100 * risk.ProbabilityOfLoss:0}% of runs end below zero.");
        string light = pays && edgeReal && stable && costRobust ? "green" : (mean <= 0 || gt < 1) ? "red" : "amber";
        sb.AppendLine();
        sb.AppendLine(light switch
        {
            "green" => "GREEN: positive after costs, the direction of the trades matters, both halves pay, and it survives doubled costs. Still check it on data it has not seen.",
            "red" => "RED: no sign that the direction of the trades beats chance, or it loses after costs.",
            _ => "AMBER: some good signs, not all. Look at which test failed above before trusting it.",
        });
        return new Result(light, sb.ToString());
    }
}
