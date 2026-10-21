using System.Globalization;
using SkiaSharp;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Rendering.Context;

namespace TradingHub.Charting.Dom;

/// <summary>Trade tape (Time &amp; Sales) with per-order aggregation and a size filter.</summary>
public sealed class TapeModel
{
    public TapeModel(InstrumentData data) => Data = data;

    public InstrumentData Data { get; }

    /// <summary>Add up the fills of the same aggressive order (same millisecond and side).</summary>
    public bool Aggregate { get; set; } = true;

    /// <summary>Minimum volume to show.</summary>
    public decimal MinVolume { get; set; }

    /// <summary>From this volume up the row is highlighted.</summary>
    public decimal HighlightVolume { get; set; }

    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    /// <summary>
    /// Scrolled back: index of the newest trade shown (null = live, the latest at the top).
    /// While it is scrolled it does not move even if new trades arrive.
    /// </summary>
    public int? Anchor { get; set; }

    /// <summary>New trades since it was scrolled (for the notice at the top).</summary>
    public int NewSinceAnchor => Anchor is { } a ? Math.Max(0, Data.Trades.Count - 1 - a) : 0;
}

public sealed class TapeRenderer
{
    private readonly SKPaint _fill = new() { IsAntialias = false };
    private readonly SKPaint _text = new() { IsAntialias = true };
    private const float RowH = 16, HeaderH = 20;

    private ChartTheme? _theme;

    /// <summary>Colors (by default, the base of the app's dark/light mode).</summary>
    public ChartTheme Theme
    {
        get => _theme ?? ChartTheme.Base;
        set => _theme = value;
    }

    private readonly record struct Row(DateTime Time, decimal Price, decimal Volume, TradeSide Side, int Count);

