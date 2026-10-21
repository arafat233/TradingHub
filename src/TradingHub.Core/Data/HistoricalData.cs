using System.Globalization;
using System.IO.Compression;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>One day of trades stored on disk.</summary>
public sealed record DayData(DateOnly Day, string Path, long Bytes);

/// <summary>Everything stored for an instrument.</summary>
public sealed record SymbolData(string FeedId, string Symbol, IReadOnlyList<DayData> Days)
{
    /// <summary>History of a futures connection combined with Databento's (see <see cref="HistorySources"/>).</summary>
    public bool Combined { get; init; }

    public long Bytes => Days.Sum(d => d.Bytes);
    public DateOnly First => Days[0].Day;
    public DateOnly Last => Days[^1].Day;
}

/// <summary>
/// Historical data on disk ({root}/{feed}/{symbol}/{day}.vtk): index, reading by range,
/// writing whole days and deleting. The chart, the Data section, the replay and the backtester use it.
/// </summary>
public static class HistoricalData
{
    public static List<SymbolData> Scan(string root)
    {
        var result = new List<SymbolData>();
        if (!Directory.Exists(root)) return result;
        foreach (var feedDir in Directory.EnumerateDirectories(root))
        {
            foreach (var symDir in Directory.EnumerateDirectories(feedDir))
            {
                var days = new List<DayData>();
                foreach (var f in Directory.EnumerateFiles(symDir, "*.vtk"))
                {
                    if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) continue;
                    days.Add(new DayData(d, f, new FileInfo(f).Length));
                }

                if (days.Count == 0) continue;
                days.Sort((a, b) => a.Day.CompareTo(b.Day));
                result.Add(new SymbolData(Path.GetFileName(feedDir), Path.GetFileName(symDir), days));
            }
        }

