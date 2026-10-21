using TradingHub.Market;

namespace TradingHub.Trading;

/// <summary>A target of the template: how many ticks from the entry and what share of the position it closes.</summary>
public sealed class BracketTarget
{
    public int Ticks { get; set; } = 40;
    public decimal Percent { get; set; } = 100;
}

/// <summary>Automatic stop loss and (partial) targets, in ticks from the entry.</summary>
public sealed class BracketTemplate
{
    /// <summary>Whether they are placed on their own when a position opens or when an entry order is sent.</summary>
    public bool Enabled { get; set; }

    public int StopTicks { get; set; } = 20;
    public List<BracketTarget> Targets { get; set; } = new() { new BracketTarget { Ticks = 40, Percent = 100 } };

    /// <summary>When the first target fills, the stop moves to the entry price.</summary>
    public bool BreakevenAfterFirstTarget { get; set; } = true;

    /// <summary>Favorable ticks above the entry when moving to breakeven (it covers commissions).</summary>
    public int BreakevenOffsetTicks { get; set; }

    public BracketTemplate Clone() => new()
    {
        Enabled = Enabled,
        StopTicks = StopTicks,
        BreakevenAfterFirstTarget = BreakevenAfterFirstTarget,
        BreakevenOffsetTicks = BreakevenOffsetTicks,
        Targets = Targets.Select(t => new BracketTarget { Ticks = t.Ticks, Percent = t.Percent }).ToList(),
    };
}

public enum BracketRole
{
    None,
    StopLoss,
    Target,
}

/// <summary>Stop and targets attached to a pending entry order: they are sent when it fills.</summary>
public sealed class PendingBracket
{
    public required Order Entry { get; set; }

    /// <summary>Distance from the stop to the entry, in ticks (null = no stop).</summary>
    public int? StopTicks { get; set; }

    /// <summary>Targets: favorable distance in ticks and percentage of the position.</summary>
    public List<BracketTarget> Targets { get; } = new();

    public decimal EntryPrice => Entry.Type == OrderTypes.Stop ? Entry.TriggerPrice : Entry.Price;

    /// <summary>+1 if the entry buys (long position), -1 if it sells.</summary>
    public int Side => Entry.Direction == OrderDirections.Buy ? 1 : -1;
}