    public void Render(SKCanvas canvas, TapeModel tape, float width, float height)
    {
        var t = Theme;
        var inst = tape.Data.Instrument;
        _fill.Color = t.BackgroundTop;
        canvas.DrawRect(0, 0, width, height, _fill);
        _fill.Color = t.AxisBackground;
        canvas.DrawRect(0, 0, width, HeaderH, _fill);
        var font = RenderContext.GetFont(t.MonoFontFamily, 11, false, false);
        var bold = RenderContext.GetFont(t.MonoFontFamily, 11, true, false);
        var head = RenderContext.GetFont(t.FontFamily, 10, false, false);
        float c1 = 6, c2 = width * 0.36f, c3 = width - 6;
        _text.Color = t.TextDim;
        canvas.DrawText("Time", c1, 14, head, _text);
        canvas.DrawText("Price", c2, 14, head, _text);
        canvas.DrawText("Volume", c3 - RenderContext.MeasureWidth(head, "Volume"), 14, head, _text);

        int maxRows = (int)((height - HeaderH) / RowH) + 1;
        if (tape.Anchor is { } anchor && anchor >= tape.Data.Trades.Count - 1) tape.Anchor = null;
        var rows = Collect(tape, maxRows);
        decimal hl = tape.HighlightVolume > 0 ? tape.HighlightVolume : AutoHighlight(rows);
        int dec = Rendering.FootprintRenderer.ChooseDecimals(rows.Count > 0 ? rows.Max(r => r.Volume) : 0, inst.VolumeDecimals);
        float baseOff = RowH / 2 - font.Metrics.Ascent / 2 - font.Metrics.Descent / 2;

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            float y = HeaderH + i * RowH;
            bool buy = r.Side == TradeSide.Buy;
            bool big = r.Volume >= hl && hl > 0;
            var col = buy ? t.Ask : t.Bid;
            _fill.Color = big ? col.WithAlpha(150) : col.WithAlpha(i % 2 == 0 ? (byte)22 : (byte)14);
            canvas.DrawRect(0, y, width, RowH, _fill);
            var f = big ? bold : font;
            _text.Color = big ? SKColors.White : col;
            var local = TimeZoneInfo.ConvertTimeFromUtc(r.Time, tape.TimeZone);
            canvas.DrawText(local.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), c1, y + baseOff, f, _text);
            canvas.DrawText(inst.FormatPrice(r.Price), c2, y + baseOff, f, _text);
            string v = VolumeFormat.Compact(r.Volume, dec);
            canvas.DrawText(v, c3 - RenderContext.MeasureWidth(f, v), y + baseOff, f, _text);
        }

        // scrolled: the header says so and a click there goes back to the latest
        if (tape.Anchor != null)
        {
            _fill.Color = t.AxisBackground;
            canvas.DrawRect(0, 0, width, HeaderH, _fill);
            string msg = tape.NewSinceAnchor > 0 ? $"▲ {tape.NewSinceAnchor:N0} new · click to go back" : "▲ click to go back to the latest";
            _text.Color = t.LastPriceUp;
            canvas.DrawText(msg, width / 2 - RenderContext.MeasureWidth(head, msg) / 2, 14, head, _text);
        }
    }

    /// <summary>Header height (a click there goes back to the latest).</summary>
    public static float HeaderHeight => HeaderH;

    /// <summary>Scrolls the tape: positive rows = backwards (older), negative = towards the latest.</summary>
    public static void Scroll(TapeModel tape, int rows)
    {
        var s = tape.Data.Trades;
        if (s.Count == 0 || rows == 0) return;
        int i = tape.Anchor ?? s.Count - 1;
        if (rows > 0)
        {
            for (int k = 0; k < rows && i > 0; k++)
            {
                int j = RowStart(tape, i) - 1; // the newest trade of the next row (older)
                if (j < 0) break;
                i = j;
            }

            tape.Anchor = i;
        }
        else
        {
            for (int k = 0; k < -rows && i < s.Count - 1; k++) i = RowEndForward(tape, i + 1);
            tape.Anchor = i >= s.Count - 1 ? null : i;
        }
    }

    /// <summary>First trade (the oldest) of the row whose newest trade is <paramref name="i"/>.</summary>
    private static int RowStart(TapeModel tape, int i)
    {
        if (!tape.Aggregate) return i;
        var s = tape.Data.Trades;
        long ms = s.GetTimeTicks(i) / TimeSpan.TicksPerMillisecond;
        var side = s.GetSide(i);
        int j = i;
        while (j > 0 && s.GetSide(j - 1) == side && s.GetTimeTicks(j - 1) / TimeSpan.TicksPerMillisecond == ms) j--;
        return j;
    }

    /// <summary>Newest trade of the row starting at <paramref name="i"/> (going towards the latest).</summary>
    private static int RowEndForward(TapeModel tape, int i)
    {
        var s = tape.Data.Trades;
        if (!tape.Aggregate) return i;
        long ms = s.GetTimeTicks(i) / TimeSpan.TicksPerMillisecond;
        var side = s.GetSide(i);
        int j = i;
        while (j + 1 < s.Count && s.GetSide(j + 1) == side && s.GetTimeTicks(j + 1) / TimeSpan.TicksPerMillisecond == ms) j++;
        return j;
    }

    private static decimal AutoHighlight(List<Row> rows)
    {
        if (rows.Count < 20) return 0;
        var vols = rows.Select(r => r.Volume).OrderBy(v => v).ToArray();
        return vols[(int)(vols.Length * 0.95)];
    }

    private static List<Row> Collect(TapeModel tape, int max)
    {
        var res = new List<Row>(max);
        var s = tape.Data.Trades;
        int i = Math.Min(tape.Anchor ?? s.Count - 1, s.Count - 1);
        int scanned = 0;
        while (i >= 0 && res.Count < max && scanned < 200_000)
        {
            long t = s.GetTimeTicks(i);
            var side = s.GetSide(i);
            decimal vol = s.GetVolume(i);
            decimal price = s.GetPrice(i);
            int count = 1;
            int j = i - 1;
            if (tape.Aggregate)
            {
                long ms = t / TimeSpan.TicksPerMillisecond;
                while (j >= 0 && s.GetSide(j) == side && s.GetTimeTicks(j) / TimeSpan.TicksPerMillisecond == ms)
                {
                    vol += s.GetVolume(j);
                    count++;
                    j--;
                }
            }

            scanned += count;
            if (vol >= tape.MinVolume) res.Add(new Row(new DateTime(t, DateTimeKind.Utc), price, vol, side, count));
            i = j;
        }

        return res;
    }
}
