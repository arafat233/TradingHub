namespace TradingHub.Market;

/// <summary>
/// Globex (CME) schedule, where the Rithmic futures trade (ES, NQ, CL, GC…): from Sunday 17:00 to Friday 16:00
/// Chicago time, with a daily break from 16:00 to 17:00. It does not know about holidays.
/// </summary>
public static class FuturesSessions
{
    private static readonly TimeSpan Close = TimeSpan.FromHours(16), Open = TimeSpan.FromHours(17);
    private static readonly TimeZoneInfo Chicago = Find("America/Chicago");

    private static TimeZoneInfo Find(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(-6), id, id);
        }
    }

    public static bool IsGlobexClosed(DateTime utc)
    {
        var ct = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Chicago);
        var t = ct.TimeOfDay;
        return ct.DayOfWeek switch
        {
            DayOfWeek.Saturday => true,
            DayOfWeek.Sunday => t < Open,
            DayOfWeek.Friday => t >= Close,
            _ => t >= Close && t < Open,
        };
    }

    /// <summary>Next Globex open (UTC); if it is open, now.</summary>
    public static DateTime NextGlobexOpen(DateTime utc)
    {
        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (!IsGlobexClosed(utc)) return utc;
        var ct = TimeZoneInfo.ConvertTimeFromUtc(utc, Chicago);
        var day = ct.Date;
        // the open is at 17:00 from Sunday to Thursday; on Friday and Saturday it skips to Sunday
        while (true)
        {
            var open = day + Open;
            if (day.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday) && open > ct)
                return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(open, DateTimeKind.Unspecified), Chicago);
            day = day.AddDays(1);
        }
    }
}
