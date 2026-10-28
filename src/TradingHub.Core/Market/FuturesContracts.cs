namespace TradingHub.Market;

/// <summary>Which contract is the front month, and the exchange code vendors put in front of a symbol.</summary>
public static class FuturesContracts
{
    /// <summary>Month codes, January to December.</summary>
    public const string MonthCodes = "FGHJKMNQUVXZ";

    /// <summary>
    /// Front month of a future as (year, month 1-12), or null when only an exact contract can be named. Indices roll with <see cref="FuturesRolls"/>;
    /// gold (Feb, Apr, Jun, Aug, Oct, Dec) moves to the next contract on the 25th of the month before.
    /// </summary>
    public static (int Year, int Month)? FrontMonth(string root, DateTime utc)
    {
        if (FuturesRolls.Supports(root))
        {
            var c = FuturesRolls.On(root, utc).Symbol; // ESZ6
            int month = MonthCodes.IndexOf(c[^2]) + 1;
            int year = utc.Year - utc.Year % 10 + (c[^1] - '0');
            if (year < utc.Year) year += 10;
            return (year, month);
        }

        if (root is "GC" or "MGC")
        {
            var (year, month) = (utc.Year, utc.Month + utc.Month % 2); // this month if even, otherwise the next one
            if (utc >= new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-1).AddDays(24)) month += 2;
            if (month > 12) (year, month) = (year + 1, month - 12);
            return (year, month);
        }

        return null; // ponytail: only indices and gold roll on their own; anything else is typed exactly by the user
    }

    /// <summary>Market identifier code of the exchange (CME Group venues): XCME, XCBT, XNYM, XCEC.</summary>
    public static string Mic(string exchange) => exchange switch
    {
        "CBOT" => "XCBT",
        "NYMEX" => "XNYM",
        "COMEX" => "XCEC",
        _ => "XCME",
    };
}
