namespace TradingHub.Market;

/// <summary>Aggressor side of a trade.</summary>
public enum TradeSide : byte
{
    /// <summary>Unknown / between bid and ask.</summary>
    None = 0,

    /// <summary>Aggressive buyer: it hit the ask (adds to the Ask volume).</summary>
    Buy = 1,

    /// <summary>Aggressive seller: it hit the bid (adds to the Bid volume).</summary>
    Sell = 2,
}

/// <summary>A trade (tick) from the tape.</summary>
public readonly struct Trade
{
    public readonly long Id;
    public readonly DateTime Time; // UTC
    public readonly decimal Price;
    public readonly decimal Volume;
    public readonly TradeSide Side;

    public Trade(long id, DateTime time, decimal price, decimal volume, TradeSide side)
    {
        Id = id;
        Time = time;
        Price = price;
        Volume = volume;
        Side = side;
    }

    public override string ToString() => $"{Time:HH:mm:ss.fff} {Side} {Volume} @ {Price} (#{Id})";
}
