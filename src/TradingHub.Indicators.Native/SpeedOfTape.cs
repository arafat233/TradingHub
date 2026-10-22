using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Attributes;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Speed of Tape: how fast the market is trading, per candle: trades per second or contracts per second. The bar
/// is the candle's count divided by the seconds the candle took, so tick and volume candles (which close sooner when it is
/// busy) read correctly too. Bars well above their recent average (the "fast" multiple) are painted green if buyers
/// led and red if sellers did: that is where a break or an absorption usually starts.
/// </summary>
[DisplayName("Speed of Tape")]
[Category(IndicatorCategories.VolumeOrderFlow)]
public class SpeedOfTape : Indicator
{
    public enum SpeedUnit
    {
        [Display(Name = "Trades per second")] Trades,
        [Display(Name = "Contracts per second")] Volume,
    }

    private readonly ValueDataSeries _speed = new("Speed", "Speed") { VisualType = VisualMode.Histogram };
    private readonly ValueDataSeries _average = new("Average", "Average") { Color = Color.FromArgb(255, 255, 196, 0), Width = 2, VisualType = VisualMode.Line };
    private SpeedUnit _unit = SpeedUnit.Trades;
    private int _period = 20;
    private decimal _fast = 2m;

    public SpeedOfTape() : base(true)
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _speed;
        DataSeries.Add(_average);
    }

    [Display(Name = "Measure", GroupName = "Calculation", Order = 10)]
    public SpeedUnit Unit { get => _unit; set { _unit = value; RecalculateValues(); } }

    [Display(Name = "Average over (candles)", GroupName = "Calculation", Order = 20)]
    [Range(2, 1000)]
    public int Period { get => _period; set { _period = Math.Clamp(value, 2, 1000); RecalculateValues(); } }

    [Display(Name = "Fast when it is at least (x average)", GroupName = "Calculation", Order = 30,
        Description = "A candle this many times above the average is painted with the color of the side that led.")]
    [Range(1, 20)]
    public decimal FastMultiple { get => _fast; set { _fast = Math.Clamp(value, 1, 20); RecalculateValues(); } }

    [Display(Name = "Normal color", GroupName = "Display", Order = 100)]
    public Color NormalColor { get; set; } = Color.FromArgb(255, 110, 120, 140);

    [Display(Name = "Fast, buyers led", GroupName = "Display", Order = 110)]
    public Color BuyColor { get; set; } = Color.FromArgb(255, 73, 209, 73);

    [Display(Name = "Fast, sellers led", GroupName = "Display", Order = 120)]
    public Color SellColor { get; set; } = Color.FromArgb(255, 232, 69, 72);

    protected override void OnCalculate(int bar, decimal value)
    {
        var c = GetCandle(bar);
        // at least a second: a candle that opens and closes within the same second would otherwise read as infinite
        decimal seconds = Math.Max(1m, (decimal)(c.LastTime - c.Time).TotalSeconds);
        decimal speed = (_unit == SpeedUnit.Trades ? c.Ticks : c.Volume) / seconds;
        _speed[bar] = speed;

        // average of the candles before this one (this one is not compared against itself)
        decimal sum = 0;
        int n = 0;
        for (int i = Math.Max(0, bar - _period); i < bar; i++, n++) sum += (decimal)_speed[i];
        decimal avg = n > 0 ? sum / n : speed;
        _average[bar] = avg;

        bool fast = n >= 3 && avg > 0 && speed >= avg * _fast;
        _speed.Colors[bar] = !fast ? NormalColor : c.Delta >= 0 ? BuyColor : SellColor;
    }
}
