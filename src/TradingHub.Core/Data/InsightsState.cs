using TradingHub.Market;

namespace TradingHub.Data;

public enum DangerLevel { Unknown, Low, Normal, Elevated, High }

/// <summary>Best bid, best ask and the gap between them (all zero without a book).</summary>
public sealed record QuoteHeader(decimal Bid, decimal Ask, decimal SpreadTicks, decimal SpreadBps)
{
    public bool HasBook => Bid > 0 && Ask > 0;
}

/// <summary>Everything the Insights panel draws, computed once per refresh.</summary>
public sealed record InsightsSnapshot(
    Decision Decision, QuoteHeader Quote, DangerLevel Danger, string DangerText, decimal? Vpin, WallStats Walls, bool WallsFromRealOrders, string EdgeText,
    decimal CostBps, string? Pulls = null, string? Futures = null, IReadOnlyList<BookEvent>? Events = null, IReadOnlyList<string>? Calls = null,
    int? StaleSeconds = null, WhaleFlowResult? Whales = null, string? WhaleVersusLean = null, SessionStats? Session = null, string? RegimeTip = null);

/// <summary>
/// The Insights panel's brain for one instrument: it owns the flow tracker, the edge meter and the fallback book-event detector, and turns the
/// instrument's live stores into one <see cref="InsightsSnapshot"/>. Everything runs on the UI thread (the one that pumps the data).
/// </summary>
public sealed class InsightsState
{
    /// <summary>Folder for the edge meters (one file per instrument); null keeps them in memory.</summary>
    public static string? EdgeFolder { get; set; }

    private readonly InstrumentData _data;
    private readonly BookEventDetector _inferred = new();
    private readonly IcebergDetector _icebergs = new();

    public FlowTracker Flow { get; } = new();
    public EdgeTracker Edge { get; }

    /// <summary>The calls made while replaying or on the simulator: kept in memory only, so they never mix into the real instrument's score.</summary>
    public EdgeTracker ReplayEdge { get; } = new(null);

