using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.Charting.Heatmap;
using System.Diagnostics;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Windows;

/// <summary>
/// Liquidity heatmap of an instrument in its own window (it can go to another monitor): what rests in the book at each
/// price over the last minutes, with the best bid and ask, the big trades as bubbles, the live book and the volume.
/// </summary>
internal sealed class HeatmapWindow : Window
{
    private readonly InstrumentData _data;
    private readonly HeatmapView _view;
    private readonly DispatcherTimer _timer;
    private readonly ComboBox _window = new() { MinWidth = 96 };
    private readonly Slider _contrast = new() { Minimum = 0.4, Maximum = 4, Width = 130, TickFrequency = 0.1 };
    private readonly TextBlock _status = Ui.Muted("");
    private readonly TextBlock _narration = new() { Margin = new Thickness(12, 0, 12, 8), TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 13 };
    private readonly List<string> _said = new();
    private readonly TextBlock _events = new() { Margin = new Thickness(12, 0, 12, 8), FontSize = 12, FontFamily = new Avalonia.Media.FontFamily(TradingHub.App.Services.Plat.Mono), Opacity = 0.85 };
    private string _lastSaid = "";
    private DateTime _lastSaidAt;
    private readonly ComboBox _day = new() { MinWidth = 150 };
    private readonly ComboBox _speed = new() { MinWidth = 80 };
    private readonly Slider _scrub = new() { Minimum = 0, Maximum = 86_399, Width = 360 };
    private readonly ComboBox _marks = new() { MinWidth = 150, PlaceholderText = "Marks" };
    private readonly HeatmapSettings _settings;
    private readonly Action _saveSettings;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StackPanel _replayBar;
    private StackPanel _skips = null!, _markBar = null!;
    private Button _pause = null!;
    private DateOnly _replayDay;
    private TimeSpan _last;
    private bool _syncing;
    private int _tick;

    public HeatmapWindow(InstrumentData data, Action saveSettings, HeatmapSettings settings)
    {
        _data = data;
        _settings = settings;
        _saveSettings = saveSettings;
        Title = $"Trading Hub — Heatmap {data.Instrument.Symbol}";
        Width = 1240;
        Height = 780;
        MinWidth = 640;
        MinHeight = 400;
        if (settings.WindowWidth >= 640 && settings.WindowHeight >= 400)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(settings.WindowX, settings.WindowY);
            Opened += (_, _) =>
            {
                // a monitor that is gone would leave the window out of reach: back to the middle
                if (Screens.All.All(sc => !sc.Bounds.Contains(Position))) WindowStartupLocation = WindowStartupLocation.CenterScreen;
                if (Screens.All.All(sc => !sc.Bounds.Contains(Position))) Position = Screens.Primary?.WorkingArea.TopLeft ?? Position;
            };
        }

        Background = Ui.Bg0;
        Icon = AppIcon.Window;

