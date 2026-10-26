using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Services;

/// <summary>One line of the big-orders feed.</summary>
public sealed record BigOrderLine(DateTime Time, string FeedId, string Symbol, BigOrder Order);

/// <summary>
/// Watches every instrument that is open (on any chart) for big market orders and keeps the newest ones, across instruments.
/// Only open instruments are watched: the connections stream what a chart asks for (London Strategic Edge's free plan
/// allows a single stream at a time).
/// </summary>
public sealed class BigOrderFeed
{
    private sealed class State
    {
        public readonly BigOrderDetector Detector = new();
        public long LastTicks = long.MinValue;
    }

    private readonly Dictionary<string, State> _states = new();
    private readonly List<BigOrderLine> _lines = new();

    public event Action<BigOrderLine>? Added;

    public IReadOnlyList<BigOrderLine> Lines => _lines;

    /// <summary>Looks at the trades that arrived since the last call (UI thread, every tick of the main timer).</summary>
    public void Poll(MarketDataHub hub)
    {
        foreach (var d in hub.Items)
        {
            if (d.Instrument.FeedId == "sim") continue; // a made-up market
            var store = d.Trades;
            int n = store.Count;
            if (n == 0) continue;
            if (!_states.TryGetValue(d.Instrument.Key, out var st))
            {
                // first sight: what is already loaded is history, not news
                _states[d.Instrument.Key] = new State { LastTicks = store.GetTimeTicks(n - 1) };
                continue;
            }

            // by time, not by index: older history can be inserted in front, which shifts the indexes
            long last = store.GetTimeTicks(n - 1);
            if (last <= st.LastTicks) continue;
            int i = n - 1;
            while (i > 0 && store.GetTimeTicks(i - 1) > st.LastTicks) i--;
            if (d.IsLoadingHistory)
            {
                st.LastTicks = last;
                continue;
            }

            for (; i < n; i++)
            {
                if (store.GetTimeTicks(i) <= st.LastTicks) continue;
                if (st.Detector.OnTrade(store.GetTimeTicks(i), store.GetPrice(i), store.GetVolume(i), store.GetSide(i)) is { } o)
                {
                    var line = new BigOrderLine(o.Time, d.Instrument.FeedId, d.Instrument.Symbol, o);
                    _lines.Add(line);
                    if (_lines.Count > 300) _lines.RemoveAt(0);
                    Added?.Invoke(line);
                }
            }

            st.LastTicks = last;
        }
    }
}
