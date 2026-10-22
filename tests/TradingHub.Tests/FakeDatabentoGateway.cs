using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace TradingHub.Tests;

/// <summary>
/// Fake Databento Live Subscription Gateway (the protocol of the official client): greeting, CRAM challenge, authentication,
/// subscription and then binary DBN records with the real layouts (trades 48 bytes, MBP-10 368, error and system messages).
/// </summary>
internal sealed class FakeDatabentoGateway : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public const string Challenge = "c0ffee42";

    public string Key { get; set; } = "db-TESTKEY0000000000000000abcde";
    public string Gateway { get; }
    public List<string> Lines { get; } = new();

    /// <summary>Records sent after start_session (already encoded).</summary>
    public List<byte[]> Records { get; } = new();

    public FakeDatabentoGateway()
    {
        _listener.Start();
        Gateway = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try
            {
                c = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using var _ = client;
        var s = client.GetStream();
        var reader = new StreamReader(s, Encoding.ASCII);
        async Task Line(string text) => await s.WriteAsync(Encoding.ASCII.GetBytes(text + "\n"));
        try
        {
            await Line("lsg_version=0.6.0");
            await Line("cram=" + Challenge);
            var auth = await reader.ReadLineAsync() ?? "";
            lock (Lines) Lines.Add(auth);
            string expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Challenge}|{Key}"))) + "-" + Key[^5..];
            if (!auth.StartsWith("auth=" + expected + "|", StringComparison.Ordinal))
            {
                await Line("success=0|error=Authentication failed.");
                return;
            }

            await Line("success=1|session_id=42");
            for (int i = 0; i < 2; i++)
            {
                var l = await reader.ReadLineAsync() ?? "";
                lock (Lines) Lines.Add(l);
            }

            // DBN prelude: "DBN", version 3, metadata length and a dummy metadata block
            var prelude = new byte[8 + 100];
            "DBN"u8.CopyTo(prelude);
            prelude[3] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(prelude.AsSpan(4), 100);
            await s.WriteAsync(prelude);
            foreach (var r in Records)
            {
                await s.WriteAsync(r);
                await Task.Delay(5);
            }

            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch
        {
            // the client closed
        }
    }

    private static byte[] Header(int length, byte rtype, long tsNs)
    {
        var b = new byte[length];
        b[0] = (byte)(length / 4);
        b[1] = rtype;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 4242);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), tsNs);
        return b;
    }

    private static long Fx(double? price) => price is { } p ? (long)Math.Round(p * 1_000_000_000) : long.MaxValue;

    /// <summary>TradeMsg (rtype 0x00, 48 bytes).</summary>
    public static byte[] Trade(long tsNs, double price, uint size, char side)
    {
        var b = Header(48, 0x00, tsNs);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), Fx(price));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), size);
        b[28] = (byte)'T';
        b[29] = (byte)side;
        return b;
    }

    /// <summary>Mbp10Msg (rtype 0x0A, 368 bytes): the event and the 10 levels after it.</summary>
    public static byte[] Mbp10(long tsNs, char action, char side, double price, uint size, (double? Bid, uint BidSz, double? Ask, uint AskSz)[] levels)
    {
        var b = Header(368, 0x0A, tsNs);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), Fx(price));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), size);
        b[28] = (byte)action;
        b[29] = (byte)side;
        for (int i = 0; i < 10; i++)
        {
            var (bid, bsz, ask, asz) = i < levels.Length ? levels[i] : (null, 0u, null, 0u);
            var lv = b.AsSpan(48 + i * 32);
            BinaryPrimitives.WriteInt64LittleEndian(lv, Fx(bid));
            BinaryPrimitives.WriteInt64LittleEndian(lv[8..], Fx(ask));
            BinaryPrimitives.WriteUInt32LittleEndian(lv[16..], bsz);
            BinaryPrimitives.WriteUInt32LittleEndian(lv[20..], asz);
        }

        return b;
    }

    /// <summary>ErrorMsg (0x15) or SystemMsg (0x17) with a text, 320 bytes.</summary>
    public static byte[] Message(byte rtype, string text)
    {
        var b = Header(320, rtype, 0);
        Encoding.UTF8.GetBytes(text).CopyTo(b, 16);
        return b;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
