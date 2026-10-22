using System.Globalization;
using System.Text;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Compact binary format for the order book over time (what the heatmap draws and what a strategy can look at).
/// One file per instrument and UTC day, next to the ticks: {root}/{feed}/{symbol}/{yyyy-MM-dd}.vdk
///
/// Header: "VDK1", tick size as text. Record = one snapshot of the top N levels per side:
/// flags byte (bit 0 = keyframe), time (Unix ms; absolute on a keyframe, difference otherwise), counts of bid and
/// ask levels, the best bid and best ask (absolute on a keyframe, difference otherwise), the size scale, then for each
/// side the size of every level (a varint) and the price gap to the level before it.
/// A keyframe is written every <see cref="KeyEvery"/> records and at the start of every file opening, so a damaged stretch
/// costs at most the time up to the next keyframe (the reader keeps what it could read and stops at the damage).
/// </summary>
public static class DepthFile
{
    private static readonly byte[] Magic = "VDK1"u8.ToArray();
    private static readonly decimal[] Pow10 = { 1m, 10m, 100m, 1_000m, 10_000m, 100_000m, 1_000_000m, 10_000_000m, 100_000_000m };
    public const int KeyEvery = 240;

    public static string DayPath(string root, string feedId, string symbol, DateOnly day) =>
        Path.ChangeExtension(TickFile.DayPath(root, feedId, symbol, day), ".vdk");

    public static IEnumerable<DateOnly> AvailableDays(string root, string feedId, string symbol)
    {
        var dir = Path.GetDirectoryName(TickFile.DayPath(root, feedId, symbol, default))!;
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.vdk").OrderBy(x => x))
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                yield return d;
    }

    /// <summary>Appends snapshots to a day file (one writer at a time: the recorder holds the instrument's lock).</summary>
    public sealed class Writer : IDisposable
    {
        private readonly FileStream _fs;
        private readonly BufferedStream _bs;
        private readonly Instrument _instrument;
        private bool _hasPrev;
        private long _prevMs, _prevBid, _prevAsk;
        private int _sinceKey;

        public Writer(string path, Instrument instrument)
        {
            _instrument = instrument;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bool exists = File.Exists(path) && new FileInfo(path).Length > 0;
            _fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _bs = new BufferedStream(_fs, 1 << 16);
            if (exists) return;
            _bs.Write(Magic);
            var ts = Encoding.UTF8.GetBytes(instrument.TickSize.ToString(CultureInfo.InvariantCulture));
            _bs.WriteByte((byte)ts.Length);
            _bs.Write(ts);
        }

        public void Write(DateTime time, DepthSnapshot book, int levels)
        {
            int nb = Math.Min(Math.Min(levels, 255), book.Bids.Length), na = Math.Min(Math.Min(levels, 255), book.Asks.Length);
            if (nb == 0 && na == 0) return;
            long ms = new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            bool key = !_hasPrev || ms < _prevMs || _sinceKey >= KeyEvery;
            int scale = 0;
            for (int i = 0; i < nb; i++) scale = Math.Max(scale, ScaleOf(book.Bids[i].Size));
            for (int i = 0; i < na; i++) scale = Math.Max(scale, ScaleOf(book.Asks[i].Size));

            Span<byte> buf = stackalloc byte[16 + 20 * 512];
            int n = 0;
            buf[n++] = (byte)(key ? 1 : 0);
            n += TickFile.WriteVarint(buf[n..], key ? (ulong)ms : (ulong)(ms - _prevMs));
            buf[n++] = (byte)nb;
            buf[n++] = (byte)na;
            if (nb > 0) n += TickFile.WriteVarint(buf[n..], TickFile.ZigZag(key ? book.Bids[0].PriceTicks : book.Bids[0].PriceTicks - _prevBid));
            if (na > 0) n += TickFile.WriteVarint(buf[n..], TickFile.ZigZag(key ? book.Asks[0].PriceTicks : book.Asks[0].PriceTicks - _prevAsk));
            buf[n++] = (byte)scale;
            for (int i = 0; i < nb; i++)
            {
                n += TickFile.WriteVarint(buf[n..], (ulong)Math.Round(book.Bids[i].Size * Pow10[scale]));
                if (i > 0) n += TickFile.WriteVarint(buf[n..], TickFile.ZigZag(book.Bids[i - 1].PriceTicks - book.Bids[i].PriceTicks));
            }

            for (int i = 0; i < na; i++)
            {
                n += TickFile.WriteVarint(buf[n..], (ulong)Math.Round(book.Asks[i].Size * Pow10[scale]));
                if (i > 0) n += TickFile.WriteVarint(buf[n..], TickFile.ZigZag(book.Asks[i].PriceTicks - book.Asks[i - 1].PriceTicks));
            }

            _bs.Write(buf[..n]);
            _sinceKey = key ? 0 : _sinceKey + 1;
            _hasPrev = true;
            _prevMs = ms;
            if (nb > 0) _prevBid = book.Bids[0].PriceTicks;
            if (na > 0) _prevAsk = book.Asks[0].PriceTicks;
        }

        private static int ScaleOf(decimal size) => Math.Min(8, TickFile.SplitDecimal(size).Scale);

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

    /// <summary>Reads a day file one snapshot at a time (so a long backtest holds one book in memory, not the whole day).</summary>
    public sealed class Decoder
    {
        private readonly byte[] _data;
        private int _pos;
        private long _prevMs, _prevBid, _prevAsk;
        private bool _any, _done;

        internal Decoder(byte[] data)
        {
            _data = data;
            _pos = 5 + data[4]; // magic, tick size length, tick size
        }

