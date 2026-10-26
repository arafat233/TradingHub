using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>A big market order found in recorded ticks (fills with the same time and side added up).</summary>
public sealed record MinedLevel(DateTime Time, decimal Price, decimal Volume, bool Buy);

/// <summary>
/// What happened after price came back to a level, read from ticks: when it touched, and for each size of move whether price
/// got there on the level's side (favourable: up for a buy) or against it first. The index arrays hold the tick at which each
/// threshold was first reached (<see cref="LevelMiner.Never"/> if it was not).
/// </summary>
public sealed record LevelTrace(DateTime TouchTime, int[] FirstFavourable, int[] FirstAgainst, decimal EndTicks, int EndIndex, bool Filled = true);

/// <summary>
/// Finds big orders in tick history and follows what price did around them, so levels can be judged by days of data instead
/// of only by what was seen live. A level counts as "touched" only after price has first gone <c>awayTicks</c> away from it
/// and then comes back (otherwise the fills right after the order would touch it at once).
/// </summary>
public static class LevelMiner
{
    public const int Never = int.MaxValue;

    /// <summary>
    /// Big orders: complete orders (same time and side) above a percentile of the orders before them, so the threshold adapts
    /// to the instrument. Nothing is reported until <paramref name="minOrders"/> orders have been seen.
    /// </summary>
    public static List<MinedLevel> FindOrders(IEnumerable<Trade> trades, decimal tickSize, int sample = 3000, double percentile = 99.7, int minOrders = 200)
    {
        var found = new List<MinedLevel>();
        var recent = new Queue<decimal>();
        decimal threshold = 0;
        int since = 0;
        long curTicks = long.MinValue;
        var curSide = TradeSide.None;
        decimal vol = 0, pv = 0;

        void Finish()
        {
            if (vol <= 0) return;
            if (threshold > 0 && vol > threshold)
            {
                decimal price = pv / vol;
                if (tickSize > 0) price = Math.Round(price / tickSize) * tickSize;
                found.Add(new MinedLevel(new DateTime(curTicks, DateTimeKind.Utc), price, vol, curSide == TradeSide.Buy));
            }

            recent.Enqueue(vol);
            while (recent.Count > sample) recent.Dequeue();
            if (recent.Count >= minOrders && (++since >= 100 || threshold == 0))
            {
                since = 0;
                var sorted = recent.OrderBy(v => v).ToArray();
                threshold = sorted[Math.Clamp((int)Math.Round(percentile / 100 * (sorted.Length - 1)), 0, sorted.Length - 1)];
            }
        }

        foreach (var t in trades)
        {
            if (t.Side == TradeSide.None || t.Volume <= 0) continue;
            long ticks = t.Time.Ticks;
            if (ticks != curTicks || t.Side != curSide)
            {
                Finish();
                curTicks = ticks;
                curSide = t.Side;
                vol = pv = 0;
            }

            vol += t.Volume;
            pv += t.Price * t.Volume;
        }

        Finish();
        return found;
    }

    /// <summary>
    /// Follows one level through the ticks (<paramref name="times"/> in time order, <paramref name="prices"/> in ticks of price).
    /// Null if price never came back within <paramref name="touchWindow"/>. After the touch it records, for every value in
    /// <paramref name="thresholds"/>, the first tick where price was that many ticks on the level's side and that many against it,
    /// for up to <paramref name="followWindow"/>.
    /// </summary>
    public static LevelTrace? Trace(long[] times, long[] prices, MinedLevel level, decimal tickSize, int awayTicks, int[] thresholds, TimeSpan touchWindow, TimeSpan followWindow, int fillThroughTicks = 0)
    {
        long L = (long)Math.Round(level.Price / tickSize);
        int start = Array.BinarySearch(times, level.Time.Ticks);
        if (start < 0) start = ~start;
        else while (start < times.Length && times[start] <= level.Time.Ticks) start++;
        long touchUntil = level.Time.Ticks + touchWindow.Ticks;
        int side = 0, touchAt = -1;
        for (int i = start; i < times.Length && times[i] <= touchUntil; i++)
        {
            long d = prices[i] - L;
            if (side == 0)
            {
                if (Math.Abs(d) >= awayTicks) side = Math.Sign(d);
            }
            else if (side > 0 ? d <= 0 : d >= 0)
            {
                touchAt = i;
                break;
            }
        }

        if (touchAt < 0) return null;
        int dir = level.Buy ? 1 : -1;
        var fav = Enumerable.Repeat(Never, thresholds.Length).ToArray();
        var against = Enumerable.Repeat(Never, thresholds.Length).ToArray();
        long touchUntilFill = times[touchAt] + followWindow.Ticks;
        if (fillThroughTicks > 0)
        {
            // pessimistic fill: a resting order at the level only fills once price trades some ticks beyond it (queue position),
            // and everything is measured from that moment
            int fillAt = -1;
            for (int i = touchAt; i < times.Length && times[i] <= touchUntilFill; i++)
                if (dir * (prices[i] - L) <= -fillThroughTicks) { fillAt = i; break; }
            if (fillAt < 0) return new LevelTrace(new DateTime(times[touchAt], DateTimeKind.Utc), fav, against, 0, touchAt, Filled: false);
            touchAt = fillAt;
        }

        long followUntil = times[touchAt] + followWindow.Ticks;
        int end = touchAt;
        long last = 0;
        for (int i = touchAt; i < times.Length && times[i] <= followUntil; i++)
        {
            long m = dir * (prices[i] - L); // ticks on the level's side
            last = m;
            end = i;
            for (int k = 0; k < thresholds.Length; k++)
            {
                if (m >= thresholds[k] && fav[k] == Never) fav[k] = i;
                if (-m >= thresholds[k] && against[k] == Never) against[k] = i;
            }
        }

        return new LevelTrace(new DateTime(times[touchAt], DateTimeKind.Utc), fav, against, last, end);
    }

    /// <summary>Held (1) if price got threshold <paramref name="k"/> ticks on the level's side before that many against it, broke (2) the other way, 0 if neither.</summary>
    public static int Outcome(LevelTrace t, int k) =>
        t.FirstFavourable[k] == t.FirstAgainst[k] ? 0 : t.FirstFavourable[k] < t.FirstAgainst[k] ? 1 : 2;

    /// <summary>Paper result in ticks of fading the level with a stop and a target (the stop wins a tie; neither = marked to the end of the follow window).</summary>
    public static decimal Simulate(LevelTrace t, int stopIndex, int stopTicks, int targetIndex, int targetTicks, int slipTicks = 0)
    {
        int stop = t.FirstAgainst[stopIndex], target = t.FirstFavourable[targetIndex];
        if (stop == Never && target == Never) return t.EndTicks - slipTicks;
        return target < stop ? targetTicks : -stopTicks - slipTicks;
    }

    /// <summary>The opposite trade: going with the break of the level instead of fading it (stop where the fade's target was, and the other way round).</summary>
    public static decimal SimulateFollow(LevelTrace t, int stopIndex, int stopTicks, int targetIndex, int targetTicks, int slipTicks = 0)
    {
        int stop = t.FirstFavourable[stopIndex], target = t.FirstAgainst[targetIndex];
        if (stop == Never && target == Never) return -t.EndTicks - slipTicks;
        return target < stop ? targetTicks : -stopTicks - slipTicks;
    }
}
