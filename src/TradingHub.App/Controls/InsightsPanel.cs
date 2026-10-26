using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Controls;

/// <summary>
/// The Insights section of the right-hand panel: the quote, the decision (a verdict with the six checks behind it), plain-language chips, how
/// toxic the tape is, how real the big resting orders are, and how the panel's own past calls have done. Everything shown is measured from the
/// tape and the book (<see cref="DecisionEngine"/>); it is context for a person, never an order.
/// </summary>
public sealed class InsightsPanel : UserControl
{
    private static readonly CultureInfo Fmt = CultureInfo.InvariantCulture;

    private readonly TextBlock _quote = new() { FontSize = 12, FontFamily = new FontFamily(Services.Plat.Mono), TextWrapping = TextWrapping.Wrap };
    private readonly NumericUpDown _cost = new() { Minimum = 0, Maximum = 500, Increment = 1, FormatString = "0.#", Width = 132, MinHeight = 30, FontSize = 12, HorizontalContentAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _verdict = new() { FontSize = 22, FontWeight = FontWeight.Bold };
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    // who is leading: a bar from the middle, to the left in red for sellers and to the right in green for buyers
    private readonly Grid _leftBar = new() { ColumnDefinitions = new ColumnDefinitions("*,0") };
    private readonly Grid _rightBar = new() { ColumnDefinitions = new ColumnDefinitions("0,*") };
    private readonly Border _sellFill = new() { Background = Ui.Down, CornerRadius = new CornerRadius(3, 0, 0, 3) };
    private readonly Border _buyFill = new() { Background = Ui.Up, CornerRadius = new CornerRadius(0, 3, 3, 0) };
    private readonly Grid _leanBar = new() { ColumnDefinitions = new ColumnDefinitions("*,2,*"), Height = 8 };
    private readonly TextBlock _buyAbove = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _sellBelow = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _missing = new() { FontSize = 12 };
    private readonly StackPanel _checks = new() { Spacing = 3 };
    private readonly TextBlock _whales = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _session = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily(Services.Plat.Mono), IsVisible = false };
    private readonly Button _bell = new() { Content = "🔔", Padding = new Thickness(8, 2), FontSize = 13, Background = Brushes.Transparent };
    private readonly Button _gamma = new() { Content = "Gamma", Padding = new Thickness(8, 2), FontSize = 12, Background = Brushes.Transparent, IsVisible = false };
    private readonly TextBox _myLevel = new() { Watermark = "add your level (price)", FontSize = 12, MinHeight = 28 };
    private readonly Button _guide = new() { Content = "Guide", Padding = new Thickness(8, 2), FontSize = 12, Background = Brushes.Transparent };
    private readonly WrapPanel _chips = new();
    private readonly Ellipse _dangerDot = new() { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _danger = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _walls = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _futures = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _edge = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _calls = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, IsVisible = false, FontFamily = new FontFamily(Services.Plat.Mono) };
    private readonly TextBlock _learning = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _decisionBody = new() { Spacing = 8 };
    private readonly StackPanel _levelsHost = new() { Spacing = 3 };
    private readonly StackPanel _peersHost = new() { Spacing = 4, IsVisible = false };
    private readonly StackPanel _peerLines = new() { Spacing = 3 };
    private readonly CheckBox _watch = new() { Content = "Watch related markets", FontSize = 12 };
    private string _peerSig = "";
    private readonly List<(TextBlock Name, TextBlock Value, TextBlock Need)> _rows = new();
    private string _chipSig = "", _levelSig = "";
    private bool _syncing;

    /// <summary>The user changed the round-trip cost (in basis points).</summary>
    public event Action<decimal>? CostChanged;

    /// <summary>The bell was pressed: the host shows the alert settings next to it.</summary>
    public event Action<Control>? AlertsRequested;

    /// <summary>The user added a level of their own (a price) or removed one.</summary>
    public event Action<decimal>? MyLevelAdded, MyLevelRemoved;

    /// <summary>The "Gamma view" button was pressed.</summary>
    public event Action? GammaRequested;

    /// <summary>The Guide button was pressed.</summary>
    public event Action? GuideRequested;

