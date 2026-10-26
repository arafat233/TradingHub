using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>The Insights alerts: each fires when its condition starts, waits out a cooldown, and respects its switch and size.</summary>
public class InsightAlertTests
{
    private static readonly Instrument Btc = new() { Symbol = "BTCUSDT", FeedId = "bybit-linear", TickSize = 0.1m, VolumeStep = 0.001m };
    private static readonly DateTime T = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
    private static readonly InsightAlertSettings On = new(Toxicity: true, BigWall: true, WallUsd: 1_000_000, OptionsTouch: true, Liquidations: true, LiquidationUsd: 1_000_000, Setup: true);

    private static Decision Wait() => new(Verdict.Wait, 0, 0, "WAIT", "", Array.Empty<Check>(), Array.Empty<Chip>(), 0, 0, null, null, null);

    private static InsightsSnapshot Snap(DangerLevel danger = DangerLevel.Normal, Decision? decision = null, IReadOnlyList<BookEvent>? events = null) =>
        new(decision ?? Wait(), new QuoteHeader(0, 0, 0, 0), danger, "", 0.82m, new WallStats(null, 0, null, 0), false, "", 8, null, null, events);

    private static readonly IReadOnlyList<LevelMark> NoMarks = Array.Empty<LevelMark>();

    [Fact]
    public void Toxicity_alerts_when_it_turns_high_not_while_it_stays_high_and_not_again_within_the_cooldown()
    {
        var e = new InsightAlertEngine();
        Assert.Empty(e.Evaluate(Snap(DangerLevel.Normal), Btc, 80000m, NoMarks, T, On));
        Assert.Single(e.Evaluate(Snap(DangerLevel.High), Btc, 80000m, NoMarks, T.AddMinutes(1), On));     // it turned high
        Assert.Empty(e.Evaluate(Snap(DangerLevel.High), Btc, 80000m, NoMarks, T.AddMinutes(2), On));      // still high: quiet
        Assert.Empty(e.Evaluate(Snap(DangerLevel.Normal), Btc, 80000m, NoMarks, T.AddMinutes(3), On));
        Assert.Empty(e.Evaluate(Snap(DangerLevel.High), Btc, 80000m, NoMarks, T.AddMinutes(10), On));     // high again, but inside the 30 minute cooldown
        Assert.Empty(e.Evaluate(Snap(DangerLevel.Normal), Btc, 80000m, NoMarks, T.AddMinutes(11), On));
        Assert.Single(e.Evaluate(Snap(DangerLevel.High), Btc, 80000m, NoMarks, T.AddMinutes(45), On));    // after it
        Assert.Empty(new InsightAlertEngine().Evaluate(Snap(DangerLevel.High), Btc, 80000m, NoMarks, T, On with { Toxicity = false }));
    }

    [Fact]
    public void A_big_wall_that_is_pulled_alerts_once_and_small_ones_and_additions_do_not()
    {
        var e = new InsightAlertEngine();
        BookEvent Ev(double sec, BookEventKind k, decimal size) => new(T.AddSeconds(sec), k, true, Btc.ToTicks(80000m), size);
        // 15 BTC at 80 000 is $1.2M: big enough; 5 BTC is $400k: not
        var big = Ev(1, BookEventKind.Pulled, 15m);
        var small = Ev(2, BookEventKind.Pulled, 5m);
        var added = Ev(3, BookEventKind.Added, 50m);
        var first = e.Evaluate(Snap(events: new[] { big, small, added }), Btc, 80000m, NoMarks, T.AddSeconds(5), On);
        var alert = Assert.Single(first);
        Assert.Contains("pulled", alert);
        Assert.Contains("$1.2M", alert);
        Assert.Empty(e.Evaluate(Snap(events: new[] { big, small, added }), Btc, 80000m, NoMarks, T.AddSeconds(6), On));  // the same events are not told twice
        Assert.Empty(new InsightAlertEngine().Evaluate(Snap(events: new[] { big }), Btc, 80000m, NoMarks, T, On with { BigWall = false }));
        Assert.Empty(new InsightAlertEngine().Evaluate(Snap(events: new[] { big }), Btc, 80000m, NoMarks, T, On with { WallUsd = 5_000_000 }));
    }

