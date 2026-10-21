using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace TradingHub.Common
{
    /// <summary>Cross-platform pen (equivalent to System.Drawing.Pen without GDI+).</summary>
    public class UniversalPen
    {
        public UniversalPen(Color color, float width = 1f)
        {
            Color = color;
            Width = width;
        }

        public UniversalPen(Color color, float width, DashStyle dashStyle) : this(color, width)
        {
            DashStyle = dashStyle;
        }

        public Color Color { get; set; }
        public float Width { get; set; }
        public DashStyle DashStyle { get; set; } = DashStyle.Solid;
        public float[] DashPattern { get; set; }

        public UniversalPen Clone() => new(Color, Width, DashStyle) { DashPattern = DashPattern };

        public TradingHub.Rendering.Tools.RenderPen ToRenderPen() => new(Color, Width, DashStyle);

        public static implicit operator TradingHub.Rendering.Tools.RenderPen(UniversalPen p) => p?.ToRenderPen();
    }

    /// <summary>Cross-platform solid brushes.</summary>
    public class UniversalSolidBrush
    {
        public UniversalSolidBrush(Color color)
        {
            Color = color;
        }

        public Color Color { get; set; }

        public UniversalSolidBrush Clone() => new(Color);
    }

    public static class UniversalPens
    {
        public static UniversalPen Black => new(Color.Black);
        public static UniversalPen White => new(Color.White);
        public static UniversalPen Gray => new(Color.Gray);
        public static UniversalPen Red => new(Color.Red);
        public static UniversalPen Green => new(Color.Green);
        public static UniversalPen Blue => new(Color.Blue);
        public static UniversalPen Transparent => new(Color.Transparent);
    }

    public static class ColorUtils
    {
        /// <summary>Parses "#RRGGBB" or "#AARRGGBB".</summary>
        public static Color ParseHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return Color.Empty;
            hex = hex.Trim().TrimStart('#');
            uint v = Convert.ToUInt32(hex, 16);
            return hex.Length <= 6 ? Color.FromArgb((int)(0xFF000000 | v)) : Color.FromArgb((int)v);
        }
    }
}

namespace TradingHub.Common.Logging
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
    }

    public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message, Exception Exception);

    /// <summary>Central platform log (the UI subscribes to show it).</summary>
    public static class Log
    {
        private static readonly ConcurrentQueue<LogEntry> Recent = new();
        public static event Action<LogEntry> Entry;

        public static void Write(LogLevel level, object source, string message, Exception ex = null)
        {
            var e = new LogEntry(DateTime.Now, level, source?.GetType().Name ?? "", message, ex);
            Recent.Enqueue(e);
            while (Recent.Count > 2000) Recent.TryDequeue(out _);
            if (level >= LogLevel.Warning)
                Console.Error.WriteLine($"[{e.Time:HH:mm:ss}] {level} {e.Source}: {message}{(ex != null ? " — " + ex.Message : "")}");
            Entry?.Invoke(e);
        }

        public static IReadOnlyCollection<LogEntry> RecentEntries => Recent.ToArray();
    }

    public static class LoggingExtensions
    {
        public static void LogError(this object source, string message, Exception ex = null) => Log.Write(LogLevel.Error, source, message, ex);
        public static void LogError(this object source, Exception ex) => Log.Write(LogLevel.Error, source, ex?.Message, ex);
        public static void LogWarn(this object source, string message, Exception ex = null) => Log.Write(LogLevel.Warning, source, message, ex);
        public static void LogInfo(this object source, string message) => Log.Write(LogLevel.Info, source, message);
        public static void LogDebug(this object source, string message) => Log.Write(LogLevel.Debug, source, message);

        public static void LogDebug(this object source, string message, Exception ex) => Log.Write(LogLevel.Debug, source, message, ex);
    }
}

namespace TradingHub.Common.Collections
{
    /// <summary>List with a maximum capacity (it discards the oldest ones).</summary>
    public class LimitedList<T> : List<T>
    {
        public LimitedList(int capacity)
        {
            MaxCount = capacity;
        }

        public int MaxCount { get; set; }

        public new void Add(T item)
        {
            base.Add(item);
            while (Count > MaxCount) RemoveAt(0);
        }
    }
}
