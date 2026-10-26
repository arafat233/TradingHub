using TradingHub.Market;

namespace TradingHub.Data;

public enum Verdict
{
    /// <summary>Not enough in place: wait.</summary>
    Wait,

    /// <summary>Every check passes with buyers leading.</summary>
    BuySetup,

    /// <summary>Every check passes with sellers leading.</summary>
    SellSetup,
}

/// <summary>One line of "what a setup needs": the name, what was measured, what it needs and whether it is met.</summary>
public sealed record Check(string Name, string Value, string Need, bool Pass);

/// <summary>A short plain-language fact about the tape or the book. <see cref="Tone"/>: +1 favours buyers, -1 favours sellers, 0 neither.</summary>
public sealed record Chip(string Text, int Tone);

/// <summary>A big resting level in the book near the price.</summary>
public sealed record Wall(decimal Price, decimal Size, decimal Notional);

/// <summary>Everything the Insights panel shows about the market right now.</summary>
public sealed record Decision(
    Verdict Verdict, int Lean, int Strength, string Headline, string Detail, IReadOnlyList<Check> Checks, IReadOnlyList<Chip> Chips,
    decimal BuyAbove, decimal SellBelow, Wall? AskWall, Wall? BidWall, string? Learning)
{
    public int Missing => Checks.Count(c => !c.Pass);
}

/// <summary>What the engine needs: the live stores plus the costs and the user's own stop and target (in ticks, 0 = automatic).</summary>
public sealed record InsightInput(Instrument Instrument, FlowTracker Flow, TradeStore Trades, DepthSnapshot Book, DateTime Now,
    decimal CostBps = 6, decimal StopTicks = 0, decimal TargetTicks = 0);

/// <summary>How real the big resting orders have been lately: of those that left, the share that were traded through (not cancelled).</summary>
public sealed record WallStats(decimal? BidReal, int BidCount, decimal? AskReal, int AskCount)
{
    /// <summary>
    /// Of the walls that have gone in the window, the share that left because trades ate them. A wall that is cancelled when price gets near,
    /// or within seconds, was never meant to be hit: low numbers mean spoofing-like behaviour. Null with fewer than 3 walls on a side (too few to say).
    /// </summary>
    public static WallStats Compute(IEnumerable<BookEvent> events, DateTime now, TimeSpan window)
    {
        int bidEaten = 0, bidGone = 0, askEaten = 0, askGone = 0;
        foreach (var e in events)
        {
            if (e.Time < now - window || e.Time > now || e.Kind == BookEventKind.Added) continue;
            if (e.Ask) { if (e.Kind == BookEventKind.Eaten) askEaten++; else askGone++; }
            else if (e.Kind == BookEventKind.Eaten) bidEaten++; else bidGone++;
        }

        int bid = bidEaten + bidGone, ask = askEaten + askGone;
        return new WallStats(bid >= 3 ? (decimal)bidEaten / bid : null, bid, ask >= 3 ? (decimal)askEaten / ask : null, ask);
    }
}

/// <summary>The numbers each check has to reach. The defaults are what the panel uses; <see cref="DecisionEngine"/> takes others so they can be swept on real tape.</summary>
public sealed record DecisionThresholds(
    decimal LeanMin = 0.05m, decimal Strength = 30, decimal Activity = 0.5m, decimal Driven = 0.55m, decimal CostMultiple = 2m, decimal RewardRisk = 1.2m, decimal Trend = 0.25m)
{
    public static readonly DecisionThresholds Default = new();
}

/// <summary>What the six checks measured at one moment, before any threshold is applied.</summary>
public sealed record Measures(
    int Lean, decimal Imbalance5, int Strength, decimal Activity, int Counted, decimal Driven, decimal TargetPct, decimal StopPct, decimal CostPct, decimal RewardRisk,
    decimal TrendEr, int TrendSign, decimal Price, decimal Spread)
{
    /// <summary>Whether a setup is called: one side leads and all six checks reach their thresholds.</summary>
    public bool IsSetup(DecisionThresholds t) => LeanOf(t) != 0 && Checks(t).All(c => c.Pass);

    public int LeanOf(DecisionThresholds t) => Imbalance5 >= t.LeanMin ? 1 : Imbalance5 <= -t.LeanMin ? -1 : 0;

    public List<Check> Checks(DecisionThresholds t)
    {
        int lean = LeanOf(t);
        return new List<Check>
        {
            new("Strength", Strength.ToString(), $"≥ {t.Strength:0}", Strength >= t.Strength),
            new("Trading activity", $"{Activity:P0}", $"≥ {t.Activity:P0}", Activity >= t.Activity),
            new("Driven by trades", Counted == 0 ? "–" : $"{Driven:P0}", $"≥ {t.Driven:P0}", Counted > 0 && Driven >= t.Driven),
            new("Target vs costs", $"{TargetPct:0.00}% vs {CostPct:0.00}%", $"≥ {t.CostMultiple:0.#}× costs", TargetPct >= t.CostMultiple * CostPct),
            new("R:R after costs", $"{RewardRisk:0.0}", $"≥ {t.RewardRisk:0.0}", RewardRisk >= t.RewardRisk),
            new("15m trend", TrendSign == 0 ? $"range {TrendEr:0.00}" : $"{(TrendSign > 0 ? "up" : "down")} {TrendEr:0.00}",
                lean == 0 ? "with the lean" : $"{(lean > 0 ? "up" : "down")} ≥ {t.Trend:0.00}", lean != 0 && TrendSign == lean && TrendEr >= t.Trend),
        };
    }
}