        /// <summary>The next snapshot; false at the end of the file or where it is damaged.</summary>
        public bool TryNext(out DateTime time, out DepthSnapshot book)
        {
            time = default;
            book = DepthSnapshot.Empty;
            if (_done || _pos >= _data.Length) return false;
            var data = _data;
            int pos = _pos;
            try
            {
                bool key = (data[pos++] & 1) != 0;
                long ms = key ? (long)TickFile.ReadVarint(data, ref pos) : _prevMs + (long)TickFile.ReadVarint(data, ref pos);
                int nb = data[pos++], na = data[pos++];
                long bid0 = 0, ask0 = 0;
                if (nb > 0) bid0 = (key ? 0 : _prevBid) + TickFile.UnZigZag(TickFile.ReadVarint(data, ref pos));
                if (na > 0) ask0 = (key ? 0 : _prevAsk) + TickFile.UnZigZag(TickFile.ReadVarint(data, ref pos));
                decimal div = Pow10[Math.Min(8, (int)data[pos++])];
                var bids = new BookLevel[nb];
                var asks = new BookLevel[na];
                long price = bid0;
                for (int i = 0; i < nb; i++)
                {
                    decimal size = TickFile.ReadVarint(data, ref pos) / div;
                    if (i > 0) price -= TickFile.UnZigZag(TickFile.ReadVarint(data, ref pos));
                    bids[i] = new BookLevel(price, size);
                }

                price = ask0;
                for (int i = 0; i < na; i++)
                {
                    decimal size = TickFile.ReadVarint(data, ref pos) / div;
                    if (i > 0) price += TickFile.UnZigZag(TickFile.ReadVarint(data, ref pos));
                    asks[i] = new BookLevel(price, size);
                }

                // junk: a time that goes backwards or a crossed book means the stretch is damaged
                if (_any && ms < _prevMs || nb > 0 && na > 0 && bid0 >= ask0) return Stop();
                _any = true;
                _prevMs = ms;
                if (nb > 0) _prevBid = bid0;
                if (na > 0) _prevAsk = ask0;
                _pos = pos;
                time = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                book = new DepthSnapshot(bids, asks, time);
                return true;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or OverflowException)
            {
                return Stop(); // the end of the file was cut in the middle of a record
            }
        }

        private bool Stop()
        {
            _done = true;
            return false;
        }
    }

    /// <summary>Opens a day file for reading (null if it does not exist or is not a depth file).</summary>
    public static Decoder? Open(string path)
    {
        if (!File.Exists(path)) return null;
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }

        return data.Length >= 5 && data.AsSpan(0, 4).SequenceEqual(Magic) ? new Decoder(data) : null;
    }

    /// <summary>Reads a whole day file. If it is damaged it returns what came before the damage.</summary>
    public static List<(DateTime Time, DepthSnapshot Book)> Read(string path)
    {
        var result = new List<(DateTime, DepthSnapshot)>();
        if (Open(path) is { } d)
            while (d.TryNext(out var t, out var b)) result.Add((t, b));
        return result;
    }

    /// <summary>Every snapshot between two instants (reads the day files that cover them), in time order.</summary>
    public static List<(DateTime Time, DepthSnapshot Book)> ReadRange(string root, string feedId, string symbol, DateTime fromUtc, DateTime toUtc)
    {
        var all = new List<(DateTime Time, DepthSnapshot Book)>();
        for (var d = DateOnly.FromDateTime(fromUtc); d <= DateOnly.FromDateTime(toUtc); d = d.AddDays(1))
        {
            if (Open(DayPath(root, feedId, symbol, d)) is not { } dec) continue;
            while (dec.TryNext(out var t, out var book))
            {
                if (t > toUtc) break;
                if (t >= fromUtc) all.Add((t, book));
            }
        }

        return all;
    }
}

/// <summary>Records the book to disk on its own thread (it never blocks the UI). One snapshot per <see cref="Interval"/> at most.</summary>
public sealed class DepthRecorder : IDisposable
{
    private readonly string _root;
    private readonly Instrument _instrument;
    private readonly System.Collections.Concurrent.BlockingCollection<(DateTime Time, DepthSnapshot Book)> _queue = new(new System.Collections.Concurrent.ConcurrentQueue<(DateTime, DepthSnapshot)>());
    private readonly Thread _thread;
    private DateTime _lastOffered = DateTime.MinValue;
    private DepthFile.Writer? _writer;
    private DateOnly _day;

    /// <summary>The caller holds the instrument's recording lock (see <see cref="TickRecorder"/>): only one process writes.</summary>
    public DepthRecorder(string root, Instrument instrument)
    {
        _root = root;
        _instrument = instrument;
        _thread = new Thread(Run) { IsBackground = true, Name = "depth-recorder-" + instrument.Symbol };
        _thread.Start();
    }

    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How many levels per side are kept.</summary>
    public int Levels { get; set; } = 20;

    public void Offer(DateTime time, DepthSnapshot book)
    {
        if (time < _lastOffered) _lastOffered = DateTime.MinValue; // the clock went back (a replay restarted)
        if (time - _lastOffered < Interval) return;
        _lastOffered = time;
        _queue.TryAdd((time, book));
    }

    private void Run()
    {
        try
        {
            foreach (var (time, book) in _queue.GetConsumingEnumerable())
            {
                var day = DateOnly.FromDateTime(time);
                if (_writer == null || day != _day)
                {
                    _writer?.Dispose();
                    _day = day;
                    _writer = new DepthFile.Writer(DepthFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day), _instrument);
                }

                _writer.Write(time, book, Levels);
                if (_queue.Count == 0) _writer.Flush();
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _writer?.Dispose();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(2000);
    }
}
