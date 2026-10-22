// Opens the Trading Hub main window with no screen (Avalonia headless + Skia) and saves PNG captures.
// Usage: dotnet run --project tools/TradingHub.UiSnapshot -- folder [seconds] [page...] [--open connection:SYMBOL] [--size 1600x950]
// Pages: trading, strategies, data, connections, settings (default: trading).
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Windows;

// the same culture the app pins in Program.Main, so a capture shows the numbers the user would see
var uiCulture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = uiCulture;
System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = uiCulture;

string outDir = args.Length > 0 ? args[0] : "ui-snapshots";
double seconds = args.Length > 1 && double.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 6;
var pages = new List<string>();
string? open = null;
double width = 1600, height = 950;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--open" && i + 1 < args.Length) open = args[++i];
    else if (args[i] == "--size" && i + 1 < args.Length)
    {
        var parts = args[++i].Split('x');
        width = double.Parse(parts[0]);
        height = double.Parse(parts[1]);
    }
    else pages.Add(args[i]);
}

if (pages.Count == 0) pages.Add("trading");
Directory.CreateDirectory(outDir);

// one scene at a time per temp folder: they all use the same copies (tradinghub-snap-*, tradinghub-fake-claude…) and another
// run alongside would delete them halfway through. It is released on its own when the process ends (even if it hangs or fails).
FileStream? snapLock = null;
for (var until = DateTime.UtcNow.AddMinutes(20); snapLock == null;)
{
    try
    {
        snapLock = new FileStream(Path.Combine(Path.GetTempPath(), "tradinghub-snap.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException)
    {
        if (DateTime.UtcNow > until)
        {
            Console.WriteLine("FAIL  another scene has been running with the same temporary folder for 20 minutes");
            return 1;
        }

        Console.WriteLine("Waiting for another scene to finish…");
        Thread.Sleep(5000);
    }
}

// never touch the Mac's power settings or ask for administrator permissions from a tool
Environment.SetEnvironmentVariable("TRADINGHUB_SLEEP_GUARD", "off");

// never the real keychain from a tool: neither the Rithmic password nor the exchange keys
// (besides, logging in with the real user from here can disconnect the session of an open Trading Hub)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE")))
    Environment.SetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE", Path.Combine(Path.GetTempPath(), "tradinghub-snap-keychain.json"));
TradingHub.Tests.FakeRithmicServer? fakeRithmic = null;

// the strategies go in a copy too: a tool never writes in the user's real folder
// (there are scenes that create strategies and move them between folders)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRADINGHUB_STRATEGIES_DIR")))
{
    var realStrategies = Path.Combine(TradingHub.Common.Product.Documents, "Estrategias");
    var snapStrategies = Path.Combine(Path.GetTempPath(), "tradinghub-snap-strategies");
    if (Directory.Exists(snapStrategies)) Directory.Delete(snapStrategies, true);
    Directory.CreateDirectory(snapStrategies);
    if (Directory.Exists(realStrategies))
        foreach (var f in Directory.EnumerateFiles(realStrategies, "*.cs"))
            File.Copy(f, Path.Combine(snapStrategies, Path.GetFileName(f)));
    Environment.SetEnvironmentVariable("TRADINGHUB_STRATEGIES_DIR", snapStrategies);
}

// and the custom indicators: there are scenes that write test indicators and an open Trading Hub watches the real folder
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRADINGHUB_INDICATORS_DIR")))
{
    var realIndicators = Path.Combine(TradingHub.Common.Product.Documents, "Indicadores");
    var snapIndicators = Path.Combine(Path.GetTempPath(), "tradinghub-snap-indicators");
    if (Directory.Exists(snapIndicators)) Directory.Delete(snapIndicators, true);
    Directory.CreateDirectory(snapIndicators);
    if (Directory.Exists(realIndicators))
        foreach (var f in Directory.EnumerateFiles(realIndicators, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(realIndicators, f);
            if (rel.Contains("/bin/") || rel.Contains("/obj/")) continue;
            var dst = Path.Combine(snapIndicators, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }

    Environment.SetEnvironmentVariable("TRADINGHUB_INDICATORS_DIR", snapIndicators);
}

// never on the real data: an open Trading Hub records into that folder and two processes writing the same
// files corrupt them. Only the configuration and the workspace are copied to a temp folder.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR")))
{
    var realRoot = TradingHub.Common.Product.AppData;
    var snapRoot = Path.Combine(Path.GetTempPath(), "tradinghub-snap-data");
    Directory.CreateDirectory(snapRoot);
    foreach (var f in new[] { "settings.json", "workspace.json", "connections.json" })
    {
        var src = Path.Combine(realRoot, f);
        if (File.Exists(src)) File.Copy(src, Path.Combine(snapRoot, f), overwrite: true);
    }

    // copies (never the originals) of what has already been downloaded, so years of data are not requested from the exchange again
    foreach (var dir in new[] { "Klines", "Ticks", "Drawings" })
    {
        var src = Path.Combine(realRoot, dir);
        if (!Directory.Exists(src)) continue;
        foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f).StartsWith('.')) continue; // recording locks
            // Rithmic goes to the fake server; Databento is not copied either (it weighs hundreds of MB and would change what the scenes test)
            if (Path.GetRelativePath(src, f) is var rel && (rel.StartsWith("rithmic", StringComparison.Ordinal) || rel.StartsWith("databento", StringComparison.Ordinal))) continue;
            var dst = Path.Combine(snapRoot, dir, Path.GetRelativePath(src, f));
            if (File.Exists(dst) && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(f)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
    }

    // the history of the test accounts starts from scratch on every run
    if (Directory.Exists(Path.Combine(snapRoot, "Accounts"))) Directory.Delete(Path.Combine(snapRoot, "Accounts"), true);

    // Rithmic against a fake server (demo user, account ACC1) and with no exchange keys
    fakeRithmic = FakeMarket();
    SanitizeConnections(Path.Combine(snapRoot, "connections.json"), fakeRithmic.Uri, Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE")!);
    Environment.SetEnvironmentVariable("TRADINGHUB_DATA_DIR", snapRoot);
}

static TradingHub.Tests.FakeRithmicServer FakeMarket()
{
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var server = new TradingHub.Tests.FakeRithmicServer { PageSize = 5000, LastTradeSsboe = now - 5, MarketPrice = 5000 };
    var rnd = new Random(7);
    double price = 5000;
    // 1-minute candles of the last 3 days and trades of the last 2 hours
    for (long t = now - 3 * 86400; t < now - 7200; t += 60)
    {
        double o = price;
        price = Math.Round((price + (rnd.NextDouble() - 0.5) * 6) * 4) / 4;
        server.Bars.Add((t + 60, o, Math.Max(o, price) + 0.5, Math.Min(o, price) - 0.5, price, 400 + rnd.Next(800), 200 + rnd.Next(400)));
    }

    for (long t = now - 7200; t < now - 5; t += 2)
    {
        price = Math.Round((price + (rnd.NextDouble() - 0.5) * 1.5) * 4) / 4;
        server.Ticks.Add((t, rnd.Next(1_000_000), price, 1 + rnd.Next(6), rnd.Next(2) == 0));
    }

    server.MarketPrice = price;
    return server;
}

static void SanitizeConnections(string path, string fakeUri, string keychainFile)
{
    var secrets = new Dictionary<string, string>();
    if (File.Exists(path))
    {
        var list = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        foreach (var node in list)
        {
            if (node is not System.Text.Json.Nodes.JsonObject o) continue;
            o["ApiKey"] = "";
            o["AllowRealOrders"] = false;
            if (Enum.TryParse<TradingHub.App.Services.ConnectionKind>(o["Kind"]?.ToString(), out var kind)
                && kind is TradingHub.App.Services.ConnectionKind.Rithmic or TradingHub.App.Services.ConnectionKind.AmpFutures)
            {
                o["RithmicUri"] = fakeUri;
                o["RithmicSystem"] = "Rithmic Paper Trading";
                o["RithmicUser"] = "demo";
                o["RithmicAggregatedQuotes"] = false;
                secrets[$"{o["Id"]}:secret"] = "demo";
            }
        }

        File.WriteAllText(path, list.ToJsonString());
    }

    File.WriteAllText(keychainFile, System.Text.Json.JsonSerializer.Serialize(secrets));
}

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

MainWindow? window = null;

void Pump(double secs)
{
    var end = DateTime.UtcNow.AddSeconds(secs);
    do
    {
        // in headless mode DispatcherTimers do not always fire: the loop is called by hand
        window?.Tick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(16);
    }
    while (DateTime.UtcNow < end);
}

window = new MainWindow(App.Services) { Width = width, Height = height };
window.Show();
for (int i = 0; i < 150 && window.Trading.ActiveTab?.Model == null; i++) Pump(0.02);

if (open != null && open.Split(':') is [var feedId, var symbol] && window.Trading.ActiveTab is { } tab)
{
    var task = window.Trading.OpenInTabAsync(tab, feedId, symbol, TradingHub.Data.ChartPeriod.M1, tab.Model?.Settings.Clone());
    while (!task.IsCompleted) Pump(0.02);
}

if (Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_INDICATORS") is { Length: > 0 } wanted && window.Trading.ActiveTab?.Model is { } chart)
{
    // adds indicators to the active chart (by display name)
    foreach (var slot in chart.Indicators.ToList()) chart.RemoveIndicator(slot);
    foreach (var name in wanted.Split('|'))
    {
        var info = App.Services.Catalog.Items.FirstOrDefault(i => i.Name == name);
        if (info == null) { Console.WriteLine("Not found: " + name); continue; }
        chart.AddIndicator((TradingHub.Indicators.Indicator)Activator.CreateInstance(info.Type)!);
        Console.WriteLine("Added " + name);
    }
}

Pump(seconds);
if (Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_INDICATORS") is { Length: > 0 } && window.Trading.ActiveTab?.Model is { } chart2)
    foreach (var slot in chart2.Indicators) Console.WriteLine($"  {slot.Indicator.Name}: {(slot.Error ?? "OK")}");

if (Environment.GetEnvironmentVariable("TRADINGHUB_SCRIPTS_DEBUG") == "1")
{
    var res = App.Services.Scripts?.LastResult;
    Console.WriteLine($"Own indicators: {(res == null ? "not compiled" : res.Success ? "OK" : "with errors")} ({res?.Diagnostics.Count(d => d.Severity == "error")} errors)");
    foreach (var g in (res?.Diagnostics ?? Array.Empty<TradingHub.Scripting.ScriptDiagnostic>()).Where(d => d.Severity == "error").GroupBy(d => d.Id + " " + System.Text.RegularExpressions.Regex.Replace(d.Message, "'[^']*'", m => m.Value)).Take(120))
        Console.WriteLine($"  {g.Count(),3}x {Path.GetFileName(g.First().File)}:{g.First().Line} {g.Key}");
    var names = App.Services.Catalog.Items.Where(i => i.Source == TradingHub.App.Services.UserScripts.Source).Select(i => i.Name);
    Console.WriteLine("In the catalog: " + string.Join(", ", names));
}

if (Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_DEBUG") == "1")
{
    foreach (var f in new[] { "_connText", "_infoText", "_statusText" })
    {
        var tb = (Avalonia.Controls.TextBlock?)typeof(MainWindow).GetField(f, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(window);
        Console.WriteLine($"{f} = '{tb?.Text}'");
    }

    var d = window.Trading.ActiveTab?.Model?.Data;
    Console.WriteLine($"data: {d?.Instrument.Symbol} status={d?.Status} trades={d?.Trades.Count} loading={d?.IsLoadingHistory} progress={d?.HistoryProgress}");
}

int exitCode = 0;
foreach (var page in pages)
{

    if (page == "connections")
    {
        exitCode += ConnectionsScene.Run(window, Pump, outDir);
        continue;
    }


    if (page == "settings-record")
    {
        exitCode += SettingsScene.Run(window, Pump, outDir);
        continue;
    }

    if (page == "heatmap")
    {
        exitCode += HeatmapScene.Run(window, Pump, outDir);
        continue;
    }

    if (page == "panel")
    {
        exitCode += PanelScene.Run(window, Pump, outDir);
        continue;
    }

    if (page == "footprint")
    {
        exitCode += FootprintScene.Run(window, Pump, outDir);
        continue;
    }

    if (page == "corners")
    {
        exitCode += CornersScene.Run(window, Pump, outDir);
        continue;
    }


    if (page == "market")
    {
        exitCode += BacktestMarketScene.Run(window, Pump, outDir);
        continue;
    }

    if (page == "choice")
    {
        // opens the toolbar's history button and shows what happens
        window.Navigate("trading");
        var hb = typeof(TradingHub.App.Views.ChartToolbar).GetField(Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_CHOICE") ?? "_historyBox", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window.Trading.Toolbar)!;
        var btn = (Avalonia.Controls.Button)hb;
        Console.WriteLine($"Button: visible={btn.IsVisible} enabled={btn.IsEffectivelyEnabled} size={btn.Bounds} template={(btn.GetType().GetProperty("Template")?.GetValue(btn) != null)} flyout={btn.Flyout?.GetType().Name}");
        btn.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(btn, null);
        Pump(1);
        var mf = (Avalonia.Controls.MenuFlyout)btn.Flyout!;
        Console.WriteLine($"Menu open={mf.IsOpen} items={mf.Items.Count}");
        var presenter = mf.GetType().GetProperty("Popup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(mf) as Avalonia.Controls.Primitives.Popup;
        var child = presenter?.Child as Avalonia.Controls.Control;
        Console.WriteLine($"Presenter: {child?.GetType().Name} size={child?.Bounds}");
        if (child is Avalonia.Controls.ItemsControl ic) Console.WriteLine($"Items in the presenter: {ic.ItemCount}");
        var shot = child != null ? Avalonia.Controls.TopLevel.GetTopLevel(child)?.CaptureRenderedFrame() : null;
        shot?.Save(Path.Combine(outDir, "choice.png"));
        continue;
    }

    if (page == "picker")
    {
        // opens the instrument picker and captures its popup
        window.Navigate("trading");
        var picker = window.Trading.Picker;
        picker.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(picker, null);
        Pump(4);
        var flyout = (Avalonia.Controls.Flyout)picker.GetType().GetField("_flyout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(picker)!;
        var content = (Avalonia.Controls.Control)flyout.Content!;
        var top = Avalonia.Controls.TopLevel.GetTopLevel(content);
        var shot = top?.CaptureRenderedFrame();
        var pp = Path.Combine(outDir, "picker.png");
        shot?.Save(pp);
        Console.WriteLine(shot != null ? $"Capture: {pp}" : "Could not capture the picker");
        flyout.Hide();
        continue;
    }

    var parts = page.Split('+');
    if (page == "data+rithmic")
    {
        // Data with a Rithmic instrument: 1 year (contracts of each stretch) and a short download against the fake server
        window.Navigate("data");
        Pump(1);
        var view = ((System.Collections.Generic.Dictionary<string, Avalonia.Controls.Control>)typeof(MainWindow).GetField("_pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!)["data"];
        var vt = view.GetType();
        var fl = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var es = (await App.Services.Connections.GetFeed("rithmic").GetInstrumentsAsync(CancellationToken.None)).First(i => i.Symbol == "ES");
        vt.GetField("_dlProfile", fl)!.SetValue(view, "rithmic");
        vt.GetField("_dlInstrument", fl)!.SetValue(view, es);
        var chip = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => (b.Content as string) == "1 year");
        chip.IsChecked = true;
        chip.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Pump(0.5);
        var status = (Avalonia.Controls.TextBlock)vt.GetField("_dlStatus", fl)!.GetValue(view)!;
        Console.WriteLine("1 year: " + status.Text);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "data_rithmic_year.png"));
        // yesterday's download (the fake server has trades from the last 2 hours: today downloads nothing, yesterday comes back empty)
        var y = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => (b.Content as string) == "Yesterday");
        y.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Console.WriteLine($"before: profile={vt.GetField("_dlProfile", fl)!.GetValue(view)} inst={vt.GetField("_dlInstrument", fl)!.GetValue(view)} can={vt.GetProperty("CanDownload", fl)!.GetValue(view)}");
        var dl = (Task)vt.GetMethod("DownloadAsync", fl)!.Invoke(view, null)!;
        string last = "";
        while (!dl.IsCompleted)
        {
            Pump(0.1);
            if (status.Text != last) Console.WriteLine("  · " + (last = status.Text ?? ""));
        }
        Console.WriteLine("Download: " + status.Text + (dl.Exception != null ? " · ERROR " + dl.Exception.GetBaseException() : ""));
        continue;
    }

    if (page == "theme")
    {
        // dark and light, with two charts (borders) and the chart settings by section
        window.Navigate("trading");
        var tv = window.Trading;
        var lay = tv.SetLayoutAsync(TradingHub.App.Views.PaneLayout.TwoColumns);
        while (!lay.IsCompleted) Pump(0.05);
        foreach (var c in tv.Tabs.Where(t => t.Model != null)) { c.Model!.Settings.Colors = null; c.Model.ApplyColors(); }
        Pump(3);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_dark.png"));
        TradingHub.App.Services.AppTheme.Apply(TradingHub.App.Services.AppTheme.Light);
        Pump(2);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_light.png"));
        tv.OpenChartSettings(tv.ActiveTab);
        Pump(1.5);
        var sw = (typeof(TradingHub.App.Views.TradingView).GetField("_settingsWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv) as System.Collections.IDictionary)?.Values.Cast<Avalonia.Controls.Window>().FirstOrDefault();
        if (sw != null)
        {
            var tabsCtl = sw.GetVisualDescendants().OfType<Avalonia.Controls.TabControl>().First();
            tabsCtl.SelectedIndex = tabsCtl.ItemCount - 1;
            Pump(1);
            sw.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_chart_settings_light.png"));
            sw.Close();
        }

        window.Navigate("settings");
        Pump(1.5);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_settings_light.png"));
        TradingHub.App.Services.AppTheme.Apply(TradingHub.App.Services.AppTheme.Dark);
        window.Navigate("accounts");
        Pump(1.5);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "theme_accounts_dark.png"));
        Console.WriteLine("Theme captures ready");
        continue;
    }

    if (page == "data+import")
    {
        // import a US100 CSV (like Dukascopy's) and check it shows up in Data and in the backtests
        var csv = Path.Combine(Path.GetTempPath(), "USATECHIDXUSD_Ticks_sample.csv");
        var sb = new System.Text.StringBuilder("Gmt time,Ask,Bid,AskVolume,BidVolume\n");
        var t = new DateTime(2025, 9, 1, 13, 30, 0, DateTimeKind.Utc);
        var rnd = new Random(3);
        double px = 23450;
        for (int i = 0; i < 20000; i++)
        {
            px += (rnd.NextDouble() - 0.5) * 2;
            t = t.AddMilliseconds(250 + rnd.Next(1500));
            sb.Append(t.ToString("dd.MM.yyyy HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append((px + 1.2).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
              .Append(px.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)).Append(",0.0015,0.0012\n");
        }

        File.WriteAllText(csv, sb.ToString());
        var r = TradingHub.Data.CsvImport.Import(csv, "US100", TradingHub.App.Services.AppPaths.Ticks, TimeSpan.Zero, null, null, CancellationToken.None);
        Console.WriteLine($"Imported: {r.Kind}, {r.Trades} trades, {r.Days} days, step {r.TickSize}");
        window.Navigate("data");
        Pump(1.5);
        var found = TradingHub.Data.HistoricalData.Scan(TradingHub.App.Services.AppPaths.Ticks).FirstOrDefault(x => x.FeedId == "importado" && x.Symbol == "US100");
        Console.WriteLine($"In Data: {(found == null ? "no" : $"US100 · imported, {found.Days.Count} day(s)")}");
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "data_import.png"));
        continue;
    }

    if (page == "accounts+demo")
    {
        // Account section with activity: orders on the simulated one (SIM-BTC) and on Rithmic (fake server, account ACC1)
        var tv = window.Trading;
        var simOpen = tv.OpenInTabAsync(tv.ActiveTab!, "sim", "SIM-BTC", TradingHub.Data.ChartPeriod.M1, tv.ActiveTab!.Model?.Settings.Clone());
        while (!simOpen.IsCompleted) Pump(0.05);
        Pump(2);
        var simData = tv.ActiveTab!.Model!.Data;
        var sim = App.Services.SimBrokerFor(simData);
        sim.Market(TradingHub.Trading.OrderDirections.Buy, 0.5m);
        Pump(1.5);
        sim.Market(TradingHub.Trading.OrderDirections.Buy, 0.5m);
        Pump(1.5);
        sim.Market(TradingHub.Trading.OrderDirections.Sell, 1m);
        Pump(1.5);
        sim.Market(TradingHub.Trading.OrderDirections.Sell, 0.3m);
        Pump(1.5);
        sim.Limit(TradingHub.Trading.OrderDirections.Buy, 0.2m, Math.Round(simData.LastPrice * 0.98m, 1));
        Pump(1);
        Console.WriteLine($"Simulated: position {sim.Position!.Volume} · orders {sim.Orders.Count} · stored fills {App.Services.Journal.FillsOf("sim").Count}");

        var rp = App.Services.Connections.Find("rithmic")!;
        var session = App.Services.RithmicSessionFor(rp);
        for (int i = 0; i < 100 && !session.IsConnected; i++) Pump(0.1);
        fakeRithmic!.MarketPrice = 5000;
        var o1 = session.PlaceAsync("ACC1", "ESZ6", "CME", true, 2, 2, 0, 0, "demo1", CancellationToken.None);
        while (!o1.IsCompleted) Pump(0.05);
        fakeRithmic.MarketPrice = 5006.25;
        var o2 = session.PlaceAsync("ACC1", "ESZ6", "CME", false, 2, 1, 0, 0, "demo2", CancellationToken.None);
        while (!o2.IsCompleted) Pump(0.05);
        var o3 = session.PlaceAsync("ACC1", "ESZ6", "CME", true, 1, 1, 4990, 4990, "demo3", CancellationToken.None);
        while (!o3.IsCompleted) Pump(0.05);
        Pump(1);
        Console.WriteLine($"Rithmic: balance {session.SummaryFor("ACC1")?.Balance} · fills {session.FillsOf("ACC1").Count} · stored {App.Services.Journal.FillsOf("rithmic|rithmic|ACC1").Count}");

        window.Navigate("accounts");
        Pump(1.5);
        var view = ((System.Collections.Generic.Dictionary<string, Avalonia.Controls.Control>)typeof(MainWindow).GetField("_pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!)["accounts"];
        var vt = view.GetType();
        var tabs = (Avalonia.Controls.TabControl)vt.GetField("_tabs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
        foreach (var acc in new[] { "rithmic|rithmic|ACC1", "sim" })
        {
            vt.GetField("_selected", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(view, acc);
            for (int t = 0; t < 5; t++)
            {
                tabs.SelectedIndex = t;
                Pump(1.3);
                string name = $"accounts_{(acc == "sim" ? "sim" : "rithmic")}_{t}";
                window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
                Console.WriteLine($"Capture: {name}");
            }
        }

        continue;
    }

    window.Navigate(parts[0]);
    Pump(1.2);

    if (parts.Length > 1 && parts[0] == "connections")
    {
        // picks the Rithmic profile and waits for the server list
        var cv = ((System.Collections.Generic.Dictionary<string, Avalonia.Controls.Control>)typeof(MainWindow).GetField("_pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(window)!)["connections"];
        var prof = App.Services.Connections.Profiles.FirstOrDefault(p => p.IsRithmic && (parts[1] != "amp" || p.Kind == TradingHub.App.Services.ConnectionKind.AmpFutures));
        if (prof != null) cv.GetType().GetMethod("Select", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(cv, new object[] { prof });
        Pump(9);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"connections_{parts[1]}.png"));
        Console.WriteLine($"Profile: {prof?.Name} ({prof?.Kind}) · {prof?.RithmicSystem} · {prof?.RithmicUri}");
        continue;
    }

    // trading actions: trading+layout4, trading+float, trading+brackets, trading+domsettings, trading+narrow
    if (parts.Length > 1 && parts[0] == "trading")
    {
        var tv = window.Trading;
        string action = parts[1];
        string shotName = page.Replace('+', '_') + ".png";
        if (action.StartsWith("layout"))
        {
            var layout = action switch
            {
                "layout2" => TradingHub.App.Views.PaneLayout.TwoColumns,
                "layout2r" => TradingHub.App.Views.PaneLayout.TwoRows,
                "layout3" => TradingHub.App.Views.PaneLayout.OneLeftTwoRight,
                "layout6" => TradingHub.App.Views.PaneLayout.Six,
                _ => TradingHub.App.Views.PaneLayout.Four,
            };
            var t1 = tv.SetLayoutAsync(layout);
            while (!t1.IsCompleted) Pump(0.05);
            Pump(seconds);
            Console.WriteLine($"Layout {layout}: {tv.ActivePage?.Charts.Count} charts in the tab, active {tv.ActiveTab?.Caption}");
        }
        else if (action.StartsWith("history"))
        {
            // trading+history8760[+H4]: loads that many hours (or "historyall") and changes the period
            double hours = action == "historyall" ? -1 : double.Parse(action["history".Length..], System.Globalization.CultureInfo.InvariantCulture);
            var hc = tv.ActiveTab!;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var load = tv.LoadHistoryAsync(hc, hours);
            while (!load.IsCompleted) Pump(0.1);
            Console.WriteLine($"History {hours} h loaded in {sw.Elapsed.TotalSeconds:0.0} s · {hc.Model?.Data.Trades.Count:N0} trades from {hc.Model?.Data.Trades.FirstTime:yyyy-MM-dd HH:mm} · real ticks from {hc.Model?.Data.RealTicksFrom:yyyy-MM-dd HH:mm}");
            if (parts.Length > 2) tv.Toolbar.SetPeriod(TradingHub.Data.ChartPeriod.Parse(parts[2]));
            Pump(seconds);
            Console.WriteLine($"Candles: {hc.Model?.Series.Count:N0} · synthetic: {hc.Model?.Series.Candles.Count(c => c.IsSynthetic):N0}");
        }
        else if (action is "jitter" or "jitterold")
        {
            if (action == "jitterold")
            {
                // the way it used to be: with no fixed widths on the price and the change
                foreach (var f in new[] { "_price", "_change" })
                    ((Avalonia.Controls.TextBlock)typeof(TradingHub.App.Controls.InstrumentPicker).GetField(f, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv.Picker)!).MinWidth = 0;
            }

            // the bar must not change height when the day's change passes through 0.00 %
            var inst = tv.ActiveTab!.Model!.Instrument;
            var buy = (Avalonia.Controls.Button)typeof(TradingHub.App.Views.TradingView).GetField("_buyButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv)!;
            if (Environment.GetEnvironmentVariable("TRADINGHUB_SNAP_SCAN") == "1")
            {
                // sweeps window widths and looks for where the bar changes height depending on the day's change
                int jumps = 0;
                for (int w = 1100; w <= 1700; w += 5)
                {
                    window.Width = w;
                    Pump(0.05);
                    tv.Picker.SetQuote(84710.6m, 0.25m, inst);
                    Pump(0.05);
                    double h1 = tv.Toolbar.Bounds.Height;
                    var y1 = buy.TranslatePoint(new Avalonia.Point(0, 0), window)?.Y;
                    tv.Picker.SetQuote(84710.6m, 0m, inst);
                    Pump(0.05);
                    double h2 = tv.Toolbar.Bounds.Height;
                    var y2 = buy.TranslatePoint(new Avalonia.Point(0, 0), window)?.Y;
                    if (h1 != h2 || y1 != y2)
                    {
                        jumps++;
                        Console.WriteLine($"width {w}: bar {h1:0}→{h2:0} px · BUY y={y1:0}→{y2:0} (it moves {y1 - y2:0} px)");
                    }
                }

                Console.WriteLine(jumps == 0 ? "No width makes the bar jump." : $"{jumps} widths with a jump");
                continue;
            }

            foreach (var (pct, label) in new[] { (0.25m, "with change"), (0m, "change 0.00 %"), (-12.5m, "change −12.50 %"), (0m, "change 0.00 %") })
            {
                tv.Picker.SetQuote(84710.6m, pct, inst);
                Pump(0.3);
                var pos = buy.TranslatePoint(new Avalonia.Point(0, 0), window);
                Console.WriteLine($"{label,-20} bar {tv.Toolbar.Bounds.Height:0} px · BUY button at y={pos?.Y:0}");
            }

            continue;
        }
        else if (action == "float")
        {
            var w = tv.Detach(tv.ActiveTab!);
            Pump(seconds);
            var fshot = w?.CaptureRenderedFrame();
            fshot?.Save(Path.Combine(outDir, "trading_float_window.png"));
            Console.WriteLine($"Separate window: {w?.Title} · windows={tv.FloatingWindows.Count} · tabs={tv.Pages.Count}");
            var main = window.CaptureRenderedFrame();
            main?.Save(Path.Combine(outDir, shotName));
            if (w != null) tv.DockBack(w);
            Pump(1);
            Console.WriteLine($"Back: windows={tv.FloatingWindows.Count} · charts in the tab={tv.ActivePage?.Charts.Count}");
            continue;
        }
        else if (action == "brackets" && tv.ActiveTab?.Model is { } m && m.Account is { } acc && m.Brackets is { } br)
        {
            for (int i = 0; i < 200 && m.Data.LastPrice == 0; i++) Pump(0.05);
            decimal last = m.Data.LastPrice;
            decimal tick = m.Instrument.TickSize;
            decimal q = Math.Max(m.Instrument.VolumeStep, 1m * m.Instrument.VolumeStep * 10);
            acc.Market(TradingHub.Trading.OrderDirections.Buy, q * 4);
            Pump(0.3);
            Console.WriteLine(br.DropFromPosition(last - 60 * tick));
            Console.WriteLine(br.DropFromPosition(last + 80 * tick));
            Console.WriteLine(br.AddTarget(last + 160 * tick));
            var entry = acc.Limit(TradingHub.Trading.OrderDirections.Buy, q, last - 120 * tick);
            Console.WriteLine(br.AttachToEntry(entry, last - 170 * tick));
            Console.WriteLine(br.AttachToEntry(entry, last - 40 * tick));
            m.Viewport.AutoScale = false;
            m.Viewport.PriceTop = (double)(last + 220 * tick);
            m.Viewport.PriceBottom = (double)(last - 220 * tick);
            m.Invalidate();
            Pump(seconds);
            tv.UpdatePosition();
            Pump(0.5);
            var bshot = window.CaptureRenderedFrame();
            bshot?.Save(Path.Combine(outDir, shotName));
            Console.WriteLine($"Capture: {Path.Combine(outDir, shotName)}");
            Console.WriteLine(br.ClosePartial(0.5m));
            Pump(0.5);
            Console.WriteLine($"After the partial: position={acc.Position.Volume} stop={br.StopLoss?.Unfilled} targets={string.Join(",", br.Targets.Select(t => t.Unfilled))}");
            acc.Flatten();
            Pump(0.5);
            Console.WriteLine($"Flat: active orders={acc.ActiveOrders.Count()}");
            m.Viewport.AutoScale = true;
            continue;
        }
        else if (action is "drawings" or "drawsettings")
        {
            // new drawings: long and short position, Fibonacci, zone with text, extended trend and alert
            var cm = tv.ActiveTab!.Model!;
            var ser = cm.Series;
            for (int i = 0; i < 100 && ser.Count < 40; i++) Pump(0.1);
            double last = (double)ser.TicksToPrice(ser[^1].CloseTicks);
            double hi = Enumerable.Range(1, 30).Max(k => (double)ser.TicksToPrice(ser[^k].HighTicks));
            double lo = Enumerable.Range(1, 30).Min(k => (double)ser.TicksToPrice(ser[^k].LowTicks));
            double r = Math.Max((hi - lo) / 4, (double)cm.Instrument.TickSize * 10);
            DateTime T(int back) => ser[^back].Time;
            var dl = new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.LongPosition, Points = { new(T(30), lo + r) }, Quantity = 0.5m };
            dl.InitPosition(r, T(12));
            var ds = new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.ShortPosition, Points = { new(T(10), hi - r / 2) } };
            ds.InitPosition(r / 2, T(1));
            cm.Drawings.Add(dl);
            cm.Drawings.Add(ds);
            cm.Drawings.Add(new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.Rectangle, Points = { new(T(28), hi), new(T(18), hi - r) }, Text = "Supply zone", ExtendRight = true, ColorArgb = 0xFFF2B53A });
            cm.Drawings.Add(new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.TrendLine, Points = { new(T(35), lo), new(T(20), lo + r) }, ExtendRight = true, Text = "Support", LineStyle = TradingHub.Charting.Drawings.LineStyleKind.Dashed });
            cm.Drawings.Add(new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.HorizontalLine, Points = { new(T(5), last + r * 1.5) }, Alert = true, ColorArgb = 0xFFF2B53A });
            cm.Invalidate();
            Pump(0.6);
            if (action == "drawsettings")
            {
                var win = new TradingHub.App.Windows.DrawingSettingsWindow(App.Services, dl, cm.Instrument, cm.Period, () => cm.Invalidate(), () => { }, () => { });
                win.Show(window);
                Pump(1.2);
                win.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_drawsettings.png"));
                var fib = new TradingHub.Charting.Drawings.ChartDrawing { Kind = TradingHub.Charting.Drawings.DrawingKind.FibRetracement, Points = { new(T(25), hi), new(T(5), lo) } };
                var win2 = new TradingHub.App.Windows.DrawingSettingsWindow(App.Services, fib, cm.Instrument, cm.Period, () => { }, () => { }, () => { });
                win2.Show(window);
                Pump(1.2);
                win2.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_drawsettings_fib.png"));
                Console.WriteLine("Captures: trading_drawsettings.png, trading_drawsettings_fib.png");
                win.Close();
                win2.Close();
                continue;
            }

            // legend with the mouse over the first indicator and the chart scrolled back (the button shows up)
            var st = (TradingHub.Charting.Rendering.ChartRenderState)typeof(TradingHub.App.Controls.ChartView).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv.ActiveTab.View)!;
            var nameHit = st.Legend.FirstOrDefault(h => h.Action == TradingHub.Charting.Rendering.LegendAction.Name);
            if (nameHit != null)
            {
                st.MouseX = nameHit.Rect.MidX;
                st.MouseY = nameHit.Rect.MidY;
            }

            cm.Invalidate();
            tv.ActiveTab.View.InvalidateVisual();
            Pump(0.6);
            Console.WriteLine($"Legend: {st.Legend.Count} zones ({string.Join(", ", st.Legend.Select(h => h.Action).Distinct())}) · go-to-latest button: {(st.GoLatestButton != null ? "yes" : "no")}");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_drawings.png"));
            cm.Viewport.ScrollBars = 25;
            st.MouseX = null;
            st.MouseY = null;
            cm.Invalidate();
            Pump(0.6);
            Console.WriteLine($"Scrolled 25 candles: go-to-latest button {(st.GoLatestButton != null ? "visible" : "NO visible")}");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_drawings_scrolled.png"));
            continue;
        }
        else if (action == "links")
        {
            // two charts linked in red: the second one takes the first one's instrument
            var setLayout = tv.SetLayoutAsync(TradingHub.App.Views.PaneLayout.TwoColumns);
            while (!setLayout.IsCompleted) Pump(0.05);
            Pump(1.5);
            var charts = tv.ActivePage!.Charts.Take(2).ToList();
            foreach (var c in charts) tv.SetLinkGroup(c, 1);
            Pump(2);
            var pt = charts[0].Model!.Period;
            tv.Toolbar.Chart = charts[0];
            tv.Activate(charts[0]);
            tv.Toolbar.SetPeriod(pt == TradingHub.Data.ChartPeriod.M5 ? TradingHub.Data.ChartPeriod.M15 : TradingHub.Data.ChartPeriod.M5);
            Pump(1.5);
            Console.WriteLine($"Linked: {string.Join(" | ", charts.Select(c => $"{c.Symbol} {c.Model?.Period.Code} group {c.LinkGroup}"))}");
            // crosshair on the first one -> it shows up on the second
            var view0 = charts[0].View;
            var ev = typeof(TradingHub.App.Controls.ChartView).GetMethod("RaiseCrosshair", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            ev.Invoke(view0, new object[] { new Avalonia.Point(view0.Bounds.Width * 0.6, view0.Bounds.Height * 0.4) });
            Pump(0.6);
            var st2 = (TradingHub.Charting.Rendering.ChartRenderState)typeof(TradingHub.App.Controls.ChartView).GetField("_state", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(charts[1].View)!;
            Console.WriteLine($"Linked crosshair on the second one: {(st2.LinkedTime != null ? st2.LinkedTime.Value.ToString("HH:mm") : "no")} price={(st2.LinkedPrice != null ? st2.LinkedPrice.Value.ToString("0.0") : "-")}");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_links.png"));
            continue;
        }
        else if (action == "toolbar")
        {
            // measures the bar: height (one or two lines) and width of each part
            Pump(1);
            var bar = tv.Toolbar;
            void Walk(Avalonia.Controls.Control c, int depth)
            {
                if (depth > 4) return;
                if (c is Avalonia.Controls.WrapPanel wp)
                {
                    Console.WriteLine($"WrapPanel width={wp.Bounds.Width:0} height={wp.Bounds.Height:0}");
                    foreach (var ch in wp.Children) Console.WriteLine($"   {ch.GetType().Name,-22} x={ch.Bounds.X:0} y={ch.Bounds.Y:0} w={ch.Bounds.Width:0}");
                    return;
                }

                if (c is Avalonia.Controls.StackPanel sp && depth > 1) Console.WriteLine($"Right panel width={sp.Bounds.Width:0}");
                foreach (var ch in Avalonia.VisualTree.VisualExtensions.GetVisualChildren(c).OfType<Avalonia.Controls.Control>()) Walk(ch, depth + 1);
            }

            Walk(bar, 0);
            Console.WriteLine($"Bar height={bar.Bounds.Height:0} window={window.Bounds.Width:0}");
            continue;
        }
        else if (action == "trader")
        {
            // its own Chart Trader and cross trading: from BTCUSDT it buys BTCUSDC
            var trChart = tv.ActiveTab!;
            Pump(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_trader.png"));
            var cross = tv.SetCrossTradingAsync(trChart, trChart.Symbol == "BTCUSDT" ? "BTCUSDC" : "BTCUSDT");
            while (!cross.IsCompleted) Pump(0.05);
            for (int i = 0; i < 80 && (trChart.CrossData?.LastPrice ?? 0) == 0; i++) Pump(0.1);
            var own = App.Services.AccountFor(trChart.Model!.Data, "sim");
            var target = tv.AccountOf(trChart)!;
            Console.WriteLine($"Cross: it watches {trChart.Symbol}, executes on {trChart.CrossSymbol} · target account {target.Instrument.Symbol} · target price {trChart.CrossData?.LastPrice}");
            var order = tv.TradeAsync(trChart, TradingHub.Trading.OrderTypes.Market, TradingHub.Trading.OrderDirections.Buy, 0, 0.002m);
            while (!order.IsCompleted) Pump(0.05);
            Pump(1.5);
            Console.WriteLine($"After buying: {target.Instrument.Symbol} position {target.Position.Volume} · {own.Instrument.Symbol} position {own.Position.Volume}");
            trChart.Trader?.Refresh();
            Pump(0.5);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_trader_cross.png"));
            var flat = tv.AccountActionAsync(trChart, a => a.Flatten(), "closed");
            while (!flat.IsCompleted) Pump(0.05);
            Pump(1);
            Console.WriteLine($"Closed: {target.Instrument.Symbol} position {target.Position.Volume}");
            var off = tv.SetCrossTradingAsync(trChart, null);
            while (!off.IsCompleted) Pump(0.05);
            continue;
        }
        else if (action == "accounts")
        {
            // Chart Trader's account picker with a Rithmic account (fake server): they all show and it switches back and forth
            var ch = tv.ActiveTab!;
            if (ch.FeedId != "rithmic")
            {
                var op = tv.OpenInTabAsync(ch, "rithmic", "ES", TradingHub.Data.ChartPeriod.M1, ch.Model?.Settings.Clone());
                while (!op.IsCompleted) Pump(0.05);
            }

            tv.SetTraderVisible(ch, true);
            for (int i = 0; i < 200 && !App.Services.AccountsFor(ch.Model!.Data).Any(a => a.Id.StartsWith("rithmic|")); i++) Pump(0.1);
            Console.WriteLine("Accounts: " + string.Join(" | ", App.Services.AccountsFor(ch.Model!.Data).Select(a => $"{a.Id} = {a.Name}")));
            ch.Trader!.Refresh();
            Pump(0.5);
            var box = (Avalonia.Controls.ComboBox)typeof(TradingHub.App.Views.ChartTraderPanel).GetField("_account", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ch.Trader)!;

            void Click(Avalonia.Controls.Control c)
            {
                var top = Avalonia.Controls.TopLevel.GetTopLevel(c)!;
                var pt = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)!.Value;
                top.MouseMove(pt);
                top.MouseDown(pt, Avalonia.Input.MouseButton.Left);
                top.MouseUp(pt, Avalonia.Input.MouseButton.Left);
                Pump(0.6);
            }

            void Shot(string name)
            {
                window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + ".png"));
                // if the open list is a separate window, it is captured too
                var popupTop = box.Items.OfType<Avalonia.Controls.ComboBoxItem>().Select(Avalonia.Controls.TopLevel.GetTopLevel).FirstOrDefault(t => t != null && t != window);
                popupTop?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, name + "_lista.png"));
            }

            string State() => $"open={box.IsDropDownOpen} · selected='{(box.SelectedItem as Avalonia.Controls.ComboBoxItem)?.Content}' · chart account={ch.AccountId} · trades on={tv.AccountOf(ch)?.Name}";

            Avalonia.Controls.ComboBoxItem Item(string idPrefix) =>
                box.Items.OfType<Avalonia.Controls.ComboBoxItem>().First(i => i.Tag is TradingHub.App.Services.AccountOption o && o.Id.StartsWith(idPrefix, StringComparison.Ordinal));

            for (int round = 1; round <= 2; round++)
            {
                Click(box);
                Console.WriteLine($"[{round}] open: {State()}");
                foreach (var it in box.Items.OfType<Avalonia.Controls.ComboBoxItem>())
                {
                    var cp = it.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().FirstOrDefault();
                    Console.WriteLine($"     · '{it.Content}' visible={it.IsEffectivelyVisible} height={it.Bounds.Height:0} color={(cp?.Foreground as Avalonia.Media.ISolidColorBrush)?.Color.ToString() ?? "NONE"}");
                }

                if (round == 1) Shot("trading_accounts_open");
                Click(Item("rithmic|"));
                Console.WriteLine($"[{round}] pick Rithmic: {State()}");
                if (round == 1) Shot("trading_accounts_rithmic");
                Click(box);
                Console.WriteLine($"[{round}] open again: {State()}");
                Click(Item("sim"));
                Console.WriteLine($"[{round}] pick simulated: {State()}");
            }

            Shot("trading_accounts_back");
            continue;
        }
        else if (action == "crossdemo")
        {
            // you watch ES and execute MES (cross trading): the MES position shows on the ES chart; right panel and colors
            var ch = tv.ActiveTab!;
            if (ch.FeedId != "rithmic" || ch.Symbol != "ES")
            {
                var op = tv.OpenInTabAsync(ch, "rithmic", "ES", TradingHub.Data.ChartPeriod.M1, ch.Model?.Settings.Clone());
                while (!op.IsCompleted) Pump(0.05);
            }

            double px = fakeRithmic!.MarketPrice;
            void PumpPush(double secs)
            {
                var end = DateTime.UtcNow.AddSeconds(secs);
                while (DateTime.UtcNow < end)
                {
                    _ = fakeRithmic.PushTrade("ESZ6", px, 1, true);
                    _ = fakeRithmic.PushTrade("MESZ6", px, 1, false);
                    Pump(0.2);
                }
            }

            tv.SetChartAccount(ch, "sim");
            var cross = tv.SetCrossTradingAsync(ch, "MES");
            while (!cross.IsCompleted) Pump(0.05);
            PumpPush(3);
            Console.WriteLine($"Cross: it watches {ch.Symbol} ({ch.Model!.Data.Status}, {ch.Model.Data.LastPrice}) · executes on {ch.CrossSymbol} ({ch.CrossData?.Status}, {ch.CrossData?.LastPrice})");
            var buy = tv.TradeAsync(ch, TradingHub.Trading.OrderTypes.Market, TradingHub.Trading.OrderDirections.Buy, 0, 1);
            while (!buy.IsCompleted) Pump(0.05);
            px += 1.25;
            PumpPush(2);
            var crossAcc = tv.AccountOf(ch)!;
            Console.WriteLine($"Chart account: {ch.Model.Account?.Instrument.Symbol} · position {crossAcc.Instrument.Symbol} {crossAcc.Position.Volume} @ {crossAcc.Position.AveragePrice} · same object={ReferenceEquals(ch.Model.Account, crossAcc)}");
            ch.Model.Viewport.ScrollBars = 0;
            ch.Model.Viewport.AutoScale = true;
            PumpPush(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_cross_projection.png"));

            // right panel = the active chart's own Chart Trader
            tv.SetPanel("ticket", true);
            PumpPush(1.5);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_side_trader.png"));
            var docked = typeof(TradingHub.App.Views.TradingView).GetField("_dockedTrader", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv) as TradingHub.App.Views.ChartTraderPanel;
            Console.WriteLine($"Right panel: {(docked == null ? "no" : $"Chart Trader of {docked.Chart.Symbol} (same chart={ReferenceEquals(docked.Chart, ch)})")}");

            // the chart's own colors
            tv.OpenChartSettings(ch);
            Pump(1);
            var sw = (typeof(TradingHub.App.Views.TradingView).GetField("_settingsWindows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(tv) as System.Collections.IDictionary)?.Values.Cast<Avalonia.Controls.Window>().FirstOrDefault();
            sw?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "chart_settings_window.png"));
            ch.Model.Settings.Colors = new() { ["BackgroundTop"] = 0xFF131722, ["BackgroundBottom"] = 0xFF131722, ["UpCandle"] = 0xFF26A69A, ["DownCandle"] = 0xFFF23645, ["UpCandleBorder"] = 0xFF26A69A, ["DownCandleBorder"] = 0xFFF23645 };
            ch.Model.ApplyColors();
            sw?.Close();
            PumpPush(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_custom_colors.png"));
            var others = tv.Tabs.Where(t => !ReferenceEquals(t, ch) && t.Model != null).Select(t => t.Model!.Theme.BackgroundTop.ToString()).Distinct();
            Console.WriteLine($"Colors: this chart's background {ch.Model.Theme.BackgroundTop} · the others {string.Join(", ", others)}");

            var flat = tv.AccountActionAsync(ch, a => a.Flatten(), "closed");
            while (!flat.IsCompleted) Pump(0.05);
            PumpPush(1.5);
            Console.WriteLine($"Closed: {crossAcc.Instrument.Symbol} position {crossAcc.Position.Volume}");
            PumpPush(2);
            Console.WriteLine($"Marks on {ch.Symbol}: {ch.Model.TradeMarks.Count} fills ({string.Join(", ", ch.Model.TradeMarks.Select(m => (m.Buy ? "buy " : "sell ") + m.Quantity + " @ " + m.Price))}) · {ch.Model.TradeLinks.Count} closed trade(s)");
            ch.Model.Settings.Colors = null;
            ch.Model.ApplyColors();
            ch.Model.Viewport.ScrollBars = 0;
            ch.Model.Viewport.BarWidth = 22;
            PumpPush(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_trade_marks.png"));

            // templates: this chart is saved, everything on it is changed and the template is applied
            var model = ch.Model;
            model.Settings.Colors = new() { ["BackgroundTop"] = 0xFF131722 };
            model.ApplyColors();
            var vwapInfo = App.Services.Catalog.Items.FirstOrDefault(i => i.Name.Contains("VWAP", StringComparison.OrdinalIgnoreCase)) ?? App.Services.Catalog.Items.First();
            foreach (var slot in model.Indicators.ToList()) model.RemoveIndicator(slot);
            model.AddIndicator((TradingHub.Indicators.Indicator)Activator.CreateInstance(vwapInfo.Type)!);
            var styleBefore = model.Settings.Style;
            TradingHub.App.Services.Workspace.SaveChartTemplate(ch, "Template test");
            tv.Toolbar.SetStyle(styleBefore == TradingHub.Charting.ChartStyle.Footprint ? TradingHub.Charting.ChartStyle.Candles : TradingHub.Charting.ChartStyle.Footprint);
            model.Settings.Colors = null;
            model.ApplyColors();
            foreach (var slot in model.Indicators.ToList()) model.RemoveIndicator(slot);
            Pump(0.5);
            Console.WriteLine($"Stored templates: {string.Join(", ", TradingHub.App.Services.Workspace.ChartTemplateNames())} · before applying: style {model.Settings.Style}, {model.Indicators.Count} indicators, background {model.Theme.BackgroundTop}");
            bool applied = TradingHub.App.Services.Workspace.ApplyChartTemplate(tv, ch, "Template test");
            Pump(0.5);
            Console.WriteLine($"Applied={applied}: style {model.Settings.Style} (was {styleBefore}), indicators [{string.Join(", ", model.Indicators.Select(i => i.Indicator.Name))}], background {model.Theme.BackgroundTop}");

            // indicator template: stored settings, applied to a new one and as the default
            var ind = model.Indicators.First().Indicator;
            var numeric = ind.GetType().GetProperties().FirstOrDefault(p => p.CanWrite && p.PropertyType == typeof(int) && p.GetCustomAttributes(typeof(System.ComponentModel.BrowsableAttribute), true).Length == 0 && p.DeclaringType != typeof(TradingHub.Indicators.Indicator));
            if (numeric != null) numeric.SetValue(ind, (int)numeric.GetValue(ind)! + 7);
            TradingHub.App.Services.Workspace.SaveIndicatorTemplate(ind, "Mi " + ind.Name);
            var fresh = (TradingHub.Indicators.Indicator)Activator.CreateInstance(ind.GetType())!;
            bool indApplied = TradingHub.App.Services.Workspace.ApplyIndicatorTemplate(fresh, "Mi " + ind.Name);
            var a1 = TradingHub.App.Services.Workspace.SaveIndicator(ind, out _).ToString();
            var a2 = TradingHub.App.Services.Workspace.SaveIndicator(fresh, out _).ToString();
            App.Services.Settings.DefaultIndicatorTemplates[ind.GetType().FullName!] = "Mi " + ind.Name;
            var fresh2 = (TradingHub.Indicators.Indicator)Activator.CreateInstance(ind.GetType())!;
            bool byDefault = TradingHub.App.Services.Workspace.ApplyDefaultIndicatorTemplate(App.Services, fresh2);
            Console.WriteLine($"Indicator template: {string.Join(", ", TradingHub.App.Services.Workspace.IndicatorTemplateNames(ind.GetType()))} · changed {numeric?.Name}={numeric?.GetValue(ind)} · applied={indApplied} equal={a1 == a2} · default={byDefault} ({numeric?.Name}={numeric?.GetValue(fresh2)})");
            App.Services.Settings.DefaultIndicatorTemplates.Clear();
            continue;
        }
        else if (action == "strategy")
        {
            // strategy on the chart: history + live (visual), settings, switching to executing with the simulated one and removing it
            var ch = tv.ActiveTab!;
            var open1 = tv.OpenInTabAsync(ch, "sim", "SIM-BTC", TradingHub.Data.ChartPeriod.M1, ch.Model?.Settings.Clone());
            while (!open1.IsCompleted) Pump(0.05);
            for (int i = 0; i < 200 && ch.Model!.Data.IsLoadingHistory; i++) Pump(0.1);
            // the moving average cross exactly as the example ships it: the copy of the user's own may have changed and may not trade on
            // this short history (it is only written in the test folder, never in the real one)
            var strategiesDir = Path.GetFullPath(TradingHub.App.Services.AppPaths.Strategies);
            if (strategiesDir != Path.GetFullPath(Path.Combine(TradingHub.Common.Product.Documents, "Estrategias")))
            {
                var example = TradingHub.App.Services.StrategyTemplates.All.First(t => t.FileName == "MovingAverageCross");
                File.WriteAllText(Path.Combine(strategiesDir, "MovingAverageCross.cs"), example.Code("MovingAverageCross").Replace("__NAME__", "Moving average cross"));
            }

            App.Services.Strategies.Refresh();
            var file = App.Services.Strategies.Files.First(f => f.FileName == "MovingAverageCross");
            // the chart without the strategies the copied workspace brings: only the test one is measured
            foreach (var old in ch.Strategies.ToList()) tv.RemoveStrategy(ch, old);
            var add = tv.AddStrategyAsync(ch, file, openSettings: false);
            while (!add.IsCompleted) Pump(0.1);
            var cs = ch.Strategies[^1];
            // if the chart loads more history the strategy is recalculated: it waits for the last run to finish
            bool Calculating() => cs.Busy || cs.Overlay.Status is "calculating…" or "waiting for the history…";
            for (int round = 0; round < 3; round++)
            {
                for (int i = 0; i < 600 && Calculating(); i++) Pump(0.1);
                Pump(2);
                if (!Calculating()) break;
            }
            Console.WriteLine($"Visual: status “{cs.Overlay.Status}” {cs.Overlay.Error} · markers {cs.Overlay.Signals.Count} · lines {cs.Overlay.Links.Count} · runner={(cs.Runner?.IsRunning == true ? "running" : "no")} on {cs.Runner?.Account.Name}");
            ch.Model!.Viewport.ScrollBars = 0;
            ch.Model.Viewport.BarWidth = 9;
            Pump(1);
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategy_visual.png"));

            tv.OpenStrategySettings(ch, cs);
            Pump(1.5);
            var sw = Avalonia.Application.Current!.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lt
                ? lt.Windows.OfType<TradingHub.App.Windows.StrategyChartWindow>().FirstOrDefault() : null;
            sw ??= window.OwnedWindows.OfType<TradingHub.App.Windows.StrategyChartWindow>().FirstOrDefault();
            sw?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategy_settings.png"));
            // and below, how it did on the chart's history (real result, curve and statistics)
            if (sw?.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().FirstOrDefault() is { } scroll)
            {
                scroll.ScrollToEnd();
                Pump(0.6);
                scroll.ScrollToEnd();
                Pump(0.4);
                sw.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategy_settings_results.png"));
            }

            sw?.Close();

            // execute with the simulated account
            cs.Live = true;
            cs.AccountId = "sim";
            var run = tv.RunStrategyAsync(ch, cs);
            while (!run.IsCompleted) Pump(0.1);
            Pump(2);
            Console.WriteLine($"Live: status “{cs.Overlay.Status}” · in the Strategies list={App.Services.Runners.Contains(cs.Runner!)} · account {cs.Runner?.Account.Name}");
            TradingHub.App.Services.Workspace.Save(tv);
            var ws = File.ReadAllText(TradingHub.App.Services.AppPaths.WorkspaceFile);
            Console.WriteLine($"Stored in the workspace: {ws.Contains("MovingAverageCross") || ws.Contains("MovingAverage")} (Live={ws.Contains("\"Live\": true")})");
            var runner = cs.Runner!;
            tv.RemoveStrategy(ch, cs);
            Pump(0.5);
            Console.WriteLine($"Removed: runner running={runner.IsRunning} · in the list={App.Services.Runners.Contains(runner)} · legend {ch.Model.Strategies.Count}");
            continue;
        }
        else if (action == "domfloat")
        {
            tv.DetachDom();
            Pump(1.5);
            var dw = tv.PanelWindow("dom");
            dw?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_domfloat_window.png"));
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "trading_domfloat_main.png"));
            Console.WriteLine($"DOM outside: {(dw != null ? dw.Title : "no")} · floating={tv.DomIsFloating}");
            tv.DockDom();
            Pump(1);
            Console.WriteLine($"DOM back: floating={tv.DomIsFloating}");
            continue;
        }
        else if (action == "domsettings")
        {
            var settingsWin = tv.ShowDomSettings();
            Pump(1.5);
            var dshot = settingsWin?.CaptureRenderedFrame();
            dshot?.Save(Path.Combine(outDir, shotName));
            Console.WriteLine(dshot != null ? $"Capture: {Path.Combine(outDir, shotName)}" : "The DOM window was not found");
            settingsWin?.Close();
            continue;
        }

        Pump(0.5);
        var tshot = window.CaptureRenderedFrame();
        tshot?.Save(Path.Combine(outDir, shotName));
        Console.WriteLine($"Capture: {Path.Combine(outDir, shotName)}");
        continue;
    }

    // optional actions: strategies+backtest, strategies+optimize, strategies+code…
    if (parts.Length > 1 && parts[0] == "strategies")
    {
        var pagesField = typeof(MainWindow).GetField("_pages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var view = ((System.Collections.Generic.Dictionary<string, Avalonia.Controls.Control>)pagesField.GetValue(window)!)["strategies"];
        var t = view.GetType();
        for (int i = 0; i < 200 && t.GetField("_current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view) is { } cur && cur.GetType().GetProperty("Type")!.GetValue(cur) == null; i++) Pump(0.05);
        if (parts.Length > 2)
        {
            // picks the strategy by file name
            var catalog = t.GetField("_catalog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
            var files = (System.Collections.IEnumerable)catalog.GetType().GetProperty("Files")!.GetValue(catalog)!;
            var file = files.Cast<object>().First(f => (string)f.GetType().GetProperty("FileName")!.GetValue(f)! == parts[2]);
            var sel = (Task)t.GetMethod("SelectAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new[] { file })!;
            while (!sel.IsCompleted) Pump(0.05);
        }

        if (parts[1] == "claudefloat")
        {
            // sample conversation in the history (separate data folder) and chat in a window
            var conv = new TradingHub.App.Services.ChatConversation { Title = "Create a 30-minute range breakout strategy", SessionId = "test", Strategy = "Breakout.cs" };
            conv.Messages.Add(new TradingHub.App.Services.ChatMessage { Role = "user", Text = conv.Title });
            conv.Messages.Add(new TradingHub.App.Services.ChatMessage { Role = "assistant", Text = "Done: I created Breakout.cs with the breakout of the first 30 minutes' range, an ATR stop and an exit at the close." });
            TradingHub.App.Services.ChatHistory.Save(conv);
            t.GetMethod("ShowTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { "code" });
            Pump(0.5);
            t.GetMethod("LoadConversation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { conv });
            t.GetMethod("DetachClaude", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, null);
            Pump(1.5);
            var cw = (Avalonia.Controls.Window?)t.GetField("_claudeWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view);
            cw?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategies_claudefloat_window.png"));
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategies_claudefloat_main.png"));
            Console.WriteLine($"Chat outside: {(cw != null ? cw.Title : "no")} · conversations in the history: {TradingHub.App.Services.ChatHistory.List().Count}");
            t.GetMethod("DockClaude", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, null);
            Pump(0.8);
            Console.WriteLine($"Chat back: {(t.GetField("_claudeWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view) == null ? "yes" : "no")}");
            continue;
        }

        if (parts[1] is "claude" or "claudelab")
        {
            t.GetMethod("ShowTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { "code" });
            if (parts[1] == "claudelab")
                ((Avalonia.Controls.Primitives.ToggleButton)t.GetField("_labToggle", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!).IsChecked = true;
            string ask = parts[1] == "claudelab" ? "Research and optimize the open strategy" : "Create a strategy that trades the breakout of the first 30 minutes' range";
            var send = (Task)t.GetMethod("SendToClaudeAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { ask })!;
            var until = DateTime.UtcNow.AddSeconds(40);
            while (!send.IsCompleted && DateTime.UtcNow < until) Pump(0.05);
            Pump(2.5);
            var frame2 = window.CaptureRenderedFrame();
            var path2 = Path.Combine(outDir, $"strategies_{parts[1]}.png");
            frame2?.Save(path2);
            Console.WriteLine($"Capture: {path2}");
            continue;
        }

        string method = parts[1] switch { "optimize" => "RunOptimizationAsync", "wfo" => "RunWalkForwardAsync", _ => "RunBacktestAsync" };
        if (parts[1] is "code" or "history" or "reports" or "portfolio" or "portfolio")
        {
            t.GetMethod("ShowTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { parts[1] });
        }
        else
        {
            var tabId = parts[1] switch { "optimize" => "optimize", "wfo" => "wfo", _ => "backtest" };
            t.GetMethod("ShowTab", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, new object[] { tabId });
            var task = (Task)t.GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(view, null)!;
            var limit = DateTime.UtcNow.AddMinutes(4);
            while (!task.IsCompleted && DateTime.UtcNow < limit) Pump(0.05);
        }

        Pump(0.8);
    }

    var frame = window.CaptureRenderedFrame();
    var path = Path.Combine(outDir, page.Replace('+', '_') + ".png");
    frame?.Save(path);
    Console.WriteLine(frame != null ? $"Capture: {path}" : $"Could not capture {page}");
}

App.Services.Shutdown();
GC.KeepAlive(snapLock); // the lock holds until the very end
return exitCode;
