using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace TradingHub.App.Services;

/// <summary>
/// Dark (black) and light (white) mode for the whole platform. The colors in Styles/TradingHubTheme.axaml are named
/// brushes: their color is changed in place here, so everything already on screen updates instantly without
/// restarting. The charts take their base (background, axes, texts) from <see cref="TradingHub.Charting.ChartTheme.Base"/>.
/// </summary>
public static class AppTheme
{
    public const string Dark = "dark", Light = "light";

    /// <summary>Older settings stored the mode in Spanish; it is still accepted.</summary>
    private const string LegacyLight = "claro";

    private sealed record Palette(
        Color Bg0, Color Bg1, Color Bg2, Color Bg3, Color Bg4, Color Input, Color Line, Color LineStrong,
        Color Text0, Color Text1, Color Text2, Color Accent, Color AccentHover, Color AccentText, Color AccentInk, Color AccentPressed,
        Color Focus, Color Up, Color Down, Color Warn, Color Pressed, Color BorderHover, Color ToolTip,
        Color Sel1, Color Sel2, Color Sel3, Color List1, Color List2, Color List3, Color Scroll1, Color Scroll2,
        Color DangerBg, Color DangerFg, Color DangerBorder, Color DangerHoverBg, Color DangerHoverFg,
        Color Buy, Color BuyHover, Color Sell, Color SellHover,
        Color BuySoftBg, Color BuySoftFg, Color BuySoftBorder, Color BuySoftHover,
        Color SellSoftBg, Color SellSoftFg, Color SellSoftBorder, Color SellSoftHover, Color SoftHoverFg);

    private static Color C(string hex) => Color.Parse(hex);

    private static Color A(Color c, byte alpha) => new(alpha, c.R, c.G, c.B);

    /// <summary>Black: neutral backgrounds (no blue), white and grey texts, amber accent.</summary>
    private static readonly Palette DarkPalette = new(
        C("#000000"), C("#0A0A0A"), C("#111111"), C("#1A1A1A"), C("#242424"), C("#0C0C0C"), C("#1E1E1E"), C("#2E2E2E"),
        C("#EDEDED"), C("#A8A8A8"), C("#727272"), C("#F2B53A"), C("#FFC757"), C("#1A1206"), C("#F2B53A"), C("#E0A52F"),
        C("#BDBDBD"), C("#26C281"), C("#F0525A"), C("#F5A524"), C("#2C2C2C"), C("#3C3C3C"), C("#1C1C1C"),
        C("#1FFFFFFF"), C("#2BFFFFFF"), C("#38FFFFFF"), C("#1E1E1E"), C("#262626"), C("#303030"), C("#33FFFFFF"), C("#55FFFFFF"),
        C("#2A1214"), C("#FF8A90"), C("#5A2228"), C("#3A1A1F"), C("#FFB0B4"),
        C("#14935F"), C("#18AB6F"), C("#C9353D"), C("#E0434B"),
        C("#0F2E22"), C("#8FE3BC"), C("#1E5E44"), C("#17503A"),
        C("#331417"), C("#FFB0B4"), C("#64252B"), C("#55202A"), C("#FFFFFF"));

    /// <summary>White: light backgrounds, almost black texts, a darker amber accent so it reads on white.</summary>
    private static readonly Palette LightPalette = new(
        C("#F3F3F4"), C("#FAFAFA"), C("#FFFFFF"), C("#EFEFF1"), C("#E4E4E7"), C("#FFFFFF"), C("#E3E3E6"), C("#D2D2D7"),
        C("#111111"), C("#4A4A4F"), C("#8A8A90"), C("#E8A21C"), C("#F0B030"), C("#1A1206"), C("#9A6400"), C("#D49012"),
        C("#5A5A60"), C("#0F9D63"), C("#D93A42"), C("#B86E00"), C("#D9D9DD"), C("#B9B9BF"), C("#FFFFFF"),
        C("#14000000"), C("#1F000000"), C("#2A000000"), C("#EDEDEF"), C("#E4E4E7"), C("#DADADE"), C("#33000000"), C("#55000000"),
        C("#FDECEC"), C("#C62828"), C("#F2C2C2"), C("#FAD8D8"), C("#B71C1C"),
        C("#12A066"), C("#0E8A57"), C("#D93A42"), C("#C0313A"),
        C("#E5F6EE"), C("#0E7A4B"), C("#B6E3CC"), C("#D0EFDF"),
        C("#FDECEE"), C("#B3262E"), C("#F1C1C6"), C("#F8D9DC"), C("#111111"));

    public static string Current { get; private set; } = Dark;

    public static bool IsLight => Current == Light;

    /// <summary>The mode changed (the app refreshes charts, DOM and whatever it draws with Skia).</summary>
    public static event Action? Changed;

