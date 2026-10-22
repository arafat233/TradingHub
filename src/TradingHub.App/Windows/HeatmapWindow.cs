using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Controls;
using TradingHub.Charting.Heatmap;
using System.Diagnostics;
using TradingHub.Data;

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
    private readonly ComboBox _day = new() { MinWidth = 150 };
    private readonly ComboBox _speed = new() { MinWidth = 80 };
    private readonly Slider _scrub = new() { Minimum = 0, Maximum = 86_399, Width = 360 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly StackPanel _replayBar;
    private Button _pause = null!;
    private DateOnly _replayDay;
    private TimeSpan _last;
    private bool _syncing;
    private int _tick;

    public HeatmapWindow(InstrumentData data, Action saveSettings, HeatmapSettings settings)
    {
        _data = data;
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
        foreach (var x in new[] { 1, 2, 5, 10, 30, 60 }) _speed.Items.Add(new ComboBoxItem { Content = $"{x}×", Tag = (double)x });
        _speed.SelectedIndex = 0;
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
        _speed.IsVisible = _scrub.IsVisible = !live;
        _pause.Content = "Pause";
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
