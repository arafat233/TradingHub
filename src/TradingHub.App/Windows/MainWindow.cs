using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Views;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.App.Windows;

/// <summary>Page of the main window (Trading, Strategies, Data…).</summary>
internal sealed record Page(string Id, string Title, MaterialIconKind Icon, Func<Control> Create, KeyGesture? Gesture);

/// <summary>
/// Main window: its own top bar (integrated with the macOS buttons), navigation sidebar,
/// pages and status bar.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly AppServices _svc;
    private readonly DispatcherTimer _timer;
    private readonly Panel _host = new();
    private readonly StackPanel _toasts = new();
    private readonly Dictionary<string, Control> _pages = new();
    private readonly Dictionary<string, ToggleButton> _navButtons = new();
    private readonly List<Page> _pageDefs;
    private readonly TextBlock _statusText = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "muted", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private Border? _connPill;
    private string _lseUsage = "";
    private DateTime _lseUsageAt;
    private readonly TextBlock _infoText = new() { VerticalAlignment = VerticalAlignment.Center, Classes = { "faint", "small" } };
    private readonly TextBlock _pnlText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5, FontWeight = FontWeight.SemiBold };
    private readonly Avalonia.Controls.Shapes.Ellipse _connDot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _connText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11.5 };
    private DateTime _lastStatus;
    private DateTime _lastTickAt;
    private string _current = "";

    public MainWindow(AppServices svc)
    {
        _svc = svc;
        Notifier.Message += m => SetStatus(m);
        Title = TradingHub.Common.Product.Name;
        Width = Math.Max(1000, svc.Settings.WindowWidth);
        Height = Math.Max(640, svc.Settings.WindowHeight);
        MinWidth = 1000;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Window;
        ExtendClientAreaToDecorationsHint = true;
        // macOS "thick" title bar (like Finder): the window buttons end up centered on the bar,
        // at the same height as the app's own
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.PreferSystemChrome | Avalonia.Platform.ExtendClientAreaChromeHints.OSXThickTitleBar;
        ExtendClientAreaTitleBarHeightHint = TopBarHeight;

        Trading = new TradingView(svc, SetStatus);
        _pages["trading"] = Trading;
        svc.Live.Trading = () => Trading;
        svc.Live.Notice += text => Dispatcher.UIThread.Post(() => SetStatus(text));
        _pageDefs = new List<Page>
        {
            new("trading", "Trading", MaterialIconKind.ChartBoxOutline, () => Trading, new KeyGesture(Key.D1, Plat.Cmd)),
            new("accounts", "Account", MaterialIconKind.WalletOutline, () => new AccountsView(svc, SetStatus, this), new KeyGesture(Key.D5, Plat.Cmd)),
            new("strategies", "Backtesting", MaterialIconKind.FlaskOutline, () => new StrategiesView(svc, SetStatus), new KeyGesture(Key.D2, Plat.Cmd)),
            new("live", "Live", MaterialIconKind.AccessPoint, () => new LiveView(svc, SetStatus, this), new KeyGesture(Key.D7, Plat.Cmd)),
            new("data", "Data", MaterialIconKind.DatabaseOutline, () => new DataView(svc, SetStatus, this), new KeyGesture(Key.D3, Plat.Cmd)),
            new("connections", "Connections", MaterialIconKind.LanConnect, () => new ConnectionsView(svc, SetStatus), new KeyGesture(Key.D4, Plat.Cmd)),
            new("settings", "Settings", MaterialIconKind.CogOutline, () => new SettingsView(svc, SetStatus), new KeyGesture(Key.OemComma, Plat.Cmd)),
        };

        Content = BuildLayout();
        BuildMenu();
        Navigate("trading");

        _svc.Scripts = new UserScripts(_svc.Catalog);
        _svc.Scripts.Compiled += r => SetStatus(r.Success
            ? $"Own indicators compiled in {r.Elapsed.TotalMilliseconds:0} ms"
            : $"Error compiling your own indicators: {r.Diagnostics.FirstOrDefault(d => d.Severity =="error")}");
        _svc.Scripts.CompileAll();

        Log.Entry += e =>
        {
            if (e.Level >= LogLevel.Warning) Dispatcher.UIThread.Post(() => SetStatus("⚠ " + e.Message));
        };

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        _timer.Start();

        Opened += async (_, _) =>
        {
            await Trading.RestoreOrDefaultAsync();
            _ = _svc.StartBackgroundRecordingAsync(); // instruments the user asked to record with no chart
            // and your live ones, if that is how Settings has it (a few seconds later: the connections have already started)
            _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await _svc.Live.ResumeAtStartAsync();
                }
                catch (Exception ex)
                {
                    CrashLog.Write(ex);
                }
            }), TaskScheduler.Default);
        };

        // configurable keyboard shortcuts (only with the Trading section visible)
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_current != "trading" || e.Handled) return;
            if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && Trading.ActiveTab?.Model?.FocusTrade != null)
            {
                Trading.ClearFocusTrade(Trading.ActiveTab);
                e.Handled = true;
                return;
            }

            if (Hotkeys.Match(_svc.Settings, e, this) is { } action && Trading.ExecuteHotkey(action)) e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Closing += (_, e) =>
        {
            Workspace.Save(Trading);
            if (WindowState == WindowState.Normal)
            {
                _svc.Settings.WindowWidth = Width;
                _svc.Settings.WindowHeight = Height;
            }

            _svc.Settings.Save();
            // the red X does not close Trading Hub: it stays in the menu bar, with the strategies and the agents working
            // (Cmd+Q or "Quit Trading Hub" from the panel do close it for good)
            if (e.CloseReason == WindowCloseReason.WindowClosing && _svc.Settings.KeepInMenuBar && MenuBar.Current != null)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            Trading.CloseFloating();
        };
    }

    private int _liveSaveTick;

    /// <summary>The data connection in one line (the one on the button at the top right).</summary>
    internal string ConnectionSummary => _connText.Text ?? "";

    internal AppServices Services => _svc;
    internal TradingView Trading { get; }

    /// <summary>A section already open (or null).</summary>
    internal Control? Page(string id) => _pages.GetValueOrDefault(id);

    // ===================================================================== layout

    private Control BuildLayout()
    {
        var root = new DockPanel { Background = Ui.Bg0 };

        var top = BuildTopBar();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        var status = BuildStatusBar();
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);

        // the notices float at the top right, over the page
        _toasts.HorizontalAlignment = HorizontalAlignment.Right;
        _toasts.VerticalAlignment = VerticalAlignment.Top;
        _toasts.Margin = new Thickness(0, 12, 16, 0);
        _toasts.Width = 430;
        _toasts.Spacing = 10;
        root.Children.Add(new Panel { Children = { _host, _toasts } });
        return root;
    }

    /// <summary>Height of the top bar: that of the macOS thick title bar (the window buttons are centered there).</summary>
    private const double TopBarHeight = 52;

    private Control BuildTopBar()
    {

        // navigation: integrated into the title bar so it does not take up width
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var labels = new List<TextBlock>();
        foreach (var p in _pageDefs)
        {
            var label = new TextBlock { Text = p.Title, VerticalAlignment = VerticalAlignment.Center };
            labels.Add(label);
            var b = new ToggleButton { Content = Ui.H(7, Ui.Icon(p.Icon, 16), label) }.Classes("nav");
            ToolTip.SetTip(b, p.Gesture != null ? $"{p.Title} ({GestureText(p.Gesture)})" : p.Title);
            b.Click += (_, _) => Navigate(p.Id);
            _navButtons[p.Id] = b;
            nav.Children.Add(b);
        }

        // flush left: it leaves only the room for the window buttons (in full screen there are none)
        var left = Ui.H(18, nav);
        void PlaceNav() => left.Margin = new Thickness(OperatingSystem.IsMacOS() && WindowState != WindowState.FullScreen ? 82 : 10, 0, 0, 0);
        PlaceNav();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) PlaceNav();
        };
        // with the window narrow the bar is left with icons only (the name is still in the tooltip)
        SizeChanged += (_, e) =>
        {
            bool compact = e.NewSize.Width < 1360;
            foreach (var l in labels) l.IsVisible = !compact;
        };

        var conn = new Border { Child = Ui.H(8, _connDot, _connText), Classes = { "pill" }, Padding = new Thickness(11, 4), VerticalAlignment = VerticalAlignment.Center, Cursor = new Cursor(StandardCursorType.Hand) };
        var reconnect = new MenuItem { Header = "Reconnect now" };
        reconnect.Click += (_, _) =>
        {
            _svc.Hub.ReconnectAll();
            SetStatus("Reconnecting… whatever was missed during the outage is backfilled automatically.");
        };
        var toConnections = new MenuItem { Header = "Connections…" };
        toConnections.Click += (_, _) => Navigate("connections");
        var connMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        connMenu.Items.Add(reconnect);
        connMenu.Items.Add(toConnections);
        conn.PointerPressed += (_, e) =>
        {
            connMenu.ShowAt(conn);
            e.Handled = true;
        };
        _connPill = conn;
        ToolTip.SetTip(conn, "The active chart's connection (click: reconnect or go to Connections)");
        // the simulated account's result leads to the Account section
        var pnl = new Border { Child = _pnlText, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(pnl, "View the account: balance, orders, trades and statistics");
        pnl.PointerPressed += (_, _) => Navigate("accounts");
        // light / dark mode
        var themeButton = new Button { Classes = { "icon" }, VerticalAlignment = VerticalAlignment.Center };
        void ThemeIcon()
        {
            themeButton.Content = Ui.Icon(AppTheme.IsLight ? MaterialIconKind.WeatherNight : MaterialIconKind.WhiteBalanceSunny, 16);
            ToolTip.SetTip(themeButton, AppTheme.IsLight ? "Switch to dark mode" : "Switch to light mode");
        }

        ThemeIcon();
        themeButton.Click += (_, _) =>
        {
            _svc.Settings.ThemeMode = AppTheme.IsLight ? AppTheme.Dark : AppTheme.Light;
            _svc.Settings.Save();
            AppTheme.Apply(_svc.Settings.ThemeMode);
        };
        AppTheme.Changed += () =>
        {
            ThemeIcon();
            Trading.OnThemeChanged();
            InvalidateVisual();
        };
        var right = Ui.H(12, pnl, themeButton, conn);
        // Windows draws its caption buttons (3 x 46 px) over the top right corner: leave them room
        void PlaceRight() => right.Margin = new Thickness(0, 0, OperatingSystem.IsWindows() && WindowState != WindowState.FullScreen ? 146 : 14, 0);
        PlaceRight();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) PlaceRight();
        };

        var bar = new DockPanel { Height = TopBarHeight, LastChildFill = false };
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        bar.Children.Add(left);
        var border = new Border { Child = bar, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };

        // drag the window from the bar and double click to maximize
        border.PointerPressed += (_, e) =>
        {
            if (e.Handled || e.Source is not (Border or DockPanel or StackPanel) || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            else BeginMoveDrag(e);
        };
        return border;
    }

    private static string GestureText(KeyGesture g)
    {
        string key = g.Key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(g.Key - Key.D0)).ToString(),
            Key.OemComma => ",",
            _ => g.Key.ToString(),
        };
        return (g.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "⇧" : "") + (g.KeyModifiers.HasFlag(Plat.Cmd) ? "⌘" : "") + key;
    }

    private Control BuildStatusBar()
    {
        var bar = new DockPanel { Margin = new Thickness(14, 0), Height = 26 };
        DockPanel.SetDock(_infoText, Dock.Right);
        bar.Children.Add(_infoText);
        bar.Children.Add(_statusText);
        return new Border { Child = bar, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    // ===================================================================== navigation

    internal void Navigate(string id)
    {
        var def = _pageDefs.FirstOrDefault(p => p.Id == id);
        if (def == null) return;
        if (!_pages.TryGetValue(id, out var page))
        {
            try
            {
                page = def.Create();
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
                page = new TextBlock { Text = "Could not open the page: " + ex.Message, Margin = new Thickness(24) };
            }

            _pages[id] = page;
        }

        if (!_host.Children.Contains(page)) _host.Children.Add(page);
        foreach (var c in _host.Children) c.IsVisible = ReferenceEquals(c, page);
        foreach (var (pid, b) in _navButtons) b.IsChecked = pid == id;
        _current = id;
        Title = id == "trading" ? TradingHub.Common.Product.Name : $"{TradingHub.Common.Product.Name} — {def.Title}";
        (page as IPage)?.OnShown();
    }

    // ===================================================================== native menu

    private void BuildMenu()
    {
        var menu = new NativeMenu();

        var file = new NativeMenuItem("File") { Menu = new NativeMenu() };
        file.Menu.Add(Item("New chart", () =>
        {
            Navigate("trading");
            _ = Trading.NewChartAsync();
        }, new KeyGesture(Key.T, Plat.Cmd)));
        file.Menu.Add(Item("Close chart", Trading.CloseActiveTab, new KeyGesture(Key.W, Plat.Cmd)));
        file.Menu.Add(Item("Search instrument…", () =>
        {
            Navigate("trading");
            Trading.Picker.Open();
        }, new KeyGesture(Key.K, Plat.Cmd)));
        file.Menu.Add(new NativeMenuItemSeparator());
        file.Menu.Add(Item("Save workspace", () =>
        {
            Workspace.Save(Trading);
            SetStatus("Workspace saved");
        }, new KeyGesture(Key.S, Plat.Cmd)));
        file.Menu.Add(Item("Layouts…", () => _ = LayoutsDialog.ShowAsync(this, Trading, SetStatus)));
        file.Menu.Add(Item("Open data folder", () => Shell.OpenFolder(AppPaths.Root)));
        menu.Add(file);

        var chart = new NativeMenuItem("Chart") { Menu = new NativeMenu() };
        chart.Menu.Add(Item("Indicators…", Trading.OpenIndicators, new KeyGesture(Key.I, Plat.Cmd)));
        chart.Menu.Add(Item("Trade copier…", () =>
        {
            if (Trading.ActiveTab is { Model: { } m } tab) _ = CopierDialog.ShowAsync(this, _svc, tab.CrossData ?? m.Data, tab.AccountId);
        }));
        chart.Menu.Add(Item("Edit footprint…", Trading.OpenFootprintEditor, new KeyGesture(Key.F, Plat.Cmd | KeyModifiers.Shift)));
        chart.Menu.Add(Item("Auto scale", () =>
        {
            if (Trading.ActiveTab?.Model is { } m)
            {
                m.Viewport.AutoScale = true;
                m.Invalidate();
            }
        }, new KeyGesture(Key.A, Plat.Cmd | KeyModifiers.Alt)));
        chart.Menu.Add(Item("Go to last price", () =>
        {
            if (Trading.ActiveTab?.Model is { } m)
            {
                m.Viewport.ScrollBars = 0;
                m.Invalidate();
            }
        }, new KeyGesture(Key.End, Plat.Cmd)));
        chart.Menu.Add(new NativeMenuItemSeparator());
        var layouts = new NativeMenuItem("Layout") { Menu = new NativeMenu() };
        foreach (var (layout, label, _) in TradingView.Layouts)
            layouts.Menu.Add(Item(label, () =>
            {
                Navigate("trading");
                _ = Trading.SetLayoutAsync(layout);
            }));
        chart.Menu.Add(layouts);
        chart.Menu.Add(Item("Pull chart into a window", () =>
        {
            Navigate("trading");
            Trading.DetachActive();
        }, new KeyGesture(Key.D, Plat.Cmd | KeyModifiers.Shift)));
        chart.Menu.Add(new NativeMenuItemSeparator());
        var panels = new NativeMenuItem("Panels") { Menu = new NativeMenu() };
        foreach (var (key, label) in new[] { ("side", "Right panel"), ("ticket", "Ticket"), ("dom", "DOM"), ("tape", "Time and sales tape") })
            panels.Menu.Add(Item($"Show or hide: {label}", () => Trading.TogglePanel(key)));
        chart.Menu.Add(panels);
        chart.Menu.Add(Item("Configure DOM…", () =>
        {
            Navigate("trading");
            Trading.ShowDomSettings();
        }));
        menu.Add(chart);

        var go = new NativeMenuItem("Go") { Menu = new NativeMenu() };
        foreach (var p in _pageDefs) go.Menu.Add(Item(p.Title, () => Navigate(p.Id), p.Gesture));
        menu.Add(go);

        var code = new NativeMenuItem("Code") { Menu = new NativeMenu() };
        code.Menu.Add(Item("Indicator editor…", () => new CodeEditorWindow(_svc, CodeKind.Indicator).Show(this), new KeyGesture(Key.E, Plat.Cmd | KeyModifiers.Shift)));
        code.Menu.Add(Item("Open indicators folder", () => Shell.OpenFolder(AppPaths.UserIndicators)));
        code.Menu.Add(Item("Recompile indicators", () => _svc.Scripts?.CompileAll()));
        code.Menu.Add(new NativeMenuItemSeparator());
        code.Menu.Add(Item("Strategy backtesting…", () => Navigate("strategies")));
        code.Menu.Add(Item("Live strategies…", () => Navigate("live")));
        code.Menu.Add(Item("Open strategies folder", () => Shell.OpenFolder(AppPaths.Strategies)));
        menu.Add(code);

        NativeMenu.SetMenu(this, menu);
    }

    private static NativeMenuItem Item(string header, Action onClick, KeyGesture? gesture = null)
    {
        var item = new NativeMenuItem(header);
        if (gesture != null) item.Gesture = gesture;
        item.Click += (_, _) => onClick();
        return item;
    }

    // ===================================================================== status

    internal void SetStatus(string text)
    {
        _statusText.Text = text;
        ToolTip.SetTip(_statusText, text);
    }

    internal void Tick()
    {
        try
        {
            // if the clock jumped (the Mac slept) the connections are dead: it reconnects right away
            var now = DateTime.UtcNow;
            if (_lastTickAt != default && (now - _lastTickAt).TotalSeconds > 20)
            {
                _svc.Hub.ReconnectAll();
                SetStatus($"It came back after {(now - _lastTickAt).TotalMinutes:0} min paused: reconnecting and backfilling what was missed.");
            }

            _lastTickAt = now;
            _svc.Hub.PumpAll();
            _svc.BigOrders.Poll(_svc.Hub);
            _svc.Latency.Poll(_svc.Hub, _svc.Settings);
            // the charts in separate windows always refresh; the main window's ones, slower if they are not visible
            Trading.Tick(_current == "trading" && IsVisible);
            if ((DateTime.UtcNow - _lastStatus).TotalMilliseconds > 250)
            {
                _lastStatus = DateTime.UtcNow;
                PollMemory();
                UpdateStatusBar();
                if (++_liveSaveTick % 12 == 0) _svc.SaveLiveState(); // every ~3 s
                if (_liveSaveTick % 4 == 0) _svc.Live.Tick();
                if (_liveSaveTick % 20 == 0) // every ~5 s: no-sleep mode based on what is running
                    _svc.Sleep.Update(_svc.Settings.KeepAwake, _svc.Settings.KeepAwakeLidClosed, _svc.Settings.LidClosedOnlyOnPower, _svc.AwakeReason());
                foreach (var p in _pages.Values.OfType<IPage>()) p.OnTimer();
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }


    private static Border Toast(Control content) => new()
    {
        Child = content,
        Background = Ui.Bg1,
        BorderBrush = Ui.Warn,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 12),
        BoxShadow = BoxShadows.Parse("0 6 24 0 #40000000"),
    };

    private DateTime _memoryChecked;
    private bool _lowMemory;

    /// <summary>Every 15 seconds: when the PC is short of memory, the app gives some back (see <see cref="MemoryGuard"/>) and says so.</summary>
    private void PollMemory()
    {
        if (DateTime.UtcNow - _memoryChecked < TimeSpan.FromSeconds(15)) return;
        _memoryChecked = DateTime.UtcNow;
        if (MemoryGuard.CurrentLoad() is not { } load) return;
        bool low = MemoryGuard.Decide(load, _lowMemory);
        if (low == _lowMemory) return;
        _lowMemory = low;
        MemoryGuard.Apply(_svc.Hub.Items, low);
        SetStatus(low ? $"Low memory ({load:P0} of the PC's memory in use): the app kept less history in memory (heatmap book, flow). Close other programs to free more." : "Memory is back to normal: full history is kept again.");
    }

    private void UpdateStatusBar()
    {
        var m = Trading.ActiveTab?.Model;
        if (m != null)
        {
            var d = m.Data;
            _connDot.Fill = d.Status switch
            {
                FeedStatus.Connected => Ui.Up,
                FeedStatus.Connecting or FeedStatus.Reconnecting => Ui.Warn,
                _ => Ui.Down,
            };
            string feedName = _svc.Connections.Find(Trading.ActiveTab!.FeedId)?.Name ?? d.Feed.DisplayName;
            string state = StatusName(d.Status);
            var quiet = DateTime.UtcNow - d.LastDataAt;
            string? tip = null;
            bool quietLong = d.LastDataAt.Year < 2000 || quiet.TotalSeconds > 60;
            if (d.Status == FeedStatus.Connected && d.Feed.IsLive && quietLong
                && (d.MarketClosed || (d.Feed is ISessionAwareFeed && FuturesSessions.IsGlobexClosed(DateTime.UtcNow))))
            {
                // futures outside trading hours: it is not a cut, the market is closed
                var tz = m.Settings.TimeZone;
                var open = FuturesSessions.NextGlobexOpen(DateTime.UtcNow);
                var openLocal = TimeZoneInfo.ConvertTimeFromUtc(open, tz);
                state = open > DateTime.UtcNow ? $"market closed · opens {Day(openLocal, tz)} {openLocal:HH:mm}" : "market closed";
                tip = d.ClosedAt is { } last
                    ? $"Last trade: {Day(TimeZoneInfo.ConvertTimeFromUtc(last, tz), tz)} {TimeZoneInfo.ConvertTimeFromUtc(last, tz):HH:mm}. The connection is still active."
                    : "The connection is still active; the data comes back on its own when the market opens.";
                _connDot.Fill = Ui.Text2;
            }
            else if (d.Status == FeedStatus.Connected && d.Feed.IsLive && d.LastDataAt.Year > 2000 && quiet.TotalSeconds > 60)
            {
                state = $"no data for {(quiet.TotalMinutes < 60 ? $"{quiet.TotalMinutes:0} min" : $"{quiet.TotalHours:0} h")}";
                _connDot.Fill = Ui.Warn;
            }
            else if (d.Status == FeedStatus.Reconnecting && d.StatusMessage is { } rm
                     && System.Text.RegularExpressions.Regex.Match(rm, @"intento (\d+)") is { Success: true } attempt)
            {
                state = $"reconnecting… (attempt {attempt.Groups[1].Value})";
            }

            _connText.Text = $"{feedName} · {state}";
            ToolTip.SetTip(_connText, tip ?? d.StatusMessage);
            if (_connPill != null) ToolTip.SetTip(_connPill, HealthTip());
            _infoText.Text = d.IsLoadingHistory || d.IsExtending
                ? $"Loading history {d.HistoryProgress * 100:0}%"
                : $"{d.Trades.Count:N0} trades · {m.Series.Count:N0} candles · from {TimeZoneInfo.ConvertTimeFromUtc(d.HistoryFrom, m.Settings.TimeZone):dd/MM HH:mm}";
        }

        // day result and balance of the simulated account (stored: they do not go back to zero when Trading Hub closes)
        var (day, balance) = _svc.AccountCenter.SimSummary();
        _pnlText.Foreground = day >= 0 ? Ui.Up : Ui.Down;
        _pnlText.Text = $"Simulated  {day:+0.00;-0.00;0.00}  ·  {balance:N2}";
        Trading.UpdatePosition();
    }

    /// <summary>Every open instrument's connection state and how long ago it last sent data, plus the London Strategic Edge plan allowance.</summary>
    private string HealthTip()
    {
        var lines = new List<string>();
        foreach (var d in _svc.Hub.Items.OrderBy(i => i.Instrument.Key))
        {
            string name = _svc.Connections.Find(d.Instrument.FeedId)?.Name ?? d.Feed.DisplayName;
            var ago = DateTime.UtcNow - d.LastDataAt;
            string last = d.LastDataAt.Year < 2000 ? "no data yet" : ago.TotalMinutes < 1 ? $"data {ago.TotalSeconds:0} s ago" : ago.TotalHours < 1 ? $"data {ago.TotalMinutes:0} min ago" : $"data {ago.TotalHours:0.#} h ago";
            var lat = _svc.Latency.Of(d.Instrument.Key);
            lines.Add($"{name} · {d.Instrument.Symbol}: {StatusName(d.Status)}, {last}{(lat is { } l ? $", delay {l.Median:0.0} s" : "")}");
        }

        RefreshLseUsage();
        if (_lseUsage.Length > 0) lines.Add("London Strategic Edge plan: " + _lseUsage);
        lines.Add("Click: reconnect or go to Connections");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Asks the London Strategic Edge allowance endpoint (it uses none of the allowance) at most every 5 minutes.</summary>
    private void RefreshLseUsage()
    {
        if (DateTime.UtcNow - _lseUsageAt < TimeSpan.FromMinutes(5)) return;
        _lseUsageAt = DateTime.UtcNow;
        var p = _svc.Connections.Enabled.FirstOrDefault(c => c.Kind == ConnectionKind.LondonStrategicEdge);
        if (p == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var (ok, msg) = await _svc.Connections.TestAsync(p, CancellationToken.None);
                _lseUsage = ok ? msg.Replace("API key OK. ", "") : "";
            }
            catch (Exception) { _lseUsage = ""; }
        });
    }

    private static string Day(DateTime local, TimeZoneInfo tz)
    {
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz).Date;
        if (local.Date == today) return "today";
        if (local.Date == today.AddDays(1)) return "tomorrow";
        return local.ToString("ddd", new System.Globalization.CultureInfo("en-US"));
    }

    private static string StatusName(FeedStatus s) => s switch
    {
        FeedStatus.Connected => "connected",
        FeedStatus.Connecting => "connecting…",
        FeedStatus.Reconnecting => "reconnecting…",
        FeedStatus.Error => "error",
        _ => "disconnected",
    };
}

