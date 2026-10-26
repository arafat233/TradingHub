using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Material.Icons;
using TradingHub.App.Services;
using TradingHub.App.Windows;

namespace TradingHub.App.Views;

/// <summary>General platform settings.</summary>
internal sealed class SettingsView : UserControl, IPage
{
    private readonly AppServices _svc;
    private readonly Action<string> _status;

    public SettingsView(AppServices svc, Action<string> status)
    {
        _svc = svc;
        _status = status;
        // the margin goes in the content: with Padding on the ScrollViewer the end was covered and could not be scrolled to
        var content = Build();
        content.Margin = new Thickness(28, 24, 28, 32);
        Content = new ScrollViewer { Content = content };
    }

    public void OnShown()
    {
    }

    private int _tick;

    public void OnTimer()
    {
        if (IsVisible && ++_tick % 8 == 0) RefreshSleep(); // ~2 s
    }

    private Control Build()
    {
        var s = _svc.Settings;

        // time zone: the usual ones first
        var tz = new ComboBox { MinWidth = 320 };
        var common = new[] { "America/Argentina/Buenos_Aires", "America/New_York", "America/Chicago", "Europe/London", "Europe/Madrid", "UTC" };
        tz.Items.Add(new ComboBoxItem { Content = $"This Mac's time ({TimeZoneInfo.Local.Id})", Tag = "" });
        foreach (var id in common)
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z)) tz.Items.Add(new ComboBoxItem { Content = Label(z), Tag = z.Id });
        tz.Items.Add(new Separator());
        foreach (var z in TimeZoneInfo.GetSystemTimeZones().Where(z => !common.Contains(z.Id)).OrderBy(z => z.BaseUtcOffset).ThenBy(z => z.Id))
            tz.Items.Add(new ComboBoxItem { Content = Label(z), Tag = z.Id });
        tz.SelectedItem = tz.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag! == (s.TimeZoneId ?? "")) ?? tz.Items[0];
        tz.SelectionChanged += (_, _) =>
        {
            if (tz.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            s.TimeZoneId = id == "" ? null : id;
            s.Save();
            if (TopLevel.GetTopLevel(this) is MainWindow w)
            {
                foreach (var t in w.Trading.Tabs)
                {
                    if (t.Model == null) continue;
                    t.Model.Settings.TimeZoneId = s.TimeZoneId;
                    t.Model.Invalidate();
                }
            }

            _status("Time zone updated");
        };

        var confirm = Switch(s.ConfirmRealOrders, v => s.ConfirmRealOrders = v);
        var sounds = Switch(s.SoundAlerts, v => s.SoundAlerts = v);
        var spoken = Switch(s.SpokenAlerts, v => s.SpokenAlerts = v);
        var researchOn = Switch(s.ResearchNightly, v => s.ResearchNightly = v);
        var researchTime = new TextBox { Text = s.ResearchTime, MinWidth = 90, Watermark = "03:00" };
        researchTime.LostFocus += (_, _) =>
        {
            s.ResearchTime = researchTime.Text?.Trim() ?? "03:00";
            s.Save();
        };
        var hypothesesFile = Ui.Button("Open", MaterialIconKind.FileEditOutline, () => Shell.Open(ResearchExtra.EnsureHypothesesFile()));
        var briefingOn = Switch(s.BriefingsOn, v => s.BriefingsOn = v);
        var briefingTimes = new TextBox { Text = s.BriefingTimes, MinWidth = 150, Watermark = "08:00,13:00" };
        briefingTimes.LostFocus += (_, _) =>
        {
            s.BriefingTimes = briefingTimes.Text?.Trim() ?? "";
            s.Save();
        };
        var eventsFile = Ui.Button("Open", MaterialIconKind.FileEditOutline, () => Shell.Open(TradingHub.Indicators.Native.EventCalendar.EnsureFile()));
        var record = Switch(s.RecordLiveData, v =>
        {
            s.RecordLiveData = v;
            _svc.Hub.RecordLive = v;
        });

        var recordDepth = Switch(s.RecordDepth, v =>
        {
            s.RecordDepth = v;
            _svc.Hub.RecordDepth = v;
        });

        var recapComment = Switch(s.RecapComment, v => s.RecapComment = v);
        var alertSnapshots = Switch(s.AlertSnapshots, v => s.AlertSnapshots = v);
        var backups = BuildBackups();

        var phone = BuildPhoneAlerts();
        var commission = new TextBox { Text = s.CommissionPerRoundTrip.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture), MinWidth = 90 };
        commission.LostFocus += (_, _) =>
        {
            if (decimal.TryParse(commission.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var v) && v >= 0)
            {
                s.CommissionPerRoundTrip = v;
                s.Save();
            }
        };

        var appearance = new Ui.Segmented<string>(new[] { (AppTheme.Dark, "Dark", (string?)"Black"), (AppTheme.Light, "Light", "White") });
        appearance.Select(AppTheme.IsLight ? AppTheme.Light : AppTheme.Dark);
        appearance.SelectionChanged += mode =>
        {
            s.ThemeMode = mode;
            s.Save();
            AppTheme.Apply(mode);
        };
        var alertToxic = Switch(s.AlertToxicity, x => s.AlertToxicity = x);
        var alertWalls = Switch(s.AlertWalls, x => s.AlertWalls = x);
        var alertWallUsd = new NumericUpDown { Value = s.AlertWallUsd, Minimum = 10_000, Maximum = 1_000_000_000, Increment = 100_000, FormatString = "#,##0", Width = 160 };
        alertWallUsd.ValueChanged += (_, _) => { s.AlertWallUsd = alertWallUsd.Value ?? s.AlertWallUsd; s.Save(); };
        var alertOptions = Switch(s.AlertOptions, x => s.AlertOptions = x);
        var alertLiq = Switch(s.AlertLiquidations, x => s.AlertLiquidations = x);
        var alertLiqUsd = new NumericUpDown { Value = s.AlertLiquidationUsd, Minimum = 10_000, Maximum = 1_000_000_000, Increment = 100_000, FormatString = "#,##0", Width = 160 };
        alertLiqUsd.ValueChanged += (_, _) => { s.AlertLiquidationUsd = alertLiqUsd.Value ?? s.AlertLiquidationUsd; s.Save(); };
        var alertSetup = Switch(s.AlertSetup, x => s.AlertSetup = x);
        var insightAlerts = Section("Insight alerts", MaterialIconKind.BellRingOutline,
            Setting("Toxic tape", "Tells you when the tape turns toxic (one side taking liquidity unusually one-sidedly, when prices tend to jump). Once when it starts, not while it lasts.", alertToxic),
            Setting("Big resting order pulled or eaten", "A wall worth at least the amount below was cancelled, vanished within seconds, or was traded through.", Ui.V(6, alertWalls, alertWallUsd)),
            Setting("Price at an options level", "Bitcoin and Ethereum: the price is within 0.05% of the call wall, put wall, max pain or gamma flip.", alertOptions),
            Setting("Big liquidation cluster", "Bybit: a cluster of liquidations worth at least the amount below formed in the last hours.", Ui.V(6, alertLiq, alertLiqUsd)),
            Setting("Setup called", "The Insights panel says BUY SETUP or SELL SETUP. Off by default: the panel is context, and testing on real tape found no edge in it.", alertSetup),
            Setting("Where they go", "The same place as every alert: the status bar, the Today log, Telegram and speech if those are on, and the alert sound. Each one waits out a cooldown before it can fire again.", new TextBlock { Text = "uses the settings above", Classes = { "muted" } }));
        var general = Section("General", MaterialIconKind.TuneVariant,
            Setting("Appearance", "Black or white across the whole platform (also with the sun/moon button at the top). Each chart's own colors are kept.", appearance),
            Setting("Charts' time zone", "The times on the time axis, the tape and the sessions.", tz),
            Setting("Alert sounds", "Price alerts, Big Trades and indicator notices.", sounds),
            Setting("Spoken alerts", "Reads each alert out loud with Windows' own voice (for example \"GC.F. Big trade level touched: sell 182 at 4070.7\"). Nothing leaves this PC.", spoken),
            Setting("Pre-session briefing", "At the times below it puts together the next events, the nearest untouched levels around the price of every open instrument, the level scoreboard and the paper trader's results. It is saved in Documents / Trading Hub / Briefings and also sent to Telegram and read aloud if those are on. The app has to be open at that time.", Ui.V(6, briefingOn, briefingTimes)),
            Setting("Nightly research", "At the time below it mines the day's new big orders, reruns the edge finder and your locked hypotheses with fixed rules, saves the report in Documents / Trading Hub / Research, and tells you (Telegram, spoken, Today) only if something held up. The app has to be open at that time.", Ui.V(6, researchOn, researchTime)),
            Setting("Hypotheses", "Write down what you think will work (one line each). The first time a line is tested it is locked with the time and only later orders count, so the test cannot be tuned on data you have already seen.", hypothesesFile),
            Setting("Economic events", "Your own list of CPI, FOMC, NFP... (one line each, time in UTC). Level alerts are muted around them and the Event markers indicator draws them.", eventsFile)
            );

        var trading = Section("Trading", MaterialIconKind.SwapVertical,
            Setting("Confirm real orders", "It asks for confirmation before sending orders to real accounts (on top of what each connection is configured with).", confirm),
            Setting("Commission per round trip", "Money paid to open and close one contract. The research tools (cost map, rule lab) turn it into ticks and count it against every trade.", commission),
            Setting("Simulated account", $"Starting balance {_svc.Portfolio.BeginBalance:N2}. The simulator's orders fill using the book queue and the real tape.", new TextBlock()));

        var data = Section("Data", MaterialIconKind.DatabaseOutline,
            Setting("Record incoming live data", "It stores every incoming trade so charts reopen instantly, and for replay and backtests.", record),
            Setting("Record the order book", "Also stores the book (20 levels per side, 4 times a second; roughly 20-40 MB a day per instrument) so the heatmap can be replayed and strategies can be backtested on it. It applies to instruments opened from now on.", recordDepth),
            Setting("Claude comment in the daily recap", "Adds a short \"what stood out\" paragraph, written by Claude Code with your session, to yesterday's recap (Documents / Trading Hub / Recaps). Off by default: it uses a little of your Claude allowance once a day.", recapComment),
            Setting("Picture with every alert", "Each indicator alert also saves a picture of the chart and adds it to today's journal (Documents / Trading Hub / Journal), so you can review the setups later.", alertSnapshots),
            backups,
            Setting("Data folder", AppPaths.Root, Ui.Button("Open", MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.Root))),
            Setting("Own indicators (C#)", AppPaths.UserIndicators, Ui.Button("Open", MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.UserIndicators))),
            Setting("Strategies (C#)", AppPaths.Strategies, Ui.Button("Open", MaterialIconKind.FolderOpenOutline, () => Shell.OpenFolder(AppPaths.Strategies))));

        var sleep = BuildSleep();
        var liveStart = BuildLiveStart();
        var hotkeys = BuildHotkeys();

        var logo = new Image { Source = AppIcon.Bitmap, Width = 56, Height = 56 };
        var about = Ui.Card(Ui.H(16, logo, Ui.V(4,
            new TextBlock { Text = TradingHub.Common.Product.Name, Classes = { "h1" } },
            Ui.Muted($"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · .NET {Environment.Version} · Avalonia + SkiaSharp"),
            Ui.Faint("A personal order flow platform: footprint, DOM, tape, C# indicators, strategies and backtesting."),
            Creator())));

        return new StackPanel
        {
            Spacing = 18,
            MaxWidth = 860,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { Ui.V(4, Ui.H1("Settings"), Ui.Muted("Trading Hub general preferences")), general, insightAlerts, trading, phone, liveStart, sleep, hotkeys, data, about },
        };
    }

    // ===================================================================== live strategies on startup

    private Control BuildLiveStart()
    {
        var s = _svc.Settings;
        var auto = Switch(s.AutoResumeLive, v => s.AutoResumeLive = v);
        var real = Switch(s.AutoResumeLiveReal, v => s.AutoResumeLiveReal = v);
        real.IsEnabled = s.AutoResumeLive;
        auto.IsCheckedChanged += (_, _) => real.IsEnabled = auto.IsChecked == true;
        var smart = Switch(s.SmartContracts, v => s.SmartContracts = v);
        var menuBar = Switch(s.KeepInMenuBar, v =>
        {
            s.KeepInMenuBar = v;
            Services.MenuBar.Current?.Refresh();
        });
        return Section("Live strategies", MaterialIconKind.PlayCircleOutline,
            Setting("Keep running in the menu bar when the window is closed", "Closing the window does not close Trading Hub: it stays in the top right, in the menu bar, with the live strategies working. Clicking its icon opens a panel with the strategies and the positions in real time. To close it completely: ⌘Q or “Quit Trading Hub” in that panel.", menuBar),
            Setting("Resume them automatically when Trading Hub opens", "The ones that were running when Trading Hub closed (or the Mac shut down) start again on their own a few seconds after opening, with the same parameters, size and limits; the paused ones come back paused. Off: they wait for you to resume them from Live.", auto),
            Setting("Real accounts too", "Off (recommended): the ones trading real money wait for you to resume them. On: they start on their own like the rest.", real),
            Setting("Pick the contract automatically (minis or micros)", "If a futures strategy's size calls for a fraction of the mini (for example 1 NQ × 0.5), the orders go to the micro with the same exposure (5 MNQ), while watching the same chart. With whole minis it uses the mini. With an open position it stays on the same contract and switches once it is flat.", smart));
    }

    // ===================================================================== no-sleep mode

    private readonly TextBlock _sleepState = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap };

    /// <summary>Who made it, with a link to their profile.</summary>
    private static Control Creator()
    {
        var handle = new TextBlock
        {
            Text = "@ralphtradesz",
            Foreground = Ui.Accent,
            FontWeight = FontWeight.SemiBold,
            TextDecorations = TextDecorations.Underline,
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        handle.PointerPressed += (_, _) => Shell.OpenUrl("https://x.com/ralphtradesz");
        ToolTip.SetTip(handle, "x.com/ralphtradesz");
        return Ui.H(6, new TextBlock { Text = "Created by", Classes = { "faint", "small" }, VerticalAlignment = VerticalAlignment.Center }, handle);
    }

    private Control BuildSleep()
    {
        var s = _svc.Settings;
        var awake = Switch(s.KeepAwake, v => s.KeepAwake = v);
        var power = Switch(s.LidClosedOnlyOnPower, v => s.LidClosedOnlyOnPower = v);
        var lid = Switch(s.KeepAwakeLidClosed, v =>
        {
            s.KeepAwakeLidClosed = v;
            _status(v ? "Lid closed: whenever something is running, macOS asks for your password once (it is undone on its own when Trading Hub closes)" : "Lid closed disabled: when you close it, the Mac sleeps as usual");
        });
        power.IsEnabled = s.KeepAwakeLidClosed;
        lid.IsCheckedChanged += (_, _) => power.IsEnabled = lid.IsChecked == true;
        RefreshSleep();
        return Section("No-sleep mode", MaterialIconKind.SleepOff,
            Setting("Keep the Mac awake", "While there are live strategies or Claude agents working, the Mac does not sleep from inactivity (the display may still turn off). No password needed.", awake),
            Setting("Keep going with the lid closed", "When you close the lid macOS sleeps everything. With this enabled, whenever something is running Trading Hub asks for the administrator password once and the Mac keeps working with the lid closed as long as Trading Hub is open. It is undone on its own when Trading Hub closes (even if it closes abruptly). With the lid closed the Mac does not ventilate as well: do not put it in a bag like that.", lid),
            Setting("Only on the charger", "On battery, the Mac sleeps when you close the lid even if this is enabled (recommended).", power),
            _sleepState);
    }

    private void RefreshSleep()
    {
        var g = _svc.Sleep;
        string reason = _svc.AwakeReason() ?? "nothing is running";
        _sleepState.Text = (g.Awake ? $"Right now: awake ({g.Reason})." : $"Right now: it can sleep ({reason}).") +
                           (g.LidHelperRunning ? " Lid closed: active." : _svc.Settings.KeepAwakeLidClosed ? " Lid closed: it kicks in once something is running." : "") +
                           (g.LidError != null ? " " + g.LidError : "");
    }

    // ===================================================================== keyboard shortcuts

    private readonly Dictionary<string, Button> _hotkeyButtons = new();

    private Control BuildHotkeys()
    {
        var s = _svc.Settings;
        var list = new StackPanel { Spacing = 6 };
        foreach (var group in Hotkeys.All.GroupBy(h => h.Group))
        {
            list.Children.Add(new TextBlock { Text = group.Key.ToUpperInvariant(), Classes = { "h3" }, Margin = new Thickness(0, list.Children.Count > 0 ? 12 : 0, 0, 2) });
            foreach (var h in group)
            {
                var def = h;
                var key = new Button { MinWidth = 150, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10, 4) };
                ToolTip.SetTip(key, "Click and press the combination. Esc cancels · ⌫ leaves it unassigned.");
                _hotkeyButtons[def.Id] = key;
                bool capturing = false;
                key.Click += (_, _) =>
                {
                    capturing = true;
                    key.Content = "Press a key…";
                    key.Classes.Add("primary");
                    key.Focus();
                };
                key.LostFocus += (_, _) =>
                {
                    if (!capturing) return;
                    capturing = false;
                    key.Classes.Remove("primary");
                    RefreshHotkey(def.Id);
                };
                key.AddHandler(KeyDownEvent, (_, e) =>
                {
                    if (!capturing) return;
                    e.Handled = true;
                    if (e.Key == Avalonia.Input.Key.Escape)
                    {
                        capturing = false;
                        key.Classes.Remove("primary");
                        RefreshHotkey(def.Id);
                        return;
                    }

                    if (e.Key is Avalonia.Input.Key.Back or Avalonia.Input.Key.Delete)
                    {
                        s.Hotkeys[def.Id] = "";
                    }
                    else
                    {
                        var gesture = Hotkeys.FromKey(e.Key, e.KeyModifiers);
                        if (gesture == null) return; // only a modifier: it keeps waiting
                        // if another action was already using that shortcut, it is taken away from it
                        foreach (var other in Hotkeys.All.Where(o => o.Id != def.Id && Hotkeys.GestureOf(s, o.Id) == gesture))
                        {
                            s.Hotkeys[other.Id] = "";
                            RefreshHotkey(other.Id);
                            _status($"{Hotkeys.Pretty(gesture)} was on “{other.Name}”: it moved to “{def.Name}”");
                        }

                        s.Hotkeys[def.Id] = gesture;
                    }

                    capturing = false;
                    key.Classes.Remove("primary");
                    s.Save();
                    RefreshHotkey(def.Id);
                }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
                RefreshHotkey(def.Id);

                var row = new DockPanel();
                DockPanel.SetDock(key, Dock.Right);
                row.Children.Add(key);
                row.Children.Add(new TextBlock { Text = def.Name, VerticalAlignment = VerticalAlignment.Center });
                list.Children.Add(row);
            }
        }

        var reset = Ui.Button("Restore the defaults", MaterialIconKind.Restore, () =>
        {
            s.Hotkeys.Clear();
            s.Save();
            foreach (var h in Hotkeys.All) RefreshHotkey(h.Id);
            _status("Shortcuts reset");
        });
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        var note = new TextBlock
        {
            Text = "They work in Trading and in the separate chart windows, on the active chart. The trading ones ship unassigned; on real accounts they still ask for confirmation if that is enabled.",
            Classes = { "muted", "small" },
            TextWrapping = TextWrapping.Wrap,
        };
        return Ui.Card(Ui.V(16, Ui.H(10, Ui.Icon(MaterialIconKind.KeyboardOutline, 18, Ui.Accent), Ui.H2("Keyboard shortcuts")), note, list, reset), 20);
    }

    private void RefreshHotkey(string id)
    {
        if (_hotkeyButtons.TryGetValue(id, out var b)) b.Content = Hotkeys.Pretty(Hotkeys.GestureOf(_svc.Settings, id));
    }

    private static string Label(TimeZoneInfo z)
    {
        var off = z.BaseUtcOffset;
        return $"(UTC{(off < TimeSpan.Zero ? "-" : "+")}{off:hh\\:mm}) {z.Id.Replace('_', ' ')}";
    }

    /// <summary>Phone alerts through the user's own Telegram bot. The token goes to the credential store, never to a file.</summary>
    private Control BuildPhoneAlerts()
    {
        var s = _svc.Settings;
        var on = Switch(s.TelegramAlerts, v => s.TelegramAlerts = v);
        var commands = Switch(s.TelegramCommands, v => s.TelegramCommands = v);
        var chat = new TextBox { Text = s.TelegramChatId, Watermark = "chat id", MinWidth = 190 };
        chat.LostFocus += (_, _) =>
        {
            s.TelegramChatId = chat.Text?.Trim() ?? "";
            s.Save();
        };
        var token = new TextBox { PasswordChar = '•', Watermark = TelegramNotifier.HasToken ? "token saved (type to replace)" : "bot token", MinWidth = 190 };
        token.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(token.Text)) return;
            TelegramNotifier.SetToken(token.Text!);
            token.Text = "";
            token.Watermark = "token saved (type to replace)";
        };
        var note = Ui.Faint("");
        var test = Ui.Button("Send test", MaterialIconKind.SendOutline, async () =>
        {
            note.Text = "Sending…";
            var (ok, error) = await TelegramNotifier.SendAsync(chat.Text ?? "", "Trading Hub: test alert");
            note.Text = ok ? "Sent. Check your phone." : "Not sent: " + error;
        });
        return Section("Phone alerts (Telegram)", MaterialIconKind.Cellphone,
            Setting("Send alerts to Telegram", "Level touches and other indicator alerts are sent to your own Telegram bot (create one with @BotFather). The text of each alert (instrument, size, price) leaves this PC for Telegram's servers. Off by default.", on),
            Setting("Answer commands", "Your bot also answers /status, /levels GC.F, /today and /mute 30 sent from your own chat, so you can check the platform from the phone. Read-only: nothing can place or change an order. The app has to be open.", commands),
            Setting("Chat id", "Your chat id with the bot (message the bot once, then look it up with @userinfobot or the bot's getUpdates).", chat),
            Setting("Bot token", "Kept in the system credential store, not in any file.", token),
            Setting("Test", "Sends one message to check the token and chat id.", Ui.V(6, test, note)));
    }

    /// <summary>Lists the automatic backups of the settings files and puts one back at the next start.</summary>
    private Control BuildBackups()
    {
        var list = SettingsBackup.List();
        var box = new ComboBox { MinWidth = 190, ItemsSource = list, SelectedIndex = list.Count > 0 ? 0 : -1 };
        var note = Ui.Faint("");
        var restore = Ui.Button("Restore", MaterialIconKind.BackupRestore, () =>
        {
            if (box.SelectedItem is not string name) return;
            SettingsBackup.RequestRestore(name);
            note.Text = $"'{name}' will be put back the next time Trading Hub starts. Close it and open it again.";
        });
        return Setting("Settings backups", "Your connections, charts and settings are copied at every start (the newest 10 are kept). Pick one and restore it: it is applied when Trading Hub starts again, and what it replaces is kept as another backup.",
            Ui.V(6, Ui.H(8, box, restore), note));
    }

    private ToggleSwitch Switch(bool value, Action<bool> set)
    {
        var t = new ToggleSwitch { IsChecked = value, OnContent = "", OffContent = "" };
        t.IsCheckedChanged += (_, _) =>
        {
            set(t.IsChecked == true);
            _svc.Settings.Save();
        };
        return t;
    }

    private static Control Section(string title, MaterialIconKind icon, params Control[] rows)
    {
        var list = new StackPanel { Spacing = 0 };
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) list.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 14) }.Classes("divider"));
            list.Children.Add(rows[i]);
        }

        return Ui.Card(Ui.V(16, Ui.H(10, Ui.Icon(icon, 18, Ui.Accent), Ui.H2(title)), list), 20);
    }

    private static Control Setting(string title, string description, Control control)
    {
        var text = Ui.V(3, new TextBlock { Text = title, FontWeight = FontWeight.Medium }, new TextBlock { Text = description, Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
        var row = new DockPanel();
        control.VerticalAlignment = VerticalAlignment.Center;
        control.Margin = new Thickness(24, 0, 0, 0);
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(text);
        return row;
    }
}
