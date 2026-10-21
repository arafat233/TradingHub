using System.Globalization;

namespace TradingHub.Data;

public enum PeriodType
{
    Seconds,
    Minutes,
    Hours,
    Days,
    Weeks,
    Months,
    /// <summary>N trades per candle.</summary>
    Tick,
    /// <summary>N of volume per candle.</summary>
    Volume,
    /// <summary>Range of N ticks per candle.</summary>
    Range,
    /// <summary>Delta of N per candle (the candle closes when |delta| reaches N).</summary>
    Delta,
    /// <summary>Renko of N ticks.</summary>
    Renko,
}

/// <summary>Chart period (e.g. 5 minutes, 1000 ticks, range of 20 ticks).</summary>
public readonly record struct ChartPeriod(PeriodType Type, decimal Value)
{
    public static ChartPeriod M1 => new(PeriodType.Minutes, 1);
    public static ChartPeriod M5 => new(PeriodType.Minutes, 5);
    public static ChartPeriod M15 => new(PeriodType.Minutes, 15);
    public static ChartPeriod M30 => new(PeriodType.Minutes, 30);
    public static ChartPeriod H1 => new(PeriodType.Hours, 1);
    public static ChartPeriod H4 => new(PeriodType.Hours, 4);
    public static ChartPeriod D1 => new(PeriodType.Days, 1);

    public bool IsTimeBased => Type is PeriodType.Seconds or PeriodType.Minutes or PeriodType.Hours or PeriodType.Days or PeriodType.Weeks or PeriodType.Months;

    /// <summary>Fixed duration for time periods (months ~ 30 days, for estimates only).</summary>
    public TimeSpan ApproxDuration => Type switch
    {
        PeriodType.Seconds => TimeSpan.FromSeconds((double)Value),
        PeriodType.Minutes => TimeSpan.FromMinutes((double)Value),
        PeriodType.Hours => TimeSpan.FromHours((double)Value),
        PeriodType.Days => TimeSpan.FromDays((double)Value),
        PeriodType.Weeks => TimeSpan.FromDays(7 * (double)Value),
        PeriodType.Months => TimeSpan.FromDays(30 * (double)Value),
        _ => TimeSpan.Zero,
    };

    /// <summary>Short ATAS-style name: M1, M5, H1, Daily, Tick500, Range10...</summary>
    public string Code
    {
        get
        {
            var v = Value.ToString("0.########", CultureInfo.InvariantCulture);
            return Type switch
            {
                PeriodType.Seconds => $"S{v}",
                PeriodType.Minutes => $"M{v}",
                PeriodType.Hours => $"H{v}",
                PeriodType.Days => Value == 1 ? "Daily" : $"D{v}",
                PeriodType.Weeks => Value == 1 ? "Weekly" : $"W{v}",
                PeriodType.Months => Value == 1 ? "Monthly" : $"Mo{v}",
                PeriodType.Tick => $"Tick{v}",
                PeriodType.Volume => $"Volume{v}",
                PeriodType.Range => $"Range{v}",
                PeriodType.Delta => $"Delta{v}",
                PeriodType.Renko => $"Renko{v}",
                _ => v,
            };
        }
    }

    /// <summary>Name to show in the UI.</summary>
    public string DisplayName
    {
        get
        {
            var v = Value.ToString("0.########", CultureInfo.InvariantCulture);
            return Type switch
            {
                PeriodType.Seconds => $"{v}s",
                PeriodType.Minutes => $"{v}m",
                PeriodType.Hours => $"{v}h",
                PeriodType.Days => Value == 1 ? "D" : $"{v}D",
                PeriodType.Weeks => Value == 1 ? "S" : $"{v}S",
                PeriodType.Months => Value == 1 ? "Month" : $"{v} months",
                PeriodType.Tick => $"{v} ticks",
                PeriodType.Volume => $"Vol {v}",
                PeriodType.Range => $"Range {v}",
                PeriodType.Delta => $"Delta {v}",
                PeriodType.Renko => $"Renko {v}",
                _ => v,
            };
        }
    }

    public override string ToString() => Code;

    public static ChartPeriod Parse(string code)
    {
        if (TryParse(code, out var p)) return p;
        throw new FormatException($"Invalid period: {code}");
    }

    /// <summary>
    /// Parses a hand-written period: the usual codes (M5, H1, D1, Daily, Tick500, Range20…) regardless of
    /// case, what the chart's toolbar shows (5m, 1h, D, 2D) and Spanish names (diario, semanal, mensual).
    /// </summary>
    public static bool TryParse(string? code, out ChartPeriod period)
    {
        period = M1;
        if (string.IsNullOrWhiteSpace(code)) return false;
        code = code.Trim().Replace(" ", "");
        switch (code.ToLowerInvariant())
        {
            case "daily" or "diario" or "diaria" or "d" or "1d" or "day":
                period = D1;
                return true;
            case "weekly" or "semanal" or "w" or "1w" or "week":
                period = new(PeriodType.Weeks, 1);
                return true;
            case "monthly" or "mensual" or "mo" or "1mo" or "month":
                period = new(PeriodType.Months, 1);
                return true;
        }

        (string prefix, PeriodType type)[] map =
        {
            ("Volume", PeriodType.Volume), ("Range", PeriodType.Range), ("Renko", PeriodType.Renko),
            ("Delta", PeriodType.Delta), ("Tick", PeriodType.Tick), ("Mo", PeriodType.Months),
            ("S", PeriodType.Seconds), ("M", PeriodType.Minutes), ("H", PeriodType.Hours),
            ("D", PeriodType.Days), ("W", PeriodType.Weeks),
        };
        foreach (var (prefix, type) in map)
        {
            if (code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                decimal.TryParse(code.AsSpan(prefix.Length), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0)
            {
                period = new ChartPeriod(type, v);
                return true;
            }
        }

        // the way the chart's toolbar shows it: the number first (30s, 5m, 1h, 2D, 3W, 2mo)
        (string suffix, PeriodType type)[] suffixes =
        {
            ("mo", PeriodType.Months), ("s", PeriodType.Seconds), ("m", PeriodType.Minutes), ("h", PeriodType.Hours),
            ("d", PeriodType.Days), ("w", PeriodType.Weeks),
        };
        foreach (var (suffix, type) in suffixes)
        {
            if (code.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                decimal.TryParse(code.AsSpan(0, code.Length - suffix.Length), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0)
            {
                period = new ChartPeriod(type, v);
                return true;
            }
        }

        return false;
    }
}
