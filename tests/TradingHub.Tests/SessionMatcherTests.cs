using TradingHub.Data;

namespace TradingHub.Tests;

public class SessionMatcherTests
{
    [Fact]
    public void Ranks_the_closest_past_session_first_and_never_uses_the_future()
    {
        var f = new List<double[]>
        {
            new[] { 4.0, 0.0 },    // 0
            new[] { 50.0, 1.0 },   // 1
            new[] { 11.5, 0.0 },   // 2: very close to the target
            new[] { 12.0, 0.0 },   // 3 = target
            new[] { 12.0, 0.0 },   // 4: identical, but in the future
        };
        var m = SessionMatcher.Nearest(f, 3, 2);
        Assert.Equal(2, m[0].Index);
        Assert.DoesNotContain(m, x => x.Index >= 3);
        Assert.Equal(2, m.Count);
    }

    [Fact]
    public void A_zero_weight_ignores_that_feature_and_a_constant_one_adds_nothing()
    {
        var f = new List<double[]>
        {
            new[] { 1.0, 100.0, 5.0 },
            new[] { 9.0, 100.0, 5.0 },
            new[] { 1.1, 200.0, 5.0 },
            new[] { 1.0, 100.0, 5.0 },
        };
        var m = SessionMatcher.Nearest(f, 3, 3, new[] { 1.0, 0.0, 1.0 });
        Assert.Equal(0, m[0].Index);
        Assert.Equal(0.0, m[0].Distance, 6);
        Assert.Equal(2, m[1].Index);
    }
}
