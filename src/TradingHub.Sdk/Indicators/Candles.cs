using TradingHub.Data;

namespace TradingHub.Indicators
{
    /// <summary>Volume at one price level of a candle (one footprint row).</summary>
    public class PriceVolumeInfo
    {
        public decimal Price { get; set; }
        public decimal Volume { get; set; }
        public decimal Bid { get; set; }
        public decimal Ask { get; set; }
        public decimal Between { get; set; }
        public decimal Ticks { get; set; }

        /// <summary>"Time" at the level (in Trading Hub, the number of trades as an approximation).</summary>
        public int Time { get; set; }

        public decimal Delta => Ask - Bid;

        public PriceVolumeInfo()
        {
        }

        public PriceVolumeInfo(decimal price)
        {
            Price = price;
        }

        public PriceVolumeInfo Clone() => (PriceVolumeInfo)MemberwiseClone();

        public override string ToString() => $"{Price}: {Bid} x {Ask}";
    }

    /// <summary>Value area of a candle or profile.</summary>
    public sealed class ValueArea
    {
        public decimal ValueAreaHigh { get; set; }
        public decimal ValueAreaLow { get; set; }
        public decimal PointOfControl { get; set; }
    }

    /// <summary>
    /// Candle as the indicators see it (API compatible with ATAS's).
    /// It wraps a core candle and converts rows/ticks to prices.
    /// </summary>
    public class IndicatorCandle
    {
        private readonly TradingHub.Data.Candle _c;
        private readonly CandleSeries _series;

        public IndicatorCandle(TradingHub.Data.Candle candle, CandleSeries series)
        {
            _c = candle;
            _series = series;
        }

        internal TradingHub.Data.Candle Core => _c;

        public DateTime Time => _c.Time;
        public DateTime LastTime => _c.LastTime;
        public decimal Open => _series.TicksToPrice(_c.OpenTicks);
        public decimal High => _series.TicksToPrice(_c.HighTicks);
        public decimal Low => _series.TicksToPrice(_c.LowTicks);
        public decimal Close => _series.TicksToPrice(_c.CloseTicks);
        public decimal Volume => _c.Volume;
        public decimal Bid => _c.Bid;
        public decimal Ask => _c.Ask;
        public decimal Betweenvolume => _c.Between;
        public decimal Between => _c.Between;
        public decimal Delta => _c.Ask - _c.Bid;
        public decimal MaxDelta => _c.MaxDelta;
        public decimal MinDelta => _c.MinDelta;
        public decimal Ticks => _c.Trades;
        public decimal OI => _c.OI;
        public decimal MaxOI => _c.MaxOI;
        public decimal MinOI => _c.MinOI;
        public bool IsCandleFinished => _c.IsClosed;
        public decimal MaxTradeVolume => _c.MaxTradeVolume;

        public decimal Body => Math.Abs(Close - Open);
        public bool IsBullish => Close > Open;
        public bool IsBearish => Close < Open;

        private PriceVolumeInfo Make(long row, in LevelData d) => new()
        {
            Price = _series.RowPrice(row),
            Volume = d.Volume,
            Bid = d.Bid,
            Ask = d.Ask,
            Between = d.Between,
            Ticks = d.Trades,
            Time = d.Trades,
        };

        /// <summary>Exact price level (or null if there were no trades there).</summary>
        public PriceVolumeInfo GetPriceVolumeInfo(decimal price)
        {
            long row = _series.PriceToRow(price);
            return _c.Levels.TryGet(row, out var d) ? Make(row, d) : null;
        }

        /// <summary>Same as <see cref="GetPriceVolumeInfo(decimal)"/> but reusing an object (no memory allocated).</summary>
        public PriceVolumeInfo GetPriceVolumeInfo(decimal price, PriceVolumeInfo reuse)
        {
            long row = _series.PriceToRow(price);
            if (!_c.Levels.TryGet(row, out var d)) return null;
            reuse ??= new PriceVolumeInfo();
            reuse.Price = _series.RowPrice(row);
            reuse.Volume = d.Volume;
            reuse.Bid = d.Bid;
            reuse.Ask = d.Ask;
            reuse.Between = d.Between;
            reuse.Ticks = d.Trades;
            reuse.Time = d.Trades;
            return reuse;
        }

