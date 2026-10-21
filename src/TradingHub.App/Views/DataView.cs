using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Views;

/// <summary>
/// Historical data: what is stored on the Mac (recorded live or downloaded), downloads of whole days
/// from the Binance/Bybit public archives, deleting and replaying days.
/// </summary>
internal sealed class DataView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;
    private readonly MainWindow _main;
    private readonly ListBox _symbols = new();
    private readonly ListBox _days = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBlock _totals = new() { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _daysTitle = new() { Classes = { "h2" } };
    private readonly TextBlock _daysInfo = new() { Classes = { "muted", "small" } };
    private readonly Control _daysPanel;
    private List<SymbolData> _index = new();
    private SymbolData? _selected;

    // download
    private readonly InstrumentPicker _picker;
    private string? _dlProfile;
    private Instrument? _dlInstrument;
    private readonly CalendarDatePicker _from = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CalendarDatePicker _to = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly TextBlock _dlStatus = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly Button _download;
    private readonly Button _cancel;
    private CancellationTokenSource? _cts;

    public DataView(AppServices svc, Action<string> status, MainWindow main)
    {
        _svc = svc;
        _status = status;
        _main = main;
        _picker = new InstrumentPicker(svc) { HorizontalAlignment = HorizontalAlignment.Stretch };
        _picker.Picked += (profileId, inst, _) =>
        {
            _dlProfile = profileId;
            _dlInstrument = inst;
            _picker.SetCurrent(profileId, inst);
            UpdateDownloadHint();
        };
        _download = Ui.Button("Download", MaterialIconKind.CloudDownloadOutline, () => _ = DownloadAsync(), "primary");
        _cancel = Ui.Button("Cancel", MaterialIconKind.Close, () => _cts?.Cancel());
        _cancel.IsVisible = false;
        var today = DateTime.UtcNow.Date;
        _from.SelectedDate = today.AddDays(-7);
        _to.SelectedDate = today.AddDays(-1);
        _daysPanel = BuildDaysPanel();
        Content = Build();
        Refresh();
    }

    public void OnShown()
    {
        Refresh();

        // it proposes the active chart's instrument if it can be downloaded
        if (_dlInstrument == null && _main.Trading.ActiveTab is { Model: { } m } tab && (PublicArchives.Supports(m.Instrument.FeedId) || DownloaderOf(tab.FeedId) != null))
        {
            _dlProfile = tab.FeedId;
            _dlInstrument = m.Instrument;
            _picker.SetCurrent(tab.FeedId, m.Instrument);
            UpdateDownloadHint();
        }
    }

    public void OnTimer()
    {
    }

    private Control Build()
    {
        var openFolder = Ui.Button("Open folder", MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.Ticks));
        var refresh = Ui.IconButton(MaterialIconKind.Refresh, "Refresh", Refresh);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        var actions = Ui.H(8, _totals, refresh, openFolder);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        header.Children.Add(Ui.V(4, Ui.H1("Data"), Ui.Muted("History stored on your Mac. It is used when opening charts, in the replay and in the backtests.")));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("340,16,*,16,360") };

        // ---- stored instruments
        var symCard = new DockPanel();
        var symHeader = Ui.H3("Stored");
        symHeader.Margin = new Thickness(4, 0, 0, 10);
        DockPanel.SetDock(symHeader, Dock.Top);
        symCard.Children.Add(symHeader);
        _symbols.SelectionChanged += (_, _) =>
        {
            _selected = (_symbols.SelectedItem as ListBoxItem)?.Tag as SymbolData;
            FillDays();
        };
        _symbols.Background = Brushes.Transparent;
        symCard.Children.Add(_symbols);
        grid.Children.Add(Ui.Card(symCard, 12));

        var daysCard = Ui.Card(_daysPanel, 16);
        Grid.SetColumn(daysCard, 2);
        grid.Children.Add(daysCard);

        // on the right: download (exchanges and Rithmic) and import files (CFDs, forex…)
        var right = new ScrollViewer { Content = Ui.V(16, Ui.Card(BuildDownloadPanel(), 16), Ui.Card(BuildDatabentoPanel(), 16), Ui.Card(BuildImportPanel(), 16)) };
        Grid.SetColumn(right, 4);
        grid.Children.Add(right);

        var root = new DockPanel { Margin = new Thickness(28, 24) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(grid);
        return root;
    }

    private Control BuildDaysPanel()
    {
        var replay = Ui.Button("Replay", MaterialIconKind.PlayCircleOutline, () => _ = ReplaySelectedAsync());
        ToolTip.SetTip(replay, "Open the selected day as a replay in Trading (you trade with the simulated account)");
        var delete = Ui.Button("Delete", MaterialIconKind.DeleteOutline, () => _ = DeleteSelectedAsync(), "ghost");
        var actions = Ui.H(8, delete, replay);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(Ui.V(3, _daysTitle, _daysInfo));
        var panel = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        _days.Background = Brushes.Transparent;
        _days.DoubleTapped += (_, _) => _ = ReplaySelectedAsync();
        panel.Children.Add(_days);
        return panel;
    }

    private Control BuildDownloadPanel()
    {
        (DateTime? From, DateTime? To) range = (_from.SelectedDate, _to.SelectedDate);
        // how much to download: shortcuts or any range with From / To
        var presets = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        var chips = new List<Avalonia.Controls.Primitives.ToggleButton>();
        foreach (var (days, label) in new[] { (1, "Yesterday"), (7, "7 days"), (30, "30 days"), (90, "90 days"), (182, "6 months"), (365, "1 year"), (730, "2 years") })
        {
            var b = new Avalonia.Controls.Primitives.ToggleButton { Content = label, Padding = new Thickness(10, 3), MinHeight = 26, FontSize = 12, IsChecked = days == 7 };
            b.Click += (_, _) =>
            {
                var today = DateTime.UtcNow.Date;
                _from.SelectedDate = today.AddDays(-days);
                _to.SelectedDate = today.AddDays(-1);
                range = (_from.SelectedDate, _to.SelectedDate);
                foreach (var c in chips) c.IsChecked = ReferenceEquals(c, b);
                UpdateDownloadHint();
            };
            chips.Add(b);
            presets.Children.Add(b);
        }

        // if a date is changed by hand, no shortcut stays selected (the picker also fires with no changes: it is ignored)
        void Custom()
        {
            if ((_from.SelectedDate, _to.SelectedDate) == range) return;
            range = (_from.SelectedDate, _to.SelectedDate);
            foreach (var c in chips) c.IsChecked = false;
            if (_cts == null || _cts.IsCancellationRequested) UpdateDownloadHint();
        }

        _from.SelectedDateChanged += (_, _) => Custom();
        _to.SelectedDateChanged += (_, _) => Custom();

        var buttons = Ui.Row(10, _cancel, _download);
        _cancel.IsVisible = true;
        _cancel.IsEnabled = false;
        return Ui.V(14,
            Ui.H2("Download history"),
            new TextBlock { Text = "Complete days: from the public files (Binance, Bybit) or over your connections' APIs (Rithmic, Tradovate, cTrader, OANDA, Alpaca). In futures, with whichever contract was trading each day.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
            Ui.Field("Instrument", _picker),
            Ui.Field("How much (UTC days)", presets),
            Ui.Row(10, Ui.Field("From", _from), Ui.Field("To", _to)),
            buttons,
            _progress,
            _dlStatus,
            Ui.Divider(),
            Note(MaterialIconKind.RecordCircleOutline, "Everything you watch live is recorded automatically, Rithmic included. Days already downloaded are not downloaded again: if you cancel, next time it picks up where it left off."));
    }

    // ===================================================================== import

    private readonly TextBox _importSymbol = new() { Watermark = "It is inferred from the file name (US100, US500…)" };
    private readonly ComboBox _importZone = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressBar _importProgress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly TextBlock _importStatus = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private Button? _importButton;

    private static readonly (string Label, double Hours)[] Zones =
    {
        ("UTC (Dukascopy, TradingView)", 0),
        ("New York fixed, -5 (HistData)", -5),
        ("UTC+2 (MetaTrader in winter)", 2),
        ("UTC+3 (MetaTrader in summer)", 3),
    };

    private Control BuildImportPanel()
    {
        foreach (var (label, _) in Zones) _importZone.Items.Add(new ComboBoxItem { Content = label });
        _importZone.SelectedIndex = 0;
        _importButton = Ui.Button("Import files…", MaterialIconKind.FileImportOutline, () => _ = ImportAsync(), "primary");
        var duka = Ui.Button("Dukascopy", MaterialIconKind.OpenInNew, () => Shell.Open("https://www.dukascopy.com/swiss/english/marketwatch/historical/"));
        ToolTip.SetTip(duka, "Free history (with a free account): US100 = USA Tech, US500, US30, DAX, gold, forex… Choose ticks or 1 minute candles and export as CSV");
        var histdata = Ui.Button("HistData", MaterialIconKind.OpenInNew, () => Shell.Open("https://www.histdata.com/download-free-forex-data/"));
        ToolTip.SetTip(histdata, "Forex and indices (NSXUSD = Nasdaq 100, SPXUSD = S&P 500) as 1 minute candles or ticks, free");
        return Ui.V(14,
            Ui.H2("Import files"),
            new TextBlock { Text = "For CFDs (US100, US500, DAX), forex, gold… Download the free CSV from Dukascopy or HistData, or export it from MetaTrader or TradingView, and Trading Hub turns it into history for charts, replay and backtests. The format is recognized automatically.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
            Ui.H(8, duka, histdata),
            Ui.Field("Instrument name", _importSymbol),
            Ui.Field("File time", _importZone),
            _importButton,
            _importProgress,
            _importStatus,
            Note(MaterialIconKind.InformationOutline, "CFDs have no real volume: each quote counts as a trade (tick volume) and the side comes from whether price went up or down. It is good for price, candles and price-based backtests; the footprint and the delta are approximate."));
    }

    // ===================================================================== Databento (CME futures, long history)

    private readonly ComboBox _dbRoot = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CalendarDatePicker _dbFrom = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CalendarDatePicker _dbTo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _dbStatus = new() { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _dbProgress = new() { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
    private readonly CheckBox _dbAlsoRithmic = new() { Content = "Also download the days Rithmic already has" };
    private readonly TextBlock _dbHave = new() { Classes = { "small" }, TextWrapping = TextWrapping.Wrap };
    private Button? _dbCost, _dbDownload, _dbCancel;
    private decimal? _dbQuote;
    private string _dbQuoteKey = "";
    private CancellationTokenSource? _dbCts;

    private ConnectionProfile? DatabentoProfile => _svc.Connections.Enabled.FirstOrDefault(p => p.Kind == ConnectionKind.Databento && !string.IsNullOrEmpty(_svc.Connections.GetSecret(p)));

    private Control BuildDatabentoPanel()
    {
        foreach (var root in new[] { "NQ", "ES", "MNQ", "MES", "YM", "RTY", "CL", "GC", "MCL", "MGC", "ZN", "6E" })
            if (TradingHub.Feeds.Rithmic.RithmicFeed.SpecFor(root) is { } spec)
                _dbRoot.Items.Add(new ComboBoxItem { Content = $"{root} · {spec.Name}", Tag = root });
        _dbRoot.SelectedIndex = 0;
        var today = DateTime.UtcNow.Date;
        _dbFrom.SelectedDate = today.AddMonths(-3);
        _dbTo.SelectedDate = today.AddDays(-1);
        _dbCost = Ui.Button("Compute cost", MaterialIconKind.Calculator, () => _ = DatabentoQuoteAsync());
        _dbDownload = Ui.Button("Download", MaterialIconKind.CloudDownloadOutline, () => _ = DatabentoDownloadAsync(), "primary");
        _dbCancel = Ui.Button("Cancel", MaterialIconKind.Close, () => _dbCts?.Cancel());
        _dbDownload.IsEnabled = false;
        _dbCancel.IsVisible = false;
        void Reset()
        {
            _dbQuote = null;
            if (_dbDownload != null) _dbDownload.IsEnabled = false;
            _dbStatus.Text = DatabentoProfile == null ? "Add the “Databento” connection (with your API key) in Connections first." : "Compute the range's cost before downloading.";
        }

        _dbRoot.SelectionChanged += (_, _) => Reset();
        _dbFrom.SelectedDateChanged += (_, _) => Reset();
        _dbTo.SelectedDateChanged += (_, _) => Reset();
        _dbAlsoRithmic.IsCheckedChanged += (_, _) => Reset();
        ToolTip.SetTip(_dbAlsoRithmic, "Unticked, Databento only fills what Rithmic is missing (cheaper). Ticked, it downloads the whole range so you have the complete Databento history.");
        var configure = Ui.Button("Connections", MaterialIconKind.LanConnect, () => _main.Navigate("connections"), "ghost");
        Reset();
        return Ui.V(14,
            Ui.H2("CME futures with Databento"),
            new TextBlock { Text = "Years of tick-by-tick history (since 2010) with each trade's real aggressor side: exactly what Rithmic does not keep. It is pay per use (new accounts come with free credits): Trading Hub computes the cost first and only downloads if you confirm.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap },
            _dbHave,
            Ui.Field("Continuous contract", _dbRoot),
            Ui.Row(10, Ui.Field("From", _dbFrom), Ui.Field("To", _dbTo)),
            _dbAlsoRithmic,
            new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { _dbCost, _dbDownload, _dbCancel, configure } },
            _dbProgress,
            _dbStatus,
            Note(MaterialIconKind.InformationOutline, "It is stored separately, as “Databento” in the list on the left. In backtests you choose Databento only, Rithmic only or both combined (Rithmic first, with Databento filling the gaps). Days you already have in full are not downloaded or charged again."));
    }

    private string DbRoot => (_dbRoot.SelectedItem as ComboBoxItem)?.Tag as string ?? "NQ";

    private static Instrument CmeInstrument(string root)
    {
        var spec = TradingHub.Feeds.Rithmic.RithmicFeed.SpecFor(root);
        return new Instrument { Symbol = root, FeedId = HistorySources.Databento, Exchange = "CME", TickSize = spec?.Tick ?? 0.25m, PointValue = spec?.PointValue ?? 1, VolumeStep = 1 };
    }

    /// <summary>
    /// Days missing in the range (excluding Saturdays): the ones Databento already downloaded are not downloaded again, and the ones Rithmic has
    /// complete ones either, unless Databento's whole history is requested.
    /// </summary>
    private List<DateOnly> DatabentoDays()
    {
        var from = DateOnly.FromDateTime(_dbFrom.SelectedDate ?? DateTime.UtcNow.Date.AddMonths(-3));
        var to = DateOnly.FromDateTime(_dbTo.SelectedDate ?? DateTime.UtcNow.Date.AddDays(-1));
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        if (to > yesterday) to = yesterday;
        var days = new List<DateOnly>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (d.DayOfWeek == DayOfWeek.Saturday) continue;
            var dayEnd = d.ToDateTime(TimeOnly.MinValue).AddDays(1.2);
            var own = TickFile.DayPath(AppPaths.Ticks, HistorySources.Databento, DbRoot, d);
            if (File.Exists(own) && File.GetLastWriteTimeUtc(own) > dayEnd) continue;
            var rithmic = TickFile.DayPath(AppPaths.Ticks, TradingHub.Feeds.Rithmic.RithmicFeed.FeedId, DbRoot, d);
            bool rithmicComplete = File.Exists(rithmic) && File.GetLastWriteTimeUtc(rithmic) > dayEnd && new FileInfo(rithmic).Length > 200_000;
            if (rithmicComplete && _dbAlsoRithmic.IsChecked != true) continue;
            days.Add(d);
        }

        return days;
    }

    /// <summary>Consecutive stretches of days (to request the cost in few requests).</summary>
    private static List<(DateOnly From, DateOnly To)> Runs(List<DateOnly> days)
    {
        var runs = new List<(DateOnly, DateOnly)>();
        foreach (var d in days)
        {
            if (runs.Count > 0 && runs[^1].Item2.AddDays(d.DayOfWeek == DayOfWeek.Sunday ? 2 : 1) >= d) runs[^1] = (runs[^1].Item1, d);
            else runs.Add((d, d));
        }

        return runs;
    }

    private async Task DatabentoQuoteAsync()
    {
        if (DatabentoProfile is not { } profile)
        {
            _dbStatus.Text = "Add the “Databento” connection (with your API key) in Connections first.";
            return;
        }

        var days = DatabentoDays();
        if (days.Count == 0)
        {
            _dbStatus.Text = "You already have every day of that range in full.";
            return;
        }

        _dbCost!.IsEnabled = false;
        _dbStatus.Text = "Calculating the cost…";
        try
        {
            using var client = _svc.Connections.DatabentoOf(profile);
            decimal total = 0;
            foreach (var (from, to) in Runs(days)) total += await client.CostAsync(DbRoot, from, to, CancellationToken.None);
            _dbQuote = total;
            _dbQuoteKey = $"{DbRoot}|{days.First()}|{days.Last()}|{days.Count}";
            _dbDownload!.IsEnabled = true;
            _dbStatus.Text = $"{DbRoot}: {days.Count} day(s) to download ({days.First():dd/MM/yyyy} → {days.Last():dd/MM/yyyy}) · cost ≈ US$ {total.ToString("0.00", CultureInfo.InvariantCulture)} of your Databento credits.";
        }
        catch (Exception ex)
        {
            _dbStatus.Text = "Could not compute: " + ex.Message;
        }
        finally
        {
            _dbCost.IsEnabled = true;
        }
    }

    private async Task DatabentoDownloadAsync()
    {
        if (DatabentoProfile is not { } profile || _dbQuote is not { } quote) return;
        var days = DatabentoDays();
        if (days.Count == 0 || $"{DbRoot}|{days.First()}|{days.Last()}|{days.Count}" != _dbQuoteKey)
        {
            _dbStatus.Text = "The range changed: compute the cost again.";
            _dbDownload!.IsEnabled = false;
            return;
        }

        string cost = quote.ToString("0.00", CultureInfo.InvariantCulture);
        if (!await ConfirmDialog.ShowAsync(_main, "Download Databento history",
                $"{days.Count} day(s) of {DbRoot} will be downloaded tick by tick. Databento charges ≈ US$ {cost} of your credits (if they are not enough, your payment method at Databento).",
                $"Download (≈ US$ {cost})")) return;

        var inst = CmeInstrument(DbRoot);
        _dbCts = new CancellationTokenSource();
        var ct = _dbCts.Token;
        using var job = BackgroundWork.Begin($"downloading {DbRoot} history from Databento");
        _dbDownload!.IsEnabled = false;
        _dbCost!.IsEnabled = false;
        _dbCancel!.IsVisible = true;
        _dbProgress.IsVisible = true;
        int done = 0, empty = 0;
        long trades = 0;
        try
        {
            using var client = _svc.Connections.DatabentoOf(profile);
            for (int i = 0; i < days.Count; i++)
            {
                var day = days[i];
                int index = i;
                _dbStatus.Text = $"{DbRoot} {day:dd/MM/yyyy}: downloading… ({i + 1}/{days.Count})";
                var progress = new Progress<double>(p => _dbProgress.Value = (index + p) / days.Count);
                var list = await client.DownloadDayAsync(inst, day, progress, ct);
                if (list == null)
                {
                    empty++;
                    continue;
                }

                _dbStatus.Text = $"{DbRoot} {day:dd/MM/yyyy}: storing {list.Count:N0} trades…";
                await Task.Run(() => HistoricalData.WriteDay(AppPaths.Ticks, inst, day, list, replace: true), ct);
                trades += list.Count;
                done++;
            }

            _dbStatus.Text = $"Done: {done} day(s) of {DbRoot} ({trades:N0} trades with their aggressor side)" + (empty > 0 ? $", {empty} with no trades (holidays)" : "") + ".";
            _status(_dbStatus.Text);
        }
        catch (OperationCanceledException)
        {
            _dbStatus.Text = $"Cancelled. {done} day(s) were stored.";
        }
        catch (Exception ex)
        {
            _dbStatus.Text = "Error: " + ex.Message;
            CrashLog.Write(ex);
        }
        finally
        {
            _dbQuote = null;
            _dbCost.IsEnabled = true;
            _dbCancel.IsVisible = false;
            _dbProgress.IsVisible = false;
            _dbCts = null;
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    /// <summary>Instrument name based on the file (USATECHIDXUSD_Ticks… -> US100).</summary>
    private static string GuessSymbol(string file)
    {
        string n = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
        if (n.Contains("USATECH") || n.Contains("NAS100") || n.Contains("NSX") || n.Contains("US100") || n.Contains("USTEC") || n.Contains("NDX")) return "US100";
        if (n.Contains("USA500") || n.Contains("SPX") || n.Contains("US500")) return "US500";
        if (n.Contains("USA30") || n.Contains("US30") || n.Contains("DJI") || n.Contains("WS30")) return "US30";
        if (n.Contains("DEU") || n.Contains("GER") || n.Contains("DAX")) return "GER40";
        if (n.Contains("XAU") || n.Contains("GOLD")) return "XAUUSD";
        foreach (var fx in new[] { "EURUSD", "GBPUSD", "USDJPY", "AUDUSD", "USDCAD", "USDCHF", "NZDUSD", "XAGUSD", "BTCUSD" })
            if (n.Replace("/", "").Replace("_", "").Contains(fx)) return fx;
        var first = n.Split('_', '.', ' ', '-')[0];
        return first.Length > 0 ? first : "IMPORTED";
    }

    private async Task ImportAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "Choose the history files (CSV or TXT)",
            AllowMultiple = true,
            FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("History (CSV, TXT)") { Patterns = new[] { "*.csv", "*.txt" } } },
        });
        if (files.Count == 0) return;
        var offset = TimeSpan.FromHours(Zones[Math.Max(0, _importZone.SelectedIndex)].Hours);
        string typed = (_importSymbol.Text ?? "").Trim().ToUpperInvariant();
        _importButton!.IsEnabled = false;
        _importProgress.IsVisible = true;
        var lines = new List<string>();
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var path = files[i].TryGetLocalPath();
                if (path == null) continue;
                string symbol = typed.Length > 0 ? typed : GuessSymbol(path);
                _importStatus.Text = $"{Path.GetFileName(path)} → {symbol}: reading… ({i + 1}/{files.Count})";
                int index = i;
                var progress = new Progress<double>(p => _importProgress.Value = (index + p) / files.Count);
                try
                {
                    var r = await Task.Run(() => CsvImport.Import(path, symbol, AppPaths.Ticks, offset, null, progress, CancellationToken.None));
                    lines.Add($"{symbol}: {r.Kind}, {r.Trades:N0} trades across {r.Days} day(s)" +
                              (r.From is { } f && r.To is { } t ? $" ({f:dd/MM/yyyy} → {t:dd/MM/yyyy})" : "") +
                              (r.BadLines > 0 ? $", {r.BadLines:N0} rows that could not be read" : "") + $" · price step {r.TickSize.ToString("0.########", CultureInfo.InvariantCulture)}");
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    lines.Add($"{Path.GetFileName(path)}: {ex.Message}");
                }
            }

            _importStatus.Text = "Done. " + string.Join(" · ", lines) + ". It appears in the list on the left (the “imported” connection) and in the backtests.";
            _status(_importStatus.Text);
        }
        finally
        {
            _importButton.IsEnabled = true;
            _importProgress.IsVisible = false;
            Refresh();
        }
    }

    private static Control Note(MaterialIconKind icon, string text)
    {
        var d = new DockPanel();
        var i = Ui.Icon(icon, 16, Ui.Text2);
        i.Margin = new Thickness(0, 1, 8, 0);
        i.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(i, Dock.Left);
        d.Children.Add(i);
        d.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "faint", "small" } });
        return d;
    }

    private bool DownloadsFromRithmic => _dlProfile != null && _svc.Connections.Find(_dlProfile)?.IsRithmic == true;

    /// <summary>Whether the connection downloads whole days through its API (Rithmic, Tradovate, cTrader, OANDA, Alpaca); null if not.</summary>
    private TradingHub.Feeds.IDayDownloader? DownloaderOf(string? profileId)
    {
        if (profileId == null || _svc.Connections.Find(profileId) is not { Enabled: true, HasMarketData: true } p) return null;
        if (PublicArchives.Supports(p.MarketDataId)) return null; // the public archives are faster
        try
        {
            return _svc.Connections.GetFeed(p.Id) as TradingHub.Feeds.IDayDownloader;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private TradingHub.Feeds.IDayDownloader? Downloader => DownloaderOf(_dlProfile);

    private bool CanDownload => _dlInstrument != null && (PublicArchives.Supports(_dlInstrument.FeedId) || Downloader != null);

    /// <summary>Days to download from the chosen range (on Rithmic Saturdays are skipped: there is no market).</summary>
    private List<DateOnly> RangeDays()
    {
        var from = DateOnly.FromDateTime(_from.SelectedDate ?? DateTime.UtcNow.Date.AddDays(-7));
        var to = DateOnly.FromDateTime(_to.SelectedDate ?? DateTime.UtcNow.Date.AddDays(-1));
        var todayUtc = DateOnly.FromDateTime(DateTime.UtcNow);
        if (to >= todayUtc) to = todayUtc.AddDays(-1);
        var days = new List<DateOnly>();
        bool skipSaturdays = Downloader?.ClosedOnSaturdays == true;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (!(skipSaturdays && d.DayOfWeek == DayOfWeek.Saturday)) days.Add(d);
        return days;
    }

    private void UpdateDownloadHint()
    {
        if (_dlInstrument == null)
        {
            _dlStatus.Text = "Choose an instrument from Binance, Bybit, Rithmic, Tradovate, cTrader, OANDA or Alpaca.";
            return;
        }

        _download.IsEnabled = CanDownload;
        if (!CanDownload)
        {
            _dlStatus.Text = "This connection provides no history to download: its data is recorded while you watch it live.";
            return;
        }

        var days = RangeDays();
        if (DownloadsFromRithmic)
        {
            // which contracts fall in the range (ESU6, ESZ6…)
            var contracts = days.Select(d => TradingHub.Feeds.Rithmic.RithmicFeed.ContractNameOn(_dlInstrument, d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))).Distinct().ToList();
            var since = TradingHub.Feeds.Rithmic.RithmicFeed.CurrentContractSince(_dlInstrument);
            bool older = since is { } sd && days.Count > 0 && days[0].ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) < sd;
            _dlStatus.Text = $"{_dlInstrument.Symbol} · {days.Count} days tick by tick from Rithmic · contract{(contracts.Count > 1 ? "s" : "")} {string.Join(", ", contracts)}" +
                             (older ? $". Careful: Rithmic does not keep history for expired contracts; what is safe is from {since:dd/MM} (contract {contracts[^1]}). Anything earlier is attempted anyway." : "") +
                             (days.Count > 60 ? " An index like ES has more than a million trades a day: a long range takes a while (it can be cancelled and continued later)." : "");
        }
        else if (Downloader is { } dl)
        {
            _dlStatus.Text = $"{_dlInstrument.Symbol} · {days.Count} days · {dl.HistoryNote}" +
                             (days.Count > 30 ? ". A long range takes a while: it can be cancelled and continued later (whatever was downloaded is kept)." : "");
        }
        else
        {
            _dlStatus.Text = $"{_dlInstrument.Symbol} · {days.Count} days · daily files from {_dlInstrument.Exchange}";
        }
    }

    // ===================================================================== index

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    private string FeedName(string feedId) => _svc.Connections.Find(feedId)?.Name ?? (feedId == HistorySources.Databento ? "Databento" : feedId);

    /// <summary>What has already been downloaded from Databento (so it shows on its card).</summary>
    private void RefreshDatabentoSummary()
    {
        var have = _index.Where(s => s.FeedId == HistorySources.Databento).OrderBy(s => s.Symbol).ToList();
        _dbHave.Text = have.Count == 0
            ? "You have not downloaded anything from Databento yet."
            : "Already from Databento: " + string.Join(" · ", have.Select(s => $"{s.Symbol} {s.Days.Count} day{(s.Days.Count == 1 ?"" : "s")} ({s.First:dd/MM/yyyy} → {s.Last:dd/MM/yyyy})"));
    }

    private void Refresh()
    {
        _index = HistoricalData.Scan(AppPaths.Ticks);
        _totals.Text = $"{_index.Count} instruments · {Size(_index.Sum(s => s.Bytes))}";
        RefreshDatabentoSummary();
        var keep = _selected;
        _symbols.Items.Clear();
        foreach (var s in _index)
        {
            var text = Ui.V(2,
                new TextBlock { Text = s.Symbol, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"{FeedName(s.FeedId)} · {s.Days.Count} day{(s.Days.Count == 1 ?"" : "s")}", Classes = { "muted", "small" } });
            var size = new TextBlock { Text = Size(s.Bytes), Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center };
            var row = new DockPanel { Margin = new Thickness(2, 4) };
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);
            row.Children.Add(text);
            _symbols.Items.Add(new ListBoxItem { Content = row, Tag = s });
        }

        if (_index.Count == 0)
            _symbols.Items.Add(new ListBoxItem { Content = new TextBlock { Text = "There is no stored data yet. Open a chart or download some days.", TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } }, IsEnabled = false });

        _symbols.SelectedItem = _symbols.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is SymbolData sd && keep != null && sd.FeedId == keep.FeedId && sd.Symbol == keep.Symbol)
                                ?? _symbols.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is SymbolData);
        FillDays();
    }

    private void FillDays()
    {
        _days.Items.Clear();
        _daysPanel.IsVisible = true;
        if (_selected == null)
        {
            _daysTitle.Text = "Days";
            _daysInfo.Text = "Choose an instrument";
            return;
        }

        _daysTitle.Text = $"{_selected.Symbol} · {FeedName(_selected.FeedId)}";
        _daysInfo.Text = $"{_selected.First:dd/MM/yyyy} → {_selected.Last:dd/MM/yyyy} · {Size(_selected.Bytes)}";
        long max = _selected.Days.Max(d => d.Bytes);
        foreach (var d in _selected.Days.OrderByDescending(d => d.Day))
        {
            var date = new TextBlock { Text = d.Day.ToDateTime(TimeOnly.MinValue).ToString("ddd dd/MM/yyyy", CultureInfo.GetCultureInfo("en-US")), Width = 130, VerticalAlignment = VerticalAlignment.Center };
            var bar = new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.Accent, Opacity = 0.75, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Width = Math.Max(4, 220.0 * d.Bytes / Math.Max(1, max)) };
            ToolTip.SetTip(bar, "Relative size (≈ number of trades)");
            var size = new TextBlock { Text = Size(d.Bytes), Classes = { "muted", "small" }, Width = 70, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            var row = new DockPanel { Margin = new Thickness(2, 3) };
            DockPanel.SetDock(date, Dock.Left);
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(date);
            row.Children.Add(size);
            row.Children.Add(bar);
            _days.Items.Add(new ListBoxItem { Content = row, Tag = d });
        }
    }

    private List<DayData> SelectedDays() => _days.SelectedItems?.OfType<ListBoxItem>().Select(i => (DayData)i.Tag!).ToList() ?? new();

    private async Task DeleteSelectedAsync()
    {
        var days = SelectedDays();
        if (_selected == null || days.Count == 0)
        {
            _status("Choose one or more days to delete (⌘ or ⇧ to select several).");
            return;
        }

        if (!await ConfirmDialog.ShowAsync(_main, "Delete data", $"{days.Count} day(s) of {_selected.Symbol} ({Size(days.Sum(d => d.Bytes))}) will be deleted. It cannot be undone.", "Delete", danger: true)) return;
        HistoricalData.DeleteDays(AppPaths.Ticks, _selected.FeedId, _selected.Symbol, days.Select(d => d.Day));
        _status($"Deleted {days.Count} day(s) of {_selected.Symbol}");
        Refresh();
    }

    private async Task ReplaySelectedAsync()
    {
        var days = SelectedDays();
        if (_selected == null || days.Count == 0)
        {
            _status("Choose the day you want to replay.");
            return;
        }

        var day = days.Min(d => d.Day);
        var lastDay = days.Max(d => d.Day);
        Instrument? inst = null;
        if (_selected.FeedId == HistorySources.Databento) inst = MarketInfo.FuturesInstrument(_selected.FeedId, _selected.Symbol);
        else
        {
            try
            {
                var instruments = await InstrumentPicker.InstrumentsAsync(_svc, _selected.FeedId);
                inst = instruments.FirstOrDefault(i => i.Symbol == _selected.Symbol);
            }
            catch (Exception)
            {
                // without the connection: the futures are still built with the CME catalog
            }

            if (inst == null && MarketInfo.IsFutures(_selected.FeedId)) inst = MarketInfo.FuturesInstrument(_selected.FeedId, _selected.Symbol);
        }

        if (inst == null)
        {
            _status($"Could not fetch the details for {_selected.Symbol} (is the connection active?).");
            return;
        }

        // it starts with the day's first data point
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var first = HistoricalData.Read(AppPaths.Ticks, _selected.FeedId, _selected.Symbol, start, start.AddDays(1)).FirstOrDefault();
        if (first.Time > start) start = first.Time;
        var end = lastDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1);
        _main.Navigate("trading");
        await _main.Trading.OpenReplayAsync(inst, start, end, _main.Trading.ActiveTab?.Model?.Period ?? ChartPeriod.M5);
    }

    // ===================================================================== downloads

    private async Task DownloadAsync()
    {
        if (_dlInstrument == null || _dlProfile == null)
        {
            _picker.Open();
            return;
        }

        if (!CanDownload)
        {
            UpdateDownloadHint();
            return;
        }

        var days = RangeDays();
        if (days.Count == 0)
        {
            _dlStatus.Text = "The range has no complete days (today's is not over yet).";
            return;
        }

        var rithmic = DownloadsFromRithmic ? _svc.Connections.GetFeed(_dlProfile!) as TradingHub.Feeds.Rithmic.RithmicFeed : null;
        rithmic?.ClearMissingContracts();
        var downloader = Downloader;
        var started = DateTime.UtcNow;
        int fetched = 0;
        string? lastProblem = null;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _download.IsEnabled = false;
        _cancel.IsEnabled = true;
        _progress.IsVisible = true;
        _progress.Value = 0;
        int done = 0, skipped = 0, missing = 0;
        long trades = 0;
        var inst = _dlInstrument;
        using var job = BackgroundWork.Begin($"downloading {inst.Symbol} history");
        try
        {
            for (int i = 0; i < days.Count; i++)
            {
                var day = days[i];
                int index = i;
                // a day that is already complete (downloaded earlier) is not downloaded again
                var path = TickFile.DayPath(AppPaths.Ticks, inst.FeedId, inst.Symbol, day);
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > day.ToDateTime(TimeOnly.MinValue).AddDays(1.2) && new FileInfo(path).Length > 200_000)
                {
                    skipped++;
                    continue;
                }

                // time remaining, based on how long the days already downloaded took
                string eta = "";
                if (fetched > 0)
                {
                    var left = TimeSpan.FromTicks((DateTime.UtcNow - started).Ticks / fetched * (days.Count - i));
                    eta = left.TotalMinutes < 1 ? " · less than a minute left" : left.TotalHours < 1 ? $" · ~{left.TotalMinutes:0} min left" : $" · ~{left.TotalHours:0.#} h left";
                }

                string contract = rithmic != null ? $" ({TradingHub.Feeds.Rithmic.RithmicFeed.ContractNameOn(inst, day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))})" : "";
                _dlStatus.Text = $"{inst.Symbol} {day:dd/MM/yyyy}{contract}: downloading… ({i + 1}/{days.Count}){eta}";
                var progress = new Progress<double>(p => _progress.Value = (index + p) / days.Count);
                var list = downloader != null
                    ? await downloader.DownloadDayAsync(inst, day, progress, ct)
                    : await PublicArchives.DownloadDayAsync(inst, day, progress, ct);
                fetched++;
                if (list == null)
                {
                    missing++;
                    if (rithmic?.LastHistoryError is { } err) lastProblem = $"{day:dd/MM}{contract}: Rithmic replied {err}";
                    continue;
                }

                _dlStatus.Text = $"{inst.Symbol} {day:dd/MM/yyyy}{contract}: storing {list.Count:N0} trades…";
                bool replace = downloader != null; // the day downloaded through the API comes complete: it replaces what was recorded live
                await Task.Run(() => HistoricalData.WriteDay(AppPaths.Ticks, inst, day, list, replace), ct);
                trades += list.Count;
                done++;
                _progress.Value = (i + 1.0) / days.Count;
            }

            _dlStatus.Text = $"Done: {done} day(s) downloaded ({trades:N0} trades)" +
                             (skipped > 0 ? $", {skipped} were already there" : "") +
                             (missing > 0 ? rithmic != null ? $", {missing} with no trades at Rithmic (holidays or outside what it keeps)" : downloader != null ? $", {missing} with no data (holidays or weekend)" : $", {missing} not published yet" : "") + "." +
                             (lastProblem != null ? " " + lastProblem : "");
            if (rithmic?.MissingContracts is { Count: > 0 } gone)
                _dlStatus.Text += $" Rithmic returned nothing for {string.Join(", ", gone)}: it does not keep history for expired contracts, so with Rithmic you can download from the last expiry onwards. Whatever you download or record stays on your Mac, so the history builds up over time.";
            _status(_dlStatus.Text);
        }
        catch (OperationCanceledException)
        {
            _dlStatus.Text = $"Cancelled. {done} day(s) were stored.";
        }
        catch (Exception ex)
        {
            _dlStatus.Text = "Error: " + ex.Message;
            CrashLog.Write(ex);
        }
        finally
        {
            _download.IsEnabled = true;
            _cancel.IsEnabled = false;
            _progress.IsVisible = false;
            _cts = null;
            Dispatcher.UIThread.Post(Refresh);
        }
    }
}
