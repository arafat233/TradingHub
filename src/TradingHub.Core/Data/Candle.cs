using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Candle with a full footprint: OHLC, bid/ask volume, delta and the price levels.
/// The prices are in instrument ticks; the footprint rows in "rows" (ticks / step).
/// </summary>
public sealed class Candle
{
    public DateTime Time;       // open (UTC)
    public DateTime LastTime;   // last trade (UTC)

    public long OpenTicks;
    public long HighTicks;
    public long LowTicks;
    public long CloseTicks;

    public decimal Volume;
    public decimal Bid;         // aggressive sell volume
    public decimal Ask;         // aggressive buy volume
    public decimal Between;
    public decimal MaxDelta;
    public decimal MinDelta;
    public int Trades;

    public decimal OI;
    public decimal MaxOI;
    public decimal MinOI;

    public int FirstTradeIndex = -1;
    public int LastTradeIndex = -1;

    /// <summary>Footprint rows.</summary>
    public readonly PriceLevels Levels = new();

    /// <summary>Row with the most volume (the candle's POC).</summary>
    public long PocRow;
    public decimal PocVolume;

    /// <summary>Volume traded in this candle by the largest single trade (for "big trades" filters).</summary>
    public decimal MaxTradeVolume;

    public bool IsClosed;

    /// <summary>It has at least one real trade (not built from exchange candles).</summary>
    public bool HasRealTrades;

    /// <summary>Candle built only from 1-minute candle history: real OHLC, volume and delta, with no footprint.</summary>
    public bool IsSynthetic => Trades > 0 && !HasRealTrades;

    public decimal Delta => Ask - Bid;

    /// <summary>Adds a trade to the candle. <paramref name="row"/> = priceTicks / step.</summary>
    public void AddTrade(long priceTicks, long row, decimal volume, TradeSide side, long timeTicks, int tradeIndex, bool synthetic = false)
    {
        if (!synthetic) HasRealTrades = true;
        if (Trades == 0)
        {
            OpenTicks = HighTicks = LowTicks = priceTicks;
            FirstTradeIndex = tradeIndex;
        }
        else
        {
            if (priceTicks > HighTicks) HighTicks = priceTicks;
            if (priceTicks < LowTicks) LowTicks = priceTicks;
        }

        CloseTicks = priceTicks;
        LastTradeIndex = tradeIndex;
        LastTime = new DateTime(timeTicks, DateTimeKind.Utc);
        Trades++;
        Volume += volume;
        if (volume > MaxTradeVolume && !synthetic) MaxTradeVolume = volume;

        if (synthetic)
        {
            // candle history: it adds to the OHLC, volume and delta, but does not invent footprint levels
            switch (side)
            {
                case TradeSide.Buy: Ask += volume; break;
                case TradeSide.Sell: Bid += volume; break;
                default: Between += volume; break;
            }

            if (!HasRealTrades) PocRow = row;
        }
        else
        {
            ref var level = ref Levels.GetOrAdd(row);
            switch (side)
            {
                case TradeSide.Buy:
                    Ask += volume;
                    level.Ask += volume;
                    break;
                case TradeSide.Sell:
                    Bid += volume;
                    level.Bid += volume;
                    break;
                default:
                    Between += volume;
                    level.Between += volume;
                    break;
            }

            level.Trades++;
            level.LastTimeTicks = timeTicks;

            var lv = level.Volume;
            if (lv > PocVolume || (lv == PocVolume && row == PocRow))
            {
                PocVolume = lv;
                PocRow = row;
            }
        }

        var d = Ask - Bid;
        if (Trades == 1)
        {
            MaxDelta = MinDelta = d;
        }
        else
        {
            if (d > MaxDelta) MaxDelta = d;
            if (d < MinDelta) MinDelta = d;
        }
    }
}
