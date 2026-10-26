using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TradingHub.Data;

namespace TradingHub.App.Windows;

/// <summary>
/// Open interest by strike, calls to the right in green and puts to the left in red, with each strike's gamma exposure (GEX) written beside it and the
/// price, max pain and gamma flip marked. It is positioning from Deribit's public data under a convention (dealers long the calls, short the puts),
/// not a forecast.
/// </summary>
internal sealed class GammaWindow : Window
{
    public GammaWindow(string currency, IReadOnlyList<StrikeRow> rows, OptionLevelSet set)
    {
        Title = $"{currency} options: open interest by strike";
        Width = 560;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        decimal max = rows.Count == 0 ? 1 : Math.Max(1, rows.Max(r => Math.Max(r.CallOi, r.PutOi)));
        double maxGex = rows.Count == 0 ? 1 : Math.Max(1e-9, rows.Max(r => Math.Abs(r.Gex)));
        var list = new StackPanel { Spacing = 1 };
        foreach (var r in rows.OrderByDescending(r => r.Strike))
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,86,*,70"), Height = 18 };
            var put = new Border { Background = Ui.Down, HorizontalAlignment = HorizontalAlignment.Right, Width = Math.Max(1, (double)(r.PutOi / max) * 200), Height = 12 };
            var call = new Border { Background = Ui.Up, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(1, (double)(r.CallOi / max) * 200), Height = 12 };
            var strike = new TextBlock { Text = r.Strike.ToString("0.####"), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily(Services.Plat.Mono) };
            bool near = set.Spot > 0 && Math.Abs(r.Strike - set.Spot) / set.Spot < 0.004m;
            if (near) strike.FontWeight = FontWeight.Bold;
            string tag = r.Strike == set.MaxPain ? " MP" : r.Strike == set.CallWall ? " CW" : r.Strike == set.PutWall ? " PW" : "";
            var gex = new TextBlock
            {
                Text = $"{(r.Gex >= 0 ? "+" : "−")}{Math.Abs(r.Gex) / maxGex * 100:0}{tag}",
                FontSize = 11,
                Foreground = r.Gex >= 0 ? Ui.Up : Ui.Down,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(strike, 1);
            Grid.SetColumn(call, 2);
            Grid.SetColumn(gex, 3);
            grid.Children.Add(put);
            grid.Children.Add(strike);
            grid.Children.Add(call);
            grid.Children.Add(gex);
            if (near) grid.Background = Ui.Bg3;
            list.Children.Add(grid);
        }

        string flip = set.GammaFlip is { } f ? $"gamma flip {f:0.####}" : "no gamma flip within range";
        var head = Ui.V(4,
            Ui.H3($"{currency} options, next 45 days"),
            new TextBlock { Text = $"price {set.Spot:0.####} · call wall {Fmt(set.CallWall)} · put wall {Fmt(set.PutWall)} · max pain {Fmt(set.MaxPain)} · {flip}", FontSize = 12, TextWrapping = TextWrapping.Wrap },
            Ui.Faint("Red = put open interest, green = call open interest, per strike. The number on the right is the strike's gamma exposure as a share of the largest (+ damps moves, − feeds them), assuming dealers are long the calls and short the puts. MP = max pain, CW = call wall, PW = put wall. Context, not a signal."));
        foreach (var t in head.Children.OfType<TextBlock>()) t.TextWrapping = TextWrapping.Wrap;
        head.Margin = new Thickness(16, 12, 16, 8);
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer { Content = new Border { Child = list, Padding = new Thickness(16, 0, 16, 16) } });
        Content = dock;
    }

    private static string Fmt(decimal? v) => v is { } x ? x.ToString("0.####") : "–";
}
