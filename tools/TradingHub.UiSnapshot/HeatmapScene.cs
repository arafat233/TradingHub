// "heatmap" scene: opens the liquidity heatmap on the simulator's book, lets it record for a while and captures the window.
using Avalonia.Headless;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Market;

internal static class HeatmapScene
{
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "OK " : "FAIL")}  {what}");
        if (!ok) Failures.Add(what);
    }

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        window.Navigate("trading");
        var tv = window.Trading;
        pump(2);
        var data = tv.ActiveTab?.Model?.Data;
        Check(data != null, "there is an active chart with data");
        if (data == null) return 1;

        // a short window so 30 seconds of recording fill the screen
        window.Services.Settings.Heatmap.WindowMinutes = 0.5;
        var day = RecordADay(data);
        tv.OpenHeatmap(tv.ActiveTab);
        var w = tv.HeatmapWindowOf(data);
        Check(w is { IsVisible: true }, $"the heatmap opens in its own window ({w?.Title})");
        pump(32);
        Check(data.DepthHistory.Count > 40, $"the book was recorded while the window was open ({data.DepthHistory.Count} snapshots)");
        w?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "heatmap.png"));

        // replay of the recorded day: the picture comes from the files, not from the clock
        if (w is HeatmapWindow hw)
        {
            hw.OpenReplay(day);
            hw.View.Model.Replay!.Seek(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(14).AddMinutes(20));
            hw.View.Model.Replay.Playing = false;
            for (int i = 0; i < 10 && hw.View.Model.History.Count == 0; i++) pump(1); // the day is read on a background thread
            Check(hw.View.Model.Replay is { Loading: false } && hw.View.Model.History.Count > 100, $"the recorded day loads ({hw.View.Model.History.Count} snapshots)");
            w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "heatmap-replay.png"));
            // bigger rows so the ladder shows its numbers, at the moment of the iceberg (buyers hit the same ask for 40 s)
            window.Services.Settings.Heatmap.RowPx = 13;
            hw.View.Model.Replay!.Seek(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(14).AddMinutes(17).AddSeconds(15));
            pump(2);
            w.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "heatmap-replay-numbers.png"));
            window.Services.Settings.Heatmap.RowPx = 6;
            hw.OpenReplay(day); // back to live is the first item
        }

        // a second click on the toolbar button does not open another one
        tv.OpenHeatmap(tv.ActiveTab);
        Check(ReferenceEquals(w, tv.HeatmapWindowOf(data)), "a second click brings the same window to the front");
        w?.Close();
        pump(1);
        Check(tv.HeatmapWindowOf(data) == null, "closing it forgets it (it can be opened again)");
        return Failures.Count;
    }

    /// <summary>Writes 40 minutes of a made-up recorded session (book every second, trades, a wall that comes and goes, an iceberg) next to the ticks.</summary>
    private static DateOnly RecordADay(InstrumentData data)
    {
        var inst = data.Instrument;
        var day = new DateOnly(2026, 9, 15);
        var t0 = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(14);
        var rnd = new Random(7);
        long mid = inst.ToTicks(100m) + 1000;
        long id = 1;
        using var depth = new DepthFile.Writer(DepthFile.DayPath(data.RecordingRoot!, inst.FeedId, inst.Symbol, day), inst);
        using var ticks = new TickFile.Writer(TickFile.DayPath(data.RecordingRoot!, inst.FeedId, inst.Symbol, day), inst);
        for (int s = 0; s < 40 * 60; s++)
        {
            var t = t0.AddSeconds(s);
            mid += rnd.Next(-1, 2) + (s % 600 < 300 ? 0 : 0);
            var b = new OrderBookBuilder();
            for (int k = 0; k < 20; k++)
            {
                b.Set(BookSide.Bid, mid - k, 3 + rnd.Next(8) + (k == 7 || k == 15 ? 60 : 0) + (s > 900 && k == 4 ? 120 : 0));
                b.Set(BookSide.Ask, mid + 1 + k, 3 + rnd.Next(8) + (k == 9 ? 70 : 0));
            }

            depth.Write(t, b.BuildUpdate(t, 100, true).Snapshot, 20);
            bool buy = rnd.Next(2) == 0;
            int n = s is > 1000 and < 1040 ? 4 : 1; // an iceberg: buyers keep hitting the same ask for 40 s
            for (int k = 0; k < n; k++)
                ticks.Write(new Trade(id++, t.AddMilliseconds(k * 200), inst.FromTicks(s is > 1000 and < 1040 ? mid + 1 : buy ? mid + 1 : mid), n > 1 ? 6 : 1 + rnd.Next(4), s is > 1000 and < 1040 || buy ? TradeSide.Buy : TradeSide.Sell));
        }

        return day;
    }
}