    /// <summary>The user switched "watch related markets" on or off.</summary>
    public event Action<bool>? WatchRelatedChanged;

    public InsightsPanel()
    {
        _bell.Click += (_, _) => AlertsRequested?.Invoke(_bell);
        _guide.Click += (_, _) => GuideRequested?.Invoke();
        _gamma.Click += (_, _) => GammaRequested?.Invoke();
        ToolTip.SetTip(_gamma, "Open interest by strike, calls against puts, with max pain and the gamma profile (Bitcoin and Ethereum).");
        _myLevel.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            if (decimal.TryParse(_myLevel.Text?.Trim().Replace(',', '.'), NumberStyles.Float, Fmt, out var price) && price > 0)
            {
                MyLevelAdded?.Invoke(price);
                _myLevel.Text = "";
            }
        };
        ToolTip.SetTip(_bell, "Which Insights events raise an alert (toxic tape, big orders pulled, options levels, liquidations, setups).");
        ToolTip.SetTip(_guide, "What every line of this panel means, and what it cannot tell you.");
        _watch.IsCheckedChanged += (_, _) =>
        {
            if (!_syncing) WatchRelatedChanged?.Invoke(_watch.IsChecked == true);
        };
        ToolTip.SetTip(_watch, "Opens the sibling markets (ES with NQ, MES, YM, RTY; gold with silver and platinum; Bitcoin with Ethereum...) so their moves and who leads can be shown here. Each one is a live subscription that stays until the app closes.");
        _peersHost.Children.Add(Ui.H3("Related markets"));
        _peersHost.Children.Add(_watch);
        _peersHost.Children.Add(_peerLines);

        _cost.ValueChanged += (_, _) =>
        {
            if (!_syncing && _cost.Value is { } v) CostChanged?.Invoke(v);
        };
        ToolTip.SetTip(_cost, "What a round trip costs you, in basis points of the price: two fees plus slippage. The decision's targets must beat it by 2×. The live spread is added on top.");

