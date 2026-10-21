namespace TradingHub.Market;

/// <summary>
/// Active contract of a future on each date, to build the continuous history (like ATAS's continuous contract).
/// CME indices (ES, NQ, YM, RTY and their micros): they expire on the third Friday of March, June, September and December.
/// The volume moves to the next one on the Monday of expiry week: in Rithmic's September 2026 data the
/// ESZ6 traded 127 thousand contracts on Friday the 11th and 845 thousand on Monday the 14th (and NQ the same).
/// </summary>
public static class FuturesRolls
{
    private static readonly HashSet<string> Quarterly = new(StringComparer.OrdinalIgnoreCase) { "ES", "MES", "NQ", "MNQ", "YM", "MYM", "RTY", "M2K", "EMD" };
    private const string Codes = "FGHJKMNQUVXZ";

    public static bool Supports(string root) => Quarterly.Contains(root);

    /// <summary>Expiry: third Friday of the month.</summary>
    public static DateTime Expiry(int year, int month)
    {
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        int toFriday = ((int)DayOfWeek.Friday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(toFriday + 14);
    }

    /// <summary>Day the volume moves to the next contract (the Monday of expiry week).</summary>
    public static DateTime Roll(int year, int month) => Expiry(year, month).AddDays(-4);

    /// <summary>The same contract with a two-digit year (NQU6 -> NQU26), in case Rithmic names it that way once expired.</summary>
    public static string? TwoDigitYear(string symbol, DateTime around)
    {
        if (symbol.Length < 3 || !char.IsDigit(symbol[^1]) || !Codes.Contains(symbol[^2])) return null;
        int digit = symbol[^1] - '0';
        int year = around.Year - around.Year % 10 + digit;
        if (year > around.Year + 1) year -= 10;
        return symbol[..^1] + (year % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Rithmic/CME symbol: root + month + last digit of the year (ES, Sep 2026 -> ESU6).</summary>
    public static string Symbol(string root, int year, int month) => $"{root.ToUpperInvariant()}{Codes[month - 1]}{year % 10}";

    /// <summary>Active contract at an instant, and from/until when it is.</summary>
    public static (string Symbol, DateTime From, DateTime To) On(string root, DateTime utc)
    {
        int y = utc.Year, m = ((utc.Month - 1) / 3 + 1) * 3; // this quarter's quarterly: 3, 6, 9 or 12
        if (utc >= Roll(y, m))
        {
            m += 3;
            if (m > 12)
            {
                m -= 12;
                y++;
            }
        }

        int py = y, pm = m - 3;
        if (pm < 1)
        {
            pm += 12;
            py--;
        }

        return (Symbol(root, y, m), Roll(py, pm), Roll(y, m));
    }

    /// <summary>Stretches of a range, each one with its contract.</summary>
    public static IEnumerable<(string Symbol, DateTime From, DateTime To)> Segments(string root, DateTime fromUtc, DateTime toUtc)
    {
        var t = fromUtc;
        while (t < toUtc)
        {
            var c = On(root, t);
            var end = c.To < toUtc ? c.To : toUtc;
            yield return (c.Symbol, t, end);
            t = end;
        }
    }
}
