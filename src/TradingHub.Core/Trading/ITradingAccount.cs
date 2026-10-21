using TradingHub.Market;

namespace TradingHub.Trading;

/// <summary>
/// An account an instrument can be traded on: the simulated (paper) one or a real one (Binance...).
/// The DOM, the chart and the ticket trade against this interface without knowing which it is.
/// </summary>
public interface ITradingAccount
{
    string Name { get; }

    /// <summary>True if the orders go to the market with real money.</summary>
    bool IsReal { get; }

    Instrument Instrument { get; }
    Position Position { get; }
    IEnumerable<Order> ActiveOrders { get; }

    /// <summary>Last broker error (order rejected, etc.).</summary>
    string? LastError { get; }

    event Action? Changed;

    Order Market(OrderDirections dir, decimal qty, string comment = "");
    Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "");
    Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "");
    void Cancel(Order order);
    void CancelAt(decimal price);
    void CancelAll();
    void Modify(Order order, decimal newPrice);
    void Flatten();
    void Reverse();

    /// <summary>Last traded price of the instrument (0 if there is no data yet).</summary>
    decimal LastPrice => 0;

    /// <summary>Exit order (stop loss / target): it can only reduce the position, never reverse it.</summary>
    Order Exit(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment = "")
    {
        var o = type == OrderTypes.Stop ? Stop(dir, qty, price, comment) : Limit(dir, qty, price, comment);
        o.ReduceOnly = true;
        return o;
    }

    /// <summary>
    /// Changes an order's price and quantity. It returns the current order: the same one, or a new one if the
    /// broker does not allow modifying (it cancels and sends again).
    /// </summary>
    Order Replace(Order order, decimal newPrice, decimal newQuantity)
    {
        if (newQuantity == order.Unfilled)
        {
            Modify(order, newPrice);
            return order;
        }

        Cancel(order);
        var n = order.Type == OrderTypes.Stop ? Stop(order.Direction, newQuantity, newPrice, order.Comment) : Limit(order.Direction, newQuantity, newPrice, order.Comment);
        n.ReduceOnly = order.ReduceOnly;
        return n;
    }
}
