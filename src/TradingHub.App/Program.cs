using Avalonia;

namespace TradingHub.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // An interpolation like $"{ticks:N0}" does not go through the Fmt field each view keeps, so it
        // follows the machine's culture: on an en-IN Windows the same screen showed "12,00,000" next to
        // "1,200.50". Pinning it here covers all of those and makes parsing agree with what is shown.
        // en-GB rather than en-US: same numbers (1,200,000.50), but day-first dates, which is what the
        // explicit "dd/MM/yyyy" formats all over the app already use.
        var fmt = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = fmt;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = fmt;

        // lab mode: Trading Hub as an MCP server for Claude Code (no windows)
        if (args.Contains("--mcp"))
        {
            Environment.ExitCode = Lab.LabServer.RunAsync().GetAwaiter().GetResult();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };

        WaitForDisplay();
        try
        {
            BuildAvaloniaApp().AfterSetup(_ =>
            {
                // an error in a control is logged and the app keeps running (it does not close mid-trading)
                Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
                {
                    CrashLog.Write(e.Exception);
                    e.Handled = true;
                };
            }).StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            throw;
        }
    }

    [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices")]
    private static extern int CGGetActiveDisplayList(uint maxDisplays, uint[]? activeDisplays, out uint displayCount);

    /// <summary>
    /// With the screen off or locked macOS has no active display and Avalonia cannot start drawing
    /// (error -6661): instead of quitting, Trading Hub waits for the display to come back and opens then.
    /// </summary>
    private static void WaitForDisplay()
    {
        if (!OperatingSystem.IsMacOS()) return;
        for (int i = 0; ; i++)
        {
            try
            {
                if (CGGetActiveDisplayList(0, null, out var count) != 0 || count > 0) return;
            }
            catch (Exception)
            {
                return; // when in doubt it starts as usual
            }

            if (i == 0) CrashLog.Write(new InvalidOperationException("There is no active display (off or locked): Trading Hub will open when it comes back"));
            Thread.Sleep(2000);
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            // menus and dropdowns inside the window: as separate windows, the first click was sometimes lost
            .With(new AvaloniaNativePlatformOptions { OverlayPopups = true })
            .LogToTrace();
}

/// <summary>Stores unexpected errors in crash.log, in the app's data folder.</summary>
internal static class CrashLog
{
    public static void Write(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            Directory.CreateDirectory(Services.AppPaths.Root);
            File.AppendAllText(Path.Combine(Services.AppPaths.Root, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // nothing to do
        }
    }
}
