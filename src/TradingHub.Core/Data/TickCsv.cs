using System.Globalization;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>What a tick-CSV import did.</summary>
public sealed record TickCsvResult(long Rows, long Trades, int DaysWritten, int DaysSkipped, int BadLines);

/// <summary>
/// Turns a plain tick CSV (<c>unix_us,price,volume</c>, UTC, oldest first) into the app's day files. Unlike <see cref="CsvImport"/> the volume is real, and
/// the ids and the side are made the same way as the live and exported ticks of the same feed (<see cref="HistoryBuilder"/>), so a day that is later
/// filled from another source de-duplicates. Days that already exist are left alone unless <c>replace</c> is set.
/// </summary>
public static class TickCsv
{
    public static TickCsvResult Import(string path, Instrument inst, string root, bool replace, IProgress<double>? progress, CancellationToken ct)
    {
        // only one process may write an instrument's files (the live recorder holds the same lock)
        using var lockFile = TickFile.TryLock(root, inst.FeedId, inst.Symbol)
            ?? throw new IOException($"{inst.Symbol} is being recorded by the app right now: close its charts (or the app) and import again.");
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        long rows = 0, trades = 0;
        int written = 0, skipped = 0, bad = 0;
        var day = new List<Trade>();
        DateOnly? current = null;
        long lastId = 0;
        decimal lastPrice = 0;
        var lastSide = TradeSide.Buy;

        void Flush()
        {
            if (current is not { } d || day.Count == 0) return;
            if (!replace && File.Exists(TickFile.DayPath(root, inst.FeedId, inst.Symbol, d))) skipped++;
            else
            {
                HistoricalData.WriteDay(root, inst, d, day, replace: true);
                trades += day.Count;
                written++;
            }

            day.Clear();
        }

        string? line = reader.ReadLine();
        if (line != null && line.Length > 0 && (char.IsLetter(line[0]) || line[0] == '"')) line = reader.ReadLine(); // header
        long total = Math.Max(1, stream.Length);
        for (; line != null; line = reader.ReadLine())
        {
            ct.ThrowIfCancellationRequested();
            var c = line.Split(',');
            if (c.Length < 3 || !long.TryParse(c[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long us)
                || !decimal.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var price) || price <= 0
                || !decimal.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vol))
            {
                bad++;
                continue;
            }

            var time = DateTime.UnixEpoch.AddTicks(us * 10);
            var d = DateOnly.FromDateTime(time);
            if (current != d)
            {
                Flush();
                current = d;
                lastId = 0; // ids are time based: a new day cannot collide with the one before
            }

            rows++;
            price = inst.RoundPrice(price);
            day.Add(new Trade(HistoryBuilder.TimeId(time, ref lastId), time, price, vol > 0 ? vol : 1, HistoryBuilder.TickRule(price, ref lastPrice, ref lastSide)));
            if ((rows & 0xFFFF) == 0) progress?.Report(Math.Clamp(stream.Position / (double)total, 0, 1));
        }

        Flush();
        progress?.Report(1);
        return new TickCsvResult(rows, trades, written, skipped, bad);
    }
}
