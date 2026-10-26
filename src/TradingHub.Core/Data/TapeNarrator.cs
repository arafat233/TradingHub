namespace TradingHub.Data;

/// <summary>What the last stretch of tape and book looked like (all numbers measured by the caller).</summary>
public sealed record TapeFacts(
    int WindowSeconds, decimal BuyVolume, decimal SellVolume, decimal ChangeTicks, decimal RangeTicks,
    decimal TypicalWindowVolume, decimal BiggestTradeVolume, bool BiggestTradeIsBuy, decimal BiggestTradePrice, decimal TypicalTradeVolume,
    decimal BidSize, decimal AskSize,
    bool IcebergActive, bool IcebergIsAsk, decimal IcebergPrice, decimal IcebergVolume);

/// <summary>
/// Says in one plain sentence what the tape is doing, from rules over numbers the app already measures. There is no model in this loop: every
/// word is backed by a number, so it cannot invent anything, and it is free to run all day. It picks the most telling thing, or stays quiet.
/// </summary>
public static class TapeNarrator
{
    public static string? Describe(TapeFacts f)
    {
        decimal total = f.BuyVolume + f.SellVolume;
        decimal buyShare = total > 0 ? f.BuyVolume / total : 0.5m;
        bool heavy = f.TypicalWindowVolume > 0 && total >= 2 * f.TypicalWindowVolume;

        if (f.IcebergActive)
            return f.IcebergIsAsk
                ? $"A resting seller at {f.IcebergPrice:0.####} keeps refilling: {f.IcebergVolume:0.##} bought into it and price {(f.ChangeTicks > 2 ? "is still pushing through" : "is stuck below it")}."
                : $"A resting buyer at {f.IcebergPrice:0.####} keeps refilling: {f.IcebergVolume:0.##} sold into it and price {(f.ChangeTicks < -2 ? "is still pushing through" : "is holding above it")}.";

        if (heavy && Math.Abs(f.ChangeTicks) <= 2 && (buyShare >= 0.68m || buyShare <= 0.32m))
            return buyShare >= 0.5m
                ? $"Buyers are hitting hard ({buyShare:P0} of {total:0.##} in {f.WindowSeconds} s) but price barely moved: someone is absorbing."
                : $"Sellers are hitting hard ({1 - buyShare:P0} of {total:0.##} in {f.WindowSeconds} s) but price barely moved: someone is absorbing.";

        if (Math.Abs(f.ChangeTicks) >= 8 && (buyShare >= 0.65m || buyShare <= 0.35m) && (f.ChangeTicks > 0) == (buyShare > 0.5m))
            return f.ChangeTicks > 0
                ? $"Buyers are lifting offers: {f.ChangeTicks:+0.#} ticks in {f.WindowSeconds} s, {buyShare:P0} of the volume on the ask."
                : $"Sellers are hitting bids: {f.ChangeTicks:0.#} ticks in {f.WindowSeconds} s, {1 - buyShare:P0} of the volume on the bid.";

        if (f.TypicalTradeVolume > 0 && f.BiggestTradeVolume >= 8 * f.TypicalTradeVolume && f.BiggestTradeVolume > 0)
            return $"Large {(f.BiggestTradeIsBuy ? "buy" : "sell")}: {f.BiggestTradeVolume:0.##} at {f.BiggestTradePrice:0.####}, about {f.BiggestTradeVolume / f.TypicalTradeVolume:0}× the usual trade.";

        decimal minSide = Math.Min(f.BidSize, f.AskSize);
        if (minSide > 0 && Math.Max(f.BidSize, f.AskSize) / minSide >= 3)
            return f.BidSize > f.AskSize
                ? $"Bids are stacked {f.BidSize / f.AskSize:0.#}× the offers near the touch."
                : $"Offers are stacked {f.AskSize / f.BidSize:0.#}× the bids near the touch.";

        return null;
    }
}
