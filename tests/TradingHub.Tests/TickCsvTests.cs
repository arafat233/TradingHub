using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public class TickCsvTests
{
    private static readonly Instrument Inst = new() { Symbol = "GC.F", FeedId = "lse", TickSize = 0.1m, VolumeStep = 1m };

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tickcsv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static long Us(DateTime t) => (t.Ticks - DateTime.UnixEpoch.Ticks) / 10;

    [Fact]
    public void Imports_real_volume_by_day_and_leaves_existing_days_alone()
    {
        string dir = Temp(), root = Path.Combine(dir, "ticks");
        var d1 = new DateTime(2026, 1, 5, 23, 59, 59, DateTimeKind.Utc);
        var lines = new[]
        {
            "\"unix_us\",\"price\",\"volume\"",
            $"{Us(d1)},3000,2", $"{Us(d1.AddMilliseconds(500))},3000.1,5", $"{Us(d1.AddSeconds(2))},3000,7", // the last one is on the next day
            "garbage",
        };
        string csv = Path.Combine(dir, "t.csv");
        File.WriteAllLines(csv, lines);

        var r = TickCsv.Import(csv, Inst, root, replace: false, null, CancellationToken.None);
        Assert.Equal((3L, 3L, 2, 0, 1), (r.Rows, r.Trades, r.DaysWritten, r.DaysSkipped, r.BadLines));
        var day1 = TickFile.Read(TickFile.DayPath(root, "lse", "GC.F", new DateOnly(2026, 1, 5)));
        Assert.Equal(new[] { 2m, 5m }, day1.Select(t => t.Volume));
        Assert.Equal(new[] { TradeSide.Buy, TradeSide.Buy }, day1.Select(t => t.Side));
        Assert.Equal(TradeSide.Sell, TickFile.Read(TickFile.DayPath(root, "lse", "GC.F", new DateOnly(2026, 1, 6)))[0].Side); // 3000.1 -> 3000

        // a second import changes nothing: both days exist
        var again = TickCsv.Import(csv, Inst, root, replace: false, null, CancellationToken.None);
        Assert.Equal((0, 2), (again.DaysWritten, again.DaysSkipped));
    }

    [Fact]
    public void Refuses_when_the_instrument_is_being_recorded()
    {
        string dir = Temp(), root = Path.Combine(dir, "ticks");
        File.WriteAllText(Path.Combine(dir, "t.csv"), "1,1,1");
        using var held = TickFile.TryLock(root, "lse", "GC.F");
        Assert.NotNull(held);
        Assert.Throws<IOException>(() => TickCsv.Import(Path.Combine(dir, "t.csv"), Inst, root, false, null, CancellationToken.None));
    }

    /// <summary>One-off: <c>$env:TICKCSV_IMPORT="file.csv"; $env:TICKCSV_ROOT="...\Ticks"</c> imports a tick CSV into the real store as lse/GC.F (the app must be closed).</summary>
    [Fact]
    public void Import_into_the_real_store_when_asked()
    {
        string? file = Environment.GetEnvironmentVariable("TICKCSV_IMPORT"), root = Environment.GetEnvironmentVariable("TICKCSV_ROOT");
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(root)) return;
        var r = TickCsv.Import(file, Inst, root, replace: false, null, CancellationToken.None);
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "tickcsv-result.txt"), r.ToString());
    }
}

public class ThinDaysTests
{
    [Fact]
    public void Flags_days_far_below_the_usual_size_but_not_today_or_a_normal_short_weekday()
    {
        var days = new List<(DateOnly, long)>();
        var mon = new DateOnly(2026, 9, 7); // a Monday
        for (int w = 0; w < 5; w++) days.Add((mon.AddDays(7 * w), 1000)); // five normal Mondays
        days.Add((mon.AddDays(7 * 5), 100)); // a Monday with only 10%
        days.Add((mon.AddDays(7 * 5 + 6), 150)); // the only Sunday: compared with all days, flagged too
        var today = mon.AddDays(7 * 6);
        days.Add((today, 5)); // being recorded now
        var thin = DataCoverage.ThinDays(days, today);
        Assert.Equal(new[] { mon.AddDays(35), mon.AddDays(41) }, thin.Keys.OrderBy(d => d));
        Assert.Equal(0.1, thin[mon.AddDays(35)], 2);
        Assert.DoesNotContain(today, thin.Keys);
    }
}

public class TickStoreCheck
{
    /// <summary>One-off, read-only: <c>$env:TICKCHECK_DIR="...\Ticks\lse\GC.F"; $env:TICKCHECK_OUT="report.txt"</c> checks every day file of an instrument.</summary>
    [Fact]
    public void Real_store_reads_back_clean()
    {
        string? dir = Environment.GetEnvironmentVariable("TICKCHECK_DIR"), outFile = Environment.GetEnvironmentVariable("TICKCHECK_OUT");
        if (dir == null || outFile == null) return;
        var report = new List<string>();
        int bad = 0;
        long total = 0;
        foreach (var f in Directory.GetFiles(dir, "*.vtk").OrderBy(x => x))
        {
            var t = TradingHub.Data.TickFile.Read(f, out int dropped);
            total += t.Count;
            string name = Path.GetFileNameWithoutExtension(f);
            int unordered = 0, noVol = 0, noSide = 0;
            decimal lo = decimal.MaxValue, hi = 0;
            for (int i = 0; i < t.Count; i++)
            {
                if (i > 0 && t[i].Id <= t[i - 1].Id) unordered++;
                if (t[i].Volume <= 0) noVol++;
                if (t[i].Side == TradingHub.Market.TradeSide.None) noSide++;
                lo = Math.Min(lo, t[i].Price);
                hi = Math.Max(hi, t[i].Price);
            }

            bool dayDiffers = t.Count > 0 && t[0].Time.ToString("yyyy-MM-dd") != name && t[^1].Time.ToString("yyyy-MM-dd") != name;
            if (dropped > 0 || unordered > 0 || noVol > 0 || t.Count == 0 || lo < 500 || hi > 20000 || dayDiffers) bad++;
            report.Add($"{name} n={t.Count} dropped={dropped} unordered={unordered} novol={noVol} noside={noSide} range={lo}-{hi}{(dayDiffers ? " DAY-MISMATCH" : "")}");
        }

        report.Insert(0, $"files={report.Count} ticks={total} suspicious={bad}");
        File.WriteAllLines(outFile, report);
    }
}
