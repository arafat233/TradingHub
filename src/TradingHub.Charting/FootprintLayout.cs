using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingHub.Attributes;

namespace TradingHub.Charting;

/// <summary>Kind of piece (column) inside each footprint candle.</summary>
public enum FootprintColumnKind
{
    [Display(Name = "Thin candle")] Candle,
    [Display(Name = "Colored cells")] Cells,
    [Display(Name = "Ladder (numbers)")] Ladder,
    [Display(Name = "Profile (histogram)")] Profile,
    [Display(Name = "Spacer")] Spacer,
}

/// <summary>Data a column shows.</summary>
public enum FootprintValue
{
    [Display(Name = "Volume")] Volume,
    [Display(Name = "Delta")] Delta,
    [Display(Name = "Bid (aggressive sells)")] Bid,
    [Display(Name = "Ask (aggressive buys)")] Ask,
    [Display(Name = "Bid x Ask")] BidAsk,
    [Display(Name = "Trades")] Trades,
}

/// <summary>Which way a profile's bars grow.</summary>
public enum ProfileDirection
{
    [Display(Name = "Left to right")] LeftToRight,
    [Display(Name = "Right to left")] RightToLeft,
    [Display(Name = "From the center (delta / bid-ask)")] Centered,
}

/// <summary>Which levels are highlighted.</summary>
public enum HighlightRule
{
    [Display(Name = "None")] None,
    [Display(Name = "Strong delta at the level")] StrongDelta,
    [Display(Name = "Diagonal imbalances")] Imbalance,
    [Display(Name = "The candle's POC")] Poc,
    [Display(Name = "High volume (over the POC's %)")] HighVolume,
}

public enum ColumnTextAlign
{
    [Display(Name = "Left")] Left,
    [Display(Name = "Center")] Center,
    [Display(Name = "Right")] Right,
}

/// <summary>A column (piece) of the footprint.</summary>
public sealed class FootprintColumn
{
    [Display(Name = "Type")]
    public FootprintColumnKind Kind { get; set; } = FootprintColumnKind.Cells;

    [Display(Name = "Data")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public FootprintValue Value { get; set; } = FootprintValue.BidAsk;

    /// <summary>Relative width (the finite candles have a fixed width in pixels).</summary>
    [Display(Name = "Width", Description = "Relative to the candle's other columns")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile, FootprintColumnKind.Spacer)]
    public double Width { get; set; } = 1;

