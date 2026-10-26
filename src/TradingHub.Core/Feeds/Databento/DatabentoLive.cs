using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TradingHub.Market;

namespace TradingHub.Feeds.Databento;

/// <summary>Live Databento connection: the API key, whether to stream the book and which CME futures to offer.</summary>
public sealed class DatabentoLiveSettings
{
    public string ApiKey { get; set; } = "";

    /// <summary>Level 2: the MBP-10 schema (top 10 levels on every book change, trades included); otherwise only trades (cheaper).</summary>
    public bool Level2 { get; set; } = true;

    /// <summary>Level 3: the MBO schema (every order add, modify, cancel and fill). The book is rebuilt from the orders, so it is deeper than MBP-10; it streams the most data.</summary>
    public bool Mbo { get; set; }

    public List<string> Products { get; set; } = new() { "ES", "NQ", "MES", "MNQ", "YM", "MYM", "RTY", "M2K", "GC", "MGC", "CL", "MCL" };

    /// <summary>Live gateway (host:port); null = Databento's for CME Globex. The tests point it at a fake one.</summary>
    public string? Gateway { get; set; }
}

/// <summary>
/// CME futures live from Databento (GLBX.MDP3) over its Live Subscription Gateway: plain TCP, a text handshake with
/// challenge-response (the key never travels) and then binary DBN records. With Level 2 it subscribes to MBP-10, whose
/// every record carries the top 10 levels (and the trades, action T); without it, to trades. The volume continuous
/// contract (NQ.v.0) rolls on its own. It never downloads history (that is paid per use and lives in Data): the chart shows
/// what is already downloaded or recorded plus what arrives live.
/// </summary>
public sealed class DatabentoLiveFeed : IMarketDataFeed
{
    public const string FeedId = "databento";
    private const string Host = "glbx-mdp3.lsg.databento.com";
    private const int Port = 13000;
    private const byte RTypeTrade = 0x00, RTypeMbp10 = 0x0A, RTypeMbo = 0xA0, RTypeError = 0x15, RTypeSystem = 0x17;
    private readonly DatabentoLiveSettings _s;

    public DatabentoLiveFeed(DatabentoLiveSettings settings) => _s = settings;

    public string Id => FeedId;
    public string DisplayName => "Databento";

    /// <summary>False on purpose: filling a chart's gaps from Databento would spend the user's credits without asking.</summary>
    public bool SupportsHistory => false;

    public bool IsLive => true;

