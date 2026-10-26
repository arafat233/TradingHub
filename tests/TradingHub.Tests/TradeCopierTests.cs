using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.Tests;

public class TradeCopierTests
{
    private sealed class FakeAccount : ITradingAccount
    {
        private readonly List<Order> _orders = new();
        public string Name { get; init; } = "acc";
        public bool IsReal => false;
        public Instrument Instrument { get; } = new() { Symbol = "ES", FeedId = "t", TickSize = 0.25m, VolumeStep = 1 };
        public Position Position { get; } = new();
        public IEnumerable<Order> ActiveOrders => _orders.ToList();
        public string? LastError { get; set; }
        public bool FillInstantly = true;
        public event Action? Changed;

        public void Raise() => Changed?.Invoke();

        public Order Market(OrderDirections dir, decimal qty, string comment = "")
        {
            var o = new Order { Direction = dir, Type = OrderTypes.Market, QuantityToFill = qty, Unfilled = qty, Comment = comment };
            if (FillInstantly) Position.Volume += dir == OrderDirections.Buy ? qty : -qty;
            else _orders.Add(o);
            Changed?.Invoke();
            return o;
        }

        public Order Limit(OrderDirections dir, decimal qty, decimal price, string comment = "") => Add(OrderTypes.Limit, dir, qty, price, comment);
        public Order Stop(OrderDirections dir, decimal qty, decimal trigger, string comment = "") => Add(OrderTypes.Stop, dir, qty, trigger, comment);

        private Order Add(OrderTypes type, OrderDirections dir, decimal qty, decimal price, string comment)
        {
            var o = new Order { Direction = dir, Type = type, QuantityToFill = qty, Unfilled = qty, Comment = comment };
            if (type == OrderTypes.Stop) o.TriggerPrice = price; else o.Price = price;
            _orders.Add(o);
            Changed?.Invoke();
            return o;
        }

        public void Cancel(Order order) { _orders.Remove(order); Changed?.Invoke(); }
        public void CancelAt(decimal price) { }
        public void CancelAll() { _orders.Clear(); }
        public void Modify(Order order, decimal newPrice) { }
        public void Flatten() { }
        public void Reverse() { }
    }

    [Fact]
    public void Follower_matches_leader_times_ratio_and_flips_with_it()
    {
        var lead = new FakeAccount { Name = "lead" };
        var fol = new FakeAccount { Name = "fol" };
        using var c = new TradeCopier(lead, new[] { ((ITradingAccount)fol, 2m) }, 0);
        c.Start();
        lead.Position.Volume = 3; lead.Raise();
        Assert.Equal(6, fol.Position.Volume);
        lead.Position.Volume = -1; lead.Raise();
        Assert.Equal(-2, fol.Position.Volume);
        lead.Position.Volume = 0; lead.Raise();
        Assert.Equal(0, fol.Position.Volume);
    }

    [Fact]
    public void Does_not_double_send_while_an_entry_is_working()
    {
        var lead = new FakeAccount();
        var fol = new FakeAccount { FillInstantly = false };
        using var c = new TradeCopier(lead, new[] { ((ITradingAccount)fol, 1m) }, 0);
        c.Start();
        lead.Position.Volume = 2; lead.Raise();
        lead.Raise(); c.Sync();
        Assert.Single(fol.ActiveOrders);
    }

    [Fact]
    public void Mirrors_stops_and_removes_them_when_the_leader_does()
    {
        var lead = new FakeAccount();
        var fol = new FakeAccount();
        using var c = new TradeCopier(lead, new[] { ((ITradingAccount)fol, 1m) }, 0);
        c.Start();
        lead.Position.Volume = 2; lead.Raise();
        var stop = ((ITradingAccount)lead).Exit(OrderTypes.Stop, OrderDirections.Sell, 2, 4990m);
        var mirrored = Assert.Single(fol.ActiveOrders);
        Assert.Equal(4990m, mirrored.TriggerPrice);
        Assert.True(mirrored.ReduceOnly);
        lead.Cancel(stop);
        Assert.Empty(fol.ActiveOrders);
    }

    [Fact]
    public void Stops_on_a_follower_error_or_a_position_over_the_limit()
    {
        var lead = new FakeAccount();
        var bad = new FakeAccount { LastError = "rejected" };
        var c = new TradeCopier(lead, new[] { ((ITradingAccount)bad, 1m) }, 0);
        c.Start();
        Assert.False(c.IsRunning);

        var fol = new FakeAccount();
        var c2 = new TradeCopier(lead, new[] { ((ITradingAccount)fol, 1m) }, 5);
        c2.Start();
        lead.Position.Volume = 9; lead.Raise();
        Assert.False(c2.IsRunning);
        Assert.Equal(0, fol.Position.Volume);
    }
}
