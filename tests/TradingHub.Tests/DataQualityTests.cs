using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public class DataQualityTests
{
    private static readonly Instrument Es = new() { Symbol = "ES", FeedId = "test", TickSize = 0.25m };
    private static readonly DateOnly Day = new(2026, 3, 2);

    private static Trade T(long id, int sec, decimal price, decimal vol = 1) =>
        new(id, Day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddSeconds(sec), price, vol, TradeSide.Buy);

    [Fact]
    public void Finds_and_removes_bad_prints_duplicates_and_bad_volume()
    {
        var t = new List<Trade>();
        for (int i = 0; i < 40; i++) t.Add(T(i + 1, i, 5000 + (i % 3) * 0.25m));
        t[20] = T(21, 20, 9000);                      // spike
        t.Add(T(10, 41, 5000));                       // duplicate id
        t.Add(T(100, 42, 5000, 0));                   // zero volume
        t.Add(new Trade(101, Day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), 5000, 1, TradeSide.Buy)); // other day
        t.Add(T(102, 43, 5000.1m));                   // off the tick grid: reported, kept

        var r = DataQuality.Check(t, Es, Day);
        Assert.Equal(1, r.Spikes);
        Assert.Equal(1, r.Duplicates);
        Assert.Equal(1, r.BadVolume);
        Assert.Equal(1, r.WrongDay);
        Assert.Equal(1, r.OffGrid);
        Assert.Equal(4, r.Fixable);

        var clean = DataQuality.Clean(t, Es, Day);
        Assert.Equal(40, clean.Count);                // 40 - 1 spike + the off-grid one
        Assert.DoesNotContain(clean, x => x.Price > 6000);
    }

    [Fact]
    public void Reports_long_silences_as_gaps()
    {
        var t = new List<Trade>();
        for (int i = 0; i < 20; i++) t.Add(T(i + 1, i, 5000));
        for (int i = 0; i < 20; i++) t.Add(T(100 + i, 3 * 3600 + i, 5000));
        var r = DataQuality.Check(t, Es, Day);
        Assert.Single(r.Gaps);
        Assert.Equal(0, r.Fixable);
    }
}
