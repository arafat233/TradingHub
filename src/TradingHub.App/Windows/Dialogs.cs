using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;

namespace TradingHub.App.Windows;

/// <summary>Base for the modal dialogs with Trading Hub's styling.</summary>
internal abstract class TradingHubDialog<T> : Window
{
    protected TradingHubDialog(string title, double width)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Ui.Bg2;
        ShowInTaskbar = false;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(default(T));
        };
    }

    protected Control Shell(Control body, params Button[] buttons)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) bar.Children.Add(b);
        return new StackPanel { Margin = new Thickness(20, 18, 20, 16), Spacing = 18, Children = { body, bar } };
    }
}

/// <summary>Confirmation question (Yes/No).</summary>
internal sealed class ConfirmDialog : TradingHubDialog<bool>
{
    private ConfirmDialog(string title, string message, string okText, bool danger) : base(title, 420)
    {
        var icon = Ui.Icon(danger ? MaterialIconKind.AlertOutline : MaterialIconKind.HelpCircleOutline, 26, danger ? Ui.Warn : Ui.Accent);
        icon.VerticalAlignment = VerticalAlignment.Top;
        var text = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h2" } },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } },
            },
        };
        var body = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        icon.Margin = new Thickness(0, 2, 14, 0);
        body.Children.Add(icon);
        body.Children.Add(text);
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = okText, IsDefault = true }.Classes(danger ? "danger" : "primary");
        ok.Click += (_, _) => Close(true);
        Content = Shell(body, cancel, ok);
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, string okText = "OK", bool danger = false) =>
        new ConfirmDialog(title, message, okText, danger).ShowDialog<bool>(owner);
}

/// <summary>Asks for a text (name of a strategy, profile, etc.).</summary>
internal sealed class PromptDialog : TradingHubDialog<string?>
{
    private PromptDialog(string title, string label, string initial, string okText) : base(title, 400)
    {
        var box = new TextBox { Text = initial };
        box.AttachedToVisualTree += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var ok = new Button { Content = okText, IsDefault = true }.Classes("primary");
        ok.Click += (_, _) => Close(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
        Content = Shell(new StackPanel { Spacing = 10, Children = { new TextBlock { Text = title, Classes = { "h2" } }, Ui.Field(label, box) } }, cancel, ok);
    }

    public static Task<string?> ShowAsync(Window owner, string title, string label, string initial = "", string okText = "OK") =>
        new PromptDialog(title, label, initial, okText).ShowDialog<string?>(owner);
}

/// <summary>Asks for a longer text (a journal note): several lines, Ctrl+Enter saves.</summary>
internal sealed class NoteDialog : TradingHubDialog<string?>
{
    private NoteDialog(string title, string label) : base(title, 520)
    {
        var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130, MaxHeight = 320 };
        box.AttachedToVisualTree += (_, _) => box.Focus();
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var ok = new Button { Content = "Save note" }.Classes("primary");
        ok.Click += (_, _) => Close(box.Text?.Trim());
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) Close(box.Text?.Trim());
        };
        Content = Shell(new StackPanel { Spacing = 8, Children = { new TextBlock { Text = label, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap }, box } }, cancel, ok);
    }

    public static Task<string?> ShowAsync(Window owner, string title, string label) => new NoteDialog(title, label).ShowDialog<string?>(owner);
}

/// <summary>
/// Position size from the money at risk: contracts = risk / (ticks between entry and stop x value of one tick).
/// The tick value is the instrument's point value x tick size.
/// </summary>
internal sealed class RiskDialog : TradingHubDialog<object?>
{
    private RiskDialog(TradingHub.Market.Instrument inst, decimal entry, decimal risk, Action<decimal> saveRisk) : base($"Risk: {inst.Symbol}", 440)
    {
        var inv = System.Globalization.CultureInfo.CurrentCulture;
        decimal tick = inst.TickSize > 0 ? inst.TickSize : 1m, tickValue = inst.PointValue * tick;
        var riskBox = new TextBox { Text = risk.ToString("0.##", inv), MinWidth = 110 };
        var entryBox = new TextBox { Text = entry.ToString("0.#####", inv), MinWidth = 110 };
        var stopBox = new TextBox { Text = (entry - 10 * tick).ToString("0.#####", inv), MinWidth = 110 };
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.Medium };
        var note = new TextBlock { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap, Text = $"One tick = {tick} = {tickValue:0.####} per contract. Long if the stop is below the entry, short if above." };

