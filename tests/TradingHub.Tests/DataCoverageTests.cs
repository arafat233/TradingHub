using TradingHub.Data;

namespace TradingHub.Tests;

public class DataCoverageTests
{
    [Fact]
    public void Finds_missing_days_and_skips_saturdays_unless_24x7()
    {
        var have = new[] { new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 9) }; // Mon, Wed, next Mon
        Assert.Equal(new[] { 3, 5, 6, 7, 8 }, DataCoverage.MissingDays(have, true).Select(d => d.Day));
        Assert.Equal(new[] { 3, 5, 6, 8 }, DataCoverage.MissingDays(have, false).Select(d => d.Day)); // 7 March is a Saturday
    }
}
