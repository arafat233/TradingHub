using Avalonia.Threading;
using TradingHub.Common.Logging;
using TradingHub.Scripting;

namespace TradingHub.App.Services;

/// <summary>
/// The user's own indicators: it compiles the .cs files in ~/Documents/Trading Hub/Indicators on startup and
/// every time they change (hot reload), and adds them to the catalog.
/// </summary>
public sealed class UserScripts : IDisposable
{
    public const string Source = "My indicators";
    private readonly IndicatorCatalog _catalog;
    private readonly ScriptCompiler _compiler = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;

    public UserScripts(IndicatorCatalog catalog)
    {
        _catalog = catalog;
        Folder = AppPaths.UserIndicators;
        Directory.CreateDirectory(Folder);
        WriteExampleIfEmpty();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            CompileAll();
        };

        try
        {
            _watcher = new FileSystemWatcher(Folder, "*.cs") { IncludeSubdirectories = true, EnableRaisingEvents = true };
            _watcher.Changed += (_, _) => Dispatcher.UIThread.Post(Restart);
            _watcher.Created += (_, _) => Dispatcher.UIThread.Post(Restart);
            _watcher.Deleted += (_, _) => Dispatcher.UIThread.Post(Restart);
            _watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(Restart);
        }
        catch (Exception ex)
        {
            this.LogWarn("Could not watch the indicators folder: " + ex.Message);
        }
    }

    public string Folder { get; }
    public ScriptCompileResult? LastResult { get; private set; }

    /// <summary>Fires after every compilation (successful or with errors).</summary>
    public event Action<ScriptCompileResult>? Compiled;

    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public ScriptCompileResult? CompileAll()
    {
        var files = Directory.EnumerateFiles(Folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
            .OrderBy(f => f)
            .ToList();
        if (files.Count == 0)
        {
            _catalog.RemoveSource(Source);
            return null;
        }

        ScriptCompileResult res;
        try
        {
            res = _compiler.Compile(files, "TradingHubUserIndicators");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a half-saved or moved file: it is retried on the folder's next change
            this.LogWarn("Could not read your own indicators: " + ex.Message);
            Restart();
            return LastResult;
        }

        LastResult = res;
        if (res.Success && res.Assembly != null)
        {
            _catalog.RemoveSource(Source);
            _catalog.Scan(res.Assembly, Source);
            this.LogInfo($"Own indicators compiled ({files.Count} files, {res.Elapsed.TotalMilliseconds:0} ms)");
        }
        else
        {
            foreach (var d in res.Diagnostics.Where(d => d.Severity == "error").Take(20)) this.LogWarn(d.ToString());
        }

        Compiled?.Invoke(res);
        return res;
    }

    private void WriteExampleIfEmpty()
    {
        if (Directory.EnumerateFiles(Folder, "*.cs", SearchOption.AllDirectories).Any()) return;
        File.WriteAllText(Path.Combine(Folder, "EjemploDeltaPorcentual.cs"), ExampleSource);
    }

    public const string ExampleSource = """
        // Example of a custom indicator. Save your changes and Trading Hub recompiles it on its own.
        // The API is the same as ATAS's: you can paste indicators written for ATAS.
        using System.ComponentModel;
        using System.ComponentModel.DataAnnotations;
        using System.Drawing;
        using TradingHub.Indicators;

        [DisplayName("Example: delta % per candle")]
        [Category("My indicators")]
        public class ExampleDeltaPercent : Indicator
        {
            private readonly ValueDataSeries _series = new("DeltaPct", "Delta %")
            {
                VisualType = VisualMode.Histogram,
            };

            [Display(Name = "Highlight threshold (%)", GroupName = "Settings")]
            public decimal Threshold { get; set; } = 30;

            public ExampleDeltaPercent()
            {
                Panel = IndicatorDataProvider.NewPanel;
                DataSeries[0] = _series;
            }

            protected override void OnCalculate(int bar, decimal value)
            {
                var candle = GetCandle(bar);
                var pct = candle.Volume > 0 ? candle.Delta / candle.Volume * 100 : 0;
                _series[bar] = pct;
                _series.Colors[bar] = Math.Abs(pct) >= Threshold
                    ? (pct > 0 ? Color.FromArgb(255, 73, 209, 73) : Color.FromArgb(255, 232, 69, 72))
                    : Color.FromArgb(255, 110, 120, 140);
            }
        }
        """;

    public void Dispose() => _watcher?.Dispose();
}
