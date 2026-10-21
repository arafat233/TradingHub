namespace TradingHub.Market;

/// <summary>
/// A tradable instrument (BTCUSDT on Binance Futures, ES in the simulator, etc.).
/// The prices are handled internally as integer multiples of the tick ("ticks").
/// </summary>
public sealed class Instrument
{
    public required string Symbol { get; init; }

    /// <summary>Identifier of the feed that provides it (e.g. "binance-futures").</summary>
    public required string FeedId { get; init; }

    /// <summary>Readable market name (e.g. "Binance Futures").</summary>
    public string Exchange { get; init; } = "";

    public required decimal TickSize { get; init; }

    /// <summary>Minimum volume step (0.001 BTC, 1 contract, ...).</summary>
    public decimal VolumeStep { get; init; } = 1m;

    /// <summary>Monetary value of 1 point per 1 unit of volume (for PnL).</summary>
    public decimal PointValue { get; init; } = 1m;

    public string BaseAsset { get; init; } = "";
    public string QuoteAsset { get; init; } = "";
    public string Description { get; init; } = "";

    public string Key => $"{FeedId}:{Symbol}";

    public int PriceDecimals => DecimalPlaces(TickSize);
    public int VolumeDecimals => DecimalPlaces(VolumeStep);

    public long ToTicks(decimal price) => (long)Math.Round(price / TickSize, MidpointRounding.AwayFromZero);
    public decimal FromTicks(long ticks) => ticks * TickSize;

    public decimal RoundPrice(decimal price) => FromTicks(ToTicks(price));

    public string FormatPrice(decimal price) => price.ToString("F" + PriceDecimals, System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString() => Symbol;

    public static int DecimalPlaces(decimal value)
    {
        value = Math.Abs(value);
        if (value == 0) return 0;
        var normalized = value / 1.0000000000000000000000000000m; // strips trailing zeros
        var bits = decimal.GetBits(normalized);
        return (bits[3] >> 16) & 0xFF;
    }
}
