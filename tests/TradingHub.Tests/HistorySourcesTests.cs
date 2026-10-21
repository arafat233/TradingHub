using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.Tests;

/// <summary>
/// The history of each source separately (Rithmic and Databento) and the combined one: Rithmic wins and Databento fills in the
/// minutes it is missing; "rithmic-solo" and "databento" are each one on its own.
/// </summary>
public class HistorySourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradinghub-sources-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Day1 = new(2026, 9, 14);
    private static readonly DateOnly Day2 = new(2026, 9, 15);
    private static readonly DateOnly Day3 = new(2026, 9, 16);

    public HistorySourcesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private static Instrument Nq(string feed) => new() { Symbol = "NQ", FeedId = feed, Exchange = "CME", TickSize = 0.25m, PointValue = 20 };

    /// <summary>One trade per minute between two hours (a different base price per source so they can be told apart).</summary>
    private void Write(string feed, string symbol, DateOnly day, int fromMinute, int toMinute, decimal basePrice, long firstId)
    {
        var trades = new List<Trade>();
        for (int m = fromMinute; m < toMinute; m++)
            trades.Add(new Trade(firstId + m, day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddMinutes(m).AddSeconds(10), basePrice + m * 0.25m, 1, TradeSide.Buy));
        HistoricalData.WriteDay(_root, new Instrument { Symbol = symbol, FeedId = feed, Exchange = "CME", TickSize = 0.25m, PointValue = 20 }, day, trades, replace: true);
    }

    [Fact]
    public void Combined_Rithmic_wins_and_Databento_fills_in_the_missing_minutes()
    {
        // Rithmic recorded from 10:00 to 10:30; Databento has from 9:00 to 12:00
        Write("rithmic", "NQ", Day1, 600, 630, 20_000m, 5_000);
        Write("databento", "NQ", Day1, 540, 720, 30_000m, 1);

        var combined = HistorySources.ReadDay(_root, "rithmic", "NQ", Day1);
        Assert.Equal(180, combined.Count); // 30 from Rithmic + 150 from Databento, with no repeated minutes
        var inRecording = combined.Where(t => t.Time.Hour == 10 && t.Time.Minute < 30).ToList();
        Assert.Equal(30, inRecording.Count);
        Assert.All(inRecording, t => Assert.True(t.Price < 30_000m)); // in those minutes, Rithmic's
        Assert.All(combined.Where(t => t.Time.Hour == 9 || t.Time.Hour == 11), t => Assert.True(t.Price >= 30_000m)); // outside, Databento

        // sorted by time, with ids that increase and do not repeat between sources
        for (int i = 1; i < combined.Count; i++)
        {
            Assert.True(combined[i].Time >= combined[i - 1].Time);
            Assert.True(combined[i].Id > combined[i - 1].Id);
        }
    }

    [Fact]
    public void Rithmic_only_and_Databento_only_do_not_mix()
    {
        Write("rithmic", "NQ", Day1, 600, 630, 20_000m, 5_000);
        Write("databento", "NQ", Day1, 540, 720, 30_000m, 1);
        Write("databento", "NQ", Day2, 540, 600, 30_000m, 1);

        Assert.Equal(30, HistorySources.ReadDay(_root, "rithmic-solo", "NQ", Day1).Count);
        Assert.Equal(180, HistorySources.ReadDay(_root, "databento", "NQ", Day1).Count);
        Assert.Empty(HistorySources.ReadDay(_root, "rithmic-solo", "NQ", Day2));
        Assert.Equal(60, HistorySources.ReadDay(_root, "rithmic", "NQ", Day2).Count); // the day Rithmic does not have, entirely from Databento

        Assert.True(HistoricalData.HasDay(_root, "rithmic", "NQ", Day2));
        Assert.False(HistoricalData.HasDay(_root, "rithmic-solo", "NQ", Day2));
        Assert.Equal(new[] { Day1, Day2 }, HistorySources.AvailableDays(_root, "rithmic", "NQ"));
        Assert.Equal(new[] { Day1 }, HistorySources.AvailableDays(_root, "rithmic-solo", "NQ"));
    }

    [Fact]
    public void What_belongs_to_other_connections_is_not_filled_in_from_Databento()
    {
        Write("binance-futures", "NQ", Day1, 0, 10, 100m, 1);
        Write("databento", "NQ", Day1, 0, 60, 30_000m, 1);
        Assert.Equal(10, HistorySources.ReadDay(_root, "binance-futures", "NQ", Day1).Count);
        Assert.Equal(new[] { "binance-futures" }, HistorySources.Folders("binance-futures"));
        Assert.Equal(new[] { "rithmic", "databento" }, HistorySources.Folders("rithmic"));
        Assert.Equal(new[] { "tradovate" }, HistorySources.Folders("tradovate-solo"));
    }

    [Fact]
    public void For_testing_the_three_histories_show_up_together()
    {
        Write("rithmic", "NQ", Day2, 600, 630, 20_000m, 1);
        Write("rithmic", "NQ", Day3, 600, 630, 20_000m, 1);
        Write("databento", "NQ", Day1, 0, 600, 30_000m, 1);
        Write("databento", "NQ", Day2, 0, 600, 30_000m, 1);
        Write("rithmic", "MNQ", Day1, 600, 610, 20_000m, 1);
        Write("databento", "ES", Day1, 600, 610, 6_000m, 1);

        var sets = HistorySources.Datasets(_root);
        var nq = sets.Where(s => s.Symbol == "NQ").ToList();
        Assert.Equal(new[] { "rithmic", "rithmic-solo", "databento" }, nq.Select(s => s.FeedId));
        Assert.True(nq[0].Combined);
        Assert.Equal(new[] { Day1, Day2, Day3 }, nq[0].Days.Select(d => d.Day));
        Assert.Equal(new[] { Day2, Day3 }, nq[1].Days.Select(d => d.Day));
        Assert.Equal(new[] { Day1, Day2 }, nq[2].Days.Select(d => d.Day));
        Assert.Equal("Rithmic + Databento", HistorySources.Describe(nq[0]));
        Assert.Equal("only Rithmic", HistorySources.Describe(nq[1]));
        Assert.Equal("Databento", HistorySources.Describe(nq[2]));

        // with a single source, a single entry (no "solo" one)
        Assert.Equal(new[] { "rithmic" }, sets.Where(s => s.Symbol == "MNQ").Select(s => s.FeedId));
        Assert.False(sets.Single(s => s.Symbol == "MNQ").Combined);
        Assert.Equal(new[] { "databento" }, sets.Where(s => s.Symbol == "ES").Select(s => s.FeedId));
    }

    [Fact]
    public void The_backtest_loads_the_history_that_was_chosen()
    {
        Write("rithmic", "NQ", Day1, 600, 630, 20_000m, 5_000);
        Write("databento", "NQ", Day1, 540, 720, 30_000m, 1);
        Write("databento", "NQ", Day2, 540, 600, 30_000m, 1);
        DateTime from = Day1.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), to = Day3.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

        Assert.Equal(240, TickData.Load(_root, Nq("rithmic"), from, to, null, CancellationToken.None).Count);
        Assert.Equal(30, TickData.Load(_root, Nq("rithmic-solo"), from, to, null, CancellationToken.None).Count);
        Assert.Equal(240, TickData.Load(_root, Nq("databento"), from, to, null, CancellationToken.None).Count);

        // and reading by range (replay, context) the same way
        Assert.Equal(240, HistoricalData.Read(_root, "rithmic", "NQ", from, to).Count());
        Assert.Equal(0.25m, HistorySources.StoredTickSize(_root, "rithmic-solo", "NQ"));
    }
}
