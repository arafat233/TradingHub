using System.Text.Json;

namespace TradingHub.Trading;

/// <summary>A fill of an account: what is stored for the history and the statistics.</summary>
public sealed record AccountFill(
    string Id,
    DateTime Time,
    string Symbol,
    bool Buy,
    decimal Quantity,
    decimal Price,
    decimal PointValue,
    decimal Commission = 0,
    string OrderId = "");

/// <summary>Complete trade: from the position opening until it goes back to zero (or reverses).</summary>
public sealed record AccountTrade(
    string Symbol,
    bool IsLong,
    DateTime OpenTime,
    DateTime CloseTime,
    decimal Quantity,
    decimal OpenPrice,
    decimal ClosePrice,
    decimal Pnl,
    decimal Commission)
{
    public decimal NetPnl => Pnl - Commission;
    public TimeSpan Duration => CloseTime - OpenTime;
}

/// <summary>What comes out of walking the fills: closed trades and each realized result (partials included).</summary>
public sealed record RoundTripResult(
    List<AccountTrade> Trades,
    List<(DateTime Time, decimal Net)> Realized,
    decimal RealizedNet,
    decimal Commission);

public static class RoundTrips
{
    /// <summary>
    /// Builds the closed trades from the fills, using the average price: adding to the position
    /// averages the entry, reducing it realizes a result, and reversing it closes one trade and opens another.
    /// </summary>
    public static RoundTripResult Analyze(IEnumerable<AccountFill> fills)
    {
        var trades = new List<AccountTrade>();
        var realized = new List<(DateTime, decimal)>();
        decimal totalNet = 0, totalComm = 0;
        foreach (var g in fills.GroupBy(f => f.Symbol))
        {
            decimal pos = 0, avg = 0, entryValue = 0, entryQty = 0, exitValue = 0, exitQty = 0, pnl = 0, comm = 0;
            bool isLong = false;
            DateTime open = default;
            foreach (var f in g.OrderBy(f => f.Time))
            {
                decimal q = f.Quantity, pv = f.PointValue > 0 ? f.PointValue : 1;
                if (q <= 0) continue;
                decimal commPerUnit = f.Commission / q;
                int dir = f.Buy ? 1 : -1;
                if (pos != 0 && Math.Sign(pos) != dir)
                {
                    decimal closing = Math.Min(q, Math.Abs(pos));
                    decimal gained = (f.Price - avg) * closing * Math.Sign(pos) * pv, c = commPerUnit * closing;
                    pnl += gained;
                    comm += c;
                    realized.Add((f.Time, gained - c));
                    totalNet += gained - c;
                    totalComm += c;
                    exitValue += f.Price * closing;
                    exitQty += closing;
                    pos += dir * closing;
                    q -= closing;
                    if (pos == 0)
                    {
                        trades.Add(new AccountTrade(g.Key, isLong, open, f.Time, entryQty, entryValue / entryQty, exitValue / exitQty, pnl, comm));
                        avg = entryValue = entryQty = exitValue = exitQty = pnl = comm = 0;
                    }
                }

                if (q > 0)
                {
                    if (pos == 0)
                    {
                        open = f.Time;
                        isLong = dir > 0;
                    }

                    decimal c = commPerUnit * q;
                    avg = (avg * Math.Abs(pos) + f.Price * q) / (Math.Abs(pos) + q);
                    pos += dir * q;
                    entryValue += f.Price * q;
                    entryQty += q;
                    comm += c;
                    // the entry commission is deducted when it is paid
                    if (c != 0)
                    {
                        realized.Add((f.Time, -c));
                        totalNet -= c;
                        totalComm += c;
                    }
                }
            }
        }

        trades.Sort((a, b) => a.CloseTime.CompareTo(b.CloseTime));
        realized.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return new RoundTripResult(trades, realized, totalNet, totalComm);
    }
}

/// <summary>
/// Fill history per account, stored on disk (one JSON per line) so the balance, the filled
/// orders and the statistics survive closing Trading Hub.
/// </summary>
public sealed class AccountJournal
{
    private readonly string _dir;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<AccountFill>> _fills = new();
    private readonly Dictionary<string, HashSet<string>> _ids = new();

    public AccountJournal(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    /// <summary>An account's history changed (account id). It can arrive from other threads.</summary>
    public event Action<string>? Changed;

    private string FileOf(string account) =>
        Path.Combine(_dir, string.Concat(account.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')) + ".jsonl");

    private List<AccountFill> Load(string account)
    {
        if (_fills.TryGetValue(account, out var list)) return list;
        list = new List<AccountFill>();
        var ids = new HashSet<string>();
        var path = FileOf(account);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<AccountFill>(line) is { } f && ids.Add(f.Id)) list.Add(f);
                }
                catch (JsonException)
                {
                    // truncated line (for example if it closed right while writing): it is skipped
                }
            }
        }

        _fills[account] = list;
        _ids[account] = ids;
        return list;
    }

    /// <summary>How many fills the account has (to know whether it changed without copying the list).</summary>
    public int CountOf(string account)
    {
        lock (_gate) return Load(account).Count;
    }

    public IReadOnlyList<AccountFill> FillsOf(string account)
    {
        lock (_gate) return Load(account).ToList();
    }

    /// <summary>Stores a fill (repeated ones, with the same id, are ignored).</summary>
    public bool Add(string account, AccountFill fill)
    {
        lock (_gate)
        {
            var list = Load(account);
            if (!_ids[account].Add(fill.Id)) return false;
            list.Add(fill);
            File.AppendAllText(FileOf(account), JsonSerializer.Serialize(fill) + "\n");
        }

        Changed?.Invoke(account);
        return true;
    }

    /// <summary>Starts from scratch (the previous file is kept aside, not deleted).</summary>
    public void Restart(string account)
    {
        lock (_gate)
        {
            var path = FileOf(account);
            if (File.Exists(path)) File.Move(path, path + $".{DateTime.Now:yyyyMMdd-HHmmss}.bak");
            _fills[account] = new List<AccountFill>();
            _ids[account] = new HashSet<string>();
        }

        Changed?.Invoke(account);
    }
}
