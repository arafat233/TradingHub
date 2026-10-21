using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.Strategies;

/// <summary>
/// Warm-up of a strategy: how many candles it takes for the indicators it creates in OnInit to be ready (IsReady), and
/// which ones they are (with their period). It does not count what it reads from Bars by hand or indicators created outside OnInit.
/// </summary>
public sealed record StrategyWarmup(int Bars, IReadOnlyList<string> Indicators, bool Complete, string? Error)
{
    /// <summary>Cap on the candles that are tried (more than this is reported as incomplete).</summary>
    public const int MaxBars = 5000;

    /// <summary>
    /// Starts the strategy without trading (OnInit only) and feeds its indicators made-up candles until they are all
    /// ready. With <paramref name="parameters"/> it measures with those values; the missing ones stay at their defaults.
    /// </summary>
    public static StrategyWarmup Measure(Type type, IReadOnlyDictionary<string, object?>? parameters = null, string period = "M15")
    {
        var names = new List<string>();
        int bars = 0;
        try
        {
            var s = (Strategy)Activator.CreateInstance(type)!;
            if (parameters != null) s.SetParameters(parameters);
            var host = new ProbeHost(ChartPeriod.Parse(period));
            s.InternalInit(host);
            var indicators = s.Indicators.ToList();
            names.AddRange(indicators.Select(i => i.Name));
            while (bars < MaxBars && !indicators.All(i => i.IsReady))
            {
                host.Next();
                bars++;
                foreach (var i in indicators) i.Update(host.Bars);
            }

            return new StrategyWarmup(bars, names, indicators.All(i => i.IsReady), null);
        }
        catch (Exception ex)
        {
            return new StrategyWarmup(bars, names, false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Minimal environment for measuring: candles that rise and fall, with no orders.</summary>
    private sealed class ProbeHost(ChartPeriod period) : IStrategyHost
    {
        private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly BarSeries _bars = new();
        private DateTime _time = T0;

        public Instrument Instrument { get; } = new() { Symbol = "TEST", FeedId = "warmup", TickSize = 0.25m, VolumeStep = 1m, PointValue = 1m };
        public ChartPeriod Period => period;
        public BarSeries Bars => _bars;
        public Bar? FormingBar => null;
        public DateTime Time => _time;
        public decimal LastPrice => _bars.Count > 0 ? _bars[0].Close : 100;
        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;
        public SessionSettings Session { get; } = new();
        public decimal Position => 0;
        public decimal AveragePrice => 0;
        public decimal OpenPnL => 0;
        public decimal RealizedPnL => 0;
        public IReadOnlyList<StrategyOrder> ActiveOrders => Array.Empty<StrategyOrder>();
        public bool IsBacktest => true;

        /// <summary>Adds a candle (oscillating prices, with volume on both sides).</summary>
        public void Next()
        {
            var step = period.IsTimeBased ? period.ApproxDuration : TimeSpan.FromMinutes(1);
            int n = _bars.Count;
            decimal open = 100 + n % 7 - n % 3;
            _bars.Add(new Bar
            {
                Index = n, Time = _time, CloseTime = _time + step, Open = open, High = open + 2, Low = open - 2, Close = open + (n % 2 == 0 ? 1 : -1),
                Volume = 10, BuyVolume = n % 2 == 0 ? 6 : 4, SellVolume = n % 2 == 0 ? 4 : 6, Trades = 10, PocPrice = open,
            });
            _time += step;
        }

        public StrategyOrder Submit(OrderSide side, StrategyOrderType type, decimal quantity, decimal price, string name) =>
            new() { Side = side, Type = type, Quantity = quantity, Price = price, Name = name, State = StrategyOrderState.Canceled, Time = _time };

        public void Cancel(StrategyOrder order)
        {
        }

        public void Modify(StrategyOrder order, decimal price)
        {
        }

        public void Log(string message)
        {
        }
    }
}
