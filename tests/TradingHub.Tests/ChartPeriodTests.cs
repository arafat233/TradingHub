using TradingHub.Data;

namespace TradingHub.Tests;

/// <summary>The periods written by hand (in the backtester, the lab or a chart).</summary>
public class ChartPeriodTests
{
    [Theory]
    [InlineData("M5", PeriodType.Minutes, 5)]
    [InlineData("m5", PeriodType.Minutes, 5)]
    [InlineData("5m", PeriodType.Minutes, 5)]
    [InlineData("H1", PeriodType.Hours, 1)]
    [InlineData("h4", PeriodType.Hours, 4)]
    [InlineData("1h", PeriodType.Hours, 1)]
    [InlineData("D1", PeriodType.Days, 1)]
    [InlineData("d1", PeriodType.Days, 1)]
    [InlineData("D", PeriodType.Days, 1)]
    [InlineData("1D", PeriodType.Days, 1)]
    [InlineData("Daily", PeriodType.Days, 1)]
    [InlineData("diario", PeriodType.Days, 1)]
    [InlineData("2D", PeriodType.Days, 2)]
    [InlineData("W1", PeriodType.Weeks, 1)]
    [InlineData("semanal", PeriodType.Weeks, 1)]
    [InlineData("Monthly", PeriodType.Months, 1)]
    [InlineData("Mo2", PeriodType.Months, 2)]
    [InlineData("2mo", PeriodType.Months, 2)]
    [InlineData("S30", PeriodType.Seconds, 30)]
    [InlineData("30s", PeriodType.Seconds, 30)]
    [InlineData("Tick500", PeriodType.Tick, 500)]
    [InlineData("tick500", PeriodType.Tick, 500)]
    [InlineData("Volume1000", PeriodType.Volume, 1000)]
    [InlineData("Range20", PeriodType.Range, 20)]
    [InlineData("Delta300", PeriodType.Delta, 300)]
    [InlineData("Renko10", PeriodType.Renko, 10)]
    [InlineData(" D1 ", PeriodType.Days, 1)]
    public void It_is_understood_however_it_is_written(string code, PeriodType type, decimal value)
    {
        Assert.True(ChartPeriod.TryParse(code, out var p), code);
        Assert.Equal(new ChartPeriod(type, value), p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("X5")]
    [InlineData("M0")]
    [InlineData("ten minutes")]
    public void What_is_not_a_period_does_not_pass(string code) => Assert.False(ChartPeriod.TryParse(code, out _));

    [Fact]
    public void The_stored_codes_read_back_the_same()
    {
        foreach (var p in new[] { ChartPeriod.M1, ChartPeriod.H4, ChartPeriod.D1, new ChartPeriod(PeriodType.Weeks, 1), new ChartPeriod(PeriodType.Months, 3), new ChartPeriod(PeriodType.Tick, 233) })
        {
            Assert.True(ChartPeriod.TryParse(p.Code, out var back));
            Assert.Equal(p, back);
            Assert.True(ChartPeriod.TryParse(p.DisplayName, out var shown) || p.Type is PeriodType.Weeks or PeriodType.Months or PeriodType.Tick, p.DisplayName);
            if (p.Type is PeriodType.Minutes or PeriodType.Hours or PeriodType.Days) Assert.Equal(p, shown);
        }
    }
}
