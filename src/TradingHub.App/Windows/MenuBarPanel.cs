using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;

namespace TradingHub.App.Windows;

/// <summary>
/// The panel that opens from the Trading Hub icon in the menu bar: live strategies (state, position and result),
/// open positions of the accounts, in real time. It opens Trading Hub or quits for good.
/// </summary>
internal sealed class MenuBarPanel : Window
{
    private static readonly CultureInfo Fmt = CultureInfo.GetCultureInfo("en-US");
    private readonly AppServices _svc;
    private readonly MainWindow _main;
    private readonly TextBlock _status = new() { Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _strategies = new() { Spacing = 8 };
    private readonly StackPanel _positions = new() { Spacing = 6 };
    private readonly TextBlock _strategiesTitle = new() { Classes = { "h3" } };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public MenuBarPanel(AppServices svc, MainWindow main, Action openMain)
    {
        _svc = svc;
        _main = main;
        Title = TradingHub.Common.Product.Name;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 680;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var logo = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = new Image { Source = AppIcon.Bitmap, Width = 30, Height = 30 } };
        var open = Ui.Button("Open Trading Hub", MaterialIconKind.OpenInNew, openMain, "primary");
        var head = new DockPanel();
        DockPanel.SetDock(open, Dock.Right);
        open.Margin = new Thickness(10, 0, 0, 0);
        open.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(open);
        DockPanel.SetDock(logo, Dock.Left);
        logo.Margin = new Thickness(0, 0, 10, 0);
        logo.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(logo);
        head.Children.Add(Ui.V(1, new TextBlock { Text = TradingHub.Common.Product.Name, FontWeight = FontWeight.SemiBold, FontSize = 14 }, _status));

        var quit = Ui.Button("Quit Trading Hub", MaterialIconKind.Power, MenuBar.Quit, "ghost");
        ToolTip.SetTip(quit, "It closes Trading Hub completely: it halts the live strategies and the agents (the ones that were running resume on open, if Settings says so)");
        var foot = new DockPanel();
        DockPanel.SetDock(quit, Dock.Right);
        foot.Children.Add(quit);
        foot.Children.Add(new TextBlock { Text = "Closing the window leaves Trading Hub running up here.", Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(Section(_strategiesTitle, _strategies));
        body.Children.Add(Section(Ui.H3("Open positions"), _positions));

        var root = new DockPanel { Margin = new Thickness(16, 14, 16, 12) };
        DockPanel.SetDock(head, Dock.Top);
        head.Margin = new Thickness(0, 0, 0, 12);
        DockPanel.SetDock(foot, Dock.Bottom);
        foot.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(head);
        root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = body, MaxHeight = 540, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = new Border
        {
            Child = root,
            Background = Ui.Bg1,
            BorderBrush = Ui.Brush("LineStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
        };

        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                if (IsVisible) _timer.Start();
                else _timer.Stop();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape) Hide();
        };
    }

    private static Control Section(Control title, Control content)
    {
        var line = new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 0, 0, 10) };
        return Ui.V(8, line, title, content);
    }

    private static TextBlock Empty(string text) => new() { Text = text, Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Fmt);

    private static IBrush MoneyBrush(decimal v) => v > 0 ? Ui.Up : v < 0 ? Ui.Down : Ui.Text1;

    private static string Side(decimal position, string avg, string symbol) =>
        position == 0 ? "flat" : $"{(position > 0 ? "long" : "short")} {Math.Abs(position).ToString("0.####", Fmt)} {symbol} @ {avg}";

    /// <summary>Reads everything again (once per second while the panel is open).</summary>
    public void Refresh()
    {
        try
        {
            RefreshStrategies();
            RefreshPositions();
            var (day, balance) = _svc.AccountCenter.SimSummary();
            _status.Text = $"{_main.ConnectionSummary} · simulated {Money(day)} today · {balance.ToString("N2", Fmt)}";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private void RefreshStrategies()
    {
        var running = _svc.Live.Entries(includeStopped: false).Where(e => e.Running).ToList();
        _strategiesTitle.Text = $"LIVE STRATEGIES ({running.Count})";
        _strategies.Children.Clear();
        if (running.Count == 0)
        {
            _strategies.Children.Add(Empty("There are no live strategies running."));
            return;
        }

        foreach (var e in running.OrderBy(e => e.Locked).ThenBy(e => e.Info.Strategy))
        {
            var r = e.Runner!;
            var inst = r.Data.Instrument;
            var state = Ui.Pill(e.StateLabel, e.Paused || r.DailyLimitActive ? Ui.Warn : Ui.Up, out _);
            var name = new TextBlock { Text = e.Info.Strategy, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var top = new DockPanel();
            DockPanel.SetDock(state, Dock.Right);
            top.Children.Add(state);
            top.Children.Add(name);
            var plan = _svc.Live.PlanOf(e);
            var where = new TextBlock { Text = $"{inst.Symbol} · {e.Info.Account} · {e.SourceLabel}{(e.Info.IsReal ? " · REAL" : "")}", Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
            // how many contracts and of which one each order sends (the micro, highlighted)
            var contract = new TextBlock
            {
                Text = plan.Units <= 0 ? "Size at zero: it opens no trades" : $"Each order: {plan.Text}{(plan.Detail.Length > 0 ? $" ({plan.Detail})" : "")}",
                FontSize = 11.5,
                Foreground = plan.Micro ? Ui.Accent : Ui.Text1,
                FontWeight = plan.Micro ? FontWeight.SemiBold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var pos = new TextBlock { Text = Side(e.Ledger.Position, inst.FormatPrice(e.Ledger.AveragePrice), e.Ledger.OpenSymbol ?? plan.Symbol), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var pnl = new TextBlock { Text = $"open {Money(e.OpenPnL)} · today {Money(r.TodayPnL)}", FontSize = 12, Foreground = MoneyBrush(e.OpenPnL + r.TodayPnL), VerticalAlignment = VerticalAlignment.Center };
            var bottom = new DockPanel();
            DockPanel.SetDock(pnl, Dock.Right);
            bottom.Children.Add(pnl);
            bottom.Children.Add(pos);
            _strategies.Children.Add(new Border
            {
                Child = Ui.V(3, top, where, contract, bottom),
                Background = Ui.Bg2,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8),
            });
        }
    }

    private void RefreshPositions()
    {
        _positions.Children.Clear();
        int count = 0;
        foreach (var a in _svc.AccountCenter.Accounts())
        {
            AccountSnapshot snap;
            try
            {
                snap = _svc.AccountCenter.Snapshot(a);
            }
            catch
            {
                continue;
            }

            foreach (var p in snap.Positions.Where(p => p.Net != 0))
            {
                count++;
                var left = new TextBlock { Text = $"{p.Symbol} · {a.Name}{(a.IsReal ? " · REAL" : "")}", FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var right = new TextBlock
                {
                    Text = $"{Side(p.Net, p.AvgPrice.ToString("#,##0.####", Fmt), p.Symbol)} · {Money(p.OpenPnl)}",
                    FontSize = 12,
                    Foreground = MoneyBrush(p.OpenPnl),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var row = new DockPanel();
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                row.Children.Add(left);
                _positions.Children.Add(row);
            }
        }

        if (count == 0) _positions.Children.Add(Empty("No open positions."));
    }

}
