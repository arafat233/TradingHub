using System.ComponentModel.DataAnnotations;

namespace TradingHub.Charting;

public enum ChartStyle
{
    [Display(Name = "Candles")] Candles,
    [Display(Name = "OHLC bars")] Bars,
    [Display(Name = "Line")] Line,
    [Display(Name = "Footprint (clusters)")] Footprint,
    [Display(Name = "Heikin Ashi")] HeikinAshi,
}

/// <summary>How the cells are colored.</summary>
public enum ClusterColorScheme
{
    [Display(Name = "Proportional to volume")] VolumeProportion,
    [Display(Name = "By delta")] Delta,
    [Display(Name = "Bid / Ask")] BidAsk,
    [Display(Name = "Heat map")] Heatmap,
    [Display(Name = "Solid")] Solid,
    [Display(Name = "No background")] None,
}

/// <summary>Visual settings of a chart.</summary>
/// <summary>Dash of the line between a trade's entry and exit.</summary>
public enum TradeLineStyle
{
    [Display(Name = "Dashed")] Dashed,
    [Display(Name = "Solid")] Solid,
    [Display(Name = "Dotted")] Dotted,
}

public sealed class ChartSettings
{
    public ChartStyle Style { get; set; } = ChartStyle.Footprint;

    /// <summary>How each footprint candle is built (columns chosen by the user).</summary>
    public FootprintLayout Layout { get; set; } = FootprintLayout.Presets[0].Clone();

    /// <summary>Shade the value area (70%) of each candle.</summary>
    public bool ShowValueArea { get; set; }

    public bool ShowGrid { get; set; } = true;
    public bool ShowSessionLines { get; set; } = true;
    public bool ShowInfoPanel { get; set; } = true;

    /// <summary>Instrument name at the top left of the chart (it is already on the toolbar; off by default).</summary>
    public bool ShowLegendTitle { get; set; }

    /// <summary>Candle values (O, H, L, C, Vol, Δ) in the legend.</summary>
    public bool ShowLegendValues { get; set; } = true;

    /// <summary>Indicator names on the chart (the legend's little arrow collapses them, as in TradingView).</summary>
    public bool ShowIndicatorNames { get; set; } = true;
    public bool ShowLastPriceLine { get; set; } = true;

    /// <summary>Time left in the current candle, under the last price on the price axis (time candles only).</summary>
    public bool ShowCountdown { get; set; } = true;

    /// <summary>
    /// Keeps the last price in the vertical middle of the chart while the chart follows the live edge: the price scale re-centres on it as the
    /// price goes up or down (still showing every visible candle). Off, the scale just fits the visible candles. Dragging the price scale or the
    /// chart vertically switches the automatic scale off, as always; a double click brings it back.
    /// </summary>
    public bool CenterOnPrice { get; set; } = true;

    /// <summary>Mark your own trades (fills and closed trades).</summary>
    public bool ShowTradeMarks { get; set; } = true;

    /// <summary>Thickness of the entry-to-exit line of each trade (your own and the strategies').</summary>
    public double TradeLineWidth { get; set; } = 1.5;

    public TradeLineStyle TradeLineStyle { get; set; } = TradeLineStyle.Dashed;

    /// <summary>Ticks per footprint row. 0 = automatic.</summary>
    public int PriceStep { get; set; }

    /// <summary>Time zone for showing the times (null = the computer's).</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>This chart's own colors (background, candles, axes…), by theme color name. Null = the usual ones.</summary>
    public Dictionary<string, uint>? Colors { get; set; }

    public TimeZoneInfo TimeZone
    {
        get
        {
            if (string.IsNullOrEmpty(TimeZoneId)) return TimeZoneInfo.Local;
            try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
            catch { return TimeZoneInfo.Local; }
        }
    }

    public ChartSettings Clone()
    {
        var c = (ChartSettings)MemberwiseClone();
        c.Layout = Layout.Clone();
        c.Colors = Colors == null ? null : new Dictionary<string, uint>(Colors);
        return c;
    }
}
