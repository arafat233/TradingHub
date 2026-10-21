using System.Globalization;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Long history in exchange candles (1, 5 or 15 minutes): it downloads in seconds even for years, is
/// cached per month and is turned into synthetic trades that exactly respect the OHLC,
/// the volume and the delta of each candle. The synthetic trades have a negative id: the candles that
/// are built get marked (<see cref="Candle.IsSynthetic"/>) so no footprint is invented.
/// </summary>
public static class KlineHistory
{
    /// <summary>Cache folder (the app sets it). With no folder nothing is stored.</summary>
    public static string? CacheRoot { get; set; }

    /// <summary>Base of the synthetic ids (always negative and increasing with time).</summary>
    public const long SyntheticIdBase = long.MinValue / 2;

    public static bool IsSynthetic(long tradeId) => tradeId < 0;

    /// <summary>Resolution based on the range's length: less memory when it spans years.</summary>
    public static int MinutesFor(TimeSpan range) => range.TotalDays <= 400 ? 1 : range.TotalDays <= 1500 ? 5 : 15;

    /// <summary>Approximate synthetic trades a range generates (to warn before loading).</summary>
    public static long EstimateTrades(TimeSpan range) => (long)(range.TotalMinutes / MinutesFor(range) * 4);

    private static long ToMs(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>Candles between two instants, using the cache for the months already closed.</summary>
    public static async Task<List<Kline>> LoadAsync(IKlineHistory source, Instrument inst, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        IProgress<double>? progress, CancellationToken ct)
    {
        var result = new List<Kline>();
        if (toUtc <= fromUtc) return result;

        // the simulator generates the history backwards from the current price: all at once and with no cache
        if (inst.FeedId == SimulatedFeed.FeedId || CacheRoot == null)
        {
            await source.LoadKlinesAsync(inst, fromUtc, toUtc, minutes, endPrice, batch =>
            {
                result.AddRange(batch);
                return !ct.IsCancellationRequested;
            }, progress, ct).ConfigureAwait(false);
            return result;
        }

        var months = new List<DateTime>();
        for (var m = new DateTime(fromUtc.Year, fromUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc); m < toUtc; m = m.AddMonths(1)) months.Add(m);
        long fromMs = ToMs(fromUtc), toMs = ToMs(toUtc);
        for (int i = 0; i < months.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var monthStart = months[i];
            var monthEnd = monthStart.AddMonths(1);
            // a month is considered closed (and stored) two days after it ends
            bool closed = monthEnd <= DateTime.UtcNow.AddDays(-2);
            int index = i;
            var sub = new Progress<double>(p => progress?.Report((index + Math.Clamp(p, 0, 1)) / months.Count));
            List<Kline> klines;
            if (closed && ReadCache(inst, monthStart, minutes, source.CacheTag) is { } cached)
            {
                klines = cached;
            }
            else
            {
                var from = closed ? monthStart : (fromUtc > monthStart ? fromUtc : monthStart);
                var to = closed ? monthEnd : (toUtc < monthEnd ? toUtc : monthEnd);
                klines = new List<Kline>();
                var target = klines;
                await source.LoadKlinesAsync(inst, from, to, minutes, endPrice, batch =>
                {
                    target.AddRange(batch);
                    return !ct.IsCancellationRequested;
                }, sub, ct).ConfigureAwait(false);
                if (closed && klines.Count > 0) WriteCache(inst, monthStart, minutes, klines, source.CacheTag);
            }

            foreach (var k in klines)
                if (k.OpenTimeMs >= fromMs && k.OpenTimeMs < toMs) result.Add(k);
            progress?.Report((i + 1) / (double)months.Count);
        }

        return result;
    }

    private static string CachePath(Instrument inst, DateTime month, int minutes, string tag)
    {
        static string Clean(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(CacheRoot!, Clean(inst.FeedId), Clean(inst.Symbol), $"{month:yyyy-MM}.{minutes}m{(tag.Length > 0 ? "." + tag : "")}.bin");
    }

    private static List<Kline>? ReadCache(Instrument inst, DateTime month, int minutes, string tag)
    {
        try
        {
            var path = CachePath(inst, month, minutes, tag);
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path));
            int n = r.ReadInt32();
            var list = new List<Kline>(n);
            for (int i = 0; i < n; i++)
            {
                long t = r.ReadInt64();
                decimal o = r.ReadDecimal(), h = r.ReadDecimal(), l = r.ReadDecimal(), c = r.ReadDecimal(), v = r.ReadDecimal(), b = r.ReadDecimal();
                bool hb = r.ReadBoolean();
                list.Add(new Kline(t, o, h, l, c, v, b, hb));
            }

            return list;
        }
        catch
        {
            return null; // corrupt cache: it is downloaded again
        }
    }

    private static void WriteCache(Instrument inst, DateTime month, int minutes, List<Kline> klines, string tag)
    {
        try
        {
            var path = CachePath(inst, month, minutes, tag);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(klines.Count);
                foreach (var k in klines)
                {
                    w.Write(k.OpenTimeMs);
                    w.Write(k.Open);
                    w.Write(k.High);
                    w.Write(k.Low);
                    w.Write(k.Close);
                    w.Write(k.Volume);
                    w.Write(k.BuyVolume);
                    w.Write(k.HasBuyVolume);
                }
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // no cache: next time it is downloaded again
        }
    }

