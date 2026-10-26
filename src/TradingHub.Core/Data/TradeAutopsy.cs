using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Data;

/// <summary>What the market was doing around one of your trades, measured on the recorded ticks.</summary>
public sealed record TradeContext(
    bool IsLong, decimal EntryPrice, decimal ExitPrice, decimal ResultTicks,
    decimal MaeTicks, decimal MfeTicks, TimeSpan TimeToMfe, TimeSpan TimeToMae,
    decimal? ExitEfficiency,
    decimal DeltaBefore5MinWithYou, decimal VolumeBurst, decimal? EntryPositionInRange,
    decimal? NearestLevelTicks, decimal? NearestLevelPrice, decimal AfterExit5MinTicks);

/// <summary>
/// The trade autopsy's measuring half: no opinions, only numbers. How far the trade went against you and for you, how much of the best
/// move you kept, whether the tape was pushing your way before you entered, whether you bought the high of the day or sold its low,
/// how close the nearest remembered level was, and what price did in the five minutes after you left. The words come from Claude.
/// </summary>
public static class TradeAutopsy
{
    /// <param name="ticks">Ticks in time order covering at least the 4 hours before the trade and 5 minutes after it.</param>
    /// <param name="levels">Prices of remembered levels (big trades, key levels) to measure the distance to.</param>
    public static TradeContext Analyze(AccountTrade t, IReadOnlyList<Trade> ticks, decimal tickSize, IReadOnlyList<decimal> levels)
    {
        int dir = t.IsLong ? 1 : -1;
        decimal ToTicks(decimal p) => p / tickSize;
        decimal mfe = 0, mae = 0;
        var tMfe = TimeSpan.Zero;
        var tMae = TimeSpan.Zero;
        decimal dayHi = decimal.MinValue, dayLo = decimal.MaxValue;
        decimal deltaBefore = 0, volLastMin = 0, vol30 = 0, after = 0;
        var afterEnd = t.CloseTime.AddMinutes(5);
        decimal lastInAfter = t.ClosePrice;
        foreach (var x in ticks)
        {
            if (x.Time > afterEnd) break;
            if (x.Time < t.OpenTime)
            {
                if (x.Time >= t.OpenTime.AddHours(-4)) { dayHi = Math.Max(dayHi, x.Price); dayLo = Math.Min(dayLo, x.Price); }
                if (x.Time >= t.OpenTime.AddMinutes(-5)) deltaBefore += (x.Side == TradeSide.Buy ? x.Volume : x.Side == TradeSide.Sell ? -x.Volume : 0) * dir;
                if (x.Time >= t.OpenTime.AddMinutes(-1)) volLastMin += x.Volume;
                else if (x.Time >= t.OpenTime.AddMinutes(-31)) vol30 += x.Volume;
            }
            else if (x.Time <= t.CloseTime)
            {
                decimal move = ToTicks((x.Price - t.OpenPrice) * dir);
                if (move > mfe) { mfe = move; tMfe = x.Time - t.OpenTime; }
                if (-move > mae) { mae = -move; tMae = x.Time - t.OpenTime; }
            }
            else lastInAfter = x.Price;
        }

        after = ToTicks((lastInAfter - t.ClosePrice) * dir);
        decimal result = ToTicks((t.ClosePrice - t.OpenPrice) * dir);
        decimal avgPerMin = vol30 / 30m;
        decimal? pos = dayHi > dayLo ? (t.OpenPrice - dayLo) / (dayHi - dayLo) : null;
        decimal? nearest = levels.Count == 0 ? null : levels.OrderBy(l => Math.Abs(l - t.OpenPrice)).First();
        return new TradeContext(
            t.IsLong, t.OpenPrice, t.ClosePrice, Math.Round(result, 1),
            Math.Round(mae, 1), Math.Round(mfe, 1), tMfe, tMae,
            mfe > 0 ? Math.Round(Math.Max(0, result) / mfe, 2) : null,
            Math.Round(deltaBefore, 2), avgPerMin > 0 ? Math.Round(volLastMin / avgPerMin, 1) : 0, pos is { } p ? Math.Round(p, 2) : null,
            nearest is { } n ? Math.Round(ToTicks(n - t.OpenPrice), 1) : null, nearest, Math.Round(after, 1));
    }
}
