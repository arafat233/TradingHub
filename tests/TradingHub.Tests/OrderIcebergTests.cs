using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Tests;

public class OrderIcebergTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 14, 0, 0, DateTimeKind.Utc);
    private static OrderEvent Ev(double ms, OrderEventKind k, long id, decimal size, bool ask = true, long ticks = 4000) =>
        new(T0.AddMilliseconds(ms), k, id, ask, ticks, size);

    [Fact]
    public void A_new_order_of_the_same_size_right_after_each_fill_is_a_synthetic_iceberg()
    {
        var d = new OrderIcebergDetector();
        double t = 0;
        d.OnEvent(Ev(t, OrderEventKind.Add, 1, 10));
        for (long id = 1; id <= 5; id++)
        {
            t += 500;
            d.OnEvent(Ev(t, OrderEventKind.Fill, id, 0));       // buyers take the 10 lots
            d.OnEvent(Ev(t + 20, OrderEventKind.Add, id + 1, 10)); // 20 ms later the same size is back
        }

        var ev = Assert.Single(d.Events);
        Assert.True(ev.Ask);
        Assert.False(ev.Native);
        Assert.True(ev.Refills >= 3);
        Assert.Equal(50, ev.Volume);
    }

    [Fact]
    public void The_same_order_id_getting_its_size_back_after_trades_is_a_native_iceberg()
    {
        var d = new OrderIcebergDetector();
        d.OnEvent(Ev(0, OrderEventKind.Add, 7, 10, ask: false));
        for (int i = 1; i <= 3; i++)
        {
            d.OnEvent(Ev(i * 400, OrderEventKind.Modify, 7, 2, ask: false));        // sellers hit 8 lots
            d.OnEvent(Ev(i * 400 + 5, OrderEventKind.Modify, 7, 10, ask: false));   // the display size is refilled
        }

        var ev = Assert.Single(d.Events);
        Assert.False(ev.Ask);
        Assert.True(ev.Native);
        Assert.Equal(3, ev.Refills);
        Assert.Equal(24, ev.Volume);
    }

    [Fact]
    public void Ordinary_orders_are_not_flagged()
    {
        var d = new OrderIcebergDetector();
        d.OnEvent(Ev(0, OrderEventKind.Add, 1, 10));
        d.OnEvent(Ev(100, OrderEventKind.Fill, 1, 0));
        d.OnEvent(Ev(5000, OrderEventKind.Add, 2, 10));   // 5 s later: a different trader
        d.OnEvent(Ev(5100, OrderEventKind.Fill, 2, 0));
        d.OnEvent(Ev(5110, OrderEventKind.Add, 3, 200));  // quickly, but a very different size
        d.OnEvent(Ev(5200, OrderEventKind.Fill, 3, 0));
        d.OnEvent(Ev(5210, OrderEventKind.Add, 4, 10, ask: false)); // other side
        d.OnEvent(Ev(6000, OrderEventKind.Modify, 4, 5, ask: false));
        d.OnEvent(Ev(6010, OrderEventKind.Modify, 4, 4, ask: false)); // reduced, never refilled
        Assert.Empty(d.Events);
    }
}
