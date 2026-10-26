using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>TrackBigTrades remembers big market orders (fills with the same time and side add up) and marks a level used up when price returns.</summary>
public class BigTradeLevelTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
    private static readonly DateTime T0 = new(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Probe : Strategy
    {
        public IReadOnlyList<BigTradeLevel> Seen = Array.Empty<BigTradeLevel>();
        protected override void OnInit() => TrackBigTrades = 50;
        protected override void OnBar(Bar bar) => Seen = BigTradeLevels;
    }

    [Fact]
    public void A_big_order_is_remembered_and_used_up_when_price_comes_back()
    {
        var trades = new List<Trade>();
        long id = 1;
        for (int s = 0; s < 600; s += 5) trades.Add(new Trade(id++, T0.AddSeconds(s), 1010m, 1, TradeSide.Sell));
        // fills of 40 at 1000 and 20 at 1001 at the same instant = one 60-lot buy; a lone 20-lot is too small
        trades.Add(new Trade(id++, T0.AddSeconds(101), 1000m, 40, TradeSide.Buy));
        trades.Add(new Trade(id++, T0.AddSeconds(101), 1001m, 20, TradeSide.Buy));
        trades.Add(new Trade(id++, T0.AddSeconds(201), 1020m, 20, TradeSide.Buy));
        trades.Sort((a, b) => a.Time.CompareTo(b.Time));
        for (int i = 0; i < trades.Count; i++) trades[i] = new Trade(i + 1, trades[i].Time, trades[i].Price, trades[i].Volume, trades[i].Side);
        var s1 = new Probe();
        new BacktestEngine(s1, TickData.FromTrades(Inst, trades), Settings()).Run();
        var l = Assert.Single(s1.Seen);
        Assert.True(l.Buy);
        Assert.Equal(60m, l.Volume);
        Assert.Equal(1000m, l.Price); // average 1000.33, rounded to the tick grid
        Assert.False(l.Touched);

        trades.Add(new Trade(trades.Count + 1, T0.AddSeconds(301), l.Price, 1, TradeSide.Sell));
        trades.Sort((a, b) => a.Time.CompareTo(b.Time));
        for (int i = 0; i < trades.Count; i++) trades[i] = new Trade(i + 1, trades[i].Time, trades[i].Price, trades[i].Volume, trades[i].Side);
        var s2 = new Probe();
        new BacktestEngine(s2, TickData.FromTrades(Inst, trades), Settings()).Run();
        Assert.True(Assert.Single(s2.Seen).Touched);
    }

    /// <summary>The "Big-trade level" strategy template exactly as it ships: it must compile and trade the first return to a level.</summary>
    private sealed class TemplateStrategy : Strategy
    {
        public int MinSize { get; set; } = 50;
        public bool Fade { get; set; } = true;
        private readonly HashSet<DateTime> _used = new();

        public TemplateStrategy()
        {
            StopLossTicks = 40;
            TakeProfitTicks = 80;
        }

        protected override void OnInit() => TrackBigTrades = MinSize;

        protected override void OnBar(Bar bar)
        {
            if (!IsFlat) return;
            foreach (var level in BigTradeLevels)
            {
                if (!level.Touched || !_used.Add(level.Time)) continue;
                bool goLong = Fade ? level.Buy : !level.Buy;
                if (goLong) EnterLong();
                else EnterShort();
                return;
            }
        }
    }

    [Fact]
    public void The_template_strategy_enters_long_when_price_returns_to_a_buy_level()
    {
        var trades = new List<Trade>();
        long id = 1;
        for (int s = 0; s < 660; s += 5) trades.Add(new Trade(id++, T0.AddSeconds(s), 1010m, 1, TradeSide.Sell));
        trades.Add(new Trade(id++, T0.AddSeconds(101), 1000m, 60, TradeSide.Buy));
        trades.Add(new Trade(id++, T0.AddSeconds(301), 1000m, 1, TradeSide.Sell)); // price comes back to the level
        trades.Sort((a, b) => a.Time.CompareTo(b.Time));
        for (int i = 0; i < trades.Count; i++) trades[i] = new Trade(i + 1, trades[i].Time, trades[i].Price, trades[i].Volume, trades[i].Side);
        var result = new BacktestEngine(new TemplateStrategy(), TickData.FromTrades(Inst, trades), Settings()).Run();
        Assert.NotEmpty(result.Trades);
    }

    private static BacktestSettings Settings() => new()
    {
        FeedId = "test", Symbol = "TEST", FromUtc = T0, ToUtc = T0.AddMinutes(11), Period = "M1", CommissionPercent = 0, SlippageTicks = 0,
    };
}
