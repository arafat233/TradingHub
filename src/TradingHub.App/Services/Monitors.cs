using System.Text;
using Avalonia.Threading;
using TradingHub.Data;
using TradingHub.Trading;

namespace TradingHub.App.Services;

/// <summary>Messages for the user that are not chart alerts (tilt checks, feed delay): logged, shown in the status bar, and sent like alerts.</summary>
public static class Notifier
{
    /// <summary>The status bar subscribes to this (it runs on the UI thread).</summary>
    public static event Action<string>? Message;

    public static void Warn(AppSettings settings, string text)
    {
        AlertLog.Add(DateTime.Now, text);
        Dispatcher.UIThread.Post(() => Message?.Invoke(text));
        if (AlertControl.IsMuted) return;
        TelegramNotifier.Notify(settings, text);
        SpokenAlerts.Say(settings, text);
    }
}

/// <summary>Alerts can be muted for a while (from Telegram: "mute 2h"). The log and the status bar still get them; sound, voice and phone do not.</summary>
public static class AlertControl
{
    public static DateTime MutedUntil { get; set; }

    public static bool IsMuted => DateTime.Now < MutedUntil;
}

/// <summary>
/// Measures how late the live ticks arrive (the time the PC receives a tick minus the tick's own time) for every open instrument, and warns
/// when the median is high: late data makes alerts and decisions late. A PC clock that is wrong shows up as an offset of its own.
/// </summary>
public sealed class LatencyMonitor
{
    private sealed class State
    {
        public long LastTicks = long.MinValue;
        public readonly Queue<(DateTime At, double Delay)> Samples = new();
        public DateTime LastWarning = DateTime.MinValue;
        public double Median;
        public int Count;
    }

    private readonly Dictionary<string, State> _states = new();

    /// <summary>Median delay in seconds and the number of ticks it is based on, per instrument key.</summary>
    public (double Median, int Samples)? Of(string instrumentKey) => _states.TryGetValue(instrumentKey, out var s) && s.Count >= 5 ? (s.Median, s.Count) : null;

    public void Poll(MarketDataHub hub, AppSettings settings)
    {
        var now = DateTime.UtcNow;
        foreach (var d in hub.Items)
        {
            if (d.Instrument.FeedId == "sim" || d.IsLoadingHistory) continue;
            var store = d.Trades;
            int n = store.Count;
            if (n == 0) continue;
            if (!_states.TryGetValue(d.Instrument.Key, out var st))
            {
                _states[d.Instrument.Key] = new State { LastTicks = store.GetTimeTicks(n - 1) };
                continue;
            }

            long last = store.GetTimeTicks(n - 1);
            if (last > st.LastTicks)
            {
                int i = n - 1;
                int taken = 0;
                while (i >= 0 && store.GetTimeTicks(i) > st.LastTicks && taken < 100)
                {
                    double delay = (now - new DateTime(store.GetTimeTicks(i), DateTimeKind.Utc)).TotalSeconds;
                    // ticks replayed from the past (a reconnect, a gap fill) are not live: ignore anything more than ten minutes old
                    if (delay < 600 && delay > -600) st.Samples.Enqueue((now, delay));
                    i--;
                    taken++;
                }

                st.LastTicks = last;
            }

            while (st.Samples.Count > 0 && now - st.Samples.Peek().At > TimeSpan.FromSeconds(90)) st.Samples.Dequeue();
            st.Count = st.Samples.Count;
            if (st.Count < 5) continue;
            var sorted = st.Samples.Select(x => x.Delay).OrderBy(x => x).ToList();
            st.Median = sorted[sorted.Count / 2];
            if (st.Count >= 10 && Math.Abs(st.Median) > 5 && now - st.LastWarning > TimeSpan.FromMinutes(10))
            {
                st.LastWarning = now;
                Notifier.Warn(settings, st.Median > 0
                    ? $"Feed delay: {d.Instrument.Symbol} ticks are arriving about {st.Median:0} s late. Alerts and levels are late by the same amount."
                    : $"Clock check: {d.Instrument.Symbol} ticks carry times {-st.Median:0} s in the future of this PC's clock. Fix the PC's time (Windows → Settings → Time) or the delay figures will be wrong.");
            }
        }
    }
}

/// <summary>
/// Looks at how the user trades, not at what the market does: re-entering soon after a loss, size after losses, days of over-trading and
/// holding losers longer than winners. Plain statistics from the journal and a non-blocking warning after a loss; it never stops an order.
/// </summary>
public static class ProcessCoach
{
    private static readonly TimeSpan ReEntryWindow = TimeSpan.FromMinutes(5);
    private static readonly HashSet<DateTime> Warned = new();