        /// <summary>Candle VWAP (volume weighted average price).</summary>
        public decimal VWAP
        {
            get
            {
                decimal pv = 0, v = 0;
                foreach (var l in GetAllPriceLevels())
                {
                    pv += l.Price * l.Volume;
                    v += l.Volume;
                }

                return v > 0 ? pv / v : 0;
            }
        }

        /// <summary>Value area (70%) and POC.</summary>
        public ValueArea ValueArea
        {
            get
            {
                if (_c.Levels.IsEmpty) return null;
                var (hi, lo) = GetValueArea();
                return new ValueArea { ValueAreaHigh = hi, ValueAreaLow = lo, PointOfControl = _series.RowPrice(_c.PocRow) };
            }
        }

        /// <summary>Level the price spent the most time at (approximated by the number of trades).</summary>
        public PriceVolumeInfo MaxTimePriceInfo => MaxTickPriceInfo;

        /// <summary>Every level with trades, from the lowest price to the highest.</summary>
        public IEnumerable<PriceVolumeInfo> GetAllPriceLevels()
        {
            foreach (var (row, d) in _c.Levels.Enumerate()) yield return Make(row, d);
        }

        /// <summary>Levels sorted by price (materialized list).</summary>
        public List<PriceVolumeInfo> GetAllPriceLevelsList() => GetAllPriceLevels().ToList();

        /// <summary>POC of the candle (level with the most volume).</summary>
        public PriceVolumeInfo MaxVolumePriceInfo
        {
            get
            {
                if (_c.Levels.IsEmpty) return null;
                return _c.Levels.TryGet(_c.PocRow, out var d) ? Make(_c.PocRow, d) : null;
            }
        }

        public PriceVolumeInfo MaxAskPriceInfo => MaxBy(d => d.Ask);
        public PriceVolumeInfo MaxBidPriceInfo => MaxBy(d => d.Bid);
        public PriceVolumeInfo MaxTickPriceInfo => MaxBy(d => d.Trades);
        public PriceVolumeInfo MaxPositiveDeltaPriceInfo => MaxBy(d => d.Delta);
        public PriceVolumeInfo MaxNegativeDeltaPriceInfo => MaxBy(d => -d.Delta);

        private PriceVolumeInfo MaxBy(Func<LevelData, decimal> selector)
        {
            long best = 0;
            decimal bestV = decimal.MinValue;
            LevelData bestD = default;
            foreach (var (row, d) in _c.Levels.Enumerate())
            {
                var v = selector(d);
                if (v > bestV)
                {
                    bestV = v;
                    best = row;
                    bestD = d;
                }
            }

            return bestV == decimal.MinValue ? null : Make(best, bestD);
        }

        /// <summary>Value area (70% of the volume by default) around the POC.</summary>
        public (decimal High, decimal Low) GetValueArea(decimal percent = 70m)
        {
            var levels = _c.Levels;
            if (levels.IsEmpty) return (High, Low);
            decimal total = 0;
            foreach (var (_, d) in levels.Enumerate()) total += d.Volume;
            decimal target = total * percent / 100m;
            long up = _c.PocRow, dn = _c.PocRow;
            decimal acc = levels.Get(_c.PocRow).Volume;
            while (acc < target && (up < levels.MaxRow || dn > levels.MinRow))
            {
                decimal vu = up < levels.MaxRow ? levels.Get(up + 1).Volume : -1;
                decimal vd = dn > levels.MinRow ? levels.Get(dn - 1).Volume : -1;
                if (vu >= vd)
                {
                    up++;
                    acc += Math.Max(0, vu);
                }
                else
                {
                    dn--;
                    acc += Math.Max(0, vd);
                }
            }

            return (_series.RowPrice(up), _series.RowPrice(dn));
        }

        public override string ToString() => $"{Time:yyyy-MM-dd HH:mm} O:{Open} H:{High} L:{Low} C:{Close} V:{Volume}";
    }
}
