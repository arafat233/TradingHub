namespace TradingHub.Market;

public enum OrderEventKind
{
    /// <summary>A new order rests on the book.</summary>
    Add,

    /// <summary>An order changed its size or price (it keeps its id).</summary>
    Modify,

    /// <summary>The order left the book because it was cancelled.</summary>
    Cancel,

    /// <summary>The order left the book because it was filled.</summary>
    Fill,

    /// <summary>The order left the book and the feed does not say why (Bitfinex): the trades around it decide (see <see cref="TradingHub.Data.OrderFlowDetector"/>).</summary>
    Remove,
}

/// <summary>
/// One order-level (Level 3) change, from feeds that publish individual orders. <paramref name="Size"/> is the order's size after the event
/// (for Cancel, Fill and Remove: what it still had). <paramref name="Ask"/> is true for a resting sell.
/// </summary>
public readonly record struct OrderEvent(DateTime Time, OrderEventKind Kind, long OrderId, bool Ask, long PriceTicks, decimal Size);