/// <summary>
/// The decision panel's rules. Nothing here is a model: each check is a number measured from the tape, the book and the costs, with its threshold
/// shown next to it, and the verdict is "setup" only when all six pass and one side leads. It is context for a human, not a trading signal: it has
/// not been shown to make money, which is what the edge meter (<see cref="EdgeTracker"/>) is for. <see cref="Measure"/> and
/// <see cref="Evaluate"/> are split so the thresholds can be tested against real tape without measuring it again.
/// </summary>
public static class DecisionEngine
{
    public const int MinMinutes = 20;

    /// <summary>The checks' numbers at this moment (null while there are fewer than <see cref="MinMinutes"/> minutes of tape).</summary>
    public static Measures? Measure(InsightInput x, DecisionThresholds? t = null)
    {
        var bars = x.Flow.Bars;
        if (bars.Count < MinMinutes) return null;
        var book = x.Book;
        decimal tick = x.Instrument.TickSize;
        var current = bars[^1];
        decimal price = x.Instrument.FromTicks(current.CloseTicks);
        var closed = bars.Take(bars.Count - 1).ToList(); // the minute in progress is not a full sample

        // ---- the lean: who took liquidity over the last five minutes
        var last5 = bars.TakeLast(5).ToList();
        decimal buy5 = last5.Sum(b => b.Buy), sell5 = last5.Sum(b => b.Sell), vol5 = buy5 + sell5;
        decimal imb5 = vol5 > 0 ? (buy5 - sell5) / vol5 : 0;
        int strength = (int)Math.Min(100, Math.Round(200 * Math.Abs(imb5)));

        // ---- activity: the last minute of trades against the usual minute (a percentile of the last two hours)
        var (b1, s1, _) = FlowTracker.Tape(x.Trades, x.Now, TimeSpan.FromMinutes(1));
        decimal vol1 = b1 + s1;
        var usual = closed.TakeLast(120).Select(b => b.Volume).ToList();
        decimal activity = usual.Count == 0 ? 0 : (decimal)usual.Count(v => v <= vol1) / usual.Count;

        // ---- driven by trades: how often the last fifteen minutes' candles went the way the tape did
        int agree = 0, counted = 0;
        foreach (var b in closed.TakeLast(15))
        {
            if (b.CloseTicks == b.OpenTicks || b.Delta == 0) continue;
            counted++;
            if ((b.CloseTicks > b.OpenTicks) == (b.Delta > 0)) agree++;
        }

        decimal driven = counted == 0 ? 0 : (decimal)agree / counted;

        // ---- costs: a typical five-minute range sets the target (one such swing) and the stop (half of one): 2:1 before costs, so costs can still leave it above 1.2; the user's own ticks replace them
        decimal range5 = TypicalRange(closed, 5, 12) * tick;
        decimal rangePct = price > 0 ? range5 / price * 100 : 0;
        decimal targetPct = x.TargetTicks > 0 && price > 0 ? x.TargetTicks * tick / price * 100 : 1.0m * rangePct;
        decimal stopPct = x.StopTicks > 0 && price > 0 ? x.StopTicks * tick / price * 100 : 0.5m * rangePct;
        decimal spreadPct = book.HasBid && book.HasAsk && price > 0 ? (x.Instrument.FromTicks(book.BestAskTicks) - x.Instrument.FromTicks(book.BestBidTicks)) / price * 100 : 0;
        decimal costPct = x.CostBps / 100m + spreadPct;
        decimal rr = stopPct + costPct > 0 ? (targetPct - costPct) / (stopPct + costPct) : 0;

        // ---- the fifteen-minute trend: how directly price travelled over the last two hours of fifteen-minute closes
        var (er, trendSign) = Trend(bars, (t ?? DecisionThresholds.Default).Trend);
        return new Measures(imb5 >= 0.05m ? 1 : imb5 <= -0.05m ? -1 : 0, imb5, strength, activity, counted, driven, targetPct, stopPct, costPct, rr, er, trendSign, price, spreadPct);
    }

