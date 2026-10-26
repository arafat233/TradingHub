using TradingHub.Data;
using TradingHub.Trading;

namespace TradingHub.Tests;

/// <summary>What fills cost: slippage against the price a second before, signed so that positive means you paid up, and commissions.</summary>
public class FillQualityTests
{
    private static readonly DateTime T = new(2026, 10, 26, 14, 0, 0, DateTimeKind.Utc);

    private static AccountFill Fill(string id, int minute, string symbol, bool buy, decimal price, decimal qty = 1, decimal commission = 0, decimal pointValue = 1) =>
        new(id, T.AddMinutes(minute), symbol, buy, qty, price, pointValue, commission);

    [Fact]
    public void Buying_above_and_selling_below_the_arrival_price_both_count_as_cost()
    {
        var fills = new[]
        {
            Fill("1", 0, "ES.F", buy: true, price: 100.10m),    // arrival 100.00: bought 10 bp above = +10
            Fill("2", 1, "ES.F", buy: false, price: 99.95m),    // arrival 100.00: sold 5 bp below = +5
            Fill("3", 2, "ES.F", buy: true, price: 99.90m),     // bought 10 bp BELOW the arrival price: price improvement = -10
        };
        var r = FillQuality.Analyze(fills, _ => 100.00m);
        Assert.Equal(3, r.Measured);
        Assert.Equal(1.6667m, r.SlippageBp, 3);                  // (10 + 5 - 10) / 3
        Assert.Equal(new[] { 2, 1 }, r.BySide.Select(g => g.Fills));
        Assert.Equal(0m, r.BySide[0].SlippageBp, 3);              // buys: (+10 and -10) average 0
        Assert.Equal(5m, r.BySide[1].SlippageBp, 3);              // sells: +5
    }

    [Fact]
    public void Commissions_are_added_and_the_round_trip_is_twice_one_fill()
    {
        // one contract worth 100 x 50 = 5000, commission 1.00 = 2 bp; bought 1 bp above arrival
        var r = FillQuality.Analyze(new[] { Fill("1", 0, "ES.F", true, 100.01m, 1, commission: 1.0m, pointValue: 50) }, _ => 100.00m);
        Assert.Equal(1m, r.SlippageBp, 3);
        Assert.Equal(1.9998m, r.CommissionBp, 3);                // 1.0 / (100.01 x 50) x 10 000
        Assert.Equal(r.SlippageBp + r.CommissionBp, r.OneWayBp, 6);
        Assert.Equal(2 * r.OneWayBp, r.RoundTripBp, 6);
        Assert.Equal(0.01m * 1 * 50, r.SlippageMoney, 6);
        Assert.Equal(1.0m, r.CommissionMoney);
    }

    [Fact]
    public void Fills_without_a_tape_are_counted_but_not_measured_and_groups_split_by_hour_and_instrument()
    {
        var fills = new[]
        {
            Fill("1", 0, "ES.F", true, 100.01m),
            Fill("2", 70, "NQ.F", false, 99.99m),               // 15:10 UTC
            Fill("3", 5, "ES.F", true, 100.00m),                // no tape for this one
        };
        var r = FillQuality.Analyze(fills, f => f.Id == "3" ? null : 100.00m);
        Assert.Equal(3, r.Fills);
        Assert.Equal(2, r.Measured);
        Assert.Equal(new[] { "14:00", "15:00" }, r.ByHour.Select(g => g.Label));
        Assert.Equal(new[] { "ES.F", "NQ.F" }, r.BySymbol.Select(g => g.Label));
        Assert.Contains("2 with a price", r.Text());
        Assert.Contains("Nothing to measure", FillQuality.Analyze(fills, _ => null).Text());
    }
}