    [Fact]
    public void Price_at_an_options_level_alerts_once_and_not_when_it_is_away()
    {
        var e = new InsightAlertEngine();
        var marks = new[] { new LevelMark("call wall (long text)", 85000m, -1, "options", "call wall"), new LevelMark("longs", 79000m, -1, "liquidations", "longs liq", 2_000_000m) };
        Assert.Empty(e.Evaluate(Snap(), Btc, 80000m, marks, T, On with { Liquidations = false }));                        // 6% away
        var hit = Assert.Single(e.Evaluate(Snap(), Btc, 84980m, marks, T.AddMinutes(1), On with { Liquidations = false })); // within 0.05%
        Assert.Contains("call wall", hit);
        Assert.Empty(e.Evaluate(Snap(), Btc, 84990m, marks, T.AddMinutes(2), On with { Liquidations = false }));          // cooldown
        Assert.Empty(new InsightAlertEngine().Evaluate(Snap(), Btc, 84990m, marks, T, On with { OptionsTouch = false, Liquidations = false }));
    }

    [Fact]
    public void A_big_liquidation_cluster_alerts_when_it_forms_or_grows_by_half_and_not_when_it_is_small()
    {
        var e = new InsightAlertEngine();
        LevelMark Liq(decimal money) => new("longs liquidated", 79000m, -1, "liquidations", "longs liq", money);
        Assert.Empty(e.Evaluate(Snap(), Btc, 80000m, new[] { Liq(400_000m) }, T, On));                                      // under $1M
        var formed = Assert.Single(e.Evaluate(Snap(), Btc, 80000m, new[] { Liq(1_500_000m) }, T.AddMinutes(1), On));
        Assert.Contains("Longs liquidated", formed);
        Assert.Contains("$1.5M", formed);
        Assert.Empty(e.Evaluate(Snap(), Btc, 80000m, new[] { Liq(1_600_000m) }, T.AddMinutes(30), On));                    // barely bigger
        Assert.Single(e.Evaluate(Snap(), Btc, 80000m, new[] { Liq(2_500_000m) }, T.AddMinutes(31), On));                    // 50% bigger than what was said
    }

    [Fact]
    public void A_setup_alert_is_off_by_default_and_fires_when_the_verdict_changes_to_a_setup()
    {
        var setup = Wait() with { Verdict = Verdict.BuySetup, Lean = 1, Headline = "BUY SETUP" };
        var e = new InsightAlertEngine();
        Assert.Empty(e.Evaluate(Snap(decision: Wait()), Btc, 80000m, NoMarks, T, On));
        Assert.Contains("BUY SETUP", Assert.Single(e.Evaluate(Snap(decision: setup), Btc, 80000m, NoMarks, T.AddMinutes(1), On)));
        Assert.Empty(e.Evaluate(Snap(decision: setup), Btc, 80000m, NoMarks, T.AddMinutes(2), On));                          // it stays a setup: quiet
        Assert.Empty(new InsightAlertEngine().Evaluate(Snap(decision: setup), Btc, 80000m, NoMarks, T, new InsightAlertSettings()));   // off unless switched on
    }

    [Fact]
    public void Touching_your_own_level_alerts_once_and_stale_data_says_nothing()
    {
        var engine = new InsightAlertEngine();
        var marks = new List<LevelMark> { new("my level", 100.02m, 0, MyLevels.Source, "mine") };
        Assert.Single(engine.Evaluate(Snap(), Btc, 100m, marks, T, On));
        Assert.Empty(engine.Evaluate(Snap(), Btc, 100m, marks, T.AddMinutes(1), On));   // cooldown
        Assert.Empty(engine.Evaluate(Snap(), Btc, 105m, marks, T.AddHours(1), On));     // not near
        var stale = Snap() with { StaleSeconds = 40 };
        Assert.Empty(new InsightAlertEngine().Evaluate(stale, Btc, 100m, marks, T, On)); // nothing is said from old numbers
    }
}
