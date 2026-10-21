using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using TradingHub.Common.Collections.Synchronized;

namespace TradingHub.Indicators
{
    /// <summary>Data series of an indicator (a line, a histogram, candles, colors...).</summary>
    public interface IDataSeries : INotifyPropertyChanged
    {
        string Id { get; }
        string Name { get; set; }
        int Count { get; }
        object this[int index] { get; set; }
        bool IsHidden { get; set; }
        bool Visible { get; set; }
        bool ScaleIt { get; set; }
        bool DrawAbovePrice { get; set; }
        Type ValueType { get; }

        /// <summary>Key of the description (localization) the editor shows.</summary>
        string DescriptionKey { get; set; }

        /// <summary>Uses the panel's minimized mode if it is enabled.</summary>
        bool UseMinimizedModeIfEnabled { get; set; }

        /// <summary>It is not offered for alerts.</summary>
        bool IgnoredByAlerts { get; set; }
        void Clear();
        void Resize(int count);
    }

    /// <summary>Base of the series: an array that grows on assignment and returns default out of range.</summary>
    public abstract class BaseDataSeries<T> : IDataSeries, IEnumerable<T>
    {
        protected T[] _values = Array.Empty<T>();
        private int _count;
        private string _name;
        private bool _isHidden;
        private bool _visible = true;

        protected BaseDataSeries(string id, string name)
        {
            Id = id ?? name ?? GetType().Name;
            _name = name ?? id;
        }

        protected BaseDataSeries(string name) : this(name, name)
        {
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [Browsable(false)]
        public string Id { get; set; }

        [Browsable(false)]
        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        [Browsable(false)]
        public string DescriptionKey { get; set; }

        [Browsable(false)]
        public bool IsHidden
        {
            get => _isHidden;
            set => SetProperty(ref _isHidden, value);
        }

        [Display(Name = "Visible", Order = 0)]
        public bool Visible
        {
            get => _visible;
            set => SetProperty(ref _visible, value);
        }

        [Browsable(false)]
        public bool ScaleIt { get; set; } = true;

        [Browsable(false)]
        public bool DrawAbovePrice { get; set; }

        [Browsable(false)]
        public bool IgnoredByAlerts { get; set; }

        [Browsable(false)]
        public bool UseMinimizedModeIfEnabled { get; set; }

        [Browsable(false)]
        public bool ResetAlertsOnNewBar { get; set; }

        [Browsable(false)]
        public bool ShowTooltip { get; set; } = true;

        [Browsable(false)]
        public bool IsDynamicColor { get; set; }

        [Browsable(false)]
        public int Count => _count;

        [Browsable(false)]
        public Type ValueType => typeof(T);

        protected virtual T CreateDefault(int index) => default;

        protected virtual bool CreatesOnRead => false;

        public virtual T this[int index]
        {
            get
            {
                if (index < 0) return default;
                if (index >= _count)
                {
                    if (!CreatesOnRead) return default;
                    EnsureCount(index + 1);
                }

                if (CreatesOnRead && _values[index] == null) _values[index] = CreateDefault(index);
                return _values[index];
            }
            set
            {
                if (index < 0) return;
                EnsureCount(index + 1);
                _values[index] = value;
            }
        }

        object IDataSeries.this[int index]
        {
            get => this[index];
            set => this[index] = (T)Convert(value);
        }

        protected virtual object Convert(object value) => value;

        protected void EnsureCount(int count)
        {
            if (count > _values.Length)
            {
                int cap = Math.Max(count, Math.Max(16, _values.Length * 2));
                Array.Resize(ref _values, cap);
            }

            if (count > _count) _count = count;
        }

        public virtual void Resize(int count)
        {
            if (count < 0) count = 0;
            if (count > _count) EnsureCount(count);
            else if (count < _count)
            {
                Array.Clear(_values, count, _count - count);
                _count = count;
            }
        }

        public virtual void Clear()
        {
            Array.Clear(_values, 0, _count);
            _count = 0;
        }

        /// <summary>Value on the last bar (or default).</summary>
        [Browsable(false)]
        public T LastValue => _count > 0 ? this[_count - 1] : default;

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _count; i++) yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        protected bool SetProperty<TV>(ref TV field, TV value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<TV>.Default.Equals(field, value)) return false;
            field = value;
            RaisePropertyChanged(propertyName);
            return true;
        }

