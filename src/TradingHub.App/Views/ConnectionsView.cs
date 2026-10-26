using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Feeds.Rithmic;

namespace TradingHub.App.Views;

/// <summary>
/// Connections: data profiles and accounts (Rithmic, Binance, Bybit, Interactive Brokers and simulator).
/// Passwords and API secrets are stored in the macOS Keychain, never in files.
/// </summary>
internal sealed class ConnectionsView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly ListBox _list = new();
    private readonly ContentControl _detail = new();
    private readonly Dictionary<string, (bool Ok, string Message, DateTime When)> _results = new();
    private ConnectionProfile? _selected;
    private CancellationTokenSource? _testCts;

    public ConnectionsView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        Content = Build();
        FillList();
        _svc.Connections.Changed += FillList;
    }

    public void OnShown() => FillList();

    public void OnTimer()
    {
    }

    private static MaterialIconKind IconOf(ConnectionKind k) => k switch
    {
        ConnectionKind.Simulator => MaterialIconKind.FlaskOutline,
        ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot => MaterialIconKind.Bitcoin,
        ConnectionKind.Bybit => MaterialIconKind.CurrencyUsd,
        ConnectionKind.Kraken or ConnectionKind.Coinbase or ConnectionKind.Bitfinex => MaterialIconKind.Bitcoin,
        ConnectionKind.Rithmic => MaterialIconKind.ChartTimelineVariant,
        ConnectionKind.AmpFutures => MaterialIconKind.ChartBellCurveCumulative,
        ConnectionKind.Tradovate => MaterialIconKind.ChartLineVariant,
        ConnectionKind.CTrader => MaterialIconKind.ChartAreaspline,
        ConnectionKind.Oanda => MaterialIconKind.Earth,
        ConnectionKind.Alpaca => MaterialIconKind.Finance,
        ConnectionKind.Databento => MaterialIconKind.DatabaseArrowDownOutline,
        ConnectionKind.DxFeed => MaterialIconKind.Layers,
        ConnectionKind.LondonStrategicEdge => MaterialIconKind.CloudDownloadOutline,
        _ => MaterialIconKind.Bank,
    };

    private Control Build()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };

        // ---- list
        var add = new Button { Content = Ui.H(6, Ui.Icon(MaterialIconKind.Plus, 16), new TextBlock { Text = "New connection" }), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var menu = new MenuFlyout();
        foreach (var kind in Enum.GetValues<ConnectionKind>())
        {
            // what each one provides, so you can pick without having to know already
            string? hint = kind switch
            {
                ConnectionKind.Tradovate => "futures: trading accounts and data",
                ConnectionKind.CTrader => "CFDs and forex from IC Markets, Pepperstone, FxPro…",
                ConnectionKind.Oanda => "index, gold and forex CFDs (free practice)",
                ConnectionKind.Alpaca => "free US stocks and ETFs (SPY, QQQ)",
                ConnectionKind.Databento => "CME futures history since 2010, and live with Level 2",
                ConnectionKind.DxFeed => "CME futures live with Level 2 (your dxFeed token)",
                ConnectionKind.LondonStrategicEdge => "free stocks, forex, crypto and indices: live ticks and history (your free key)",
                _ => null,
            };
            object itemHeader = hint == null ? Ui.EnumName(kind) : Ui.V(1, new TextBlock { Text = Ui.EnumName(kind) }, new TextBlock { Text = hint, Classes = { "faint", "small" } });
            var mi = new MenuItem { Header = itemHeader, Icon = Ui.Icon(IconOf(kind), 16) };
            mi.Click += (_, _) => AddProfile(kind);
            menu.Items.Add(mi);
        }

        add.Flyout = menu;
        var header = Ui.V(4, Ui.H1("Connections"), Ui.Muted("Market data and trading accounts"));
        header.Margin = new Thickness(0, 0, 0, 16);
        var left = new DockPanel { Margin = new Thickness(20, 20, 12, 20) };
        DockPanel.SetDock(header, Dock.Top);
        left.Children.Add(header);
        DockPanel.SetDock(add, Dock.Bottom);
        add.Margin = new Thickness(0, 12, 0, 0);
        left.Children.Add(add);
        _list.SelectionChanged += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: ConnectionProfile p }) Select(p);
        };
        _list.Background = Brushes.Transparent;
        left.Children.Add(_list);
        grid.Children.Add(new Border { Child = left, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 1, 0) });

        // ---- detail
        _detail.Margin = new Thickness(28, 24, 28, 32);
        var scroll = new ScrollViewer { Content = _detail };
        Grid.SetColumn(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    private void FillList()
    {
        var keep = _selected?.Id;
        _list.Items.Clear();
        foreach (var p in _svc.Connections.Profiles)
        {
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            dot.Fill = !p.Enabled ? Ui.Text2 : _results.TryGetValue(p.Id, out var r) ? (r.Ok ? Ui.Up : Ui.Down) : Ui.Line;
            ToolTip.SetTip(dot, !p.Enabled ? "Disabled" : _results.TryGetValue(p.Id, out var r2) ? r2.Message : "Not tested");
            var icon = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Background = Ui.Bg3, Child = Ui.Icon(IconOf(p.Kind), 18, p.Enabled ? Ui.Accent : Ui.Text2) };
            string sub = Describe(p);
            var text = Ui.V(1, new TextBlock { Text = p.Name, FontWeight = FontWeight.SemiBold, Opacity = p.Enabled ? 1 : 0.6 }, new TextBlock { Text = sub, Classes = { "muted", "small" } });
            var row = new DockPanel { Margin = new Thickness(2, 4) };
            DockPanel.SetDock(icon, Dock.Left);
            icon.Margin = new Thickness(0, 0, 12, 0);
            DockPanel.SetDock(dot, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(dot);
            row.Children.Add(text);
            _list.Items.Add(new ListBoxItem { Content = row, Tag = p });
        }

        var sel = _list.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((ConnectionProfile)i.Tag!).Id == keep) ?? _list.Items.OfType<ListBoxItem>().FirstOrDefault();
        _list.SelectedItem = sel;
    }

    /// <summary>Summary of a connection for the list.</summary>
    private string Describe(ConnectionProfile p)
    {
        string prefix = p.Name == Ui.EnumName(p.Kind) ? "" : Ui.EnumName(p.Kind) + " · ";
        if (!p.Enabled) return prefix + "disabled";
        string detail = p.Kind switch
        {
            ConnectionKind.Simulator => "simulated market",
            ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot => string.IsNullOrWhiteSpace(p.ApiKey)
                ? "public data · no account"
                : p.Testnet ? "data + testnet account" : p.AllowRealOrders ? "data + REAL account" : "data + real account (read only)",
            ConnectionKind.Bybit or ConnectionKind.Kraken or ConnectionKind.Coinbase or ConnectionKind.Bitfinex => "public data",
            ConnectionKind.Rithmic or ConnectionKind.AmpFutures => string.IsNullOrWhiteSpace(p.RithmicUser) ? "no user configured" : $"{p.RithmicUser} · {p.RithmicSystem}",
            ConnectionKind.Tradovate => string.IsNullOrWhiteSpace(p.TradovateUser) ? "no user configured" : $"{p.TradovateUser} · {(p.Testnet ? "demo" : p.AllowRealOrders ? "REAL" : "real (read only)")}",
            ConnectionKind.CTrader => string.IsNullOrWhiteSpace(p.CTraderClientId) ? "no application configured" : $"{(p.Testnet ? "demo" : "real")}{(p.CTraderLogin > 0 ? " · account " + p.CTraderLogin : "")}",
            ConnectionKind.Oanda => (p.Testnet ? "practice" : "real") + (string.IsNullOrWhiteSpace(p.OandaAccount) ? "" : " · " + p.OandaAccount),
            ConnectionKind.Alpaca => string.IsNullOrWhiteSpace(p.ApiKey) ? "no keys" : $"SIP history · live {p.AlpacaLiveFeed.ToUpperInvariant()}",
            ConnectionKind.Databento => p.DatabentoMbo ? "CME history + live with Level 3 (every order)" : p.DatabentoLevel2 ? "CME history + live with Level 2" : "CME history + live trades",
            ConnectionKind.DxFeed => p.DxFeedUrl.TrimEnd('/') == TradingHub.Feeds.DxFeed.DxFeedSettings.DemoUrl ? "demo server (delayed)" : $"live · Level 2 {p.DxFeedDomSource}",
            ConnectionKind.LondonStrategicEdge => "free · ticks (24 h) + candles",
            _ => $"{p.IbHost}:{p.IbPort}",
        };
        return prefix + detail;
    }

    private void AddProfile(ConnectionKind kind)
    {
        int n = _svc.Connections.Profiles.Count(p => p.Kind == kind) + 1;
        var p = new ConnectionProfile { Kind = kind, Name = Ui.EnumName(kind) + (n > 1 ? $" {n}" : ""), Enabled = kind is not (ConnectionKind.Rithmic or ConnectionKind.AmpFutures) };
        if (kind is ConnectionKind.CTrader) p.Name = n > 1 ? $"cTrader {n}" : "cTrader";
        if (kind is ConnectionKind.Alpaca) p.Name = n > 1 ? $"Alpaca {n}" : "Alpaca";
        if (kind is ConnectionKind.Databento) p.Name = "Databento";
        if (kind is ConnectionKind.DxFeed) p.Name = n > 1 ? $"dxFeed {n}" : "dxFeed";
        if (kind is ConnectionKind.LondonStrategicEdge) p.Name = "London Strategic Edge";
        // the first one of each type uses its data's id (so the tick files end up with a clear name)
        string defaultId = ConnectionProfile.DefaultId(kind);
        if (kind is ConnectionKind.Tradovate or ConnectionKind.CTrader or ConnectionKind.Oanda or ConnectionKind.Alpaca or ConnectionKind.Databento or ConnectionKind.DxFeed or ConnectionKind.LondonStrategicEdge
            && _svc.Connections.Profiles.All(x => x.Id != defaultId))
            p.Id = defaultId;
        if (kind == ConnectionKind.Simulator) p.HistoryHours = 6;
        if (kind == ConnectionKind.AmpFutures)
        {
            // AMP provides Rithmic accounts: production server (Chicago) and the demo system by default
            p.RithmicUri = RithmicGateways.ChicagoUri;
            p.RithmicSystem = "Rithmic Paper Trading";
        }
        _svc.Connections.Profiles.Add(p);
        _svc.Connections.Save();
        _selected = p;
        FillList();
    }

    // ===================================================================== detail

    private void Select(ConnectionProfile p)
    {
        _selected = p;
        _detail.Content = BuildDetail(p);
    }

    private Control BuildDetail(ConnectionProfile p)
    {
        var name = new TextBox { Text = p.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, MinWidth = 170, MaxWidth = 300 };
        var enabled = new ToggleSwitch { IsChecked = p.Enabled, OnContent = "Enabled", OffContent = "Disabled" };
        var history = new ComboBox { MinWidth = 160 };
        foreach (var (h, label) in new[] { (0.5, "30 minutes"), (1.0, "1 hour"), (1.5, "1.5 hours"), (3.0, "3 hours"), (6.0, "6 hours"), (12.0, "12 hours"), (24.0, "1 day"), (72.0, "3 days"), (168.0, "7 days") })
            history.Items.Add(new ComboBoxItem { Content = label, Tag = h });
        history.SelectedItem = history.Items.OfType<ComboBoxItem>().OrderBy(i => Math.Abs((double)i.Tag! - p.HistoryHours)).First();

        var resultIcon = new ContentControl();
        var resultText = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var resultBox = new Border { Child = Ui.H(10, resultIcon, resultText), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), Background = Ui.Bg2, IsVisible = false };
        void ShowResult(bool ok, string msg, bool running = false)
        {
            resultBox.IsVisible = true;
            resultIcon.Content = running ? Ui.Icon(MaterialIconKind.ProgressClock, 18, Ui.Text1) : Ui.Icon(ok ? MaterialIconKind.CheckCircle : MaterialIconKind.CloseCircle, 18, ok ? Ui.Up : Ui.Down);
            resultText.Text = msg;
        }

        if (_results.TryGetValue(p.Id, out var last)) ShowResult(last.Ok, $"{last.Message}  ({Ui.Ago(last.When)})");

        // fields based on the type (they are written into the profile on save)
        var apply = new List<Action>();
        var sections = new StackPanel { Spacing = 16 };

        var dataSection = Ui.V(14,
            Ui.H2("Market data"),
            Ui.Field("History when opening a chart", history, "Whatever is recorded or downloaded in Data loads instantly; the rest is requested from the connection."));
        sections.Children.Add(Ui.Card(dataSection));
        apply.Add(() => p.HistoryHours = (double)((ComboBoxItem)history.SelectedItem!).Tag!);

        switch (p.Kind)
        {
            case ConnectionKind.Simulator:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "A simulated market with a realistic book and trades, ideal for testing offline. No configuration needed."));
                break;
            case ConnectionKind.BinanceFutures:
            case ConnectionKind.BinanceSpot:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Binance market data is public: no account is needed to see charts, DOM and tape."));
                sections.Children.Add(Ui.Card(BinanceAccount(p, apply)));
                break;
            case ConnectionKind.Bybit:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Public data for Bybit's linear (USDT) perpetuals. Trading with a Bybit account is not available yet."));
                sections.Children.Add(Ui.Card(BybitHistorySection(p, apply)));
                break;
            case ConnectionKind.Kraken:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, $"Public spot data from {p.Kind}: tape, order book and history. No account is needed. Trading is not available yet."));
                break;
            case ConnectionKind.Coinbase:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Public spot data from Coinbase: tape, order book and history, no account needed. Trading is not available yet."));
                sections.Children.Add(Ui.Card(CoinbaseSection(p, apply)));
                break;
            case ConnectionKind.Bitfinex:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "Public spot data from Bitfinex: tape, order book and history, free, no account. Bitfinex also publishes every individual order (Level 3), so the heatmap's book events show real orders being added, pulled and filled instead of guesses. Trading is not available yet."));
                break;
            case ConnectionKind.Rithmic:
            case ConnectionKind.AmpFutures:
                sections.Children.Add(Ui.Card(RithmicSection(p, apply)));
                break;
            case ConnectionKind.InteractiveBrokers:
                sections.Children.Add(Ui.Card(IbSection(p, apply)));
                break;
            case ConnectionKind.Tradovate:
                sections.Children.Add(Ui.Card(TradovateSection(p, apply)));
                break;
            case ConnectionKind.CTrader:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "CFDs have no real trades: each quote (bid) counts as a trade with volume 1. It is good for price, candles, replay and backtests; the footprint and the delta are approximate."));
                sections.Children.Add(Ui.Card(CTraderSection(p, apply)));
                break;
            case ConnectionKind.Oanda:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "CFDs have no real trades: each quote (mid price) counts as a trade with volume 1. It is good for price, candles, replay and backtests; the footprint and the delta are approximate."));
                sections.Children.Add(Ui.Card(OandaSection(p, apply)));
                break;
            case ConnectionKind.Alpaca:
                sections.Children.Add(Ui.Card(AlpacaSection(p, apply)));
                break;
            case ConnectionKind.Databento:
                dataSection.Children.Add(Note(MaterialIconKind.ShieldCheckOutline, "Charts show what you downloaded in Data plus what arrives live: Trading Hub never buys history from Databento on its own."));
                sections.Children.Add(Ui.Card(DatabentoSection(p, apply)));
                break;
            case ConnectionKind.DxFeed:
                sections.Children.Add(Ui.Card(DxFeedSection(p, apply)));
                break;
            case ConnectionKind.LondonStrategicEdge:
                dataSection.Children.Add(Note(MaterialIconKind.InformationOutline, "No order book and no trade side from this service: each trade's side is inferred from the tick rule (whether the price rose or fell), so the footprint, delta and CVD are estimates, and the heatmap needs a feed with depth. The last 24 hours come as real ticks, older history as candles."));
                sections.Children.Add(Ui.Card(LseSection(p, apply)));
                break;
        }

        // ---- header and actions
        var test = Ui.Button("Test connection", MaterialIconKind.LanConnect, () => { });
        var save = Ui.Button("Save", MaterialIconKind.ContentSaveOutline, () => { }, "primary");
        var delete = Ui.Button("Delete", MaterialIconKind.DeleteOutline, () => { }, "ghost");
        delete.IsVisible = _svc.Connections.Profiles.Count(x => x.Kind == p.Kind) > 1 || p.Kind is ConnectionKind.InteractiveBrokers or ConnectionKind.AmpFutures
            or ConnectionKind.Tradovate or ConnectionKind.CTrader or ConnectionKind.Oanda or ConnectionKind.Alpaca or ConnectionKind.Databento or ConnectionKind.DxFeed or ConnectionKind.LondonStrategicEdge;

        void Save()
        {
            p.Name = string.IsNullOrWhiteSpace(name.Text) ? Ui.EnumName(p.Kind) : name.Text.Trim();
            p.Enabled = enabled.IsChecked == true;
            foreach (var a in apply) a();
            _svc.Connections.Save();
            _svc.Connections.Invalidate(p);
            InstrumentPicker.ClearCache(p.Id);
            _status($"Connection \"{p.Name}\" saved");
        }

        save.Click += (_, _) => Save();
        test.Click += async (_, _) =>
        {
            Save();
            _testCts?.Cancel();
            _testCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            test.IsEnabled = false;
            ShowResult(true, "Testing…", running: true);
            try
            {
                var (ok, msg) = await _svc.Connections.TestAsync(p, _testCts.Token);
                _results[p.Id] = (ok, msg, DateTime.UtcNow);
                ShowResult(ok, msg);
                FillList();
            }
            finally
            {
                test.IsEnabled = true;
            }
        };
        delete.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window w) return;
            if (!await ConfirmDialog.ShowAsync(w, "Delete connection", $"\"{p.Name}\" and its secrets are removed from the secure store (Keychain / Credential Manager). The recorded data is left untouched.", "Delete", danger: true)) return;
            _svc.Connections.ClearSecrets(p);
            _svc.Connections.Invalidate(p);
            _svc.Connections.Profiles.Remove(p);
            _svc.Connections.Save();
            _selected = null;
            FillList();
        };

        var kindBadge = new Border { Child = Ui.H(6, Ui.Icon(IconOf(p.Kind), 14, Ui.Accent), new TextBlock { Text = Ui.EnumName(p.Kind), FontSize = 12 }), Classes = { "pill" } };
        var titleRow = new DockPanel();
        // the switch goes with the actions: on narrow windows it does not overlap "Delete"
        enabled.VerticalAlignment = VerticalAlignment.Center;
        enabled.Margin = new Thickness(0, 0, 8, 0);
        var actions = Ui.H(8, enabled, delete, test, save);
        DockPanel.SetDock(actions, Dock.Right);
        titleRow.Children.Add(actions);
        var left = Ui.H(12, name, kindBadge);
        left.ClipToBounds = true;
        titleRow.Children.Add(left);

        return new StackPanel { Spacing = 16, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left, Children = { titleRow, resultBox, sections } };
    }

    private static Control Note(MaterialIconKind icon, string text)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center };
        var d = new DockPanel();
        var i = Ui.Icon(icon, 16, Ui.Text2);
        i.Margin = new Thickness(0, 0, 8, 0);
        i.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(i, Dock.Left);
        d.Children.Add(i);
        d.Children.Add(t);
        return d;
    }

    private static Grid TwoColumns(params Control[] fields)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
        int row = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            if (i % 2 == 0)
            {
                g.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                if (i > 0) g.RowDefinitions.Add(new RowDefinition(new GridLength(14)));
                row = g.RowDefinitions.Count - 1;
            }

            Grid.SetRow(fields[i], row);
            Grid.SetColumn(fields[i], i % 2 == 0 ? 0 : 2);
            g.Children.Add(fields[i]);
        }

        return g;
    }

    private Control BinanceAccount(ConnectionProfile p, List<Action> apply)
    {
        var testnet = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Testnet (test money)", OffContent = "Real account" };
        var key = new TextBox { Text = p.ApiKey, Watermark = "API key" };
        var secret = new TextBox { PasswordChar = '•', Watermark = string.IsNullOrEmpty(_svc.Connections.GetSecret(p)) ? "API secret" : "•••••••• (stored securely)" };
        var allowReal = new CheckBox { Content = "Allow REAL orders from Trading Hub", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Ask for confirmation before each real order", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Increment = 0.001m, FormatString = "0.#####", Watermark = "no limit" };
        var warn = Note(MaterialIconKind.AlertOutline, "With a real account the orders move real money. By default Trading Hub only trades on testnet; enable real orders only if you know what you are doing.");
        void UpdateEnabled()
        {
            bool real = testnet.IsChecked != true;
            allowReal.IsEnabled = real;
            confirm.IsEnabled = real && allowReal.IsChecked == true;
            maxQty.IsEnabled = real && allowReal.IsChecked == true;
            warn.IsVisible = real;
        }

        testnet.IsCheckedChanged += (_, _) => UpdateEnabled();
        allowReal.IsCheckedChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();

        apply.Add(() =>
        {
            p.Testnet = testnet.IsChecked == true;
            p.ApiKey = (key.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            p.AllowRealOrders = !p.Testnet && allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked != false;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });

        var help = new Button { Content = "How do I create an API key?", Classes = { "ghost" }, Padding = new Thickness(0), FontSize = 12, Foreground = Ui.Focus };
        help.Click += (_, _) => Shell.Open(testnet.IsChecked == true
            ? (p.Kind == ConnectionKind.BinanceFutures ? "https://testnet.binancefuture.com" : "https://testnet.binance.vision")
            : "https://www.binance.com/es/my/settings/api-management");

        return Ui.V(14,
            Ui.H2("Trading account"),
            Note(MaterialIconKind.ShieldKeyOutline, "Create an API key with trading permission only (no withdrawals). The secret is stored in the macOS Keychain."),
            testnet,
            TwoColumns(Ui.Field("API key", key), Ui.Field("API secret", secret)),
            help,
            Ui.Divider(),
            allowReal,
            confirm,
            Ui.Field("Maximum quantity per order (0 = no limit)", maxQty),
            warn);
    }

    private static string RithmicCacheFile => Path.Combine(AppPaths.Root, "rithmic-servidores.json");

    private static List<RithmicServer> LoadRithmicCache()
    {
        try
        {
            if (File.Exists(RithmicCacheFile))
                return System.Text.Json.JsonSerializer.Deserialize<List<RithmicServer>>(File.ReadAllText(RithmicCacheFile)) ?? new();
        }
        catch
        {
            // corrupt cache: it is requested again
        }

        return new List<RithmicServer>();
    }

    /// <summary>Rithmic as in ATAS: user, password, a single "Server" (system. region), aggregated quotes and timeout.</summary>
    private Control RithmicSection(ConnectionProfile p, List<Action> apply)
    {
        var servers = LoadRithmicCache();
        var search = new TextBox { Watermark = "Search server (paper, 01, Apex, Frankfurt, Sao Paulo…)" };
        var server = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, MaxDropDownHeight = 420, PlaceholderText = "Choose the server" };
        var info = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var refresh = Ui.IconButton(MaterialIconKind.Refresh, "Request the list from every server again", () => { }, 16);
        refresh.Margin = new Thickness(6, 0, 0, 0);
        string selSystem = p.RithmicSystem, selUri = p.RithmicUri;

        // server by hand (in case Rithmic adds a new one)
        var customUri = new TextBox { Text = p.RithmicUri, Watermark = "wss://server:443" };
        var customSystem = new TextBox { Text = p.RithmicSystem, Watermark = "System (e.g. Rithmic 01)" };
        var customToggle = new CheckBox { Content = "Use a server that is not in the list" };
        var customPanel = TwoColumns(Ui.Field("Address", customUri), Ui.Field("System", customSystem));
        customPanel.IsVisible = false;
        customToggle.IsCheckedChanged += (_, _) => customPanel.IsVisible = customToggle.IsChecked == true;

        void Fill()
        {
            var words = (search.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var list = servers.Where(sv => words.All(w => sv.Display.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
            int fastest = servers.Count > 0 ? servers.Min(x => x.LatencyMs) : 0;
            server.Items.Clear();
            ComboBoxItem? selected = null;
            foreach (var sv in list)
            {
                var item = new ComboBoxItem
                {
                    Tag = sv,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new DockPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = $"{sv.LatencyMs} ms", Classes = { "faint", "small" }, Foreground = sv.LatencyMs <= fastest * 1.3 ? Ui.Up : null, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) }.Also(t => DockPanel.SetDock(t, Dock.Right)),
                            new TextBlock { Text = sv.Display, VerticalAlignment = VerticalAlignment.Center },
                        },
                    },
                };
                server.Items.Add(item);
                if (sv.System == selSystem && sv.Uri == selUri) selected = item;
            }

            if (selected == null && words.Length == 0)
            {
                // what is stored is not in the list (or has not arrived yet): it is shown anyway
                var cur = new RithmicServer(selSystem, RithmicGateways.NameOf(selUri), selUri, 0);
                selected = new ComboBoxItem { Tag = cur, Content = new TextBlock { Text = cur.Display } };
                server.Items.Insert(0, selected);
            }

            server.SelectedItem = selected;
        }

        var aggNote = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        void UpdateAggregated()
        {
            var known = servers.FirstOrDefault(x => x.System == selSystem && x.Uri == selUri);
            aggNote.Text = known == null ? ""
                : known.Aggregated ? "This system offers aggregated quotes."
                : "This system does not offer aggregated quotes: it connects without them (asking for them anyway makes Rithmic reject the login).";
        }

        server.SelectionChanged += (_, _) =>
        {
            if (server.SelectedItem is ComboBoxItem { Tag: RithmicServer sv })
            {
                selSystem = sv.System;
                selUri = sv.Uri;
                UpdateAggregated();
            }
        };
        search.TextChanged += (_, _) => Fill();

        async Task DiscoverAsync()
        {
            info.Text = "Querying the Rithmic servers…";
            refresh.IsEnabled = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var found = await RithmicDirectory.DiscoverAsync(cts.Token);
                if (found.Count > 0)
                {
                    servers = found;
                    try
                    {
                        Directory.CreateDirectory(AppPaths.Root);
                        File.WriteAllText(RithmicCacheFile, System.Text.Json.JsonSerializer.Serialize(found));
                    }
                    catch
                    {
                        // with no cache nothing happens
                    }
                }

                Fill();
                UpdateAggregated();
                var best = servers.GroupBy(x => x.Gateway).Select(g => g.First()).OrderBy(x => x.LatencyMs).FirstOrDefault();
                info.Text = servers.Count == 0
                    ? "No server responded (no internet?)."
                    : $"{servers.Count} combinations across {servers.Select(x => x.Uri).Distinct().Count()} servers. The fastest from here: {best?.Gateway} ({best?.LatencyMs} ms). Colo 75 only works from the CME datacenter.";
            }
            catch (Exception ex)
            {
                info.Text = "Could not query: " + ex.Message;
            }
            finally
            {
                refresh.IsEnabled = true;
            }
        }

        refresh.Click += async (_, _) => await DiscoverAsync();
        var serverRow = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        serverRow.Children.Add(refresh);
        serverRow.Children.Add(server);

        var user = new TextBox { Text = p.RithmicUser, Watermark = "user (Login)" };
        var pass = new TextBox { PasswordChar = '•', RevealPassword = false, Watermark = string.IsNullOrEmpty(_svc.Connections.GetSecret(p)) ? "password" : "•••••••• (stored securely)" };
        var reveal = new ToggleButton { Content = Ui.Icon(MaterialIconKind.EyeOutline, 15), Padding = new Thickness(6, 2), MinHeight = 0, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTip.SetTip(reveal, "Show what you type");
        reveal.IsCheckedChanged += (_, _) => pass.RevealPassword = reveal.IsChecked == true;
        var passRow = new DockPanel();
        DockPanel.SetDock(reveal, Dock.Right);
        reveal.Margin = new Thickness(6, 0, 0, 0);
        passRow.Children.Add(reveal);
        passRow.Children.Add(pass);

        var aggregated = new CheckBox { Content = "Aggregated quotes", IsChecked = p.RithmicAggregatedQuotes };
        ToolTip.SetTip(aggregated, "Rithmic merges book updates that arrive at the same instant (fewer messages, same price). The same as the ATAS option.");
        var timeout = new NumericUpDown { Value = p.RithmicTimeoutMs, Minimum = 2000, Maximum = 120000, Increment = 1000, FormatString = "0", Width = 140 };
        var app = new TextBox { Text = p.RithmicAppName, Watermark = "TradingHub" };
        var products = new TextBox { Text = string.Join(", ", p.RithmicProducts), Watermark = "ES, NQ, MES, MNQ, CL…" };

        // orders: practice accounts always trade; the real ones (Rithmic 01, prop firms) only if enabled here
        var allowReal = new CheckBox { Content = "Allow orders on real accounts (Rithmic 01, prop firms…)", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Confirm each real order", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0", Width = 140 };

        apply.Add(() =>
        {
            if (customToggle.IsChecked == true && !string.IsNullOrWhiteSpace(customUri.Text))
            {
                p.RithmicUri = customUri.Text.Trim();
                if (!string.IsNullOrWhiteSpace(customSystem.Text)) p.RithmicSystem = customSystem.Text.Trim();
            }
            else
            {
                p.RithmicUri = selUri;
                p.RithmicSystem = selSystem;
            }

            p.RithmicUser = (user.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pass.Text)) _svc.Connections.SetSecret(p, pass.Text);
            pass.Text = "";
            p.RithmicAggregatedQuotes = aggregated.IsChecked == true;
            p.RithmicTimeoutMs = (int)(timeout.Value ?? 10000);
            p.RithmicAppName = string.IsNullOrWhiteSpace(app.Text) ? "TradingHub" : app.Text.Trim();
            p.RithmicProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
            p.AllowRealOrders = allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked == true;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });

        Fill();
        Dispatcher.UIThread.Post(() => _ = DiscoverAsync(), DispatcherPriority.Background);
        return Ui.V(14,
            Ui.H2(p.Kind == ConnectionKind.AmpFutures ? "AMP Futures account" : "Rithmic account"),
            Note(MaterialIconKind.InformationOutline, p.Kind == ConnectionKind.AmpFutures
                ? "AMP Futures runs on Rithmic: AMP sends you a Rithmic user and password. For the demo choose “Rithmic Paper Trading” and for the real account “Rithmic 01”, on the nearest server. Trading Hub connects directly over R|Protocol (you do not need R|Trader Pro open)."
                : "A direct connection over R|Protocol, Rithmic's official API (no R|Trader Pro needed). Choose the server as you would in ATAS: system (Paper Trading, Rithmic 01, your prop firm) and region."),
            Ui.H3("Connection"),
            TwoColumns(Ui.Field("User", user), Ui.Field("Password", passRow)),
            Ui.Field("Server", Ui.V(8, search, serverRow, info)),
            customToggle,
            customPanel,
            Ui.H3("Market data"),
            TwoColumns(Ui.V(4, aggregated, aggNote), Ui.Field("Connection timeout (ms)", timeout)),
            Ui.H3("Trading"),
            Note(MaterialIconKind.ShieldCheckOutline, "Your Rithmic accounts appear in the Chart Trader (“Account”) once the connection is active. The practice ones (Paper Trading) trade directly; the real ones need this authorization and ask for confirmation."),
            allowReal,
            TwoColumns(confirm, Ui.Field("Maximum quantity per order (0 = no limit)", maxQty)),
            Ui.H3("Advanced"),
            TwoColumns(Ui.Field("Application name", app, "Some systems require an approved app name."), Ui.Field("Products for the search", products, "The front-month contract is looked up for each product (e.g. ES → ESZ6).")));
    }

    /// <summary>Secret field: it does not show what is stored, it only says that it is in the Keychain.</summary>
    private TextBox SecretBox(ConnectionProfile p, string? name, string watermark) =>
        new() { PasswordChar = '•', Watermark = string.IsNullOrEmpty(name == null ? _svc.Connections.GetSecret(p) : _svc.Connections.GetSecret(p, name)) ? watermark : "•••••••• (stored securely)" };

    private static Button Link(string text, string url)
    {
        var b = new Button { Content = text, Classes = { "ghost" }, Padding = new Thickness(0), FontSize = 12, Foreground = Ui.Focus };
        b.Click += (_, _) => Shell.Open(url);
        ToolTip.SetTip(b, url);
        return b;
    }

    /// <summary>Tradovate: user, password and API key (CID + secret); demo or real; products.</summary>
    private Control TradovateSection(ConnectionProfile p, List<Action> apply)
    {
        var demo = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Demo (Tradovate simulated)", OffContent = "Real account" };
        var user = new TextBox { Text = p.TradovateUser, Watermark = "Tradovate user" };
        var pass = SecretBox(p, null, "password");
        var cid = new TextBox { Text = p.TradovateCid, Watermark = "CID (number)" };
        var sec = SecretBox(p, "apisecret", "API key secret");
        var products = new TextBox { Text = string.Join(", ", p.TradovateProducts), Watermark = "ES, NQ, MES, MNQ, CL…" };
        var allowReal = new CheckBox { Content = "Allow orders on real Tradovate accounts", IsChecked = p.AllowRealOrders };
        var confirm = new CheckBox { Content = "Ask for confirmation before each real order", IsChecked = p.ConfirmOrders };
        var maxQty = new NumericUpDown { Value = p.MaxOrderQuantity, Minimum = 0, Maximum = 10000, Increment = 1, FormatString = "0", Width = 140 };
        void Update()
        {
            allowReal.IsEnabled = demo.IsChecked != true;
            confirm.IsEnabled = demo.IsChecked != true && allowReal.IsChecked == true;
        }

        allowReal.IsCheckedChanged += (_, _) => Update();
        demo.IsCheckedChanged += (_, _) => Update();
        Update();
        apply.Add(() =>
        {
            p.Testnet = demo.IsChecked == true;
            p.TradovateUser = (user.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(pass.Text)) _svc.Connections.SetSecret(p, pass.Text);
            pass.Text = "";
            p.TradovateCid = (cid.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(sec.Text)) _svc.Connections.SetSecret(p, "apisecret", sec.Text.Trim());
            sec.Text = "";
            p.TradovateProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
            p.AllowRealOrders = !p.Testnet && allowReal.IsChecked == true;
            p.ConfirmOrders = confirm.IsChecked != false;
            p.MaxOrderQuantity = maxQty.Value ?? 0;
        });
        return Ui.V(14,
            Ui.H2("Tradovate account"),
            Note(MaterialIconKind.InformationOutline, "A direct connection to Tradovate's official API. Your accounts appear in the Chart Trader and in Account, and can be traded from any CME futures chart (a Rithmic one included). The demo ones trade directly; the real ones need the authorization below."),
            Note(MaterialIconKind.AlertOutline, "Market data over the API: Tradovate only provides it if your account has CME data for API enabled (a separate CME license). Without it you can still trade, using the Rithmic charts or another connection."),
            demo,
            TwoColumns(Ui.Field("User", user), Ui.Field("Password", pass)),
            TwoColumns(Ui.Field("API key CID", cid), Ui.Field("API key secret", sec)),
            Ui.H(16, Link("Where do I get the API key? (Settings → API Access)", "https://trader.tradovate.com/"), Link("API documentation", "https://partner.tradovate.com")),
            Ui.Field("Products for the search", products, "The front-month contract is used for each one (ES → ESZ6)."),
            Ui.Divider(),
            allowReal,
            confirm,
            Ui.Field("Maximum quantity per order (0 = no limit)", maxQty),
            Note(MaterialIconKind.ShieldCheckOutline, "Tradovate allows only one data session per user: if you open Tradovate Trader with the same user, it may drop the Trading Hub one (and the other way round)."));
    }

    /// <summary>cTrader: Open API application (Client ID + Secret) and account token (OAuth or Playground).</summary>
    private Control CTraderSection(ConnectionProfile p, List<Action> apply)
    {
        const string redirect = "http://127.0.0.1:53682/ctrader";
        var demo = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Demo account", OffContent = "Real account" };
        var clientId = new TextBox { Text = p.CTraderClientId, Watermark = "Your application's Client ID" };
        var secret = SecretBox(p, null, "Your application's Secret");
        var token = SecretBox(p, "token", "access token (or click “Connect with cTrader”)");
        var login = new NumericUpDown { Value = p.CTraderLogin, Minimum = 0, Maximum = 999999999999, FormatString = "0", Watermark = "the first one", HorizontalAlignment = HorizontalAlignment.Stretch };
        var oauthStatus = new TextBlock { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };
        var connect = Ui.Button("Connect with cTrader", MaterialIconKind.Login, () => { }, "primary");
        connect.Click += async (_, _) =>
        {
            string id = (clientId.Text ?? "").Trim();
            string sec = !string.IsNullOrWhiteSpace(secret.Text) ? secret.Text.Trim() : _svc.Connections.GetSecret(p);
            if (id.Length == 0 || sec.Length == 0)
            {
                oauthStatus.Text = "Fill in your application's Client ID and Secret first.";
                return;
            }

            connect.IsEnabled = false;
            oauthStatus.Text = "The browser opened: sign in with your cTrader ID and authorize Trading Hub. Waiting…";
            try
            {
                var (access, refresh) = await CTraderOAuthAsync(id, sec, redirect);
                p.CTraderClientId = id;
                _svc.Connections.SetSecret(p, sec);
                _svc.Connections.SetSecret(p, "token", access);
                if (refresh.Length > 0) _svc.Connections.SetSecret(p, "refresh", refresh);
                _svc.Connections.Save();
                _svc.Connections.Invalidate(p);
                token.Watermark = "•••••••• (stored securely)";
                oauthStatus.Text = "Done: Trading Hub is authorized. Click “Test connection”.";
            }
            catch (Exception ex)
            {
                oauthStatus.Text = "Could not authorize: " + ex.Message;
            }
            finally
            {
                connect.IsEnabled = true;
            }
        };
        var copy = Ui.Button("Copy", MaterialIconKind.ContentCopy, () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) _ = cb.SetTextAsync(redirect);
        }, "ghost");
        apply.Add(() =>
        {
            p.Testnet = demo.IsChecked == true;
            p.CTraderClientId = (clientId.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            if (!string.IsNullOrWhiteSpace(token.Text)) _svc.Connections.SetSecret(p, "token", token.Text.Trim());
            token.Text = "";
            p.CTraderLogin = (long)(login.Value ?? 0);
        });
        return Ui.V(14,
            Ui.H2("cTrader Open API"),
            Note(MaterialIconKind.InformationOutline, "Free with any broker that has cTrader (IC Markets, Pepperstone, FxPro, Fusion Markets…) and a demo account: index CFDs (US100/NAS100, US500, GER40…), forex, gold and oil, with years of tick-by-tick history."),
            Ui.H3("1 · Your application (just once)"),
            Note(MaterialIconKind.ShieldKeyOutline, "Create an application at openapi.ctrader.com (sign in with your cTrader ID) and add this redirect address. Then copy the Client ID and the Secret here."),
            Ui.H(8, new TextBlock { Text = redirect, FontFamily = new FontFamily(Plat.Mono), VerticalAlignment = VerticalAlignment.Center }, copy, Link("Open openapi.ctrader.com", "https://openapi.ctrader.com/apps")),
            TwoColumns(Ui.Field("Client ID", clientId), Ui.Field("Secret", secret)),
            Ui.H3("2 · Authorize your account"),
            Ui.H(10, connect, demo),
            oauthStatus,
            Ui.Field("Or paste an access token (the one from your application's “Playground”)", token),
            Ui.Field("Account number (cTrader login)", login, "If the token has several accounts, the one you want to use. 0 = the first demo/real one."));
    }

    /// <summary>cTrader OAuth: opens the browser and waits for the code at the local redirect address.</summary>
    private static async Task<(string Access, string Refresh)> CTraderOAuthAsync(string clientId, string secret, string redirect)
    {
        var uri = new Uri(redirect);
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{uri.Port}/");
        listener.Start();
        Shell.Open(TradingHub.Feeds.CTrader.CTraderAuth.AuthorizeUrl(clientId, redirect));
        var wait = listener.GetContextAsync();
        if (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromMinutes(5))) != wait) throw new TimeoutException("the authorization never arrived (5 minutes)");
        var ctx = await wait;
        string? code = ctx.Request.QueryString["code"];
        string? error = ctx.Request.QueryString["error"] ?? ctx.Request.QueryString["error_description"];
        var html = System.Text.Encoding.UTF8.GetBytes(code != null
            ? "<html><body style='font-family:-apple-system;padding:40px'><h2>Done</h2><p>Trading Hub is authorized. You can close this tab and go back to Trading Hub.</p></body></html>"
            : "<html><body style='font-family:-apple-system;padding:40px'><h2>Not authorized</h2><p>Go back to Trading Hub and try again.</p></body></html>");
        ctx.Response.ContentType = "text/html; charset=utf-8";
        await ctx.Response.OutputStream.WriteAsync(html);
        ctx.Response.Close();
        if (code == null) throw new InvalidOperationException(error ?? "cTrader did not send the code");
        var (access, refresh, _) = await TradingHub.Feeds.CTrader.CTraderAuth.ExchangeAsync(clientId, secret, code, redirect, CancellationToken.None);
        return (access, refresh);
    }

    /// <summary>OANDA: personal token and practice (free) or real account.</summary>
    private Control OandaSection(ConnectionProfile p, List<Action> apply)
    {
        var practice = new ToggleSwitch { IsChecked = p.Testnet, OnContent = "Practice account (free)", OffContent = "Real account" };
        var token = SecretBox(p, null, "API token (Manage API Access)");
        var account = new TextBox { Text = p.OandaAccount, Watermark = "101-001-… (empty = the first one)" };
        apply.Add(() =>
        {
            p.Testnet = practice.IsChecked == true;
            if (!string.IsNullOrWhiteSpace(token.Text)) _svc.Connections.SetSecret(p, token.Text.Trim());
            token.Text = "";
            p.OandaAccount = (account.Text ?? "").Trim();
        });
        return Ui.V(14,
            Ui.H2("OANDA (API v20)"),
            Note(MaterialIconKind.InformationOutline, "Free with a practice account: index CFDs (NAS100_USD = Nasdaq 100, SPX500_USD = S&P 500, US30_USD, DE30_EUR…), gold, oil and forex, with years of history in 5 second candles."),
            practice,
            TwoColumns(Ui.Field("Token", token), Ui.Field("Account", account)),
            Ui.H(16, Link("Create a practice account", "https://www.oanda.com/demo-account/"), Link("Generate the token (Manage API Access)", "https://www.oanda.com/account/tpa/personal_token")),
            Note(MaterialIconKind.ShieldKeyOutline, "The token is stored in the Keychain (Credential Manager on Windows). Trading Hub uses it for data only (it sends no orders to OANDA)."));
    }

    /// <summary>Alpaca: keys of the practice account (free SIP history from 2016).</summary>
    private Control AlpacaSection(ConnectionProfile p, List<Action> apply)
    {
        var key = new TextBox { Text = p.ApiKey, Watermark = "API key ID" };
        var secret = SecretBox(p, null, "secret key");
        var live = new ComboBox { MinWidth = 260 };
        live.Items.Add(new ComboBoxItem { Content = "IEX (free, one exchange)", Tag = "iex" });
        live.Items.Add(new ComboBoxItem { Content = "SIP (every exchange, paid plan)", Tag = "sip" });
        live.SelectedItem = live.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == p.AlpacaLiveFeed) ?? live.Items[0];
        apply.Add(() =>
        {
            p.ApiKey = (key.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            secret.Text = "";
            p.AlpacaLiveFeed = (live.SelectedItem as ComboBoxItem)?.Tag as string ?? "iex";
        });
        return Ui.V(14,
            Ui.H2("Alpaca (US stocks and ETFs)"),
            Note(MaterialIconKind.InformationOutline, "With the practice account (free, from any country) tick-by-tick history for EVERY US exchange since 2016 is free (except the last 15 minutes). SPY = S&P 500, QQQ = Nasdaq 100, IWM = Russell 2000, DIA = Dow: real trades to study the indices."),
            TwoColumns(Ui.Field("API key ID", key), Ui.Field("Secret key", secret)),
            Ui.Field("Live data", live),
            Ui.H(16, Link("Create a practice account", "https://app.alpaca.markets/signup"), Link("Where the keys are", "https://app.alpaca.markets/paper/dashboard/overview")),
            Note(MaterialIconKind.ShieldKeyOutline, "Trading Hub uses the keys for data only (it sends no orders to Alpaca)."));
    }

    /// <summary>London Strategic Edge: one free API key.</summary>
    /// <summary>Coinbase: an optional Exchange API key turns on the order-level (Level 3) feed.</summary>
    private Control CoinbaseSection(ConnectionProfile p, List<Action> apply)
    {
        var key = new TextBox { Text = p.ApiKey, Watermark = "API key" };
        var secret = SecretBox(p, null, "API secret");
        var pass = SecretBox(p, "passphrase", "Passphrase");
        apply.Add(() =>
        {
            p.ApiKey = (key.Text ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(secret.Text)) _svc.Connections.SetSecret(p, secret.Text.Trim());
            if (!string.IsNullOrWhiteSpace(pass.Text)) _svc.Connections.SetSecret(p, "passphrase", pass.Text.Trim());
            secret.Text = "";
            pass.Text = "";
        });
        return Ui.V(14,
            Ui.H2("Order-level data (optional)"),
            Note(MaterialIconKind.InformationOutline, "Coinbase only publishes every individual order (Level 3) to signed-in clients. With a Coinbase Exchange API key the heatmap's book events show real orders being added, pulled and filled instead of guesses from the order book. Without a key everything else keeps working."),
            Ui.Field("API key", key),
            Ui.Field("API secret", secret),
            Ui.Field("Passphrase", pass),
            Ui.H(16, Link("Create a key (Coinbase Exchange)", "https://exchange.coinbase.com/profile/api")),
            Note(MaterialIconKind.ShieldKeyOutline, "Give the key View permission only: Trading Hub never trades with it. The secret and passphrase are stored in the Keychain (Windows Credential Manager), never in a file. Use Test connection to check the key."));
    }

    /// <summary>Bybit: the long tick history built in the background from Bybit's free public daily files.</summary>
    private Control BybitHistorySection(ConnectionProfile p, List<Action> apply)
    {
        var symbols = new TextBox { Text = string.Join(", ", p.BybitBackfill), Watermark = "BTCUSDT, ETHUSDT…" };
        var days = new NumericUpDown { Value = p.BackfillDays, Minimum = 0, Maximum = 1500, Increment = 10, FormatString = "0", Width = 140 };
        apply.Add(() =>
        {
            p.BybitBackfill = (symbols.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
            p.BackfillDays = (int)(days.Value ?? 0);
        });
        return Ui.V(14,
            Ui.H2("Tick history"),
            Note(MaterialIconKind.InformationOutline, "Bybit publishes every trade of every finished day as a free file, going back years. In the background, a couple of days every 20 minutes, Trading Hub downloads the ones below into your tick folders, so charts, backtests and the event tester have months of data instead of weeks. A day is 20 to 150 MB to download and about 6 MB stored. Set the days to 0 to turn it off."),
            Ui.Field("Perpetuals to keep", symbols),
            Ui.Field("How many days back", days));
    }

    private Control LseSection(ConnectionProfile p, List<Action> apply)
    {
        var key = SecretBox(p, null, "API key");
        var keep = new TextBox { Text = string.Join(", ", p.LseBackfill), Watermark = "ES.F, NQ.F, GC.F…" };
        ToolTip.SetTip(keep, "Futures whose finished days are downloaded in the background, a few a round, so their tick history keeps growing (the free archive only keeps the last weeks). Available: " + string.Join(" ", TradingHub.Feeds.LondonStrategicEdge.LseFeed.FuturesSymbols));
        apply.Add(() =>
        {
            if (!string.IsNullOrWhiteSpace(key.Text)) _svc.Connections.SetSecret(p, key.Text.Trim());
            key.Text = "";
            p.LseBackfill = (keep.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.ToUpperInvariant()).Where(x => TradingHub.Feeds.LondonStrategicEdge.LseFeed.FuturesInstrument(x) != null).Distinct().ToList();
        });
        return Ui.V(14,
            Ui.H2("London Strategic Edge"),
            Note(MaterialIconKind.InformationOutline, "Free stocks, ETFs, forex, crypto, commodities, indices and the ES, NQ, GC and SI futures. Live ticks over a websocket (one connection per key, up to 16 symbols), tick history and candles from the service's archive. The free plan allows 60 calls a minute and a weekly download allowance. The picker lists the symbols the service streams; which of them your free plan serves is up to the service."),
            Ui.Field("API key", key),
            Ui.Field("Keep the tick history of these futures", keep, "Their finished days are downloaded in the background (a few days every 20 minutes) into the tick folders, so the history keeps growing. Leave empty to turn it off."),
            Ui.H(16, Link("Get a free key", "https://londonstrategicedge.com/data")),
            Note(MaterialIconKind.ShieldKeyOutline, "The key is stored in the Keychain (Windows Credential Manager on Windows), never in a file. Trading Hub uses it for data only."));
    }

    /// <summary>Databento: API key (CME futures history from 2010, pay as you go with gift credits).</summary>
    private Control DatabentoSection(ConnectionProfile p, List<Action> apply)
    {
        var key = SecretBox(p, null, "API key (db-…)");
        var level2 = new ToggleSwitch { IsChecked = p.DatabentoLevel2, OnContent = "Live trades + Level 2 (MBP-10, top 10 levels)", OffContent = "Live trades only (cheaper)" };
        var mbo = new ToggleSwitch { IsChecked = p.DatabentoMbo, OnContent = "Level 3: every order (MBO) — overrides Level 2, streams the most data", OffContent = "No order-by-order data" };
        var products = new TextBox { Text = string.Join(", ", p.DatabentoProducts), Watermark = "ES, NQ, MES, MNQ, GC, MGC…" };
        apply.Add(() =>
        {
            if (!string.IsNullOrWhiteSpace(key.Text)) _svc.Connections.SetSecret(p, key.Text.Trim());
            key.Text = "";
            p.DatabentoLevel2 = level2.IsChecked == true;
            p.DatabentoMbo = mbo.IsChecked == true;
            p.DatabentoProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToUpperInvariant()).Distinct().ToList();
        });
        return Ui.V(14,
            Ui.H2("Databento"),
            Note(MaterialIconKind.InformationOutline, "Tick-by-tick history for CME futures (NQ, ES, MNQ, MES, CL, GC…) since 2010, with each trade's real aggressor side: exactly what Rithmic does not keep. It is pay per use; new accounts come with US$ 125 of free credits."),
            Ui.Field("API key", key),
            Ui.H(16, Link("Create an account (with free credits)", "https://databento.com/signup"), Link("Prices", "https://databento.com/pricing")),
            Ui.H2("Live data"),
            Note(MaterialIconKind.InformationOutline, "Live CME futures on charts, DOM and tape (continuous front month, it rolls on its own). Live is a separate Databento licence (CME, non-professional) plus usage; Level 2 streams much more data than trades only, so it costs more. “Test connection” tells you whether your key has live access."),
            level2,
            mbo,
            Ui.Field("Products", products),
            Note(MaterialIconKind.ShieldCheckOutline, "It is used from Data → “CME futures with Databento”: Trading Hub computes the cost first and only downloads if you confirm. It is stored separately (“Databento” in Data) and in backtests you choose Databento only, Rithmic only or both combined (Rithmic first, with Databento filling the gaps)."));
    }

    /// <summary>dxFeed: dxLink address and token of your plan, Level 2 source and products.</summary>
    private Control DxFeedSection(ConnectionProfile p, List<Action> apply)
    {
        var url = new TextBox { Text = p.DxFeedUrl, Watermark = TradingHub.Feeds.DxFeed.DxFeedSettings.DemoUrl };
        var token = SecretBox(p, null, "token from your dxFeed plan (empty on the demo server)");
        var source = new TextBox { Text = p.DxFeedDomSource, Watermark = "glbx" };
        var products = new TextBox { Text = string.Join(", ", p.DxFeedProducts), Watermark = "ES, NQ, MES, MNQ, GC, MGC, /CLZ26:XNYM…" };
        apply.Add(() =>
        {
            p.DxFeedUrl = string.IsNullOrWhiteSpace(url.Text) ? TradingHub.Feeds.DxFeed.DxFeedSettings.DemoUrl : url.Text.Trim();
            if (!string.IsNullOrWhiteSpace(token.Text)) _svc.Connections.SetSecret(p, token.Text.Trim());
            token.Text = "";
            p.DxFeedDomSource = string.IsNullOrWhiteSpace(source.Text) ? "glbx" : source.Text.Trim();
            p.DxFeedProducts = (products.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        });
        return Ui.V(14,
            Ui.H2("dxFeed (dxLink)"),
            Note(MaterialIconKind.InformationOutline, "Live CME futures with every trade (aggressor side included) and the full order book (Level 2) in the DOM. You need the dxLink address and token of your own dxFeed plan; the demo server works without a token but its data is delayed. There is no history download: the chart fills from the moment it connects."),
            TwoColumns(Ui.Field("dxLink address", url), Ui.Field("Token", token)),
            TwoColumns(Ui.Field("Level 2 source", source), Ui.Field("Products", products)),
            Note(MaterialIconKind.InformationOutline, "Indices (ES, NQ, YM, RTY and micros) and gold (GC, MGC) use the front month on their own. Anything else, type its exact dxFeed symbol, for example /CLZ26:XNYM. glbx is CME Globex by price level (lower case: dxFeed rejects GLBX); “Test connection” tells you whether Level 2 is arriving."),
            Note(MaterialIconKind.ShieldKeyOutline, "The token is stored in the Credential Manager / Keychain. Trading Hub uses dxFeed for data only (it sends no orders)."));
    }

    private Control IbSection(ConnectionProfile p, List<Action> apply)
    {
        var host = new TextBox { Text = p.IbHost };
        var port = new NumericUpDown { Value = p.IbPort, Minimum = 1, Maximum = 65535, FormatString = "0" };
        var client = new NumericUpDown { Value = p.IbClientId, Minimum = 0, Maximum = 999, FormatString = "0" };
        apply.Add(() =>
        {
            p.IbHost = string.IsNullOrWhiteSpace(host.Text) ? "127.0.0.1" : host.Text.Trim();
            p.IbPort = (int)(port.Value ?? 7497);
            p.IbClientId = (int)(client.Value ?? 17);
        });
        return Ui.V(14,
            Ui.H2("Interactive Brokers (TWS / IB Gateway)"),
            Note(MaterialIconKind.InformationOutline, "Open TWS or IB Gateway and enable the API (Configure → API → Settings). The usual ports: 7497 TWS paper, 7496 TWS real, 4002 Gateway paper. For now Trading Hub only verifies the connection."),
            TwoColumns(Ui.Field("Host", host), Ui.Field("Port", port)),
            Ui.Field("Client ID", client));
    }
}
