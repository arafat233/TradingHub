using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Whale detection modes, the range sentence, zones, pull counts, estimated liquidations and the derivatives parsers.</summary>
public class WhaleViewTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 0.01m, VolumeStep = 0.001m };
    private static readonly DateTime T0 = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Detect_modes_each_pick_their_own_orders()
    {
        var sweep = new OrderShape(100, 20, 5, false);
        var single = new OrderShape(100, 100, 1, false);
        var atLevel = new OrderShape(100, 100, 1, true);
        Assert.True(WhaleDetect.Passes(WhaleMode.Aggressive, single, 50));
        Assert.False(WhaleDetect.Passes(WhaleMode.Aggressive, single, 150));
        Assert.True(WhaleDetect.Passes(WhaleMode.Sweep, sweep, 50));
        Assert.False(WhaleDetect.Passes(WhaleMode.Sweep, single, 50)); // one level only
        Assert.False(WhaleDetect.Passes(WhaleMode.Level, sweep, 50));
        Assert.True(WhaleDetect.Passes(WhaleMode.Level, atLevel, 50));
        Assert.False(WhaleDetect.Passes(WhaleMode.BigPrint, sweep, 50)); // 100 in total but no single print of 50
        Assert.True(WhaleDetect.Passes(WhaleMode.BigPrint, single, 50));
    }

    [Fact]
    public void Summary_says_two_sided_or_who_leads_and_never_gives_advice()
    {
        Assert.Contains("two-sided", WhaleDetect.Summary("15:10", -0.04m, 107, 6_000_000, 4_000_000));
        Assert.Contains("net buyers", WhaleDetect.Summary("15:10", 0.5m, 10, 9_000_000, 1_000_000));
        Assert.Contains("net sellers", WhaleDetect.Summary("15:10", -0.5m, 10, 1_000_000, 9_000_000));
        Assert.Contains("no whale orders", WhaleDetect.Summary("15:10", 0.1m, 0, 0, 0));
        Assert.Contains("-0.04%", WhaleDetect.Summary("15:10", -0.04m, 3, 1, 1));
    }

    [Fact]
    public void Average_price_is_volume_weighted()
    {
        Assert.Equal(103.2m, WhaleDetect.AveragePrice(new[] { (100m, 1m), (110m, 1m), (102m, 3m) }));
        Assert.Null(WhaleDetect.AveragePrice(Array.Empty<(decimal, decimal)>()));
    }

    [Fact]
    public void Zones_need_three_close_orders_of_the_same_side()
    {
        var orders = new (decimal, decimal, bool)[]
        {
            (100m, 10, true), (100.1m, 20, true), (100.2m, 30, true), (110m, 5, true), // three buys chain together, one is far away
            (100.1m, 50, false), (100.15m, 50, false), // only two sells
        };
        var zones = WhaleDetect.Zones(orders, 0.2m);
        var z = Assert.Single(zones);
        Assert.True(z.Buy);
        Assert.Equal(3, z.Count);
        Assert.Equal(100m, z.Low);
        Assert.Equal(100.2m, z.High);
        Assert.Equal(60m, z.Money);
    }

    [Fact]
    public void Pull_hotspot_is_the_price_pulled_most_and_needs_two()
    {
        var now = T0.AddMinutes(10);
        var events = new List<BookEvent>
        {
            new(T0.AddMinutes(1), BookEventKind.Pulled, true, 8049201, 5),
            new(T0.AddMinutes(3), BookEventKind.Flash, true, 8049201, 5),
            new(T0.AddMinutes(4), BookEventKind.Pulled, true, 8049201, 5),
            new(T0.AddMinutes(5), BookEventKind.Pulled, false, 8043413, 5),
            new(T0.AddMinutes(6), BookEventKind.Eaten, true, 8049201, 5), // eaten is not a pull
        };
        var h = PullStats.Hotspot(events, now, TimeSpan.FromMinutes(10));
        Assert.Equal((8049201L, true, 3), h);
        Assert.Null(PullStats.Hotspot(events.Skip(3), now, TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Estimated_liquidations_come_from_the_big_orders_and_sit_on_the_right_side_of_price()
    {
        var store = new TradeStore(Inst);
        long id = 1;
        for (int i = 0; i < 1500; i++)
        {
            bool big = i % 35 == 0;
            store.Add(new Trade(id++, T0.AddSeconds(i), 100m + (i % 3) * 0.1m, big ? 100m : 1m, i % 2 == 0 ? TradeSide.Buy : TradeSide.Sell));
        }

        var marks = LiquidationProjection.Marks(store, Inst, store.LastTime, TimeSpan.FromHours(6));
        var longs = Assert.Single(marks, m => m.Tone < 0);
        var shorts = Assert.Single(marks, m => m.Tone > 0);
        Assert.InRange(longs.Price, 90m, 99.9m);
        Assert.InRange(shorts.Price, 100.5m, 110m);
        Assert.Contains("est.", longs.Label);
        Assert.Empty(LiquidationProjection.Marks(new TradeStore(Inst), Inst, T0, TimeSpan.FromHours(1))); // too little tape: no guess
    }

    [Fact]
    public void Derivatives_parsers_read_the_public_answers_and_survive_bad_ones()
    {
        var t = DerivativesService.ParseTicker("""{"result":{"list":[{"symbol":"BTCUSDT","lastPrice":"80450","indexPrice":"80400","fundingRate":"0.0001","nextFundingTime":"1793000000000","openInterestValue":"5000000000"}]}}""")!;
        Assert.Equal(0.0001m, t.Funding);
        Assert.Equal(5_000_000_000m, t.OpenInterestValue);
        Assert.Equal(0.0622m, Math.Round(t.BasisPct, 4));
        Assert.Null(DerivativesService.ParseTicker("""{"result":{"list":[]}}"""));

        Assert.Equal(10m, DerivativesService.ParseOiChange("""{"result":{"list":[{"openInterest":"110"},{"openInterest":"105"},{"openInterest":"100"}]}}"""));
        Assert.Null(DerivativesService.ParseOiChange("not json"));
        Assert.Equal(0.55m, DerivativesService.ParseBuyRatio("""{"result":{"list":[{"buyRatio":"0.55","sellRatio":"0.45"}]}}"""));
        Assert.Equal((41, "Fear"), DerivativesService.ParseFearGreed("""{"data":[{"value":"41","value_classification":"Fear"}]}"""));
        Assert.Equal((null, null), DerivativesService.ParseFearGreed(null));
    }

    [Theory]
    [InlineData("BTCUSDT", "BTCUSDT")]
    [InlineData("XBTUSD", "BTCUSDT")]
    [InlineData("SOLUSDT", "SOLUSDT")]
    [InlineData("ES", null)]
    public void Bybit_symbol_of_an_instrument(string symbol, string? expected)
    {
        var inst = new Instrument { Symbol = symbol, FeedId = "test", TickSize = 0.01m };
        Assert.Equal(expected, DerivativesService.SymbolOf(inst));
    }
}
