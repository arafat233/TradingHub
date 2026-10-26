using System.Text.Json;
using TradingHub.Data;
using TradingHub.Feeds.Bybit;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>Options walls, max pain and gamma flip; liquidation clusters; and the shared board the chart overlay and the panel read.</summary>
public class MarketLevelsTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Soon = Now.AddDays(7), Later = Now.AddDays(20);

    private static OptionQuote Q(decimal strike, bool call, decimal oi, DateTime? expiry = null, decimal iv = 50) => new(strike, call, expiry ?? Soon, oi, iv);

    [Fact]
    public void Call_wall_is_the_biggest_call_strike_above_the_price_and_put_wall_the_biggest_put_strike_below()
    {
        var quotes = new[]
        {
            Q(82000, true, 900), Q(84000, true, 400), Q(78000, true, 5000),    // 78000 is below the price: not a wall for calls
            Q(76000, false, 700), Q(78000, false, 200), Q(85000, false, 6000), // 85000 is above the price: not a wall for puts
        };
        var set = OptionLevelSet.Compute(quotes, 80000m, Now);
        Assert.Equal(82000m, set.CallWall);
        Assert.Equal(76000m, set.PutWall);
    }

    [Fact]
    public void Max_pain_is_the_strike_where_the_biggest_expiry_pays_out_least()
    {
        // calls and puts both piled on 80000: it is where everyone holding them loses most, so the writers pay least
        var quotes = new[]
        {
            Q(78000, true, 100), Q(80000, true, 1000), Q(82000, true, 100),
            Q(78000, false, 100), Q(80000, false, 1000), Q(82000, false, 100),
            Q(90000, true, 50, Later),                                       // a smaller expiry is not the one
        };
        var set = OptionLevelSet.Compute(quotes, 80500m, Now);
        Assert.Equal(80000m, set.MaxPain);
        Assert.Equal(Soon.Date.AddHours(8).Date, set.MaxPainExpiry!.Value.Date);
    }

    [Fact]
    public void Gamma_flips_sign_where_call_gamma_stops_outweighing_put_gamma()
    {
        // calls concentrated high, puts concentrated low: net gamma is negative at low prices (puts) and positive at high prices (calls)
        var quotes = new[] { Q(88000, true, 3000), Q(72000, false, 3000) };
        var set = OptionLevelSet.Compute(quotes, 80000m, Now);
        Assert.NotNull(set.GammaFlip);
        Assert.InRange(set.GammaFlip!.Value, 76000m, 84000m);                // by symmetry, between the two walls and near the middle
    }

    [Fact]
    public void Gamma_is_zero_for_nonsense_and_peaks_at_the_money()
    {
        Assert.Equal(0, OptionLevelSet.Gamma(0, 100, 0.5, 0.1));
        Assert.Equal(0, OptionLevelSet.Gamma(100, 100, 0, 0.1));
        Assert.True(OptionLevelSet.Gamma(100, 100, 0.5, 0.1) > OptionLevelSet.Gamma(100, 150, 0.5, 0.1));
        Assert.True(OptionLevelSet.Gamma(100, 100, 0.5, 0.1) > OptionLevelSet.Gamma(100, 60, 0.5, 0.1));
    }

    [Fact]
    public void Deribit_book_summaries_are_read_with_the_index_price_taken_from_the_middle_of_the_estimated_delivery_prices()
    {
        string json = "{\"result\":[" +
            "{\"instrument_name\":\"BTC-27NOV26-73000-P\",\"open_interest\":400.7,\"mark_iv\":40.49,\"estimated_delivery_price\":80204.88,\"underlying_price\":80593.62}," +
            "{\"instrument_name\":\"BTC-7NOV26-90000-C\",\"open_interest\":12,\"mark_iv\":55.0,\"estimated_delivery_price\":80204.90}," +
            "{\"instrument_name\":\"BTC-PERPETUAL\",\"open_interest\":1}," +
            "{\"instrument_name\":\"BTC-25DEC26-80000-C\",\"open_interest\":7,\"mark_iv\":0,\"estimated_delivery_price\":80204.86}]}";
        var (quotes, spot) = OptionsLevelService.Parse(json);
        Assert.Equal(3, quotes.Count);                                       // the perpetual is not an option
        Assert.Equal(80204.88m, spot);
        Assert.False(quotes[0].IsCall);
        Assert.Equal((73000m, 400.7m, 40.49m), (quotes[0].Strike, quotes[0].OpenInterest, quotes[0].MarkIv));
        Assert.Equal(new DateTime(2026, 11, 27, 8, 0, 0, DateTimeKind.Utc), quotes[0].Expiry);
        Assert.Equal(new DateTime(2026, 11, 7, 8, 0, 0, DateTimeKind.Utc), quotes[1].Expiry);  // a one digit day
    }

    [Fact]
    public void Currencies_are_found_from_the_base_asset_or_the_symbol()
    {
        Assert.Equal("BTC", OptionsLevelService.CurrencyOf(new Instrument { Symbol = "BTCUSDT", FeedId = "x", TickSize = 0.1m }));
        Assert.Equal("BTC", OptionsLevelService.CurrencyOf(new Instrument { Symbol = "BTC-USD", FeedId = "x", TickSize = 0.01m, BaseAsset = "BTC" }));
        Assert.Equal("ETH", OptionsLevelService.CurrencyOf(new Instrument { Symbol = "ETH/USD", FeedId = "x", TickSize = 0.01m }));
        Assert.Null(OptionsLevelService.CurrencyOf(new Instrument { Symbol = "GC.F", FeedId = "x", TickSize = 0.1m }));
    }

    [Fact]
    public void Liquidation_clusters_find_where_most_money_was_forced_out_on_each_side()
    {
        var inst = new Instrument { Symbol = "BTCUSDT", FeedId = "test", TickSize = 0.1m, VolumeStep = 0.001m };
        var liqs = new List<Liquidation>();
        for (int i = 0; i < 5; i++) liqs.Add(new Liquidation(Now.AddMinutes(-30 + i), inst.ToTicks(79000m + i), 2m, LongLiquidated: true));      // longs forced out near 79000: 5 x 2 BTC
        liqs.Add(new Liquidation(Now.AddMinutes(-20), inst.ToTicks(80500m), 1m, LongLiquidated: false));                                           // a small short squeeze above
        liqs.Add(new Liquidation(Now.AddHours(-9), inst.ToTicks(70000m), 50m, LongLiquidated: true));                                              // too old
        var marks = LiquidationClusters.Marks(liqs, inst, Now, TimeSpan.FromHours(6));
        Assert.Equal(2, marks.Count);
        var longs = marks.Single(m => m.Label.StartsWith("longs"));
        Assert.InRange(longs.Price, 78999m, 79005m);
        Assert.Contains("5×", longs.Label);
        Assert.Equal(-1, longs.Tone);
        Assert.Equal(1, marks.Single(m => m.Label.StartsWith("shorts")).Tone);
        Assert.Empty(LiquidationClusters.Marks(new List<Liquidation>(), inst, Now, TimeSpan.FromHours(6)));
    }

    [Fact]
    public void Bybit_liquidation_messages_say_which_side_was_closed()
    {
        var inst = new Instrument { Symbol = "BTCUSDT", FeedId = "bybit-linear", TickSize = 0.1m, VolumeStep = 0.001m };
        using var doc = JsonDocument.Parse("[{\"T\":1793013044720,\"s\":\"BTCUSDT\",\"S\":\"Buy\",\"v\":\"0.021\",\"p\":\"79986.00\"},{\"T\":1793013047551,\"s\":\"BTCUSDT\",\"S\":\"Sell\",\"v\":\"0.5\",\"p\":\"80100.5\"},{\"s\":\"BTCUSDT\"}]");
        var list = BybitFeed.ParseLiquidations(doc.RootElement, inst);
        Assert.Equal(2, list.Count);                                         // the malformed one is skipped
        Assert.True(list[0].LongLiquidated);                                 // "Buy" = a long position was closed
        Assert.False(list[1].LongLiquidated);
        Assert.Equal(inst.ToTicks(79986m), list[0].PriceTicks);
        Assert.Equal(DateTime.UnixEpoch.AddMilliseconds(1793013044720), list[0].Time);
    }

    [Fact]
    public void The_board_gives_an_instrument_its_own_marks_and_those_of_its_underlying()
    {
        MarketLevels.Clear();
        var inst = new Instrument { Symbol = "BTCUSDT", FeedId = "bybit-linear", TickSize = 0.1m, VolumeStep = 0.001m };
        MarketLevels.Set("BTC", "options", new[] { new LevelMark("call wall", 82000m, -1, "options") });
        MarketLevels.Set(inst.Key, "liquidations", new[] { new LevelMark("longs", 79000m, -1, "liquidations") });
        MarketLevels.Set("ETH", "options", new[] { new LevelMark("eth wall", 3000m, 0, "options") });
        MarketLevels.Set("bybit-linear:SOLUSDT", "liquidations", new[] { new LevelMark("sol", 150m, 0, "liquidations") });
        Assert.Equal(new[] { "call wall", "longs" }, MarketLevels.For(inst).Select(m => m.Label).OrderBy(x => x));
        MarketLevels.Clear();
    }
}

/// <summary>The options levels from the REAL Deribit (only when BOOKEVENTS_LIVE is set).</summary>
public class OptionsLevelsLiveTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task OptionsLive_real_deribit_btc_and_eth_levels()
    {
        if (Environment.GetEnvironmentVariable("BOOKEVENTS_LIVE") is not { Length: > 0 }) return;
        foreach (var cur in new[] { "BTC", "ETH" })
        {
            var set = await OptionsLevelService.RefreshAsync(cur, CancellationToken.None);
            Assert.NotNull(set);
            output.WriteLine($"{cur}: spot {set!.Spot:0.##} · {set.Options} options within 45 days · call wall {set.CallWall} · put wall {set.PutWall} · max pain {set.MaxPain} ({set.MaxPainExpiry:d MMM}) · gamma flip {set.GammaFlip:0.#}");
            foreach (var m in MarketLevels.For(new Instrument { Symbol = cur + "USDT", FeedId = "x", TickSize = 0.1m })) output.WriteLine($"   mark: {m.Label} @ {m.Price:0.#} ({m.Tone})");
            Assert.True(set.CallWall > set.Spot && set.PutWall < set.Spot);
        }
    }
}
