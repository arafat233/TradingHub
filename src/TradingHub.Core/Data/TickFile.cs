using System.Globalization;
using System.Text;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Compact binary format for storing trades (~6 bytes per tick).
/// One file per instrument and UTC day: {root}/{feed}/{symbol}/{yyyy-MM-dd}.vtk
///
/// Record: flags byte (bits 0-1 side, bit 2 keyframe, bits 3-7 volume scale),
/// then varints: id, time (Unix ms), price (ticks, zigzag), volume mantissa.
/// In the records that are not keyframes, id/time/price go as differences from the previous one.
/// </summary>
public static class TickFile
{
    private static readonly byte[] Magic = "VTK1"u8.ToArray();

    public static string DayPath(string root, string feedId, string symbol, DateOnly day) =>
        Path.Combine(root, Sanitize(feedId), Sanitize(symbol), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".vtk");

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    public static IEnumerable<DateOnly> AvailableDays(string root, string feedId, string symbol)
    {
        var dir = Path.Combine(root, Sanitize(feedId), Sanitize(symbol));
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.vtk").OrderBy(x => x))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                yield return d;
        }
    }

    /// <summary>Writer in append mode (thread-safe per instance with an external lock).</summary>
    public sealed class Writer : IDisposable
    {
        private readonly FileStream _fs;
        private readonly BufferedStream _bs;
        private readonly Instrument _instrument;
        private bool _hasPrev;
        private long _prevId, _prevMs, _prevPrice;
        private int _sinceKey;

        public Writer(string path, Instrument instrument)
        {
            _instrument = instrument;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool exists = File.Exists(path) && new FileInfo(path).Length > 0;
            _fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _bs = new BufferedStream(_fs, 1 << 16);
            if (!exists)
            {
                _bs.Write(Magic);
                var ts = Encoding.UTF8.GetBytes(instrument.TickSize.ToString(CultureInfo.InvariantCulture));
                _bs.WriteByte((byte)ts.Length);
                _bs.Write(ts);
            }
        }

        public void Write(in Trade t)
        {
            long ms = new DateTimeOffset(t.Time).ToUnixTimeMilliseconds();
            long price = _instrument.ToTicks(t.Price);
            var (mantissa, scale) = SplitDecimal(t.Volume);
            bool key = !_hasPrev || ms < _prevMs || _sinceKey >= 4096 || t.Id < _prevId;
            byte flags = (byte)(((byte)t.Side & 0x3) | (key ? 0x4 : 0) | ((scale & 0x1F) << 3));
            Span<byte> buf = stackalloc byte[48];
            int n = 0;
            buf[n++] = flags;
            if (key)
            {
                n += WriteVarint(buf[n..], (ulong)t.Id);
                n += WriteVarint(buf[n..], (ulong)ms);
                n += WriteVarint(buf[n..], ZigZag(price));
                _sinceKey = 0;
            }
            else
            {
                n += WriteVarint(buf[n..], ZigZag(t.Id - _prevId));
                n += WriteVarint(buf[n..], (ulong)(ms - _prevMs));
                n += WriteVarint(buf[n..], ZigZag(price - _prevPrice));
                _sinceKey++;
            }

            n += WriteVarint(buf[n..], mantissa);
            _bs.Write(buf[..n]);
            _hasPrev = true;
            _prevId = t.Id;
            _prevMs = ms;
            _prevPrice = price;
        }

        public void Flush()
        {
            _bs.Flush();
            _fs.Flush();
        }

        public void Dispose()
        {
            _bs.Flush();
            _bs.Dispose();
            _fs.Dispose();
        }
    }

    /// <summary>Tick size stored in the file's header (null if it cannot be read).</summary>
    public static decimal? ReadTickSize(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4 || !magic.SequenceEqual(Magic)) return null;
            int len = fs.ReadByte();
            if (len <= 0) return null;
            var buf = new byte[len];
            fs.ReadExactly(buf);
            return decimal.Parse(Encoding.UTF8.GetString(buf), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads a whole file. Returns the trades in the stored order, without the corrupt stretches.</summary>
    public static List<Trade> Read(string path) => Read(path, out _);

    /// <summary>
    /// Reads a whole file and discards the corrupt stretches (for example if two processes recorded the same
    /// file at once and the records got mixed): times outside the file's day, ids that go backwards inside
    /// a stretch, prices that jump suddenly. It discards from the keyframe before the first impossible record
    /// to the keyframe after the last one, so no "plausible" junk is left in the middle.
    /// Whatever is missing is requested from the exchange again on loading (the gaps fill themselves in).
    /// </summary>
    public static List<Trade> Read(string path, out int dropped)
    {
        dropped = 0;
        var result = new List<Trade>();
        if (!File.Exists(path)) return result;
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            data = new byte[fs.Length];
            int read = 0;
            while (read < data.Length)
            {
                int r = fs.Read(data, read, data.Length - read);
                if (r <= 0) break;
                read += r;
            }
        }

        if (data.Length < 5 || !data.AsSpan(0, 4).SequenceEqual(Magic)) return result;
        int pos = 4;
        int tsLen = data[pos++];
        if (pos + tsLen > data.Length) return result;
        var tickSize = decimal.Parse(Encoding.UTF8.GetString(data, pos, tsLen), CultureInfo.InvariantCulture);
        pos += tsLen;

        // the file belongs to one UTC day: a time outside that day is junk
        long minMs = long.MinValue, maxMs = long.MaxValue;
        if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            minMs = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeMilliseconds() - 60_000;
            maxMs = minMs + 86_400_000 + 120_000;
        }

        var keys = new List<int>();
        List<int>? bad = null;
        long prevId = 0, prevMs = 0, prevPrice = 0;
        long firstId = -1; // the file's first record is always a healthy keyframe
        try
        {
            while (pos < data.Length)
            {
                byte flags = data[pos++];
                int sideBits = flags & 0x3;
                bool key = (flags & 0x4) != 0;
                int scale = (flags >> 3) & 0x1F;
                long id, ms, price, dId = 1, dPrice = 0;
                if (key)
                {
                    id = (long)ReadVarint(data, ref pos);
                    ms = (long)ReadVarint(data, ref pos);
                    price = UnZigZag(ReadVarint(data, ref pos));
                }
                else
                {
                    dId = UnZigZag(ReadVarint(data, ref pos));
                    id = prevId + dId;
                    ms = prevMs + (long)ReadVarint(data, ref pos);
                    dPrice = UnZigZag(ReadVarint(data, ref pos));
                    price = prevPrice + dPrice;
                }

                ulong mant = ReadVarint(data, ref pos);
                if (pos > data.Length) break; // truncated record (power cut, etc.)

                if (firstId < 0) firstId = id;

                // a lone writer never leaves these records: if they show up, the file is mixed.
                // A keyframe with an id of another scale is a record read from the middle (a truncated varint).
                bool ok = sideBits != 3 && scale <= 28 && price > 0 && ms >= minMs && ms < maxMs
                          && (!key || firstId < 1L << 28 || (id > firstId / 64 && id / 64 < firstId))
                          && (key || dId > 0 || (dId == 0 && dPrice == 0))
                          && (key || prevPrice <= 0 || Math.Abs(dPrice) <= Math.Max(10, prevPrice / 33)); // jump > 3 % between two consecutive trades
                if (key) keys.Add(result.Count);
                if (ok)
                {
                    var vol = new decimal((int)(mant & 0xFFFFFFFF), (int)(mant >> 32), 0, false, (byte)scale);
                    result.Add(new Trade(id, DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, price * tickSize, vol, (TradeSide)sideBits));
                }
                else
                {
                    (bad ??= new List<int>()).Add(result.Count);
                    result.Add(default);
                }

                prevId = id;
                prevMs = ms;
                prevPrice = price;
            }
        }
        catch (IndexOutOfRangeException)
        {
            // last record incomplete: it is ignored
        }

        if (bad == null && Increasing(result, null)) return result;

        // phase 1: stretches around the impossible records
        var drop = Zones(bad, keys, result.Count);

        // phase 2: the same id with different data outside those stretches: one writer's chain of differences ended up
        // applied on another's base (both were recording the same live feed). Of each pair the copy nearest
        // a corrupt stretch is discarded (the other is the good one); if it cannot be told, both are.
        if (!Increasing(result, drop))
        {
            int n = result.Count;
            var dist = new int[n];
            int lastDrop = int.MinValue / 2;
            for (int i = 0; i < n; i++)
            {
                if (drop[i]) lastDrop = i;
                dist[i] = i - lastDrop;
            }

            int nextDrop = int.MaxValue / 2;
            for (int i = n - 1; i >= 0; i--)
            {
                if (drop[i]) nextDrop = i;
                dist[i] = Math.Min(dist[i], nextDrop - i);
            }

            var extra = new List<int>();
            var seen = new Dictionary<long, int>(n);
            for (int i = 0; i < n; i++)
            {
                if (drop[i]) continue;
                var t = result[i];
                if (!seen.TryGetValue(t.Id, out int j))
                {
                    seen[t.Id] = i;
                    continue;
                }

                var o = result[j];
                if (o.Time == t.Time && o.Price == t.Price && o.Volume == t.Volume) continue; // the same one, recorded twice
                if (dist[i] <= dist[j]) extra.Add(i);
                if (dist[j] <= dist[i]) extra.Add(j);
                if (dist[j] > dist[i]) seen[t.Id] = j; else seen[t.Id] = i;
            }

            if (extra.Count > 0)
            {
                var all = new List<int>(bad ?? new List<int>());
                all.AddRange(extra);
                all.Sort();
                drop = Zones(all, keys, n);
            }
        }

        var clean = new List<Trade>(result.Count);
        for (int i = 0; i < result.Count; i++)
        {
            if (drop[i]) dropped++;
            else clean.Add(result[i]);
        }

        return clean;
    }

    /// <summary>Whether the ids (not counting the discarded ones) always come in ascending order.</summary>
    private static bool Increasing(List<Trade> list, bool[]? drop)
    {
        long last = long.MinValue;
        for (int i = 0; i < list.Count; i++)
        {
            if (drop != null && drop[i]) continue;
            if (list[i].Id <= last) return false;
            last = list[i].Id;
        }

        return true;
    }

    /// <summary>
    /// Marks the corrupt stretches: nearby bad records are grouped and it discards from the keyframe before
    /// the first one to the keyframe after the last one (what is in between depends on the same chain of differences).
    /// </summary>
    private static bool[] Zones(List<int>? bad, List<int> keys, int count)
    {
        var drop = new bool[count];
        if (bad == null) return drop;
        for (int b = 0; b < bad.Count;)
        {
            int first = bad[b], last = first;
            while (b + 1 < bad.Count && bad[b + 1] - last <= 4096) last = bad[++b];
            b++;
            int k = keys.BinarySearch(first);
            int from = k >= 0 ? (k > 0 ? keys[k - 1] : 0) : (~k > 0 ? keys[~k - 1] : 0);
            int next = keys.BinarySearch(last + 1);
            int to = next >= 0 ? keys[next] : ~next < keys.Count ? keys[~next] : count;
            for (int i = from; i < to; i++) drop[i] = true;
        }

        return drop;
    }

    /// <summary>
    /// Rewrites a file without the corrupt stretches (it is saved aside and replaced in one go).
    /// Only call it with nobody recording that instrument. Returns how many records were removed.
    /// </summary>
    public static int Repair(string path, Instrument instrument)
    {
        var trades = Read(path, out int dropped);
        if (dropped == 0) return 0;
        var tmp = path + ".reparando";
        File.Delete(tmp);
        using (var w = new Writer(tmp, instrument))
            foreach (var t in trades) w.Write(t);
        File.Move(tmp, path, overwrite: true);
        return dropped;
    }

    /// <summary>
    /// Exclusive permission to record an instrument: a single process writes its files (two writers
    /// mix the records and corrupt the file). Returns null if another one already holds it; it is released on close.
    /// </summary>
    public static FileStream? TryLock(string root, string feedId, string symbol)
    {
        var dir = Path.Combine(root, Sanitize(feedId), Sanitize(symbol));
        try
        {
            Directory.CreateDirectory(dir);
            return new FileStream(Path.Combine(dir, ".grabando.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Sorts by id and removes duplicates if needed.</summary>
    public static List<Trade> SortAndDedupe(List<Trade> trades)
    {
        bool sorted = true;
        for (int i = 1; i < trades.Count; i++)
        {
            if (trades[i].Id <= trades[i - 1].Id)
            {
                sorted = false;
                break;
            }
        }

        if (sorted) return trades;
        trades.Sort((a, b) => a.Id.CompareTo(b.Id));
        var res = new List<Trade>(trades.Count);
        long last = long.MinValue;
        foreach (var t in trades)
        {
            if (t.Id == last) continue;
            res.Add(t);
            last = t.Id;
        }

        return res;
    }

    internal static (ulong Mantissa, int Scale) SplitDecimal(decimal d)
    {
        d = Math.Abs(d) / 1.0000000000000000000000000000m; // normalizes (strips zeros)
        var bits = decimal.GetBits(d);
        int scale = (bits[3] >> 16) & 0xFF;
        if (bits[2] != 0 || scale > 28)
        {
            // out of range: it is rounded to 8 decimals
            d = Math.Round(d, 8);
            bits = decimal.GetBits(d);
            scale = (bits[3] >> 16) & 0xFF;
        }

        ulong mant = ((ulong)(uint)bits[1] << 32) | (uint)bits[0];
        return (mant, Math.Min(scale, 31));
    }

    internal static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
    internal static long UnZigZag(ulong v) => (long)(v >> 1) ^ -(long)(v & 1);

    internal static int WriteVarint(Span<byte> buf, ulong v)
    {
        int n = 0;
        while (v >= 0x80)
        {
            buf[n++] = (byte)(v | 0x80);
            v >>= 7;
        }

        buf[n++] = (byte)v;
        return n;
    }

    internal static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63) throw new IndexOutOfRangeException();
        }

        return result;
    }
}