    public static Decision Evaluate(InsightInput x, DecisionThresholds? thresholds = null)
    {
        var t = thresholds ?? DecisionThresholds.Default;
        var m = Measure(x, t);
        var bars = x.Flow.Bars;
        if (m == null)
            return new Decision(Verdict.Wait, 0, 0, "WAIT", "Collecting the tape.", Array.Empty<Check>(), Array.Empty<Chip>(), 0, 0, null, null,
                $"Learning this market: {bars.Count} of {MinMinutes} minutes of trades so far.");

        var checks = m.Checks(t);
        int lean = m.LeanOf(t);
        int missing = checks.Count(c => !c.Pass);
        var verdict = lean != 0 && missing == 0 ? (lean > 0 ? Verdict.BuySetup : Verdict.SellSetup) : Verdict.Wait;
        string side = lean > 0 ? "buy" : "sell";
        string headline = verdict switch { Verdict.BuySetup => "BUY SETUP", Verdict.SellSetup => "SELL SETUP", _ => "WAIT" };
        string detail = verdict != Verdict.Wait
            ? $"Leaning {side} and all {checks.Count} checks pass. This is context, not a signal: check the risk before acting."
            : lean == 0 ? "No clear lean: buyers and sellers are balanced."
            : $"Leaning {side}, but the evidence is not strong enough ({missing} of {checks.Count} checks missing).";

        // ---- the levels that would confirm it, and the walls beside the price
        var last5 = bars.TakeLast(5).ToList();
        long hi = last5.Max(b => b.HighTicks), lo = last5.Min(b => b.LowTicks);
        var (b1, s1, _) = FlowTracker.Tape(x.Trades, x.Now, TimeSpan.FromMinutes(1));
        return new Decision(verdict, lean, m.Strength, headline, detail, checks, Chips(x, bars, b1, s1, lean, m.Imbalance5),
            x.Instrument.FromTicks(hi), x.Instrument.FromTicks(lo), BiggestWall(x.Book.Asks, x.Instrument), BiggestWall(x.Book.Bids, x.Instrument), null);
    }

    /// <summary>The average high-to-low range of the last <paramref name="groups"/> groups of <paramref name="minutes"/> closed minutes, in ticks.</summary>
    private static decimal TypicalRange(List<MinuteBar> closed, int minutes, int groups)
    {
        var ranges = new List<long>();
        for (int g = 0; g < groups; g++)
        {
            int end = closed.Count - g * minutes, start = end - minutes;
            if (start < 0) break;
            ranges.Add(closed.Skip(start).Take(minutes).Max(b => b.HighTicks) - closed.Skip(start).Take(minutes).Min(b => b.LowTicks));
        }

        return ranges.Count == 0 ? 0 : (decimal)ranges.Average();
    }

    /// <summary>Efficiency ratio of the fifteen-minute closes (1 = a straight line, 0 = round trip) and its direction (0 when it is a range).</summary>
    private static (decimal Er, int Sign) Trend(IReadOnlyList<MinuteBar> bars, decimal need)
    {
        var closes = new List<long>();
        for (int i = bars.Count - 1; i >= 0 && closes.Count < 9; i--)
            if (bars[i].Start.Minute % 15 == 14 || i == bars.Count - 1) closes.Insert(0, bars[i].CloseTicks);
        if (closes.Count < 4) return (0, 0);
        long net = closes[^1] - closes[0], path = 0;
        for (int i = 1; i < closes.Count; i++) path += Math.Abs(closes[i] - closes[i - 1]);
        decimal er = path == 0 ? 0 : (decimal)Math.Abs(net) / path;
        return (er, er >= need ? Math.Sign(net) : 0);
    }

    private static Wall? BiggestWall(BookLevel[] levels, Instrument inst)
    {
        Wall? best = null;
        foreach (var l in levels.Take(10))
        {
            decimal price = inst.FromTicks(l.PriceTicks), notional = price * l.Size * inst.PointValue;
            if (best == null || notional > best.Notional) best = new Wall(price, l.Size, notional);
        }

        return best;
    }

