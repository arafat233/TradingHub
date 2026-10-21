// "panel" scene: Trading's right panel with Chart Trader, DOM and tape all at once (each with its own height and
// scroll), the DOM buttons, the three sections in separate windows and the chart flush against the panel.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using TradingHub.App.Controls;
using TradingHub.App.Windows;

internal static class PanelScene
{
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "OK " : "FAIL")}  {what}");
        if (!ok) Failures.Add(what);
    }

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        window.Navigate("trading");
        var tv = window.Trading;
        foreach (var key in new[] { "ticket", "dom", "tape" }) tv.SetPanel(key, true);
        pump(2);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_tres.png"));

        Control? Section(string title) => tv.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == title.ToUpperInvariant() && t.IsEffectivelyVisible)
            ?.GetVisualAncestors().OfType<DockPanel>().FirstOrDefault(d => d.Parent is Border or Grid);
        var ticket = Section("Chart Trader");
        var dom = tv.GetVisualDescendants().OfType<DomView>().FirstOrDefault();
        var tape = tv.GetVisualDescendants().OfType<TapeView>().FirstOrDefault();
        Console.WriteLine($"Heights: ticket {ticket?.Bounds.Height:0} · DOM {dom?.Bounds.Height:0} · tape {tape?.Bounds.Height:0}");
        Check(ticket is { Bounds.Height: > 80 } && dom is { Bounds.Height: > 60, IsEffectivelyVisible: true } && tape is { Bounds.Height: > 60, IsEffectivelyVisible: true },
            "with all three enabled all three are visible (none covers another)");

        // the Chart Trader scrolls
        var sv = ticket?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        Check(sv != null && sv.Extent.Height > sv.Viewport.Height + 20, $"the Chart Trader has more content than height (content {sv?.Extent.Height:0}, in view {sv?.Viewport.Height:0})");
        if (sv != null)
        {
            sv.Offset = new Vector(0, 10_000);
            pump(0.5);
            Check(sv.Offset.Y > 0 && sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 1, $"the Chart Trader scrolls to the bottom ({sv.Offset.Y:0} px of {sv.Extent.Height - sv.Viewport.Height:0})");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_ticket_below.png"));
            sv.Offset = new Vector(0, 0);
        }

        // the tape scrolls with the wheel and goes back to the latest with a click at the top
        if (tape?.Tape is { } model)
        {
            pump(1);
            int count = model.Data.Trades.Count;
            var center = tape.TranslatePoint(new Point(tape.Bounds.Width / 2, tape.Bounds.Height / 2), window) ?? default;
            window.MouseWheel(center, new Vector(0, -3));
            pump(0.5);
            Check(model.Anchor != null && model.Anchor < count - 1, $"the tape scrolls towards older trades ({count} trades, {model.Anchor} at the top)");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_tape_scrolled.png"));
            var top = tape.TranslatePoint(new Point(tape.Bounds.Width / 2, 8), window) ?? default;
            window.MouseDown(top, MouseButton.Left);
            window.MouseUp(top, MouseButton.Left);
            pump(0.3);
            Check(model.Anchor == null, "a click on the header returns to the latest");
        }
        else Check(false, "the tape has data");

        // DOM buttons
        var domButtons = tv.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text is "C. Bid" or "V. Ask" or "Close")).ToList();
        Check(domButtons.Count >= 3, $"the DOM has execution buttons ({domButtons.Count})");

        // the chart flush against the panel: no gap between the chart's edge and the panel's
        var frame = tv.ActiveTab?.Frame;
        var side = dom?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.BorderThickness.Left == 1 && b.Child is Grid);
        if (frame != null && side != null)
        {
            double frameRight = (frame.TranslatePoint(new Point(frame.Bounds.Width, 0), window) ?? default).X;
            double sideLeft = (side.TranslatePoint(new Point(0, 0), window) ?? default).X;
            Check(Math.Abs(sideLeft - frameRight) < 1.5, $"the chart sits flush against the panel (the chart ends at {frameRight:0}, the panel starts at {sideLeft:0})");
        }
        else Check(false, "it finds the chart and the panel");

        // the three sections in separate windows
        foreach (var key in new[] { "ticket", "tape", "dom" })
        {
            tv.DetachSection(key);
            pump(1);
            var w = tv.PanelWindow(key);
            Check(w is { IsVisible: true }, $"{key} in a separate window ({w?.Title})");
            w?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, $"panel_window_{key}.png"));
        }

        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_all_out.png"));
        foreach (var key in new[] { "ticket", "tape", "dom" })
        {
            tv.PanelWindow(key)?.Close();
            pump(0.5);
        }

        pump(1);
        dom = tv.GetVisualDescendants().OfType<DomView>().FirstOrDefault();
        tape = tv.GetVisualDescendants().OfType<TapeView>().FirstOrDefault();
        Check(tv.PanelWindow("ticket") == null && dom is { IsEffectivelyVisible: true } && tape is { IsEffectivelyVisible: true }, "closing the windows returns them to the panel");
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "panel_back.png"));

        // the chart's toolbar: chart type in a picker, favorite periods in a row and the rest in their picker
        var toolbar = tv.Toolbar;
        var settings = TradingHub.App.App.Services.Settings;
        settings.FavoritePeriods = new() { "M1", "M5", "M15", "H1", "H4", "Daily" };
        toolbar.Sync();
        pump(0.5);
        var barTexts = toolbar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Check(barTexts.Count(t => t is "Candles" or "Bars" or "Line" or "Heikin Ashi" or "Footprint") == 1 && !barTexts.Contains("30m") && barTexts.Contains("15m"),
            $"the bar shows only the favourite periods and the chart type sits in a picker ({string.Join(" | ", barTexts)})");
        var menuBtn = (Button)typeof(TradingHub.App.Views.ChartToolbar).GetField("_periodMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(toolbar)!;
        var fly = (Flyout)typeof(TradingHub.App.Views.ChartToolbar).GetField("_periodFlyout", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(toolbar)!;
        fly.ShowAt(menuBtn);
        pump(1);
        var menu = fly.Content as Control;
        TopLevel.GetTopLevel(menu)?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "toolbar_periods.png"));
        // right click on 30m puts it on the toolbar
        var pick30 = menu?.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "30m");
        var popupTop = TopLevel.GetTopLevel(menu);
        if (pick30 != null && popupTop != null)
        {
            var at = pick30.TranslatePoint(new Point(pick30.Bounds.Width / 2, pick30.Bounds.Height / 2), popupTop) ?? default;
            popupTop.MouseDown(at, MouseButton.Right);
            popupTop.MouseUp(at, MouseButton.Right);
        }
        pump(0.5);
        fly.Hide();
        pump(0.3);
        barTexts = toolbar.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Check(settings.FavoritePeriods.Contains("M30") && barTexts.Contains("30m"), "a right click adds the period to the bar");
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "chart_bar.png"));
        settings.FavoritePeriods.Remove("M30");
        toolbar.Sync();

        // the confirmation popup (Account -> Reset the simulated account): roomy, with Cancel and the action
        window.Navigate("accounts");
        pump(1);
        var restart = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == "Reset the simulated account");
        if (restart?.Flyout is Flyout confirm)
        {
            confirm.ShowAt(restart);
            pump(0.8);
            var content = confirm.Content as Control;
            var labels = content?.GetVisualDescendants().OfType<Button>().Select(b => b.Content as string).ToList() ?? new();
            Check(labels.Contains("Cancel") && labels.Contains("Restart") && content is StackPanel { Margin.Left: >= 12 }, $"the popup has padding, “Cancel” and the action ({string.Join(", ", labels)})");
            TopLevel.GetTopLevel(content)?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "cartel_confirmar.png"));
            confirm.Hide();
        }
        else Check(false, "it finds the reset simulated account button");

        Console.WriteLine(Failures.Count == 0 ? "PANEL: all OK" : $"PANEL: {Failures.Count} failure(s)");
        return Failures.Count;
    }
}