        void Update()
        {
            if (!decimal.TryParse(riskBox.Text, System.Globalization.NumberStyles.Number, inv, out var r) || r <= 0
                || !decimal.TryParse(entryBox.Text, System.Globalization.NumberStyles.Number, inv, out var e)
                || !decimal.TryParse(stopBox.Text, System.Globalization.NumberStyles.Number, inv, out var st) || st == e)
            {
                result.Text = "Enter the money at risk, an entry and a stop that is different from it.";
                return;
            }

            decimal ticks = Math.Abs(e - st) / tick, perContract = ticks * tickValue;
            decimal step = inst.VolumeStep > 0 ? inst.VolumeStep : 1m;
            decimal qty = Math.Floor(r / perContract / step) * step;
            result.Text = qty < step
                ? $"{ticks:0.#} ticks to the stop = {perContract:N2} per contract: more than the {r:N2} you allow. Widen the risk or tighten the stop."
                : $"{(st < e ? "Long" : "Short")} {qty:0.##} contracts · {ticks:0.#} ticks to the stop · {qty * perContract:N2} at risk ({perContract:N2} per contract)";
            saveRisk(r);
        }

        riskBox.TextChanged += (_, _) => Update();
        entryBox.TextChanged += (_, _) => Update();
        stopBox.TextChanged += (_, _) => Update();
        Update();
        Control Row(string label, TextBox box) => new DockPanel { Children = { new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center }, box } };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close(null);
        Content = Shell(new StackPanel { Spacing = 10, Children = { Row("Money at risk", riskBox), Row("Entry", entryBox), Row("Stop", stopBox), result, note } }, close);
    }

    public static Task ShowAsync(Window owner, TradingHub.Market.Instrument inst, decimal entry, decimal risk, Action<decimal> saveRisk) =>
        new RiskDialog(inst, entry, risk, saveRisk).ShowDialog<object?>(owner);
}

/// <summary>Copy the active chart's account onto other accounts of the same instrument (position and its stops/targets, scaled by a ratio).</summary>
internal sealed class CopierDialog : TradingHubDialog<object?>
{
    private CopierDialog(Services.AppServices svc, TradingHub.Data.InstrumentData data, string leaderId) : base($"Trade copier: {data.Instrument.Symbol}", 520)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var options = svc.AccountsFor(data).ToList();
        var leaderName = options.FirstOrDefault(o => o.Id == leaderId)?.Name ?? leaderId;
        var rows = new List<(Services.AccountOption Option, CheckBox On, TextBox Ratio)>();
        var list = new StackPanel { Spacing = 6 };
        foreach (var o in options.Where(o => o.Id != leaderId))
        {
            var on = new CheckBox { Content = o.Name, Width = 300 };
            var ratio = new TextBox { Text = "1", Width = 70 };
            rows.Add((o, on, ratio));
            list.Children.Add(new DockPanel { Children = { on, new TextBlock { Text = "× ", VerticalAlignment = VerticalAlignment.Center }, ratio } });
        }