    public InsightsState(InstrumentData data)
    {
        _data = data;
        string? path = null;
        if (EdgeFolder != null)
        {
            string safe = string.Concat(data.Instrument.Key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
            path = Path.Combine(EdgeFolder, safe + ".json");
        }

        Edge = new EdgeTracker(path);
    }

    public InsightsSnapshot Compute(decimal costBps, decimal stopTicks, decimal targetTicks)
    {
        var inst = _data.Instrument;
        var now = _data.Feed.Now;
        Flow.Update(_data.Trades);
        var book = _data.Depth;
        var decision = DecisionEngine.Evaluate(new InsightInput(inst, Flow, _data.Trades, book, now, costBps, stopTicks, targetTicks));
        // a replay or the simulator is not the market: its calls are scored too (so you can judge the panel on a stored day) but in their own memory-only score
        bool live = _data.Feed.IsLive;
        var edge = live ? Edge : ReplayEdge;
        // the call is from old data when nothing has traded for longer than this market's usual gap: it is said so and it is not scored
        int? stale = StaleGuard.StaleSeconds(_data.Trades, now, live);
        if (decision.Learning == null && stale == null) edge.Observe(decision.Verdict, inst, _data.Trades, now, costBps);

        bool real = _data.OrderFlow.Active;
        IEnumerable<BookEvent> events;
        if (real) events = _data.OrderFlow.Events;
        else
        {
            _inferred.Update(_data.DepthHistory, _data.Trades);
            events = _inferred.Events;
        }

        var walls = WallStats.Compute(events, now, TimeSpan.FromMinutes(10));

        decimal bid = _data.BestBid, ask = _data.BestAsk;
        decimal mid = (bid + ask) / 2;
        var quote = bid > 0 && ask > 0 && ask >= bid
            ? new QuoteHeader(bid, ask, (ask - bid) / inst.TickSize, mid > 0 ? (ask - bid) / mid * 10_000m : 0)
            : new QuoteHeader(0, 0, 0, 0);

        string? pulls = null;
        if (PullStats.Hotspot(events, now, TimeSpan.FromMinutes(10)) is { } h)
            pulls = $"{(h.Ask ? "offer" : "bid")} at {inst.FromTicks(h.Ticks):0.####} pulled {h.Count}× in 10 min";

        string? futures = null;
        if (DerivativesService.SymbolOf(inst) is { } sym)
        {
            DerivativesService.Request(sym);
            futures = DerivativesService.LatestFor(sym)?.Text();
        }

        // more evidence for the decision card: what the large traders did against the lean, a possible iceberg, and the gamma regime
        var whales = WhaleFlow.Compute(_data.Trades, inst, now, TimeSpan.FromMinutes(30));
        string? versus = whales == null ? null : WhaleFlow.VersusLean(whales, decision.Lean);
        var extra = new List<Chip>();
        if (whales is { Prints: > 0 } w && Math.Abs(w.Net) >= 2 * w.Threshold)
            extra.Add(new Chip($"Large traders net {(w.Net > 0 ? "buying" : "selling")} {LiquidationClusters.Money(Math.Abs(w.Net))} ({w.Prints} prints, 30 min)", w.Net > 0 ? 1 : -1));
        _icebergs.Update(_data.DepthHistory, _data.Trades, 0);
        if (IcebergEvents.Merge(_icebergs.Events, _data.OrderIcebergs.Events).LastOrDefault(e => e.Last >= now.AddSeconds(-30)) is { } ice)
            extra.Add(new Chip($"Possible iceberg {(ice.Ask ? "offer" : "bid")} at {inst.FromTicks(ice.Ticks):0.####}: {ice.Volume:0.##} traded into it{(ice.PredictedHidden > 0 ? $", ~{ice.PredictedHidden:0.##} more expected" : "")}",ice.Ask ? -1 : 1));
        if (PullStats.Hotspot(events, now, TimeSpan.FromMinutes(10)) is { } pull)
            extra.Add(new Chip($"{(pull.Ask ? "Offer" : "Bid")} at {inst.FromTicks(pull.Ticks):0.####} pulled {pull.Count}× in 10 min", 0));
        string? regimeTip = null;
        if (OptionsLevelService.CurrencyOf(inst) is { } currency && GammaRegime.Of(_data.LastPrice, OptionsLevelService.LatestFor(currency)?.GammaFlip) is { } regime)
        {
            extra.Add(new Chip(regime.Chip, regime.Sign));
            regimeTip = regime.Tip;
        }

        if (extra.Count > 0) decision = decision with { Chips = decision.Chips.Concat(extra).ToList() };

        var session = Flow.Bars.Count == 0 ? null : new SessionStats(Flow.Bars[^1].Delta, Flow.SessionDeltaMoney, Flow.SessionPocTicks is { } poc ? inst.FromTicks(poc) : null);
        var (danger, text) = Danger(Flow.VpinPercentile, Flow.Vpin);
        return new InsightsSnapshot(decision, quote, danger, text, Flow.Vpin, walls, real, edge.Text() + (live ? "" : " (this replay only)"), costBps, pulls, futures,
            events.Where(e => e.Time >= now - TimeSpan.FromMinutes(5)).ToList(), Calls(edge, inst), stale, whales, versus, session, regimeTip);
    }

    /// <summary>The last five calls, newest first: when, which way, at what price, and how it turned out 15 minutes later (or that it is still waiting).</summary>
    private static List<string> Calls(EdgeTracker edge, Instrument inst) =>
        edge.Setups.TakeLast(5).Reverse().Select(c =>
            $"{c.Time:HH:mm:ss} UTC  {(c.Side > 0 ? "BUY" : "SELL")} setup at {inst.FromTicks(c.EntryTicks):0.####}  " +
            (c.ResultTicks is { } r ? $"→ {r:+0.0;-0.0;0.0} ticks after costs, 15 min later" : "→ waiting for its 15 minutes")).ToList();

    private static (DangerLevel, string) Danger(decimal? pct, decimal? vpin)
    {
        if (pct is not { } p || vpin is not { } v) return (DangerLevel.Unknown, "Danger: learning this market's usual toxicity.");
        var level = p >= 0.9m ? DangerLevel.High : p >= 0.75m ? DangerLevel.Elevated : p >= 0.25m ? DangerLevel.Normal : DangerLevel.Low;
        string name = level switch { DangerLevel.High => "high", DangerLevel.Elevated => "elevated", DangerLevel.Normal => "normal", _ => "low" };
        string why = level >= DangerLevel.Elevated ? " (one side is taking liquidity unusually one-sidedly: prices can jump)" : "";
        return (level, $"Danger {name}: toxicity {v:0.00}, higher than {p:P0} of its last day{why}");
    }
}
