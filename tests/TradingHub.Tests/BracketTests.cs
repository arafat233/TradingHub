using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;

namespace TradingHub.Tests;

/// <summary>Stop loss, partial targets, breakeven and OCO on the simulated account.</summary>
public class BracketTests
{
    private static readonly Instrument Inst = new() { Symbol = "TEST", FeedId = "test", TickSize = 1m, VolumeStep = 1m };
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Rig
    {
        private long _id = 1;
        private int _sec;
        public readonly InstrumentData Data = new(Inst, new SimulatedFeed());
        public readonly SimBroker Broker;
        public readonly BracketManager Brackets;
        public readonly List<string> Messages = new();

        public Rig(decimal price = 100)
        {
            Data.Trades.Add(new Trade(_id++, T0, price, 1, TradeSide.Buy));
            Broker = new SimBroker(Data, new Portfolio(), new TradingStatistics());
            Brackets = new BracketManager(Broker);
            Brackets.Message += Messages.Add;
        }

        /// <summary>Trades a price (as if it came from the market).</summary>
        public void Trade(decimal price, decimal vol = 1)
        {
            int start = Data.Trades.Count;
            Data.Trades.Add(new Trade(_id++, T0.AddSeconds(++_sec), price, vol, TradeSide.Buy));
            var field = typeof(InstrumentData).GetField("TradesAppended", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ((Action<int, int>?)field!.GetValue(Data))?.Invoke(start, 1);
        }

        public decimal Position => Broker.Position!.Volume;
    }

    [Fact]
    public void Stop_and_target_from_the_position_and_oco_when_the_target_fills()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Buy, 2);
        Assert.Equal(2m, r.Position);

        // towards the loss = stop, towards the profit = target
        Assert.StartsWith("Stop loss", r.Brackets.DropFromPosition(90));
        Assert.StartsWith("Target", r.Brackets.DropFromPosition(110));
        Assert.Equal(BracketRole.StopLoss, r.Brackets.RoleOf(r.Brackets.StopLoss!));
        Assert.Single(r.Brackets.Targets);
        Assert.Equal(2m, r.Brackets.Targets[0].Unfilled);

        // the target fills: the position goes flat and the stop cancels itself
        r.Trade(111);
        Assert.Equal(0m, r.Position);
        Assert.Empty(r.Broker.ActiveOrders);
        Assert.Contains(r.Messages, m => m.Contains("cancelled"));
    }

    [Fact]
    public void Wrong_sides_are_rejected()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Sell, 1);
        Assert.Contains("above", r.Brackets.SetStopLoss(95));
        Assert.Contains("below", r.Brackets.AddTarget(105));
        Assert.Empty(r.Broker.ActiveOrders);
    }

    [Fact]
    public void Partials_shrink_the_stop_and_move_to_breakeven()
    {
        var r = new Rig();
        r.Brackets.Template = new BracketTemplate
        {
            Enabled = true,
            StopTicks = 10,
            Targets = { },
            BreakevenAfterFirstTarget = true,
        };
        r.Brackets.Template.Targets.Clear();
        r.Brackets.Template.Targets.Add(new BracketTarget { Ticks = 5, Percent = 50 });
        r.Brackets.Template.Targets.Add(new BracketTarget { Ticks = 15, Percent = 50 });

        r.Broker.Market(OrderDirections.Buy, 4); // enters at 101 (last + 1 tick, with no book)
        decimal entry = r.Broker.Position!.AveragePrice;
        Assert.Equal(4m, r.Position);
        Assert.Equal(4m, r.Brackets.StopLoss!.Unfilled);
        Assert.Equal(entry - 10, r.Brackets.StopLoss!.TriggerPrice);
        Assert.Equal(2, r.Brackets.Targets.Count);
        Assert.Equal(4m, r.Brackets.Targets.Sum(t => t.Unfilled));

        // target 1 (half of it): the stop drops to 2 and moves to breakeven
        r.Trade(entry + 6);
        Assert.Equal(2m, r.Position);
        Assert.Equal(2m, r.Brackets.StopLoss!.Unfilled);
        Assert.Equal(entry, r.Brackets.StopLoss!.TriggerPrice);
        Assert.Single(r.Brackets.Targets);

        // it comes back to the entry: it exits at breakeven and nothing is left hanging
        r.Trade(entry);
        Assert.Equal(0m, r.Position);
        Assert.Empty(r.Broker.ActiveOrders);
    }

    [Fact]
    public void Closing_part_by_hand_adjusts_the_stop_and_targets()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Buy, 4);
        r.Brackets.SetStopLoss(90);
        r.Brackets.AddTarget(120);
        r.Brackets.ClosePartial(0.5m);
        Assert.Equal(2m, r.Position);
        Assert.Equal(2m, r.Brackets.StopLoss!.Unfilled);
        Assert.Equal(2m, r.Brackets.Targets.Sum(t => t.Unfilled));
    }

    [Fact]
    public void A_second_target_splits_the_first_into_partials()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Buy, 4);
        r.Brackets.AddTarget(110);
        r.Brackets.AddTarget(120);
        var targets = r.Brackets.Targets.OrderBy(t => t.Price).ToList();
        Assert.Equal(2, targets.Count);
        Assert.Equal(2m, targets[0].Unfilled);
        Assert.Equal(2m, targets[1].Unfilled);
        Assert.Equal(1, r.Brackets.TargetNumber(targets[0]));
        Assert.Equal(2, r.Brackets.TargetNumber(targets[1]));

        // changing one's quantity shrinks the other so the position is not exceeded
        r.Brackets.SetTargetFraction(targets[0], 0.75m);
        Assert.Equal(4m, r.Brackets.Targets.Sum(t => t.Unfilled));
        Assert.Equal(3m, r.Brackets.Targets.First(t => t.Price == 110).Unfilled);
    }

    [Fact]
    public void A_pending_order_with_an_attached_stop_and_target_activates_when_it_fills()
    {
        var r = new Rig();
        var entry = r.Broker.Limit(OrderDirections.Buy, 2, 95);
        Assert.Contains("Stop attached", r.Brackets.AttachToEntry(entry, 90));
        Assert.Contains("Target attached", r.Brackets.AttachToEntry(entry, 105));
        var pb = r.Brackets.PendingFor(entry)!;
        Assert.Equal(5, pb.StopTicks);
        Assert.Equal(10, pb.Targets[0].Ticks);

        // moving the entry moves the stop and the target too (they are stored in ticks)
        r.Brackets.Move(entry, 96);
        Assert.Equal(96m, r.Brackets.PendingFor(entry)!.EntryPrice);

        r.Trade(95); // it goes through the limit of 96
        Assert.Equal(2m, r.Position);
        Assert.Equal(91m, r.Brackets.StopLoss!.TriggerPrice);
        Assert.Equal(106m, r.Brackets.Targets.Single().Price);
        Assert.Empty(r.Brackets.Pending);
    }

    [Fact]
    public void The_stops_never_reverse_the_position()
    {
        var r = new Rig();
        r.Broker.Market(OrderDirections.Buy, 1);
        var exit = ((ITradingAccount)r.Broker).Exit(OrderTypes.Stop, OrderDirections.Sell, 5, 95);
        r.Trade(94);
        Assert.Equal(0m, r.Position); // it sells only what was there, it does not end up short
        Assert.NotEqual(OrderStates.Active, exit.State);
    }
}
