// "settings" scene: the "Record the order book" switch is there, starts on, and flips the hub's flag and the saved settings.
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using TradingHub.App.Windows;

internal static class SettingsScene
{
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "OK " : "FAIL")}  {what}");
        if (!ok) Failures.Add(what);
    }

    private static ToggleSwitch? SwitchOf(MainWindow window, string title) =>
        window.GetVisualDescendants().OfType<DockPanel>()
            .Where(d => d.Children.OfType<ToggleSwitch>().Any()) // the row itself, not a container around it
            .FirstOrDefault(d => d.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == title))
            ?.Children.OfType<ToggleSwitch>().First();

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        window.Navigate("settings");
        pump(1.5);
        var s = window.Services;
        var depth = SwitchOf(window, "Record the order book");
        var ticks = SwitchOf(window, "Record incoming live data");
        Check(ticks != null, "the existing 'Record incoming live data' switch is still there");
        Check(depth != null, "there is a 'Record the order book' switch");
        if (depth == null) return 1;
        Check(depth.IsChecked == true && s.Settings.RecordDepth && s.Hub.RecordDepth, "it starts on (settings and hub agree)");
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "settings-record.png"));

        depth.IsChecked = false;
        pump(0.3);
        Check(!s.Settings.RecordDepth && !s.Hub.RecordDepth, "switching it off turns off the setting and the hub's flag");
        var file = Path.Combine(TradingHub.App.Services.AppPaths.Root, "settings.json");
        Check(File.Exists(file) && File.ReadAllText(file).Replace(" ", "").Contains("\"RecordDepth\":false", StringComparison.OrdinalIgnoreCase), "and it is saved to settings.json");
        Check(ticks!.IsChecked == true && s.Hub.RecordLive, "recording trades is not affected");

        depth.IsChecked = true;
        pump(0.3);
        Check(s.Settings.RecordDepth && s.Hub.RecordDepth, "switching it on again turns both back on");
        return Failures.Count;
    }
}
