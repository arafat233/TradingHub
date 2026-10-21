using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators
{
    /// <summary>Compatibility extensions used by the indicators.</summary>
    public static class IndicatorExtensions
    {
        /// <summary>Color conversion (on this platform CrossColor and System.Drawing.Color are the same thing).</summary>
        public static Color Convert(this Color color) => color;

        public static Color? Convert(this Color? color) => color;

        public static Color SetTransparency(this Color color, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), color);

        /// <summary>Sum of the last <paramref name="period"/> values up to <paramref name="bar"/>.</summary>
        public static decimal CalcSum(this IDataSeries series, int period, int bar)
        {
            decimal sum = 0;
            for (int i = Math.Max(0, bar - period + 1); i <= bar; i++) sum += ToDec(series[i]);
            return sum;
        }

        public static decimal CalcAverage(this IDataSeries series, int period, int bar)
        {
            int start = Math.Max(0, bar - period + 1);
            int n = bar - start + 1;
            return n <= 0 ? 0 : CalcSum(series, period, bar) / n;
        }

        public static decimal MAX(this IDataSeries series, int period, int bar)
        {
            decimal m = decimal.MinValue;
            for (int i = Math.Max(0, bar - period + 1); i <= bar; i++) m = Math.Max(m, ToDec(series[i]));
            return m == decimal.MinValue ? 0 : m;
        }

        public static decimal MIN(this IDataSeries series, int period, int bar)
        {
            decimal m = decimal.MaxValue;
            for (int i = Math.Max(0, bar - period + 1); i <= bar; i++) m = Math.Min(m, ToDec(series[i]));
            return m == decimal.MaxValue ? 0 : m;
        }

        public static decimal GetValue(this IDataSeries series, int bar) => ToDec(series[bar]);

        private static decimal ToDec(object o) => o switch
        {
            null => 0m,
            decimal d => d,
            IndicatorCandle c => c.Close,
            _ => System.Convert.ToDecimal(o),
        };

        /// <summary>Draws a label with the value over the price axis (to the right of the panel).</summary>
        public static void DrawLabelOnPriceAxis(this Indicator indicator, RenderContext context, string text, int y, RenderFont font, Color background, Color textColor)
        {
            if (indicator?.ChartInfo == null || string.IsNullOrEmpty(text)) return;
            var region = indicator.ChartInfo.PriceChartContainer.Region;
            var size = context.MeasureString(text, font);
            int h = size.Height + 2;
            int x = region.Right + 1;
            int w = Math.Max(size.Width + 8, indicator.ChartInfo.Region.Right - x);
            var rect = new Rectangle(x, y - h / 2, w, h);
            context.FillRectangle(background, rect);
            context.DrawString(text, font, textColor, rect, new RenderStringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center });
        }
    }
}