    private static List<AccountTrade> TradesOf(AppServices svc)
    {
        var all = new List<AccountTrade>();
        foreach (var a in svc.AccountCenter.Accounts())
            all.AddRange(RoundTrips.Analyze(svc.Journal.FillsOf(a.Id)).Trades);
        return all.OrderBy(t => t.OpenTime).ToList();
    }

    public static string Section(AppServices svc)
    {
        var sb = new StringBuilder("PROCESS COACH (how you trade, from the journal; it only reports, it never blocks anything)\n");
        var trades = TradesOf(svc);
        if (trades.Count < 20) return sb.Append($"  not enough trades yet ({trades.Count}; twenty or more are needed)").ToString();
        // after a loss
        var afterLoss = new List<AccountTrade>();
        var afterWin = new List<AccountTrade>();
        var reEntries = new List<AccountTrade>();
        for (int i = 1; i < trades.Count; i++)
        {
            var prev = trades[i - 1];
            (prev.NetPnl < 0 ? afterLoss : afterWin).Add(trades[i]);
            if (prev.NetPnl < 0 && trades[i].OpenTime - prev.CloseTime <= ReEntryWindow && trades[i].OpenTime >= prev.CloseTime) reEntries.Add(trades[i]);
        }

        decimal Avg(IEnumerable<AccountTrade> t) => t.Any() ? t.Average(x => x.NetPnl) : 0;
        var others = trades.Except(reEntries).ToList();
        sb.AppendLine($"  trades {trades.Count} · net {trades.Sum(t => t.NetPnl):+0.00;-0.00;0.00} · winners {100.0 * trades.Count(t => t.NetPnl > 0) / trades.Count:0}%");
        sb.AppendLine($"  re-entries within {ReEntryWindow.TotalMinutes:0} min of a loss: {reEntries.Count} trades averaging {Avg(reEntries):+0.00;-0.00;0.00}; all other trades {Avg(others):+0.00;-0.00;0.00}"
                      + (reEntries.Count >= 10 && Avg(reEntries) < Avg(others) ? "   <-- trades taken right after a loss do worse" : ""));
        decimal q(IEnumerable<AccountTrade> t) => t.Any() ? t.Average(x => x.Quantity) : 0;
        sb.AppendLine($"  size: after a loss you trade {q(afterLoss):0.##} on average, after a win {q(afterWin):0.##}" + (q(afterLoss) > q(afterWin) * 1.25m ? "   <-- size grows after losses" : ""));
        var perDay = trades.GroupBy(t => svc.AccountCenter.ToLocal(t.OpenTime).Date).Select(g => (Day: g.Key, N: g.Count(), Net: g.Sum(t => t.NetPnl))).ToList();
        double median = perDay.Select(d => (double)d.N).OrderBy(v => v).ElementAt(perDay.Count / 2);
        var busy = perDay.Where(d => d.N > 2 * median && d.N >= 5).ToList();
        sb.AppendLine($"  trades per day: typical {median:0}, most {perDay.Max(d => d.N)}; days with more than twice the typical number ({busy.Count}) netted {busy.Sum(d => d.Net):+0.00;-0.00;0.00}, the other days {perDay.Except(busy).Sum(d => d.Net):+0.00;-0.00;0.00}");
        var winners = trades.Where(t => t.NetPnl > 0).ToList();
        var losers = trades.Where(t => t.NetPnl < 0).ToList();
        if (winners.Count >= 5 && losers.Count >= 5)
        {
            double w = winners.Average(t => t.Duration.TotalMinutes), l = losers.Average(t => t.Duration.TotalMinutes);
            sb.AppendLine($"  you hold winners {w:0.#} min and losers {l:0.#} min" + (l > w * 1.5 ? "   <-- losers are held much longer than winners" : ""));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Called when an account's fills change: warns once if a new position was opened soon after a losing trade closed.</summary>
    public static void OnFill(AppServices svc, string accountId)
    {
        try
        {
            var fills = svc.Journal.FillsOf(accountId);
            var result = RoundTrips.Analyze(fills);
            var last = result.Trades.OrderBy(t => t.CloseTime).LastOrDefault();
            if (last == null || last.NetPnl >= 0 || Warned.Contains(last.CloseTime)) return;
            var opening = fills.Where(f => f.Time > last.CloseTime).OrderBy(f => f.Time).FirstOrDefault();
            if (opening == null || opening.Time - last.CloseTime > ReEntryWindow) return;
            Warned.Add(last.CloseTime);
            Notifier.Warn(svc.Settings, $"Tilt check: you opened a new position {(opening.Time - last.CloseTime).TotalMinutes:0.#} min after a loss of {-last.NetPnl:0.00} on {last.Symbol}. See the Process coach in the Today window for how re-entries have gone for you.");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex); // a coaching note must never disturb trading
        }
    }
}