        _view = new HeatmapView(new HeatmapModel(data, settings));
        foreach (var m in new[] { 0.5, 1, 2, 3, 5, 10, 15, 20, 30 })
            _window.Items.Add(new ComboBoxItem { Content = m < 1 ? "30 seconds" : $"{m:0} min", Tag = m });
        _window.SelectionChanged += (_, _) =>
        {
            if (_syncing || _window.SelectedItem is not ComboBoxItem { Tag: double m }) return;
            settings.WindowMinutes = m;
            saveSettings();
        };
        _contrast.PropertyChanged += (_, e) =>
        {
            if (_syncing || e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            settings.Contrast = Math.Round(_contrast.Value, 1);
            saveSettings();
        };
        _view.SettingsChanged += () =>
        {
            Sync(settings);
            saveSettings();
        };

        CheckBox Toggle(string text, string tip, bool on, Action<bool> set)
        {
            var c = new CheckBox { Content = text, IsChecked = on, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            ToolTip.SetTip(c, tip);
            c.IsCheckedChanged += (_, _) =>
            {
                if (_syncing) return;
                set(c.IsChecked == true);
                saveSettings();
            };
            return c;
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(12, 8) };
        bar.Children.Add(Ui.Field("Time on screen", _window));
        bar.Children.Add(Ui.Field("Contrast", _contrast));
        var toggles = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 12, 6) };
        toggles.Children.Add(Toggle("Bid / ask", "The best bid (green) and the best ask (purple)", settings.ShowBidAsk, v => settings.ShowBidAsk = v));
        toggles.Children.Add(Toggle("Bubbles", "The biggest trades: green when buyers hit the ask, purple when sellers hit the bid", settings.ShowBubbles, v => settings.ShowBubbles = v));
        toggles.Children.Add(Toggle("Volume", "Volume traded below the heatmap", settings.ShowVolume, v => settings.ShowVolume = v));
        toggles.Children.Add(Toggle("Icebergs", "Levels that kept refilling: much more traded against them than they ever showed (green = resting buy, red = resting sell). Live only; it is a guess, stops and market makers can look the same", settings.ShowIcebergs, v => settings.ShowIcebergs = v));
        toggles.Children.Add(Toggle("CVD", "Cumulative delta (buy minus sell volume) under the volume", settings.ShowCvd, v => settings.ShowCvd = v));
        toggles.Children.Add(Toggle("Book", "The ladder on the right: live book size and the volume traded at each price", settings.ShowDom, v => settings.ShowDom = v));
        toggles.Children.Add(Toggle("Stats", "The Market Vol and Avg. Liquidity boxes in the corner", settings.ShowStats, v => settings.ShowStats = v));
        toggles.Children.Add(Toggle("Gauges", "The dials under the ladder: hidden bid/ask (volume absorbed by icebergs), hit bid/ask, delta/depth and CVD", settings.ShowGauges, v => settings.ShowGauges = v));
        toggles.Children.Add(Toggle("Blocks", "Pale blocks over big orders that sat still for 20 seconds or more", settings.ShowBlocks, v => settings.ShowBlocks = v));
        var follow = Ui.Button("Follow price", MaterialIconKind.CrosshairsGps, () => _view.Model.Follow());
        follow.VerticalAlignment = VerticalAlignment.Bottom;
        bar.Children.Add(follow);
        var pause = Ui.Button("Pause", MaterialIconKind.Pause, () => { });
        pause.VerticalAlignment = VerticalAlignment.Bottom;
        _pause = pause;
        pause.Click += (_, _) =>
        {
            if (_view.Model.Replay is { } r)
            {
                r.Playing = !r.Playing;
                pause.Content = r.Playing ? "Pause" : "Play";
                return;
            }

            if (_view.Model.FrozenAt is null) _view.Model.Freeze(); else _view.Model.Resume();
            pause.Content = _view.Model.FrozenAt is null ? "Pause" : "Resume";
        };
        bar.Children.Add(pause);
        // replay of the recorded book: pick a day, play it, move the time slider
        _day.Items.Add(new ComboBoxItem { Content = "Live", Tag = null });
        if (data.RecordingRoot != null)
            foreach (var d in DepthFile.AvailableDays(data.RecordingRoot, data.Instrument.FeedId, data.Instrument.Symbol).Reverse())
                _day.Items.Add(new ComboBoxItem { Content = d.ToString("ddd dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), Tag = d });
        _day.SelectedIndex = 0;
        foreach (var x in new[] { 0.5, 1, 2, 5, 10, 30, 60, 120 }) _speed.Items.Add(new ComboBoxItem { Content = $"{x}×", Tag = x });
        _speed.SelectedIndex = 1;
        _day.SelectionChanged += (_, _) => OnDayChanged();
        _speed.SelectionChanged += (_, _) =>
        {
            if (_view.Model.Replay is { } r && _speed.SelectedItem is ComboBoxItem { Tag: double x }) r.Speed = x;
        };
        _scrub.PropertyChanged += (_, e) =>
        {
            if (_syncing || e.Property != Avalonia.Controls.Primitives.RangeBase.ValueProperty) return;
            if (_view.Model.Replay is { } r) r.Seek(_replayDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddSeconds(_scrub.Value));
        };
        _replayBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(12, 0, 12, 8) };
        _replayBar.Children.Add(Ui.Field(_day.Items.Count > 1 ? "Replay a recorded day" : "Replay (nothing recorded yet)", _day));
        _replayBar.Children.Add(Ui.Field("Speed", _speed));
        _replayBar.Children.Add(Ui.Field("Time of day (UTC)", _scrub));
        // jump buttons, and marks: keep the moments worth coming back to
        Button Skip(string text, double seconds)
        {
            var b = Ui.Button(text, null, () => Jump(TimeSpan.FromSeconds(seconds)));
            b.VerticalAlignment = VerticalAlignment.Bottom;
            ToolTip.SetTip(b, "Jump in the replay (keyboard: ← → for 30 s, with Shift 5 min; Space plays or pauses)");
            return b;
        }

        _skips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, IsVisible = false, Children = { Skip("−5m", -300), Skip("−30s", -30), Skip("+30s", 30), Skip("+5m", 300) } };
        var mark = Ui.Button("Mark", MaterialIconKind.BookmarkOutline, AddMark);
        mark.VerticalAlignment = VerticalAlignment.Bottom;
        ToolTip.SetTip(mark, "Remember this moment of the replay (it is saved with the heatmap settings)");
        _marks.SelectionChanged += (_, _) =>
        {
            if (_syncing || _marks.SelectedItem is not ComboBoxItem { Tag: DateTime t } || _view.Model.Replay is not { } r) return;
            r.Seek(t);
            _view.Model.Follow();
        };
        _markBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, IsVisible = false, Children = { mark, _marks } };
        _replayBar.Children.Add(_skips);
        _replayBar.Children.Add(_markBar);
        KeyDown += OnReplayKey;
        _speed.IsVisible = false;
        _scrub.IsVisible = false;
        _replayBar.Children[1].IsVisible = false;
        _replayBar.Children[2].IsVisible = false;

