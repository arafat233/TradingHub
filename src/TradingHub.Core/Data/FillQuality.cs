using System.Globalization;
using System.Text;
using TradingHub.Trading;

namespace TradingHub.Data;

/// <summary>One line of the report: a group of fills and what they cost.</summary>
public sealed record FillGroup(string Label, int Fills, decimal SlippageBp, decimal CommissionBp);

public sealed record FillQualityReport(
    int Fills, int Measured, decimal SlippageBp, decimal CommissionBp, decimal OneWayBp, decimal SlippageMoney, decimal CommissionMoney,
    IReadOnlyList<FillGroup> ByHour, IReadOnlyList<FillGroup> BySymbol, IReadOnlyList<FillGroup> BySide)
{
    /// <summary>What a round trip (in and out) costs you, in basis points: twice what one fill costs.</summary>
    public decimal RoundTripBp => OneWayBp * 2;

    public string Text()
    {
        var inv = CultureInfo.InvariantCulture;
        static string S(decimal v) => v.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine($"{Fills} fills, {Measured} with a price on the recorded tape to compare against.");
        if (Measured == 0)
        {
            sb.AppendLine("Nothing to measure: the fills have no recorded ticks around them (record or download the instrument's ticks for those days).");
            return sb.ToString();
        }

        sb.AppendLine($"Slippage: {S(SlippageBp)} bp per fill on average (positive = you paid more than the price a second before). Commissions: {CommissionBp.ToString("0.00", inv)} bp per fill.");
        sb.AppendLine($"So one fill costs about {OneWayBp.ToString("0.00", inv)} bp and a round trip about {RoundTripBp.ToString("0.0", inv)} bp. That is the number to put in the Insights panel's costs box. In money: slippage {SlippageMoney.ToString("0.00", inv)}, commissions {CommissionMoney.ToString("0.00", inv)} over these fills.");
        sb.AppendLine("By hour (UTC):");
        foreach (var g in ByHour) sb.AppendLine($"  {g.Label}: {g.Fills,4} fills, slippage {S(g.SlippageBp)} bp");
        sb.AppendLine("By instrument:");
        foreach (var g in BySymbol) sb.AppendLine($"  {g.Label,-10} {g.Fills,4} fills, slippage {S(g.SlippageBp)} bp, commission {g.CommissionBp.ToString("0.00", inv)} bp");
        sb.AppendLine("By side:");
        foreach (var g in BySide) sb.AppendLine($"  {g.Label,-5} {g.Fills,4} fills, slippage {S(g.SlippageBp)} bp");
        sb.AppendLine("A simulated account fills at the tape's price, so its slippage is about zero by construction: this report is for real accounts. A few dozen fills say little per hour.");
        return sb.ToString();
    }
}

/// <summary>
/// What your fills really cost: each fill is compared with the price of the last trade a second before it (the price you could have seen when
/// you sent it), and the commission is added. Slippage is signed so that positive means you paid up (bought higher or sold lower). It says nothing
/// about orders that did not fill, so it flatters limit orders that waited and missed.
/// </summary>
public static class FillQuality
{
    /// <param name="arrival">The price just before a fill (null when the tape does not cover it).</param>
    public static FillQualityReport Analyze(IReadOnlyList<AccountFill> fills, Func<AccountFill, decimal?> arrival)
    {
        var rows = new List<(AccountFill F, decimal SlipBp, decimal CommBp, decimal SlipMoney)>();
        foreach (var f in fills)
        {
            if (arrival(f) is not { } a || a <= 0 || f.Price <= 0 || f.Quantity <= 0) continue;
            decimal sign = f.Buy ? 1 : -1, move = sign * (f.Price - a);
            decimal notional = f.Price * f.Quantity * f.PointValue;
            rows.Add((f, move / a * 10_000m, notional > 0 ? f.Commission / notional * 10_000m : 0, move * f.Quantity * f.PointValue));
        }

        FillGroup Group(string label, IEnumerable<(AccountFill F, decimal SlipBp, decimal CommBp, decimal SlipMoney)> r)
        {
            var l = r.ToList();
            return new FillGroup(label, l.Count, l.Count == 0 ? 0 : l.Average(x => x.SlipBp), l.Count == 0 ? 0 : l.Average(x => x.CommBp));
        }

        decimal slip = rows.Count == 0 ? 0 : rows.Average(r => r.SlipBp), comm = rows.Count == 0 ? 0 : rows.Average(r => r.CommBp);
        return new FillQualityReport(fills.Count, rows.Count, slip, comm, slip + comm, rows.Sum(r => r.SlipMoney), rows.Sum(r => r.F.Commission),
            rows.GroupBy(r => r.F.Time.Hour).OrderBy(g => g.Key).Select(g => Group($"{g.Key:00}:00", g)).ToList(),
            rows.GroupBy(r => r.F.Symbol).OrderByDescending(g => g.Count()).Select(g => Group(g.Key, g)).ToList(),
            new[] { Group("buys", rows.Where(r => r.F.Buy)), Group("sells", rows.Where(r => !r.F.Buy)) });
    }
}
