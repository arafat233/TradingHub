using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using TradingHub.App.Windows;

namespace TradingHub.App.Services;

/// <summary>
/// Trading Hub in the Mac's menu bar (top right): clicking its icon opens a panel with the live strategies,
/// the positions and what the agents are doing. Closing the window leaves Trading Hub running there (if it is
/// enabled in Settings); you quit for good with Cmd+Q or with "Quit Trading Hub" from the panel.
/// </summary>
internal sealed class MenuBar
{
    private readonly AppServices _svc;
    private readonly MainWindow _main;
    private readonly TrayIcon _tray;
    private MenuBarPanel? _panel;
    private DateTime _panelHiddenAt;
    private readonly NativeMenu _menu = new();
    private readonly DispatcherTimer _menuTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(string Key, NativeMenuItemBase Item)> _menuItems = new();

    private MenuBar(Application app, AppServices svc, MainWindow main)
    {
        _svc = svc;
        _main = main;
        _tray = new TrayIcon { Icon = new WindowIcon(Glyph()), ToolTipText = TradingHub.Common.Product.Name, IsVisible = svc.Settings.KeepInMenuBar };
        MacOSProperties.SetIsTemplateIcon(_tray, true); // it is painted white or black depending on the bar, like the system icons
        // on macOS clicking the icon opens its menu (with no menu no notice arrives): the menu carries what is running, live
        _tray.Menu = _menu;
        _menu.NeedsUpdate += (_, _) => RefreshMenu();
        _menu.Opening += (_, _) =>
        {
            RefreshMenu();
            _menuTimer.Start(); // open: the numbers update on their own
        };
        _menu.Closed += (_, _) => _menuTimer.Stop();
        _tray.Clicked += (_, _) => TogglePanel();
        RefreshMenu();
        _menuTimer.Tick += (_, _) => RefreshMenu();
        TrayIcon.SetIcons(app, new TrayIcons { _tray });

        // clicking the Dock icon with the window closed opens it again
        if (app.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) Dispatcher.UIThread.Post(ShowMain);
            };
    }

    public static MenuBar? Current { get; private set; }

    /// <summary>In tools and tests (data in another folder) nothing is put in the menu bar.</summary>
    public static MenuBar? Install(Application app, AppServices svc, MainWindow main)
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 }) return null;
        try
        {
            Current = new MenuBar(app, svc, main);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        return Current;
    }

    /// <summary>"Keep running in the menu bar" was enabled or disabled in Settings.</summary>
    public void Refresh() => _tray.IsVisible = _svc.Settings.KeepInMenuBar;

    public void ShowMain()
    {
        _panel?.Hide();
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    /// <summary>Quits Trading Hub for good (it stops the strategies; the ones that were running resume on opening if Settings says so).</summary>
    public static void Quit()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    /// <summary>A row of the menu: stable key, text, whether it can be clicked and what it does (null = separator).</summary>
    private sealed record MenuLine(string Key, string? Text, bool Enabled = false, Action? Click = null);

    private static readonly System.Globalization.CultureInfo Fmt = System.Globalization.CultureInfo.GetCultureInfo("en-US");

    private static string Money(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", Fmt);

    private List<MenuLine> MenuLines()
    {
        var lines = new List<MenuLine> { new("hdr", $"Trading Hub · {_main.ConnectionSummary}") };
        int sep = 0;
        void Sep() => lines.Add(new MenuLine($"sep{++sep}", null));

        Sep();
        var running = _svc.Live.Entries(includeStopped: false).Where(e => e.Running).OrderBy(e => e.Locked).ThenBy(e => e.Info.Strategy).ToList();
        lines.Add(new MenuLine("live", $"Live strategies ({running.Count})"));
        foreach (var e in running)
        {
            var r = e.Runner!;
            decimal pos = e.Ledger.Position;
            var plan = _svc.Live.PlanOf(e);
            string side = pos == 0 ? "flat" : $"{(pos > 0 ? "long" : "short")} {Math.Abs(pos).ToString("0.####", Fmt)} {e.Ledger.OpenSymbol ?? plan.Symbol}";
            string state = e.Paused ? " · paused" : r.DailyLimitActive ? " · halted for today" : "";
            string each = plan.Units <= 0 ? "does not open" : $"{plan.Text} per order{(plan.Micro ? " (micro)" : "")}";
            lines.Add(new MenuLine($"s:{e.Key}", $"   {e.Info.Strategy} · {r.Data.Instrument.Symbol} · {each} · {side} · open {Money(e.OpenPnL)} · today {Money(r.TodayPnL)}{state}", true, ShowPanel));
        }

        if (running.Count == 0) lines.Add(new MenuLine("live-none", "   None running"));

        Sep();
        var positions = new List<MenuLine>();
        foreach (var a in _svc.AccountCenter.Accounts())
        {
            try
            {
                foreach (var p in _svc.AccountCenter.Snapshot(a).Positions.Where(p => p.Net != 0))
                    positions.Add(new MenuLine($"p:{a.Id}:{p.Symbol}", $"   {p.Symbol} · {a.Name} · {(p.Net > 0 ? "long" : "short")} {Math.Abs(p.Net).ToString("0.####", Fmt)} · {Money(p.OpenPnl)}", true, ShowPanel));
            }
            catch
            {
                // an account that does not answer does not hold up the menu
            }
        }

        lines.Add(new MenuLine("pos", $"Open positions ({positions.Count})"));
        lines.AddRange(positions);
        if (positions.Count == 0) lines.Add(new MenuLine("pos-none", "   No positions"));


        Sep();
        lines.Add(new MenuLine("panel", "Show the live panel…", true, ShowPanel));
        lines.Add(new MenuLine("open", "Open Trading Hub", true, ShowMain));
        lines.Add(new MenuLine("quit", "Quit Trading Hub", true, Quit));
        return lines;
    }

    /// <summary>Brings the menu up to date: if the rows changed it rebuilds it; if not, it only changes the texts.</summary>
    private void RefreshMenu()
    {
        List<MenuLine> lines;
        try
        {
            lines = MenuLines();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return;
        }

        if (lines.Select(l => l.Key).SequenceEqual(_menuItems.Select(m => m.Key)))
        {
            for (int i = 0; i < lines.Count; i++)
                if (_menuItems[i].Item is NativeMenuItem mi && mi.Header != lines[i].Text) mi.Header = lines[i].Text;
            return;
        }

        _menu.Items.Clear();
        _menuItems.Clear();
        foreach (var line in lines)
        {
            NativeMenuItemBase item;
            if (line.Text == null) item = new NativeMenuItemSeparator();
            else
            {
                var mi = new NativeMenuItem { Header = line.Text, IsEnabled = line.Enabled };
                if (line.Click is { } click) mi.Click += (_, _) => Dispatcher.UIThread.Post(click);
                item = mi;
            }

            _menu.Items.Add(item);
            _menuItems.Add((line.Key, item));
        }
    }

    /// <summary>The full panel (more detail than the menu), below the bar.</summary>
    public void ShowPanel()
    {
        _panel ??= CreatePanel();
        _panel.Refresh();
        Place(_panel);
        _panel.Show();
        _panel.Activate();
    }

    private void TogglePanel()
    {
        // clicking the icon also takes the focus away from the panel (and closes it): it does not reopen it right away
        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
            return;
        }

        if ((DateTime.UtcNow - _panelHiddenAt).TotalMilliseconds < 350) return;
        _panel ??= CreatePanel();
        _panel.Refresh();
        Place(_panel);
        _panel.Show();
        _panel.Activate();
    }

    private MenuBarPanel CreatePanel()
    {
        var p = new MenuBarPanel(_svc, _main, ShowMain);
        p.Deactivated += (_, _) =>
        {
            if (!p.IsVisible) return;
            p.Hide();
            _panelHiddenAt = DateTime.UtcNow;
        };
        return p;
    }

    /// <summary>Below the icon (where the mouse is when clicking it), without going off screen.</summary>
    private void Place(Window panel)
    {
        var screens = _main.Screens;
        var primary = screens.Primary ?? screens.All.FirstOrDefault();
        if (primary == null) return;
        double scale = primary.Scaling;
        PixelPoint? mouse = null;
        if (MouseLocation() is { } m)
        {
            // macOS measures from the bottom left of the main screen, in points
            double heightPoints = primary.Bounds.Height / scale;
            mouse = new PixelPoint((int)(m.X * scale), (int)((heightPoints - m.Y) * scale));
        }

        var screen = mouse is { } mp ? screens.All.FirstOrDefault(s => s.Bounds.Contains(mp)) ?? primary : primary;
        var area = screen.WorkingArea;
        int width = (int)(panel.Width * screen.Scaling);
        int x = mouse is { } p0 ? p0.X - width / 2 : area.Right - width - (int)(12 * screen.Scaling);
        x = Math.Clamp(x, area.X + (int)(8 * screen.Scaling), Math.Max(area.X, area.Right - width - (int)(8 * screen.Scaling)));
        panel.Position = new PixelPoint(x, area.Y + (int)(6 * screen.Scaling));
    }

    // ===================================================================== macOS

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGPoint SendPoint(IntPtr receiver, IntPtr selector);

    /// <summary>Where the mouse is ([NSEvent mouseLocation]: in points, from the bottom left).</summary>
    private static CGPoint? MouseLocation()
    {
        try
        {
            var cls = objc_getClass("NSEvent");
            return cls == IntPtr.Zero ? null : SendPoint(cls, sel_registerName("mouseLocation"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The bar icon: the Trading Hub snake in black on transparent (macOS adapts it to the theme).</summary>
    internal static Bitmap Glyph()
    {
        const int size = 44; // 22 points on Retina screens
        using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.Transparent);
        // the body: an S rising from the tail (bottom left) up to the head (top right)
        using var body = new SKPath();
        body.MoveTo(7, 38);
        body.CubicTo(17, 43, 31, 39, 29, 30);
        body.CubicTo(27.5f, 23, 14, 26, 15, 18.5f);
        body.CubicTo(16, 12, 23, 9.5f, 29, 9.5f);
        using var stroke = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5.4f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        c.DrawPath(body, stroke);
        using var fill = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Fill };
        c.DrawOval(new SKRect(27, 4.5f, 38.5f, 13.5f), fill); // the head
        using var clear = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true };
        c.DrawCircle(34.2f, 7.8f, 1.5f, clear); // the eye
        // the forked tongue
        using var tongue = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round };
        c.DrawLine(38, 9.5f, 41.5f, 10.5f, tongue);
        c.DrawLine(41.5f, 10.5f, 43.2f, 9, tongue);
        c.DrawLine(41.5f, 10.5f, 43, 12.4f, tongue);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        return new Bitmap(stream);
    }
}
