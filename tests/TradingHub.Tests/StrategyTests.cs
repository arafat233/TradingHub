using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

public class StrategyTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m, PointValue = 1m };
    private static readonly DateTime T0 = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>One trade per second with the given prices.</summary>
    private static TickData Ticks(params decimal[] prices)
    {
        var list = new List<Trade>();
        for (int i = 0; i < prices.Length; i++) list.Add(new Trade(i + 1, T0.AddSeconds(i * 20), prices[i], 1, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        return TickData.FromTrades(Inst, list);
    }

    private static BacktestSettings Settings() => new()
    {
        FeedId = "test",
        Symbol = "TEST",
        FromUtc = T0,
        ToUtc = T0.AddDays(1),
        Period = "M1",
        CommissionPercent = 0,
        SlippageTicks = 0,
        InitialCapital = 1000,
    };

    /// <summary>Buys on the first closed candle and sells on the third.</summary>
    private sealed class BuyThenSell : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            if (CurrentBar == 0) Buy(2);
            if (CurrentBar == 2 && IsLong) ClosePosition();
        }
    }

    [Fact]
    public void A_market_order_fills_with_the_trade_after_the_candle_close()
    {
        // 3 trades per 1-minute candle: 100,101,102 | 103,104,105 | 106,107,108 | 109
        var data = Ticks(100, 101, 102, 103, 104, 105, 106, 107, 108, 109);
        var r = new BacktestEngine(new BuyThenSell(), data, Settings()).Run();
        Assert.Null(r.Error);
        var t = Assert.Single(r.Trades);
        Assert.True(t.IsLong);
        Assert.Equal(103m, t.EntryPrice); // first trade of candle 2
        Assert.Equal(109m, t.ExitPrice);  // first trade after the close of candle 3
        Assert.Equal(12m, t.Pnl);         // (109 - 103) * 2
        Assert.Equal(12m, r.Metrics.NetProfit);
    }

    [Fact]
    public void Commission_and_slippage_are_deducted()
    {
        var s = Settings();
        s.SlippageTicks = 1;
        s.CommissionPerUnit = 0.5m;
        var data = Ticks(100, 101, 102, 103, 104, 105, 106, 107, 108, 109);
        var r = new BacktestEngine(new BuyThenSell(), data, s).Run();
        var t = Assert.Single(r.Trades);
        Assert.Equal(104m, t.EntryPrice); // 103 + 1 tick
        Assert.Equal(108m, t.ExitPrice);  // 109 - 1 tick
        Assert.Equal(8m, t.Pnl);
        Assert.Equal(2m, t.Commission);   // 2 units x 0.5 x 2 sides
        Assert.Equal(6m, r.Metrics.NetProfit);
    }

    /// <summary>Enters long with an automatic stop and target.</summary>
    private sealed class Bracket : Strategy
    {
        public Bracket()
        {
            StopLossTicks = 3;
            TakeProfitTicks = 5;
        }

        protected override void OnBar(Bar bar)
        {
            if (CurrentBar == 0 && IsFlat) Buy();
        }
    }

    [Fact]
    public void Take_profit_closes_and_cancels_the_stop()
    {
        var data = Ticks(100, 100, 100, 100, 102, 104, 106, 103, 99, 98);
        var r = new BacktestEngine(new Bracket(), data, Settings()).Run();
        var t = Assert.Single(r.Trades);
        Assert.Equal(100m, t.EntryPrice);
        Assert.Equal(105m, t.ExitPrice); // limit at +5 ticks, crossed by 106
        Assert.Equal("Take profit", t.ExitName);
    }

    [Fact]
    public void Stop_loss_executes_as_a_market_order()
    {
        var data = Ticks(100, 100, 100, 100, 99, 98, 96, 95, 99, 98);
        var r = new BacktestEngine(new Bracket(), data, Settings()).Run();
        var t = Assert.Single(r.Trades);
        Assert.Equal(96m, t.ExitPrice); // the stop at 97 triggers with 96 (a gap)
        Assert.Equal(-4m, t.Pnl);
        Assert.Equal("Stop loss", t.ExitName);
    }

    /// <summary>Buy limit below the price.</summary>
    private sealed class LimitBuyer : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            if (CurrentBar == 0) BuyLimit(98);
        }
    }

    [Fact]
    public void A_limit_only_fills_if_the_price_goes_through_it()
    {
        var touchOnly = Ticks(100, 100, 100, 99, 98, 99, 100, 101, 102, 103);
        var r = new BacktestEngine(new LimitBuyer(), touchOnly, Settings()).Run();
        Assert.Empty(r.Trades);

        var through = Ticks(100, 100, 100, 99, 97, 99, 100, 101, 102, 103);
        var r2 = new BacktestEngine(new LimitBuyer(), through, Settings()).Run();
        var t = Assert.Single(r2.Trades); // it is closed at the end of the backtest
        Assert.Equal(98m, t.EntryPrice);
        Assert.Equal("End of backtest", t.ExitName);
    }

    /// <summary>Reverses the position from long to short.</summary>
    private sealed class Reverser : Strategy
    {
        protected override void OnBar(Bar bar)
        {
            if (CurrentBar == 0) EnterLong();
            if (CurrentBar == 1) EnterShort();
        }
    }

    [Fact]
    public void Reversing_the_position_records_two_trades()
    {
        var data = Ticks(100, 100, 100, 101, 102, 103, 104, 105, 106, 103);
        var r = new BacktestEngine(new Reverser(), data, Settings()).Run();
        Assert.Equal(2, r.Trades.Count);
        Assert.True(r.Trades[0].IsLong);
        Assert.Equal(101m, r.Trades[0].EntryPrice);
        Assert.Equal(104m, r.Trades[0].ExitPrice);
        Assert.False(r.Trades[1].IsLong);
        Assert.Equal(104m, r.Trades[1].EntryPrice);
        Assert.Equal(103m, r.Trades[1].ExitPrice); // end of the backtest
        Assert.Equal(4m, r.Metrics.NetProfit);
    }

    private sealed class SmaCross : Strategy
    {
        [Parameter, Optimize(2, 4, 1)]
        public int Fast { get; set; } = 2;

        private Sma _fast = null!, _slow = null!;

        protected override void OnInit()
        {
            _fast = Sma(Fast);
            _slow = Sma(5);
        }

        protected override void OnBar(Bar bar)
        {
            if (CrossAbove(_fast, _slow)) EnterLong();
            else if (CrossBelow(_fast, _slow)) EnterShort();
        }
    }

    [Fact]
    public void The_optimization_tests_every_combination_and_sorts_by_metric()
    {
        var prices = Enumerable.Range(0, 600).Select(i => 1000m + (decimal)Math.Round(30 * Math.Sin(i / 15.0))).ToArray();
        var data = Ticks(prices);
        var ranges = ParameterRange.FromStrategy(typeof(SmaCross), new SmaCross());
        var fast = ranges.Single(r => r.Name == nameof(SmaCross.Fast));
        Assert.True(fast.Enabled);
        Assert.Equal(3, fast.Count);
        var res = Optimizer.Optimize(() => new SmaCross(), new(), ranges, data, Settings(), FitnessMetric.NetProfit, 0, null, CancellationToken.None, 2);
        Assert.Equal(3, res.Rows.Count);
        Assert.True(res.Rows[0].Fitness >= res.Rows[1].Fitness);

        var mc = Optimizer.MonteCarlo(new BacktestEngine(new SmaCross(), data, Settings()).Run().Trades, 1000, 200);
        Assert.True(mc.DrawdownP95 >= mc.DrawdownP50);
    }

    [Fact]
    public void History_gaps_are_detected_before_between_and_after()
    {
        var t0 = new DateTime(2026, 10, 3, 10, 0, 0, DateTimeKind.Utc);
        var local = new List<Trade> { new(10, t0.AddMinutes(30), 1, 1, TradeSide.Buy), new(11, t0.AddMinutes(31), 1, 1, TradeSide.Buy), new(12, t0.AddMinutes(50), 1, 1, TradeSide.Buy) };
        var gaps = InstrumentData.FindGaps(local, t0, t0.AddHours(1), tailById: true);
        Assert.Equal(3, gaps.Count);
        Assert.Equal((t0, t0.AddMinutes(30), (long?)null), gaps[0]);
        Assert.Equal(t0.AddMinutes(31), gaps[1].From);
        Assert.Equal(13, gaps[2].FromId);
    }

    /// <summary>Channel breakout with ATR: the channel is a parameter and the ATR period a constant in the code.</summary>
    private sealed class ChannelWithAtr : Strategy
    {
        private const int PeriodoAtr = 14;
        private Highest _max = null!;
        private Atr _atr = null!;

        [Parameter("Channel"), Optimize(10, 40, 10)]
        public int Channel { get; set; } = 20;

        /// <summary>Candles closed once every indicator was ready (-1 = never).</summary>
        public int ReadyAt { get; private set; } = -1;

        protected override void OnInit()
        {
            _max = Highest(Channel);
            Lowest(Channel);
            _atr = Atr(PeriodoAtr);
            StopLossTicks = TickSize > 0 ? 50 : 0; // it uses the instrument in OnInit (the meter has to have it)
        }

        protected override void OnBar(Bar bar)
        {
            if (ReadyAt < 0 && _max.IsReady && _atr.IsReady) ReadyAt = CurrentBar + 1;
        }
    }

    private sealed class RsiYMacd : Strategy
    {
        protected override void OnInit()
        {
            Rsi(14);
            Macd();
        }
    }

    private sealed class SinIndicadores : Strategy
    {
    }

    private sealed class FallaAlEmpezar : Strategy
    {
        protected override void OnInit()
        {
            Sma(5);
            throw new InvalidOperationException("broken");
        }
    }

    [Fact]
    public void Warmup_reports_the_indicators_with_their_period_and_the_candles_they_need()
    {
        var w = StrategyWarmup.Measure(typeof(ChannelWithAtr));
        Assert.Null(w.Error);
        Assert.True(w.Complete);
        Assert.Equal(new[] { "Highest(20)", "Lowest(20)", "ATR(14)" }, w.Indicators);
        Assert.Equal(20, w.Bars);
        // with the channel at the maximum of [Optimize] (the parameters arrive as double, as they do from the lab)
        var max = StrategyWarmup.Measure(typeof(ChannelWithAtr), new Dictionary<string, object?> { ["Channel"] = 40.0 });
        Assert.Equal(40, max.Bars);
        Assert.Contains("Highest(40)", max.Indicators);
        // with the channel shorter than the ATR, the ATR wins
        Assert.Equal(14, StrategyWarmup.Measure(typeof(ChannelWithAtr), new Dictionary<string, object?> { ["Channel"] = 10.0 }).Bars);

        Assert.Equal(26, StrategyWarmup.Measure(typeof(RsiYMacd)).Bars); // the MACD's slow EMA (the RSI asks for 15)
        var none = StrategyWarmup.Measure(typeof(SinIndicadores));
        Assert.True(none.Complete);
        Assert.Equal(0, none.Bars);
        Assert.Empty(none.Indicators);
        var bad = StrategyWarmup.Measure(typeof(FallaAlEmpezar));
        Assert.False(bad.Complete);
        Assert.Contains("broken", bad.Error);
    }

    [Fact]
    public void The_measured_warmup_matches_the_backtest_one()
    {
        var prices = Enumerable.Range(0, 300).Select(i => 1000m + (decimal)Math.Round(30 * Math.Sin(i / 9.0))).ToArray();
        var s = new ChannelWithAtr { Channel = 30 };
        var r = new BacktestEngine(s, Ticks(prices), Settings()).Run();
        Assert.Null(r.Error);
        Assert.Equal(StrategyWarmup.Measure(typeof(ChannelWithAtr), s.GetParameters()).Bars, s.ReadyAt);
        Assert.Equal(30, s.ReadyAt);
    }
}
