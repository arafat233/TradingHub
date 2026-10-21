using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;

namespace TradingHub.App.Views;

/// <summary>
/// Chart Trader, DOM and tape in separate (floating) windows: they follow the active chart, remember where they were and on
/// closing the window they go back to the right panel.
/// </summary>
public sealed partial class TradingView
{
    private readonly Dictionary<string, Window> _panelWindows = new();
    private readonly Dictionary<string, Button> _detachButtons = new();
    private bool _closingAll;

    internal bool DomIsFloating => _panelWindows.ContainsKey("dom");

    /// <summary>Some section is in its own window (it refreshes even if the main window is on another section).</summary>
    private bool AnyPanelFloating => _panelWindows.Count > 0;

    private static string PanelName(string key) => key switch
    {
        "dom" => "DOM",
        "tape" => "Time and sales tape",
        _ => "Chart Trader",
    };

    private Control? SectionOf(string key) => key switch
    {
        "dom" => _domSection,
        "tape" => _tapeSection,
        _ => _ticketSection,
    };

    private Button BuildDetachButton(string key)
    {
        var b = Ui.IconButton(MaterialIconKind.OpenInNew, $"Move {Article(key)} to a separate window", () =>
        {
            if (_panelWindows.ContainsKey(key)) DockSection(key);
            else DetachSection(key);
        }, 15);
        _detachButtons[key] = b;
        return b;
    }

    private static string Article(string key) => key == "tape" ? "the tape" : key == "dom" ? "the DOM" : "the Chart Trader";

    private void UpdateDetachButton(string key)
    {
        if (!_detachButtons.TryGetValue(key, out var b)) return;
        bool floating = _panelWindows.ContainsKey(key);
        b.Content = Ui.Icon(floating ? MaterialIconKind.DockRight : MaterialIconKind.OpenInNew, 15);
        ToolTip.SetTip(b, floating ? $"Put {Article(key)} back in the right panel" : $"Move {Article(key)} to a separate window");
    }

    /// <summary>Where and at what size a section's window was (the DOM carries the previous version's).</summary>
    private PanelWindowState WindowStateOf(string key)
    {
        var s = _svc.Settings;
        if (s.PanelWindows.TryGetValue(key, out var st)) return st;
        st = key == "dom"
            ? new PanelWindowState { Floating = s.DomFloating, X = s.DomWindowX, Y = s.DomWindowY, Width = s.DomWindowWidth, Height = s.DomWindowHeight }
            : new PanelWindowState { Width = key == "tape" ? 340 : 300, Height = key == "tape" ? 620 : 860 };
        s.PanelWindows[key] = st;
        return st;
    }

    /// <summary>Pulls a section out into a separate window (or brings it to the front if it is already out).</summary>
    internal void DetachSection(string key)
    {
        var section = SectionOf(key);
        if (section == null) return;
        if (_panelWindows.TryGetValue(key, out var open))
        {
            open.Activate();
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var s = _svc.Settings;
        switch (key)
        {
            case "dom": s.ShowDom = true; break;
            case "tape": s.ShowTape = true; break;
            default: s.ShowTicket = true; break;
        }

        if (section.Parent is Panel parent) parent.Children.Remove(section);
        var st = WindowStateOf(key);
        var w = new Window
        {
            Title = PanelTitle(key),
            Width = Math.Clamp(st.Width, 240, 1400),
            Height = Math.Clamp(st.Height, 240, 2400),
            MinWidth = 220,
            MinHeight = 200,
            Background = Ui.Bg1,
            Content = section,
            Icon = TradingHub.App.Windows.AppIcon.Bitmap is { } bmp ? new WindowIcon(bmp) : null,
        };
        var pos = new PixelPoint(st.X, st.Y);
        if (st.X >= 0 && w.Screens.All.Any(sc => sc.WorkingArea.Contains(pos)))
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = pos;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        w.Closing += (_, _) => SaveWindowBounds(key, w);
        w.Closed += (_, _) => DockSection(key);
        _panelWindows[key] = w;
        st.Floating = true;
        if (key == "dom") s.DomFloating = true;
        s.Save();
        UpdateDetachButton(key);
        ApplyPanelVisibility();
        w.Show(owner);
        _status($"{PanelName(key)} in a separate window: it follows the active chart and, when the window closes, it returns to the right panel.");
    }

    private void SaveWindowBounds(string key, Window w)
    {
        var st = WindowStateOf(key);
        st.X = w.Position.X;
        st.Y = w.Position.Y;
        if (w.Width > 100) st.Width = w.Width;
        if (w.Height > 100) st.Height = w.Height;
        _svc.Settings.Save();
    }

    /// <summary>Returns a section to the right panel.</summary>
    internal void DockSection(string key)
    {
        if (!_panelWindows.Remove(key, out var w)) return;
        w.Content = null;
        if (!_closingAll)
        {
            WindowStateOf(key).Floating = false;
            if (key == "dom") _svc.Settings.DomFloating = false;
            _svc.Settings.Save();
        }

        UpdateDetachButton(key);
        ApplyPanelVisibility();
        if (w.IsVisible) w.Close();
    }

    internal Window? PanelWindow(string key) => _panelWindows.GetValueOrDefault(key);

    internal void DetachDom() => DetachSection("dom");

    internal void DockDom() => DockSection("dom");

    private string PanelTitle(string key) => _active != null ? $"{PanelName(key)} · {_active.Symbol}" : PanelName(key);

    private void UpdatePanelTitles()
    {
        foreach (var (key, w) in _panelWindows) w.Title = PanelTitle(key);
    }

    /// <summary>On opening the app: the sections that were out go back to their window.</summary>
    private void RestorePanelWindows()
    {
        // now each chart has its own Chart Trader: the first time the general ticket is hidden (it comes back from Panels)
        if (!_svc.Settings.ChartTraderIntroduced)
        {
            _svc.Settings.ChartTraderIntroduced = true;
            _svc.Settings.ShowTicket = false;
            _svc.Settings.Save();
            ApplyPanelVisibility();
        }

        foreach (var key in new[] { "ticket", "dom", "tape" })
            if (WindowStateOf(key).Floating && PanelOn(key) && !_panelWindows.ContainsKey(key))
            {
                var k = key;
                Dispatcher.UIThread.Post(() => DetachSection(k), DispatcherPriority.Background);
            }
    }

    /// <summary>On closing the app: it closes the windows without forgetting that they were out.</summary>
    private void ClosePanelWindowsForShutdown()
    {
        _closingAll = true;
        foreach (var w in _panelWindows.Values.ToList()) w.Close();
    }
}
