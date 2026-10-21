using TradingHub.Market;

namespace TradingHub.Feeds;

/// <summary>Feeds that can download a whole day of history (for the Data section and the backtests).</summary>
public interface IDayDownloader
{
    /// <summary>A whole UTC day in chronological order; null if there is no data that day.</summary>
    Task<List<Trade>?> DownloadDayAsync(Instrument instrument, DateOnly day, IProgress<double>? progress, CancellationToken ct);

    /// <summary>The market does not trade on Saturdays (futures, CFDs, stocks): there is no need to request them.</summary>
    bool ClosedOnSaturdays => true;

    /// <summary>Short text about where the history comes from (to show before downloading).</summary>
    string HistoryNote => "";
}

/// <summary>Helpers for building history: ids increasing by time, side by the tick rule and short candles to trades.</summary>
public static class HistoryBuilder
{
    /// <summary>Id increasing from the time (Unix microseconds x 8, same as Rithmic): it never repeats or goes backwards.</summary>
    public static long TimeId(DateTime utc, ref long last)
    {
        long us = (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        long id = us * 8;
        if (id <= last) id = last + 1;
        last = id;
        return id;
    }

    /// <summary>Side of a quote with no real trades (CFDs, forex): up = a buy, down = a sell, equal = the previous one.</summary>
    public static TradeSide TickRule(decimal price, ref decimal lastPrice, ref TradeSide lastSide)
    {
        var side = lastPrice == 0 ? TradeSide.Buy : price > lastPrice ? TradeSide.Buy : price < lastPrice ? TradeSide.Sell : lastSide;
        lastPrice = price;
        lastSide = side;
        return side;
    }

    /// <summary>
    /// Candles of a few seconds (for example OANDA's 5 s ones) to four trades each: the open, the extreme
    /// touched first based on the direction, the other extreme and the close. The (tick) volume is split among the
    /// four: that way the OHLC comes out exact on any period and the backtest's stops see the path inside the candle.
    /// </summary>
    public static void Synthesize(DateTime open, TimeSpan length, decimal o, decimal h, decimal l, decimal c, decimal volume, Instrument inst, ref long lastId, List<Trade> output)
    {
        if (volume <= 0) volume = 1;
        o = inst.RoundPrice(o);
        h = inst.RoundPrice(h);
        l = inst.RoundPrice(l);
        c = inst.RoundPrice(c);
        bool up = c >= o;
        decimal q = volume / 4;
        long step = length.Ticks / 4;
        output.Add(new Trade(TimeId(open, ref lastId), open, o, q, up ? TradeSide.Buy : TradeSide.Sell));
        output.Add(new Trade(TimeId(open.AddTicks(step), ref lastId), open.AddTicks(step), up ? l : h, q, up ? TradeSide.Sell : TradeSide.Buy));
        output.Add(new Trade(TimeId(open.AddTicks(step * 2), ref lastId), open.AddTicks(step * 2), up ? h : l, q, up ? TradeSide.Buy : TradeSide.Sell));
        output.Add(new Trade(TimeId(open.AddTicks(step * 3), ref lastId), open.AddTicks(step * 3), c, q, up ? TradeSide.Sell : TradeSide.Buy));
    }
}
