using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Charting;
using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Trading;

namespace TradingHub.App.Views;

/// <summary>A chart: it lives in a tab of the main window or in a separate window.</summary>
public sealed class ChartTab
{
    public required ChartView View { get; init; }

    /// <summary>Chart frame (header + chart); it is what moves between tabs and windows.</summary>
    public required Border Frame { get; init; }

    /// <summary>Header of the frame (visible in multi-chart layouts).</summary>
    public required Control Header { get; init; }

    public required TextBlock Title { get; init; }
    public ChartModel? Model { get; set; }
    public string FeedId { get; set; } = SimulatedFeed.FeedId;
    public string Symbol { get; set; } = "SIM-BTC";
    public string AccountId { get; set; } = "sim";
    public double HistoryHours { get; set; }

    /// <summary>Link group by color (0 = not linked).</summary>
    public int LinkGroup { get; set; }

    internal LinkButton? LinkButton { get; set; }

    /// <summary>Quantity to trade from this chart (Chart Trader, DOM and hotkeys when it is the active one).</summary>
    public decimal Quantity { get; set; }

    /// <summary>Cross trading: instrument this chart's orders are executed on (null = the chart's own).</summary>
    public string? CrossSymbol { get; set; }

    internal TradingHub.Data.InstrumentData? CrossData { get; set; }

    /// <summary>Whether this chart's Chart Trader is expanded.</summary>
    public bool TraderVisible { get; set; } = true;

    internal ChartTraderPanel? Trader { get; set; }

    /// <summary>The chart's own drawing toolbar (on the left).</summary>
    internal DrawingToolStrip? Tools { get; set; }

    internal ChartPage? Page { get; set; }
    internal ChartWindow? Window { get; set; }

    /// <summary>Maximize / restore button of the frame's bar.</summary>
    internal Avalonia.Controls.Button? MaximizeButton { get; set; }

    /// <summary>Called when the chart is closed (for example, to close a replay's feed).</summary>
    internal Action? OnClosed { get; set; }

    public bool IsReplay => Model?.Data.Feed is ReplayFeed;

    public string Caption => Model == null ? Symbol : $"{(IsReplay ? "▶ " : "")}{Symbol} · {Model.Period.DisplayName}";

    /// <summary>Strategies placed on this chart (like indicators: visual or running on an account).</summary>
    public List<ChartStrategy> Strategies { get; } = new();

    /// <summary>Which fills are already marked on the chart (account, instrument and quantity) so they are not redone.</summary>
    public string MarksKey { get; set; } = "";
}

/// <summary>How the charts are arranged inside a tab.</summary>
public enum PaneLayout
{
    One,
    TwoColumns,
    TwoRows,
    ThreeColumns,
    OneLeftTwoRight,
    OneTopTwoBottom,
    Four,
    Six,
}

/// <summary>A tab of the main window: one or several charts with a layout.</summary>
internal sealed class ChartPage
{
    public required TabItem Item { get; init; }
    public required TextBlock HeaderText { get; init; }
    public Grid Host { get; } = new() { Background = Ui.Bg0, Margin = new Thickness(6) };
    public PaneLayout Layout { get; set; } = PaneLayout.One;
    public List<ChartTab> Charts { get; } = new();
    public ChartTab? Active { get; set; }

    /// <summary>Maximized chart: it fills the whole tab until you restore it (the others are still there).</summary>
    public ChartTab? Maximized { get; set; }
    public int VisibleCount => Maximized != null ? 1 : Math.Min(Charts.Count, TradingView.PaneCount(Layout));
}

