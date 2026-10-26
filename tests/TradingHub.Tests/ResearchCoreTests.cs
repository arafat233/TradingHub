using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public class ResearchCoreTests
{
    private static readonly DateTime T0 = new(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Tick_rule_audit_is_perfect_when_sides_follow_price_and_a_coin_flip_when_they_do_not()
    {
        var rng = new Random(2);
        var follow = new List<Trade>();
        var random = new List<Trade>();
        decimal price = 100;
        for (int i = 0; i < 4000; i++)
        {
            bool up = rng.Next(2) == 0;
            price += up ? 1 : -1;
            follow.Add(new Trade(i + 1, T0.AddMilliseconds(i * 10), price, 1 + rng.Next(3), up ? TradeSide.Buy : TradeSide.Sell));
            random.Add(new Trade(i + 1, T0.AddMilliseconds(i * 10), price, 1 + rng.Next(3), rng.Next(2) == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        Assert.True(TickRuleAudit.Evaluate(follow).Accuracy > 0.99);
        var coin = TickRuleAudit.Evaluate(random);
        Assert.InRange(coin.Accuracy, 0.45, 0.55);
    }

    [Fact]
    public void Risk_simulation_shows_deeper_falls_for_noisier_results_and_decay_notices_a_worse_recent_stretch()
    {
        var rng = new Random(9);
        var steady = Enumerable.Range(0, 200).Select(_ => 1.0 + (rng.NextDouble() - 0.5)).ToList();
        var wild = Enumerable.Range(0, 200).Select(_ => 1.0 + (rng.NextDouble() - 0.5) * 40).ToList();
        var a = RiskSim.Bootstrap(steady, 100, 2000);
        var b = RiskSim.Bootstrap(wild, 100, 2000);
        Assert.True(b.Drawdown95 > a.Drawdown95 * 5);
        Assert.True(a.ProbabilityOfLoss < 0.01);

        // 150 trades that paid about +2 each, then 50 that paid about -2
        var results = Enumerable.Range(0, 150).Select(_ => 2 + (rng.NextDouble() - 0.5) * 4).Concat(Enumerable.Range(0, 50).Select(_ => -2 + (rng.NextDouble() - 0.5) * 4)).ToList();
        var (recent, prior, t) = RiskSim.Decay(results, 50);
        Assert.True(recent < 0 && prior > 0 && t < -5, $"recent {recent} prior {prior} t {t}");
    }

    [Fact]
    public void Event_impact_measures_the_move_after_an_event_and_the_usual_move()
    {
        var times = new List<long>();
        var prices = new List<long>();
        long price = 1000;
        for (int m = 0; m < 600; m++)
        {
            // a quiet market, except a 40-tick jump over the 10 minutes after 14:30
            var t = T0.AddMinutes(m - 120).AddSeconds(30);
            if (t >= new DateTime(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc) && t < new DateTime(2026, 3, 2, 14, 40, 0, DateTimeKind.Utc)) price += 4;
            else price += (m % 2 == 0) ? 1 : -1;
            times.Add(t.Ticks);
            prices.Add(price);
        }

        var ev = EventImpact.Measure(times.ToArray(), prices.ToArray(), new DateTime(2026, 3, 2, 14, 30, 0, DateTimeKind.Utc), new[] { 5, 30 });
        Assert.NotNull(ev);
        Assert.InRange(ev![1], 35, 45);
        Assert.True(EventImpact.TypicalAbsMove(times.ToArray(), prices.ToArray(), 30) < 20);
    }
}
