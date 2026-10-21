using System.Drawing;
using System.Drawing.Drawing2D;
using SkiaSharp;

namespace TradingHub.Rendering.Tools
{
    /// <summary>Font for drawing from indicators. The size is in points (as in GDI+: 9pt ~ 12px).</summary>
    public class RenderFont
    {
        public RenderFont(string fontFamily, float size, FontStyle style = FontStyle.Regular)
        {
            FontFamily = fontFamily;
            Size = size;
            Style = style;
        }

        public RenderFont(string fontFamily, float size, FontStyle style, GraphicsUnit unit, byte gdiCharSet)
            : this(fontFamily, size, style, unit)
        {
        }

        public RenderFont(string fontFamily, float size, FontStyle style, GraphicsUnit unit)
            : this(fontFamily, size, style)
        {
        }

        public string FontFamily { get; }
        public float Size { get; }
        public FontStyle Style { get; }
        public bool Bold => (Style & FontStyle.Bold) != 0;
        public bool Italic => (Style & FontStyle.Italic) != 0;
        public bool Underline => (Style & FontStyle.Underline) != 0;
        public bool Strikeout => (Style & FontStyle.Strikeout) != 0;

        /// <summary>Size in logical pixels.</summary>
        public float PixelSize => Size * 96f / 72f;

        public override string ToString() => $"{FontFamily} {Size}";
    }

    /// <summary>Pen for lines and borders.</summary>
    public class RenderPen
    {
        public RenderPen(Color color, float width = 1f, DashStyle dashStyle = DashStyle.Solid)
        {
            Color = color;
            Width = width;
            DashStyle = dashStyle;
        }

        public RenderPen(Color color, float width, DashStyle dashStyle, float[] dashPattern)
            : this(color, width, dashStyle)
        {
            DashPattern = dashPattern;
        }

        public Color Color { get; set; }
        public float Width { get; set; }
        public DashStyle DashStyle { get; set; }
        public float[] DashPattern { get; set; }

        public LineCap StartCap { get; set; }
        public LineCap EndCap { get; set; }

        public RenderPen Clone() => new(Color, Width, DashStyle) { DashPattern = DashPattern };
    }

    /// <summary>Text format (horizontal and vertical alignment inside a rectangle).</summary>
    public class RenderStringFormat
    {
        public StringAlignment Alignment { get; set; } = StringAlignment.Near;
        public StringAlignment LineAlignment { get; set; } = StringAlignment.Near;
        public StringTrimming Trimming { get; set; } = StringTrimming.None;
        public StringFormatFlags FormatFlags { get; set; }

        public static RenderStringFormat GenericDefault => new();
        public static RenderStringFormat GenericTypographic => new();
    }
}

namespace TradingHub.Rendering.Context
{
    /// <summary>Drawing layers an indicator can paint on.</summary>
    [Flags]
    public enum DrawingLayouts
    {
        None = 0,
        Historical = 1,
        LatestBar = 2,
        Final = 4,
    }

    public enum RenderTextRenderingHint
    {
        SystemDefault,
        SingleBitPerPixelGridFit,
        SingleBitPerPixel,
        AntiAliasGridFit,
        AntiAlias,
        ClearTypeGridFit,
        Aliased = SingleBitPerPixel,
    }
}

namespace TradingHub.Rendering.Control
{
    [Flags]
    public enum RenderControlMouseButtons
    {
        None = 0,
        Left = 1,
        Right = 2,
        Middle = 4,
    }

    [Flags]
    public enum RenderControlKeyModifiers
    {
        None = 0,
        Shift = 1,
        Control = 2,
        Alt = 4,
        Command = 8,
    }

    /// <summary>Mouse event over the chart (coordinates in chart pixels).</summary>
    public class RenderControlMouseEventArgs : EventArgs
    {
        public RenderControlMouseEventArgs(int x, int y, RenderControlMouseButtons button, int clicks = 1, int delta = 0)
        {
            X = x;
            Y = y;
            Button = button;
            Clicks = clicks;
            Delta = delta;
        }

        public int X { get; }
        public int Y { get; }
        public Point Location => new(X, Y);
        public RenderControlMouseButtons Button { get; }
        public int Clicks { get; }
        public int Delta { get; }
        public RenderControlKeyModifiers Modifiers { get; init; }
        public bool Handled { get; set; }
    }
}

namespace TradingHub.Rendering
{
    /// <summary>Standard cursors an indicator can ask for.</summary>
    public enum StdCursor
    {
        Arrow,
        Cross,
        Hand,
        IBeam,
        SizeAll,
        SizeNS,
        SizeWE,
        SizeNESW,
        SizeNWSE,
        No,
        Wait,
        UpArrow,
        None,
    }

    internal static class SkiaConvert
    {
        public static SKColor ToSk(this Color c) => new(c.R, c.G, c.B, c.A);
        public static SKRect ToSk(this Rectangle r) => new(r.Left, r.Top, r.Right, r.Bottom);
        public static SKRect ToSk(this RectangleF r) => new(r.Left, r.Top, r.Right, r.Bottom);
    }
}
