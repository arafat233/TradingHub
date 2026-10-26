using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public class ResearchTests
{
    private static readonly DateTime T0 = new(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Edge_finder_passes_a_real_effect_and_nothing_in_pure_noise()
    {
        var rng = new Random(11);
        var real = new List<LevelRecord>();
        var noise = new List<LevelRecord>();
        for (int i = 0; i < 1200; i++)
        {
            int hour = rng.Next(0, 24);
            // fading pays +6 ticks on average between 13:00 and 17:00 UTC and nothing elsewhere; everything has noise of about 25 ticks
            double fade = (hour >= 12 && hour < 16 ? 6 : 0) + (rng.NextDouble() - 0.5) * 50;
            var make = new Func<double, LevelRecord>(f => new LevelRecord(T0.AddMinutes(i * 20), i % 2 == 0, rng.NextDouble(), hour, rng.NextDouble(), rng.NextDouble() * 10, rng.NextDouble() * 100, rng.Next(2) == 0, f, -f));
            real.Add(make(fade));
            noise.Add(make((rng.NextDouble() - 0.5) * 50));
        }

        var found = EdgeFinder.Evaluate(real, r => r.FadeTicks);
        Assert.Contains(found, s => s.Passes && s.Name.StartsWith("12:00-16:00"));
        Assert.DoesNotContain(EdgeFinder.Evaluate(noise, r => r.FadeTicks), s => s.Passes);
    }

    [Fact]
    public void Tick_sanity_drops_a_one_tick_spike_and_reports_a_long_gap()
    {
        var trades = new List<Trade>();
        for (int i = 0; i < 100; i++) trades.Add(new Trade(i + 1, T0.AddSeconds(i), 1000m + i % 3, 1, TradeSide.Buy));
        trades[50] = new Trade(51, trades[50].Time, 1500m, 1, TradeSide.Buy);                                  // a bad print
        trades.Add(new Trade(101, T0.AddMinutes(60), 1001m, 1, TradeSide.Buy));                                // an hour of silence
        var (clean, problems) = TickSanity.Scan(trades, 1m);
        Assert.Equal(trades.Count - 1, clean.Count);
        Assert.DoesNotContain(clean, t => t.Price == 1500m);
        Assert.Contains(problems, p => p.Kind == "spike");
        Assert.Contains(problems, p => p.Kind == "gap");
    }

    [Fact]
    public void Regime_tells_a_straight_line_from_a_zigzag_and_cross_market_finds_the_follower()
    {
        long[] straight = Enumerable.Range(0, 60).Select(i => (long)i).ToArray(), zigzag = Enumerable.Range(0, 60).Select(i => (long)(i % 2)).ToArray();
        var line = straight.Select((p, i) => new Candle { Time = T0.AddMinutes(i), CloseTicks = p }).ToList();
        var zig = zigzag.Select((p, i) => new Candle { Time = T0.AddMinutes(i), CloseTicks = p }).ToList();
        Assert.Equal(RegimeKind.Trend, Regime.Classify(line, TimeSpan.FromHours(1)).Kind);
        Assert.Equal(RegimeKind.Range, Regime.Classify(zig, TimeSpan.FromHours(1)).Kind);

        // market B rises 2 bps in the 30 s after every buy in A and falls after every sell
        var rng = new Random(5);
        var orders = new List<(DateTime, bool)>();
        var times = new List<long>();
        var prices = new List<double>();
        double price = 1000;
        for (int i = 0; i < 300; i++)
        {
            var t = T0.AddMinutes(i * 2);
            bool buy = rng.Next(2) == 0;
            orders.Add((t, buy));
            times.Add(t.Ticks); prices.Add(price);
            double noise = (rng.NextDouble() - 0.5) * 0.0001; // plus or minus half a basis point
            price *= buy ? 1.0002 + noise : 0.9998 - noise;
            times.Add(t.AddSeconds(20).Ticks); prices.Add(price);
        }

        var lead = CrossMarket.Study(orders, times.ToArray(), prices.ToArray(), new[] { 30 });
        Assert.True(lead[0].MeanBps > 1.5 && lead[0].T > 10, $"mean {lead[0].MeanBps} t {lead[0].T}");
    }
}
