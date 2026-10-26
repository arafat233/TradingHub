using TradingHub.Market;

namespace TradingHub.Trading;

/// <summary>
/// Mirrors one account (the leader) onto others on the same instrument. It is position-based: after every change it compares each follower's
/// position with <c>leader × ratio</c> and sends the market order that closes the difference, so a missed event or a restart heals itself.
/// The leader's stops and targets (reduce-only orders) are mirrored too, as the follower's own orders tagged <see cref="Tag"/>.
/// Any follower error or a position above <see cref="MaxPosition"/> stops the copier: it never keeps trading on a bad state.
/// </summary>
public sealed class TradeCopier : IDisposable
{
    public const string Tag = "copier";

    private readonly ITradingAccount _leader;
    private readonly List<(ITradingAccount Account, decimal Ratio)> _followers;
    private readonly object _gate = new();
    private bool _running;

    public TradeCopier(ITradingAccount leader, IEnumerable<(ITradingAccount Account, decimal Ratio)> followers, decimal maxPosition)
    {
        _leader = leader;
        _followers = followers.Where(f => f.Account != leader && f.Ratio > 0).ToList();
        MaxPosition = maxPosition;
    }

    /// <summary>Largest position a follower may be asked to hold (0 = no limit).</summary>
    public decimal MaxPosition { get; }

    public bool IsRunning => _running;

    /// <summary>Progress and the reason it stopped.</summary>
    public event Action<string>? Message;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _leader.Changed += OnChanged;
        foreach (var f in _followers) f.Account.Changed += OnChanged;
        Message?.Invoke($"Copying {_leader.Name} → {string.Join(", ", _followers.Select(f => $"{f.Account.Name} ×{f.Ratio}"))}");
        Sync();
    }

    public void Stop(string reason = "Copier stopped")
    {
        if (!_running) return;
        _running = false;
        _leader.Changed -= OnChanged;
        foreach (var f in _followers) f.Account.Changed -= OnChanged;
        Message?.Invoke(reason);
    }

    public void Dispose() => Stop();

    private void OnChanged() => Sync();

    /// <summary>One pass over all followers (also called by tests).</summary>
    public void Sync()
    {
        lock (_gate)
        {
            if (!_running) return;
            try
            {
                foreach (var (acc, ratio) in _followers)
                {
                    if (acc.LastError is { Length: > 0 } err)
                    {
                        Stop($"Copier stopped: {acc.Name} reported \"{err}\"");
                        return;
                    }

                    if (!SyncOne(acc, ratio)) return;
                }
            }
            catch (Exception ex)
            {
                Stop($"Copier stopped: {ex.Message}");
            }
        }
    }

    private bool SyncOne(ITradingAccount f, decimal ratio)
    {
        decimal step = f.Instrument.VolumeStep > 0 ? f.Instrument.VolumeStep : 1m;
        decimal target = Math.Round(_leader.Position.Volume * ratio / step, MidpointRounding.ToZero) * step;
        if (MaxPosition > 0 && Math.Abs(target) > MaxPosition)
        {
            Stop($"Copier stopped: {f.Name} would need {Math.Abs(target)}, above the limit of {MaxPosition}");
            return false;
        }

        // an entry we sent is still working: wait for it instead of sending the same difference twice
        if (f.ActiveOrders.Any(o => o.Comment == Tag && !o.ReduceOnly)) return true;

        decimal diff = target - f.Position.Volume;
        if (Math.Abs(diff) >= step)
        {
            f.Market(diff > 0 ? OrderDirections.Buy : OrderDirections.Sell, Math.Abs(diff), Tag);
            return true; // exits are placed once the fill brings the position to the target
        }

        MirrorExits(f, ratio, step);
        return true;
    }

    private static decimal ExitPrice(Order o) => o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price;

    // The reduce-only flag is set after the order is placed (and Changed fires before that), so an order against the leader's position that
    // is not bigger than it also counts as an exit.
    private bool IsExit(Order o)
    {
        decimal pos = _leader.Position.Volume;
        return o.ReduceOnly || (pos != 0 && (pos > 0) == (o.Direction == OrderDirections.Sell) && o.Unfilled <= Math.Abs(pos));
    }

    private void MirrorExits(ITradingAccount f, decimal ratio, decimal step)
    {
        decimal held = Math.Abs(f.Position.Volume);
        var wanted = held == 0
            ? new List<(OrderDirections Dir, OrderTypes Type, decimal Price, decimal Qty)>()
            : _leader.ActiveOrders.Where(o => IsExit(o) && o.Type is OrderTypes.Stop or OrderTypes.Limit)
                .Select(o => (Dir: o.Direction, Type: o.Type, Price: ExitPrice(o), Qty: Math.Min(held, Math.Round(o.Unfilled * ratio / step, MidpointRounding.ToZero) * step)))
                .Where(w => w.Qty > 0).ToList();

        var mine = f.ActiveOrders.Where(o => o.Comment == Tag && o.ReduceOnly).ToList();
        foreach (var o in mine)
        {
            int i = wanted.FindIndex(w => w.Dir == o.Direction && w.Type == o.Type && w.Price == ExitPrice(o) && w.Qty == o.Unfilled);
            if (i >= 0) wanted.RemoveAt(i);
            else f.Cancel(o);
        }

        foreach (var w in wanted) f.Exit(w.Type, w.Dir, w.Qty, w.Price, Tag);
    }
}