    public static void Apply(string mode)
    {
        Current = mode is Light or LegacyLight ? Light : Dark;
        var p = IsLight ? LightPalette : DarkPalette;
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = IsLight ? ThemeVariant.Light : ThemeVariant.Dark;

        var brushes = new (string Key, Color Color)[]
        {
            ("Bg0Brush", p.Bg0), ("Bg1Brush", p.Bg1), ("Bg2Brush", p.Bg2), ("Bg3Brush", p.Bg3), ("Bg4Brush", p.Bg4), ("InputBrush", p.Input),
            ("LineBrush", p.Line), ("LineStrongBrush", p.LineStrong), ("Text0Brush", p.Text0), ("Text1Brush", p.Text1), ("Text2Brush", p.Text2),
            ("AccentBrush", p.Accent), ("AccentHoverBrush", p.AccentHover), ("AccentTextBrush", p.AccentText), ("FocusBrush", p.Focus),
            ("SelectionBrush", p.Sel1), ("UpBrush", p.Up), ("DownBrush", p.Down), ("WarnBrush", p.Warn), ("UpSoftBrush", A(p.Up, 0x22)), ("DownSoftBrush", A(p.Down, 0x22)),
            ("ButtonBackground", p.Bg3), ("ButtonBackgroundPointerOver", p.Bg4), ("ButtonBackgroundPressed", p.Pressed), ("ButtonBackgroundDisabled", p.Bg2),
            ("ButtonForeground", p.Text0), ("ButtonForegroundPointerOver", p.Text0), ("ButtonForegroundPressed", p.Text0), ("ButtonForegroundDisabled", p.Text2),
            ("ButtonBorderBrush", p.LineStrong), ("ButtonBorderBrushPointerOver", p.BorderHover), ("ButtonBorderBrushPressed", p.BorderHover), ("ButtonBorderBrushDisabled", p.Line),
            ("ToggleButtonBackground", p.Bg3), ("ToggleButtonBackgroundPointerOver", p.Bg4), ("ToggleButtonBackgroundPressed", p.Pressed),
            ("ToggleButtonBackgroundChecked", A(p.Accent, 0x33)), ("ToggleButtonBackgroundCheckedPointerOver", A(p.Accent, 0x44)), ("ToggleButtonBackgroundCheckedPressed", A(p.Accent, 0x55)),
            ("ToggleButtonForegroundChecked", p.AccentInk), ("ToggleButtonForegroundCheckedPointerOver", p.AccentInk), ("ToggleButtonForegroundCheckedPressed", p.AccentInk),
            ("ToggleButtonBorderBrush", p.LineStrong), ("ToggleButtonBorderBrushChecked", A(p.Accent, 0x66)), ("ToggleButtonBorderBrushCheckedPointerOver", A(p.Accent, 0x88)),
            ("TextControlBackground", p.Input), ("TextControlBackgroundPointerOver", p.Input), ("TextControlBackgroundFocused", p.Input),
            ("TextControlBorderBrush", p.LineStrong), ("TextControlBorderBrushPointerOver", p.BorderHover), ("TextControlBorderBrushFocused", p.Focus),
            ("TextControlForeground", p.Text0), ("TextControlForegroundFocused", p.Text0), ("TextControlPlaceholderForeground", p.Text2), ("TextControlPlaceholderForegroundFocused", p.Text2),
            ("TextControlSelectionHighlightColor", A(p.Focus, 0x55)),
            ("ComboBoxBackground", p.Bg3), ("ComboBoxBackgroundPointerOver", p.Bg4), ("ComboBoxBackgroundPressed", p.Pressed), ("ComboBoxBackgroundFocused", p.Bg3),
            ("ComboBoxBorderBrush", p.LineStrong), ("ComboBoxBorderBrushPointerOver", p.BorderHover), ("ComboBoxForeground", p.Text0),
            ("ComboBoxDropDownBackground", p.Bg2), ("ComboBoxDropDownBorderBrush", p.LineStrong), ("ComboBoxItemBackgroundPointerOver", p.Bg4),
            ("ComboBoxItemBackgroundSelected", p.Sel1), ("ComboBoxItemBackgroundSelectedPointerOver", p.Sel2), ("ComboBoxDropDownGlyphForeground", p.Text1),
            ("ListBoxItemBackgroundPointerOver", p.Bg3), ("ListBoxItemBackgroundPressed", p.Bg4), ("ListBoxItemBackgroundSelected", p.Sel1),
            ("ListBoxItemBackgroundSelectedPointerOver", p.Sel2), ("ListBoxItemBackgroundSelectedPressed", p.Sel3), ("ListBoxItemForegroundSelected", p.Text0),
            ("SystemControlHighlightListAccentLowBrush", p.List1), ("SystemControlHighlightListAccentMediumBrush", p.List2), ("SystemControlHighlightListAccentHighBrush", p.List3),
            ("SystemControlHighlightListLowBrush", p.Bg3), ("SystemControlHighlightListMediumBrush", p.Bg4),
            ("TabItemHeaderForegroundSelected", p.Text0), ("TabItemHeaderForegroundUnselected", p.Text1), ("TabItemHeaderForegroundUnselectedPointerOver", p.Text0),
            ("TabItemHeaderSelectedPipeFill", p.Accent),
            ("MenuFlyoutPresenterBackground", p.Bg2), ("MenuFlyoutPresenterBorderBrush", p.LineStrong), ("MenuFlyoutItemBackgroundPointerOver", p.Bg4),
            ("MenuFlyoutItemBackgroundPressed", p.Pressed), ("FlyoutPresenterBackground", p.Bg2), ("FlyoutBorderThemeBrush", p.LineStrong),
            ("ToolTipBackground", p.ToolTip), ("ToolTipForeground", p.Text0), ("ToolTipBorderBrush", p.LineStrong),
            ("CheckBoxCheckBackgroundFillChecked", p.Accent), ("CheckBoxCheckBackgroundFillCheckedPointerOver", p.AccentHover), ("CheckBoxCheckBackgroundStrokeChecked", p.Accent),
            ("CheckBoxCheckGlyphForegroundChecked", p.AccentText), ("ToggleSwitchFillOn", p.Accent), ("ToggleSwitchFillOnPointerOver", p.AccentHover), ("ToggleSwitchKnobFillOn", p.AccentText),
            ("ScrollBarThumbFill", p.Scroll1), ("ScrollBarThumbFillPointerOver", p.Scroll2), ("SystemControlForegroundBaseHighBrush", p.Text0),
            ("AccentPressedBrush", p.AccentPressed), ("AccentSoftBrush", A(p.Accent, 0x2E)),
            ("ActiveFrameBrush", A(p.Accent, 0x8C)), ("ActiveHeaderBrush", IsLight ? Color.Parse("#FFFDF6") : Color.Parse("#121008")),
            ("DangerBg", p.DangerBg), ("DangerFg", p.DangerFg), ("DangerBorder", p.DangerBorder), ("DangerHoverBg", p.DangerHoverBg), ("DangerHoverFg", p.DangerHoverFg),
            ("BuyBg", p.Buy), ("BuyHoverBg", p.BuyHover), ("SellBg", p.Sell), ("SellHoverBg", p.SellHover),
            ("BuySoftBg", p.BuySoftBg), ("BuySoftFg", p.BuySoftFg), ("BuySoftBorder", p.BuySoftBorder), ("BuySoftHoverBg", p.BuySoftHover),
            ("SellSoftBg", p.SellSoftBg), ("SellSoftFg", p.SellSoftFg), ("SellSoftBorder", p.SellSoftBorder), ("SellSoftHoverBg", p.SellSoftHover), ("SoftHoverFg", p.SoftHoverFg),
        };
        foreach (var (key, color) in brushes)
            if (app.TryFindResource(key, out var r) && r is SolidColorBrush b) b.Color = color;

        // the loose colors (Ui.Color reads them)
        var colors = new (string Key, Color Color)[]
        {
            ("Bg0", p.Bg0), ("Bg1", p.Bg1), ("Bg2", p.Bg2), ("Bg3", p.Bg3), ("Bg4", p.Bg4), ("InputBg", p.Input), ("Line", p.Line), ("LineStrong", p.LineStrong),
            ("Text0", p.Text0), ("Text1", p.Text1), ("Text2", p.Text2), ("Accent", p.Accent), ("AccentHover", p.AccentHover), ("AccentText", p.AccentText),
            ("Focus", p.Focus), ("Up", p.Up), ("Down", p.Down), ("Warn", p.Warn),
        };
        foreach (var dict in Dictionaries(app.Styles))
            foreach (var (key, color) in colors)
                if (dict.ContainsKey(key)) dict[key] = color;

        TradingHub.Charting.ChartTheme.Base = IsLight ? TradingHub.Charting.ChartTheme.Light : TradingHub.Charting.ChartTheme.Dark;
        Changed?.Invoke();
    }

    private static IEnumerable<IResourceDictionary> Dictionaries(IEnumerable<IStyle> styles)
    {
        foreach (var s in styles)
        {
            if (s is Avalonia.Markup.Xaml.Styling.StyleInclude inc && inc.Loaded is { } loaded)
                foreach (var d in Dictionaries(new[] { loaded })) yield return d;
            if (s is Styles st)
            {
                if (st.Resources is IResourceDictionary rd) yield return rd;
                foreach (var d in Dictionaries(st)) yield return d;
            }
        }
    }
}
