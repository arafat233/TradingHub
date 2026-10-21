using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Dom;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.App.Views;

/// <summary>Right panel (ticket, DOM and tape) and everything that trades: orders, stop/targets and partials.</summary>
public sealed partial class TradingView
{
    private readonly DomView _dom = new();
    private readonly TapeView _tape = new();
    private int _lastTradeCount;
    private DepthSnapshot? _lastDepth;
    private bool _syncing;

    // ticket
    private readonly ComboBox _accountBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch };

    // the trading panel on the right is the same Chart Trader, tied to the active chart (so it never falls behind)
    private readonly Border _dockedTraderHost = new();
    private ChartTraderPanel? _dockedTrader;
    private readonly NumericUpDown _qty = new() { Value = 1, Minimum = 0, Increment = 1, FormatString = "0.####", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly WrapPanel _qtyPresets = new() { ItemSpacing = 6, LineSpacing = 6 };
    private readonly TextBlock _buyPrice = new() { FontSize = 11, Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _sellPrice = new() { FontSize = 11, Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Center };
    // fixed height of two lines: if it changed on every update, it would push the DOM up and down
    private readonly TextBlock _positionText = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Height = 34, MaxLines = 2 };
    private readonly TextBlock _accountWarning = new() { FontSize = 11, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private ChoiceButton<int>? _domStep;

    // automatic stop and targets
    private readonly CheckBox _bracketAuto = new() { Content = "Place them automatically on entry" };
    private readonly NumericUpDown _slTicks = new() { Minimum = 0, Maximum = 100000, Increment = 1, FormatString = "0", Width = 96 };
    private readonly StackPanel _targetRows = new() { Spacing = 6 };
    private readonly CheckBox _beAfterTp1 = new() { Content = "Stop to breakeven when target 1 fills" };
    private readonly TextBlock _bracketSummary = new() { FontSize = 11, Classes = { "faint" }, TextWrapping = TextWrapping.Wrap };
    private BracketTemplate? _template;

    private Button? _buyButton;

    // sections of the panel: Chart Trader, DOM and tape, one below the other. They share the height (with splitters to
    // change it) and each one scrolls inside; any of them can be pulled out into a separate window
    private readonly Grid _side = new() { Background = Ui.Bg1 };
    private Control? _ticketSection, _domSection, _tapeSection;
    private string _sideSig = "";
    private bool _sideVisible;
    private readonly Dictionary<string, MenuItem> _panelItems = new();
    private readonly HashSet<BracketManager> _hookedBrackets = new(ReferenceEqualityComparer.Instance);

    // ===================================================================== right panel

    private Control BuildRightPanel()
    {
        _ticketSection = BuildTicket();
        _domSection = BuildDomSection();
        _tapeSection = BuildTapeSection();

        _sideSplitter.DragCompleted += (_, _) =>
        {
            double w = _body.ColumnDefinitions[2].ActualWidth;
            if (w > 0)
            {
                _svc.Settings.SidePanelWidth = w;
                _svc.Settings.Save();
            }
        };
        return new Border { Child = _side, BorderBrush = Ui.Line, BorderThickness = new Thickness(1, 0, 0, 0) };
    }

    /// <summary>Section header: title, its own controls and a button to close it.</summary>
    private Control SectionHeader(string title, Action close, params Control[] extra)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in extra) right.Children.Add(c);
        right.Children.Add(Ui.IconButton(MaterialIconKind.Close, "Hide (it comes back from the panels button on the bar)", close, 15));
        var header = new DockPanel { Margin = new Thickness(14, 8, 8, 6) };
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        var t = Ui.H3(title);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(t);
        return header;
    }

    private Control BuildDomSection()
    {
        _domStep = new ChoiceButton<int>(MaterialIconKind.FormatLineSpacing,
            new[] { 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000 }.Select(n => (n, $"{n} tick{(n > 1 ? "s" : "")}")),
            "Group the DOM prices", n => $"×{n}");
        _domStep.MinHeight = 26;
        _domStep.Padding = new Thickness(8, 2);
        _domStep.Changed += n =>
        {
            if (_dom.Dom == null) return;
            _dom.Dom.Step = n;
            _dom.Dom.ResetVolume();
            _dom.Recenter();
        };

        var help = new Button { Content = Ui.Icon(MaterialIconKind.HelpCircleOutline, 15) }.Classes("icon");
        ToolTip.SetTip(help, "How to trade with the DOM");
        help.Flyout = new Flyout
        {
            Content = Ui.V(6,
                Ui.H3("Trade with the DOM"),
                Ui.Text("Click on Bid: buy limit (or stop if it is above the price)."),
                Ui.Text("Click on Ask: sell limit (or stop if it is below the price)."),
                Ui.Text("Right click or ⌥ click: cancels the orders at that price."),
                Ui.Text("Drag one of your own orders to move it."),
                Ui.Text("Wheel: scroll · center button: back to the price."),
                Ui.Faint("In Configure DOM you choose columns, sizes, colors and whether a double click is required.")).Also(v => v.Margin = new Thickness(12)),
        };

        var buttonsToggle = Ui.IconButton(MaterialIconKind.GestureTapButton, "Show or hide the execution buttons (market, limits, cancel, close)", ToggleDomButtons, 15);
        var header = SectionHeader("DOM", () => SetPanel("dom", false),
            _domStep,
            buttonsToggle,
            Ui.IconButton(MaterialIconKind.ImageFilterCenterFocus, "Center on the price", () => _dom.Recenter(), 15),
            Ui.IconButton(MaterialIconKind.Tune, "Configure DOM (columns, sizes, colors)", () => OpenDomSettings(), 15),
            BuildDetachButton("dom"),
            help);
        _dom.OrderRequested += OnDomOrder;
        _dom.Message += msg => _status(msg);
        _dom.OrderMoved += (o, price) => _ = MoveOrderAsync(_active, o, price);
        _dom.MinHeight = 80;
        _domButtons = BuildDomButtons();
        _domButtons.IsVisible = _svc.Settings.ShowDomButtons;
        var box = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        box.Children.Add(header);
        DockPanel.SetDock(_domButtons, Dock.Bottom);
        box.Children.Add(_domButtons);
        box.Children.Add(_dom);
        return new Border { Child = box, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    private Control? _domButtons;

    private void ToggleDomButtons()
    {
        var s = _svc.Settings;
        s.ShowDomButtons = !s.ShowDomButtons;
        s.Save();
        if (_domButtons != null) _domButtons.IsVisible = s.ShowDomButtons;
        _sideSig = ""; // the section's minimum height changes
        ApplyPanelVisibility();
    }

    /// <summary>
    /// DOM execution buttons (they trade the active chart, like the DOM): quantity, buy and sell at market, limits
    /// on the bid and on the ask, cancel, reverse and close. Fixed height: nothing moves with the prices.
    /// </summary>
    private Control BuildDomButtons()
    {
        Button B(string text, string cls, string tip, Action onClick)
        {
            var b = new Button
            {
                Content = new TextBlock { Text = text, FontSize = 11.5, FontWeight = cls is "buy" or "sell" ? FontWeight.SemiBold : FontWeight.Normal, HorizontalAlignment = HorizontalAlignment.Center },
                MinHeight = 30,
                Padding = new Thickness(2, 3),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            if (cls.Length > 0) b.Classes.Add(cls);
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => onClick();
            return b;
        }

        Grid Row(params Control[] cells)
        {
            var g = new Grid();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) g.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(6)));
                g.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                Grid.SetColumn(cells[i], g.ColumnDefinitions.Count - 1);
                g.Children.Add(cells[i]);
            }

            return g;
        }

        void AtBook(OrderDirections dir, bool ask)
        {
            if (_active == null || TradeData(_active) is not { } data) return;
            decimal price = ask ? data.BestAsk : data.BestBid;
            if (price <= 0) price = data.LastPrice;
            if (price <= 0)
            {
                _status("There is no book price for that order yet");
                return;
            }

            _ = TradeAsync(_active, OrderTypes.Limit, dir, price, QtyOf(_active));
        }

        // quantity and its shortcuts on a single row
        var qtyRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        qtyRow.Children.Add(_qty);
        _qtyPresets.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_qtyPresets, 2);
        qtyRow.Children.Add(_qtyPresets);
        ToolTip.SetTip(_qty, "Quantity for the DOM orders and for these buttons");

        var panel = Ui.V(6,
            qtyRow,
            Row(B("Buy MKT", "buy", "Buy at market", () => _ = TradeAsync(_active, OrderTypes.Market, OrderDirections.Buy, 0, QtyOf(_active))),
                B("Sell MKT", "sell", "Sell at market", () => _ = TradeAsync(_active, OrderTypes.Market, OrderDirections.Sell, 0, QtyOf(_active)))),
            Row(B("C. Bid", "buy-soft", "Buy limit at the bid (waits on the demand)", () => AtBook(OrderDirections.Buy, ask: false)),
                B("C. Ask", "buy-soft", "Buy limit at the ask (fills right away, with a price cap)", () => AtBook(OrderDirections.Buy, ask: true)),
                B("V. Bid", "sell-soft", "Sell limit at the bid (fills right away, with a price cap)", () => AtBook(OrderDirections.Sell, ask: false)),
                B("V. Ask", "sell-soft", "Sell limit at the ask (waits on the offer)", () => AtBook(OrderDirections.Sell, ask: true))),
            Row(B("Cancel", "", "Cancels every order", () => _ = AccountActionAsync(_active, a => a.CancelAll(), "Orders cancelled")),
                B("Reverse", "", "Flips the position", () => _ = AccountActionAsync(_active, a => a.Reverse(), "Position reversed")),
                B("Close", "primary", "Closes the whole position at market and cancels the orders", () => _ = AccountActionAsync(_active, a => a.Flatten(), "Position closed"))));
        panel.Margin = new Thickness(10, 8, 10, 10);
        return new Border { Child = panel, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0), Background = Ui.Bg1 };
    }

    private Control BuildTapeSection()
    {
        var header = SectionHeader("Time and sales tape", () => SetPanel("tape", false), BuildDetachButton("tape"));
        _tape.MinHeight = 50;
        var box = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        box.Children.Add(header);
        box.Children.Add(_tape);
        return new Border { Child = box, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
    }

    /// <summary>Toolbar button to show or hide the right panel and its sections.</summary>
    private Control BuildPanelsButton()
    {
        var menu = new MenuFlyout();
        MenuItem Toggle(string key, string text)
        {
            var mi = new MenuItem { Header = text };
            mi.Click += (_, _) => SetPanel(key, !PanelOn(key));
            _panelItems[key] = mi;
            menu.Items.Add(mi);
            return mi;
        }

        Toggle("side", "Right panel");
        menu.Items.Add(new Separator());
        Toggle("ticket", "Chart Trader");
        Toggle("dom", "DOM");
        Toggle("tape", "Time and sales tape");
        menu.Items.Add(new Separator());
        var cfg = new MenuItem { Header = "Configure DOM…", Icon = Ui.Icon(MaterialIconKind.Tune, 15) };
        cfg.Click += (_, _) => OpenDomSettings();
        menu.Items.Add(cfg);

        var b = new Button { Content = Ui.Icon(MaterialIconKind.PageLayoutSidebarRight, 18), Flyout = menu }.Classes("icon");
        ToolTip.SetTip(b, "Panels: ticket, DOM and tape");
        return b;
    }

    internal void TogglePanel(string key) => SetPanel(key, !PanelOn(key));

    internal Window? ShowDomSettings() => OpenDomSettings();

    private bool PanelOn(string key)
    {
        var s = _svc.Settings;
        return key switch
        {
            "side" => s.ShowSidePanel,
            "ticket" => s.ShowTicket,
            "dom" => s.ShowDom,
            "tape" => s.ShowTape,
            _ => false,
        };
    }

    /// <summary>Shows or hides a section (or the whole panel) and remembers it.</summary>
    internal void SetPanel(string key, bool on)
    {
        var s = _svc.Settings;
        switch (key)
        {
            case "side": s.ShowSidePanel = on; break;
            case "ticket": s.ShowTicket = on; break;
            case "dom": s.ShowDom = on; break;
            case "tape": s.ShowTape = on; break;
        }

        // showing a section also shows the panel
        if (on && key != "side") s.ShowSidePanel = true;
        s.Save();
        ApplyPanelVisibility();
    }

    private void ApplyPanelVisibility()
    {
        var s = _svc.Settings;
        if (_ticketSection == null || _domSection == null || _tapeSection == null) return;
        // it was hidden from its own window: the window is closed (and it comes back through here)
        foreach (var key in _panelWindows.Keys.ToList())
            if (!PanelOn(key))
            {
                DockSection(key);
                return;
            }

        var parts = new List<(string Key, Control Section)>();
        if (s.ShowTicket && !_panelWindows.ContainsKey("ticket")) parts.Add(("ticket", _ticketSection));
        if (s.ShowDom && !_panelWindows.ContainsKey("dom")) parts.Add(("dom", _domSection));
        if (s.ShowTape && !_panelWindows.ContainsKey("tape")) parts.Add(("tape", _tapeSection));
        LayoutSide(parts);

        bool any = s.ShowSidePanel && parts.Count > 0;
        var col = _body.ColumnDefinitions[2];
        col.MinWidth = any ? 240 : 0;
        col.MaxWidth = any ? 760 : 0;
        col.Width = any ? new GridLength(Math.Clamp(s.SidePanelWidth, 240, 760)) : new GridLength(0);
        _sideSplitter.IsVisible = any;
        if (_side.Parent is Control sideBorder) sideBorder.IsVisible = any;
        if (_sideVisible != any)
        {
            _sideVisible = any;
            foreach (var page in _pages) UpdateFrames(page);
        }

        foreach (var (key, mi) in _panelItems)
            mi.Icon = PanelOn(key) ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null;
    }

    private static double DefaultWeight(string key) => key switch { "ticket" => 1, "dom" => 1.5, _ => 0.7 };

    /// <summary>Puts into the panel the sections that belong there, with a splitter between each one (only if which ones belong changed).</summary>
    private void LayoutSide(List<(string Key, Control Section)> parts)
    {
        string sig = string.Join(",", parts.Select(p => p.Key));
        if (sig == _sideSig && parts.All(p => ReferenceEquals(p.Section.Parent, _side))) return;
        _sideSig = sig;
        _side.Children.Clear();
        _side.RowDefinitions.Clear();
        var weights = _svc.Settings.SideWeights;
        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                _side.RowDefinitions.Add(new RowDefinition(new GridLength(5)));
                var split = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Ui.Bg1 };
                split.DragCompleted += (_, _) => SaveSideWeights(parts);
                Grid.SetRow(split, _side.RowDefinitions.Count - 1);
                _side.Children.Add(split);
            }

            var (key, section) = parts[i];
            double weight = weights.TryGetValue(key, out var w) && w > 0 ? w : DefaultWeight(key);
            double min = key == "dom" && _svc.Settings.ShowDomButtons ? 290 : 90;
            _side.RowDefinitions.Add(new RowDefinition(new GridLength(weight, GridUnitType.Star)) { MinHeight = min });
            if (section.Parent is Panel old && !ReferenceEquals(old, _side)) old.Children.Remove(section);
            Grid.SetRow(section, _side.RowDefinitions.Count - 1);
            _side.Children.Add(section);
        }
    }

    /// <summary>Remembers how the height ended up split (the heights serve as proportions).</summary>
    private void SaveSideWeights(List<(string Key, Control Section)> parts)
    {
        foreach (var (key, section) in parts)
            if (section.Bounds.Height > 0) _svc.Settings.SideWeights[key] = Math.Round(section.Bounds.Height, 1);
        _svc.Settings.Save();
    }

    private Window? OpenDomSettings()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        var w = new DomSettingsWindow(_svc.Settings.Dom, () =>
        {
            _dom.ApplySettings(_svc.Settings.Dom);
            _svc.Settings.Save();
        });
        w.Show(owner);
        return w;
    }

    // ===================================================================== ticket

    private Control BuildTicket()
    {
        _accountBox.SelectionChanged += (_, _) =>
        {
            if (_syncing || _active == null || _accountBox.SelectedItem is not ComboBoxItem { Tag: AccountOption a } || a.Id == _active.AccountId) return;
            var chart = _active;
            // after the picker finishes closing (rebuilding it inside its own event left it stuck)
            Dispatcher.UIThread.Post(() => SetChartAccount(chart, a.Id));
        };
        _qty.ValueChanged += (_, _) =>
        {
            if (_dom.Dom != null) _dom.Dom.OrderQuantity = Qty();
            if (_syncing || _active == null) return;
            _active.Quantity = Qty();
            _active.Trader?.SyncQuantity();
            if (ReferenceEquals(_dockedTrader?.Chart, _active)) _dockedTrader!.SyncQuantity();
        };

        var buy = new Button { MinHeight = 48 }.Classes("buy");
        buy.Content = Ui.V(0, new TextBlock { Text = "BUY", FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center }, _buyPrice);
        buy.Click += async (_, _) => await TradeAsync(_active, OrderTypes.Market, OrderDirections.Buy, 0);
        _buyButton = buy;
        var sell = new Button { MinHeight = 48 }.Classes("sell");
        sell.Content = Ui.V(0, new TextBlock { Text = "SELL", FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center }, _sellPrice);
        sell.Click += async (_, _) => await TradeAsync(_active, OrderTypes.Market, OrderDirections.Sell, 0);
        var buySell = Ui.Row(10, sell, buy);

        Button Small(string text, string tip, Action a)
        {
            var b = new Button { Content = text, Padding = new Thickness(4, 5), FontSize = 12 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => a();
            return b;
        }

        var actions = Ui.Row(8,
            Small("Close", "Closes the whole position at market and cancels the orders", () => _ = AccountActionAsync(_active, acc => acc.Flatten(), "Position closed")),
            Small("Reverse", "Flips the position", () => _ = AccountActionAsync(_active, acc => acc.Reverse(), "Position reversed")),
            Small("Cancel", "Cancels every order", () => _ = AccountActionAsync(_active, acc => acc.CancelAll(), "Orders cancelled")));

        // partials: they close part of it at market; stop and targets adjust themselves
        var partials = Ui.Row(6,
            Small("25%", "Close 25% of the position at market", () => _ = BracketActionAsync(_active, b => b.ClosePartial(0.25m), "Close 25% of the position")),
            Small("50%", "Close half the position at market", () => _ = BracketActionAsync(_active, b => b.ClosePartial(0.5m), "Close half the position")),
            Small("75%", "Close 75% of the position at market", () => _ = BracketActionAsync(_active, b => b.ClosePartial(0.75m), "Close 75% of the position")),
            Small("BE", "Move the stop to breakeven (the entry price)", () => _ = BracketActionAsync(_active, b => b.MoveStopToBreakeven(), "Move the stop to breakeven")));

        var partialRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        partialRow.Children.Add(new TextBlock { Text = "Partial", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" }, Margin = new Thickness(0, 0, 10, 0) });
        Grid.SetColumn(partials, 1);
        partialRow.Children.Add(partials);

        // (the quantity and its shortcuts now live in the DOM buttons)
        var panel = Ui.V(10,
            _accountBox,
            _accountWarning,
            buySell,
            actions,
            partialRow,
            BuildBracketEditor(),
            _positionText);
        panel.Margin = new Thickness(14, 0, 14, 14);

        // (the old controls at the top are still built because the DOM and the shortcuts use their quantity, but the Chart Trader is what is shown)
        _ = panel;
        var box = new DockPanel();
        var header = SectionHeader("Chart Trader", () => SetPanel("ticket", false), BuildDetachButton("ticket"));
        DockPanel.SetDock(header, Dock.Top);
        box.Children.Add(header);
        box.Children.Add(_dockedTraderHost);
        return box;
    }

    private Control BuildBracketEditor()
    {
        _bracketAuto.IsCheckedChanged += (_, _) => EditTemplate(t => t.Enabled = _bracketAuto.IsChecked == true);
        _slTicks.ValueChanged += (_, _) => EditTemplate(t => t.StopTicks = (int)(_slTicks.Value ?? 0));
        _beAfterTp1.IsCheckedChanged += (_, _) => EditTemplate(t => t.BreakevenAfterFirstTarget = _beAfterTp1.IsChecked == true);
        ToolTip.SetTip(_bracketAuto, "When a position is opened (or an entry order fills) the stop and the targets below are set, in ticks from the entry.");

        var slRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        slRow.Children.Add(new TextBlock { Text = "Stop loss (ticks)", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } });
        Grid.SetColumn(_slTicks, 1);
        slRow.Children.Add(_slTicks);

        var addTarget = Ui.Button("Target", MaterialIconKind.Plus, () => EditTemplate(t =>
        {
            if (t.Targets.Count >= 3) return;
            int last = t.Targets.Count > 0 ? t.Targets.Max(x => x.Ticks) : t.StopTicks;
            t.Targets.Add(new BracketTarget { Ticks = Math.Max(1, last * 2), Percent = 0 });
            Rebalance(t);
        }, rebuild: true));
        ToolTip.SetTip(addTarget, "Up to 3 partial targets; the percentages split the position");

        var apply = Ui.Button("Apply to the position", MaterialIconKind.ShieldOutline, () => _ = BracketActionAsync(_active, b => b.ApplyTemplate(), "Set the stop and the targets on the position"));
        ToolTip.SetTip(apply, "It replaces the open position's stop and targets with the template's");

        var content = Ui.V(10,
            _bracketAuto,
            slRow,
            new TextBlock { Text = "Targets (ticks · % of the position)", Classes = { "muted" } },
            _targetRows,
            Ui.H(8, addTarget, apply),
            _beAfterTp1,
            Ui.Faint("On the chart: drag the position's or a pending order's SL / TP buttons to set them by hand. Drag from the position's line: towards profit creates a target; towards loss, the stop."));
        foreach (var tb in content.Children.OfType<TextBlock>()) tb.TextWrapping = TextWrapping.Wrap;
        return new Expander
        {
            Header = Ui.H(8, Ui.Icon(MaterialIconKind.TargetVariant, 15), new TextBlock { Text = "Stop and targets (OCO)", VerticalAlignment = VerticalAlignment.Center }, _bracketSummary),
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = false,
        };
    }

    /// <summary>Spreads 100% among the targets (the last one takes the remainder).</summary>
    private static void Rebalance(BracketTemplate t)
    {
        if (t.Targets.Count == 0) return;
        decimal each = Math.Floor(100m / t.Targets.Count);
        for (int i = 0; i < t.Targets.Count; i++) t.Targets[i].Percent = i == t.Targets.Count - 1 ? 100 - each * (t.Targets.Count - 1) : each;
    }

    private void EditTemplate(Action<BracketTemplate> edit, bool rebuild = false)
    {
        if (_syncing || _template == null) return;
        edit(_template);
        _svc.Settings.Save();
        if (rebuild) LoadTemplate();
        else UpdateBracketSummary();
    }

    private void LoadTemplate()
    {
        var t = _template;
        if (t == null) return;
        _syncing = true;
        _bracketAuto.IsChecked = t.Enabled;
        _slTicks.Value = t.StopTicks;
        _beAfterTp1.IsChecked = t.BreakevenAfterFirstTarget;
        _targetRows.Children.Clear();
        for (int i = 0; i < t.Targets.Count; i++)
        {
            var target = t.Targets[i];
            var ticks = new NumericUpDown { Minimum = 1, Maximum = 100000, Increment = 1, FormatString = "0", Value = target.Ticks, MinWidth = 80 };
            var pct = new NumericUpDown { Minimum = 1, Maximum = 100, Increment = 5, FormatString = "0", Value = target.Percent, MinWidth = 80 };
            ticks.ValueChanged += (_, _) => EditTemplate(_ => target.Ticks = (int)(ticks.Value ?? 1));
            pct.ValueChanged += (_, _) => EditTemplate(_ => target.Percent = pct.Value ?? 100);
            var remove = Ui.IconButton(MaterialIconKind.Close, "Remove this target", () => EditTemplate(tt =>
            {
                tt.Targets.Remove(target);
                Rebalance(tt);
            }, rebuild: true), 14);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,6,*,Auto") };
            row.Children.Add(new TextBlock { Text = $"TP{i + 1}", Width = 34, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
            Grid.SetColumn(ticks, 1);
            row.Children.Add(ticks);
            Grid.SetColumn(pct, 3);
            row.Children.Add(pct);
            Grid.SetColumn(remove, 4);
            row.Children.Add(remove);
            _targetRows.Children.Add(row);
        }

        _syncing = false;
        UpdateBracketSummary();
    }

    private void UpdateBracketSummary()
    {
        var t = _template;
        _bracketSummary.Text = t == null || !t.Enabled ? "" : $"· {t.StopTicks}t / {string.Join(" ", t.Targets.Select(x => x.Ticks + "t"))}";
    }

    // ===================================================================== link with the active chart

    private void HookBrackets(BracketManager m)
    {
        if (!_hookedBrackets.Add(m)) return;
        m.Message += msg => Dispatcher.UIThread.Post(() => _status(msg));
        m.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            foreach (var c in AllCharts.Where(c => ReferenceEquals(c.Model?.Brackets, m))) c.Model?.Invalidate();
            _dom.InvalidateVisual();
        });
    }

    private void BindSidePanel(ChartTab tab, bool force)
    {
        if (tab.Model == null) return;
        var data = tab.Model.Data;
        var account = AccountOf(tab)!;
        tab.Model.Account = account;
        tab.Model.Brackets = _svc.BracketsFor(account);
        if (force || _dom.Dom?.Data != data || _dom.Dom?.Broker != account)
        {
            _dom.Dom = new DomModel(data, _svc.Settings.Dom) { Broker = account, Brackets = tab.Model.Brackets, OrderQuantity = QtyOf(tab), Step = 1 };
            _domStep?.Select(1);
            _tape.Tape = new TapeModel(data) { TimeZone = tab.Model.Settings.TimeZone };
        }

        UpdatePanelTitles();

        // the Chart Trader on the right follows the active chart
        if (!ReferenceEquals(_dockedTrader?.Chart, tab))
        {
            _dockedTrader = new ChartTraderPanel(this, tab, docked: true);
            _dockedTraderHost.Child = _dockedTrader;
        }

        _dockedTrader.Rebind();

        _syncing = true;
        Ui.SyncAccounts(_accountBox, AccountOptions(tab, data), tab.AccountId);
        _syncing = false;
        _accountWarning.IsVisible = account.IsReal;
        _accountWarning.Text = "REAL account: the orders go to the market with your money.";

        // suggested quantities based on the instrument
        decimal step = data.Instrument.VolumeStep;
        decimal[] presets = step >= 1 ? new[] { 1m, 2m, 3m, 5m, 10m } : new[] { step, step * 10, step * 100, step * 1000 }.Where(v => v > 0).ToArray();
        _qtyPresets.Children.Clear();
        foreach (var v in presets)
        {
            var b = new Button { Content = v.ToString("0.####", CultureInfo.InvariantCulture), Padding = new Thickness(7, 3), MinHeight = 26, FontSize = 11.5 };
            b.Click += (_, _) => _qty.Value = v;
            _qtyPresets.Children.Add(b);
        }

        _qty.Increment = Math.Max(step, 0.0001m);
        _syncing = true;
        if (tab.Quantity <= 0) tab.Quantity = presets[0];
        _qty.Value = tab.Quantity;
        _syncing = false;
        if (_dom.Dom != null) _dom.Dom.OrderQuantity = tab.Quantity;

        _template = _svc.Settings.BracketTemplateFor(data.Instrument.Key);
        LoadTemplate();
        _dom.InvalidateVisual();
        _tape.InvalidateVisual();
    }

    private decimal Qty() => Math.Max(0, (decimal)(_qty.Value ?? 1));

    /// <summary>Quantity to trade from a chart: its Chart Trader's.</summary>
    private decimal QtyOf(ChartTab? chart) => chart?.Quantity > 0 ? chart.Quantity : Qty();

    /// <summary>Data of the instrument it executes on (the cross trading one, if there is one).</summary>
    internal TradingHub.Data.InstrumentData? TradeData(ChartTab chart) => chart.CrossData ?? chart.Model?.Data;

    /// <summary>Account a chart's orders trade on (on the cross trading instrument, if there is one).</summary>
    internal ITradingAccount? AccountOf(ChartTab? chart) =>
        chart?.Model == null ? null : _svc.AccountFor(chart.CrossData ?? chart.Model.Data, chart.AccountId);

    /// <summary>The chart draws the position and the orders of the account it executes on.</summary>
    internal void ApplyTradingTarget(ChartTab chart)
    {
        if (chart.Model == null || AccountOf(chart) is not { } acc) return;
        chart.Model.Account = acc;
        chart.Model.Brackets = _svc.BracketsFor(acc);
        chart.Model.Invalidate();
    }

    /// <summary>Cross trading: this chart's orders go to another instrument (null = the chart's own).</summary>
    internal async Task SetCrossTradingAsync(ChartTab chart, string? symbol, bool save = true)
    {
        if (chart.Model == null) return;
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Equals(chart.Symbol, StringComparison.OrdinalIgnoreCase))
        {
            chart.CrossSymbol = null;
            chart.CrossData = null;
        }
        else
        {
            var list = await InstrumentPicker.InstrumentsAsync(_svc, chart.FeedId);
            var inst = list.FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            if (inst == null)
            {
                _status($"{symbol} was not found on this connection");
                return;
            }

            chart.CrossSymbol = inst.Symbol;
            chart.CrossData = _svc.Open(chart.FeedId, inst);
        }

        ApplyTradingTarget(chart);
        if (ReferenceEquals(chart, _active)) BindSidePanel(chart, force: true);
        chart.Trader?.Rebind();
        if (save) Workspace.Save(this);
        _status(chart.CrossSymbol != null
            ? $"Cross trading: you watch {chart.Symbol} and this chart's orders go to {chart.CrossSymbol}"
            : $"This chart's orders go to {chart.Symbol}");
    }

    internal void SetChartAccount(ChartTab chart, string accountId)
    {
        chart.AccountId = accountId;
        ApplyTradingTarget(chart);
        if (ReferenceEquals(chart, _active)) BindSidePanel(chart, force: true);
        chart.Trader?.Rebind();
        Workspace.Save(this);
    }

    internal void SetTraderVisible(ChartTab chart, bool visible)
    {
        chart.TraderVisible = visible;
        chart.Trader?.ApplyVisible();
        chart.Trader?.Refresh();
        Workspace.Save(this);
    }

    /// <summary>The quantity in the Chart Trader changed: if it is the active chart, the DOM and the ticket follow it.</summary>
    internal void OnChartQuantityChanged(ChartTab chart)
    {
        // both panels of the same chart (its own and the one on the right) show the same quantity
        chart.Trader?.SyncQuantity();
        if (ReferenceEquals(_dockedTrader?.Chart, chart)) _dockedTrader!.SyncQuantity();
        if (!ReferenceEquals(chart, _active)) return;
        _syncing = true;
        _qty.Value = chart.Quantity;
        _syncing = false;
        if (_dom.Dom != null) _dom.Dom.OrderQuantity = chart.Quantity;
    }

    private ITradingAccount? Account => AccountOf(_active);

    // ===================================================================== trading

    /// <summary>Confirms an order on real accounts (if confirmation is enabled).</summary>
    private async Task<bool> ConfirmAsync(ChartTab chart, ITradingAccount acc, string description, bool checkQuantity = true, decimal qty = 0)
    {
        if (!acc.IsReal) return true;
        var profile = _svc.Connections.FindForAccount(chart.AccountId);
        if (checkQuantity && profile?.MaxOrderQuantity > 0 && (qty > 0 ? qty : QtyOf(chart)) > profile.MaxOrderQuantity)
        {
            _status($"Order blocked: it exceeds the configured maximum quantity ({profile.MaxOrderQuantity}).");
            return false;
        }

        if (profile?.ConfirmOrders == false && !_svc.Settings.ConfirmRealOrders) return true;
        var owner = chart.Window ?? TopLevel.GetTopLevel(this) as Window;
        return owner != null && await ConfirmDialog.ShowAsync(owner, "Confirm REAL order", $"{description}\nCuenta: {acc.Name}", "Send order", danger: true);
    }

    private static bool IsEntry(ITradingAccount acc, OrderDirections dir) =>
        acc.Position.Volume == 0 || (dir == OrderDirections.Buy) == (acc.Position.Volume > 0);

    internal async Task<Order?> TradeAsync(ChartTab? chart, OrderTypes type, OrderDirections dir, decimal price, decimal? quantity = null)
    {
        var acc = AccountOf(chart);
        if (acc == null || chart == null) return null;
        decimal qty = quantity is > 0 ? quantity.Value : QtyOf(chart);
        if (qty <= 0)
        {
            _status("Enter a quantity greater than zero");
            return null;
        }

        string desc = $"{(dir == OrderDirections.Buy ? "BUY" : "SELL")} {type switch { OrderTypes.Market => "at market", OrderTypes.Limit => "limit", _ => "stop" }} {qty} {acc.Instrument.Symbol}{(price > 0 ? " @ " + acc.Instrument.FormatPrice(price) : "")}";
        if (!await ConfirmAsync(chart, acc, desc, qty: qty)) return null;
        bool entry = IsEntry(acc, dir);
        var order = type switch
        {
            OrderTypes.Market => acc.Market(dir, qty),
            OrderTypes.Limit => acc.Limit(dir, qty, price),
            _ => acc.Stop(dir, qty, price),
        };

        // entry order with the active template: it carries the stop and the targets attached
        var br = _svc.BracketsFor(acc);
        if (type != OrderTypes.Market && entry && br.Template.Enabled && order.State == OrderStates.Active) br.AttachTemplate(order);
        _status(desc + " sent");
        chart.Model?.Invalidate();
        return order;
    }

    private async Task MoveOrderAsync(ChartTab? chart, Order order, decimal price)
    {
        var acc = AccountOf(chart);
        if (acc == null || chart == null) return;
        string desc = $"Move {(order.Direction == OrderDirections.Buy ? "buy" : "sell")} order to {acc.Instrument.FormatPrice(price)}";
        if (!await ConfirmAsync(chart, acc, desc, checkQuantity: false)) return;
        _status(_svc.BracketsFor(acc).Move(order, price));
        chart.Model?.Invalidate();
        _dom.InvalidateVisual();
    }

    private async Task OnBracketDroppedAsync(ChartTab chart, BracketDrag d, decimal price)
    {
        var acc = AccountOf(chart);
        if (acc == null) return;
        var br = _svc.BracketsFor(acc);
        string msg;
        if (d.Pending != null) msg = br.MovePending(d.Pending, d.Index, price);
        else if (d.Entry != null) msg = br.AttachToEntry(d.Entry, price, d.Role);
        else
        {
            if (!await ConfirmAsync(chart, acc, $"Set stop/target at {acc.Instrument.FormatPrice(price)}", checkQuantity: false)) return;
            msg = br.DropFromPosition(price, d.Role);
        }

        if (msg.Length > 0) _status(msg);
        chart.Model?.Invalidate();
        _dom.InvalidateVisual();
    }

    internal async Task AccountActionAsync(ChartTab? chart, Action<ITradingAccount> action, string message)
    {
        var acc = AccountOf(chart);
        if (acc == null || chart == null) return;
        if (acc.IsReal && !await ConfirmAsync(chart, acc, message + "?", checkQuantity: false)) return;
        action(acc);
        _status(message);
    }

    internal async Task BracketActionAsync(ChartTab? chart, Func<BracketManager, string> action, string description)
    {
        var acc = AccountOf(chart);
        if (acc == null || chart == null) return;
        if (acc.IsReal && !await ConfirmAsync(chart, acc, description + "?", checkQuantity: false)) return;
        _status(action(_svc.BracketsFor(acc)));
        chart.Model?.Invalidate();
        _dom.InvalidateVisual();
    }

    private async void OnDomOrder(DomColumn col, decimal price, bool cancel)
    {
        var chart = _active;
        var acc = AccountOf(chart);
        if (acc == null || chart?.Model == null) return;
        if (cancel)
        {
            acc.CancelAt(price);
            _status($"Orders cancelled at {acc.Instrument.FormatPrice(price)}");
            return;
        }

        var book = chart.Model.Data.Depth;
        decimal last = chart.Model.Data.LastPrice;
        decimal bestBid = book.HasBid ? acc.Instrument.FromTicks(book.BestBidTicks) : last;
        decimal bestAsk = book.HasAsk ? acc.Instrument.FromTicks(book.BestAskTicks) : last;
        switch (col)
        {
            case DomColumn.Bid:
            case DomColumn.BuyOrders:
                await TradeAsync(chart, price < bestAsk ? OrderTypes.Limit : OrderTypes.Stop, OrderDirections.Buy, price);
                break;
            case DomColumn.Ask:
            case DomColumn.SellOrders:
                await TradeAsync(chart, price > bestBid ? OrderTypes.Limit : OrderTypes.Stop, OrderDirections.Sell, price);
                break;
        }
    }

    // ===================================================================== keyboard shortcuts

    /// <summary>Runs a shortcut on a chart (the active one if none is given). Returns true if it handled it.</summary>
    internal bool ExecuteHotkey(string id, ChartTab? chart = null)
    {
        chart ??= _active;
        if (chart?.Model == null) return false;
        var model = chart.Model;
        var toolbar = chart.Window?.Toolbar ?? _toolbar;
        decimal Best(bool bid)
        {
            var book = model.Data.Depth;
            if (bid && book.HasBid) return model.Instrument.FromTicks(book.BestBidTicks);
            if (!bid && book.HasAsk) return model.Instrument.FromTicks(book.BestAskTicks);
            return model.Data.LastPrice;
        }

        switch (id)
        {
            case "buy-market": _ = TradeAsync(chart, OrderTypes.Market, OrderDirections.Buy, 0); break;
            case "sell-market": _ = TradeAsync(chart, OrderTypes.Market, OrderDirections.Sell, 0); break;
            case "buy-bid": _ = TradeAsync(chart, OrderTypes.Limit, OrderDirections.Buy, Best(true)); break;
            case "sell-ask": _ = TradeAsync(chart, OrderTypes.Limit, OrderDirections.Sell, Best(false)); break;
            case "flatten": _ = AccountActionAsync(chart, a => a.Flatten(), "Position closed"); break;
            case "reverse": _ = AccountActionAsync(chart, a => a.Reverse(), "Position reversed"); break;
            case "cancel-all": _ = AccountActionAsync(chart, a => a.CancelAll(), "Orders cancelled"); break;
            case "close-25": _ = BracketActionAsync(chart, b => b.ClosePartial(0.25m), "Close 25% of the position"); break;
            case "close-50": _ = BracketActionAsync(chart, b => b.ClosePartial(0.5m), "Close half the position"); break;
            case "breakeven": _ = BracketActionAsync(chart, b => b.MoveStopToBreakeven(), "Move the stop to breakeven"); break;
            case "brackets": _ = BracketActionAsync(chart, b => b.ApplyTemplate(), "Set the stop and the targets"); break;
            case "qty-next":
            case "qty-prev":
            {
                var values = _qtyPresets.Children.OfType<Button>().Select(b => decimal.Parse((string)b.Content!, CultureInfo.InvariantCulture)).ToList();
                if (values.Count == 0) break;
                decimal q = QtyOf(chart);
                decimal next = id == "qty-next" ? values.FirstOrDefault(v => v > q, values[^1]) : values.LastOrDefault(v => v < q, values[0]);
                chart.Quantity = next;
                chart.Trader?.SyncQuantity();
                OnChartQuantityChanged(chart);
                _status($"Quantity: {next.ToString("0.####", CultureInfo.InvariantCulture)}");
                break;
            }

            case "period-m1": toolbar.SetPeriod(ChartPeriod.M1); break;
            case "period-m5": toolbar.SetPeriod(ChartPeriod.M5); break;
            case "period-m15": toolbar.SetPeriod(ChartPeriod.M15); break;
            case "period-h1": toolbar.SetPeriod(ChartPeriod.H1); break;
            case "period-h4": toolbar.SetPeriod(ChartPeriod.H4); break;
            case "period-d1": toolbar.SetPeriod(ChartPeriod.D1); break;
            case "style-candles": toolbar.SetStyle(ChartStyle.Candles); break;
            case "style-footprint": toolbar.SetStyle(ChartStyle.Footprint); break;
            case "style-heikin": toolbar.SetStyle(ChartStyle.HeikinAshi); break;
            case "autoscale":
                model.Viewport.AutoScale = true;
                model.Invalidate();
                break;
            case "go-last":
                model.Viewport.ScrollBars = 0;
                model.Invalidate();
                break;
            case "tool-hline":
            case "tool-cursor":
            {
                DrawingKind? kind = id == "tool-hline" ? DrawingKind.HorizontalLine : null;
                if (chart.View.Controller is { } c) c.ActiveTool = kind;
                chart.Tools?.Check(kind);
                chart.View.Focus();
                break;
            }

            case "layout-1": _ = SetLayoutAsync(PaneLayout.One); break;
            case "layout-4": _ = SetLayoutAsync(PaneLayout.Four); break;
            case "next-tab":
            case "prev-tab":
                if (_tabControl.ItemCount > 1)
                    _tabControl.SelectedIndex = (_tabControl.SelectedIndex + (id == "next-tab" ? 1 : _tabControl.ItemCount - 1)) % _tabControl.ItemCount;
                break;
            case "detach": Detach(chart); break;
            case "toggle-dom": TogglePanel("dom"); break;
            case "toggle-trader":
                SetTraderVisible(chart, !chart.TraderVisible);
                _status(chart.TraderVisible ? $"{chart.Caption}'s Chart Trader in view" : $"{chart.Caption}'s Chart Trader hidden");
                break;
            case "toggle-panel": TogglePanel("side"); break;
            default: return false;
        }

        return true;
    }

    // ===================================================================== context menu

    private void ShowChartMenu(ChartTab tab, Point p)
    {
        var model = tab.Model;
        if (model == null) return;
        var items = new List<Control>();
        var acc = AccountOf(tab);
        var br = acc != null ? _svc.BracketsFor(acc) : null;
        var ctrl = tab.View.Controller;
        float x = (float)p.X, y = (float)p.Y;

        // one of your own orders under the mouse
        if (br != null && ctrl?.OrderAt(x, y) is { } order)
        {
            var role = br.RoleOf(order);
            items.Add(Item(role switch { BracketRole.StopLoss => "Cancel the stop loss", BracketRole.Target => "Cancel the target", _ => "Cancel the order" },
                MaterialIconKind.CloseCircleOutline, () => _status(br.CancelBracket(order))));
            if (role == BracketRole.Target)
            {
                var qty = new MenuItem { Header = "Target quantity", Icon = Ui.Icon(MaterialIconKind.ScaleBalance, 16) };
                foreach (var f in new[] { 0.25m, 0.5m, 0.75m, 1m })
                    qty.Items.Add(Item($"{f * 100:0}% of the position", null, () => _status(br.SetTargetFraction(order, f))));
                items.Add(qty);
            }

            if (role == BracketRole.StopLoss) items.Add(Item("Stop to breakeven", MaterialIconKind.ShieldOutline, () => _ = BracketActionAsync(tab, b => b.MoveStopToBreakeven(), "Move the stop to breakeven")));
            if (role == BracketRole.None && !order.ReduceOnly && IsEntry(acc!, order.Direction))
                items.Add(Item("Attach the template's stop and targets", MaterialIconKind.TargetVariant, () =>
                {
                    br.AttachTemplate(order);
                    _status("Stop and targets attached: they are sent when the order fills");
                    model.Invalidate();
                }));
            items.Add(new Separator());
        }
        else if (br != null && ctrl?.HandleAt(x, y, false) is { Drag.Pending: { } pb } handle)
        {
            items.Add(Item(handle.Drag.Index < 0 ? "Remove the attached stop" : "Remove the attached target", MaterialIconKind.CloseCircleOutline, () =>
            {
                br.RemovePending(pb, handle.Drag.Index);
                model.Invalidate();
            }));
            items.Add(new Separator());
        }

        // open position
        if (br != null && acc!.Position.Volume != 0)
        {
            items.Add(Item("Close half at market", MaterialIconKind.ContentCut, () => _ = BracketActionAsync(tab, b => b.ClosePartial(0.5m), "Close half the position")));
            items.Add(Item("Close everything at market", MaterialIconKind.CloseBoxOutline, () => _ = AccountActionAsync(tab, a => a.Flatten(), "Position closed")));
            items.Add(Item("Stop to breakeven", MaterialIconKind.ShieldOutline, () => _ = BracketActionAsync(tab, b => b.MoveStopToBreakeven(), "Move the stop to breakeven")));
            items.Add(Item("Set the template's stop and targets", MaterialIconKind.TargetVariant, () => _ = BracketActionAsync(tab, b => b.ApplyTemplate(), "Set the stop and the targets")));
            items.Add(new Separator());
        }

        var sel = model.Drawings.FirstOrDefault(d => d.Selected && d.IsVisibleOn(model.Period));
        if (sel != null)
        {
            items.Add(Item("Configure drawing…", MaterialIconKind.TuneVariant, () => EditDrawing(tab, sel)));
            items.Add(Item("Delete drawing", MaterialIconKind.DeleteOutline, () => { model.Drawings.Remove(sel); DrawingsTouched(tab); }));
            items.Add(Item(sel.Locked ? "Unlock" : "Lock", MaterialIconKind.LockOutline, () => { sel.Locked = !sel.Locked; DrawingsTouched(tab); }));
            items.Add(Item(sel.EffectiveLineStyle == LineStyleKind.Solid ? "Dotted line" : "Solid line", MaterialIconKind.Minus, () =>
            {
                sel.LineStyle = sel.EffectiveLineStyle == LineStyleKind.Solid ? LineStyleKind.Dashed : LineStyleKind.Solid;
                sel.Dashed = false;
                DrawingsTouched(tab);
            }));
            var colors = new MenuItem { Header = "Color", Icon = Ui.Icon(MaterialIconKind.Palette, 16) };
            foreach (var (name, c) in new[] { ("Blue", 0xFF3D8BFDu), ("Amber", 0xFFF2B53Au), ("Green", 0xFF26C281u), ("Red", 0xFFF0525Au), ("White", 0xFFFFFFFFu), ("Purple", 0xFFB478FFu) })
                colors.Items.Add(Item(name, null, () => { sel.ColorArgb = c; DrawingsTouched(tab); }));
            items.Add(colors);
            if (sel.Kind is DrawingKind.HorizontalLine or DrawingKind.HorizontalRay)
                items.Add(Item(sel.IsAlert ? "Remove alert" : "Alert when price touches", MaterialIconKind.BellOutline, () =>
                {
                    if (sel.IsAlert)
                    {
                        sel.Alert = false;
                        if (sel.Text == "alerta") sel.Text = "";
                    }
                    else sel.Alert = true;
                    DrawingsTouched(tab);
                }));
            items.Add(new Separator());
        }

        var layout = model.LastLayout;
        if (layout != null && layout.Main.Rect.Contains(x, y))
        {
            decimal price = model.Instrument.RoundPrice((decimal)layout.Main.ValueAt(y));
            decimal last = model.Data.LastPrice;
            string ps = model.Instrument.FormatPrice(price);
            if (price < last)
            {
                items.Add(Item($"Buy limit {QtyOf(tab)} @ {ps}", MaterialIconKind.ArrowUpBold, () => _ = TradeAsync(tab, OrderTypes.Limit, OrderDirections.Buy, price)));
                items.Add(Item($"Sell stop {QtyOf(tab)} @ {ps}", MaterialIconKind.ArrowDownBold, () => _ = TradeAsync(tab, OrderTypes.Stop, OrderDirections.Sell, price)));
            }
            else
            {
                items.Add(Item($"Sell limit {QtyOf(tab)} @ {ps}", MaterialIconKind.ArrowDownBold, () => _ = TradeAsync(tab, OrderTypes.Limit, OrderDirections.Sell, price)));
                items.Add(Item($"Buy stop {QtyOf(tab)} @ {ps}", MaterialIconKind.ArrowUpBold, () => _ = TradeAsync(tab, OrderTypes.Stop, OrderDirections.Buy, price)));
            }

            items.Add(Item($"Price alert at {ps}", MaterialIconKind.BellPlusOutline, () =>
            {
                model.Drawings.Add(new ChartDrawing { Kind = DrawingKind.HorizontalLine, Alert = true, ColorArgb = 0xFFF2B53A, LineStyle = LineStyleKind.Dashed, Points = { new ChartPoint(DateTime.UtcNow, (double)price) } });
                DrawingsTouched(tab);
            }));
            if (acc?.ActiveOrders.Any() == true) items.Add(Item("Cancel every order", MaterialIconKind.CloseCircleOutline, () => _ = AccountActionAsync(tab, a => a.CancelAll(), "Orders cancelled")));
            items.Add(new Separator());
        }

        items.Add(Item("Indicators…", MaterialIconKind.FunctionVariant, () => OpenIndicators(tab)));
        items.Add(Item("Edit footprint…", MaterialIconKind.ViewColumnOutline, () => OpenFootprintEditor(tab)));
        items.Add(Item("Chart settings…", MaterialIconKind.PaletteOutline, () => OpenChartSettings(tab)));
        var strategyMenu = new MenuItem { Header = "Strategies on the chart", Icon = Ui.Icon(MaterialIconKind.RobotOutline, 16) };
        foreach (var i in StrategyMenuItems(tab)) strategyMenu.Items.Add(i);
        items.Add(strategyMenu);
        var templates = new MenuItem { Header = "Chart templates", Icon = Ui.Icon(MaterialIconKind.BookmarkMultipleOutline, 16) };
        foreach (var i in ChartTemplateItems(tab)) templates.Items.Add(i);
        items.Add(templates);
        var legend = new MenuItem { Header = "Chart legend", Icon = Ui.Icon(MaterialIconKind.FormatListBulleted, 16) };
        void LegendToggle(string text, Func<bool> get, Action<bool> set)
        {
            var mi = new MenuItem { Header = text, Icon = get() ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null };
            mi.Click += (_, _) =>
            {
                set(!get());
                model.Invalidate();
                Workspace.Save(this);
            };
            legend.Items.Add(mi);
        }

        LegendToggle("Instrument name", () => model.Settings.ShowLegendTitle, v => model.Settings.ShowLegendTitle = v);
        LegendToggle("Candle values (O, H, L, C…)", () => model.Settings.ShowLegendValues, v => model.Settings.ShowLegendValues = v);
        LegendToggle("Indicator names", () => model.Settings.ShowIndicatorNames, v => model.Settings.ShowIndicatorNames = v);
        items.Add(legend);
        var marks = new MenuItem { Header = "Mark my trades", Icon = model.Settings.ShowTradeMarks ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : Ui.Icon(MaterialIconKind.SwapVertical, 16) };
        ToolTip.SetTip(marks, "Triangles on each execution and a line from the entry to the exit of each trade");
        marks.Click += (_, _) =>
        {
            model.Settings.ShowTradeMarks = !model.Settings.ShowTradeMarks;
            model.Invalidate();
            Workspace.Save(this);
        };
        items.Add(marks);
        items.Add(Item("Auto scale", MaterialIconKind.ArrowExpandVertical, () => { model.Viewport.AutoScale = true; model.Invalidate(); }));
        items.Add(Item("Go to last price", MaterialIconKind.PageLast, () => { model.Viewport.ScrollBars = 0; model.Invalidate(); }));
        if (model.FocusTrade != null) items.Add(Item("Remove the trade's highlight", MaterialIconKind.SelectionRemove, () => ClearFocusTrade(tab)));
        if (tab.Window == null) items.Add(Item("Move to a separate window", MaterialIconKind.OpenInNew, () => Detach(tab)));
        if (tab.Window == null && tab.Page is { Charts.Count: > 1 })
        {
            bool big = ReferenceEquals(tab.Page.Maximized, tab);
            items.Add(Item(big ? "Show every chart again" : "Enlarge this chart", big ? MaterialIconKind.ArrowCollapse : MaterialIconKind.ArrowExpand, () => ToggleMaximize(tab)));
            items.Add(Item("Move this chart to a new tab", MaterialIconKind.TabPlus, () => _ = MoveToNewPageAsync(tab)));
        }
        else if (tab.Window is { } window) items.Add(Item("Back to the main window", MaterialIconKind.DockWindow, () => DockBack(window)));
        new ContextMenu { ItemsSource = items }.Open(tab.View);
    }

    private static MenuItem Item(string header, MaterialIconKind? icon, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        if (icon is { } k) mi.Icon = Ui.Icon(k, 16);
        mi.Click += (_, _) => onClick();
        return mi;
    }

    // ===================================================================== quote and position

    private void UpdateQuote(InstrumentData data)
    {
        var inst = data.Instrument;
        var book = data.Depth;
        _buyPrice.Text = book.HasAsk ? inst.FormatPrice(inst.FromTicks(book.BestAskTicks)) : inst.FormatPrice(data.LastPrice);
        _sellPrice.Text = book.HasBid ? inst.FormatPrice(inst.FromTicks(book.BestBidTicks)) : inst.FormatPrice(data.LastPrice);
        _toolbar.UpdateQuote();
    }

    /// <summary>
    /// Accounts to pick from on a chart. If the chosen one belongs to Rithmic and the session has not listed it yet
    /// (connecting), it still shows up, so the picker is not left blank.
    /// </summary>
    internal List<AccountOption> AccountOptions(ChartTab tab, InstrumentData data)
    {
        var list = _svc.AccountsFor(data).ToList();
        if (tab.AccountId.StartsWith("rithmic|", StringComparison.Ordinal) && list.All(a => a.Id != tab.AccountId))
            list.Add(new AccountOption(tab.AccountId, $"{tab.AccountId.Split('|')[^1]} · Rithmic (connecting…)", false));
        return list;
    }

    internal void UpdatePosition()
    {
        var acc = Account;
        if (acc == null || _active?.Model == null) return;

        // the Rithmic accounts show up when their session finishes connecting
        bool was = _syncing;
        _syncing = true;
        Ui.SyncAccounts(_accountBox, AccountOptions(_active, _active.Model.Data), _active.AccountId);
        _syncing = was;
        var pos = acc.Position;
        int orders = acc.ActiveOrders.Count();
        string brackets = "";
        if (pos.Volume != 0)
        {
            var br = _svc.BracketsFor(acc);
            var sl = br.StopLoss;
            int tps = br.Targets.Count;
            brackets = (sl != null ? $" · stop {_active.Model.Instrument.FormatPrice(sl.TriggerPrice)}" : " · no stop") + (tps > 0 ? $" · {tps} target{(tps > 1 ? "s" : "")}" : "");
        }

        ToolTip.SetTip(_positionText, null);
        _positionText.Text = acc.LastError != null
            ? "⚠ " + acc.LastError
            : pos.Volume == 0
                ? $"Flat · {orders} active order(s)"
                : $"{(pos.Volume > 0 ? "LONG" : "SHORT")} {Math.Abs(pos.Volume)} @ {_active.Model.Instrument.FormatPrice(pos.AveragePrice)} · PnL {pos.UnrealizedPnL:+0.00;-0.00}{brackets}";
        _positionText.Foreground = acc.LastError != null ? Brushes.OrangeRed : pos.Volume == 0 ? Ui.Text2 : pos.UnrealizedPnL >= 0 ? Ui.Up : Ui.Down;
    }
}
