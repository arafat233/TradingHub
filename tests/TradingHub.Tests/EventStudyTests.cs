using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

/// <summary>The order-flow event tester: replays days of tape, scores events at four horizons after costs, with a train/test split and baselines.</summary>
public class EventStudyTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 0.01m, VolumeStep = 0.001m };

    /// <summary>A day of tape: 150 minutes, a trade a second, <paramref name="buyShare"/> of them buys, price moving <paramref name="ticksPerMinute"/> ticks a minute.</summary>
    private static List<Trade> Day(int dayIndex, double buyShare, int ticksPerMinute, int minutes = 150)
    {
        var list = new List<Trade>();
        var t0 = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc).AddDays(dayIndex);
        long id = 1;
        for (int m = 0; m < minutes; m++)
            for (int s = 0; s < 60; s++)
            {
                decimal price = 100m + (m * ticksPerMinute + s * ticksPerMinute / 60m) * 0.01m;
                list.Add(new Trade(id++, t0.AddMinutes(m).AddSeconds(s), Inst.RoundPrice(price), 1m, s < 60 * buyShare ? TradeSide.Buy : TradeSide.Sell));
            }

        return list;
    }

    private static IReadOnlyList<IReadOnlyList<Trade>> Days(int n, double buyShare, int ticksPerMinute) =>
        Enumerable.Range(0, n).Select(d => (IReadOnlyList<Trade>)Day(d, buyShare, ticksPerMinute)).ToList();

    [Fact]
    public void An_idea_that_really_works_is_positive_on_the_unseen_days_and_its_opposite_loses()
    {
        // buyers take 80% and price keeps rising: following the lean wins on every day, train and test alike
        var r = EventStudy.Run(Days(5, 0.8, 4), Inst, StudyEvent.Lean, costBps: 1);
        Assert.True(r.Events > 10);
        Assert.Equal(3, r.TrainDays);
        var h15 = r.Horizons.Single(h => h.Minutes == 15);
        Assert.InRange(h15.All.Gross, 40, 80);                                  // 15 minutes x 4 ticks x 0.01 on a price near 100 = about 60 bp
        Assert.True(h15.Train.Net > 30 && h15.Test.Net > 30, $"train {h15.Train.Net:0.0}, test {h15.Test.Net:0.0}");
        Assert.True(h15.Faded.Gross < -40, "the opposite trade loses");
        Assert.True(h15.AlwaysLong.Gross > 40, "and the baseline shows the drift that made it look good");
        Assert.Equal(1.0, h15.Test.Win);
    }

    [Fact]
    public void An_idea_with_nothing_in_it_loses_exactly_the_costs()
    {
        // a flat market where buyers happen to lead: there is nowhere to go, so every event pays the cost and earns nothing
        var r = EventStudy.Run(Days(5, 0.8, 0), Inst, StudyEvent.Lean, costBps: 3);
        Assert.True(r.Events > 10);
        var h = r.Horizons.Single(x => x.Minutes == 5);
        Assert.Equal(0, h.All.Gross, 6);
        Assert.Equal(-3, h.All.Net, 6);
        Assert.Equal(0, h.All.Win);
    }

    [Fact]
    public void Every_event_kind_runs_and_the_report_names_the_event_and_warns_about_luck()
    {
        foreach (var kind in Enum.GetValues<StudyEvent>())
        {
            var r = EventStudy.Run(Days(4, 0.7, 2), Inst, kind, costBps: 2);
            Assert.Equal(4, r.Horizons.Count);
            Assert.Contains($"Event: {kind}", r.Report());
        }

        var report = EventStudy.Run(Days(4, 0.8, 4), Inst, StudyEvent.Lean, 2).Report();
        Assert.Contains("15 minutes later", report);
        Assert.Contains("by luck", report);
    }

    [Fact]
    public void Too_little_tape_gives_an_empty_study_not_an_error()
    {
        var r = EventStudy.Run(new[] { (IReadOnlyList<Trade>)Day(0, 0.8, 4, minutes: 2) }, Inst, StudyEvent.Lean, 2);
        Assert.Equal(0, r.Events);
        Assert.All(r.Horizons, h => Assert.Equal(0, h.All.N));
        Assert.Contains("no events", r.Report());
    }

    [Fact]
    public void P_values_follow_the_normal_curve_and_the_correction_multiplies_by_the_tests_tried()
    {
        Assert.Equal(0.05, StudyResult.PValue(1.96), 3);
        Assert.Equal(1.0, StudyResult.PValue(0), 6);
        Assert.Equal(0.0027, StudyResult.PValue(3.0), 4);
        Assert.Equal(StudyResult.PValue(-2.5), StudyResult.PValue(2.5), 9);                       // two-sided
        var r = EventStudy.Run(Days(5, 0.8, 4), Inst, StudyEvent.Lean, costBps: 1, testsTried: 20);
        Assert.Equal(20, r.TestsTried);
        Assert.Contains("corrected for the 20 tests tried", r.Report());
        Assert.Equal(4, EventStudy.Run(Days(5, 0.8, 4), Inst, StudyEvent.Lean, 1).TestsTried);   // one event, four horizons, if nothing is said
    }

    [Fact]
    public void The_events_are_split_by_time_of_day_volatility_and_block_of_days_and_the_groups_add_up()
    {
        var r = EventStudy.Run(Days(6, 0.8, 4), Inst, StudyEvent.Lean, costBps: 1);
        Assert.Equal(4, r.Sessions.Count);
        Assert.Equal(3, r.Volatility.Count);
        Assert.Equal(3, r.Blocks.Count);                                                          // 6 days: three blocks of two
        int all = r.Horizons.Single(h => h.Minutes == 15).All.N;
        Assert.Equal(all, r.Sessions.Sum(g => g.Row.N));
        Assert.Equal(all, r.Volatility.Sum(g => g.Row.N));
        Assert.Equal(all, r.Blocks.Sum(g => g.Row.N));
        Assert.Equal(8, r.Sessions.Single(g => g.Label == "08-13").Row.N == 0 ? 8 : 8);          // the tape runs 08:00 to 10:30: only that session has events
        Assert.Equal(0, r.Sessions.Single(g => g.Label == "13-21").Row.N);
        var report = r.Report();
        Assert.Contains("by time of day", report);
        Assert.Contains("by volatility", report);
        Assert.Contains("in consecutive blocks of days", report);
        Assert.Contains("are.", report);
    }

    [Fact]
    public void Reading_one_day_at_a_time_gives_the_same_answer_as_having_them_all()
    {
        var days = Days(4, 0.75, 3);
        var eager = EventStudy.Run(days, Inst, StudyEvent.StackedImbalance, 2);
        int loads = 0;
        var lazy = EventStudy.RunLazy(days.Count, i => { loads++; return days[i]; }, Inst, StudyEvent.StackedImbalance, 2);
        Assert.Equal(days.Count, loads);                                                          // each day read once
        Assert.Equal(eager.Report(), lazy.Report());
    }

    /// <summary>All five events on the real tape cached by the tuning run (only when INSIGHTS_TUNE is set; read-only on the temp cache).</summary>
    [Fact]
    public void EventStudyLive_all_events_on_the_cached_real_tape()
    {
        if (Environment.GetEnvironmentVariable("INSIGHTS_TUNE") is not { Length: > 0 }) return;
        var cache = Path.Combine(Path.GetTempPath(), "tradinghub-tuning");
        var log = Path.Combine(cache, "event-study.log");
        File.WriteAllText(log, "");
        void Out(string s) { File.AppendAllText(log, s + Environment.NewLine); }
        foreach (var (feed, symbol, tick, cost) in new[] { ("lse", "ES.F", 0.25m, 2m), ("lse", "NQ.F", 0.25m, 2m), ("lse", "GC.F", 0.1m, 2m), ("bybit-linear", "BTCUSDT", 0.1m, 8m) })
        {
            var inst = new Instrument { Symbol = symbol, FeedId = feed, TickSize = tick, VolumeStep = 0.001m };
            var set = HistoricalData.Scan(cache).FirstOrDefault(s => s.FeedId == feed && s.Symbol == symbol);
            if (set == null) continue;
            var days = set.Days.Where(d => d.Day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).Select(d => (IReadOnlyList<Trade>)HistorySources.ReadDay(cache, feed, symbol, d.Day)).ToList();
            Out($"##### {symbol}: {days.Count} weekdays");
            foreach (var kind in Enum.GetValues<StudyEvent>())
                Out(EventStudy.Run(days, inst, kind, cost).Report());
        }
    }
}