        result.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return result;
    }

    /// <summary>Are there trades stored that day? For "rithmic" it counts Databento's too (see <see cref="HistorySources"/>).</summary>
    public static bool HasDay(string root, string feedId, string symbol, DateOnly day) => HistorySources.HasDay(root, feedId, symbol, day);

    public static void DeleteDays(string root, string feedId, string symbol, IEnumerable<DateOnly> days)
    {
        foreach (var d in days)
        {
            var p = TickFile.DayPath(root, feedId, symbol, d);
            if (File.Exists(p)) File.Delete(p);
        }

        var dir = Path.GetDirectoryName(TickFile.DayPath(root, feedId, symbol, DateOnly.MinValue))!;
        if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
    }

    /// <summary>Trades between two instants (UTC), sorted, read day by day (from the <see cref="HistorySources"/> sources).</summary>
    public static IEnumerable<Trade> Read(string root, string feedId, string symbol, DateTime fromUtc, DateTime toUtc)
    {
        for (var d = DateOnly.FromDateTime(fromUtc); d <= DateOnly.FromDateTime(toUtc); d = d.AddDays(1))
        {
            var list = HistorySources.ReadDay(root, feedId, symbol, d);
            foreach (var t in list)
            {
                if (t.Time < fromUtc) continue;
                if (t.Time >= toUtc) yield break;
                yield return t;
            }
        }
    }

    /// <summary>Stores a whole day (it is merged with whatever was there, without duplicating).</summary>
    /// <param name="replace">The downloaded day is complete: it replaces what was recorded live instead of merging it (it does not duplicate).</param>
    public static void WriteDay(string root, Instrument instrument, DateOnly day, IReadOnlyList<Trade> trades, bool replace = false)
    {
        var path = TickFile.DayPath(root, instrument.FeedId, instrument.Symbol, day);
        var all = new List<Trade>(trades.Count);
        if (!replace && File.Exists(path)) all.AddRange(TickFile.Read(path));
        all.AddRange(trades);
        all = TickFile.SortAndDedupe(all);
        string tmp = path + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        using (var w = new TickFile.Writer(tmp, instrument))
        {
            foreach (var t in all)
                if (DateOnly.FromDateTime(t.Time) == day) w.Write(t);
        }

        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Downloads whole days from the exchanges' public archives (much faster than the API):
/// Binance (data.binance.vision, daily aggTrades in zip) and Bybit (public.bybit.com, csv.gz).
/// </summary>
public static class PublicArchives
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    static PublicArchives() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingHub/0.2");

    public static bool Supports(string feedId) => feedId is "binance-futures" or "binance-spot" or "bybit-linear";

    public static string? DailyUrl(string feedId, string symbol, DateOnly day)
    {
        string d = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return feedId switch
        {
            "binance-futures" => $"https://data.binance.vision/data/futures/um/daily/aggTrades/{symbol}/{symbol}-aggTrades-{d}.zip",
            "binance-spot" => $"https://data.binance.vision/data/spot/daily/aggTrades/{symbol}/{symbol}-aggTrades-{d}.zip",
            "bybit-linear" => $"https://public.bybit.com/trading/{symbol}/{symbol}{d}.csv.gz",
            _ => null,
        };
    }

    /// <summary>Downloads a day. Returns null if the file is not published yet (404).</summary>
    public static async Task<List<Trade>?> DownloadDayAsync(Instrument instrument, DateOnly day, IProgress<double>? progress, CancellationToken ct)
    {
        var url = DailyUrl(instrument.FeedId, instrument.Symbol, day) ?? throw new NotSupportedException("This connection has no public files.");
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0;

        // downloaded into memory with progress
        var ms = new MemoryStream(total > 0 ? (int)Math.Min(total, int.MaxValue) : 1 << 20);
        await using (var net = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = await net.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                ms.Write(buf, 0, n);
                if (total > 0) progress?.Report(0.8 * ms.Length / total);
            }
        }

        ms.Position = 0;
        var trades = instrument.FeedId == "bybit-linear" ? ParseBybit(ms) : ParseBinance(ms);
        progress?.Report(1);
        return trades;
    }

    private static List<Trade> ParseBinance(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = zip.Entries.First(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
        using var reader = new StreamReader(entry.Open());
        var list = new List<Trade>(1 << 18);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || !char.IsDigit(line[0])) continue; // header
            // agg_id, price, quantity, first_id, last_id, time, buyer_is_maker[, best_price]
            var span = line.AsSpan();
            long id = long.Parse(Next(ref span), CultureInfo.InvariantCulture);
            decimal price = decimal.Parse(Next(ref span), NumberStyles.Float, CultureInfo.InvariantCulture);
            decimal qty = decimal.Parse(Next(ref span), NumberStyles.Float, CultureInfo.InvariantCulture);
            Next(ref span);
            Next(ref span);
            long ts = long.Parse(Next(ref span), CultureInfo.InvariantCulture);
            var makerSpan = Next(ref span);
            bool buyerIsMaker = makerSpan.Equals("true", StringComparison.OrdinalIgnoreCase) || makerSpan.Equals("True", StringComparison.Ordinal);
            // spot publishes microseconds from 2025
            var time = ts > 100_000_000_000_000 ? DateTime.UnixEpoch.AddTicks(ts * 10) : DateTime.UnixEpoch.AddMilliseconds(ts);
            list.Add(new Trade(id, time, price, qty, buyerIsMaker ? TradeSide.Sell : TradeSide.Buy));
        }

        return list;
    }

    private static List<Trade> ParseBybit(Stream gzStream)
    {
        using var gz = new GZipStream(gzStream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        var list = new List<Trade>(1 << 18);
        string? line;
        long lastMs = 0;
        int seq = 0;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || !char.IsDigit(line[0])) continue;
            // timestamp(s), symbol, side, size, price, ...
            var span = line.AsSpan();
            double secs = double.Parse(Next(ref span), CultureInfo.InvariantCulture);
            Next(ref span);
            var side = Next(ref span);
            decimal size = decimal.Parse(Next(ref span), NumberStyles.Float, CultureInfo.InvariantCulture);
            decimal price = decimal.Parse(Next(ref span), NumberStyles.Float, CultureInfo.InvariantCulture);
            long ms = (long)Math.Round(secs * 1000);
            seq = ms == lastMs ? seq + 1 : 0;
            lastMs = ms;
            list.Add(new Trade(ms * 1000 + seq, DateTime.UnixEpoch.AddMilliseconds(ms), price, size, side.SequenceEqual("Buy") ? TradeSide.Buy : TradeSide.Sell));
        }

        // the Bybit file comes from the newest to the oldest on some days
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        return list;
    }

    private static ReadOnlySpan<char> Next(ref ReadOnlySpan<char> span)
    {
        int i = span.IndexOf(',');
        ReadOnlySpan<char> field;
        if (i < 0)
        {
            field = span;
            span = ReadOnlySpan<char>.Empty;
        }
        else
        {
            field = span[..i];
            span = span[(i + 1)..];
        }

        return field.Trim();
    }
}