        // the status goes on its own line under the controls so it is never cut off at the right edge
        _status.Margin = new Thickness(12, 0, 12, 6);
        _status.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        var top = new StackPanel();
        top.Children.Add(bar);
        top.Children.Add(_replayBar);
        top.Children.Add(toggles);
        top.Children.Add(_status);
        top.Children.Add(_narration);
        top.Children.Add(_events);

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_view);
        Content = dock;
        Sync(settings);

        // the picture is redrawn about ten times a second (the data itself is pumped by the main window's timer)
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Render, (_, _) => OnTick());
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (WindowState != WindowState.Normal) return;
            (settings.WindowWidth, settings.WindowHeight, settings.WindowX, settings.WindowY) = (Width, Height, Position.X, Position.Y);
            saveSettings();
        };
    }

    private void Sync(HeatmapSettings s)
    {
        _syncing = true;
        _window.SelectedItem = _window.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is double m && Math.Abs(m - s.WindowMinutes) < 1e-6);
        _contrast.Value = s.Contrast;
        _syncing = false;
    }

    /// <summary>Starts playing a recorded day (for the headless scenes; the day has to be in the list).</summary>
    internal void OpenReplay(DateOnly day)
    {
        var item = _day.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is DateOnly d && d == day);
        if (item != null) _day.SelectedItem = item;
    }

    internal HeatmapView View => _view;

    private void OnDayChanged()
    {
        if (_syncing) return;
        var m = _view.Model;
        bool live = _day.SelectedItem is not ComboBoxItem { Tag: DateOnly };
        _replayBar.Children[1].IsVisible = _replayBar.Children[2].IsVisible = !live;
        _speed.IsVisible = _scrub.IsVisible = _skips.IsVisible = _markBar.IsVisible = !live;
        _pause.Content = "Pause";
        _said.Clear(); // a line about the live market does not belong under a replay
        _narration.Text = "";
        _events.Text = "";
        _view.Model.BookEvents.Reset();
        if (live)
        {
            m.StopReplay();
            m.Resume();
            return;
        }

        _replayDay = (DateOnly)((ComboBoxItem)_day.SelectedItem!).Tag!;
        var start = DepthReplay.FirstTime(_data.RecordingRoot!, _data.Instrument, _replayDay) ?? _replayDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        m.Resume();
        m.StartReplay(start);
        if (m.Replay is { } r && _speed.SelectedItem is ComboBoxItem { Tag: double x }) r.Speed = x;
        m.Follow();
        FillMarks();
    }

    private string MarkKey => _data.Instrument.Key;

    private void FillMarks()
    {
        _syncing = true;
        _marks.Items.Clear();
        if (_settings.Bookmarks.TryGetValue(MarkKey, out var list))
            foreach (var t in list.OrderBy(t => t))
                _marks.Items.Add(new ComboBoxItem { Content = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(t, DateTimeKind.Utc), TimeZoneInfo.Local).ToString("ddd dd MMM HH:mm:ss"), Tag = t });
        _marks.SelectedItem = null;
        _syncing = false;
    }

    private void AddMark()
    {
        if (_view.Model.Replay is not { } r) return;
        if (!_settings.Bookmarks.TryGetValue(MarkKey, out var list)) _settings.Bookmarks[MarkKey] = list = new List<DateTime>();
        list.Add(DateTime.SpecifyKind(r.Cursor, DateTimeKind.Utc));
        _saveSettings();
        FillMarks();
    }

    private void Jump(TimeSpan by)
    {
        if (_view.Model.Replay is not { } r) return;
        var day0 = _replayDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var t = r.Cursor + by;
        r.Seek(t < day0 ? day0 : t > day0.AddDays(1).AddSeconds(-1) ? day0.AddDays(1).AddSeconds(-1) : t);
    }

    private void OnReplayKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (_view.Model.Replay is not { } r || e.Source is TextBox) return;
        var big = e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift);
        switch (e.Key)
        {
            case Avalonia.Input.Key.Space: r.Playing = !r.Playing; _pause.Content = r.Playing ? "Pause" : "Play"; break;
            case Avalonia.Input.Key.Left: Jump(TimeSpan.FromSeconds(big ? -300 : -30)); break;
            case Avalonia.Input.Key.Right: Jump(TimeSpan.FromSeconds(big ? 300 : 30)); break;
            default: return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// The newest add / pull / eaten events of big resting orders, one line each. From a feed that publishes every order (Bitfinex) they are real
    /// (<see cref="OrderFlowDetector"/>); otherwise they are inferred from the Level 2 book (<see cref="BookEventDetector"/>), and a replay always is.
    /// </summary>
    private void ShowBookEvents(HeatmapModel m, DateTime now)
    {
        bool real = m.Replay == null && _data.OrderFlow.Active;
        List<BookEvent> events;
        if (real) events = _data.OrderFlow.Events;
        else
        {
            m.BookEvents.Update(m.History, m.Trades);
            events = m.BookEvents.Events;
        }

        // grouped (a re-quoted ladder is one line) and without the crumbs next to the biggest orders of the last 10 minutes
        var lines = BookEventGrouper.Group(events.Where(e => e.Time <= now && e.Time >= now.AddMinutes(-10))).TakeLast(6).Reverse()
            .Select(g => $"{TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(g.Time, DateTimeKind.Utc), TimeZoneInfo.Local):HH:mm:ss}  {g.Text(m.Instrument)}").ToList();
        if (lines.Count == 0) { _events.Text = ""; return; }
        lines.Insert(0, real ? "Book events (real orders from the feed)" : "Book events (inferred from the order book)");
        _events.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>One plain sentence about the last 30 seconds of tape (see <see cref="TapeNarrator"/>); the last few stay on screen.</summary>
    private void Narrate()
    {
        var m = _view.Model;
        var store = m.Trades;
        var inst = m.Instrument;
        var now = m.Now;
        ShowBookEvents(m, now);
        int i1 = store.LowerBound(now.AddTicks(1)), i0 = store.LowerBound(now.AddSeconds(-30)), i5 = store.LowerBound(now.AddMinutes(-5));
        if (i1 - i0 < 3) return;
        decimal buy = 0, sell = 0, big = 0, bigPrice = 0;
        bool bigBuy = true;
        for (int i = i0; i < i1; i++)
        {
            decimal v = store.GetVolume(i);
            if (store.GetSide(i) == TradeSide.Buy) buy += v; else sell += v;
            if (v > big) { (big, bigPrice, bigBuy) = (v, store.GetPrice(i), store.GetSide(i) == TradeSide.Buy); }
        }

        var sizes = new List<decimal>(Math.Min(i1 - i5, 20000));
        for (int i = Math.Max(i5, i1 - 20000); i < i1; i++) sizes.Add(store.GetVolume(i));
        sizes.Sort();
        decimal typical = sizes.Count == 0 ? 0 : sizes[sizes.Count / 2];
        decimal total5 = 0;
        for (int i = i5; i < i1; i++) total5 += store.GetVolume(i);
        decimal change = (store.GetPriceTicks(i1 - 1) - store.GetPriceTicks(i0));
        long lo = long.MaxValue, hi = long.MinValue;
        for (int i = i0; i < i1; i++) { long p = store.GetPriceTicks(i); lo = Math.Min(lo, p); hi = Math.Max(hi, p); }
        var book = m.Book;
        decimal bid = 0, ask = 0;
        for (int i = 0; i < Math.Min(5, book.Bids.Length); i++) bid += book.Bids[i].Size;
        for (int i = 0; i < Math.Min(5, book.Asks.Length); i++) ask += book.Asks[i].Size;
        var ice = m.Icebergs.Events.LastOrDefault(e => e.Last >= now.AddSeconds(-20));
        var facts = new TapeFacts(30, buy, sell, change, hi - lo, total5 / 10m, big, bigBuy, bigPrice, typical, bid, ask,
            ice != null, ice?.Ask ?? false, ice == null ? 0 : inst.FromTicks(ice.Ticks), ice?.Volume ?? 0);
        var line = TapeNarrator.Describe(facts);
        if (line == null) return;
        // the same thing again within 20 s is not news
        if (line == _lastSaid && (DateTime.UtcNow - _lastSaidAt).TotalSeconds < 20) return;
        (_lastSaid, _lastSaidAt) = (line, DateTime.UtcNow);
        _said.Insert(0, $"{TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(now, DateTimeKind.Utc), TimeZoneInfo.Local):HH:mm:ss}  {line}");
        if (_said.Count > 3) _said.RemoveAt(3);
        _narration.Text = string.Join(Environment.NewLine, _said);
    }

    private void OnTick()
    {
        var now = _clock.Elapsed;
        var elapsed = now - _last;
        _last = now;
        if (_view.Model.Replay is { } r)
        {
            r.Advance(elapsed);
            _syncing = true;
            _scrub.Value = Math.Clamp((r.Cursor - _replayDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).TotalSeconds, 0, 86_399);
            _syncing = false;
        }

        _view.InvalidateVisual();
        if (_tick % 20 == 0) Narrate();
        if (++_tick % 10 != 0) return;
        if (_view.Model.Replay is { } rp)
        {
            var hh = _view.Model.History;
            _status.Text = rp.Loading
                ? "Replay: loading the recorded book…"
                : $"Replay {TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(rp.Cursor, DateTimeKind.Utc), TimeZoneInfo.Local):ddd dd MMM HH:mm:ss} (local) · {(rp.Playing ? $"playing {rp.Speed:0}×" : "paused")} · {hh.Count:N0} snapshots loaded";
            return;
        }

        var h = _data.DepthHistory;
        var book = _data.Depth;
        _status.Text = h.Count == 0
            ? $"{_data.Feed.DisplayName}: no order book yet"
            : $"{_data.Feed.DisplayName} · {book.Bids.Length} bid / {book.Asks.Length} ask levels · {h.Count:N0} snapshots ({(h.LastTime - h.FirstTime).TotalMinutes:0.#} min)";
    }
}