    /// <summary>
    /// Turns candles into synthetic trades: four per candle (open, low/high, high/low and
    /// close), with the exact buy and sell volume. It only uses the candles that fall entirely inside
    /// one of the windows (the gaps with no real ticks).
    /// </summary>
    public static void Synthesize(IReadOnlyList<Kline> klines, Instrument inst, int minutes, IReadOnlyList<(DateTime From, DateTime To)> windows, List<Trade> output)
    {
        long step = minutes * 60_000L;
        long quarter = step / 4;
        int w = 0;
        var prints = new (decimal Vol, TradeSide Side)[4];
        var prices = new decimal[4];
        foreach (var k in klines)
        {
            if (k.Volume <= 0) continue;
            var open = FromMs(k.OpenTimeMs);
            var close = open.AddMilliseconds(step);
            while (w < windows.Count && windows[w].To <= open) w++;
            if (w >= windows.Count) break;
            if (open < windows[w].From || close > windows[w].To) continue;

            decimal buy = k.HasBuyVolume ? Math.Clamp(k.BuyVolume, 0, k.Volume) : k.Volume / 2;
            decimal sell = k.Volume - buy;
            bool up = k.Close >= k.Open;
            decimal o = inst.RoundPrice(k.Open), h = inst.RoundPrice(k.High), l = inst.RoundPrice(k.Low), c = inst.RoundPrice(k.Close);
            prices[0] = o;
            prices[1] = up ? l : h;
            prices[2] = up ? h : l;
            prices[3] = c;

            // buys at the open and the favorable extreme, sells at the other extreme and the close;
            // if one side has no volume, the other is spread over the four points (so the OHLC comes out exact)
            if (buy <= 0)
                for (int j = 0; j < 4; j++) prints[j] = (sell / 4, TradeSide.Sell);
            else if (sell <= 0)
                for (int j = 0; j < 4; j++) prints[j] = (buy / 4, TradeSide.Buy);
            else
            {
                prints[0] = (buy / 2, TradeSide.Buy);
                prints[1] = (sell / 2, TradeSide.Sell);
                prints[2] = (buy / 2, TradeSide.Buy);
                prints[3] = (sell / 2, TradeSide.Sell);
            }

            long baseId = SyntheticIdBase + k.OpenTimeMs * 4;
            for (int j = 0; j < 4; j++)
                output.Add(new Trade(baseId + j, open.AddMilliseconds(quarter * j), prices[j], prints[j].Vol, prints[j].Side));
        }
    }

    /// <summary>
    /// Intervals where there are real ticks (cuts of more than 3 minutes separate them), rounded to whole
    /// minutes so no candle overlaps real trades.
    /// </summary>
    public static List<(DateTime From, DateTime To)> Coverage(IReadOnlyList<Trade> real)
    {
        var res = new List<(DateTime, DateTime)>();
        if (real.Count == 0) return res;
        static DateTime Floor(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        var start = real[0].Time;
        var last = real[0].Time;
        for (int i = 1; i < real.Count; i++)
        {
            var t = real[i].Time;
            if (t - last > TimeSpan.FromMinutes(3))
            {
                res.Add((Floor(start), Floor(last).AddMinutes(1)));
                start = t;
            }

            last = t;
        }

        res.Add((Floor(start), Floor(last).AddMinutes(1)));
        return res;
    }

    /// <summary>Subtracts the covered stretches from the windows.</summary>
    public static List<(DateTime From, DateTime To)> Subtract(IReadOnlyList<(DateTime From, DateTime To)> windows, IReadOnlyList<(DateTime From, DateTime To)> covered)
    {
        var res = new List<(DateTime, DateTime)>();
        foreach (var (wf, wt) in windows)
        {
            var pieces = new List<(DateTime From, DateTime To)> { (wf, wt) };
            foreach (var (cf, ct) in covered)
            {
                var next = new List<(DateTime, DateTime)>();
                foreach (var (pf, pt) in pieces)
                {
                    if (ct <= pf || cf >= pt)
                    {
                        next.Add((pf, pt));
                        continue;
                    }

                    if (cf > pf) next.Add((pf, cf));
                    if (ct < pt) next.Add((ct, pt));
                }

                pieces = next;
            }

            res.AddRange(pieces.Where(p => p.To > p.From));
        }

        return res.OrderBy(p => p.Item1).ToList();
    }

    /// <summary>Merges two lists sorted by time.</summary>
    public static List<Trade> MergeByTime(List<Trade> a, List<Trade> b)
    {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var res = new List<Trade>(a.Count + b.Count);
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count) res.Add(a[i].Time <= b[j].Time ? a[i++] : b[j++]);
        while (i < a.Count) res.Add(a[i++]);
        while (j < b.Count) res.Add(b[j++]);
        return res;
    }

    /// <summary>Reads a candle CSV from the Binance public archives (with or without a header).</summary>
    public static List<Kline> ParseBinanceCsv(TextReader reader)
    {
        var list = new List<Kline>(45_000);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0 || !char.IsDigit(line[0])) continue;
            var f = line.Split(',');
            if (f.Length < 10) continue;
            long t = long.Parse(f[0], CultureInfo.InvariantCulture);
            if (t > 100_000_000_000_000) t /= 1000; // from 2025 spot comes in microseconds
            decimal D(int k) => decimal.Parse(f[k], NumberStyles.Float, CultureInfo.InvariantCulture);
            list.Add(new Kline(t, D(1), D(2), D(3), D(4), D(5), D(9), true));
        }

        return list;
    }
}
