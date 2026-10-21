using System.Collections;

namespace TradingHub.Trading.Statistics
{
    /// <summary>Complete trade (entry + exit) for statistics and chart markers.</summary>
    public class HistoryMyTrade
    {
        public Security Security { get; set; } = new();
        public Portfolio Portfolio { get; set; } = new();
        public string AccountID => Portfolio?.AccountID ?? "";
        public DateTime OpenTime { get; set; }
        public DateTime CloseTime { get; set; }
        public decimal OpenPrice { get; set; }
        public decimal ClosePrice { get; set; }

        /// <summary>Signed volume: + long, - short.</summary>
        public decimal OpenVolume { get; set; }

        public decimal PnL { get; set; }
        public decimal TicksPnL { get; set; }
        public decimal Commission { get; set; }
        public decimal MaxRunUp { get; set; }
        public decimal MaxDrawDown { get; set; }

        public override string ToString() => $"{(OpenVolume > 0 ? "Long" : "Short")} {Math.Abs(OpenVolume)} {OpenPrice}->{ClosePrice} PnL {PnL}";
    }

    /// <summary>List that reports when an element is added.</summary>
    public class NotifyingList<T> : IReadOnlyList<T>
    {
        private readonly List<T> _items = new();

        public event Action<T>? Added;
        public event Action? Cleared;

        public int Count => _items.Count;
        public T this[int index] => _items[index];

        public void Add(T item)
        {
            _items.Add(item);
            Added?.Invoke(item);
        }

        public void Clear()
        {
            _items.Clear();
            Cleared?.Invoke();
        }

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public interface ITradingStatistics
    {
        NotifyingList<HistoryMyTrade> HistoryMyTrades { get; }
    }

    public class TradingStatistics : ITradingStatistics
    {
        public NotifyingList<HistoryMyTrade> HistoryMyTrades { get; } = new();
    }

    /// <summary>Trading statistics provider (the simulated broker feeds it).</summary>
    public class TradingStatisticsProvider
    {
        public static TradingStatisticsProvider Empty { get; } = new();

        public ITradingStatistics RawStatistics { get; private set; } = new TradingStatistics();
        public ITradingStatistics FilteredStatistics => RawStatistics;

        public event Action? StatisticsRebuilt;
        public event Action<ITradingStatistics>? RawStatisticsSourceChanged;
        public event Action<ITradingStatistics>? FilteredStatisticsSourceChanged;

        public void SetSource(ITradingStatistics stats)
        {
            RawStatistics = stats;
            RawStatisticsSourceChanged?.Invoke(stats);
            FilteredStatisticsSourceChanged?.Invoke(stats);
            StatisticsRebuilt?.Invoke();
        }

        public void RaiseRebuilt() => StatisticsRebuilt?.Invoke();
    }
}

namespace TradingHub.Trading.Dom
{
    /// <summary>Book level type.</summary>
    public enum DomLevelType
    {
        Bid,
        Ask,
    }
}