        var max = new TextBox { Text = "10", Width = 70 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "muted", "small" } };
        var action = new Button { Content = "Start copying" };
        void Refresh()
        {
            bool running = svc.Copier?.IsRunning == true;
            action.Content = running ? "Stop copying" : "Start copying";
            foreach (var r in rows) { r.On.IsEnabled = !running; r.Ratio.IsEnabled = !running; }
            max.IsEnabled = !running;
        }

        action.Click += async (_, _) =>
        {
            if (svc.Copier?.IsRunning == true) { svc.Copier.Stop(); Refresh(); return; }
            var chosen = new List<(TradingHub.Trading.ITradingAccount, decimal)>();
            foreach (var r in rows.Where(r => r.On.IsChecked == true))
            {
                if (!decimal.TryParse(r.Ratio.Text, System.Globalization.NumberStyles.Number, inv, out var ratio) || ratio <= 0) { status.Text = $"Ratio for {r.Option.Name} must be above 0 (use a dot for decimals)."; return; }
                chosen.Add((svc.AccountFor(data, r.Option.Id), ratio));
            }

            if (chosen.Count == 0) { status.Text = "Tick at least one account to copy onto."; return; }
            if (!decimal.TryParse(max.Text, System.Globalization.NumberStyles.Number, inv, out var maxPos) || maxPos < 0) { status.Text = "Max position must be 0 (no limit) or more."; return; }
            if (rows.Any(r => r.On.IsChecked == true && r.Option.IsReal)
                && !await ConfirmDialog.ShowAsync(this, "Copy onto REAL accounts", "Orders will be sent to real accounts with real money every time the leader trades. Start?", "Start", danger: true)) return;
            var copier = new TradingHub.Trading.TradeCopier(svc.AccountFor(data, leaderId), chosen, maxPos);
            copier.Message += m => Avalonia.Threading.Dispatcher.UIThread.Post(() => { status.Text = m; Services.Notifier.Warn(svc.Settings, m); Refresh(); });
            svc.Copier?.Dispose();
            svc.Copier = copier;
            copier.Start();
            Refresh();
        };
        Refresh();
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close(null);
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"Leader: {leaderName} (the account selected on the active chart)", FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap },
                rows.Count == 0 ? new TextBlock { Text = "No other account is available for this instrument.", Classes = { "muted" } } : list,
                new DockPanel { Children = { new TextBlock { Text = "Max position per follower (0 = no limit)", Width = 300, VerticalAlignment = VerticalAlignment.Center }, max } },
                new TextBlock { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap, Text = "Followers follow the leader's position times the ratio, and get copies of its stops and targets. It stops by itself on any follower error or if a follower would exceed the max position. Stopping leaves the follower positions open." },
                status,
                action,
            },
        };
        Content = Shell(body, close);
    }

    public static Task ShowAsync(Window owner, Services.AppServices svc, TradingHub.Data.InstrumentData data, string leaderId) =>
        new CopierDialog(svc, data, leaderId).ShowDialog<object?>(owner);
}

/// <summary>Named layouts: everything open on the Trading page (charts, splits, floating windows, indicators), saved and restored by name.</summary>
internal sealed class LayoutsDialog : TradingHubDialog<object?>
{
    private LayoutsDialog(Views.TradingView trading, Action<string> status) : base("Layouts", 420)
    {
        var list = new ListBox { Height = 200 };
        void Fill() { list.ItemsSource = Services.Workspace.LayoutNames(); }
        Fill();
        var name = new TextBox { Watermark = "Name for the current layout", MinWidth = 220 };
        var save = new Button { Content = "Save current" };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) { status("Type a name for the layout."); return; }
            try { Services.Workspace.SaveLayout(trading, name.Text); status($"Layout “{name.Text.Trim()}” saved"); Fill(); }
            catch (Exception ex) { status("Could not save the layout: " + ex.Message); }
        };
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is string s) name.Text = s; };
        var load = new Button { Content = "Load" };
        load.Click += async (_, _) =>
        {
            if (list.SelectedItem is not string s) { status("Choose a layout to load."); return; }
            if (!await ConfirmDialog.ShowAsync(this, "Load layout", $"The charts that are open now will be closed and replaced by “{s}”.", "Load")) return;
            Close(null);
            status(await Services.Workspace.LoadLayoutAsync(trading, s) ? $"Layout “{s}” loaded" : $"Layout “{s}” is missing or empty");
        };
        var delete = new Button { Content = "Delete" };
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is not string s) return;
            if (!await ConfirmDialog.ShowAsync(this, "Delete layout", $"“{s}” will be deleted.", "Delete", danger: true)) return;
            Services.Workspace.DeleteLayout(s);
            Fill();
        };
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close(null);
        var body = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                list,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { load, delete } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { name, save } },
            },
        };
        Content = Shell(body, close);
    }

    public static Task ShowAsync(Window owner, Views.TradingView trading, Action<string> status) => new LayoutsDialog(trading, status).ShowDialog<object?>(owner);
}
