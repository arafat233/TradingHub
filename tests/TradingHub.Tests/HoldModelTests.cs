using TradingHub.Data;

namespace TradingHub.Tests;

public class HoldModelTests
{
    [Fact]
    public void Learns_that_bigger_orders_hold_when_that_is_what_the_data_says_and_refuses_to_learn_from_one_result()
    {
        var rng = new Random(7);
        var levels = new List<(LevelSample, bool)>();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 400; i++)
        {
            decimal volume = 20 + rng.Next(0, 200);
            // the larger the order, the more often it held (about 15% at the smallest, 90% at the largest)
            bool held = rng.NextDouble() < 0.15 + 0.75 * (double)(volume - 20) / 200;
            levels.Add((new LevelSample(t0.AddMinutes(i * 17), 4000m + rng.Next(0, 400) * 0.1m, volume, i % 2 == 0), held));
        }

        var all = levels.Select(l => l.Item1).ToList();
        var model = HoldModel.Train(levels, all, 0.1m);
        Assert.NotNull(model);
        Assert.True(model!.TestedOn >= 90);
        Assert.True(model.Accuracy > 0.6, $"accuracy {model.Accuracy}");
        var small = new LevelSample(t0.AddDays(30), 4020m, 22m, true);
        var big = new LevelSample(t0.AddDays(30), 4020m, 210m, true);
        Assert.True(model.Predict(big) > model.Predict(small) + 0.3);

        // everything held: there is nothing to learn the difference from
        Assert.Null(HoldModel.Train(levels.Select(l => (l.Item1, true)).ToList(), all, 0.1m));
        // too few results
        Assert.Null(HoldModel.Train(levels.Take(10).ToList(), all, 0.1m));
    }
}
