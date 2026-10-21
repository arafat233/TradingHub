using System.Drawing;
using SkiaSharp;
using TradingHub.Indicators;

namespace TradingHub.Charting;

/// <summary>Chart navigation state (horizontal zoom, scroll, price scale).</summary>
public sealed class ChartViewport
{
    /// <summary>Width of each bar in pixels (includes the gap between bars).</summary>
    public double BarWidth { get; set; } = 90;

    /// <summary>How many bars it is scrolled back from the right edge (0 = it follows the live feed).</summary>
    public double ScrollBars { get; set; }

    /// <summary>Empty space to the right of the last bar, in pixels.</summary>
    public double RightMarginPx { get; set; } = 70;

    /// <summary>Empty bars to the right of the last one (derived from RightMarginPx).</summary>
    public int RightMarginBars => Math.Max(1, (int)Math.Ceiling(RightMarginPx / Math.Max(1, BarWidth)));

    /// <summary>Automatic price scale.</summary>
    public bool AutoScale { get; set; } = true;

    /// <summary>Prices of the edges when the scale is manual.</summary>
    public double PriceTop { get; set; }
    public double PriceBottom { get; set; }

    public const double MinBarWidth = 1.5;
    public const double MaxBarWidth = 400;

    public int BarSpacing => BarWidth >= 40 ? Math.Max(3, (int)(BarWidth * 0.06)) : BarWidth >= 5 ? Math.Max(1, (int)(BarWidth * 0.22)) : 0;

    public ChartViewport Clone() => (ChartViewport)MemberwiseClone();
}

/// <summary>Chart layout computed on every frame.</summary>
public sealed class ChartLayout
{
    public const float PriceAxisWidth = 78;
    public const float TimeAxisHeight = 24;

    public SKRect Bounds { get; init; }
    public SKRect Plot { get; init; }
    public SKRect PriceAxis { get; init; }
    public SKRect TimeAxis { get; init; }
    public List<PanelLayout> Panels { get; } = new();
    public PanelLayout Main => Panels[0];

    public int Count { get; init; }
    public double BarWidth { get; init; }
    public int BarSpacing { get; init; }
    public double ScrollBars { get; init; }
    public int RightMarginBars { get; init; }

    public int FirstVisible { get; set; }
    public int LastVisible { get; set; }

    /// <summary>Base rows grouped by visible footprint row (the renderer decides it based on the zoom).</summary>
    public int FootprintRowFactor { get; set; } = 1;

    /// <summary>Center X of a bar (it can be off screen or in the future).</summary>
    public float CenterX(int bar) =>
        (float)(Plot.Right - (Count - 1 - bar + RightMarginBars - ScrollBars + 0.5) * BarWidth);

    public float CenterX(double bar) =>
        (float)(Plot.Right - (Count - 1 - bar + RightMarginBars - ScrollBars + 0.5) * BarWidth);

    /// <summary>Left edge of a bar's slot.</summary>
    public float LeftX(int bar) => CenterX(bar) - (float)(BarWidth / 2);

    /// <summary>Bar under an X coordinate (it can be &lt; 0 or &gt;= Count).</summary>
    public int BarAt(float x) => (int)Math.Floor(BarAtExact(x) + 0.5);

    public double BarAtExact(float x) => Count - 1 + RightMarginBars - ScrollBars + 0.5 - (Plot.Right - x) / BarWidth;

    public PanelLayout? PanelAt(float y) => Panels.FirstOrDefault(p => y >= p.Rect.Top && y <= p.Rect.Bottom);
}

/// <summary>A panel (the price one or an indicator one) with its vertical scale.</summary>
public sealed class PanelLayout : IChartContainer
{
    public PanelLayout(ChartLayout chart, SKRect rect, bool isPrice)
    {
        Chart = chart;
        Rect = rect;
        IsPrice = isPrice;
    }

    public ChartLayout Chart { get; }
    public SKRect Rect { get; }
    public bool IsPrice { get; }

    /// <summary>Value at the top / bottom edge.</summary>
    public double Top { get; set; } = 1;
    public double Bottom { get; set; }

    /// <summary>Row size (price panel).</summary>
    public decimal RowSize { get; set; } = 1m;

    /// <summary>Indicator panel (null on the price one).</summary>
    public SubPanel? SubPanel { get; set; }

    public double Range => Top - Bottom == 0 ? 1 : Top - Bottom;

    public float Y(double value) => (float)(Rect.Top + (Top - value) / Range * Rect.Height);

    public float Y(decimal value) => Y((double)value);

    public double ValueAt(float y) => Top - (y - Rect.Top) / Rect.Height * Range;

    /// <summary>Height in pixels of a price row.</summary>
    public double RowHeight => (double)RowSize / Range * Rect.Height;

    // ------------------------------------------------------------- IChartContainer (for indicators)

    Rectangle IChartContainer.Region => new((int)Rect.Left, (int)Rect.Top, (int)Rect.Width, (int)Rect.Height);
    public Rectangle RelativeRegion => new(0, 0, (int)Rect.Width, (int)Rect.Height);
    public int BarsWidth => Math.Max(1, (int)Math.Round(Chart.BarWidth - Chart.BarSpacing));
    public int BarSpacing => Chart.BarSpacing;
    public decimal Step => RowSize;
    public decimal PriceRowHeight => (decimal)Math.Max(0.0001, RowHeight);
    public int TotalBars => Chart.Count;
    public decimal High => (decimal)Top;
    public decimal Low => (decimal)Bottom;
    public decimal Minimum => (decimal)Bottom;
    public decimal Maximum => (decimal)Top;
    public int FirstVisibleBarNumber => Chart.FirstVisible;
    public int LastVisibleBarNumber => Chart.LastVisible;

    public int GetXByBar(int bar, bool isStartOfBar = true) =>
        (int)Math.Round(isStartOfBar ? Chart.LeftX(bar) + Chart.BarSpacing / 2f : Chart.CenterX(bar));

    public int GetYByPrice(decimal price, bool isStartOfPrice = true)
    {
        var main = Chart.Main;
        // level prices (multiples of the row step) are placed at the visual center of their row
        float y = RowCenterOf != null && price % main.RowSize == 0 ? main.Y(RowCenterOf(price)) : main.Y(price);
        if (isStartOfPrice) y -= (float)(main.RowHeight / 2);
        return (int)Math.Round(y);
    }

    /// <summary>Converts a level price to the center of its row (the renderer assigns it).</summary>
    public Func<decimal, decimal>? RowCenterOf { get; set; }

    public decimal GetPriceByY(int y) => (decimal)Chart.Main.ValueAt(y);

    public int GetBarByX(int x) => Chart.BarAt(x);

    public int GetYByValue(decimal value) => (int)Math.Round(Y(value));

    public decimal GetValueByY(int y) => (decimal)ValueAt(y);
}
