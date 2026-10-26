using TradingHub.Data;

namespace TradingHub.Tests;

public class AuctionAnalysisTests
{
    private static int[] Letters(string s) => s.Select(c => c - 'A').ToArray();

    /// <summary>The profile drawn in the video, bottom row first (letters = the 30-minute periods that traded there).</summary>
    private static AuctionInput VideoProfile()
    {
        string[] letters = { "DEF", "CDEF", "BCDEF", "ABCDEFG", "ABCDEFGH", "ABCDEFGH", "ABCFGH", "ABGH", "BGHI", "HI", "I", "I", "I", "IJK", "IJK", "IJK", "K", "K" };
        decimal[] volume = { 2, 3, 6, 9, 12, 11, 10, 8, 8, 16, 1, 1, 1, 4, 6, 5, 1, 1 };
        var rows = Enumerable.Range(0, letters.Length).Select(i => 5002m + i).ToArray();
        return new AuctionInput(rows, letters.Select(Letters).ToArray(), volume, 1m);
    }

    [Fact]
    public void Reads_the_tail_poor_low_single_prints_initial_balance_nodes_and_both_pocs_of_the_video_profile()
    {
        var p = VideoProfile();
        var r = AuctionAnalysis.Analyze(p);
        Assert.Equal(new Zone(5019m, 5019m + 0), new Zone(r.TailHigh!.Low + 1, r.TailHigh.High)); // the two K rows: 5018 and 5019
        Assert.Equal(5018m, r.TailHigh!.Low);
        Assert.False(r.PoorHigh);
        Assert.True(r.PoorLow);                                    // DEF: three letters, no tail
        Assert.Contains(r.SinglePrints, z => z.Low == 5012m && z.High == 5014m); // the three rows with only an I
        Assert.True(r.ExtendedUp);                                 // price went well above the first hour's range
        Assert.Equal(5010m, r.IbHigh);                             // the BGHI row is the highest one traded in A or B
        Assert.Contains(r.LowVolumeNodes, z => z.Low == 5012m && z.High == 5014m);
        Assert.NotEmpty(r.HighVolumeNodes);
        Assert.Equal(5011m, r.VolumePoc);                          // the HI row has the most contracts
        Assert.False(r.PocsAligned);                               // the TPO POC is lower: time here, money there
        Assert.Equal(r.FairValue.High, r.VolumePoc);
        Assert.Equal(RowCase.Rush, r.Cases[11]);                   // one letter and almost no volume
        Assert.Equal(RowCase.BigSpender, r.Cases[9]);              // one or two letters but the most contracts
        Assert.Equal(RowCase.Regulars, r.Cases[4]);                // most letters and most volume
    }

    [Fact]
    public void Classifies_the_shape_of_the_day()
    {
        AuctionInput Make(params string[] lettersBottomFirst) => new(Enumerable.Range(0, lettersBottomFirst.Length).Select(i => 100m + i).ToArray(), lettersBottomFirst.Select(Letters).ToArray(), lettersBottomFirst.Select(l => (decimal)l.Length).ToArray(), 1m);
        // fat in the middle
        Assert.Equal("D", AuctionAnalysis.Analyze(Make("AB", "ABC", "ABCDE", "ABCDEFG", "ABCDEFGH", "ABCDEFG", "ABCDE", "ABC", "AB")).Shape);
        // thick at the top, a thin tail below
        Assert.Equal("P", AuctionAnalysis.Analyze(Make("A", "AB", "ABC", "ABCD", "ABCDE", "ABCDEF", "ABCDEFGHI", "ABCDEFGH")).Shape);
        // thick at the bottom
        Assert.Equal("b", AuctionAnalysis.Analyze(Make("ABCDEF", "ABCDEFGHI", "ABCDEFGH", "ABCDEFG", "ABCD", "ABC", "AB", "A")).Shape);
        // a long thin profile: one or two letters per row over a range much bigger than the first hour's
        var thin = AuctionAnalysis.Analyze(Make("A", "AB", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N"));
        Assert.Equal("Trend", thin.Shape);
    }

    [Fact]
    public void Acceptance_needs_two_periods_beyond_the_level_and_volume_building_there()
    {
        var level = 100m;
        // two periods closing above 100 and 20% of the session's volume traded above it: accepted
        Assert.Equal("accepted", Acceptance.Evaluate(true, level, new[] { (99m, 0m), (101m, 100m), (102m, 150m) }, 1250m).State);
        // the same time above but almost no volume there: drifting, not accepted
        Assert.Equal("drifting", Acceptance.Evaluate(true, level, new[] { (99m, 0m), (101m, 10m), (102m, 15m) }, 1250m).State);
        // one period beyond: still testing
        Assert.Equal("testing", Acceptance.Evaluate(true, level, new[] { (99m, 0m), (101m, 100m) }, 500m).State);
        // it went above and came back inside: rejected; it never left: inside
        Assert.Equal("rejected", Acceptance.Evaluate(true, level, new[] { (101m, 100m), (99m, 0m) }, 500m).State);
        Assert.Equal("inside", Acceptance.Evaluate(true, level, new[] { (98m, 0m), (99m, 0m) }, 500m).State);
        // below value works the other way round
        Assert.Equal("accepted", Acceptance.Evaluate(false, level, new[] { (99m, 100m), (98m, 100m) }, 600m).State);
    }
}
