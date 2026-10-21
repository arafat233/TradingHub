using System.ComponentModel.DataAnnotations;

namespace TradingHub.Indicators
{
    /// <summary>Marks a property as a calculation parameter.</summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public sealed class ParameterAttribute : Attribute
    {
    }

    /// <summary>How a value series is drawn.</summary>
    public enum VisualMode
    {
        [Display(Name = "Line")] Line,
        [Display(Name = "Histogram")] Histogram,
        [Display(Name = "Hash")] Hash,
        [Display(Name = "Cross")] Cross,
        [Display(Name = "Dots")] Dots,
        [Display(Name = "Block")] Block,
        [Display(Name = "Square")] Square,
        [Display(Name = "Arrow up")] UpArrow,
        [Display(Name = "Arrow down")] DownArrow,
        [Display(Name = "Value on the axis only")] OnlyValueOnAxis,
        [Display(Name = "Hidden")] Hide,
        [Display(Name = "Bands")] Bands,
        [Display(Name = "Dots (line)")] Points,
        [Display(Name = "Levels")] Levels,
        [Display(Name = "Escalones")] Steps,
    }

    public enum LineDashStyle
    {
        [Display(Name = "Solid")] Solid,
        [Display(Name = "Guiones")] Dash,
        [Display(Name = "Dot")] Dot,
        [Display(Name = "Dash-dot")] DashDot,
        [Display(Name = "Dash-dot-dot")] DashDotDot,
    }

    /// <summary>Candle value OnCalculate receives as "value".</summary>
    public enum DataSeriesType
    {
        Open,
        High,
        Low,
        Close,
        HL2,
        HLC3,
        OHLC4,
        Volume,
        Delta,
        Ticks,
        Bid,
        Ask,
    }

    /// <summary>Side of a trade (as the indicators report it).</summary>
    public enum TradeDirection
    {
        Between = 0,
        Buy = 1,
        Sell = 2,
    }

    public enum MarketDataType
    {
        Trade = 0,
        Bid = 1,
        Ask = 2,
    }

    public enum ChartVisualModes
    {
        Candles,
        Clusters,
        Line,
        Bars,
        Mountain,
        Heatmap,
    }

    public enum CumulativeTradeStates
    {
        Active,
        Finished,
    }
}
