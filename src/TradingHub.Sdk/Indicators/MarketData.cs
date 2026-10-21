using TradingHub.Trading;

namespace TradingHub.Indicators
{
    /// <summary>A market event: a trade, or a change of a book level.</summary>
    public class MarketDataArg
    {
        public DateTime Time { get; set; }
        public decimal Price { get; set; }
        public decimal Volume { get; set; }
        public TradeDirection Direction { get; set; }
        public MarketDataType DataType { get; set; }
        public string Security { get; set; }
        public long? AggressorExchangeOrderId { get; set; }
        public long? ExchangeOrderId { get; set; }
        public long Id { get; set; }
        public decimal OpenInterest { get; set; }

        /// <summary>Original price (before grouping into rows).</summary>
        public decimal OriginPrice { get; set; }

        public bool IsBid => DataType == MarketDataType.Bid;
        public bool IsAsk => DataType == MarketDataType.Ask;
        public bool IsTrade => DataType == MarketDataType.Trade;

        public MarketDataArg Clone() => (MarketDataArg)MemberwiseClone();

        public override string ToString() => $"{DataType} {Direction} {Volume}@{Price} {Time:HH:mm:ss.fff}";
    }

    /// <summary>
    /// Accumulated trade: every fill of the same aggressive order
    /// (same instant and same side), even if it swept several levels.
    /// </summary>
    public class CumulativeTrade
    {
        public DateTime Time { get; set; }
        public decimal FirstPrice { get; set; }
        public decimal Lastprice { get; set; }
        public decimal Volume { get; set; }
        public TradeDirection Direction { get; set; }
        public List<MarketDataArg> Ticks { get; set; } = new();
        public MarketDataArg PreviousBid { get; set; }
        public MarketDataArg PreviousAsk { get; set; }
        public MarketDataArg NewBid { get; set; }
        public MarketDataArg NewAsk { get; set; }
        public CumulativeTradeStates State { get; set; }

        public decimal LastPrice => Lastprice;
        public decimal MaxPrice => Ticks.Count > 0 ? Ticks.Max(t => t.Price) : Math.Max(FirstPrice, Lastprice);
        public decimal MinPrice => Ticks.Count > 0 ? Ticks.Min(t => t.Price) : Math.Min(FirstPrice, Lastprice);

        /// <summary>Whether it is the same accumulated trade (it is updated while the order keeps sweeping).</summary>
        public bool IsEqual(CumulativeTrade other) =>
            other != null && other.Time == Time && other.Direction == Direction && other.FirstPrice == FirstPrice;

        public new CumulativeTrade MemberwiseClone() => (CumulativeTrade)base.MemberwiseClone();

        public override string ToString() => $"{Direction} {Volume} {FirstPrice}->{Lastprice} {Time:HH:mm:ss.fff}";
    }

    /// <summary>Request for historical accumulated trades.</summary>
    public class CumulativeTradesRequest
    {
        public CumulativeTradesRequest(DateTime beginTime)
        {
            BeginTime = beginTime;
            EndTime = DateTime.MaxValue;
        }

        public CumulativeTradesRequest(DateTime beginTime, DateTime endTime, decimal minVolume, decimal maxVolume)
        {
            BeginTime = beginTime;
            EndTime = endTime;
            MinVolume = minVolume;
            MaxVolume = maxVolume;
        }

        private static int _nextId;

        public int RequestId { get; } = Interlocked.Increment(ref _nextId);
        public DateTime BeginTime { get; }
        public DateTime EndTime { get; }
        public decimal MinVolume { get; }
        public decimal MaxVolume { get; }
    }

    /// <summary>Order book information for the indicators.</summary>
    public class MarketDepthInfo
    {
        private readonly Func<IEnumerable<MarketDataArg>> _snapshot;
        private readonly Func<decimal> _cumAsks;
        private readonly Func<decimal> _cumBids;

        public MarketDepthInfo(Func<IEnumerable<MarketDataArg>> snapshot, Func<decimal> cumulativeAsks, Func<decimal> cumulativeBids)
        {
            _snapshot = snapshot;
            _cumAsks = cumulativeAsks;
            _cumBids = cumulativeBids;
        }

        public static MarketDepthInfo Empty { get; } = new(() => Array.Empty<MarketDataArg>(), () => 0, () => 0);

        public decimal CumulativeDomAsks => _cumAsks();
        public decimal CumulativeDomBids => _cumBids();

        public IEnumerable<MarketDataArg> GetMarketDepthSnapshot() => _snapshot();
    }

    /// <summary>Instrument information for the indicators.</summary>
    public class InstrumentInfo
    {
        public string Instrument { get; set; } = "";
        public string Exchange { get; set; } = "";
        public decimal TickSize { get; set; } = 1m;
        public decimal TickCost { get; set; } = 1m;
        public TimeSpan TimeZoneOffset { get; set; } = TimeSpan.Zero;
        public Valuation Valuation { get; set; } = new();
        public decimal LotSize { get; set; } = 1m;
        public string Description { get; set; } = "";
        public int Digits => TradingHub.Market.Instrument.DecimalPlaces(TickSize);

        public override string ToString() => Instrument;
    }
}