        protected void RaisePropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public override string ToString() => Name;
    }

    /// <summary>Per-bar colors of a series (Color.Empty = use the series color).</summary>
    public sealed class SeriesColors
    {
        private Color[] _colors = Array.Empty<Color>();

        public Color this[int bar]
        {
            get => bar >= 0 && bar < _colors.Length ? _colors[bar] : Color.Empty;
            set
            {
                if (bar < 0) return;
                if (bar >= _colors.Length) Array.Resize(ref _colors, Math.Max(bar + 1, Math.Max(16, _colors.Length * 2)));
                _colors[bar] = value;
            }
        }

        public bool Has(int bar) => bar >= 0 && bar < _colors.Length && !_colors[bar].IsEmpty;

        public void Clear() => Array.Clear(_colors);
    }

    /// <summary>Numeric series: lines, histograms, dots, arrows...</summary>
    public class ValueDataSeries : BaseDataSeries<decimal>
    {
        private Color _color = Color.FromArgb(255, 70, 130, 230);
        private VisualMode _visualType = VisualMode.Line;
        private int _width = 1;
        private LineDashStyle _lineDashStyle = LineDashStyle.Solid;
        private readonly HashSet<int> _endLines = new();

        public ValueDataSeries(string id, string name) : base(id, name)
        {
        }

        public ValueDataSeries(string name) : base(name)
        {
        }

        public ValueDataSeries() : base("Value")
        {
        }

        [Display(Name = "Color", Order = 10)]
        public Color Color
        {
            get => _color;
            set => SetProperty(ref _color, value);
        }

        [Browsable(false)]
        [JsonIgnore]
        public SeriesColors Colors { get; } = new();

        /// <summary>Format of the value on the axis (e.g. "{0:F2}").</summary>
        [Browsable(false)]
        [JsonIgnore]
        public string StringFormat { get; set; }

        /// <summary>Color of the values (labels).</summary>
        [Browsable(false)]
        public Color ValuesColor { get; set; } = Color.White;

        /// <summary>Currency if the series shows money.</summary>
        [Browsable(false)]
        [JsonIgnore]
        public TradingHub.Trading.MoneyCurrency? ValueCurrency { get; set; }

        [Display(Name = "Type", Order = 20)]
        public VisualMode VisualType
        {
            get => _visualType;
            set => SetProperty(ref _visualType, value);
        }

        [Display(Name = "Width", Order = 30)]
        [Range(1, 20)]
        public int Width
        {
            get => _width;
            set => SetProperty(ref _width, Math.Max(1, value));
        }

        [Display(Name = "Line style", Order = 40)]
        public LineDashStyle LineDashStyle
        {
            get => _lineDashStyle;
            set => SetProperty(ref _lineDashStyle, value);
        }

        [Display(Name = "Show zeros", Order = 50)]
        public bool ShowZeroValue { get; set; } = true;

        [Display(Name = "Current value on the axis", Order = 60)]
        public bool ShowCurrentValue { get; set; } = true;

        [Browsable(false)]
        public bool ShowNullValue { get; set; }

        [Browsable(false)]
        public decimal ZeroValue { get; set; }

        [Browsable(false)]
        public int Digits { get; set; } = -1;

        [Browsable(false)]
        public Color ValueAxisBackground { get; set; } = Color.Empty;

        /// <summary>Breaks the line after this bar (it does not join it with the next one).</summary>
        public void SetPointOfEndLine(int bar) => _endLines.Add(bar);

        public void RemovePointOfEndLine(int bar) => _endLines.Remove(bar);

        public bool IsThisPointOfEndLine(int bar) => _endLines.Contains(bar);

        public bool IsThisPointOfStartBar(int bar) => _endLines.Contains(bar - 1);

        public override void Clear()
        {
            base.Clear();
            Colors.Clear();
            _endLines.Clear();
        }

        protected override object Convert(object value) => value switch
        {
            null => 0m,
            decimal d => d,
            _ => System.Convert.ToDecimal(value),
        };

        public decimal Max(int count, int bar)
        {
            decimal m = decimal.MinValue;
            for (int i = Math.Max(0, bar - count + 1); i <= bar; i++) m = Math.Max(m, this[i]);
            return m == decimal.MinValue ? 0 : m;
        }

        public decimal Min(int count, int bar)
        {
            decimal m = decimal.MaxValue;
            for (int i = Math.Max(0, bar - count + 1); i <= bar; i++) m = Math.Min(m, this[i]);
            return m == decimal.MaxValue ? 0 : m;
        }
    }

    /// <summary>Value of a band (upper and lower).</summary>
    public class RangeValue
    {
        public decimal Upper { get; set; }
        public decimal Lower { get; set; }

        public RangeValue()
        {
        }

        public RangeValue(decimal upper, decimal lower)
        {
            Upper = upper;
            Lower = lower;
        }

        public override string ToString() => $"{Lower} - {Upper}";
    }

    /// <summary>Band series: it fills between Upper and Lower.</summary>
    public class RangeDataSeries : BaseDataSeries<RangeValue>
    {
        public RangeDataSeries(string id, string name) : base(id, name)
        {
        }

        public RangeDataSeries(string name) : base(name)
        {
        }

        [Display(Name = "Fill color", Order = 10)]
        public Color RangeColor { get; set; } = Color.FromArgb(40, 70, 130, 230);

        protected override bool CreatesOnRead => true;
        protected override RangeValue CreateDefault(int index) => new();
    }

    /// <summary>Candle of a CandleDataSeries (for example, the cumulative delta drawn as candles).</summary>
    public class Candle
    {
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public DateTime Time { get; set; }
        public Color? Color { get; set; }

        public Candle()
        {
        }

        public Candle(decimal open, decimal high, decimal low, decimal close)
        {
            Open = open;
            High = high;
            Low = low;
            Close = close;
        }

        public bool IsEmpty => Open == 0 && High == 0 && Low == 0 && Close == 0;

        public Candle Clone() => (Candle)base.MemberwiseClone();

        public new Candle MemberwiseClone() => (Candle)base.MemberwiseClone();
    }

    /// <summary>Where a candle's tooltip is anchored.</summary>
    public enum CandleTooltipAnchor
    {
        Close,
        Top,
        Bottom,
    }

    /// <summary>Candle series on a panel.</summary>
    public class CandleDataSeries : BaseDataSeries<Candle>
    {
        public CandleDataSeries(string id, string name) : base(id, name)
        {
        }

        public CandleDataSeries(string name) : base(name)
        {
        }

        [Display(Name = "Bullish candle", Order = 10)]
        public Color UpCandleColor { get; set; } = Color.FromArgb(255, 73, 209, 73);

        [Display(Name = "Bearish candle", Order = 20)]
        public Color DownCandleColor { get; set; } = Color.FromArgb(255, 232, 69, 72);

        [Display(Name = "Border", Order = 30)]
        public Color BorderColor { get; set; } = Color.FromArgb(255, 120, 120, 120);

        [Display(Name = "Draw border", Order = 40)]
        public bool DrawCandleBorder { get; set; } = true;

        [Browsable(false)]
        public bool ShowCurrentValue { get; set; } = true;

        [Browsable(false)]
        public SeriesColors Colors { get; } = new();

        /// <summary>Candles or bars.</summary>
        [Display(Name = "Mode", Order = 50)]
        public CandleVisualMode Mode { get; set; } = CandleVisualMode.Candles;

        [Browsable(false)]
        public Color ValuesColor { get; set; } = Color.White;

        [Browsable(false)]
        [JsonIgnore]
        public TradingHub.Trading.MoneyCurrency? ValueCurrency { get; set; }

        /// <summary>Does not draw the empty candles (everything at zero).</summary>
        [Browsable(false)]
        public bool HideZeroCandles { get; set; }

        [Browsable(false)]
        public bool HideOpenCloseLabels { get; set; }

        [Browsable(false)]
        public CandleTooltipAnchor TooltipAnchor { get; set; }

        protected override bool CreatesOnRead => true;
        protected override Candle CreateDefault(int index) => new();

        public override void Clear()
        {
            base.Clear();
            Colors.Clear();
        }
    }

    /// <summary>Colors the main chart's candles bar by bar (null = normal color).</summary>
    public class PaintbarsDataSeries : BaseDataSeries<Color?>
    {
        public PaintbarsDataSeries(string id, string name) : base(id, name)
        {
        }

        public PaintbarsDataSeries(string name) : base(name)
        {
        }

        /// <summary>Hides the chart's candles (only the series' ones are drawn).</summary>
        [Browsable(false)]
        public bool HideChart { get; set; }

        protected override object Convert(object value) => value switch
        {
            null => null,
            Color c => c,
            _ => (Color?)value,
        };
    }

    public enum ObjectType
    {
        Rectangle,
        Ellipse,
        Triangle,
        Diamond,
        Cross,
        Square,
        OnlyCluster,
        Line,
        DownArrow,
        UpArrow,
    }

    public enum PriceSelectionValueTypes
    {
        Default,
        Ask,
        Bid,
    }

    /// <summary>Marks a price range on a bar (big trade circles, highlighted cells...).</summary>
    public class PriceSelectionValue
    {
        public PriceSelectionValue(decimal price)
        {
            MinimumPrice = MaximumPrice = price;
        }

        public PriceSelectionValue(decimal minimumPrice, decimal maximumPrice)
        {
            MinimumPrice = minimumPrice;
            MaximumPrice = maximumPrice;
        }

        public decimal MinimumPrice { get; set; }
        public decimal MaximumPrice { get; set; }
        public ObjectType VisualObject { get; set; } = ObjectType.Rectangle;
        public int Size { get; set; } = 10;
        public int MinimumSize { get; set; } = 1;
        public int MaximumSize { get; set; } = 100;
        public Color PriceSelectionColor { get; set; } = Color.FromArgb(80, 70, 130, 230);
        public Color ObjectColor { get; set; } = Color.FromArgb(160, 70, 130, 230);
        public Color ObjectBorderColor { get; set; } = Color.Empty;
        public int ObjectsTransparency { get; set; }
        public string Tooltip { get; set; }
        public object Context { get; set; }
        public bool DrawAbovePrice { get; set; } = true;
        public SelectionType SelectionSide { get; set; }
        public decimal Volume { get; set; }
        public int ClusterTransparency { get; set; }
        public bool IsBorderEnabled { get; set; }
    }

    public class PriceSelectionDataSeries : BaseDataSeries<SyncList<PriceSelectionValue>>
    {
        public PriceSelectionDataSeries(string id, string name) : base(id, name)
        {
        }

        public PriceSelectionDataSeries(string name) : base(name)
        {
        }

        protected override bool CreatesOnRead => true;
        protected override SyncList<PriceSelectionValue> CreateDefault(int index) => new();

        /// <summary>The series' content changed.</summary>
        public event Action<int> Changed;

        public void RaiseChanged(int bar) => Changed?.Invoke(bar);
    }

    /// <summary>Series of arbitrary objects (it is not drawn).</summary>
    public class ObjectDataSeries : BaseDataSeries<object>
    {
        public ObjectDataSeries(string id, string name) : base(id, name)
        {
        }

        public ObjectDataSeries(string name) : base(name)
        {
        }
    }

    /// <summary>List of an indicator's series (DataSeries[0] is the main one).</summary>
    public sealed class IndicatorDataSeriesCollection : Collection<IDataSeries>
    {
        internal IndicatorDataSeriesCollection()
        {
        }

        public IDataSeries FindById(string id) => this.FirstOrDefault(s => s.Id == id);

        public void ForEach(Action<IDataSeries> action)
        {
            foreach (var s in this.ToList()) action(s);
        }

        public void AddRange(IEnumerable<IDataSeries> series)
        {
            foreach (var s in series) Add(s);
        }
    }
}
