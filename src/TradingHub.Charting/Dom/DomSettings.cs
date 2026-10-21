using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingHub.Charting.Dom;

public enum DomColumn
{
    [Display(Name = "Buy orders")] BuyOrders,
    [Display(Name = "Bid (book)")] Bid,
    [Display(Name = "Price")] Price,
    [Display(Name = "Ask (book)")] Ask,
    [Display(Name = "Sell orders")] SellOrders,
    [Display(Name = "Session volume")] Volume,
    [Display(Name = "Session delta")] Delta,
    [Display(Name = "Traded at the bid")] TradedBid,
    [Display(Name = "Traded at the ask")] TradedAsk,
}

/// <summary>A DOM column: whether it is visible and how much relative width it takes.</summary>
public sealed class DomColumnSetting
{
    public DomColumn Column { get; set; }
    public bool Visible { get; set; } = true;
    public double Width { get; set; } = 1;
}

/// <summary>Everything configurable about the DOM.</summary>
public sealed class DomSettings
{
    [Browsable(false)]
    public List<DomColumnSetting> Columns { get; set; } = DefaultColumns();

    [Display(Name = "Row height (px)", GroupName = "Size", Order = 10)]
    [Range(12, 40)]
    public int RowHeight { get; set; } = 18;

    [Display(Name = "Font size", GroupName = "Size", Order = 20)]
    [Range(8, 20)]
    public int FontSize { get; set; } = 12;

    [Display(Name = "Show headers", GroupName = "Size", Order = 30)]
    public bool ShowHeader { get; set; } = true;

    [Display(Name = "Center on the price automatically", GroupName = "Behavior", Order = 10)]
    public bool AutoCenter { get; set; } = true;

    [Display(Name = "Double click to trade", GroupName = "Behavior", Order = 20, Description = "It avoids sending orders from an accidental click.")]
    public bool DoubleClickToTrade { get; set; }

    [Display(Name = "Highlight sizes from", GroupName = "Behavior", Order = 30, Description = "Book sizes equal or larger are highlighted (0 = no).")]
    public decimal LargeSize { get; set; }

    [Display(Name = "Book bars", GroupName = "Behavior", Order = 40, Description = "It draws bars proportional to each level's size.")]
    public bool BookBars { get; set; } = true;

    [Display(Name = "Bid", GroupName = "Colors", Order = 10)]
    public Color BidColor { get; set; } = Color.FromArgb(255, 30, 110, 200);

    [Display(Name = "Ask", GroupName = "Colors", Order = 20)]
    public Color AskColor { get; set; } = Color.FromArgb(255, 200, 50, 60);

    [Display(Name = "Background below the price", GroupName = "Colors", Order = 30)]
    public Color BidBackground { get; set; } = Color.FromArgb(255, 10, 22, 40);

    [Display(Name = "Background above the price", GroupName = "Colors", Order = 40)]
    public Color AskBackground { get; set; } = Color.FromArgb(255, 40, 14, 18);

    [Display(Name = "Background between bid and ask", GroupName = "Colors", Order = 50)]
    public Color SpreadBackground { get; set; } = Color.FromArgb(255, 16, 18, 26);

    [Display(Name = "Last price", GroupName = "Colors", Order = 60)]
    public Color LastPriceColor { get; set; } = Color.FromArgb(255, 242, 181, 58);

    [Display(Name = "Buy volume", GroupName = "Colors", Order = 70)]
    public Color BuyVolumeColor { get; set; } = Color.FromArgb(150, 38, 194, 129);

    [Display(Name = "Sell volume", GroupName = "Colors", Order = 80)]
    public Color SellVolumeColor { get; set; } = Color.FromArgb(150, 240, 82, 90);

    [Display(Name = "Price text", GroupName = "Colors", Order = 90)]
    public Color PriceText { get; set; } = Color.FromArgb(255, 160, 175, 200);

    public static List<DomColumnSetting> DefaultColumns() => new()
    {
        new() { Column = DomColumn.BuyOrders, Width = 0.55 },
        new() { Column = DomColumn.Bid, Width = 1 },
        new() { Column = DomColumn.Price, Width = 1.05 },
        new() { Column = DomColumn.Ask, Width = 1 },
        new() { Column = DomColumn.SellOrders, Width = 0.55 },
        new() { Column = DomColumn.Volume, Width = 1.25 },
        new() { Column = DomColumn.Delta, Width = 0.9, Visible = false },
        new() { Column = DomColumn.TradedBid, Width = 0.9, Visible = false },
        new() { Column = DomColumn.TradedAsk, Width = 0.9, Visible = false },
    };

    /// <summary>Fills in missing columns (configurations stored by earlier versions).</summary>
    public void Normalize()
    {
        foreach (var d in DefaultColumns())
            if (Columns.All(c => c.Column != d.Column)) Columns.Add(d);
        Columns = Columns.GroupBy(c => c.Column).Select(g => g.First()).ToList();
        if (!Columns.Any(c => c.Visible)) Columns.First(c => c.Column == DomColumn.Price).Visible = true;
    }

    public DomSettings Clone()
    {
        var c = (DomSettings)MemberwiseClone();
        c.Columns = Columns.Select(x => new DomColumnSetting { Column = x.Column, Visible = x.Visible, Width = x.Width }).ToList();
        return c;
    }
}

/// <summary>Stores colors as #AARRGGBB in JSON.</summary>
public sealed class ColorJsonConverter : JsonConverter<Color>
{
    public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString() ?? "";
            if (s.StartsWith('#') && uint.TryParse(s[1..], System.Globalization.NumberStyles.HexNumber, null, out var argb)) return Color.FromArgb((int)argb);
        }
        else if (reader.TokenType == JsonTokenType.Number)
        {
            return Color.FromArgb(reader.GetInt32());
        }
        else if (reader.TokenType == JsonTokenType.StartObject)
        {
            // old format: it is ignored
            using var _ = JsonDocument.ParseValue(ref reader);
        }

        return Color.Empty;
    }

    public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"#{(uint)value.ToArgb():X8}");
}
