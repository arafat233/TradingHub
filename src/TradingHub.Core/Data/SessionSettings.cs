namespace TradingHub.Data;

/// <summary>
/// Defines where each "trading day" starts (for daily candles, session VWAP, profiles, IsNewSession).
/// By default: 00:00 UTC, as in the crypto markets.
/// </summary>
public sealed class SessionSettings
{
    public static readonly SessionSettings CryptoUtc = new();

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;
    public TimeSpan Start { get; init; } = TimeSpan.Zero;

    /// <summary>Session date (in the session's zone) a UTC instant belongs to.</summary>
    public DateOnly SessionDate(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone);
        var shifted = local - Start;
        return DateOnly.FromDateTime(shifted);
    }

    /// <summary>UTC instant a date's session starts at.</summary>
    public DateTime SessionStartUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue) + Start;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (TimeZone == TimeZoneInfo.Utc) return DateTime.SpecifyKind(local, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZone);
    }

    public bool IsNewSession(DateTime prevUtc, DateTime curUtc) => SessionDate(prevUtc) != SessionDate(curUtc);

    public bool IsNewWeek(DateTime prevUtc, DateTime curUtc)
    {
        var a = SessionDate(prevUtc);
        var b = SessionDate(curUtc);
        return WeekStart(a) != WeekStart(b);
    }

    public bool IsNewMonth(DateTime prevUtc, DateTime curUtc)
    {
        var a = SessionDate(prevUtc);
        var b = SessionDate(curUtc);
        return a.Month != b.Month || a.Year != b.Year;
    }

    public static DateOnly WeekStart(DateOnly d)
    {
        int diff = ((int)d.DayOfWeek + 6) % 7; // Monday = 0
        return d.AddDays(-diff);
    }
}