        var costRow = new DockPanel();
        var costLabel = Ui.Faint("Costs (round trip, bp)");
        costLabel.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_cost, Dock.Right);
        costRow.Children.Add(_cost);
        costRow.Children.Add(costLabel);

        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        var buyTile = Tile(_buyAbove, Ui.Up);
        var sellTile = Tile(_sellBelow, Ui.Down);
        Grid.SetColumn(sellTile, 2);
        tiles.Children.Add(buyTile);
        tiles.Children.Add(sellTile);

        var checkHeader = new DockPanel();
        var need = Ui.H3("What a setup needs");
        _missing.Foreground = Ui.Text2;
        DockPanel.SetDock(_missing, Dock.Right);
        checkHeader.Children.Add(_missing);
        checkHeader.Children.Add(need);

        Grid.SetColumn(_sellFill, 1);
        Grid.SetColumn(_buyFill, 1);
        _leftBar.Children.Add(_sellFill);
        _rightBar.Children.Add(_buyFill);
        Grid.SetColumn(_rightBar, 2);
        Grid.SetColumn(_leftBar, 0);
        var mid = new Border { Background = Ui.Text2 };
        Grid.SetColumn(mid, 1);
        _leanBar.Children.Add(_leftBar);
        _leanBar.Children.Add(mid);
        _leanBar.Children.Add(_rightBar);
        _leanBar.Background = Ui.Bg3;
        ToolTip.SetTip(_leanBar, "Strength of the lean: how one-sided the last five minutes of trades were (left = sellers, right = buyers).");
        _decisionBody.Children.Add(_leanBar);
        _decisionBody.Children.Add(_detail);
        _decisionBody.Children.Add(tiles);
        _decisionBody.Children.Add(_chips);
        _decisionBody.Children.Add(new Expander
        {
            Header = checkHeader,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = Ui.V(6, _checks, _whales, _session),
        });

        var card = new Border { Child = Ui.V(8, _verdict, _learning, _decisionBody), Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Background = Ui.Bg2 };
        var dangerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        Grid.SetColumn(_danger, 1);
        _dangerDot.VerticalAlignment = VerticalAlignment.Top;
        _dangerDot.Margin = new Thickness(0, 4, 0, 0);
        dangerRow.Children.Add(_dangerDot);
        dangerRow.Children.Add(_danger);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_gamma);
        buttons.Children.Add(_guide);
        buttons.Children.Add(_bell);
        var content = Ui.V(10,
            buttons,
            _quote,
            costRow,
            Ui.H3("Decision"),
            card,
            dangerRow,
            _walls,
            _futures,
            _edge,
            _calls,
            _levelsHost,
            _myLevel,
            _peersHost,
            Ui.Faint("Everything here is measured from the tape and the book, with its threshold shown. It is context for you, not a signal: it has not been shown to make money, which is what the edge meter keeps score of.").Also(t => t.TextWrapping = TextWrapping.Wrap));
        content.Margin = new Thickness(14, 4, 14, 14);
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private static Border Tile(TextBlock text, IBrush edge) =>
        new() { Child = text, Padding = new Thickness(8, 6), CornerRadius = new CornerRadius(6), BorderBrush = edge, BorderThickness = new Thickness(1) };

    private void SetLean(int signed)
    {
        double f = Math.Clamp(Math.Abs(signed) / 100.0, 0, 1);
        _leftBar.ColumnDefinitions = new ColumnDefinitions(signed < 0 ? $"{1 - f:0.###}*,{f:0.###}*" : "*,0");
        _rightBar.ColumnDefinitions = new ColumnDefinitions(signed > 0 ? $"{f:0.###}*,{1 - f:0.###}*" : "0,*");
        Grid.SetColumn(_sellFill, 1);
        Grid.SetColumn(_buyFill, 0);
        _sellFill.IsVisible = signed < 0;
        _buyFill.IsVisible = signed > 0;
    }

    private static string Compact(decimal v) => v >= 1_000_000 ? $"${v / 1_000_000:0.##}M" : v >= 1_000 ? $"${v / 1_000:0.#}K" : $"${v:0}";

    /// <summary>Shows one snapshot. Controls are updated in place (only the chips and levels are rebuilt, and only when they changed).</summary>
    /// <param name="peers">Null = this market has no related ones (the block is hidden); empty = they exist but are not being watched; otherwise one line each.</param>
    public void Show(InsightsSnapshot s, Instrument inst, IReadOnlyList<LevelLine>? levels = null, IReadOnlyList<string>? peers = null, bool watching = false)
    {
        _syncing = true;
        if (_cost.Value is not { } cur || cur != s.CostBps) _cost.Value = s.CostBps;
        _syncing = false;

        var q = s.Quote;
        _quote.Text = q.HasBook
            ? $"bid {q.Bid.ToString("0.####", Fmt)}  ask {q.Ask.ToString("0.####", Fmt)}  spread {q.SpreadTicks:0.#} tick{(q.SpreadTicks == 1 ? "" : "s")} · {q.SpreadBps:0.###} bp"
            : "no order book from this feed: the spread is not known";

        var d = s.Decision;
        if (d.Learning != null)
        {
            _verdict.Text = "WAIT";
            _verdict.Foreground = Ui.Text2;
            _learning.Text = d.Learning;
            _learning.IsVisible = true;
            _decisionBody.IsVisible = false;
        }
        else
        {
            _learning.IsVisible = false;
            _decisionBody.IsVisible = true;
            var tone = d.Verdict == Verdict.BuySetup ? Ui.Up : d.Verdict == Verdict.SellSetup ? Ui.Down : Ui.Text1;
            _verdict.Text = d.Headline;
            _verdict.Foreground = tone;
            _detail.Text = d.Detail;
            SetLean(d.Lean * d.Strength);
            _buyAbove.Text = $"Buy above {d.BuyAbove.ToString("0.####", Fmt)}" + (d.AskWall is { } aw ? $"\nbig ask {Compact(aw.Notional)} at {aw.Price.ToString("0.####", Fmt)}{Away(aw.Price, q)}" : "");
            _sellBelow.Text = $"Sell below {d.SellBelow.ToString("0.####", Fmt)}" + (d.BidWall is { } bw ? $"\nbig bid {Compact(bw.Notional)} at {bw.Price.ToString("0.####", Fmt)}{Away(bw.Price, q)}" : "");
            _missing.Text = d.Missing == 0 ? "all met" : $"{d.Missing} missing";
            SyncChecks(d.Checks);
            SyncChips(d.Chips);
        }

        // nothing has traded for much longer than this market's usual gap: the call is from old numbers, so it is dimmed and says so
        if (s.StaleSeconds is { } stale)
        {
            _verdict.Text = "PAUSED";
            _verdict.Foreground = Ui.Warn;
            _learning.Text = $"No trades for {stale} s (more than this market's usual gap allows): the numbers below are old, so no call is made and none is scored.";
            _learning.IsVisible = true;
            _decisionBody.IsVisible = d.Learning == null;
        }

        _decisionBody.Opacity = s.StaleSeconds != null ? 0.35 : 1;
        SyncExtras(s);

        _dangerDot.Fill = s.Danger switch { DangerLevel.High => Ui.Down, DangerLevel.Elevated => Ui.Warn, DangerLevel.Normal => Ui.Up, DangerLevel.Low => Ui.Up, _ => Ui.Text2 };
        _danger.Text = s.DangerText;
        _walls.Text = WallsText(s.Walls, s.WallsFromRealOrders) + (s.Pulls != null ? $"\nPulled: {s.Pulls}." : "");
        _futures.IsVisible = s.Futures != null;
        _futures.Text = s.Futures == null ? "" : "Futures and sentiment\n" + s.Futures;
        _edge.Text = s.EdgeText;
        _calls.IsVisible = s.Calls is { Count: > 0 };
        _calls.Text = s.Calls == null ? "" : string.Join("\n", s.Calls);
        SyncLevels(levels ?? Array.Empty<LevelLine>());
        SyncPeers(peers, watching);
    }

    private void SyncExtras(InsightsSnapshot s)
    {
        _gamma.IsVisible = s.RegimeTip != null;
        _whales.IsVisible = s.Whales != null;
        if (s.Whales is { } w)
            _whales.Text = $"Large traders (30 min, prints of {Compact(w.Threshold)}+): bought {Compact(w.Buy)}, sold {Compact(w.Sell)} in {w.Prints} prints · everyone else net {(w.OthersNet >= 0 ? "+" : "−")}{Compact(Math.Abs(w.OthersNet))}."
                + (s.WhaleVersusLean != null ? "\n" + s.WhaleVersusLean : "");
        _session.IsVisible = s.Session != null;
        if (s.Session is { } ss)
            _session.Text = $"1 min Δ {(ss.CandleDelta >= 0 ? "+" : "")}{ss.CandleDelta:0.##} · CVD {(ss.DeltaMoney >= 0 ? "+" : "−")}{Compact(Math.Abs(ss.DeltaMoney))} today · POC {(ss.PocPrice is { } p ? p.ToString("0.####", Fmt) : "–")}";
    }

    private void SyncPeers(IReadOnlyList<string>? peers, bool watching)
    {
        _peersHost.IsVisible = peers != null;
        if (peers == null) return;
        _syncing = true;
        if (_watch.IsChecked != watching) _watch.IsChecked = watching;
        _syncing = false;
        string sig = string.Join("\n", peers);
        if (sig == _peerSig) return;
        _peerSig = sig;
        _peerLines.Children.Clear();
        foreach (var line in peers)
            _peerLines.Children.Add(new TextBlock { Text = line, FontSize = 11.5, TextWrapping = TextWrapping.Wrap });
        if (peers.Count == 0) _peerLines.Children.Add(new TextBlock { Text = "Off: switch it on to see how the sibling markets are moving and who moves first.", FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
    }

    /// <summary>" · 4.0 bp below" for a wall's distance from the middle of the spread.</summary>
    private static string Away(decimal price, QuoteHeader q)
    {
        if (!q.HasBook) return "";
        decimal mid = (q.Bid + q.Ask) / 2, bp = Math.Abs(price - mid) / mid * 10_000m;
        return $" · {bp:0.0} bp {(price >= mid ? "above" : "below")}";
    }

    private static string WallsText(WallStats w, bool real)
    {
        string source = real ? "real orders" : "inferred from the book";
        if (w.BidCount == 0 && w.AskCount == 0) return $"Walls real ({source}): no big resting orders have left in the last 10 minutes.";
        string Side(string name, decimal? r, int n) => r is { } v ? $"{name} {v:P0} ({n})" : $"{name} too few ({n})";
        return $"Walls real ({source}): of the big orders that left in 10 min, the share that were traded through, not cancelled: {Side("bids", w.BidReal, w.BidCount)} · {Side("offers", w.AskReal, w.AskCount)}. Low = they mostly vanish before they are hit.";
    }

    private void SyncChecks(IReadOnlyList<Check> checks)
    {
        while (_rows.Count < checks.Count)
        {
            var name = new TextBlock { FontSize = 12 };
            var value = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, FontFamily = new FontFamily(Services.Plat.Mono) };
            var need = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Ui.Text2, MinWidth = 70, TextAlignment = TextAlignment.Right };
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
            Grid.SetColumn(value, 1);
            Grid.SetColumn(need, 2);
            g.Children.Add(name);
            g.Children.Add(value);
            g.Children.Add(need);
            _checks.Children.Add(g);
            _rows.Add((name, value, need));
        }

        for (int i = 0; i < _rows.Count; i++)
        {
            bool shown = i < checks.Count;
            ((Control)_checks.Children[i]).IsVisible = shown;
            if (!shown) continue;
            var c = checks[i];
            _rows[i].Name.Text = (c.Pass ? "✓  " : "✗  ") + c.Name;
            _rows[i].Name.Foreground = c.Pass ? Ui.Up : Ui.Down;
            _rows[i].Value.Text = c.Value;
            _rows[i].Value.Foreground = c.Pass ? Ui.Text0 : Ui.Down;
            _rows[i].Need.Text = c.Need;
        }
    }

    private void SyncChips(IReadOnlyList<Chip> chips)
    {
        string sig = string.Join("|", chips.Select(c => c.Tone + c.Text));
        if (sig == _chipSig) return;
        _chipSig = sig;
        _chips.Children.Clear();
        foreach (var c in chips)
        {
            var brush = c.Tone > 0 ? Ui.Up : c.Tone < 0 ? Ui.Down : Ui.Text2;
            _chips.Children.Add(new Border
            {
                Child = new TextBlock { Text = c.Text, FontSize = 11.5, Foreground = brush },
                BorderBrush = brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2),
                Margin = new Thickness(0, 0, 6, 6),
            });
        }
    }

    private void SyncLevels(IReadOnlyList<LevelLine> levels)
    {
        string sig = string.Join("|", levels.Select(l => $"{l.Label}{l.Price}"));
        if (sig == _levelSig) return;
        _levelSig = sig;
        _levelsHost.Children.Clear();
        if (levels.Count == 0) return;
        _levelsHost.Children.Add(Ui.H3("Levels"));
        foreach (var l in levels.OrderByDescending(l => l.Price))
        {
            var row = new DockPanel();
            if (l.Mine)
            {
                var remove = new Button { Content = "✕", Padding = new Thickness(6, 0), FontSize = 11, Background = Brushes.Transparent, Margin = new Thickness(6, 0, 0, 0) };
                decimal at = l.Price;
                remove.Click += (_, _) => MyLevelRemoved?.Invoke(at);
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
            }

            var price = new TextBlock { Text = l.Price.ToString("0.####", Fmt), FontSize = 12, FontFamily = new FontFamily(Services.Plat.Mono), Foreground = l.Tone > 0 ? Ui.Up : l.Tone < 0 ? Ui.Down : Ui.Text1 };
            DockPanel.SetDock(price, Dock.Right);
            row.Children.Add(price);
            row.Children.Add(new TextBlock { Text = l.Label, FontSize = 12, Foreground = Ui.Text1, TextTrimming = TextTrimming.CharacterEllipsis });
            _levelsHost.Children.Add(row);
        }
    }
}

/// <summary>A named price the panel lists (an options wall, a liquidation cluster...). <see cref="Tone"/>: +1 above-green, -1 red, 0 neutral.</summary>
public sealed record LevelLine(string Label, decimal Price, int Tone, bool Mine = false);
