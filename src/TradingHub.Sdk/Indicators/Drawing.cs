using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Runtime.CompilerServices;
using TradingHub.Common;

namespace TradingHub.Indicators
{
    /// <summary>Horizontal line fixed on a panel (the RSI's 30/70 levels, the MACD's zero...).</summary>
    public class LineSeries : INotifyPropertyChanged
    {
        private decimal _value;
        private Color _color = Color.FromArgb(255, 120, 120, 130);
        private int _width = 1;
        private LineDashStyle _lineDashStyle = LineDashStyle.Dash;
        private bool _useScale;
        private bool _isHidden;

        public LineSeries(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public LineSeries(string name) : this(name, name)
        {
        }

        public event PropertyChangedEventHandler PropertyChanged;

        [Browsable(false)]
        public string Id { get; }

        [Browsable(false)]
        public string Name { get; set; }

        /// <summary>Key of the description (localization).</summary>
        [Browsable(false)]
        public string DescriptionKey { get; set; }

        [Display(Name = "Value", Order = 10)]
        public decimal Value
        {
            get => _value;
            set => Set(ref _value, value);
        }

        [Display(Name = "Color", Order = 20)]
        public Color Color
        {
            get => _color;
            set => Set(ref _color, value);
        }

        [Display(Name = "Width", Order = 30)]
        public int Width
        {
            get => _width;
            set => Set(ref _width, Math.Max(1, value));
        }

        [Display(Name = "Style", Order = 40)]
        public LineDashStyle LineDashStyle
        {
            get => _lineDashStyle;
            set => Set(ref _lineDashStyle, value);
        }

        [Display(Name = "Include in scale", Order = 50)]
        public bool UseScale
        {
            get => _useScale;
            set => Set(ref _useScale, value);
        }

        [Browsable(false)]
        public bool IsHidden
        {
            get => _isHidden;
            set => Set(ref _isHidden, value);
        }

        [Browsable(false)]
        public VisualMode VisualType { get; set; } = VisualMode.Line;

        [Browsable(false)]
        public string Text { get; set; }

        [Browsable(false)]
        public bool Visible { get; set; } = true;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public override string ToString() => Name;
    }

}

namespace TradingHub.Indicators.Drawing
{
    /// <summary>Horizontal line that starts on a bar and extends until the price touches it.</summary>
    public class LineTillTouch : TrendLine
    {
        public LineTillTouch(int bar, decimal price, UniversalPen pen, int length = 0)
            : base(bar, price, bar, price, pen)
        {
            Length = length;
        }

        /// <summary>Maximum length in bars (0 = no limit).</summary>
        public int Length { get; set; }

        /// <summary>Whether it has already been touched by the price.</summary>
        public bool Finished { get; set; }

        public object Context { get; set; }
    }

    /// <summary>Trend line between two points (bar, price).</summary>
    public class TrendLine
    {
        public TrendLine(int firstBar, decimal firstPrice, int secondBar, decimal secondPrice, UniversalPen pen)
        {
            FirstBar = firstBar;
            FirstPrice = firstPrice;
            SecondBar = secondBar;
            SecondPrice = secondPrice;
            Pen = pen;
        }

        public int FirstBar { get; set; }
        public decimal FirstPrice { get; set; }
        public int SecondBar { get; set; }
        public decimal SecondPrice { get; set; }
        public UniversalPen Pen { get; set; }

        /// <summary>Whether it extends indefinitely to the right.</summary>
        public bool IsRay { get; set; }

        public Color Color => Pen?.Color ?? Color.Gray;
    }

    /// <summary>Rectangle between two points (bar, price).</summary>
    public class DrawingRectangle
    {
        public DrawingRectangle(int firstBar, decimal firstPrice, int secondBar, decimal secondPrice, UniversalPen pen, UniversalSolidBrush brush)
        {
            FirstBar = firstBar;
            FirstPrice = firstPrice;
            SecondBar = secondBar;
            SecondPrice = secondPrice;
            Pen = pen;
            Brush = brush;
        }

        public int FirstBar { get; set; }
        public decimal FirstPrice { get; set; }
        public int SecondBar { get; set; }
        public decimal SecondPrice { get; set; }
        public UniversalPen Pen { get; set; }
        public UniversalSolidBrush Brush { get; set; }
    }

    /// <summary>Text anchored to a bar and a price.</summary>
    public class DrawingText
    {
        public enum TextAlign
        {
            Left,
            Center,
            Right,
        }

        public string Tag { get; set; }
        public string Text { get; set; }
        public bool IsAbovePrice { get; set; }
        public int Bar { get; set; }
        public decimal TextPrice { get; set; }
        public int YOffset { get; set; }
        public int XOffset { get; set; }
        public Color Textcolor { get; set; }
        public Color BorderColor { get; set; }
        public Color FillColor { get; set; }
        public float FontSize { get; set; } = 10;
        public TextAlign Align { get; set; } = TextAlign.Center;
        public bool AutoSize { get; set; }
        public bool Visible { get; set; } = true;
    }
}
