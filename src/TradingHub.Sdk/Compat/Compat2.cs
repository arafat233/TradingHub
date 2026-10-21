// Second part of the ATAS API compatibility: utilities and static types that
// its public indicators use (collections, colors, currencies, predefined pens…).

using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using TradingHub.Common;
using TradingHub.Common.Collections.Synchronized;
using TradingHub.Rendering.Heatmap;
using TradingHub.Rendering.Tools;
using TradingHub.Trading;

namespace TradingHub.Indicators
{
    /// <summary>Global platform settings the indicators query.</summary>
    public static class PlatformSettings
    {
        /// <summary>Value area percentage.</summary>
        public static int ValueAreaPercent { get; set; } = 70;

        /// <summary>Step (in rows) when expanding the value area.</summary>
        public static int ValueAreaStep { get; set; } = 1;

        /// <summary>Delay between value area recalculations (ms).</summary>
        public static int ValueAreaUpdateDelayMs { get; set; } = 500;
    }

    /// <summary>Heat map colors by percentage (0-100).</summary>
    public static class HeatmapExtensions
    {
        public static Color GetColor(HeatmapTypes type, int rate) => HeatmapColors.GetColor(type, Math.Clamp(rate, 0, 100) / 100.0);
    }

    /// <summary>Format for money values.</summary>
    public static class MoneyCurrencies
    {
        public static string Symbol(MoneyCurrency? currency) => currency switch
        {
            MoneyCurrency.USD or MoneyCurrency.USDT or MoneyCurrency.USDC or null => "$",
            MoneyCurrency.EUR => "€",
            MoneyCurrency.GBP => "£",
            MoneyCurrency.JPY => "¥",
            MoneyCurrency.BTC => "₿",
            _ => currency + " ",
        };

        public static string Format(decimal value, MoneyCurrency? currency, string format = "N2") =>
            Symbol(currency) + value.ToString(format, CultureInfo.InvariantCulture);

        public static string FormatCompact(decimal value, MoneyCurrency? currency) =>
            Symbol(currency) + VolumeFormat.Compact(value, 1);
    }

    /// <summary>Collection, enum and color utilities with ATAS's names.</summary>
    public static class AtasCompatExtensions
    {
        public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, Func<TKey, TValue> factory)
        {
            if (!dict.TryGetValue(key, out var v))
            {
                v = factory(key);
                dict[key] = v;
            }

            return v;
        }

        public static void IncrementValue<TKey>(this Dictionary<TKey, decimal> dict, TKey key, decimal value)
        {
            dict.TryGetValue(key, out var v);
            dict[key] = v + value;
        }

        public static int RemoveWhere<TKey, TValue>(this Dictionary<TKey, TValue> dict, Func<KeyValuePair<TKey, TValue>, bool> predicate)
        {
            var keys = dict.Where(predicate).Select(kv => kv.Key).ToList();
            foreach (var k in keys) dict.Remove(k);
            return keys.Count;
        }

        public static void ForEach<T>(this IEnumerable<T> items, Action<T> action)
        {
            foreach (var item in items.ToList()) action(item);
        }

        /// <summary>Display name of an enumeration value ([Display(Name)] or the name).</summary>
        public static string GetDisplayName(this Enum value)
        {
            var field = value.GetType().GetField(value.ToString());
            var display = field?.GetCustomAttribute<DisplayAttribute>();
            try
            {
                return display?.GetName() ?? value.ToString();
            }
            catch
            {
                return display?.Name ?? value.ToString();
            }
        }

        /// <summary>Applies transparency (0 = opaque, 1 = invisible).</summary>
        public static Color SetTransparency(this Color color, decimal transparency) =>
            Color.FromArgb((int)Math.Round(255 * (1 - Math.Clamp(transparency, 0m, 1m))), color);

        public static DashStyle To(this LineDashStyle style) => style switch
        {
            LineDashStyle.Dash => DashStyle.Dash,
            LineDashStyle.Dot => DashStyle.Dot,
            LineDashStyle.DashDot => DashStyle.DashDot,
            LineDashStyle.DashDotDot => DashStyle.DashDotDot,
            _ => DashStyle.Solid,
        };