/// <summary>App icon (Assets/icon.png).</summary>
internal static class AppIcon
{
    private static Avalonia.Media.Imaging.Bitmap? _bitmap;

    public static Avalonia.Media.Imaging.Bitmap? Bitmap
    {
        get
        {
            if (_bitmap != null) return _bitmap;
            try
            {
                using var s = Avalonia.Platform.AssetLoader.Open(new Uri("avares://TradingHub/Assets/icon.png"));
                _bitmap = new Avalonia.Media.Imaging.Bitmap(s);
            }
            catch
            {
                // no icon
            }

            return _bitmap;
        }
    }

    public static WindowIcon? Window => Bitmap is { } b ? new WindowIcon(b) : null;
}

/// <summary>A page that wants to know when it is shown, or to refresh periodically.</summary>
internal interface IPage
{
    void OnShown();
    void OnTimer();
}

/// <summary>Utilities for opening folders and files with the system.</summary>
internal static class Shell
{
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            // Windows: the shell opens a folder, a file or an address with whatever the user has set for it
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = false });
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Opens a web address in the browser.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add(url);
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>Shows the selected file in Finder.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo("open") { UseShellExecute = false };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // ignore
        }
    }

    public static void Open(string pathOrUrl)
    {
        try
        {
            if (OperatingSystem.IsWindows()) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
            else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", $"\"{pathOrUrl}\"") { UseShellExecute = false });
        }
        catch
        {
            // ignore
        }
    }
}