/// <summary>
/// Manages the stop loss and the targets of an account's position: it creates them when they are dragged from the
/// position or from a pending order, moves them, adjusts their quantities when a part is closed
/// (partials), moves the stop to breakeven after the first target and cancels whatever is left when the
/// position closes (OCO). It works the same with the simulated account and with remote brokers.
/// </summary>
public sealed class BracketManager : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Order> _stops = new();
    private readonly List<Order> _targets = new();
    private readonly List<PendingBracket> _pending = new();
    private readonly HashSet<Order> _cancelled = new(ReferenceEqualityComparer.Instance);
    private decimal _lastVolume;
    private decimal _entryPrice;
    private int _targetHits;
    private bool _busy;

    public BracketManager(ITradingAccount account)
    {
        Account = account;
        _lastVolume = account.Position.Volume;
        _entryPrice = account.Position.AveragePrice;
        account.Changed += OnAccountChanged;
    }

    public ITradingAccount Account { get; }

    /// <summary>Instrument template (stop, partial targets, breakeven).</summary>
    public BracketTemplate Template { get; set; } = new();

    /// <summary>Something changed (to redraw). It can arrive from another thread.</summary>
    public event Action? Changed;

    /// <summary>Notices for the status bar (OCO, breakeven…). They can arrive from another thread.</summary>
    public event Action<string>? Message;

    private Instrument Inst => Account.Instrument;
    private decimal Volume => Account.Position.Volume;
    private OrderDirections ExitDirection => Volume > 0 ? OrderDirections.Sell : OrderDirections.Buy;

    // ===================================================================== queries

    private static bool Same(Order a, Order b) =>
        ReferenceEquals(a, b) || (a.ExchangeOrderId.Length > 0 && a.ExchangeOrderId == b.ExchangeOrderId);

    public static decimal PriceOf(Order o) => o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price;

    /// <summary>The current version of a tracked order, or null if it is no longer active.</summary>
    private Order? Live(Order tracked)
    {
        foreach (var a in Account.ActiveOrders)
            if (Same(a, tracked)) return a;

        // just sent to a remote broker: it does not show up in the open orders list yet
        if (tracked.State == OrderStates.Active && tracked.ExchangeOrderId.Length == 0 && Account is not SimBroker &&
            DateTime.UtcNow - tracked.Time < TimeSpan.FromSeconds(8)) return tracked;
        return null;
    }

    private List<(Order Tracked, Order Live)> LiveOf(List<Order> list)
    {
        var r = new List<(Order, Order)>();
        foreach (var t in list)
            if (Live(t) is { } l) r.Add((t, l));
        return r;
    }

    public BracketRole RoleOf(Order o)
    {
        lock (_gate)
        {
            if (_stops.Any(s => Same(s, o))) return BracketRole.StopLoss;
            if (_targets.Any(t => Same(t, o))) return BracketRole.Target;
            return BracketRole.None;
        }
    }

    /// <summary>Number of the target (1 = the closest to the entry), or 0 if it is not a target.</summary>
    public int TargetNumber(Order o)
    {
        lock (_gate)
        {
            var live = LiveOf(_targets).Select(x => x.Live).OrderBy(x => Math.Abs(PriceOf(x) - _entryPrice)).ToList();
            int i = live.FindIndex(x => Same(x, o));
            return i + 1;
        }
    }

    public Order? StopLoss
    {
        get
        {
            lock (_gate) return LiveOf(_stops).Select(x => x.Live).FirstOrDefault();
        }
    }

    public IReadOnlyList<Order> Targets
    {
        get
        {
            lock (_gate) return LiveOf(_targets).Select(x => x.Live).ToList();
        }
    }

    /// <summary>Entry price of the open position.</summary>
    public decimal EntryPrice => Volume != 0 ? Account.Position.AveragePrice : _entryPrice;

    public IReadOnlyList<PendingBracket> Pending
    {
        get
        {
            lock (_gate) return _pending.ToList();
        }
    }

    public PendingBracket? PendingFor(Order entry)
    {
        lock (_gate) return _pending.FirstOrDefault(p => Same(p.Entry, entry));
    }

    /// <summary>Result in money if the position (or the given quantity) were closed at that price.</summary>
    public decimal PnLAt(decimal price, decimal? quantity = null, decimal? entry = null, int? side = null)
    {
        decimal e = entry ?? EntryPrice;
        int s = side ?? Math.Sign(Volume);
        decimal q = quantity ?? Math.Abs(Volume);
        return (price - e) * q * s * Inst.PointValue;
    }

    // ===================================================================== actions

    /// <summary>Runs an action with the account locked and synchronizes afterwards (OCO, partials).</summary>
    private T Run<T>(Func<T> op)
    {
        T result;
        lock (_gate)
        {
            bool outer = !_busy;
            _busy = true;
            try
            {
                result = op();
            }
            finally
            {
                if (outer) _busy = false;
            }

            if (outer) SyncLoop();
        }

        Changed?.Invoke();
        return result;
    }

    private decimal RoundQty(decimal q)
    {
        decimal step = Inst.VolumeStep > 0 ? Inst.VolumeStep : 1;
        return Math.Floor(q / step) * step;
    }

    /// <summary>Error if the price is on the wrong side (for a long: stop below, target above).</summary>
    private string? Validate(BracketRole role, decimal price, int side, decimal reference)
    {
        if (reference <= 0) return null;
        bool below = price < reference;
        bool above = price > reference;
        return role switch
        {
            BracketRole.StopLoss when side > 0 && !below => "A long's stop goes below the current price.",
            BracketRole.StopLoss when side < 0 && !above => "A short's stop goes above the current price.",
            BracketRole.Target when side > 0 && !above => "A long's target goes above the current price.",
            BracketRole.Target when side < 0 && !below => "A short's target goes below the current price.",
            _ => null,
        };
    }

    /// <summary>Drag from the position line: towards profit it creates a target, towards loss the stop.</summary>
    public string DropFromPosition(decimal price, BracketRole role = BracketRole.None)
    {
        decimal v = Volume;
        if (v == 0) return "There is no open position.";
        if (role == BracketRole.None)
        {
            decimal last = Account.LastPrice > 0 ? Account.LastPrice : EntryPrice;
            if (price == last) return "Drop it above or below the current price.";
            role = (price > last) == (v > 0) ? BracketRole.Target : BracketRole.StopLoss;
        }

        return role == BracketRole.StopLoss ? SetStopLoss(price) : AddTarget(price);
    }

    public string SetStopLoss(decimal price) => Run(() =>
    {
        decimal v = Volume;
        if (v == 0) return "There is no open position.";
        price = Inst.RoundPrice(price);
        if (Validate(BracketRole.StopLoss, price, Math.Sign(v), Account.LastPrice) is { } err) return err;
        var live = LiveOf(_stops).FirstOrDefault();
        if (live.Live != null)
        {
            var n = Account.Replace(live.Live, price, Math.Abs(v));
            Swap(_stops, live.Tracked, n);
        }
        else
        {
            _stops.Clear();
            _stops.Add(Account.Exit(OrderTypes.Stop, ExitDirection, Math.Abs(v), price, "SL"));
        }

        return $"Stop loss at {Inst.FormatPrice(price)} ({FormatMoney(PnLAt(price))})";
    });

    /// <summary>
    /// Adds a target. With no quantity: it covers what is left of the position; if the targets already cover
    /// all of it, it splits the biggest one in half (partial).
    /// </summary>
    public string AddTarget(decimal price, decimal? quantity = null) => Run(() =>
    {
        decimal v = Volume;
        if (v == 0) return "There is no open position.";
        price = Inst.RoundPrice(price);
        if (Validate(BracketRole.Target, price, Math.Sign(v), Account.LastPrice) is { } err) return err;
        decimal abs = Math.Abs(v);
        var live = LiveOf(_targets);
        decimal covered = live.Sum(t => t.Live.Unfilled);
        decimal qty;
        if (quantity is { } q)
        {
            qty = RoundQty(Math.Min(q, abs));
            ReduceTargets(abs - qty, null);
        }
        else if (covered < abs)
        {
            qty = abs - covered;
        }
        else
        {
            var biggest = live.OrderByDescending(t => t.Live.Unfilled).First();
            decimal half = RoundQty(biggest.Live.Unfilled / 2);
            if (half <= 0) return "The quantity cannot be split any further: move the target that already exists.";
            var n = Account.Replace(biggest.Live, PriceOf(biggest.Live), biggest.Live.Unfilled - half);
            Swap(_targets, biggest.Tracked, n);
            qty = half;
        }

        if (qty <= 0) return "The target's quantity came out as zero.";
        _targets.Add(Account.Exit(OrderTypes.Limit, ExitDirection, qty, price, "TP"));
        return $"Target of {Fmt(qty)} at {Inst.FormatPrice(price)} ({FormatMoney(PnLAt(price, qty))})";
    });

    /// <summary>Moves an order: stop, target, entry with brackets or any other.</summary>
    public string Move(Order order, decimal price) => Run(() =>
    {
        price = Inst.RoundPrice(price);
        var role = _stops.Any(s => Same(s, order)) ? BracketRole.StopLoss : _targets.Any(t => Same(t, order)) ? BracketRole.Target : BracketRole.None;
        if (role != BracketRole.None)
        {
            if (Validate(role, price, Math.Sign(Volume), Account.LastPrice) is { } err) return err;
            var list = role == BracketRole.StopLoss ? _stops : _targets;
            var tracked = list.First(s => Same(s, order));
            var live = Live(tracked) ?? order;
            Swap(list, tracked, Account.Replace(live, price, live.Unfilled));
            return $"{(role == BracketRole.StopLoss ? "Stop" : "Target")} moved to {Inst.FormatPrice(price)}";
        }

        var pb = _pending.FirstOrDefault(p => Same(p.Entry, order));
        if (pb != null)
        {
            pb.Entry = Account.Replace(order, price, order.Unfilled);
            return $"Order moved to {Inst.FormatPrice(price)} (the stop and the targets follow it)";
        }

        Account.Modify(order, price);
        return $"Order moved to {Inst.FormatPrice(price)}";
    });

    /// <summary>Changes a target's quantity to a fraction of the position (the others shrink if needed).</summary>
    public string SetTargetFraction(Order target, decimal fraction) => Run(() =>
    {
        decimal abs = Math.Abs(Volume);
        var tracked = _targets.FirstOrDefault(t => Same(t, target));
        if (abs == 0 || tracked == null || Live(tracked) is not { } live) return "It is not an active target.";
        decimal qty = RoundQty(abs * fraction);
        if (qty <= 0) return "That fraction comes out as zero with the position's quantity.";
        var n = Account.Replace(live, PriceOf(live), qty);
        Swap(_targets, tracked, n);
        ReduceTargets(abs, n);
        return $"Target now closes {Fmt(qty)}";
    });

    public string CancelBracket(Order order) => Run(() =>
    {
        foreach (var list in new[] { _stops, _targets })
        {
            var tracked = list.FirstOrDefault(t => Same(t, order));
            if (tracked == null) continue;
            if (Live(tracked) is { } live)
            {
                _cancelled.Add(tracked);
                Account.Cancel(live);
            }

            list.Remove(tracked);
            return "Order cancelled";
        }

        Account.Cancel(order);
        _pending.RemoveAll(p => Same(p.Entry, order));
        return "Order cancelled";
    });

    /// <summary>Moves the stop to the entry price (plus the template's margin).</summary>
    public string MoveStopToBreakeven() => Run(MoveStopToBreakevenCore);

    private string MoveStopToBreakevenCore()
    {
        decimal v = Volume;
        if (v == 0) return "There is no open position.";
        decimal be = Inst.RoundPrice(EntryPrice + Math.Sign(v) * Template.BreakevenOffsetTicks * Inst.TickSize);
        if (Validate(BracketRole.StopLoss, be, Math.Sign(v), Account.LastPrice) != null)
            return "There is not enough profit yet to put the stop at breakeven.";
        var live = LiveOf(_stops).FirstOrDefault();
        if (live.Live != null) Swap(_stops, live.Tracked, Account.Replace(live.Live, be, Math.Abs(v)));
        else _stops.Add(Account.Exit(OrderTypes.Stop, ExitDirection, Math.Abs(v), be, "SL"));
        return $"Stop at breakeven ({Inst.FormatPrice(be)})";
    }

    /// <summary>Closes part of the position at market (0.5 = half).</summary>
    public string ClosePartial(decimal fraction) => Run(() =>
    {
        decimal abs = Math.Abs(Volume);
        if (abs == 0) return "There is no open position.";
        decimal qty = fraction >= 1 ? abs : RoundQty(abs * fraction);
        if (qty <= 0) qty = Math.Min(abs, Inst.VolumeStep > 0 ? Inst.VolumeStep : 1);
        Account.Market(ExitDirection, qty, "Partial");
        return $"Closed {Fmt(qty)} of {Fmt(abs)} at market";
    });

    /// <summary>Places the template's stop and targets on the open position.</summary>
    public string ApplyTemplate() => Run(ApplyTemplateCore);

    private string ApplyTemplateCore()
    {
        decimal v = Volume;
        if (v == 0) return "There is no open position.";
        CancelManaged();
        return PlaceBrackets(Math.Sign(v), EntryPrice, Template.StopTicks, Template.Targets);
    }

    private string PlaceBrackets(int side, decimal entry, int? stopTicks, IReadOnlyList<BracketTarget> targets)
    {
        decimal abs = Math.Abs(Volume);
        decimal last = Account.LastPrice;
        var exit = side > 0 ? OrderDirections.Sell : OrderDirections.Buy;
        var skipped = new List<string>();
        int placed = 0;

        if (stopTicks is > 0)
        {
            decimal sp = Inst.RoundPrice(entry - side * stopTicks.Value * Inst.TickSize);
            if (Validate(BracketRole.StopLoss, sp, side, last) == null)
            {
                _stops.Add(Account.Exit(OrderTypes.Stop, exit, abs, sp, "SL"));
                placed++;
            }
            else skipped.Add("stop");
        }

        var valid = targets.Where(t => t.Ticks > 0 && t.Percent > 0).OrderBy(t => t.Ticks).ToList();
        decimal remaining = abs;
        decimal totalPct = valid.Sum(t => t.Percent);
        for (int i = 0; i < valid.Count && remaining > 0; i++)
        {
            var t = valid[i];
            bool lastOne = i == valid.Count - 1;
            // the last target takes the remainder if the percentages add up to 100
            decimal qty = lastOne && totalPct >= 100 ? remaining : RoundQty(abs * t.Percent / 100m);
            qty = Math.Min(qty, remaining);
            if (qty <= 0) continue;
            decimal tp = Inst.RoundPrice(entry + side * t.Ticks * Inst.TickSize);
            if (Validate(BracketRole.Target, tp, side, last) != null)
            {
                skipped.Add($"target {i + 1}");
                continue;
            }

            _targets.Add(Account.Exit(OrderTypes.Limit, exit, qty, tp, "TP"));
            remaining -= qty;
            placed++;
        }

        string msg = placed > 0 ? $"Stop and targets set ({placed} orders)" : "No stop or target was set";
        if (skipped.Count > 0) msg += $" · price has already gone past: {string.Join(", ", skipped)}";
        return msg;
    }

    // ===================================================================== pending entries

    /// <summary>Attaches the template to an entry order: the stop and the targets are sent when it fills.</summary>
    public PendingBracket AttachTemplate(Order entry) => Run(() =>
    {
        var pb = _pending.FirstOrDefault(p => Same(p.Entry, entry));
        if (pb == null)
        {
            pb = new PendingBracket { Entry = entry };
            _pending.Add(pb);
        }

        pb.StopTicks = Template.StopTicks > 0 ? Template.StopTicks : null;
        pb.Targets.Clear();
        pb.Targets.AddRange(Template.Targets.Where(t => t.Ticks > 0 && t.Percent > 0).Select(t => new BracketTarget { Ticks = t.Ticks, Percent = t.Percent }));
        return pb;
    });

    /// <summary>
    /// Drag from a pending entry order: on the loss side it sets the stop, on the
    /// profit side it adds a target (if there already is one, it splits the biggest).
    /// </summary>
    public string AttachToEntry(Order entry, decimal price, BracketRole role = BracketRole.None) => Run(() =>
    {
        price = Inst.RoundPrice(price);
        var pb = _pending.FirstOrDefault(p => Same(p.Entry, entry));
        if (pb == null)
        {
            pb = new PendingBracket { Entry = entry };
            _pending.Add(pb);
        }

        int ticks = (int)Math.Round((price - pb.EntryPrice) / Inst.TickSize) * pb.Side;
        if (ticks == 0) return "Drop it above or below the order.";
        if (role == BracketRole.None) role = ticks > 0 ? BracketRole.Target : BracketRole.StopLoss;
        if (role == BracketRole.StopLoss)
        {
            if (ticks >= 0) return "The stop goes on the order's loss side.";
            pb.StopTicks = -ticks;
            return $"Stop attached at {-ticks} ticks (it is sent when the order fills)";
        }

        if (ticks <= 0) return "The target goes on the order's profit side.";
        if (pb.Targets.Count == 0) pb.Targets.Add(new BracketTarget { Ticks = ticks, Percent = 100 });
        else
        {
            var big = pb.Targets.OrderByDescending(t => t.Percent).First();
            decimal half = Math.Round(big.Percent / 2, 1);
            big.Percent -= half;
            pb.Targets.Add(new BracketTarget { Ticks = ticks, Percent = half });
        }

        return $"Target attached at {ticks} ticks (it is sent when the order fills)";
    });

    /// <summary>Moves the stop (index -1) or a target attached to a pending entry.</summary>
    public string MovePending(PendingBracket pb, int index, decimal price) => Run(() =>
    {
        int ticks = (int)Math.Round((Inst.RoundPrice(price) - pb.EntryPrice) / Inst.TickSize) * pb.Side;
        if (index < 0)
        {
            if (ticks >= 0) return "The stop goes on the order's loss side.";
            pb.StopTicks = -ticks;
            return $"Stop attached at {-ticks} ticks";
        }

        if (index >= pb.Targets.Count) return "";
        if (ticks <= 0) return "The target goes on the order's profit side.";
        pb.Targets[index].Ticks = ticks;
        return $"Target attached at {ticks} ticks";
    });

    public void RemovePending(PendingBracket pb, int index) => Run(() =>
    {
        if (index < 0) pb.StopTicks = null;
        else if (index < pb.Targets.Count)
        {
            var removed = pb.Targets[index];
            pb.Targets.RemoveAt(index);
            if (pb.Targets.Count > 0) pb.Targets.OrderByDescending(t => t.Percent).First().Percent += removed.Percent;
        }

        return 0;
    });

    // ===================================================================== synchronization

    private void OnAccountChanged()
    {
        lock (_gate)
        {
            if (_busy) return;
            SyncLoop();
        }

        Changed?.Invoke();
    }

    private void SyncLoop()
    {
        // the orders sent while synchronizing can fill right away: it repeats until it settles
        for (int i = 0; i < 3; i++)
        {
            _busy = true;
            bool changed;
            try
            {
                changed = Sync();
            }
            catch (Exception ex)
            {
                Message?.Invoke("Error with the stop/targets: " + ex.Message);
                changed = false;
            }
            finally
            {
                _busy = false;
            }

            if (!changed || Volume == _lastVolume) break;
        }
    }

    /// <summary>Reacts to position changes. Returns true if the position changed in the meantime.</summary>
    private bool Sync()
    {
        decimal v = Volume;
        decimal before = _lastVolume;
        PendingBracket? filledEntry = null;

        foreach (var pb in _pending.ToList())
        {
            if (Live(pb.Entry) != null) continue;
            _pending.Remove(pb);
            bool filled = pb.Entry.Status is OrderStatus.Filled or OrderStatus.PartlyFilled ||
                          (Account is not SimBroker && v != before && Math.Sign(v - before) == pb.Side);
            if (filled) filledEntry = pb;
        }

        if (before != 0 && v == 0)
        {
            // OCO: the position closed (stop, target or by hand): whatever was left is cancelled
            if (CancelManaged() > 0) Message?.Invoke("Position closed: the remaining stop and targets were cancelled.");
            _targetHits = 0;
        }
        else if (v != 0 && (before == 0 || Math.Sign(v) != Math.Sign(before)))
        {
            if (before != 0) CancelManaged();
            _entryPrice = Account.Position.AveragePrice;
            _targetHits = 0;
            // entry filled (or filling in parts) with an attached stop/targets
            var source = filledEntry ?? _pending.FirstOrDefault(p => p.Side == Math.Sign(v) && Live(p.Entry) is { Status: OrderStatus.PartlyFilled });
            if (source != null && Math.Sign(v) == source.Side && (source.StopTicks != null || source.Targets.Count > 0))
                Message?.Invoke(PlaceBrackets(source.Side, _entryPrice, source.StopTicks, source.Targets));
            else if (Template.Enabled)
                Message?.Invoke(PlaceBrackets(Math.Sign(v), _entryPrice, Template.StopTicks, Template.Targets));
        }
        else if (v != 0 && Math.Abs(v) < Math.Abs(before))
        {
            // partial exit (target, partial at market or partial stop)
            int hits = PruneFilledTargets();
            ResizeToPosition();
            if (hits > 0)
            {
                _targetHits += hits;
                if (_targetHits >= 1 && Template.BreakevenAfterFirstTarget && LiveOf(_stops).Count > 0 && _targetHits == hits)
                {
                    var msg = MoveStopToBreakevenCore();
                    Message?.Invoke($"Target filled · {msg}");
                }
                else Message?.Invoke("Target filled: the stop was adjusted to what is still open.");
            }
        }
        else if (v != 0 && Math.Abs(v) > Math.Abs(before))
        {
            // the position grew: the stop and the targets cover everything that is open
            _entryPrice = Account.Position.AveragePrice;
            bool none = LiveOf(_stops).Count == 0 && LiveOf(_targets).Count == 0;
            if (none && filledEntry != null && Math.Sign(v) == filledEntry.Side && (filledEntry.StopTicks != null || filledEntry.Targets.Count > 0))
            {
                Message?.Invoke(PlaceBrackets(filledEntry.Side, _entryPrice, filledEntry.StopTicks, filledEntry.Targets));
            }
            else
            {
                ResizeToPosition();
                GrowTargets(Math.Abs(v));
            }
        }

        _lastVolume = v;
        Prune();
        return Volume != v;
    }

    /// <summary>Targets that disappeared without us cancelling them: they filled.</summary>
    private int PruneFilledTargets()
    {
        int n = 0;
        foreach (var t in _targets.ToList())
        {
            if (Live(t) != null || _cancelled.Contains(t)) continue;
            _targets.Remove(t);
            n++;
        }

        return n;
    }

    /// <summary>Adjusts the stop to the position and shrinks the targets that are too big (from the farthest to the nearest).</summary>
    private void ResizeToPosition()
    {
        decimal abs = Math.Abs(Volume);
        if (abs == 0) return;
        foreach (var (tracked, live) in LiveOf(_stops))
            if (live.Unfilled != abs) Swap(_stops, tracked, Account.Replace(live, PriceOf(live), abs));
        ReduceTargets(abs, null);
    }

    /// <summary>Shrinks targets (except <paramref name="keep"/>) until they add up to at most <paramref name="max"/>.</summary>
    private void ReduceTargets(decimal max, Order? keep)
    {
        var live = LiveOf(_targets).OrderByDescending(t => Math.Abs(PriceOf(t.Live) - _entryPrice)).ToList();
        decimal total = live.Sum(t => t.Live.Unfilled);
        foreach (var (tracked, l) in live)
        {
            if (total <= max) break;
            if (keep != null && Same(l, keep)) continue;
            decimal excess = total - max;
            decimal newQty = l.Unfilled - excess;
            if (newQty <= 0)
            {
                _cancelled.Add(tracked);
                Account.Cancel(l);
                _targets.Remove(tracked);
                total -= l.Unfilled;
            }
            else
            {
                Swap(_targets, tracked, Account.Replace(l, PriceOf(l), newQty));
                total -= excess;
            }
        }
    }

    /// <summary>If the targets cover less than the position, the farthest one takes the difference.</summary>
    private void GrowTargets(decimal abs)
    {
        var live = LiveOf(_targets);
        if (live.Count == 0) return;
        decimal total = live.Sum(t => t.Live.Unfilled);
        if (total >= abs) return;
        var far = live.OrderByDescending(t => Math.Abs(PriceOf(t.Live) - _entryPrice)).First();
        Swap(_targets, far.Tracked, Account.Replace(far.Live, PriceOf(far.Live), far.Live.Unfilled + abs - total));
    }

    private int CancelManaged()
    {
        int n = 0;
        foreach (var list in new[] { _stops, _targets })
        {
            foreach (var t in list)
            {
                if (Live(t) is not { } l) continue;
                _cancelled.Add(t);
                Account.Cancel(l);
                n++;
            }

            list.Clear();
        }

        return n;
    }

    /// <summary>Removes from the lists the orders that are no longer active.</summary>
    private void Prune()
    {
        _stops.RemoveAll(t => Live(t) == null);
        _targets.RemoveAll(t => Live(t) == null);
        if (_cancelled.Count > 200) _cancelled.Clear();
    }

    private static void Swap(List<Order> list, Order tracked, Order replacement)
    {
        int i = list.IndexOf(tracked);
        if (i >= 0) list[i] = replacement;
        else list.Add(replacement);
    }

    private static string Fmt(decimal q) => q.ToString("0.########", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatMoney(decimal v) => v.ToString("+0.00;-0.00;0.00", System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose() => Account.Changed -= OnAccountChanged;
}
