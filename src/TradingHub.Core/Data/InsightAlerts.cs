using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Which Insights events raise an alert, and how big a wall or a liquidation cluster has to be (in money) to be worth one.</summary>
public sealed record InsightAlertSettings(
    bool Toxicity = true, bool BigWall = true, decimal WallUsd = 1_000_000, bool OptionsTouch = true, bool Liquidations = true, decimal LiquidationUsd = 1_000_000, bool Setup = false);

/// <summary>
/// Turns what the Insights panel already knows into alerts for the existing alert path (the status bar, the log, Telegram, speech and sound). They
/// are about the market's state, not predictions: the tape has become toxic, a big resting order was pulled or eaten, price is at an options wall, a
/// big liquidation cluster formed. Each one fires when its condition starts, not while it lasts, and waits out a cooldown before it can fire again, so
/// a busy market does not turn into a stream of messages. One engine per instrument (it remembers what it has already said).
/// </summary>
public sealed class InsightAlertEngine
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30), LiquidationCooldown = TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, DateTime> _said = new();
    private readonly Dictionary<string, decimal> _liquidationSaid = new();
    private DangerLevel _danger = DangerLevel.Unknown;
    private Verdict _verdict = Verdict.Wait;
    private DateTime _eventsSeen;

    private bool Allowed(string key, DateTime now, TimeSpan cooldown)
    {
        if (_said.TryGetValue(key, out var at) && now - at < cooldown) return false;
        _said[key] = now;
        return true;
    }

    /// <summary>The alerts that should go out now (usually none).</summary>
    public List<string> Evaluate(InsightsSnapshot s, Instrument inst, decimal last, IReadOnlyList<LevelMark> marks, DateTime now, InsightAlertSettings cfg)
    {
        var alerts = new List<string>();
        if (s.StaleSeconds != null) return alerts; // the numbers are old: nothing is said until the feed is live again

        // toxicity turning high (not while it stays high)
        if (cfg.Toxicity && s.Danger == DangerLevel.High && _danger != DangerLevel.High && s.Vpin is { } v && Allowed("toxicity", now, Cooldown))
            alerts.Add($"Toxicity is high ({v:0.00}): one side is taking liquidity unusually one-sidedly, prices can jump.");
        _danger = s.Danger;

        // a big resting order was pulled, flashed or eaten (only events newer than the last look)
        if (s.Events != null)
        {
            foreach (var e in s.Events.Where(e => e.Time > _eventsSeen).OrderBy(e => e.Time))
            {
                if (!cfg.BigWall || e.Kind == BookEventKind.Added) continue;
                decimal money = inst.FromTicks(e.Ticks) * e.Size * inst.PointValue;
                if (money < cfg.WallUsd) continue;
                if (!Allowed($"wall:{e.Ask}:{e.Ticks}:{e.Kind}", now, TimeSpan.FromMinutes(5))) continue;
                alerts.Add($"{e.Text(inst)} ({LiquidationClusters.Money(money)}).");
            }

            if (s.Events.Count > 0) _eventsSeen = s.Events.Max(e => e.Time);
        }

        // price at an options level
        if (cfg.OptionsTouch && last > 0)
            foreach (var m in marks.Where(m => m.Source == "options"))
                if (Math.Abs(last - m.Price) / m.Price <= 0.0005m && Allowed("options:" + m.ChartText, now, Cooldown))
                    alerts.Add($"Price is at the {m.ChartText} ({m.Price:0.####}): options positioning is stacked here.");

        // the user's own levels (always on: they were put there to be told about)
        if (last > 0)
            foreach (var m in marks.Where(m => m.Source == MyLevels.Source))
                if (Math.Abs(last - m.Price) / m.Price <= 0.0005m && Allowed("mine:" + m.Price, now, TimeSpan.FromMinutes(10)))
                    alerts.Add($"Price is at your level {m.Price:0.####}.");

        // a big liquidation cluster formed (or grew a lot, or moved)
        if (cfg.Liquidations)
            foreach (var m in marks.Where(m => m.Source == "liquidations" && m.Weight >= cfg.LiquidationUsd))
            {
                string side = m.Tone < 0 ? "longs" : "shorts";
                bool fresh = !_liquidationSaid.TryGetValue(side, out var before) || m.Weight >= before * 1.5m;
                if (fresh && Allowed("liq:" + side, now, LiquidationCooldown))
                {
                    _liquidationSaid[side] = m.Weight;
                    alerts.Add($"{char.ToUpperInvariant(side[0])}{side[1..]} liquidated: {LiquidationClusters.Money(m.Weight)} around {m.Price:0.####} in the last hours.");
                }
            }

        // a setup called (off by default: it is context, not a signal)
        if (cfg.Setup && s.Decision.Verdict != Verdict.Wait && _verdict == Verdict.Wait && Allowed("setup", now, TimeSpan.FromMinutes(5)))
            alerts.Add($"{s.Decision.Headline}: leaning {(s.Decision.Lean > 0 ? "buy" : "sell")} and all checks pass (context, not a signal).");
        _verdict = s.Decision.Verdict;
        return alerts;
    }
}
