using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;

namespace TradingHub.App.Views;

/// <summary>
/// Link groups by color (as in TradingView/ATAS): the charts with the same color share
/// instrument, period and crosshair (each one can be switched off).
/// </summary>
internal static class ChartLinks
{
    public static readonly (int Id, string Name, Color Color)[] Groups =
    {
        (1, "Red", Color.FromRgb(240, 82, 90)),
        (2, "Green", Color.FromRgb(38, 194, 129)),
        (3, "Blue", Color.FromRgb(59, 130, 246)),
        (4, "Yellow", Color.FromRgb(234, 179, 8)),
        (5, "Purple", Color.FromRgb(168, 85, 247)),
        (6, "Orange", Color.FromRgb(249, 115, 22)),
    };

    public static Color? ColorOf(int group) => Groups.FirstOrDefault(g => g.Id == group) is { Id: > 0 } g ? g.Color : null;

    public static string NameOf(int group) => Groups.FirstOrDefault(g => g.Id == group).Name ?? "Not linked";

    /// <summary>Little color square for menus and buttons.</summary>
    public static Control Swatch(Color? color, double size = 11) => new Border
    {
        Width = size,
        Height = size,
        CornerRadius = new CornerRadius(3),
        Background = color is { } c ? new SolidColorBrush(c) : Brushes.Transparent,
        BorderBrush = color is null ? Ui.Text2 : null,
        BorderThickness = color is null ? new Thickness(1.2) : new Thickness(0),
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>
/// A chart's link color button: it goes in the corner of the chart's little bar and in the
/// toolbar. It opens a menu with the colors and what is synchronized.
/// </summary>
internal sealed class LinkButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly Func<ChartTab?> _chart;
    private readonly TradingView _host;
    private readonly Border _square = new() { Width = 11, Height = 11, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
    private readonly Material.Icons.Avalonia.MaterialIcon _chain = Ui.Icon(MaterialIconKind.LinkVariant, 14, Ui.Text2);

    public LinkButton(TradingView host, Func<ChartTab?> chart)
    {
        _host = host;
        _chart = chart;
        Classes.Add("icon");
        Width = 22;
        Height = 22;
        MinHeight = 0;
        Padding = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Center;
        Content = new Panel { Children = { _chain, _square } };
        Click += (_, e) =>
        {
            ShowMenu();
            e.Handled = true;
        };
        Refresh();
    }

    public void Refresh()
    {
        int g = _chart()?.LinkGroup ?? 0;
        var color = ChartLinks.ColorOf(g);
        _square.IsVisible = color != null;
        _chain.IsVisible = color == null;
        if (color is { } c) _square.Background = new SolidColorBrush(c);
        ToolTip.SetTip(this, color == null
            ? "Link with other charts by color (instrument, period and crosshair)"
            : $"Linked to the {ChartLinks.NameOf(g).ToLowerInvariant()} group: click to change");
    }

    private void ShowMenu()
    {
        var chart = _chart();
        if (chart == null) return;
        var s = _host.Services.Settings;
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = "Link with other charts", IsEnabled = false });

        void Add(int id, string name, Color? color)
        {
            int n = _host.ChartsInGroup(id).Count(c => !ReferenceEquals(c, chart));
            string header = id == 0 ? name : n > 0 ? $"{name}  ·  {n} chart{(n > 1 ? "s" : "")}" : name;
            var item = new MenuItem
            {
                Header = header + (chart.LinkGroup == id ? "   ✓" : ""),
                Icon = ChartLinks.Swatch(color),
            };
            item.Click += (_, _) => _host.SetLinkGroup(chart, id);
            menu.Items.Add(item);
        }

        Add(0, "Not linked", null);
        foreach (var (id, name, color) in ChartLinks.Groups) Add(id, name, color);
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "What gets synced in the group", IsEnabled = false });

        void Toggle(string text, Func<bool> get, Action<bool> set)
        {
            var item = new MenuItem { Header = text, Icon = get() ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null };
            item.Click += (_, _) =>
            {
                set(!get());
                s.Save();
            };
            menu.Items.Add(item);
        }

        Toggle("Instrument", () => s.LinkSymbol, v => s.LinkSymbol = v);
        Toggle("Period", () => s.LinkPeriod, v => s.LinkPeriod = v);
        Toggle("Crosshair", () => s.LinkCrosshair, v => s.LinkCrosshair = v);
        menu.ShowAt(this);
    }
}
