using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Charting;

namespace TradingHub.App.Windows;

/// <summary>
/// Settings of a chart: colors of the background, the candles, the axes, the price, the footprint and the orders, and what
/// is shown. Each chart has its own (new charts start with the active chart's).
/// </summary>
internal sealed class ChartSettingsWindow : Window
{
    private static readonly uint[] Palette =
    {
        0xFF000000, 0xFF01000C, 0xFF06152A, 0xFF0B1220, 0xFF131722, 0xFF1E222D, 0xFF2A2E39, 0xFF434651, 0xFF787B86, 0xFFB2B5BE, 0xFFFFFFFF,
        0xFF49D149, 0xFF26A69A, 0xFF089981, 0xFF22D3EE, 0xFF1E90FF, 0xFF2962FF, 0xFFB478FF, 0xFFEC4899, 0xFFE84548, 0xFFF23645, 0xFFF97316, 0xFFF2B53A,
    };

    private readonly TradingView _host;
    private readonly ChartTab _chart;
    private readonly ChartModel _model;
    private readonly List<Action> _refreshers = new();

    public ChartSettingsWindow(TradingView host, ChartTab chart)
    {
        _host = host;
        _chart = chart;
        _model = chart.Model!;
        Title = $"Chart settings · {chart.Symbol} {_model.Period.DisplayName}";
        Width = 470;
        Height = 700;
        MinWidth = 420;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg1;

        // sections in tabs (top bar), like ATAS's settings
        var tabs = new TabControl { Margin = new Thickness(14, 4, 14, 0) };
        void Tab(string title, params Control[] rows)
        {
            var v = new StackPanel { Spacing = 10, Margin = new Thickness(6, 14, 6, 18) };
            foreach (var r in rows) v.Children.Add(r);
            tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = v } });
        }

        Tab("Background",
            ColorRow("Background (top)", nameof(ChartTheme.BackgroundTop)),
            ColorRow("Background (bottom)", nameof(ChartTheme.BackgroundBottom)),
            SmallButton("Flat background (bottom same as top)", () => Set(nameof(ChartTheme.BackgroundBottom), Get(nameof(ChartTheme.BackgroundTop)))),
            ColorRow("Grid", nameof(ChartTheme.Grid)),
            Toggle("Show the grid", () => _model.Settings.ShowGrid, v => _model.Settings.ShowGrid = v),
            ColorRow("Panel separator", nameof(ChartTheme.PanelSeparator)));
        Tab("Candles",
            ColorRow("Bullish", nameof(ChartTheme.UpCandle)),
            ColorRow("Bearish", nameof(ChartTheme.DownCandle)),
            ColorRow("Bullish border", nameof(ChartTheme.UpCandleBorder)),
            ColorRow("Bearish border", nameof(ChartTheme.DownCandleBorder)),
            ColorRow("Wick", nameof(ChartTheme.Wick)));
        Tab("Axes",
            ColorRow("Axis background", nameof(ChartTheme.AxisBackground)),
            ColorRow("Axis text", nameof(ChartTheme.AxisText)),
            ColorRow("Axis lines", nameof(ChartTheme.AxisLine)),
            ColorRow("Text", nameof(ChartTheme.Text)),
            ColorRow("Secondary text", nameof(ChartTheme.TextDim)),
            Toggle("Candle data at the top (O, H, L, C…)", () => _model.Settings.ShowInfoPanel, v => _model.Settings.ShowInfoPanel = v));
        Tab("Price",
            ColorRow("Last price (up)", nameof(ChartTheme.LastPriceUp)),
            ColorRow("Last price (down)", nameof(ChartTheme.LastPriceDown)),
            ColorRow("Last price line", nameof(ChartTheme.LastPriceLine)),
            Toggle("Show the last price line", () => _model.Settings.ShowLastPriceLine, v => _model.Settings.ShowLastPriceLine = v),
            Toggle("Countdown to the end of the candle (under the last price)", () => _model.Settings.ShowCountdown, v => _model.Settings.ShowCountdown = v),
            Toggle("Keep the price in the middle of the chart", () => _model.Settings.CenterOnPrice, v => _model.Settings.CenterOnPrice = v),
            ColorRow("Cursor (crosshair)", nameof(ChartTheme.Crosshair)),
            ColorRow("Crosshair label", nameof(ChartTheme.CrosshairLabelBack)),
            ColorRow("Session start", nameof(ChartTheme.NewSession)),
            Toggle("Mark each session's start", () => _model.Settings.ShowSessionLines, v => _model.Settings.ShowSessionLines = v));
        Tab("Footprint",
            ColorRow("Bid (sellers)", nameof(ChartTheme.Bid)),
            ColorRow("Ask (buyers)", nameof(ChartTheme.Ask)),
            ColorRow("Cells", nameof(ChartTheme.ClusterBase)),
            ColorRow("Cell border", nameof(ChartTheme.CellBorder)),
            ColorRow("Cell text", nameof(ChartTheme.ClusterText)),
            ColorRow("POC text in the cells", nameof(ChartTheme.ClusterTextStrong)),
            ColorRow("Ladder text", nameof(ChartTheme.LadderText)),
            ColorRow("Ladder: positive highlight", nameof(ChartTheme.LadderPositive)),
            ColorRow("Ladder: negative highlight", nameof(ChartTheme.LadderNegative)),
            ColorRow("POC", nameof(ChartTheme.Poc)),
            ColorRow("Value area", nameof(ChartTheme.ValueArea)),
            ColorRow("Buying imbalance", nameof(ChartTheme.ImbalanceAsk)),
            ColorRow("Selling imbalance", nameof(ChartTheme.ImbalanceBid)),
            ColorRow("Positive delta", nameof(ChartTheme.DeltaPositiveText)),
            ColorRow("Negative delta", nameof(ChartTheme.DeltaNegativeText)),
            ColorRow("Volume profile", nameof(ChartTheme.ProfileBar)),
            Note("These are the colors for the whole chart. Each footprint column (delta, volume, bid or ask ladder, cells or profile) can also have its own, text included, in the Footprint editor."));

        // thickness and dash of the entry -> exit line (your own trades and the strategies')
        var width = new Slider { Minimum = 0.5, Maximum = 6, Value = _model.Settings.TradeLineWidth, TickFrequency = 0.5, IsSnapToTickEnabled = true, Width = 180 };
        var widthText = new TextBlock { Text = $"{_model.Settings.TradeLineWidth:0.#} px", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
        width.ValueChanged += (_, _) =>
        {
            _model.Settings.TradeLineWidth = width.Value;
            widthText.Text = $"{width.Value:0.#} px";
            _model.Invalidate();
            Workspace.Save(_host);
        };
        var style = new ComboBox { Width = 180 };
        foreach (var (value, label) in new[] { (TradeLineStyle.Dashed, "Dashed"), (TradeLineStyle.Solid, "Solid"), (TradeLineStyle.Dotted, "Dotted") })
            style.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        style.SelectedItem = style.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (TradeLineStyle)i.Tag! == _model.Settings.TradeLineStyle);
        style.SelectionChanged += (_, _) =>
        {
            if (style.SelectedItem is not ComboBoxItem { Tag: TradeLineStyle v }) return;
            _model.Settings.TradeLineStyle = v;
            _model.Invalidate();
            Workspace.Save(_host);
        };
        _refreshers.Add(() =>
        {
            width.Value = _model.Settings.TradeLineWidth;
            style.SelectedItem = style.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (TradeLineStyle)i.Tag! == _model.Settings.TradeLineStyle);
        });
        Control Row(string label, Control control)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }

        Tab("Trades",
            Ui.H3("Entry-to-exit lines"),
            ColorRow("Winning trade", nameof(ChartTheme.TradeWin)),
            ColorRow("Losing trade", nameof(ChartTheme.TradeLoss)),
            Row("Thickness", Ui.H(10, width, widthText)),
            Row("Dash", style),
            Toggle("Mark my trades (executions and entry to exit)", () => _model.Settings.ShowTradeMarks, v => _model.Settings.ShowTradeMarks = v),
            Note("The lines of strategies placed on the chart use the same colors, thickness and dash."),
            Ui.H3("Orders and position"),
            ColorRow("Buy order", nameof(ChartTheme.BuyOrder)),
            ColorRow("Sell order", nameof(ChartTheme.SellOrder)),
            ColorRow("Long position / buys", nameof(ChartTheme.PositionLong)),
            ColorRow("Short position / sells", nameof(ChartTheme.PositionShort)));

        var head = Ui.V(4, Ui.H2("Chart settings"), Note($"{chart.Symbol} · {_model.Period.DisplayName}. The changes show instantly and are stored on this chart."));
        head.Margin = new Thickness(20, 16, 20, 4);
        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        body.Children.Add(tabs);

        var reset = Ui.Button("Reset all", MaterialIconKind.Restore, () =>
        {
            _model.Settings.Colors = null;
            Changed();
        });
        ToolTip.SetTip(reset, "It goes back to the original colors on this chart");
        var toAll = Ui.Button("Apply to every chart", MaterialIconKind.ContentCopy, () =>
        {
            int n = _host.ApplyChartLook(_chart);
            Title = $"Chart settings · applied to {n} chart{(n == 1 ?"" : "s")}";
        });
        ToolTip.SetTip(toAll, "It copies these colors and what is shown to every open chart");
        var close = Ui.Button("Done", MaterialIconKind.Check, Close, "primary");
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(20, 10, 20, 16) };
        foreach (var b in new Control[] { reset, toAll, close }) actions.Children.Add(b);

        var root = new DockPanel();
        var actionsBorder = new Border { Child = actions, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(actionsBorder, Dock.Bottom);
        root.Children.Add(actionsBorder);
        root.Children.Add(body);
        Content = root;
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Close();
        };
    }

    private static TextBlock Note(string text) => new() { Text = text, Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    private static Control Section(string title, params Control[] rows)
    {
        var v = new StackPanel { Spacing = 9 };
        v.Children.Add(Ui.H3(title));
        foreach (var r in rows) v.Children.Add(r);
        return v;
    }

    private static Button SmallButton(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2), MinHeight = 24, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => onClick();
        return b;
    }

    private uint Get(string property) =>
        _model.Settings.Colors != null && _model.Settings.Colors.TryGetValue(property, out var c) ? c : ChartTheme.DefaultOf(property);

    private void Set(string property, uint? argb)
    {
        var colors = _model.Settings.Colors ??= new Dictionary<string, uint>();
        if (argb is not { } v || v == ChartTheme.DefaultOf(property)) colors.Remove(property);
        else colors[property] = v;
        if (colors.Count == 0) _model.Settings.Colors = null;
        Changed();
    }

    private void Changed()
    {
        _model.ApplyColors();
        Workspace.Save(_host);
        foreach (var r in _refreshers) r();
    }

    private Control Toggle(string label, Func<bool> get, Action<bool> set)
    {
        var cb = new CheckBox { Content = label, IsChecked = get() };
        cb.IsCheckedChanged += (_, _) =>
        {
            set(cb.IsChecked == true);
            _model.Invalidate();
            Workspace.Save(_host);
        };
        _refreshers.Add(() => cb.IsChecked = get());
        return cb;
    }

    /// <summary>Row of a color: it shows the current one and clicking it opens the palette, the code and "reset".</summary>
    private Control ColorRow(string label, string property)
    {
        var swatch = new Border { Width = 34, Height = 18, CornerRadius = new CornerRadius(4), BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
        var code = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Text2, FontFamily = new FontFamily("Menlo") };
        var changed = new TextBlock { Text = "•", Foreground = Ui.Accent, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Bold };
        ToolTip.SetTip(changed, "Changed on this chart");
        var hex = new TextBox { Width = 110, Watermark = "#RRGGBB o #RRGGBBAA", FontSize = 12 };
        var opacity = new Slider { Minimum = 0, Maximum = 255, Width = 150, TickFrequency = 5 };
        var opacityText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Text2, Width = 38 };
        var palette = new WrapPanel { ItemSpacing = 4, LineSpacing = 4, MaxWidth = 290 };
        var flyout = new Flyout();
        bool syncing = false;

        void Refresh()
        {
            uint c = Get(property);
            swatch.Background = new SolidColorBrush(Color.FromUInt32(c));
            code.Text = $"#{c & 0xFFFFFF:X6}" + ((c >> 24) != 0xFF ? $" · {(c >> 24) * 100 / 255}%" : "");
            changed.IsVisible = _model.Settings.Colors?.ContainsKey(property) == true;
            syncing = true;
            hex.Text = (c >> 24) == 0xFF ? $"#{c & 0xFFFFFF:X6}" : $"#{c & 0xFFFFFF:X6}{c >> 24:X2}";
            opacity.Value = c >> 24;
            opacityText.Text = $"{(c >> 24) * 100 / 255}%";
            syncing = false;
        }

        foreach (var p in Palette)
        {
            uint color = p;
            var b = new Button { Content = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromUInt32(color)), BorderBrush = Ui.Line, BorderThickness = new Thickness(1) }, Padding = new Thickness(1), MinHeight = 0 };
            b.Click += (_, _) =>
            {
                // it keeps the transparency it had (the grid, for example, is almost transparent)
                uint alpha = Get(property) & 0xFF000000;
                Set(property, (color & 0x00FFFFFF) | (alpha == 0 ? 0xFF000000 : alpha));
            };
            palette.Children.Add(b);
        }

        void ApplyHex()
        {
            var t = (hex.Text ?? "").Trim().TrimStart('#');
            if (t.Length is 6 or 8 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                Set(property, t.Length == 6 ? 0xFF000000 | v : (v >> 8) | ((v & 0xFF) << 24));
            else Refresh();
        }

        hex.LostFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) ApplyHex();
        };
        opacity.ValueChanged += (_, _) =>
        {
            if (syncing) return;
            uint c = Get(property);
            Set(property, (c & 0x00FFFFFF) | ((uint)Math.Round(opacity.Value) << 24));
        };
        var reset = SmallButton("Reset", () => Set(property, null));
        flyout.Content = Ui.V(10, Ui.H3(label), palette, Ui.H(8, new TextBlock { Text = "Code", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, hex),
            Ui.H(8, new TextBlock { Text = "Opacity", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center }, opacity, opacityText), reset).Also(v => v.Margin = new Thickness(12));

        var button = new Button { Content = Ui.H(8, swatch, code), Padding = new Thickness(6, 3), MinHeight = 26, Flyout = flyout, HorizontalAlignment = HorizontalAlignment.Left };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*,Auto") };
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
        Grid.SetColumn(button, 1);
        g.Children.Add(button);
        Grid.SetColumn(changed, 2);
        g.Children.Add(changed);
        _refreshers.Add(Refresh);
        Refresh();
        return g;
    }
}