    public Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        var list = _s.Products.Select(x => FuturesCatalog.Find(x)).OfType<FuturesProduct>().DistinctBy(p => p.Root)
            .Select(p => new Instrument
            {
                Symbol = p.Root, FeedId = FeedId, Exchange = p.Exchange, TickSize = p.Tick, VolumeStep = 1, PointValue = p.PointValue,
                Description = $"{p.Name} · {DatabentoClient.Continuous(p.Root)}",
            }).ToList();
        return Task.FromResult<IReadOnlyList<Instrument>>(list);
    }

    public Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct) => Task.CompletedTask;

    /// <summary>The CRAM answer: sha256("challenge|key") in hex, a dash and the key's last 5 characters.</summary>
    public static string CramResponse(string challenge, string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{challenge}|{key}"))) + "-" + key[^5..];

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription("Databento", (st, msg) => sink.OnStatus(instrument, st, msg));
        _ = Task.Run(() => RunAsync(instrument, sink, sub));
        return sub;
    }

    private async Task RunAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription live)
    {
        var ct = live.Token;
        int failures = 0, requested = 0;
        long lastId = 0;
        while (!ct.IsCancellationRequested)
        {
            var conn = live.BeginConnection();
            string reason = "the connection dropped";
            bool any = false;
            try
            {
                string key = _s.ApiKey.Trim();
                if (key.Length < 5) throw new InvalidOperationException("The Databento API key is missing (Connections → Databento)");
                live.SetChannel("data", FeedStatus.Connecting, "Connecting to Databento…");
                var (host, port) = _s.Gateway is { Length: > 0 } g ? (g.Split(':')[0], int.Parse(g.Split(':')[1])) : (Host, Port);
                using var tcp = new TcpClient { NoDelay = true };
                using (var c = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                {
                    c.CancelAfter(TimeSpan.FromSeconds(15));
                    await tcp.ConnectAsync(host, port, c.Token).ConfigureAwait(false);
                }

                var stream = tcp.GetStream();
                var r = new Reader(stream);
                Task Send(string line) => stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), conn.Token).AsTask();

                // 1) handshake in text lines: greeting, challenge, authentication
                conn.CancelAfter(TimeSpan.FromSeconds(15));
                string? cram = null;
                while (cram == null)
                {
                    var line = await r.LineAsync(conn.Token).ConfigureAwait(false);
                    if (line.StartsWith("cram=", StringComparison.Ordinal)) cram = line[5..];
                }

                await Send($"auth={CramResponse(cram, key)}|dataset={DatabentoClient.Dataset}|encoding=dbn|ts_out=0|compression=none|client=TradingHub/1.0").ConfigureAwait(false);
                var auth = Fields(await r.LineAsync(conn.Token).ConfigureAwait(false));
                if (auth.GetValueOrDefault("success") != "1")
                    throw new InvalidOperationException("Databento rejected the connection: " + (auth.GetValueOrDefault("error") ?? "check the API key and that your plan includes live CME data"));

                // MBO asks for a snapshot too: the orders already resting when we connect (the book is empty without them)
                await Send($"schema={(_s.Mbo ? "mbo" : _s.Level2 ? "mbp-10" : "trades")}|stype_in=continuous|symbols={DatabentoClient.Continuous(instrument.Symbol)}|snapshot={(_s.Mbo ? 1 : 0)}|is_last=1").ConfigureAwait(false);
                await Send("start_session=0").ConfigureAwait(false);

                // 2) DBN: "DBN" + version + metadata length, the metadata (not needed) and then the records
                var prelude = await r.ExactAsync(8, conn.Token).ConfigureAwait(false);
                if (prelude.Span[0] != 'D' || prelude.Span[1] != 'B' || prelude.Span[2] != 'N') throw new InvalidDataException("Databento did not send DBN data");
                await r.ExactAsync((int)BinaryPrimitives.ReadUInt32LittleEndian(prelude.Span[4..]), conn.Token).ConfigureAwait(false);
                live.SetChannel("data", FeedStatus.Connected, null);
                failures = 0;

                var book = new OrderBookBuilder();
                long lastBook = 0;
                var mbo = new MboState();
                while (!conn.IsCancellationRequested)
                {
                    conn.CancelAfter(TimeSpan.FromSeconds(90)); // the gateway sends a heartbeat every 30 s
                    int length = (await r.ExactAsync(1, conn.Token).ConfigureAwait(false)).Span[0] * 4;
                    if (length < 16) throw new InvalidDataException("Corrupt DBN record");
                    var rec = (await r.ExactAsync(length - 1, conn.Token).ConfigureAwait(false)).Span; // without the length byte: offsets below are -1
                    byte rtype = rec[0];
                    if (rtype == RTypeError) throw new InvalidOperationException("Databento: " + Text(rec[15..]));
                    if (rtype == RTypeMbo)
                    {
                        if (length >= 56)
                        {
                            any = true;
                            ApplyMbo(rec, instrument, sink, mbo, ref lastId, ref lastBook);
                        }

                        continue;
                    }

                    if (rtype == RTypeSystem || length < 48 || (rtype != RTypeTrade && rtype != RTypeMbp10)) continue;

                    any = true;
                    long tsNs = BinaryPrimitives.ReadInt64LittleEndian(rec[7..]);
                    char action = (char)rec[27], side = (char)rec[28];
                    if (action == 'T' && Px(BinaryPrimitives.ReadInt64LittleEndian(rec[15..])) is { } price && BinaryPrimitives.ReadUInt32LittleEndian(rec[23..]) is var size and > 0)
                    {
                        var time = DateTime.UnixEpoch.AddTicks(tsNs / 100);
                        var tside = side switch { 'B' => TradeSide.Buy, 'A' => TradeSide.Sell, _ => TradeSide.None };
                        sink.OnTrades(instrument, new[] { new Trade(HistoryBuilder.TimeId(time, ref lastId), time, instrument.RoundPrice(price), size, tside) });
                    }

                    // ponytail: the book is shown at most every 50 ms (NQ can send thousands of changes per second); the last change of a burst waits for the next record
                    if (rtype != RTypeMbp10 || length < 368 || Environment.TickCount64 - lastBook < 50) continue;
                    lastBook = Environment.TickCount64;
                    book.Clear();
                    for (int i = 0; i < 10; i++)
                    {
                        var lv = rec[(47 + i * 32)..];
                        if (Px(BinaryPrimitives.ReadInt64LittleEndian(lv)) is { } bid && BinaryPrimitives.ReadUInt32LittleEndian(lv[16..]) is var bs and > 0)
                            book.Set(BookSide.Bid, instrument.ToTicks(bid), bs);
                        if (Px(BinaryPrimitives.ReadInt64LittleEndian(lv[8..])) is { } ask && BinaryPrimitives.ReadUInt32LittleEndian(lv[20..]) is var asz and > 0)
                            book.Set(BookSide.Ask, instrument.ToTicks(ask), asz);
                    }

                    sink.OnDepth(instrument, book.BuildUpdate(DateTime.UnixEpoch.AddTicks(tsNs / 100), 10, true));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                reason = live.WasRequested(ref requested) ? "reconnection requested" : "it did not respond in time";
                if (!any) failures++;
            }
            catch (Exception ex)
            {
                failures++;
                reason = ex.Message;
            }
            finally
            {
                live.EndConnection(conn);
            }

            if (ct.IsCancellationRequested) break;
            var wait = LiveSubscription.Backoff(failures);
            live.SetChannel("data", FeedStatus.Reconnecting, $"Reconnecting in {wait.TotalSeconds:0} s ({reason})");
            await Task.Delay(wait, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        live.SetChannel("data", FeedStatus.Disconnected, null);
    }

    /// <summary>The resting orders by id and the book summed from them.</summary>
    private sealed class MboState
    {
        public readonly Dictionary<long, (bool Ask, long Ticks, long Size)> Orders = new();
        public readonly Dictionary<(bool Ask, long Ticks), long> Levels = new();
        public readonly OrderBookBuilder Book = new();

        public void Level(bool ask, long ticks, long delta)
        {
            long total = Math.Max(0, Levels.GetValueOrDefault((ask, ticks)) + delta);
            if (total == 0) Levels.Remove((ask, ticks));
            else Levels[(ask, ticks)] = total;
            Book.Set(ask ? BookSide.Ask : BookSide.Bid, ticks, total);
        }

        public void Clear()
        {
            Orders.Clear();
            Levels.Clear();
            Book.Clear();
        }
    }

    /// <summary>
    /// One MboMsg (rtype 0xA0, 56 bytes; offsets are -1 because the length byte is gone): order id 15, price 23, size 31, flags 35, action 37, side 38.
    /// A add, M modify, C cancel (size = what was cancelled), F fill (size = what was filled), T trade (it does not touch the book), R clear.
    /// Records of the initial snapshot (flag 32) build the book but are not announced as order events.
    /// </summary>
    private static void ApplyMbo(ReadOnlySpan<byte> rec, Instrument inst, IMarketDataSink sink, MboState st, ref long lastId, ref long lastBook)
    {
        long tsNs = BinaryPrimitives.ReadInt64LittleEndian(rec[7..]);
        long id = unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(rec[15..]));
        var price = Px(BinaryPrimitives.ReadInt64LittleEndian(rec[23..]));
        long size = BinaryPrimitives.ReadUInt32LittleEndian(rec[31..]);
        bool silent = (rec[35] & 32) != 0;
        char action = (char)rec[37], side = (char)rec[38];
        var time = DateTime.UnixEpoch.AddTicks(tsNs / 100);

        void Event(OrderEventKind kind, bool ask, long ticks, long sz)
        {
            if (!silent) sink.OnOrderEvents(inst, new[] { new OrderEvent(time, kind, id, ask, ticks, sz) });
        }

        void Add(bool ask)
        {
            long ticks = inst.ToTicks(price!.Value);
            st.Orders[id] = (ask, ticks, size);
            st.Level(ask, ticks, size);
            Event(OrderEventKind.Add, ask, ticks, size);
        }

        // a cancel or a fill takes `size` off the order; what is left stays on the book
        void Reduce(OrderEventKind gone)
        {
            if (!st.Orders.TryGetValue(id, out var o)) return;
            long left = o.Size - size;
            st.Level(o.Ask, o.Ticks, -Math.Min(size, o.Size));
            if (left > 0)
            {
                st.Orders[id] = (o.Ask, o.Ticks, left);
                Event(OrderEventKind.Modify, o.Ask, o.Ticks, left);
            }
            else
            {
                st.Orders.Remove(id);
                Event(gone, o.Ask, o.Ticks, o.Size);
            }
        }

        switch (action)
        {
            case 'T':
                if (price is { } p && size > 0)
                {
                    var tside = side switch { 'B' => TradeSide.Buy, 'A' => TradeSide.Sell, _ => TradeSide.None };
                    sink.OnTrades(inst, new[] { new Trade(HistoryBuilder.TimeId(time, ref lastId), time, inst.RoundPrice(p), size, tside) });
                }

                return;
            case 'R':
                st.Clear();
                break;
            case 'A' when price != null && side is 'A' or 'B':
                Add(side == 'A');
                break;
            case 'M':
                if (st.Orders.TryGetValue(id, out var old) && price != null)
                {
                    long ticks = inst.ToTicks(price.Value);
                    st.Level(old.Ask, old.Ticks, -old.Size);
                    st.Level(old.Ask, ticks, size);
                    st.Orders[id] = (old.Ask, ticks, size);
                    Event(OrderEventKind.Modify, old.Ask, ticks, size);
                }
                else if (price != null && side is 'A' or 'B') Add(side == 'A'); // modifying an order we never saw
                break;
            case 'C':
                Reduce(OrderEventKind.Cancel);
                break;
            case 'F':
                Reduce(OrderEventKind.Fill);
                break;
            default:
                return;
        }

        // ponytail: shown at most every 50 ms like the other schemas; the book itself is always exact, only the redraw waits
        if (Environment.TickCount64 - lastBook < 50) return;
        lastBook = Environment.TickCount64;
        sink.OnDepth(inst, st.Book.BuildUpdate(time, 50, true));
    }

    /// <summary>Fixed-point price (1e-9); i64.MaxValue means "no price".</summary>
    private static decimal? Px(long fixedPx) => fixedPx == long.MaxValue ? null : fixedPx / 1_000_000_000m;

    private static string Text(ReadOnlySpan<byte> s)
    {
        int end = s.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? s[..end] : s).Trim();
    }

    private static Dictionary<string, string> Fields(string line) =>
        line.Split('|').Select(t => t.Split('=', 2)).Where(kv => kv.Length == 2).GroupBy(kv => kv[0]).ToDictionary(g => g.Key, g => g.First()[1]);

    /// <summary>Connects with the first product and waits for the first data (or the error).</summary>
    public async Task<(bool Ok, string Message)> TestAsync(CancellationToken ct)
    {
        var instruments = await GetInstrumentsAsync(ct).ConfigureAwait(false);
        if (instruments.Count == 0) return (false, "Products has no CME future Trading Hub knows (for example NQ, ES, GC).");
        var done = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool connected = false;
        var sink = new TestSink(
            (st, msg) =>
            {
                if (st == FeedStatus.Connected) connected = true;
                else if (st is FeedStatus.Error or FeedStatus.Reconnecting) done.TrySetResult((false, msg ?? "Databento did not accept the connection"));
            },
            depth => done.TrySetResult((true, $"Live data OK: {DatabentoClient.Continuous(instruments[0].Symbol)}" + (depth ? (_s.Mbo ? " with Level 3 (MBO)." : " with Level 2 (MBP-10).") : "."))));
        using var sub = Subscribe(instruments[0], sink);
        try
        {
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return connected
                ? (true, "Signed in to Databento live; nothing traded in 20 s (market closed?).")
                : (false, "Databento did not answer in 20 s");
        }
    }

    private sealed class TestSink(Action<FeedStatus, string?> onStatus, Action<bool> onData) : IMarketDataSink
    {
        public void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades) => onData(false);
        public void OnDepth(Instrument instrument, DepthUpdate update) => onData(true);
        public void OnStatus(Instrument instrument, FeedStatus status, string? message) => onStatus(status, message);
    }

    /// <summary>Buffered reading of the socket: text lines first, then exact blocks of bytes.</summary>
    private sealed class Reader(Stream stream)
    {
        private byte[] _buf = new byte[1 << 16];
        private int _start, _end;

        private async Task FillAsync(CancellationToken ct)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buf.Length) Array.Resize(ref _buf, _buf.Length * 2);
            int n = await stream.ReadAsync(_buf.AsMemory(_end), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("Databento closed the connection");
            _end += n;
        }

        public async Task<string> LineAsync(CancellationToken ct)
        {
            while (true)
            {
                int nl = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);
                if (nl >= 0)
                {
                    var line = Encoding.ASCII.GetString(_buf, _start, nl - _start);
                    _start = nl + 1;
                    return line;
                }

                await FillAsync(ct).ConfigureAwait(false);
            }
        }

        public async Task<ReadOnlyMemory<byte>> ExactAsync(int count, CancellationToken ct)
        {
            while (_end - _start < count) await FillAsync(ct).ConfigureAwait(false);
            var m = new ReadOnlyMemory<byte>(_buf, _start, count);
            _start += count;
            return m;
        }
    }

    public void Dispose()
    {
    }
}