    private static List<Chip> Chips(InsightInput x, IReadOnlyList<MinuteBar> bars, decimal b1, decimal s1, int lean, decimal imb5)
    {
        var chips = new List<Chip>();
        decimal vol1 = b1 + s1;
        if (vol1 > 0)
        {
            decimal share = b1 / vol1;
            if (share >= 0.6m) chips.Add(new Chip($"Buyers took {share:P0} of 1-min volume", 1));
            else if (share <= 0.4m) chips.Add(new Chip($"Sellers took {1 - share:P0} of 1-min volume", -1));
        }

        var (buyStacks, sellStacks) = StackedImbalances(x.Trades, x.Instrument, x.Now, TimeSpan.FromMinutes(5));
        if (buyStacks > 0) chips.Add(new Chip($"{buyStacks} stacked buy imbalances", 1));
        if (sellStacks > 0) chips.Add(new Chip($"{sellStacks} stacked sell imbalances", -1));

        if (imb5 >= 0.2m) chips.Add(new Chip("Aggressive buyers in control", 1));
        else if (imb5 <= -0.2m) chips.Add(new Chip("Aggressive sellers in control", -1));

        // the book: do the resting orders lean the way the tape does
        decimal bid = x.Book.Bids.Take(10).Sum(l => l.Size), ask = x.Book.Asks.Take(10).Sum(l => l.Size);
        if (bid + ask > 0)
        {
            decimal tilt = (bid - ask) / (bid + ask);
            if (Math.Abs(tilt) >= 0.1m)
            {
                int bookSign = tilt > 0 ? 1 : -1;
                if (lean != 0) chips.Add(new Chip(bookSign == lean ? "Order book agrees" : "Order book leans the other way", bookSign));
                else chips.Add(new Chip(bookSign > 0 ? "Bids heavier than offers" : "Offers heavier than bids", bookSign));
            }
        }

        // absorption: aggressive volume piling in one direction over two minutes while price stays put
        if (lean != 0)
        {
            var (b2, s2, n2) = FlowTracker.Tape(x.Trades, x.Now, TimeSpan.FromMinutes(2));
            decimal typical2 = bars.Count > 3 ? Median(bars.Take(bars.Count - 1).TakeLast(120).Select(b => b.Volume)) * 2 : 0;
            decimal leaning = lean > 0 ? b2 : s2, total = b2 + s2;
            bool heavy = typical2 > 0 && total >= 1.5m * typical2 && total > 0 && leaning / total >= 0.6m;
            bool stuck = false;
            if (n2 > 1)
            {
                int i0 = x.Trades.LowerBound(x.Now - TimeSpan.FromMinutes(2)), i1 = x.Trades.LowerBound(x.Now.AddTicks(1)) - 1;
                long move = x.Trades.GetPriceTicks(i1) - x.Trades.GetPriceTicks(i0);
                stuck = lean > 0 ? move <= 1 : move >= -1;
            }

            chips.Add(heavy && stuck
                ? new Chip(lean > 0 ? "Buying is being absorbed: volume without progress" : "Selling is being absorbed: volume without progress", -lean)
                : new Chip("No absorption against it", lean));
        }

        return chips;
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var v = values.OrderBy(a => a).ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    /// <summary>
    /// Stacked imbalances over a window of the tape: prices are grouped into about twenty rows; a row has a buy imbalance when buyers at that
    /// price took at least three times what sellers took one row below (the usual diagonal comparison) and a sell imbalance the other way; three
    /// or more such rows in a row are a stack. Returns how many rows belong to buy stacks and to sell stacks.
    /// </summary>
    public static (int Buy, int Sell) StackedImbalances(TradeStore store, Instrument inst, DateTime now, TimeSpan window)
    {
        int i0 = store.LowerBound(now - window), i1 = store.LowerBound(now.AddTicks(1));
        if (i1 - i0 < 10) return (0, 0);
        long lo = long.MaxValue, hi = long.MinValue;
        for (int i = i0; i < i1; i++) { long p = store.GetPriceTicks(i); lo = Math.Min(lo, p); hi = Math.Max(hi, p); }
        long row = Math.Max(1, (hi - lo + 19) / 20);
        var buy = new Dictionary<long, decimal>();
        var sell = new Dictionary<long, decimal>();
        for (int i = i0; i < i1; i++)
        {
            long r = (store.GetPriceTicks(i) - lo) / row;
            var d = store.GetSide(i) == TradeSide.Buy ? buy : sell;
            d[r] = d.GetValueOrDefault(r) + store.GetVolume(i);
        }

        decimal avg = (buy.Values.Sum() + sell.Values.Sum()) / Math.Max(1, buy.Keys.Union(sell.Keys).Count());
        int Stacks(Dictionary<long, decimal> hit, Dictionary<long, decimal> other, int dir)
        {
            int rows = 0, run = 0;
            for (long r = 0; r <= (hi - lo) / row + 1; r++)
            {
                decimal mine = hit.GetValueOrDefault(r), theirs = other.GetValueOrDefault(r - dir);
                if (mine >= avg * 0.5m && mine >= 3 * Math.Max(theirs, avg * 0.01m)) run++;
                else
                {
                    if (run >= 3) rows += run;
                    run = 0;
                }
            }

            return rows + (run >= 3 ? run : 0);
        }

        return (Stacks(buy, sell, 1), Stacks(sell, buy, -1));
    }
}