/// <summary>Trading workspace: tabs with one or several charts, DOM, ticket and tape.</summary>
public sealed partial class TradingView : UserControl
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly List<ChartPage> _pages = new();
    private readonly List<ChartWindow> _floating = new();
    private readonly TabControl _tabControl = new();
    private readonly ChartToolbar _toolbar;
    private readonly ChoiceButton<PaneLayout> _layoutChoice;
    private ChartPage? _page;
    private ChartTab? _active;
    private bool _switching;

    internal static readonly (PaneLayout Layout, string Label, string Short)[] Layouts =
    {
        (PaneLayout.One, "One chart", "1"),
        (PaneLayout.TwoColumns, "Two, side by side", "2 ‖"),
        (PaneLayout.TwoRows, "Two, one above the other", "2 ═"),
        (PaneLayout.ThreeColumns, "Three in columns", "3 ‖"),
        (PaneLayout.OneLeftTwoRight, "One big plus two on the right", "1+2"),
        (PaneLayout.OneTopTwoBottom, "One big on top plus two below", "1+2 ═"),
        (PaneLayout.Four, "Four (2 × 2)", "4"),
        (PaneLayout.Six, "Six (3 × 2)", "6"),
    };

    public TradingView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        _layoutChoice = new ChoiceButton<PaneLayout>(MaterialIconKind.ViewGridOutline, Layouts.Select(l => (l.Layout, l.Label)),
            "Layout: how many charts to see at once in this tab", l => Layouts.First(x => x.Layout == l).Short);
        _layoutChoice.Changed += l => _ = SetLayoutAsync(l);
        var popOut = Ui.IconButton(MaterialIconKind.OpenInNew, $"Pull the chart into a separate window ({Plat.Keys(Plat.Cmd | KeyModifiers.Shift, "D")})", () => DetachActive());
        var newTab = Ui.IconButton(MaterialIconKind.Plus, $"New tab ({Plat.Keys(Plat.Cmd, "T")})", () => _ = NewChartAsync());
        _toolbar = new ChartToolbar(this, false, Ui.Divider(true), _layoutChoice, BuildPanelsButton(), popOut, newTab);
        _svc.BracketsCreated += HookBrackets;
        Content = BuildLayout();
    }

    internal IEnumerable<ChartTab> AllCharts => _pages.SelectMany(p => p.Charts).Concat(_floating.Select(w => w.Chart));

    /// <summary>Every open chart (tabs and separate windows).</summary>
    internal IReadOnlyList<ChartTab> Tabs => AllCharts.ToList();

    internal IReadOnlyList<ChartPage> Pages => _pages;
    internal IReadOnlyList<ChartWindow> FloatingWindows => _floating;
    internal ChartTab? ActiveTab => _active;
    internal ChartPage? ActivePage => _page;
    internal AppServices Services => _svc;
    internal InstrumentPicker Picker => _toolbar.Picker;
    internal ChartToolbar Toolbar => _toolbar;

    internal void SetStatus(string text) => _status(text);

    // ===================================================================== layout

    private readonly Grid _body = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,340") };

    // the splitter sits on top of the panel's border (6 px to grab it with the mouse, without leaving a gap between the chart and the panel)
    private readonly GridSplitter _sideSplitter = new()
    {
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndCurrent,
        Width = 6,
        HorizontalAlignment = HorizontalAlignment.Left,
        Background = Brushes.Transparent,
        ZIndex = 10,
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.SizeWestEast),
    };

    private Control BuildLayout()
    {
        var root = new DockPanel();
        DockPanel.SetDock(_toolbar, Dock.Top);
        root.Children.Add(_toolbar);

        _tabControl.SelectionChanged += (_, _) => OnPageChanged();
        _tabControl.Margin = new Thickness(0);
        // the tab strip only appears when there is more than one tab
        _tabControl.Classes.Add("single");
        ((System.Collections.Specialized.INotifyCollectionChanged)_tabControl.Items).CollectionChanged += (_, _) =>
            _tabControl.Classes.Set("single", _tabControl.ItemCount <= 1);
        var chartColumn = new DockPanel();
        var replayBar = BuildReplayBar();
        DockPanel.SetDock(replayBar, Dock.Bottom);
        chartColumn.Children.Add(replayBar);
        chartColumn.Children.Add(_tabControl);
        Grid.SetColumn(chartColumn, 1);
        _body.Children.Add(chartColumn);

        var right = BuildRightPanel();
        Grid.SetColumn(right, 2);
        _body.Children.Add(right);
        Grid.SetColumn(_sideSplitter, 2);
        _body.Children.Add(_sideSplitter);
        root.Children.Add(_body);
        ApplyPanelVisibility();
        return root;
    }

    // ===================================================================== multi-chart layouts

    internal static int PaneCount(PaneLayout l) => l switch
    {
        PaneLayout.One => 1,
        PaneLayout.TwoColumns or PaneLayout.TwoRows => 2,
        PaneLayout.ThreeColumns or PaneLayout.OneLeftTwoRight or PaneLayout.OneTopTwoBottom => 3,
        PaneLayout.Four => 4,
        _ => 6,
    };

    /// <summary>Columns, rows and cells (column, row, width, height) of each layout.</summary>
    private static (int Cols, int Rows, (int C, int R, int CS, int RS)[] Cells) LayoutGrid(PaneLayout l) => l switch
    {
        PaneLayout.One => (1, 1, new[] { (0, 0, 1, 1) }),
        PaneLayout.TwoColumns => (2, 1, new[] { (0, 0, 1, 1), (1, 0, 1, 1) }),
        PaneLayout.TwoRows => (1, 2, new[] { (0, 0, 1, 1), (0, 1, 1, 1) }),
        PaneLayout.ThreeColumns => (3, 1, new[] { (0, 0, 1, 1), (1, 0, 1, 1), (2, 0, 1, 1) }),
        PaneLayout.OneLeftTwoRight => (2, 2, new[] { (0, 0, 1, 2), (1, 0, 1, 1), (1, 1, 1, 1) }),
        PaneLayout.OneTopTwoBottom => (2, 2, new[] { (0, 0, 2, 1), (0, 1, 1, 1), (1, 1, 1, 1) }),
        PaneLayout.Four => (2, 2, new[] { (0, 0, 1, 1), (1, 0, 1, 1), (0, 1, 1, 1), (1, 1, 1, 1) }),
        _ => (3, 2, new[] { (0, 0, 1, 1), (1, 0, 1, 1), (2, 0, 1, 1), (0, 1, 1, 1), (1, 1, 1, 1), (2, 1, 1, 1) }),
    };

    /// <summary>Arranges a tab's charts according to its layout (with splitters to resize them).</summary>
    private void Arrange(ChartPage page)
    {
        var host = page.Host;
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        if (page.Maximized is { } big && !page.Charts.Contains(big)) page.Maximized = null;
        if (page.Maximized is { } max)
        {
            // maximized: only that chart, with its bar (to restore it)
            host.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            host.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            max.Header.IsVisible = true;
            if (max.Frame.Parent is Panel prev && !ReferenceEquals(prev, host)) prev.Children.Remove(max.Frame);
            host.Children.Add(max.Frame);
            foreach (var c in page.Charts) UpdateMaximizeButton(c);
            UpdateFrames(page);
            UpdatePageHeader(page);
            return;
        }

        var (cols, rows, cells) = LayoutGrid(page.Layout);
        for (int c = 0; c < cols; c++)
        {
            if (c > 0) host.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5)));
            host.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star) { MinWidth = 160 });
        }

        for (int r = 0; r < rows; r++)
        {
            if (r > 0) host.RowDefinitions.Add(new RowDefinition(new GridLength(5)));
            host.RowDefinitions.Add(new RowDefinition(GridLength.Star) { MinHeight = 120 });
        }

        bool multi = cells.Length > 1;
        for (int i = 0; i < cells.Length; i++)
        {
            var (c, r, cs, rs) = cells[i];
            Control cell;
            if (i < page.Charts.Count)
            {
                var chart = page.Charts[i];
                chart.Header.IsVisible = multi;
                cell = chart.Frame;
                if (cell.Parent is Panel old && !ReferenceEquals(old, host)) old.Children.Remove(cell);
            }
            else
            {
                cell = EmptyPane(page);
            }

            Avalonia.Controls.Grid.SetColumn(cell, c * 2);
            Avalonia.Controls.Grid.SetRow(cell, r * 2);
            Avalonia.Controls.Grid.SetColumnSpan(cell, cs * 2 - 1);
            Avalonia.Controls.Grid.SetRowSpan(cell, rs * 2 - 1);
            host.Children.Add(cell);
        }

        // splitters: between columns (on the rows that need them) and between rows
        void VSplit(int col, int row, int rowSpan)
        {
            var s = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = Ui.Bg0 };
            Avalonia.Controls.Grid.SetColumn(s, col);
            Avalonia.Controls.Grid.SetRow(s, row);
            Avalonia.Controls.Grid.SetRowSpan(s, rowSpan);
            host.Children.Add(s);
        }

        void HSplit(int row, int col, int colSpan)
        {
            var s = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = Ui.Bg0 };
            Avalonia.Controls.Grid.SetRow(s, row);
            Avalonia.Controls.Grid.SetColumn(s, col);
            Avalonia.Controls.Grid.SetColumnSpan(s, colSpan);
            host.Children.Add(s);
        }

        switch (page.Layout)
        {
            case PaneLayout.OneLeftTwoRight:
                VSplit(1, 0, 3);
                HSplit(1, 2, 1);
                break;
            case PaneLayout.OneTopTwoBottom:
                HSplit(1, 0, 3);
                VSplit(1, 2, 1);
                break;
            default:
                for (int c = 1; c < cols; c++) VSplit(c * 2 - 1, 0, rows * 2 - 1);
                for (int r = 1; r < rows; r++) HSplit(r * 2 - 1, 0, cols * 2 - 1);
                break;
        }

        foreach (var c in page.Charts) UpdateMaximizeButton(c);
        UpdateFrames(page);
        UpdatePageHeader(page);
    }

    /// <summary>Maximizes a chart so it fills the whole tab (or puts it back).</summary>
    internal void ToggleMaximize(ChartTab chart)
    {
        if (chart.Page is not { } page) return;
        page.Maximized = ReferenceEquals(page.Maximized, chart) ? null : chart;
        Arrange(page);
        Activate(chart);
    }

    private static void UpdateMaximizeButton(ChartTab chart)
    {
        if (chart.MaximizeButton is not { } b) return;
        bool big = chart.Page?.Maximized != null && ReferenceEquals(chart.Page.Maximized, chart);
        b.Content = Ui.Icon(big ? MaterialIconKind.ArrowCollapse : MaterialIconKind.ArrowExpand, 14);
        ToolTip.SetTip(b, big ? "Show every chart again" : "Enlarge this chart (it takes over the whole tab)");
    }

    /// <summary>Layout that fits exactly the number of charts left (closing or pulling one out leaves no empty slots).</summary>
    private static PaneLayout FitLayout(PaneLayout current, int count) => count switch
    {
        <= 1 => PaneLayout.One,
        2 => current is PaneLayout.TwoRows or PaneLayout.OneTopTwoBottom ? PaneLayout.TwoRows : PaneLayout.TwoColumns,
        3 => current is PaneLayout.OneLeftTwoRight or PaneLayout.OneTopTwoBottom or PaneLayout.ThreeColumns ? current : PaneLayout.OneLeftTwoRight,
        4 => PaneLayout.Four,
        _ => current,
    };

    /// <summary>If empty slots were left in the tab, the layout shrinks to what is there.</summary>
    private void ShrinkLayout(ChartPage page)
    {
        if (PaneCount(page.Layout) <= page.Charts.Count) return;
        page.Layout = FitLayout(page.Layout, page.Charts.Count);
        if (ReferenceEquals(page, _page)) _layoutChoice.Select(page.Layout);
    }

    private Control EmptyPane(ChartPage page)
    {
        var add = Ui.Button("Add chart", MaterialIconKind.Plus, () => _ = AddClonedChartAsync(page));
        var box = Ui.V(10, Ui.Icon(MaterialIconKind.ChartBoxOutline, 34, Ui.Text2), add);
        box.HorizontalAlignment = HorizontalAlignment.Center;
        box.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Child = box, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
    }

    /// <summary>
    /// Highlights the active chart when there are several: a thin border in the soft accent and the title lit up (the others with
    /// the usual border and the title dimmed). It used to be a strong amber border that cluttered the screen.
    /// </summary>
    private void UpdateFrames(ChartPage page)
    {
        bool multi = PaneCount(page.Layout) > 1;
        // a single chart: it fills everything, edge to edge (no margin, no frame and no rounded corners)
        bool alone = page.Maximized != null || !multi;
        // with the right panel visible, the charts of the last column sit flush against it: no gap, no rounded
        // corners on that side and a single line between the two (the panel's)
        page.Host.Margin = alone ? new Thickness(0) : _sideVisible ? new Thickness(6, 6, 0, 6) : new Thickness(6);
        int cols = page.Host.ColumnDefinitions.Count;
        foreach (var c in page.Charts)
        {
            bool on = multi && ReferenceEquals(c, page.Active);
            bool flush = _sideVisible && ReferenceEquals(c.Frame.Parent, page.Host) &&
                         Avalonia.Controls.Grid.GetColumn(c.Frame) + Avalonia.Controls.Grid.GetColumnSpan(c.Frame) >= cols;
            c.Frame.BorderBrush = on ? Ui.Brush("ActiveFrameBrush") : Ui.Line;
            c.Frame.BorderThickness = alone ? new Thickness(0) : flush ? new Thickness(1, 1, 0, 1) : new Thickness(1);
            c.Frame.CornerRadius = alone ? new CornerRadius(0) : flush ? new CornerRadius(8, 0, 0, 8) : new CornerRadius(8);
            if (c.Frame.Child is Border inner) inner.CornerRadius = alone ? new CornerRadius(0) : flush ? new CornerRadius(7, 0, 0, 7) : new CornerRadius(7);
            c.Title.Foreground = !multi || on ? Ui.Text0 : Ui.Text2;
            if (c.Header is Border hb) hb.Background = on ? Ui.Brush("ActiveHeaderBrush") : Ui.Bg1;
        }
    }

    private void UpdatePageHeader(ChartPage page)
    {
        var main = page.Active ?? page.Charts.FirstOrDefault();
        int n = page.VisibleCount;
        page.HeaderText.Text = main == null ? "Empty" : n > 1 ? $"{main.Caption}  +{n - 1}" : main.Caption;
    }

    /// <summary>Updates the titles wherever a chart appears (frame, tab and window).</summary>
    internal void UpdateTitles(ChartTab chart)
    {
        chart.Title.Text = chart.Caption;
        if (chart.Page != null) UpdatePageHeader(chart.Page);
        chart.Window?.UpdateTitle();
    }

    internal async Task SetLayoutAsync(PaneLayout layout)
    {
        var page = _page;
        if (page == null) return;
        page.Layout = layout;
        page.Maximized = null; // picking a layout shows them all again
        _layoutChoice.Select(layout);
        int need = PaneCount(layout);
        // the chart you were using stays visible even if the layout has fewer slots
        if (page.Active is { } act && page.Charts.IndexOf(act) is var ai && ai >= need)
        {
            page.Charts.RemoveAt(ai);
            page.Charts.Insert(0, act);
        }

        // the charts that do not fit stay stored in the tab (they come back when a bigger layout is picked)
        while (page.Charts.Count < need)
            if (await AddClonedChartAsync(page, arrange: false) == null) break;
        Arrange(page);
        if (page.Active == null || page.Charts.IndexOf(page.Active) >= need) Activate(page.Charts.FirstOrDefault());
        _status(need == 1 ? "One chart per tab" : $"{need} charts in the tab: click one to select it and change its instrument or period");
    }

    /// <summary>Adds a copy of the active chart with another period to the tab.</summary>
    private async Task<ChartTab?> AddClonedChartAsync(ChartPage page, bool arrange = true)
    {
        var src = page.Active ?? page.Charts.FirstOrDefault() ?? _active;
        var used = page.Charts.Select(c => c.Model?.Period).Where(p => p != null).ToHashSet();
        var period = new[] { ChartPeriod.M1, ChartPeriod.M5, ChartPeriod.M15, ChartPeriod.H1, ChartPeriod.M30, ChartPeriod.H4, ChartPeriod.Parse("Daily"), ChartPeriod.Parse("S15") }
            .Where(p => !used.Contains(p)).DefaultIfEmpty(ChartPeriod.M5).First();
        var chart = CreateChart(src?.FeedId ?? SimulatedFeed.FeedId, src?.Symbol ?? "SIM-BTC", src?.AccountId ?? "sim", src?.HistoryHours ?? 0);
        page.Charts.Add(chart);
        chart.Page = page;
        await OpenInTabAsync(chart, chart.FeedId, chart.Symbol, period, src?.Model?.Settings.Clone(), copyIndicatorsFrom: src);
        if (arrange) Arrange(page);
        return chart;
    }

    // ===================================================================== charts and tabs

    /// <summary>Creates an empty chart (frame, header and events). It is opened afterwards with OpenInTabAsync.</summary>
    private ChartTab CreateChart(string feedId, string symbol, string accountId, double historyHours)
    {
        var view = new ChartView();
        var title = new TextBlock { Text = symbol, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var headerRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var header = new DockPanel { Height = 26, Margin = new Thickness(8, 0, 4, 0) };
        DockPanel.SetDock(headerRight, Dock.Right);
        header.Children.Add(headerRight);
        header.Children.Add(title);
        var headerBorder = new Border { Child = header, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        var body = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        body.Children.Add(headerBorder);
        body.Children.Add(view);
        // the content is clipped with the border's inner radius: the chart follows the corner's curve (no sharp tips)
        var inner = new Border { Child = body, CornerRadius = new CornerRadius(7), ClipToBounds = true };
        var frame = new Border { Child = inner, ClipToBounds = true, CornerRadius = new CornerRadius(8), Background = Ui.Bg0, BorderBrush = Ui.Line, BorderThickness = new Thickness(1) };
        var chart = new ChartTab { View = view, Frame = frame, Header = headerBorder, Title = title, FeedId = feedId, Symbol = symbol, AccountId = accountId, HistoryHours = historyHours };

        // each chart with its drawing toolbar (left) and its Chart Trader (right), as in ATAS
        var tools = new DrawingToolStrip();
        chart.Tools = tools;
        DockPanel.SetDock(tools, Dock.Left);
        body.Children.Insert(body.Children.IndexOf(view), tools);
        tools.ToolSelected += kind =>
        {
            Activate(chart);
            if (chart.View.Controller is { } ctl)
            {
                ctl.ActiveTool = kind;
                chart.View.Focus();
            }
        };
        tools.ClearRequested += () =>
        {
            if (chart.Model == null) return;
            chart.Model.Drawings.Clear();
            chart.Model.Invalidate();
            DrawingsTouched(chart);
        };
        var trader = new ChartTraderPanel(this, chart);
        chart.Trader = trader;
        DockPanel.SetDock(trader, Dock.Right);
        body.Children.Insert(body.Children.IndexOf(view), trader);

        // link color in the corner of the chart's little bar
        var link = new LinkButton(this, () => chart) { Margin = new Thickness(0, 0, 6, 0) };
        chart.LinkButton = link;
        DockPanel.SetDock(link, Dock.Left);
        header.Children.Insert(1, link);
        view.CrosshairMoved += (time, price) => OnCrosshair(chart, time, price);

        Button Small(MaterialIconKind icon, string tip, Action a)
        {
            var b = new Button { Content = Ui.Icon(icon, 14), Width = 22, Height = 22, MinHeight = 0, Padding = new Thickness(0) }.Classes("icon");
            ToolTip.SetTip(b, tip);
            b.Click += (_, e) =>
            {
                a();
                e.Handled = true;
            };
            return b;
        }

        chart.MaximizeButton = Small(MaterialIconKind.ArrowExpand, "Enlarge this chart (it takes over the whole tab)", () => ToggleMaximize(chart));
        headerRight.Children.Add(chart.MaximizeButton);
        headerRight.Children.Add(Small(MaterialIconKind.OpenInNew, "Move to a separate window", () => Detach(chart)));
        headerRight.Children.Add(Small(MaterialIconKind.Close, "Close this chart", () => CloseChart(chart)));

        // a click anywhere on the chart: it becomes the active one
        frame.AddHandler(PointerPressedEvent, (_, _) => Activate(chart), RoutingStrategies.Tunnel, handledEventsToo: true);
        view.ToolFinished += () => chart.Tools?.Check(null);
        view.DrawingsChanged += () => DrawingsTouched(chart);
        view.DrawingDefaults = k => _svc.Settings.DrawingDefaults.TryGetValue(k.ToString(), out var d) ? d : null;
        view.MenuRequested += (_, p) => ShowChartMenu(chart, p);
        view.IndicatorSettingsRequested += slot => OpenIndicators(chart, slot);
        view.StrategySettingsRequested += ov =>
        {
            if (ov.Tag is ChartStrategy cs) OpenStrategySettings(chart, cs);
        };
        view.StrategyRemoveRequested += ov =>
        {
            if (ov.Tag is ChartStrategy cs) RemoveStrategy(chart, cs);
        };
        view.IndicatorsChanged += () => Workspace.Save(this);
        view.DrawingDoubleClicked += d => EditDrawing(chart, d);
        view.OrderDragged += (o, price) => _ = MoveOrderAsync(chart, o, price);
        view.BracketDropped += (d, price) => _ = OnBracketDroppedAsync(chart, d, price);
        return chart;
    }

    private ChartPage CreatePage()
    {
        var headerText = new TextBlock { Text = "…", VerticalAlignment = VerticalAlignment.Center };
        var close = new Button { Content = Ui.Icon(MaterialIconKind.Close, 13), Width = 20, Height = 20, MinHeight = 0, Padding = new Thickness(0) }.Classes("icon");
        ToolTip.SetTip(close, "Close the tab");
        var item = new TabItem { Header = Ui.H(8, headerText, close) };
        var page = new ChartPage { Item = item, HeaderText = headerText };
        item.Content = page.Host;
        close.Click += (_, e) =>
        {
            ClosePage(page);
            e.Handled = true;
        };
        return page;
    }

    /// <summary>New tab with one chart.</summary>
    internal async Task<ChartTab> NewTabAsync(string feedId, string symbol, ChartPeriod period, ChartSettings? settings = null, bool activate = true, string accountId = "sim", double historyHours = 0, PaneLayout layout = PaneLayout.One)
    {
        var page = CreatePage();
        page.Layout = layout;
        var chart = CreateChart(feedId, symbol, accountId, historyHours);
        page.Charts.Add(chart);
        page.Active = chart;
        chart.Page = page;
        _pages.Add(page);
        Arrange(page);
        _tabControl.Items.Add(page.Item);
        if (activate || _page == null) _tabControl.SelectedItem = page.Item;
        await OpenInTabAsync(chart, feedId, symbol, period, settings, copyIndicatorsFrom: null);
        return chart;
    }

    /// <summary>Adds a chart to an existing tab (used by the workspace restore).</summary>
    internal async Task<ChartTab> AddToPageAsync(ChartPage page, string feedId, string symbol, ChartPeriod period, ChartSettings? settings, string accountId, double historyHours)
    {
        var chart = CreateChart(feedId, symbol, accountId, historyHours);
        page.Charts.Add(chart);
        chart.Page = page;
        Arrange(page);
        await OpenInTabAsync(chart, feedId, symbol, period, settings, copyIndicatorsFrom: null);
        return chart;
    }

    internal async Task OpenInTabAsync(ChartTab tab, string feedId, string symbol, ChartPeriod period, ChartSettings? settings) =>
        await OpenInTabAsync(tab, feedId, symbol, period, settings, copyIndicatorsFrom: tab);

    /// <summary>Saves the chart as it is on screen (candles, indicators, drawings) to Documents\Trading Hub\Screenshots.</summary>
    internal string? SaveChartPicture(ChartTab? tab, string? folder = null)
    {
        if (tab?.Model is not { } model) return null;
        try
        {
            int w = Math.Max(400, (int)tab.View.Bounds.Width), h = Math.Max(300, (int)tab.View.Bounds.Height);
            var renderer = new TradingHub.Charting.Rendering.ChartRenderer();
            var state = new TradingHub.Charting.Rendering.ChartRenderState { Crosshair = false };
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(w, h));
            renderer.Render(surface.Canvas, model, w, h, state); // the first pass lays the chart out, the second draws it
            renderer.Render(surface.Canvas, model, w, h, state);
            var dir = folder ?? Path.Combine(TradingHub.Common.Product.Documents, "Screenshots");
            Directory.CreateDirectory(dir);
            var name = string.Concat($"{tab.Symbol}-{DateTime.Now:yyyyMMdd-HHmmss}.png".Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(dir, name);
            using (var image = surface.Snapshot())
            using (var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 92))
                File.WriteAllBytes(path, png.ToArray());
            _status("Picture saved: " + path);
            return path;
        }
        catch (Exception ex)
        {
            _status("Could not save the picture: " + ex.Message);
            CrashLog.Write(ex);
            return null;
        }
    }

    /// <summary>
    /// Journal note: asks for a text and appends it, with the time, the instrument, the last price and a picture of the chart,
    /// to Documents\Trading Hub\Journal\yyyy-MM-dd.md.
    /// </summary>
    internal async Task AddJournalNoteAsync(ChartTab? tab)
    {
        if (tab?.Model is not { } model || TopLevel.GetTopLevel(this) is not Window owner) return;
        var note = await NoteDialog.ShowAsync(owner, "Journal note", $"{tab.Symbol} · {model.Period.DisplayName}: what do you see, what is the idea? A picture of the chart is saved with it.");
        if (string.IsNullOrWhiteSpace(note)) return;
        try
        {
            var dir = Path.Combine(TradingHub.Common.Product.Documents, "Journal");
            var picture = SaveChartPicture(tab, Path.Combine(dir, "images"));
            var tz = model.Settings.TimeZone;
            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var last = model.Data.Trades.Count > 0 ? model.Data.Trades.GetPrice(model.Data.Trades.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";
            var entry = $"## {now:HH:mm} · {tab.Symbol} · {model.Period.DisplayName} · last {last}{Environment.NewLine}{Environment.NewLine}{note}{Environment.NewLine}"
                        + (picture != null ? $"{Environment.NewLine}![chart](images/{Path.GetFileName(picture)}){Environment.NewLine}" : "") + Environment.NewLine;
            Directory.CreateDirectory(dir);
            await File.AppendAllTextAsync(Path.Combine(dir, $"{now:yyyy-MM-dd}.md"), entry);
            _status("Journal note saved (Documents / Trading Hub / Journal)");
        }
        catch (Exception ex)
        {
            _status("Could not save the note: " + ex.Message);
            CrashLog.Write(ex);
        }
    }

    /// <summary>Position size calculator for the active chart's instrument (the entry starts at the last price).</summary>
    internal async Task OpenRiskAsync(ChartTab? tab)
    {
        if (tab?.Model is not { } model || TopLevel.GetTopLevel(this) is not Window owner) return;
        var data = model.Data;
        decimal entry = data.Trades.Count > 0 ? data.Trades.GetPrice(data.Trades.Count - 1) : 0;
        await RiskDialog.ShowAsync(owner, data.Instrument, entry, _svc.Settings.RiskPerTrade, r =>
        {
            if (r == _svc.Settings.RiskPerTrade) return;
            _svc.Settings.RiskPerTrade = r;
            _svc.Settings.Save();
        });
    }

    /// <summary>Saves a picture of the chart for an alert and adds it to today's journal (Journal\\alerts).</summary>
    private void SnapshotAlert(AlertInfo a)
    {
        try
        {
            var tab = AllCharts.FirstOrDefault(t => t.Symbol.Equals(a.Instrument, StringComparison.OrdinalIgnoreCase)) ?? _active;
            if (tab?.Model == null) return;
            var dir = Path.Combine(TradingHub.Common.Product.Documents, "Journal");
            var picture = SaveChartPicture(tab, Path.Combine(dir, "alerts"));
            if (picture == null) return;
            var entry = $"## {a.Time:HH:mm} · alert · {a.Instrument}{Environment.NewLine}{Environment.NewLine}{a.Message}{Environment.NewLine}{Environment.NewLine}![chart](alerts/{Path.GetFileName(picture)}){Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, $"{a.Time:yyyy-MM-dd}.md"), entry);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex); // a picture is a bonus: the alert itself must still show
        }
    }

    /// <summary>A read-only text window (used for the answers of the tools below).</summary>
    private void ShowText(string title, string text)
    {
        var box = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Text = text, FontSize = 13 };
        var w = new Window { Title = title, Width = 720, Height = 560, Background = Ui.Bg2, Content = new DockPanel { Margin = new Thickness(14), Children = { box } } };
        if (TopLevel.GetTopLevel(this) is Window owner) w.Show(owner);
        else w.Show();
    }

    /// <summary>"Have we seen this before?": the earlier candles that looked most like the latest one and what price did after them.</summary>
    internal void ShowSimilarMoments(ChartTab? tab)
    {
        if (tab?.Model is not { } model) return;
        var candles = model.Series.Candles;
        const int horizon = 30;
        if (candles.Count < TradingHub.Data.SimilarMoments.Warmup + horizon + 100)
        {
            ShowText("Similar moments", $"Not enough candles loaded ({candles.Count}). Load more history first (the clock button on the toolbar): at least a few hours of 1 minute candles, ideally days.");
            return;
        }

        int now = candles.Count - 2; // the last closed candle
        var result = TradingHub.Data.SimilarMoments.Find(candles, now, horizon, 12);
        var tz = model.Settings.TimeZone;
        string Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz).ToString("dd/MM HH:mm");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{tab.Symbol} · {model.Period.DisplayName} · the latest closed candle is {Local(candles[now].Time)}");
        sb.AppendLine($"I compared it with the {candles.Count:N0} candles loaded (recent move, delta, volume, range and position in the last hour) and kept the {result.Matches.Count} most similar, at least 15 candles apart.");
        sb.AppendLine().AppendLine($"What price did {horizon} candles after each of them:");
        foreach (var m in result.Matches)
            sb.AppendLine($"  {Local(candles[m.Index].Time)}   similarity gap {m.Distance:0.0}   {m.AheadTicks:+0;-0;0} ticks");
        if (result.Matches.Count > 0)
            sb.AppendLine().AppendLine($"Higher after {result.Up} of {result.Matches.Count}, lower after {result.Down}; median {result.Median:+0;-0;0} ticks.");
        sb.AppendLine().AppendLine("This describes the loaded past only. The sample is small and the market changes: it is something to look at, not a forecast.");
        ShowText("Similar moments", sb.ToString());
    }

    /// <summary>Asks Claude (the user's Claude Code session) about the chart: a picture of it plus the levels, alerts and next event.</summary>
    internal async Task AskChartAsync(ChartTab? tab)
    {
        if (tab?.Model is not { } model || TopLevel.GetTopLevel(this) is not Window owner) return;
        var question = await NoteDialog.ShowAsync(owner, "Ask Claude about this chart", $"{tab.Symbol} · {model.Period.DisplayName}. A picture of the chart, your saved levels and today's alerts are sent to Claude Code with your question. Nothing else.");
        if (string.IsNullOrWhiteSpace(question)) return;
        try
        {
            var dir = Path.Combine(AppPaths.Root, "ask-chart");
            Directory.CreateDirectory(dir);
            foreach (var old in new DirectoryInfo(dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Skip(10)) old.Delete();
            var picture = SaveChartPicture(tab, dir);
            if (picture == null) return;

            var ctx = new System.Text.StringBuilder();
            ctx.AppendLine($"Instrument {tab.Symbol}, chart period {model.Period.DisplayName}, now {DateTime.Now:yyyy-MM-dd HH:mm} local, last price {model.Data.LastPrice}.");
            if (TodayReport.Load().FirstOrDefault(l => string.Equals(l.Instrument, tab.Symbol, StringComparison.OrdinalIgnoreCase)) is { Levels: { Count: > 0 } levels })
            {
                ctx.AppendLine("Remembered big-trade levels price has not come back to (largest first):");
                foreach (var l in levels.Where(l => !l.Touched && !l.Hidden).OrderByDescending(l => l.Volume).Take(8))
                    ctx.AppendLine($"  {(l.Buy ? "buy" : "sell")} {l.Volume:0.##} @ {l.Price} ({l.Time.ToLocalTime():dd/MM HH:mm})");
                ctx.AppendLine(TodayReport.Scoreboard(levels));
            }

            var alerts = AlertLog.Since(DateTime.Now.Date);
            if (alerts.Count > 0) ctx.AppendLine("Alerts today: " + string.Join(" | ", alerts.TakeLast(6).Select(a => $"{a.Time:HH:mm} {a.Text}")));
            if (TradingHub.Indicators.Native.EventCalendar.Next(DateTime.UtcNow) is { } ev) ctx.AppendLine($"Next event: {ev.Name} at {ev.Utc.ToLocalTime():dd/MM HH:mm} local.");

            _status("Asking Claude…");
            var res = await new ClaudeAgent().RunAsync(new ClaudeAgentRequest
            {
                Role = "ask-chart",
                Prompt = $"Look at the chart picture {Path.GetFileName(picture)} in your folder (use the Read tool).{Environment.NewLine}{Environment.NewLine}Question: {question}{Environment.NewLine}{Environment.NewLine}Data from the trader's platform:{Environment.NewLine}{ctx}",
                SystemPrompt = "You are a careful order-flow analyst helping a discretionary futures trader read a chart. Answer the question from the picture and the data given only; say when something cannot be seen. Be concise (under 200 words), concrete about prices and times, and do not give trade instructions or promise outcomes.",
                WorkDir = dir,
                Tools = new[] { "Read" },
                PermissionMode = "dontAsk",
                Model = ClaudeCodeBridge.Model,
                Effort = "medium",
                MaxUsd = 1.0,
                Timeout = TimeSpan.FromMinutes(4),
            }, CancellationToken.None);
            if (res.Ok && res.Text.Trim().Length > 0)
            {
                _status("Claude answered.");
                ShowText("Claude's read of " + tab.Symbol, res.Text.Trim());
            }
            else _status("Claude did not answer: " + (res.NeedsLogin ? "sign in to Claude Code first (run claude in a terminal and use /login)" : res.Error ?? "no text"));
        }
        catch (Exception ex)
        {
            _status("Could not ask Claude: " + ex.Message);
            CrashLog.Write(ex);
        }
    }

    private void OpenFlashcards()
    {
        var w = new FlashcardWindow();
        if (TopLevel.GetTopLevel(this) is Window owner) w.Show(owner);
        else w.Show();
    }

    /// <summary>Asks Claude to explain the Auction map's profile in plain language, from the picture and the indicator's own numbers.</summary>
    internal async Task ExplainProfileAsync(ChartTab? tab)
    {
        if (tab?.Model is not { } model || TopLevel.GetTopLevel(this) is not Window) return;
        var map = model.Indicators.Select(s => s.Indicator).OfType<TradingHub.Indicators.Native.AuctionMap>().FirstOrDefault();
        string facts = map?.Facts ?? "";
        if (map == null || facts.Length == 0)
        {
            _status("Add the Auction map indicator to this chart first (Indicators → Auction map).");
            return;
        }

        try
        {
            var dir = Path.Combine(AppPaths.Root, "ask-chart");
            Directory.CreateDirectory(dir);
            var picture = SaveChartPicture(tab, dir);
            if (picture == null) return;
            _status("Asking Claude to explain the profile…");
            var res = await new ClaudeAgent().RunAsync(new ClaudeAgentRequest
            {
                Role = "explain-profile",
                Prompt = $"Look at the chart picture {Path.GetFileName(picture)} in your folder (use the Read tool): it shows {tab.Symbol} with the Auction map indicator (TPO + volume profile). Explain the auction in plain language, in at most 200 words: where value is, what has been accepted or rejected, what unfinished business is left and how far it is, and what to watch next. Use ONLY the facts below and what you can see; say when something cannot be told. No trade instructions.{Environment.NewLine}{Environment.NewLine}Facts from the platform:{Environment.NewLine}{facts}",
                SystemPrompt = "You are a market-profile (auction market theory) teacher explaining one session's profile to a discretionary futures trader. Be concrete about prices, concise, and honest about uncertainty. The platform's statistics describe the past only.",
                WorkDir = dir,
                Tools = new[] { "Read" },
                PermissionMode = "dontAsk",
                Model = ClaudeCodeBridge.Model,
                Effort = "medium",
                MaxUsd = 1.0,
                Timeout = TimeSpan.FromMinutes(4),
            }, CancellationToken.None);
            if (res.Ok && res.Text.Trim().Length > 0)
            {
                _status("Claude explained the profile.");
                ShowText("The auction, explained · " + tab.Symbol, res.Text.Trim());
            }
            else _status("Claude did not answer: " + (res.NeedsLogin ? "sign in to Claude Code first (run claude in a terminal and use /login)" : res.Error ?? "no text"));
        }
        catch (Exception ex)
        {
            _status("Could not ask Claude: " + ex.Message);
            CrashLog.Write(ex);
        }
    }

    private Window? _bigOrdersWindow;

    /// <summary>Makes the chart showing an instrument the active one (false if no chart shows it).</summary>
    internal bool ActivateSymbol(string feedId, string symbol)
    {
        var tab = AllCharts.FirstOrDefault(t => t.FeedId == feedId && t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        if (tab == null) return false;
        Activate(tab);
        return true;
    }

    /// <summary>Big orders on every open instrument as they print (double click a line to go to its chart).</summary>
    internal void OpenBigOrders()
    {
        if (_bigOrdersWindow != null)
        {
            _bigOrdersWindow.Activate();
            return;
        }

        var feed = _svc.BigOrders;
        var list = new ListBox { FontFamily = new FontFamily(Plat.Mono), FontSize = 12 };
        var sound = new CheckBox { Content = "Sound", IsChecked = false };
        var tz = TimeZoneInfo.Local;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(_svc.Settings.TimeZoneId); } catch (Exception) { /* the machine's own time zone */ }
        TextBlock Row(BigOrderLine l) => new()
        {
            Text = $"{TimeZoneInfo.ConvertTimeFromUtc(l.Time, tz):HH:mm:ss}  {l.Symbol,-8} {(l.Order.Buy ? "BUY " : "SELL")} {l.Order.Volume,8:0.##} @ {l.Order.Price}",
            Foreground = l.Order.Buy ? Ui.Up : Ui.Down,
        };
        foreach (var l in feed.Lines.Reverse()) list.Items.Add(Row(l));
        void OnAdded(BigOrderLine l)
        {
            list.Items.Insert(0, Row(l));
            while (list.Items.Count > 300) list.Items.RemoveAt(list.Items.Count - 1);
            if (sound.IsChecked == true) Sound.Play();
        }

        feed.Added += OnAdded;
        var note = Ui.Faint("Only instruments that are open on a chart are watched. A big order is far above that instrument's usual (the top 0.3% of recent orders).");
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            var l = feed.Lines[feed.Lines.Count - 1 - list.SelectedIndex];
            if (!ActivateSymbol(l.FeedId, l.Symbol)) _status($"{l.Symbol} is not open on a chart");
        };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 0, 0, 8), Children = { sound, note } };
        var w = new Window
        {
            Title = "Big orders",
            Width = 520,
            Height = 560,
            Background = Ui.Bg2,
            Content = new DockPanel { Margin = new Thickness(12), Children = { Docked(top, Dock.Top), list } },
        };
        w.Closed += (_, _) =>
        {
            feed.Added -= OnAdded;
            _bigOrdersWindow = null;
        };
        _bigOrdersWindow = w;
        if (TopLevel.GetTopLevel(this) is Window owner) w.Show(owner);
        else w.Show();

        static Control Docked(Control c, Dock d)
        {
            DockPanel.SetDock(c, d);
            return c;
        }
    }

    private Window? _todayWindow;

    internal void OpenGuide() => ShowText("Guide", TradingHub.App.Services.ViewGuide.Text);

    /// <summary>The Today window: alerts, today's levels per instrument, the level scoreboard and yesterday's recap (it refreshes on its own).</summary>
    internal void OpenToday()
    {
        if (_todayWindow != null)
        {
            _todayWindow.Activate();
            return;
        }

        var box = new TextBox { IsReadOnly = true, FontFamily = new FontFamily(Plat.Mono), FontSize = 12, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, Text = TodayReport.Build(_svc) };
        void Refresh() => box.Text = TodayReport.Build(_svc);
        var refresh = Ui.Button("Refresh", MaterialIconKind.Refresh, Refresh);
        var mine = Ui.Button("Mine levels from ticks", MaterialIconKind.Pickaxe, async () =>
        {
            box.Text = "Reading the recorded ticks and finding big orders... (this can take a minute)";
            var text = await Task.Run(() =>
            {
                try { return LevelTools.Mine(); }
                catch (Exception ex) { CrashLog.Write(ex); return "Could not mine: " + ex.Message; }
            });
            Refresh();
            box.Text = "MINING RESULT\n" + text + "\n\n" + box.Text;
        });
        var lab = Ui.Button("Shadow lab", MaterialIconKind.FlaskOutline, async () =>
        {
            ShowText("Shadow lab", "Working... close and reopen this if it stays empty.");
            var text = await Task.Run(() =>
            {
                try { return LevelTools.ShadowLab(); }
                catch (Exception ex) { CrashLog.Write(ex); return "Could not run the lab: " + ex.Message; }
            });
            ShowText("Shadow lab", text);
        });
        var brief = Ui.Button("Briefing now", MaterialIconKind.WeatherSunsetUp, () => ShowText("Briefing", Briefing.Compose(_svc)));
        var edge = Ui.Button("Edge finder", MaterialIconKind.MagnifyScan, async () =>
        {
            ShowText("Edge finder", "Working on the recorded ticks (about a minute)... close and reopen this window if it stays empty.");
            var text = await Task.Run(() =>
            {
                try { return Research.RunEdgeFinder(); }
                catch (Exception ex) { CrashLog.Write(ex); return "Could not run the edge finder: " + ex.Message; }
            });
            ShowText("Edge finder", text);
        });
        var cross = Ui.Button("Cross-market", MaterialIconKind.SwapHorizontal, async () =>
        {
            ShowText("Cross-market", "Working on the recorded ticks (about a minute)... close and reopen this window if it stays empty.");
            var text = await Task.Run(() =>
            {
                try { return Research.RunCrossMarket(); }
                catch (Exception ex) { CrashLog.Write(ex); return "Could not run the study: " + ex.Message; }
            });
            ShowText("Cross-market", text);
        });
        Button ResearchButton(string label, MaterialIconKind icon, string title, Func<string> run) => Ui.Button(label, icon, async () =>
        {
            ShowText(title, "Working on the recorded ticks (up to a minute)... close and reopen this window if it stays empty.");
            var text = await Task.Run(() =>
            {
                try { return run(); }
                catch (Exception ex) { CrashLog.Write(ex); return "Could not run: " + ex.Message; }
            });
            ShowText(title, text);
        });
        var placebo = ResearchButton("Placebo test", MaterialIconKind.DiceMultipleOutline, "Placebo test", ResearchExtra.Placebo);
        var swing = ResearchButton("Swing baseline", MaterialIconKind.ChartLineVariant, "Swing-level baseline", ResearchExtra.SwingBaseline);
        var side = ResearchButton("Side accuracy", MaterialIconKind.ScaleBalance, "Side accuracy", ResearchExtra.SideAccuracy);
        var hyp = ResearchButton("Hypotheses", MaterialIconKind.ClipboardTextClockOutline, "Hypotheses", ResearchExtra.Hypotheses);
        var atlas = ResearchButton("Event atlas", MaterialIconKind.CalendarAlertOutline, "Event impact atlas", ResearchExtra.EventAtlas);
        var cost = ResearchButton("Cost map", MaterialIconKind.CashMultiple, "Cost map", () => ResearchHistory.CostMap(_svc));
        var season = ResearchButton("Seasonality", MaterialIconKind.WeatherSunnyAlert, "Seasonality", ResearchHistory.Seasonality);
        var rules = ResearchButton("Rule lab", MaterialIconKind.TestTube, "Rule lab", () => ResearchHistory.RuleLab(_svc));
        var stops = ResearchButton("Stops in nodes", MaterialIconKind.OctagonOutline, "Stops and volume nodes", ResearchAuction.StopsInNodes);
        var accuracy = ResearchButton("Profile accuracy", MaterialIconKind.TargetVariant, "Profile accuracy", ResearchAuction.ProfileAccuracy);
        var download = Ui.Button("Download more history", MaterialIconKind.DownloadOutline, async () =>
        {
            ShowText("Download more history", "Downloading about 13 months of 1-minute candles for ES.F, NQ.F and SI.F from London Strategic Edge (a few minutes; closed months are stored and not downloaded twice)...");
            var text = await Task.Run(async () =>
            {
                try { return await HistoryDownload.RunAsync(_svc.Connections, new[] { "ES.F", "NQ.F", "SI.F" }); }
                catch (Exception ex) { CrashLog.Write(ex); return "Could not download: " + ex.Message; }
            });
            ShowText("Download more history", text + Environment.NewLine + Environment.NewLine + "Now run Auction claims again to repeat the tests on these markets.");
        });
        var claims = ResearchButton("Auction claims", MaterialIconKind.ScaleBalance, "Auction claims test", ResearchAuction.Claims);
        var opens = ResearchButton("Open & day types", MaterialIconKind.WeatherSunsetUp, "Open location and day types", ResearchAuction.OpenAndDayTypes);
        var profileCards = Ui.Button("Profile flashcards", MaterialIconKind.CardsOutline, () =>
        {
            var w = new ProfileFlashcardWindow();
            if (TopLevel.GetTopLevel(this) is Window owner) w.Show(owner);
            else w.Show();
        });
        var cards = Ui.Button("Flashcards", MaterialIconKind.CardsOutline, () => OpenFlashcards());
        var asOf = Ui.Button("As of…", MaterialIconKind.ClockTimeFourOutline, async () =>
        {
            if (TopLevel.GetTopLevel(this) is not Window owner) return;
            var answer = await PromptDialog.ShowAsync(owner, "As of", "Date and local time (yyyy-MM-dd HH:mm)", DateTime.Now.AddHours(-1).ToString("yyyy-MM-dd HH:mm"), "Show");
            if (answer == null) return;
            if (!DateTime.TryParseExact(answer.Trim(), "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var when)) ShowText("As of", "Use the form 2026-10-23 14:30.");
            else ShowText("As of " + when.ToString("yyyy-MM-dd HH:mm"), AsOf.Build(when));
        });
        var export = Ui.Button("Export levels (TradingView + CSV)", MaterialIconKind.Export, () =>
        {
            try
            {
                var (folder, summary) = LevelExport.Run();
                Refresh();
                box.Text = summary + Environment.NewLine + Environment.NewLine + "Files: " + folder + Environment.NewLine + Environment.NewLine + box.Text;
                Shell.OpenFolder(folder);
            }
            catch (Exception ex)
            {
                box.Text = "Could not export: " + ex.Message;
                CrashLog.Write(ex);
            }
        });
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        var w = new Window
        {
            Title = "Today",
            Width = 780,
            Height = 720,
            Background = Ui.Bg2,
            Content = new DockPanel { Margin = new Thickness(14), Children = { Docked(new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, Children = { refresh, mine, lab, edge, cross, placebo, swing, side, hyp, atlas, cost, season, rules, download, claims, opens, stops, accuracy, cards, profileCards, asOf, brief, export } }, Dock.Top), box } },
        };
        w.Closed += (_, _) =>
        {
            timer.Stop();
            _todayWindow = null;
        };
        _todayWindow = w;
        if (TopLevel.GetTopLevel(this) is Window owner) w.Show(owner);
        else w.Show();

        static Control Docked(Control c, Dock d)
        {
            c.Margin = new Thickness(0, 0, 0, 10);
            c.HorizontalAlignment = HorizontalAlignment.Left;
            DockPanel.SetDock(c, d);
            return c;
        }
    }

    /// <summary>The connection's instrument list; a feed that is not ready yet (starting up, network) is retried instead of leaving the chart empty.</summary>
    private async Task<IReadOnlyList<Instrument>> InstrumentsWithRetryAsync(ConnectionProfile profile)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { return await InstrumentPicker.InstrumentsAsync(_svc, profile.Id); }
            catch (Exception ex) when (attempt < 5)
            {
                CrashLog.Write(ex);
                _status($"{profile.Name} is not ready ({ex.Message}); retrying {attempt}/4…");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
        }
    }

    /// <summary>Loads (or changes) a chart's instrument. Copies the indicators of the chart given.</summary>
    private async Task OpenInTabAsync(ChartTab tab, string feedId, string symbol, ChartPeriod period, ChartSettings? settings, ChartTab? copyIndicatorsFrom)
    {
        try
        {
            var profile = _svc.Connections.Find(feedId);
            if (profile == null)
            {
                _status($"The connection {feedId} does not exist; check Connections.");
                return;
            }

            var instruments = await InstrumentsWithRetryAsync(profile);
            var inst = instruments.FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            if (inst == null)
            {
                _status($"{symbol} was not found in {profile.Name}");
                return;
            }

            bool instrumentChanged = tab.Model != null && (tab.FeedId != profile.Id || !tab.Symbol.Equals(inst.Symbol, StringComparison.OrdinalIgnoreCase));
            var oldIndicators = copyIndicatorsFrom?.Model?.Indicators.Select(s => s.Indicator).ToList();
            tab.Model?.Dispose();
            var data = _svc.Open(profile.Id, inst);
            var model = new ChartModel(data, period, settings ?? new ChartSettings())
            {
                Trading = _svc.SimBrokerFor(data),
                Statistics = _svc.StatsProvider,
            };
            model.Settings.TimeZoneId = _svc.Settings.TimeZoneId;
            model.Viewport.BarWidth = model.Settings.Style == ChartStyle.Footprint ? 110 : 8;
            model.AlertRaised += a => Dispatcher.UIThread.Post(() => ShowAlert(a));
            tab.Model = model;
            tab.FeedId = profile.Id;
            tab.Symbol = inst.Symbol;
            tab.View.Model = model;
            // a Rithmic account is kept even while its session is still connecting
            if (!tab.AccountId.StartsWith("rithmic|", StringComparison.Ordinal) && !_svc.AccountsFor(data).Any(a => a.Id == tab.AccountId)) tab.AccountId = "sim";
            if (instrumentChanged && tab.CrossSymbol != null)
            {
                tab.CrossSymbol = null;
                tab.CrossData = null;
            }

            model.Account = AccountOf(tab) ?? _svc.AccountFor(data, tab.AccountId);
            model.Brackets = _svc.BracketsFor(model.Account);
            tab.Trader?.Rebind();
            Workspace.LoadDrawings(tab);
            if (tab.HistoryHours != 0) _ = LoadHistoryAsync(tab, tab.HistoryHours, quiet: true);

            if (oldIndicators != null)
            {
                foreach (var ind in oldIndicators)
                {
                    var copy = Workspace.CloneIndicator(_svc, ind);
                    if (copy != null) model.AddIndicator(copy, applyDefaultColors: false);
                }
            }

            UpdateTitles(tab);
            if (tab.Strategies.Count > 0) RestartStrategies(tab, instrumentChanged);
            if (ReferenceEquals(tab, _active)) BindSidePanel(tab, force: true);
            if (ReferenceEquals(tab, _active)) _toolbar.Chart = tab;
            tab.Window?.Toolbar.Sync();
            if (!_linkSync) _ = PropagateSymbolAsync(tab);
            _status($"Opening {inst.Symbol} on {profile.Name}…");
            void Ready()
            {
                data.HistoryLoaded -= Ready;
                if (tab.Model?.Data != data) return;
                var from = TimeZoneInfo.ConvertTimeFromUtc(data.Trades.Count > 0 ? data.Trades.FirstTime : DateTime.UtcNow, model.Settings.TimeZone);
                _status(data.HistoryError != null
                    ? $"{inst.Symbol}: the full history could not be fetched ({data.HistoryError})"
                    : $"{inst.Symbol} ready · {data.Trades.Count:N0} trades from {from:dd/MM HH:mm}");
            }

            if (data.IsLoadingHistory) data.HistoryLoaded += Ready;
        }
        catch (Exception ex)
        {
            _status("Error while opening: " + ex.Message);
            CrashLog.Write(ex);
        }
    }

    /// <summary>Makes a chart of the main window active (bar, DOM and ticket move to it).</summary>
    internal void Activate(ChartTab? chart)
    {
        if (chart == null || chart.Window != null) return;
        var page = chart.Page;
        if (page == null) return;
        bool changed = !ReferenceEquals(chart, _active);
        page.Active = chart;
        if (!ReferenceEquals(page, _page))
        {
            _switching = true;
            _tabControl.SelectedItem = page.Item;
            _switching = false;
            _page = page;
        }

        _active = chart;
        UpdateFrames(page);
        UpdatePageHeader(page);
        if (!changed) return;
        _toolbar.Chart = chart;
        _layoutChoice.Select(page.Layout);
        BindSidePanel(chart, force: false);
        UpdateReplayBar();
    }

    private void OnPageChanged()
    {
        if (_switching) return;
        _page = _pages.FirstOrDefault(p => ReferenceEquals(p.Item, _tabControl.SelectedItem));
        if (_page == null) return;
        var chart = _page.Active ?? _page.Charts.FirstOrDefault();
        _active = null; // forces the toolbar and panel to re-attach
        Activate(chart);
        _layoutChoice.Select(_page.Layout);
    }

    private void DisposeChart(ChartTab chart)
    {
        // the chart's strategies are stopped (open positions are left as they are)
        foreach (var cs in chart.Strategies) StopStrategy(cs);
        chart.Model?.Dispose();
        chart.OnClosed?.Invoke();
        chart.Page = null;
        chart.Window = null;
    }

    /// <summary>Closes a chart (from a tab or from a separate window).</summary>
    internal void CloseChart(ChartTab chart)
    {
        if (chart.Window is { } w)
        {
            w.Close();
            return;
        }

        var page = chart.Page;
        if (page == null) return;
        if (page.Charts.Count == 1)
        {
            ClosePage(page);
            return;
        }

        page.Charts.Remove(chart);
        DisposeChart(chart);
        if (ReferenceEquals(page.Active, chart)) page.Active = page.Charts[0];
        if (ReferenceEquals(page.Maximized, chart)) page.Maximized = null;
        ShrinkLayout(page);
        Arrange(page);
        if (ReferenceEquals(_active, chart))
        {
            _active = null;
            Activate(page.Active);
        }
    }

    private void ClosePage(ChartPage page)
    {
        if (_pages.Count <= 1)
        {
            _status("At least one tab has to stay open.");
            return;
        }

        foreach (var c in page.Charts.ToList()) DisposeChart(c);
        page.Charts.Clear();
        _pages.Remove(page);
        _tabControl.Items.Remove(page.Item);
    }

    /// <summary>Closes the active chart (Cmd+W).</summary>
    internal void CloseActiveTab()
    {
        if (_active != null) CloseChart(_active);
    }

    /// <summary>Pulls the chart out of its tab and leaves it alone in a new tab.</summary>
    internal async Task MoveToNewPageAsync(ChartTab chart)
    {
        var from = chart.Page;
        if (from == null || from.Charts.Count == 1) return;
        from.Charts.Remove(chart);
        if (ReferenceEquals(from.Active, chart)) from.Active = from.Charts[0];
        if (ReferenceEquals(from.Maximized, chart)) from.Maximized = null;
        ShrinkLayout(from);
        Arrange(from);
        var page = CreatePage();
        page.Charts.Add(chart);
        page.Active = chart;
        chart.Page = page;
        _pages.Add(page);
        Arrange(page);
        _tabControl.Items.Add(page.Item);
        _active = null;
        Activate(chart);
        await Task.CompletedTask;
    }

    // ===================================================================== separate windows

    internal void DetachActive()
    {
        if (_active != null) Detach(_active);
    }

    /// <summary>Pulls a chart out into its own window (it can be moved to another monitor).</summary>
    internal ChartWindow? Detach(ChartTab chart, PixelPoint? position = null, Size? size = null)
    {
        var page = chart.Page;
        if (page == null) return null;
        bool lastChart = _pages.Count == 1 && page.Charts.Count == 1;
        page.Charts.Remove(chart);
        if (ReferenceEquals(page.Active, chart)) page.Active = page.Charts.FirstOrDefault();
        chart.Page = null;
        // in its own window it has all four rounded corners again (flush against the right panel it did not)
        chart.Frame.BorderThickness = new Thickness(1);
        chart.Frame.CornerRadius = new CornerRadius(8);
        if (chart.Frame.Child is Border innerFrame) innerFrame.CornerRadius = new CornerRadius(7);

        var w = new ChartWindow(this, chart);
        // it only returns to the stored position if that spot is still on some screen
        if (position is { } p && w.Screens.All.Any(sc => sc.WorkingArea.Contains(p))) w.Position = p;
        if (size is { } s)
        {
            w.Width = s.Width;
            w.Height = s.Height;
        }

        _floating.Add(w);
        w.Closed += (_, _) => OnFloatingClosed(w);
        w.Show();

        if (page.Charts.Count == 0)
        {
            if (lastChart)
            {
                // the main window is never left with no charts: it keeps a copy
                _pages.Remove(page);
                _tabControl.Items.Remove(page.Item);
                _ = NewTabAsync(chart.FeedId, chart.Symbol, chart.Model?.Period ?? ChartPeriod.M5, chart.Model?.Settings.Clone(), accountId: chart.AccountId);
            }
            else
            {
                _pages.Remove(page);
                _tabControl.Items.Remove(page.Item);
            }
        }
        else
        {
            Arrange(page);
            if (ReferenceEquals(_active, chart))
            {
                _active = null;
                Activate(page.Active);
            }
        }

        _status($"{chart.Caption} in a separate window: move it to another monitor if you like. To go back, use “Back to the main window”.");
        return w;
    }

    /// <summary>Returns a chart from a separate window to the active tab (or to a new one if it is full).</summary>
    internal void DockBack(ChartWindow w)
    {
        var chart = w.Chart;
        w.ReleaseChart();
        _floating.Remove(w);
        chart.Window = null;
        chart.Frame.Margin = new Thickness(0); // in a tab the spacing comes from the grid
        var page = _page;
        if (page != null && page.Charts.Count < PaneCount(page.Layout))
        {
            page.Charts.Add(chart);
            chart.Page = page;
            Arrange(page);
        }
        else
        {
            page = CreatePage();
            page.Charts.Add(chart);
            chart.Page = page;
            _pages.Add(page);
            Arrange(page);
            _tabControl.Items.Add(page.Item);
        }

        _active = null;
        Activate(chart);
        w.Close();
    }

    private void OnFloatingClosed(ChartWindow w)
    {
        if (!_floating.Remove(w)) return;
        if (!w.Released) DisposeChart(w.Chart);
    }

    /// <summary>Closes the separate windows (when quitting the app).</summary>
    internal void CloseFloating()
    {
        foreach (var w in _floating.ToList()) w.Close();
        ClosePanelWindowsForShutdown();
    }

    internal void RefreshLayoutBoxes()
    {
        _toolbar.RefreshLayoutBox();
        foreach (var w in _floating) w.Toolbar.RefreshLayoutBox();
    }

    // ===================================================================== replay

    private readonly Border _replayBar = new() { IsVisible = false };
    private readonly ToggleButton _replayPlay = new() { Width = 34, Height = 30, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _replayClock = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
    private readonly ProgressBar _replayProgress = new() { Minimum = 0, Maximum = 1, Height = 4, MinWidth = 160, VerticalAlignment = VerticalAlignment.Center };
    private Ui.Segmented<double>? _replaySpeed;

    private ReplayFeed? ActiveReplay => _active?.Model?.Data.Feed as ReplayFeed;

    private Control BuildReplayBar()
    {
        _replayPlay.Click += (_, _) =>
        {
            if (ActiveReplay is { } r) r.Paused = _replayPlay.IsChecked != true;
            UpdateReplayBar();
        };
        _replaySpeed = new Ui.Segmented<double>(new[] { 1.0, 2, 5, 10, 30, 60, 300 }.Select(v => (v, $"{v:0}×", (string?)null)));
        _replaySpeed.SelectionChanged += v =>
        {
            if (ActiveReplay is { } r) r.Speed = v;
        };
        Button Jump(string text, TimeSpan by) => Ui.Button(text, null, () =>
        {
            if (ActiveReplay is { } r) r.SeekForward(r.Clock + by);
        });

        var badge = new Border { Child = Ui.H(6, Ui.Icon(MaterialIconKind.PlayCircleOutline, 15, Ui.Accent), new TextBlock { Text = "REPLAY", FontSize = 11, FontWeight = FontWeight.Bold }), Classes = { "pill" } };
        var row = Ui.H(12, badge, _replayPlay, _replaySpeed, _replayClock, _replayProgress, Ui.H(6, Jump("+5 min", TimeSpan.FromMinutes(5)), Jump("+30 min", TimeSpan.FromMinutes(30)), Jump("+1 h", TimeSpan.FromHours(1))));
        row.Margin = new Thickness(12, 8);
        _replayBar.Child = row;
        _replayBar.Background = Ui.Bg1;
        _replayBar.BorderBrush = Ui.Line;
        _replayBar.BorderThickness = new Thickness(0, 1, 0, 0);
        return _replayBar;
    }

    private void UpdateReplayBar()
    {
        var r = ActiveReplay;
        _replayBar.IsVisible = r != null;
        if (r == null || _active?.Model == null) return;
        _replayPlay.IsChecked = !r.Paused && !r.IsFinished;
        _replayPlay.Content = Ui.Icon(r.Paused || r.IsFinished ? MaterialIconKind.Play : MaterialIconKind.Pause, 18);
        _replaySpeed?.Select(r.Speed);
        var local = TimeZoneInfo.ConvertTimeFromUtc(r.Clock, _active.Model.Settings.TimeZone);
        _replayClock.Text = r.IsFinished ? "Finished" : local.ToString("ddd dd/MM HH:mm:ss", CultureInfo.GetCultureInfo("en-US"));
        _replayProgress.Value = r.Progress;
    }

    /// <summary>Opens a replay in a new tab.</summary>
    internal async Task OpenReplayAsync(Instrument source, DateTime startUtc, DateTime endUtc, ChartPeriod period)
    {
        var data = _svc.OpenReplay(source, startUtc, endUtc, TimeSpan.FromHours(3));
        var page = CreatePage();
        var chart = CreateChart(data.Instrument.FeedId, source.Symbol, "sim", 0);
        chart.OnClosed = () => data.Feed.Dispose();
        page.Charts.Add(chart);
        page.Active = chart;
        chart.Page = page;
        var settings = _active?.Model?.Settings.Clone() ?? new ChartSettings();
        var model = new ChartModel(data, period, settings) { Trading = _svc.SimBrokerFor(data), Statistics = _svc.StatsProvider };
        model.Settings.TimeZoneId = _svc.Settings.TimeZoneId;
        model.Viewport.BarWidth = model.Settings.Style == ChartStyle.Footprint ? 110 : 8;
        model.Account = _svc.SimBrokerFor(data);
        model.Brackets = _svc.BracketsFor(model.Account);
        if (_active?.Model != null)
        {
            foreach (var ind in _active.Model.Indicators.Select(s => s.Indicator))
            {
                var copy = Workspace.CloneIndicator(_svc, ind);
                if (copy != null) model.AddIndicator(copy, applyDefaultColors: false);
            }
        }

        chart.Model = model;
        chart.View.Model = model;
        _pages.Add(page);
        Arrange(page);
        _tabControl.Items.Add(page.Item);
        _tabControl.SelectedItem = page.Item;
        UpdateTitles(chart);
        _status($"Replay of {source.Symbol} from {TimeZoneInfo.ConvertTimeFromUtc(startUtc, model.Settings.TimeZone):dd/MM HH:mm}: trade with the simulated account as if it were live.");
        await Task.CompletedTask;
    }

    // ===================================================================== history

    /// <summary>
    /// Loads history up to <paramref name="hours"/> back (or the whole history). If something is still
    /// loading, it waits and carries on. What is not on disk is fetched from the exchange: the recent part as ticks and the old part as candles.
    /// </summary>
    internal async Task LoadHistoryAsync(ChartTab chart, double hours, bool quiet = false)
    {
        var data = chart.Model?.Data;
        if (data == null) return;
        chart.HistoryHours = hours;
        string what = ChartToolbar.SpanText(hours);
        DateTime from;
        try
        {
            if (hours == ChartToolbar.AllHistory)
            {
                if (!quiet) _status($"Looking for where {chart.Symbol}'s history starts…");
                from = data.Feed is IKlineHistory k ? await k.FirstAvailableAsync(data.Instrument, CancellationToken.None) ?? data.Feed.Now.AddYears(-5) : data.Feed.Now.AddYears(-5);
            }
            else from = data.Feed.Now.AddHours(-hours);
        }
        catch (Exception ex)
        {
            _status($"Could not query {chart.Symbol}'s history: {ex.Message}");
            return;
        }

        // waits for whatever is loading to finish (for example, the initial history)
        for (int i = 0; i < 2400 && (data.IsLoadingHistory || data.IsExtending); i++) await Task.Delay(250);
        if (!ReferenceEquals(chart.Model?.Data, data)) return;
        if (!data.ExtendHistory(from))
        {
            if (!quiet) _status($"{chart.Symbol}: the requested history ({what}) is already loaded.");
            return;
        }

        if (!quiet || hours > 24) _status($"Loading {what} of {chart.Symbol}: whatever is not stored is fetched from the exchange…");
        for (int i = 0; i < 24000 && data.IsExtending; i++) await Task.Delay(250);
        if (!ReferenceEquals(chart.Model?.Data, data)) return;
        var tz = chart.Model!.Settings.TimeZone;
        string fromText = TimeZoneInfo.ConvertTimeFromUtc(data.Trades.Count > 0 ? data.Trades.FirstTime : from, tz).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        string ticks = data.RealTicksFrom < DateTime.MaxValue
            ? $" · footprint (ticks) from {TimeZoneInfo.ConvertTimeFromUtc(data.RealTicksFrom, tz):dd/MM HH:mm}; before that, 1 minute candles from the exchange (for more footprint, download days in Data)"
            : "";
        _status(data.HistoryError != null
            ? $"{chart.Symbol}: the full history could not be fetched ({data.HistoryError})"
            : $"{chart.Symbol}: history from {fromText} · {data.Trades.Count:N0} trades{ticks}");
    }

    // ===================================================================== tools and auxiliary windows

    internal void OpenIndicators() => OpenIndicators(_active);

    internal void OpenIndicators(ChartTab? chart, IndicatorSlot? slot = null)
    {
        if (chart?.Model == null) return;
        var owner = chart.Window ?? TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        var w = new IndicatorsWindow(_svc, chart.Model);
        w.Show(owner);
        if (slot != null) w.Select(slot);
    }

    /// <summary>A drawing changed: it is saved and every chart of the same instrument is redrawn.</summary>
    internal void DrawingsTouched(ChartTab chart)
    {
        if (!chart.IsReplay) Workspace.SaveDrawings(chart);
        foreach (var c in AllCharts)
        {
            if (ReferenceEquals(c.Model?.Drawings, chart.Model?.Drawings)) c.Model?.Invalidate();
        }
    }

    /// <summary>
    /// Marks on the chart the fills of its account on the instrument it trades (with cross trading, the other one's):
    /// they come from the stored history, so previous days' fills are visible too.
    /// </summary>
    private void UpdateTradeMarks(ChartTab c)
    {
        if (c.Model is not { } m || c.IsReplay) return;
        var inst = (c.CrossData ?? m.Data).Instrument;
        string key = m.Settings.ShowTradeMarks ? $"{c.AccountId}|{inst.Key}|{_svc.Journal.CountOf(c.AccountId)}" : "off";
        if (key == c.MarksKey) return;
        c.MarksKey = key;
        if (!m.Settings.ShowTradeMarks)
        {
            m.TradeMarks = Array.Empty<TradeMark>();
            m.TradeLinks = Array.Empty<TradeLink>();
            m.Invalidate();
            return;
        }

        // ESZ6 (Rithmic) es ES; BTCUSDT es BTCUSDT
        string root = System.Text.RegularExpressions.Regex.Replace(inst.Symbol, "[FGHJKMNQUVXZ][0-9]{1,2}$", "");
        bool Same(string symbol) =>
            symbol.Equals(inst.Symbol, StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.Replace(symbol, "[FGHJKMNQUVXZ][0-9]{1,2}$", "").Equals(root, StringComparison.OrdinalIgnoreCase);
        var fills = _svc.Journal.FillsOf(c.AccountId).Where(f => Same(f.Symbol)).ToList();
        m.TradeMarks = fills.Select(f => new TradeMark(f.Time, f.Price, f.Buy, f.Quantity)).ToList();
        m.TradeLinks = RoundTrips.Analyze(fills).Trades.Select(t => new TradeLink(t.OpenTime, t.OpenPrice, t.CloseTime, t.ClosePrice, t.IsLong, t.NetPnl)).ToList();
        m.Invalidate();
    }

    /// <summary>Light/dark mode changed: each chart rebuilds its colors on the new base and everything is redrawn.</summary>
    internal void OnThemeChanged()
    {
        foreach (var c in AllCharts) c.Model?.ApplyColors();
        _dom.InvalidateVisual();
        _tape.InvalidateVisual();
        InvalidateVisual();
    }

    /// <summary>New tab (+ button or Cmd+T): copies the active chart, or uses the new-tab template if there is one.</summary>
    internal async Task NewChartAsync()
    {
        var tab = await NewTabAsync(_active?.FeedId ?? SimulatedFeed.FeedId, _active?.Symbol ?? "SIM-BTC", _active?.Model?.Period ?? ChartPeriod.M5, _active?.Model?.Settings.Clone());
        if (_svc.Settings.DefaultChartTemplate is { } t && Workspace.ApplyChartTemplate(this, tab, t)) AfterTemplate(tab);
    }

    // ===================================================================== chart templates

    private void AfterTemplate(ChartTab tab)
    {
        if (ReferenceEquals(tab, _active)) _toolbar.Sync();
        tab.Window?.Toolbar.Sync();
        tab.Trader?.Rebind();
    }

    private async Task SaveChartTemplateAsync(ChartTab tab)
    {
        if (tab.Model == null || TopLevel.GetTopLevel(tab.Window ?? (Control)this) is not Window owner) return;
        string suggested = $"{tab.Symbol} {(tab.Model.Settings.Style == ChartStyle.Footprint ? "footprint" : tab.Model.Settings.Style.ToString().ToLowerInvariant())}";
        var name = await PromptDialog.ShowAsync(owner, "Save chart template",
            "The template's name. It stores the style, the footprint, the colors, what is shown and the indicators (not the instrument or the period).", suggested, "Save");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Workspace.SaveChartTemplate(tab, name);
            _status($"Chart template “{name.Trim()}” saved · apply it to any chart from Templates");
        }
        catch (Exception ex)
        {
            _status("Could not save the template: " + ex.Message);
        }
    }

    /// <summary>Template options for a chart (for the toolbar button and the right-click menu).</summary>
    internal List<Control> ChartTemplateItems(ChartTab tab)
    {
        var items = new List<Control>();
        var save = new MenuItem { Header = "Save this chart as a template…", Icon = Ui.Icon(MaterialIconKind.ContentSaveOutline, 16) };
        save.Click += (_, _) => _ = SaveChartTemplateAsync(tab);
        items.Add(save);
        var names = Workspace.ChartTemplateNames();
        if (names.Count == 0)
        {
            items.Add(new MenuItem { Header = "There are no stored templates yet", IsEnabled = false });
            return items;
        }

        items.Add(new Separator());
        string? def = _svc.Settings.DefaultChartTemplate;
        foreach (var n in names)
        {
            var apply = new MenuItem { Header = n == def ? $"{n}  · new tabs" : n, Icon = Ui.Icon(MaterialIconKind.BookmarkOutline, 16) };
            ToolTip.SetTip(apply, $"Apply “{n}” to this chart (it replaces its style, colors and indicators)");
            apply.Click += (_, _) =>
            {
                if (!Workspace.ApplyChartTemplate(this, tab, n)) return;
                AfterTemplate(tab);
                _status($"Template “{n}” applied to {tab.Symbol}");
            };
            items.Add(apply);
        }

        items.Add(new Separator());
        var forNew = new MenuItem { Header = "For new tabs", Icon = Ui.Icon(MaterialIconKind.StarOutline, 16) };
        MenuItem Choice(string header, string? value)
        {
            var mi = new MenuItem { Header = header, Icon = value == def ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null };
            mi.Click += (_, _) =>
            {
                _svc.Settings.DefaultChartTemplate = value;
                _svc.Settings.Save();
                _status(value == null ? "New tabs copy the active chart" : $"New tabs start with “{value}”");
            };
            return mi;
        }

        forNew.Items.Add(Choice("Copy the active chart (no template)", null));
        foreach (var n in names) forNew.Items.Add(Choice(n, n));
        items.Add(forNew);

        var delete = new MenuItem { Header = "Delete a template", Icon = Ui.Icon(MaterialIconKind.DeleteOutline, 16) };
        foreach (var n in names)
        {
            var mi = new MenuItem { Header = n };
            mi.Click += (_, _) =>
            {
                Workspace.DeleteChartTemplate(n);
                if (_svc.Settings.DefaultChartTemplate == n)
                {
                    _svc.Settings.DefaultChartTemplate = null;
                    _svc.Settings.Save();
                }

                _status($"Template “{n}” deleted");
            };
            delete.Items.Add(mi);
        }

        items.Add(delete);
        return items;
    }

    internal void ShowChartTemplates(ChartTab? tab, Control anchor)
    {
        if (tab?.Model == null) return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var i in ChartTemplateItems(tab)) flyout.Items.Add(i);
        flyout.ShowAt(anchor);
    }

    private readonly Dictionary<ChartTab, Window> _settingsWindows = new();

    /// <summary>Chart settings (colors, background, candles…). If it is already open, it is brought to the front.</summary>
    internal void OpenChartSettings(ChartTab? chart)
    {
        if (chart?.Model == null) return;
        if (_settingsWindows.TryGetValue(chart, out var open))
        {
            open.Activate();
            return;
        }

        if ((chart.Window ?? TopLevel.GetTopLevel(this) as Window) is not { } owner) return;
        var w = new ChartSettingsWindow(this, chart);
        _settingsWindows[chart] = w;
        w.Closed += (_, _) => _settingsWindows.Remove(chart);
        w.Show(owner);
    }

    /// <summary>Copies a chart's colors and what it shows to all the others. Returns how many it changed.</summary>
    internal int ApplyChartLook(ChartTab source)
    {
        if (source.Model is not { } src) return 0;
        int n = 0;
        foreach (var c in AllCharts)
        {
            if (ReferenceEquals(c, source) || c.Model is not { } m) continue;
            m.Settings.Colors = src.Settings.Colors == null ? null : new Dictionary<string, uint>(src.Settings.Colors);
            m.Settings.ShowGrid = src.Settings.ShowGrid;
            m.Settings.ShowSessionLines = src.Settings.ShowSessionLines;
            m.Settings.ShowLastPriceLine = src.Settings.ShowLastPriceLine;
            m.Settings.ShowCountdown = src.Settings.ShowCountdown;
            m.Settings.CenterOnPrice = src.Settings.CenterOnPrice;
            m.Settings.ShowInfoPanel = src.Settings.ShowInfoPanel;
            m.Settings.ShowTradeMarks = src.Settings.ShowTradeMarks;
            m.ApplyColors();
            n++;
        }

        Workspace.Save(this);
        return n;
    }

    private readonly Dictionary<Guid, Window> _drawingWindows = new();

    /// <summary>Full panel of a drawing (double click or menu). If it is already open, it is brought to the front.</summary>
    internal void EditDrawing(ChartTab chart, ChartDrawing d)
    {
        if (chart.Model == null) return;
        if (_drawingWindows.TryGetValue(d.Id, out var open))
        {
            open.Activate();
            return;
        }

        var owner = chart.Window ?? TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        var model = chart.Model;
        void Changed()
        {
            model.Invalidate();
            DrawingsTouched(chart);
        }

        var w = new DrawingSettingsWindow(_svc, d, model.Instrument, model.Period, Changed,
            delete: () =>
            {
                model.Drawings.Remove(d);
                Changed();
            },
            duplicate: () =>
            {
                var copy = d.Clone();
                double shift = model.LastLayout is { } l ? Math.Abs(l.Main.Top - l.Main.Bottom) * 0.04 : 0;
                copy.Points = copy.Points.Select(p => new ChartPoint(p.Time, p.Price + shift)).ToList();
                d.Selected = false;
                copy.Selected = true;
                model.Drawings.Add(copy);
                Changed();
            });
        _drawingWindows[d.Id] = w;
        w.Closed += (_, _) => _drawingWindows.Remove(d.Id);
        w.Show(owner);
    }

    internal void OpenFootprintEditor() => OpenFootprintEditor(_active);

    internal void OpenFootprintEditor(ChartTab? chart)
    {
        if (chart?.Model == null) return;
        var owner = chart.Window ?? TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;
        new FootprintEditorWindow(chart.Model, this).Show(owner);
    }

    private void ShowAlert(AlertInfo a)
    {
        if (_svc.Settings.AlertSnapshots) SnapshotAlert(a);
        _status($"🔔 {a.Source}: {a.Message}");
        AlertLog.Add(a.Time, $"{a.Instrument} · {a.Source}: {a.Message}");
        if (!AlertControl.IsMuted)
        {
            TelegramNotifier.Notify(_svc.Settings, $"{a.Instrument} · {a.Message}");
            SpokenAlerts.Say(_svc.Settings, $"{a.Instrument}. {a.Message}");
            Sound.Play();
        }
    }

    internal async Task RestoreOrDefaultAsync()
    {
        try
        {
            if (await Workspace.RestoreAsync(this))
            {
                RestorePanelWindows();
                return;
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        var tab = await NewTabAsync(SimulatedFeed.FeedId, "SIM-BTC", ChartPeriod.M5, new ChartSettings { Layout = FootprintLayout.Presets[1].Clone() });
        tab.Model?.AddIndicator(new TradingHub.Indicators.Native.BigTrades());
        tab.Model?.AddIndicator(new TradingHub.Indicators.Native.DeltaBars());
        RestorePanelWindows();
    }

    // ===================================================================== loop (the window calls it at 60 Hz)

    private readonly Dictionary<ChartDrawing, decimal> _alertLast = new();
    private int _frame;

    /// <summary>Refreshes the charts. With the section hidden, the main window's ones go slower.</summary>
    internal void Tick(bool mainVisible = true)
    {
        _frame++;
        bool slowFrame = _frame % 15 == 0;
        var visible = new HashSet<ChartTab>(ReferenceEqualityComparer.Instance);
        if (mainVisible && _page != null)
            foreach (var c in _page.Charts.Take(PaneCount(_page.Layout))) visible.Add(c);

        foreach (var page in _pages)
        {
            if (!mainVisible && !slowFrame) break;
            foreach (var t in page.Charts)
            {
                if (t.Model == null) continue;
                t.Model.Update();
                CheckAlerts(t);
                if (t.Model.IsDirty && visible.Contains(t)) t.View.InvalidateVisual();
            }
        }

        foreach (var w in _floating)
        {
            var t = w.Chart;
            if (t.Model == null) continue;
            t.Model.Update();
            CheckAlerts(t);
            if (t.Model.IsDirty) t.View.InvalidateVisual();
            if (slowFrame) w.Toolbar.UpdateQuote();
        }

        // the candle countdown changes every second even when no trade arrives
        if (_frame % 60 == 0)
        {
            foreach (var t in visible)
                if (t.Model is { } cm && cm.Settings.ShowCountdown && cm.Period.IsTimeBased) cm.Invalidate();
            foreach (var w in _floating)
                if (w.Chart.Model is { } fm && fm.Settings.ShowCountdown && fm.Period.IsTimeBased) fm.Invalidate();
        }

        if (slowFrame)
        {
            foreach (var c in AllCharts)
            {
                if (c.Trader != null && (c.Window != null || mainVisible)) c.Trader.Refresh();
                UpdateTradeMarks(c);
                UpdateStrategySignals(c);
            }

            if ((mainVisible || _panelWindows.ContainsKey("ticket")) && _dockedTrader is { IsEffectivelyVisible: true } docked) docked.Refresh();
            if (mainVisible && _frame % 30 == 0) RefreshInsights();
        }

        if (!mainVisible && !slowFrame && !AnyPanelFloating) return;
        if (++_replayTick % 10 == 0 && _replayBar.IsVisible | ActiveReplay != null) UpdateReplayBar();

        var data = _dom.Dom?.Data;
        if (data != null && (data.Trades.Count != _lastTradeCount || !ReferenceEquals(data.Depth, _lastDepth)))
        {
            _lastTradeCount = data.Trades.Count;
            _lastDepth = data.Depth;
            _dom.InvalidateVisual();
            _tape.InvalidateVisual();
            UpdateQuote(data);
        }
    }

    private int _replayTick;

    private void CheckAlerts(ChartTab t)
    {
        var m = t.Model!;
        decimal last = m.Data.LastPrice;
        if (last == 0) return;
        foreach (var d in m.Drawings.Where(d => d.IsAlert && d.Points.Count > 0).ToList())
        {
            decimal level = (decimal)d.Points[0].Price;
            if (_alertLast.TryGetValue(d, out var prev) && prev != 0 && Math.Sign(prev - level) != Math.Sign(last - level))
            {
                d.Alert = false;
                if (d.Text == "alerta") d.Text = "";
                d.Dashed = false;
                d.LineStyle = LineStyleKind.Solid;
                DrawingsTouched(t);
                _status($"🔔 {t.Symbol} touched {m.Instrument.FormatPrice(level)}");
                Sound.Play();
                m.Invalidate();
            }

            _alertLast[d] = last;
        }
    }
}

/// <summary>System alert sound.</summary>
internal static class Sound
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    public static void Play(string file = "/System/Library/Sounds/Glass.aiff")
    {
        if (App.Services?.Settings.SoundAlerts == false) return;
        try
        {
            if (OperatingSystem.IsWindows()) { MessageBeep(0x40); return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("afplay", file) { UseShellExecute = false });
        }
        catch
        {
            // no sound
        }
    }
}