        public static LineDashStyle To(this DashStyle style) => style switch
        {
            DashStyle.Dash => LineDashStyle.Dash,
            DashStyle.Dot => LineDashStyle.Dot,
            DashStyle.DashDot => LineDashStyle.DashDot,
            DashStyle.DashDotDot => LineDashStyle.DashDotDot,
            _ => LineDashStyle.Solid,
        };

        public static UniversalPen ToPen(this RenderPen pen) => new(pen.Color, pen.Width, pen.DashStyle);

        /// <summary>Runs a function over the list with the lock held.</summary>
        public static TResult SyncGet<T, TArg, TResult>(this SyncList<T> list, Func<List<T>, TArg, TResult> func, TArg arg)
        {
            lock (list.SyncRoot) return func(list, arg);
        }
    }
}

namespace TradingHub.Indicators.Drawing
{
    /// <summary>Default platform colors.</summary>
    /// <summary>Default platform colors (the standard names).</summary>
    public static class DefaultColors
    {
        public static Color Black { get; } = Color.Black;
        public static Color White { get; } = Color.White;
        public static Color Gray { get; } = Color.Gray;
        public static Color DarkGray { get; } = Color.DarkGray;
        public static Color LightGray { get; } = Color.LightGray;
        public static Color Silver { get; } = Color.Silver;
        public static Color Red { get; } = Color.Red;
        public static Color DarkRed { get; } = Color.DarkRed;
        public static Color Maroon { get; } = Color.Maroon;
        public static Color Crimson { get; } = Color.Crimson;
        public static Color Tomato { get; } = Color.Tomato;
        public static Color Coral { get; } = Color.Coral;
        public static Color Orange { get; } = Color.Orange;
        public static Color DarkOrange { get; } = Color.DarkOrange;
        public static Color Gold { get; } = Color.Gold;
        public static Color Yellow { get; } = Color.Yellow;
        public static Color Khaki { get; } = Color.Khaki;
        public static Color Olive { get; } = Color.Olive;
        public static Color YellowGreen { get; } = Color.YellowGreen;
        public static Color Lime { get; } = Color.Lime;
        public static Color LimeGreen { get; } = Color.LimeGreen;
        public static Color Green { get; } = Color.Green;
        public static Color DarkGreen { get; } = Color.DarkGreen;
        public static Color SeaGreen { get; } = Color.SeaGreen;
        public static Color Teal { get; } = Color.Teal;
        public static Color Aqua { get; } = Color.Aqua;
        public static Color Cyan { get; } = Color.Cyan;
        public static Color SkyBlue { get; } = Color.SkyBlue;
        public static Color LightBlue { get; } = Color.LightBlue;
        public static Color SteelBlue { get; } = Color.SteelBlue;
        public static Color DodgerBlue { get; } = Color.DodgerBlue;
        public static Color Blue { get; } = Color.Blue;
        public static Color DarkBlue { get; } = Color.DarkBlue;
        public static Color Navy { get; } = Color.Navy;
        public static Color Indigo { get; } = Color.Indigo;
        public static Color Purple { get; } = Color.Purple;
        public static Color Violet { get; } = Color.Violet;
        public static Color Magenta { get; } = Color.Magenta;
        public static Color Fuchsia { get; } = Color.Fuchsia;
        public static Color Pink { get; } = Color.Pink;
        public static Color HotPink { get; } = Color.HotPink;
        public static Color Brown { get; } = Color.Brown;
        public static Color Chocolate { get; } = Color.Chocolate;
        public static Color Transparent { get; } = Color.Transparent;
    }
}

namespace TradingHub.Rendering.Tools
{
    /// <summary>Predefined pens.</summary>
    public static class RenderPens
    {
        public static RenderPen Black { get; } = new(Color.Black);
        public static RenderPen White { get; } = new(Color.White);
        public static RenderPen Blue { get; } = new(Color.Blue);
        public static RenderPen Red { get; } = new(Color.Red);
        public static RenderPen Green { get; } = new(Color.Green);
        public static RenderPen Gray { get; } = new(Color.Gray);
        public static RenderPen Yellow { get; } = new(Color.Yellow);
        public static RenderPen Orange { get; } = new(Color.Orange);
        public static RenderPen Transparent { get; } = new(Color.Transparent);
    }
}
