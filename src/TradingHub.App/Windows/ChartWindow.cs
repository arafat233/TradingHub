using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Material.Icons;
using TradingHub.App.Views;

namespace TradingHub.App.Windows;

/// <summary>
/// Chart in its own window (as in ATAS): it can be moved to another monitor. It has its own toolbar; the
/// drawing tools and the Chart Trader travel with the chart frame. "Back to the main
/// window" returns it to a tab.
/// </summary>
internal sealed class ChartWindow : Window
{
    private readonly TradingView _host;
    private readonly DockPanel _content = new();

    public ChartWindow(TradingView host, ChartTab chart)
    {
        _host = host;
        Chart = chart;
        chart.Window = this;
        Width = 1180;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        Background = Ui.Bg0;
        Icon = AppIcon.Window;

        var dock = Ui.IconButton(MaterialIconKind.DockWindow, "Back to the main window", () => _host.DockBack(this));
        Toolbar = new ChartToolbar(host, true, dock) { Chart = chart };
        DockPanel.SetDock(Toolbar, Dock.Top);
        _content.Children.Add(Toolbar);

        // the chart frame moves as it is (with no header: the window already has a title)
        chart.Header.IsVisible = false;
        chart.Frame.BorderThickness = new Thickness(1);
        chart.Frame.BorderBrush = Ui.Line;
        chart.Frame.Margin = new Thickness(6);
        if (chart.Frame.Parent is Panel old) old.Children.Remove(chart.Frame);
        _content.Children.Add(chart.Frame);
        Content = _content;
        UpdateTitle();

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || App.Services == null) return;
            if (e.Key == Avalonia.Input.Key.Escape && e.KeyModifiers == Avalonia.Input.KeyModifiers.None && Chart.Model?.FocusTrade != null)
            {
                _host.ClearFocusTrade(Chart);
                e.Handled = true;
                return;
            }

            if (TradingHub.App.Services.Hotkeys.Match(App.Services.Settings, e, this) is { } action && _host.ExecuteHotkey(action, Chart)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, KeyModifiers.Meta), Command = new Relay(Close) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.I, KeyModifiers.Meta), Command = new Relay(() => _host.OpenIndicators(Chart)) });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.K, KeyModifiers.Meta), Command = new Relay(() => Toolbar.Picker.Open()) });
    }

    public ChartTab Chart { get; }
    public ChartToolbar Toolbar { get; }

    /// <summary>True if the chart went back to the main window (it is not discarded on close).</summary>
    public bool Released { get; private set; }

    public void UpdateTitle() => Title = $"Trading Hub — {Chart.Caption}";

    /// <summary>Releases the chart frame so it can go back to a tab.</summary>
    public void ReleaseChart()
    {
        Released = true;
        _content.Children.Remove(Chart.Frame);
    }

    /// <summary>Simple command for keyboard shortcuts.</summary>
    private sealed class Relay : System.Windows.Input.ICommand
    {
        private readonly Action _run;
        public Relay(Action run) => _run = run;
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _run();
    }
}