    [Display(Name = "Bar direction")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Profile)]
    public ProfileDirection Direction { get; set; } = ProfileDirection.LeftToRight;

    [Display(Name = "Highlight")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public HighlightRule Highlight { get; set; } = HighlightRule.None;

    [Display(Name = "Cell colors")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells)]
    public ClusterColorScheme ColorScheme { get; set; } = ClusterColorScheme.VolumeProportion;

    [Display(Name = "Text alignment")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Ladder)]
    public ColumnTextAlign Align { get; set; } = ColumnTextAlign.Right;

    /// <summary>On profiles, write the value over the bar.</summary>
    [Display(Name = "Numbers over the bars")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Profile)]
    public bool ShowNumbers { get; set; }

    /// <summary>Its own colors (ARGB). Null = the theme's.</summary>
    [Display(Name = "Positive color", GroupName = "Background colors", Description = "Background or bar for the ask, the positive delta and anything highlighted in favour. Automatic = the theme's.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? PositiveColor { get; set; }

    [Display(Name = "Negative color", GroupName = "Background colors", Description = "Background or bar for the bid, the negative delta and anything highlighted against. Automatic = the theme's.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? NegativeColor { get; set; }

    [Display(Name = "Neutral color", GroupName = "Background colors", Description = "Background or bar for the volume and anything highlighted with no side. Automatic = the theme's.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? NeutralColor { get; set; }

    /// <summary>Color of the numbers (null = the theme's). The ones below, if present, override this one.</summary>
    [Display(Name = "Text color", GroupName = "Text colors", Description = "Every number in the column, except the ones with their own color (delta, bid, ask). Automatic = the theme's.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    public uint? TextColor { get; set; }

    [Display(Name = "Positive delta text", GroupName = "Text colors")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Delta)]
    public uint? PositiveTextColor { get; set; }

    [Display(Name = "Negative delta text", GroupName = "Text colors")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Delta)]
    public uint? NegativeTextColor { get; set; }

    [Display(Name = "Bid text", GroupName = "Text colors", Description = "Marked imbalances keep their color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Bid, FootprintValue.BidAsk)]
    public uint? BidTextColor { get; set; }

    [Display(Name = "Ask text", GroupName = "Text colors", Description = "Marked imbalances keep their color.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Cells, FootprintColumnKind.Ladder, FootprintColumnKind.Profile)]
    [VisibleWhen(nameof(Value), FootprintValue.Ask, FootprintValue.BidAsk)]
    public uint? AskTextColor { get; set; }

    [Display(Name = "Text on highlighted levels", GroupName = "Text colors", Description = "Automatic = white.")]
    [VisibleWhen(nameof(Kind), FootprintColumnKind.Ladder)]
    [VisibleWhen(nameof(Highlight), HighlightRule.StrongDelta, HighlightRule.Imbalance, HighlightRule.Poc, HighlightRule.HighVolume)]
    public uint? HighlightTextColor { get; set; }

    public FootprintColumn Clone() => (FootprintColumn)MemberwiseClone();

    public override string ToString() => Kind switch
    {
        FootprintColumnKind.Candle => "Candle",
        FootprintColumnKind.Spacer => "Spacer",
        _ => $"{Kind} · {Value}",
    };
}

/// <summary>
/// How each footprint candle is built: a list of columns from left to right.
/// It is stored as a JSON template.
/// </summary>
public sealed class FootprintLayout
{
    public string Name { get; set; } = "Custom";
    public List<FootprintColumn> Columns { get; set; } = new();

    /// <summary>Candle delta above and total volume below.</summary>
    [Display(Name = "Each candle's totals", Description = "The candle's delta on top and the total volume below")]
    public bool ShowTotals { get; set; }

    /// <summary>Box on each candle's POC (on the cell columns).</summary>
    [Display(Name = "A box at the POC", Description = "On the cell columns")]
    public bool ShowPoc { get; set; } = true;

    /// <summary>Highlight imbalances in the bid x ask text.</summary>
    [Display(Name = "Mark imbalances", Description = "On the bid x ask text (diagonal)")]
    public bool ShowImbalances { get; set; } = true;

    [Display(Name = "Imbalance: ratio", Description = "How many times larger one side has to be than the other (diagonally)")]
    public decimal ImbalanceRatio { get; set; } = 3m;

    [Display(Name = "Imbalance: minimum volume")]
    public decimal ImbalanceMinVolume { get; set; }

    /// <summary>A bar along the candle where several rows in a row are imbalanced the same way: a push, easier to see than one number.</summary>
    [Display(Name = "Mark stacked imbalances", Description = "A bar beside the candle where several rows in a row are imbalanced: green = buyers, red = sellers, on the candle's right edge")]
    public bool ShowStackedImbalances { get; set; } = true;

    [Display(Name = "Stacked imbalance: rows in a row", Description = "How many consecutive imbalanced rows make a stack")]
    public int StackedImbalanceLength { get; set; } = 3;

    /// <summary>Two lines at the bottom of the chart for each candle: delta as a % of its volume, volume against the visible average, and where its POC sits.</summary>
    [Display(Name = "Summary under each candle", Description = "Delta as a % of the candle's volume (colored), its volume against the visible average (1.5x = half as much again) and a triangle: POC in the upper or lower third, a diamond in the middle")]
    public bool ShowSummary { get; set; } = true;

    /// <summary>When the rows are small, only the numbers that matter are drawn (the big levels, the POC and the imbalanced ones).</summary>
    [Display(Name = "Hide small numbers when zoomed out", Description = "With small rows only the large levels, the POC and imbalances keep their numbers; the colors still show every level")]
    public bool AutoDeclutter { get; set; } = true;

    /// <summary>Numbers of levels with less volume than this fraction of the candle's POC are not drawn (0 = all). The POC and imbalanced levels always keep theirs.</summary>
    [Display(Name = "Hide numbers below", Description = "A fraction of the candle's POC volume (0.1 = 10%); 0 shows every number. The POC and imbalanced levels are always shown")]
    public decimal HideNumbersBelow { get; set; }

    /// <summary>For "strong delta": |delta| / level volume (0.45 ~ 72/28).</summary>
    [Display(Name = "Strong delta from", Description = "|delta| / the level's volume (0.45 ≈ 72% on one side)")]
    public decimal StrongDeltaRatio { get; set; } = 0.45m;

    /// <summary>Minimum level volume (fraction of the POC) to highlight.</summary>
    [Display(Name = "Minimum volume to highlight", Description = "A fraction of the POC's volume (0.3 = 30%)")]
    public decimal HighlightMinVolumeFraction { get; set; } = 0.3m;

    /// <summary>Colors proportional to the candle's maximum (true) or to everything visible.</summary>
    [Display(Name = "Colors proportional to each candle", Description = "Off: against the maximum of everything visible")]
    public bool ProportionPerCandle { get; set; } = true;

    public FootprintLayout Clone()
    {
        var c = (FootprintLayout)MemberwiseClone();
        c.Columns = Columns.Select(x => x.Clone()).ToList();
        return c;
    }

    // ------------------------------------------------------------------ templates

    private static FootprintColumn Candle() => new() { Kind = FootprintColumnKind.Candle };

    public static IReadOnlyList<FootprintLayout> Presets { get; } = new[]
    {
        new FootprintLayout
        {
            Name = "Bid x Ask (classic)",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.BidAsk, ColorScheme = ClusterColorScheme.VolumeProportion } },
        },
        new FootprintLayout
        {
            Name = "Volume ladder + profile",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Volume, Highlight = HighlightRule.StrongDelta, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, Highlight = HighlightRule.StrongDelta, Direction = ProfileDirection.LeftToRight, Width = 1.3 },
            },
        },
        new FootprintLayout
        {
            Name = "Delta ladder + volume profile",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Delta, Highlight = HighlightRule.StrongDelta, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, Direction = ProfileDirection.LeftToRight, Width = 1.3 },
            },
        },
        new FootprintLayout
        {
            Name = "Double ladder (bid | ask)",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Bid, Highlight = HighlightRule.Imbalance, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Ask, Highlight = HighlightRule.Imbalance, Align = ColumnTextAlign.Left, Width = 1 },
            },
        },
        new FootprintLayout
        {
            Name = "Double delta ladder",
            ShowTotals = true,
            Columns =
            {
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Delta, Highlight = HighlightRule.StrongDelta, Align = ColumnTextAlign.Right, Width = 1 },
                Candle(),
                new() { Kind = FootprintColumnKind.Ladder, Value = FootprintValue.Volume, Highlight = HighlightRule.HighVolume, Align = ColumnTextAlign.Left, Width = 1 },
            },
        },
        new FootprintLayout
        {
            Name = "Volume (cells)",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.Volume } },
        },
        new FootprintLayout
        {
            Name = "Delta (cells)",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.Delta, ColorScheme = ClusterColorScheme.Delta } },
        },
        new FootprintLayout
        {
            Name = "Bid / ask histogram",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.BidAsk, Direction = ProfileDirection.Centered, ShowNumbers = true } },
        },
        new FootprintLayout
        {
            Name = "Per-candle volume profile",
            Columns = { Candle(), new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, ShowNumbers = true } },
        },
        new FootprintLayout
        {
            Name = "Bid x Ask + profile",
            Columns =
            {
                Candle(),
                new() { Kind = FootprintColumnKind.Cells, Value = FootprintValue.BidAsk, Width = 1.4 },
                new() { Kind = FootprintColumnKind.Profile, Value = FootprintValue.Volume, Width = 0.8 },
            },
        },
    };

    // ------------------------------------------------------------------ persistence

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static FootprintLayout? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize<FootprintLayout>(json, Json); }
        catch { return null; }
    }

    /// <summary>User templates folder (with TRADINGHUB_DATA_DIR, the tests' one).</summary>
    public static string TemplatesFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } dir ? dir : TradingHub.Common.Product.AppData, "FootprintTemplates");

    public static List<FootprintLayout> LoadUserTemplates()
    {
        var list = new List<FootprintLayout>();
        if (!Directory.Exists(TemplatesFolder)) return list;
        foreach (var f in Directory.EnumerateFiles(TemplatesFolder, "*.json"))
        {
            var l = FromJson(File.ReadAllText(f));
            if (l != null) list.Add(l);
        }

        return list;
    }

    public void SaveAsTemplate()
    {
        Directory.CreateDirectory(TemplatesFolder);
        var safe = string.Concat(Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        File.WriteAllText(Path.Combine(TemplatesFolder, safe + ".json"), ToJson());
    }
}
