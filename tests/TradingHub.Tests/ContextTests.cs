using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>Market context, the strategies' context filter and their chart drawings.</summary>
public class ContextTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>One trade per minute: a day and a half rising straight up and then a day and a half of back-and-forth.</summary>
    private static TickData TrendThenRange()
    {
        var list = new List<Trade>();
        decimal price = 1000;
        for (int i = 0; i < 72 * 60; i++)
        {
            if (i < 36 * 60) price += 1;
            else price = 3160 + (i / 60 % 2 == 0 ? 40 : -40) + i % 7;
            list.Add(new Trade(i + 1, T0.AddMinutes(i), price, 1, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        return TickData.FromTrades(Inst, list);
    }

    private static BacktestSettings Settings(int days = 4) => new()
    {
        FeedId = "test", Symbol = "TEST", FromUtc = T0, ToUtc = T0.AddDays(days), Period = "H1", CommissionPercent = 0, SlippageTicks = 0, InitialCapital = 100_000,
    };

    /// <summary>Enters long every time it is flat and exits two candles later.</summary>
    private sealed class AlwaysIn : Strategy
    {
        private int _entry = -10;

        protected override void OnBar(Bar bar)
        {
            if (IsFlat && ActiveOrders.Count == 0)
            {
                Buy(1);
                _entry = CurrentBar;
            }
            else if (IsLong && CurrentBar - _entry >= 2) ClosePosition();
        }
    }

    [Fact]
    public void The_context_filter_only_allows_entering_in_that_regime()
    {
        var free = new BacktestEngine(new AlwaysIn(), TrendThenRange(), Settings()).Run();
        Assert.Contains(free.Trades, t => t.EntryTrend >= Robustness.TrendingTrend);

        var onlyRange = new AlwaysIn { RegimeFilter = RegimeFilter.RangeOnly };
        var r = new BacktestEngine(onlyRange, TrendThenRange(), Settings()).Run();
        var judged = r.Trades.Where(t => t.EntryTrend != null).ToList();
        Assert.NotEmpty(judged);
        Assert.All(judged, t => Assert.True(t.EntryTrend <= Robustness.RangeTrend, $"entered with trend {t.EntryTrend}"));
        Assert.True(r.Trades.Count < free.Trades.Count);
        // exits always go through: nothing is left open that should not be
        Assert.True(r.OpenPosition == null || r.OpenPosition.Quantity == 1);
    }

    [Fact]
    public void Each_trade_stores_the_context_and_the_volatility_percentile()
    {
        var r = new BacktestEngine(new AlwaysIn(), TrendThenRange(), Settings()).Run();
        var late = r.Trades.Where(t => t.EntryTime > T0.AddDays(2)).ToList();
        Assert.NotEmpty(late);
        Assert.All(late, t =>
        {
            Assert.NotNull(t.EntryTrend);
            Assert.NotNull(t.EntryVolatility);
            Assert.NotNull(t.EntryVolatilityRank);
            Assert.InRange(t.EntryVolatilityRank!.Value, 0, 100);
        });
    }

    /// <summary>Draws each day's range, a line candle by candle and an arrow on each entry.</summary>
    private sealed class Drawer : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            var day = bar.Time.Date;
            DrawBox($"range-{day:yyyyMMdd}", day, bar.High + 5, bar.Time, bar.Low - 5, "green", 20, "range");
            Plot("Close", bar.Close, "#FF8800");
            if (CurrentBar % 10 == 0) DrawArrowUp($"arrow-{CurrentBar}", bar.Time, bar.Low, "blue", "entry");
        }
    }

    [Fact]
    public void The_strategies_draw_ranges_lines_and_arrows()
    {
        var s = new Drawer();
        var r = new BacktestEngine(s, TrendThenRange(), Settings()).Run();
        var drawings = s.Canvas.Drawings();
        var boxes = drawings.Where(d => d.Kind == DrawingKind.Box).ToList();
        Assert.Equal(r.Bars > 0 ? drawings.Count(d => d.Id.StartsWith("range-", StringComparison.Ordinal)) : 0, boxes.Count);
        Assert.InRange(boxes.Count, 3, 4); // one range per day: the same id is stretched, not repeated
        Assert.Contains(drawings, d => d.Kind == DrawingKind.Marker && d.Shape == MarkerShape.ArrowUp && d.Text == "entry");
        var plot = Assert.Single(s.Canvas.Plots());
        Assert.Equal("Close", plot.Name);
        Assert.Equal(r.Bars, plot.Points.Count);
    }

    [Fact]
    public void The_canvas_has_a_cap_and_the_oldest_ones_are_dropped()
    {
        var c = new StrategyCanvas();
        for (int i = 0; i < StrategyCanvas.MaxDrawings + 50; i++) c.Set(new StrategyDrawing { Id = $"d{i}", Kind = DrawingKind.Text, Text = "x" });
        var list = c.Drawings();
        Assert.Equal(StrategyCanvas.MaxDrawings, list.Count);
        Assert.Equal("d50", list[0].Id);
    }

    [Fact]
    public void If_it_only_wins_in_a_range_with_low_volatility_it_suggests_that_filter()
    {
        var trades = Enumerable.Range(0, 90).Select(i =>
        {
            bool range = i % 3 != 0, calm = i % 2 == 0;
            decimal pnl = range && calm ? (i % 5 == 0 ? -60 : 150) : range ? (i % 4 == 0 ? 90 : -70) : -120;
            return new ClosedTrade(i + 1, true, T0.AddHours(i), 100, T0.AddHours(i + 0.5), 100 + pnl, 1, pnl, 0, 0, 0, "", "", 1)
            {
                EntryTrend = range ? 0.08 : 0.5,
                EntryVolatility = calm ? 0.2 : 0.8,
                EntryVolatilityRank = calm ? 20 : 80,
            };
        }).ToList();
        var f = Robustness.SuggestContextFilter(trades);
        Assert.NotNull(f);
        Assert.True(f!.Regime is RegimeFilter.RangeOnly or RegimeFilter.NoTrend, $"regime {f.Regime}");
        Assert.True(f.MaxVolatilityRank <= 66, $"volatility up to {f.MaxVolatilityRank}");
        Assert.True(f.ProfitFactor >= 1.5m && f.DroppedNet < 0 && f.Trades >= 20);
        Assert.Contains("range", f.Description);

        // if it wins evenly in every context, there is nothing to filter
        var even = trades.Select((t, i) => t with { Pnl = i % 3 == 0 ? -50 : 100 }).ToList();
        Assert.Null(Robustness.SuggestContextFilter(even));
    }
    /// <summary>Hourly closes: a day and a half rising, one and a half falling and one and a half of back-and-forth.</summary>
    private static TickData UpDownRange()
    {
        var list = new List<Trade>();
        decimal price = 2000;
        for (int i = 0; i < 108 * 60; i++)
        {
            if (i < 36 * 60) price += 1;
            else if (i < 72 * 60) price -= 1;
            else price = 2000 + (i / 60 % 2 == 0 ? 40 : -40) + i % 7;
            list.Add(new Trade(i + 1, T0.AddMinutes(i), price, 1, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        return TickData.FromTrades(Inst, list);
    }

    [Fact]
    public void The_context_says_whether_the_trend_is_up_or_down()
    {
        var up = new MarketContextTracker();
        var down = new MarketContextTracker();
        for (int h = 0; h < 30; h++)
        {
            up.Add(T0.AddHours(h), 100 + h);
            down.Add(T0.AddHours(h), 200 - h);
        }

        Assert.Equal(MarketRegime.Trend, up.Regime);
        Assert.Equal(1, up.Direction);
        Assert.True(up.Change > 0);
        Assert.Equal("Up trend", up.RegimeLabel);
        Assert.Equal(-1, down.Direction);
        Assert.Equal("Down trend", down.RegimeLabel);
        Assert.StartsWith("down trend", down.Describe());
        Assert.True(up.Allows(RegimeFilter.UpTrendOnly, 0, 100) && !up.Allows(RegimeFilter.DownTrendOnly, 0, 100));
        Assert.True(down.Allows(RegimeFilter.DownTrendOnly, 0, 100) && !down.Allows(RegimeFilter.UpTrendOnly, 0, 100));
        Assert.False(down.Allows(RegimeFilter.RangeOnly, 0, 100));
        Assert.Equal("low", MarketContextTracker.VolatilityLabel(20));
        Assert.Equal("high", MarketContextTracker.VolatilityLabel(80));
    }

    [Fact]
    public void Up_trend_only_enters_only_when_it_had_been_rising()
    {
        var free = new BacktestEngine(new AlwaysIn(), UpDownRange(), Settings(5)).Run();
        Assert.Contains(free.Trades, t => t.EntryMarketDirection < 0 && t.EntryTrend >= Robustness.TrendingTrend);

        var r = new BacktestEngine(new AlwaysIn { RegimeFilter = RegimeFilter.UpTrendOnly }, UpDownRange(), Settings(5)).Run();
        var judged = r.Trades.Where(t => t.EntryTrend != null).ToList();
        Assert.NotEmpty(judged);
        Assert.All(judged, t =>
        {
            Assert.True(t.EntryTrend >= Robustness.TrendingTrend, $"entered with trend {t.EntryTrend}");
            Assert.Equal(1, t.EntryMarketDirection);
        });

        var down = new BacktestEngine(new AlwaysIn { RegimeFilter = RegimeFilter.DownTrendOnly }, UpDownRange(), Settings(5)).Run();
        Assert.All(down.Trades.Where(t => t.EntryTrend != null), t => Assert.Equal(-1, t.EntryMarketDirection));
    }

    [Fact]
    public void The_hourly_history_builds_the_same_context_as_the_candles()
    {
        // with candles shorter than an hour: adding the history is the same as having seen it hour by hour
        var hourly = Enumerable.Range(0, 40 * 24).Select(i => (T0.AddHours(i), 1000m + (decimal)Math.Sin(i / 5.0) * 30 + i * 0.2m)).ToList();
        var seeded = new MarketContextTracker();
        seeded.AddHistory(hourly, TimeSpan.FromMinutes(15));
        var direct = new MarketContextTracker();
        foreach (var (t, c) in hourly) direct.Add(t, c);
        Assert.Equal(direct.Trend, seeded.Trend);
        Assert.Equal(direct.VolatilityRank, seeded.VolatilityRank);

        // with 4-hour candles it is grouped: the last close of each 4-hour stretch
        var coarse = new MarketContextTracker(coarse: true);
        coarse.AddHistory(hourly, TimeSpan.FromHours(4));
        var fours = new MarketContextTracker(coarse: true);
        foreach (var g in hourly.GroupBy(h => h.Item1.Ticks / TimeSpan.FromHours(4).Ticks)) fours.Add(g.First().Item1, g.Last().Item2);
        Assert.Equal(fours.Trend, coarse.Trend);
        Assert.NotNull(coarse.Trend);
    }

    [Fact]
    public void If_it_only_wins_in_an_up_trend_it_suggests_that_filter_and_the_table_separates_it()
    {
        var trades = Enumerable.Range(0, 90).Select(i =>
        {
            int kind = i % 3; // 0 = up trend, 1 = down trend, 2 = range
            decimal pnl = kind == 0 ? (i % 5 == 0 ? -60 : 160) : kind == 1 ? (i % 4 == 0 ? 50 : -110) : (i % 4 == 0 ? 60 : -90);
            return new ClosedTrade(i + 1, true, T0.AddHours(i), 100, T0.AddHours(i + 0.5), 100 + pnl, 1, pnl, 0, 0, 0, "", "", 1)
            {
                EntryTrend = kind == 2 ? 0.08 : 0.5,
                EntryVolatility = 0.4,
                EntryMarketDirection = kind == 1 ? -1 : 1,
            };
        }).ToList();
        var f = Robustness.SuggestContextFilter(trades);
        Assert.NotNull(f);
        Assert.Equal(RegimeFilter.UpTrendOnly, f!.Regime);
        Assert.Contains("up trend", f.Description);

        var regimes = Robustness.Regimes(trades);
        var up = regimes.Single(r => r.Name == "up trend");
        Assert.True(up.Detail && up.Trades == 30 && up.Net > 0);
        Assert.True(regimes.Single(r => r.Name == "down trend").Net < 0);

        // what the filter keeps and what it drops on another market
        var (kept, keptNet, dropped, droppedNet) = Robustness.ApplyFilter(trades, RegimeFilter.UpTrendOnly, 0, 100);
        Assert.Equal(30, kept);
        Assert.Equal(60, dropped);
        Assert.True(keptNet > 0 && droppedNet < 0);
    }
    [Fact]
    public void Any_combination_of_conditions_can_be_chosen()
    {
        // the 15 combinations of range, mixed, up trend and down trend each have their filter, and they map back to the same
        for (int bits = 1; bits <= 15; bits++)
        {
            var set = (RegimeSet)bits;
            var f = MarketContextTracker.FilterOf(set);
            Assert.Equal(set, MarketContextTracker.SetOf(f));
        }

        Assert.Equal(RegimeFilter.All, MarketContextTracker.FilterOf(RegimeSet.All));
        Assert.True(MarketContextTracker.RegimeAllows(RegimeFilter.RangeOrUpTrend, MarketRegime.Range, 0));
        Assert.True(MarketContextTracker.RegimeAllows(RegimeFilter.RangeOrUpTrend, MarketRegime.Trend, 1));
        Assert.False(MarketContextTracker.RegimeAllows(RegimeFilter.RangeOrUpTrend, MarketRegime.Trend, -1));
        Assert.False(MarketContextTracker.RegimeAllows(RegimeFilter.RangeOrUpTrend, MarketRegime.Mixed, 1));
        Assert.True(MarketContextTracker.RegimeAllows(RegimeFilter.MixedOnly, MarketRegime.Unknown, null)); // with no data it does not block
        Assert.Equal("range or up trend", Robustness.DescribeRegimes(RegimeSet.Range | RegimeSet.UpTrend));
        Assert.Equal("everything but down trend", Robustness.DescribeRegimes(RegimeSet.Range | RegimeSet.Mixed | RegimeSet.UpTrend));
        Assert.Equal("mixed only", Robustness.DescribeRegimes(RegimeSet.Mixed));
        Assert.Equal("range only, low volatility", Robustness.DescribeFilter(RegimeFilter.RangeOnly, 0, 33));
    }

    [Fact]
    public void With_the_earlier_history_the_filter_applies_from_the_first_candle()
    {
        // it starts in the middle of a trend (a day and a half rising): with no history, in the first hours it does not know it is a trend and
        // "range only" lets it enter; with the previous month (also rising), it knows from the first candle
        var data = TrendThenRange();
        var free = new BacktestEngine(new AlwaysIn { RegimeFilter = RegimeFilter.RangeOnly }, data, Settings()).Run();
        Assert.Contains(free.Trades, t => t.EntryTime < T0.AddHours(8));

        var before = Enumerable.Range(1, 30 * 24).Select(h => (T0.AddHours(-h), 1000m - h * 0.5m)).Reverse().ToList();
        var seeded = new BacktestEngine(new AlwaysIn { RegimeFilter = RegimeFilter.RangeOnly }, data, Settings()) { ContextHistory = before }.Run();
        Assert.DoesNotContain(seeded.Trades, t => t.EntryTime < T0.AddHours(30));
        Assert.All(seeded.Trades, t => Assert.True(t.EntryTrend <= Robustness.RangeTrend, $"entered with trend {t.EntryTrend}"));
        // and each trade already carries its context (there is no "no data" at the start)
        var all = new BacktestEngine(new AlwaysIn(), data, Settings()) { ContextHistory = before }.Run();
        Assert.All(all.Trades, t => Assert.NotNull(t.EntryTrend));
    }

    [Fact]
    public void Each_walk_forward_window_starts_knowing_how_the_market_had_been()
    {
        var data = UpDownRange();
        var settings = Settings(5);
        var wf = Optimizer.WalkForward(() => new AlwaysIn(), new Dictionary<string, object?>(), new List<ParameterRange>(), data, settings,
            FitnessMetric.NetProfit, 1, 1, false, 0, null, CancellationToken.None);
        // the out-of-sample windows start on day 1, 2 and 3: with what came before, their first trade already knows whether there is
        // a range or a trend (before, each window started with no context)
        var firstOfDay = wf.OutSampleTrades.GroupBy(t => t.EntryTime.Date).Select(g => g.OrderBy(t => t.EntryTime).First()).ToList();
        Assert.True(firstOfDay.Count >= 2, $"windows with trades: {firstOfDay.Count}");
        Assert.All(firstOfDay, t => Assert.NotNull(t.EntryTrend));
    }
}
