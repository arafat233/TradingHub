using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;

namespace TradingHub.App.Windows;

/// <summary>
/// Flashcards: a random moment from the recorded days, shown only up to that moment (candles, volume and the big-order levels that
/// existed and had not been touched). You call the next 30 minutes up, down or flat; it then shows what happened and keeps your score
/// against chance, so you find out whether your reading of the tape has an edge before you risk money on it.
/// </summary>
internal sealed class FlashcardWindow : Window
{
    private sealed record Bar(DateTime Time, long O, long H, long L, long C, decimal Volume);

    private sealed record Card(string Symbol, LevelTools.Ticks Ticks, int Index, List<Bar> Past, List<Bar> Future, List<(decimal Price, bool Buy, decimal Volume)> Levels, long Move, int Truth);

    private sealed record Source(string Symbol, LevelTools.Ticks Ticks, List<MinedLevel> Orders, long Threshold);

    private const int BarMinutes = 3, PastBars = 60, FutureBars = 10;
    private readonly List<Source> _sources = new();
    private readonly Random _rng = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _info = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _score = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _up, _down, _flat, _next, _ask;
    private byte[] _png = Array.Empty<byte>();
    private int? _claudeCall;          // Claude's call on the current card (hidden until the user has answered)
    private bool _asking;
    private int _claudeN, _claudeRight;
    private Card? _card;
    private int _n, _right;

    private static string ScoreFile => Path.Combine(AppPaths.Root, "flashcards.jsonl");

    public FlashcardWindow()
    {
        Title = "Flashcards";
        Width = 1000;
        Height = 760;
        Background = Ui.Bg2;
        _up = Ui.Button("Up", Material.Icons.MaterialIconKind.ArrowUpBold, () => Answer(1));
        _down = Ui.Button("Down", Material.Icons.MaterialIconKind.ArrowDownBold, () => Answer(-1));
        _flat = Ui.Button("Flat", Material.Icons.MaterialIconKind.MinusThick, () => Answer(0));
        _next = Ui.Button("Next card", Material.Icons.MaterialIconKind.SkipNext, NextCard);
        _ask = Ui.Button("Ask Claude too", Material.Icons.MaterialIconKind.Robot, async () => await AskClaudeAsync());
        ToolTip.SetTip(_ask, "Claude gets the same picture and makes its own call (hidden until you answer), so you can compare. It uses a little of your Claude allowance and sends the card's picture to Claude Code.");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0), Children = { _up, _down, _flat, _next, _ask } };
        DockPanel.SetDock(_info, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_score, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(14), Children = { _info, buttons, _score, _image } };
        SetButtons(false);
        _info.Text = "Loading the recorded ticks (up to a minute)...";
        Opened += async (_, _) => await LoadAsync();
    }

    private void SetButtons(bool canAnswer)
    {
        _up.IsEnabled = _down.IsEnabled = _flat.IsEnabled = canAnswer;
        _ask.IsEnabled = canAnswer && !_asking && _claudeCall == null;
        _next.IsEnabled = !canAnswer && _sources.Count > 0;
    }

    private async Task LoadAsync()
    {
        await Task.Run(() =>
        {
            foreach (var (feed, symbol) in LevelTools.Instruments())
            {
                var t = LevelTools.Load(feed, symbol);
                if (t == null || !t.TicksMeaningful || t.Times.Length < 5000) continue;
                var orders = LevelMiner.FindOrders(t.Trades, t.Tick);
                // "flat" is the smaller third of 30-minute moves, so the three answers are about equally likely by chance
                var rng = new Random(7);
                var moves = new List<long>();
                for (int i = 0; i < 400; i++)
                {
                    int a = rng.Next(0, t.Times.Length);
                    int b = Array.BinarySearch(t.Times, t.Times[a] + TimeSpan.FromMinutes(30).Ticks);
                    if (b < 0) b = ~b;
                    if (b < t.Times.Length) moves.Add(Math.Abs(t.Prices[b] - t.Prices[a]));
                }

                moves.Sort();
                _sources.Add(new Source(symbol, t, orders, Math.Max(1, moves.Count == 0 ? 1 : moves[moves.Count / 3])));
            }
        });
        LoadScore();
        if (_sources.Count == 0)
        {
            _info.Text = "No recorded ticks with a usable tick size yet. Let the app record a few days (or use Mine levels) and come back.";
            return;
        }

        NextCard();
    }

    private void LoadScore()
    {
        try
        {
            if (!File.Exists(ScoreFile)) return;
            foreach (var line in File.ReadLines(ScoreFile))
            {
                using var d = JsonDocument.Parse(line);
                _n++;
                if (d.RootElement.GetProperty("right").GetBoolean()) _right++;
            }
        }
        catch (Exception) { /* a bad score file starts the count again */ }

        ShowScore();
    }

    private void ShowScore()
    {
        if (_n == 0) { _score.Text = "No cards answered yet. By chance you would be right about 1 time in 3."; return; }
        double p = _right / (double)_n, z = (_right - _n / 3.0) / Math.Sqrt(_n * (1 / 3.0) * (2 / 3.0));
        _score.Text = $"All your cards: {_right} right of {_n} = {100 * p:0}% (chance is about 33%; difference {z:+0.0;-0.0;0.0} standard errors). "
                      + (_n < 60 ? "Fewer than 60 cards says almost nothing: keep going." : Math.Abs(z) >= 2 ? (z > 0 ? "That is unlikely to be luck." : "That is worse than chance.") : "That is within what luck produces.")
                      + (_claudeN > 0 ? $"  Claude on the cards it was asked: {_claudeRight} right of {_claudeN} = {100.0 * _claudeRight / _claudeN:0}%." : "");
    }

    private void NextCard()
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            var s = _sources[_rng.Next(_sources.Count)];
            var t = s.Ticks;
            long lo = t.Times[0] + TimeSpan.FromHours(4).Ticks, hi = t.Times[^1] - TimeSpan.FromMinutes(40).Ticks;
            if (hi <= lo) continue;
            long when = lo + (long)(_rng.NextDouble() * (hi - lo));
            int idx = Array.BinarySearch(t.Times, when);
            if (idx < 0) idx = ~idx;
            if (idx >= t.Times.Length) continue;
            var past = Bars(t, idx, -PastBars * BarMinutes, BarMinutes);
            if (past.Count < PastBars * 2 / 3) continue; // a quiet stretch: skip it
            var future = Bars(t, idx, FutureBars * BarMinutes, BarMinutes, forward: true);
            if (future.Count == 0) continue;
            int end = Array.BinarySearch(t.Times, t.Times[idx] + TimeSpan.FromMinutes(30).Ticks);
            if (end < 0) end = ~end;
            if (end >= t.Times.Length) continue;
            long move = t.Prices[end] - t.Prices[idx];
            int truth = Math.Abs(move) <= s.Threshold ? 0 : Math.Sign(move);

            // the levels as they were known then: big orders of the previous 6 hours that price had not come back to by that moment
            var levels = new List<(decimal, bool, decimal)>();
            var now = new DateTime(t.Times[idx], DateTimeKind.Utc);
            foreach (var o in s.Orders.Where(o => o.Time <= now && o.Time >= now.AddHours(-6)))
            {
                var trace = LevelMiner.Trace(t.Times, t.Prices, o, t.Tick, 4, new[] { 8 }, TimeSpan.FromHours(7), TimeSpan.FromMinutes(1));
                if (trace == null || trace.TouchTime > now) levels.Add((o.Price, o.Buy, o.Volume));
            }

            _card = new Card(s.Symbol, t, idx, past, future, levels.OrderByDescending(l => l.Item3).Take(12).ToList(), move, truth);
            _claudeCall = null;
            _info.Text = $"{s.Symbol} · {now.ToLocalTime():dddd dd/MM/yyyy HH:mm} local · 3-minute candles of the 3 hours before this moment, volume below, and the big-order levels not yet touched (blue = buy, red = sell). What will price do over the next 30 minutes?";
            Draw(false);
            SetButtons(true);
            return;
        }

        _info.Text = "Could not find a moment with enough activity. Record more days.";
    }

    private static List<Bar> Bars(LevelTools.Ticks t, int idx, int minutes, int barMinutes, bool forward = false)
    {
        long start = t.Times[idx] + (forward ? 0 : TimeSpan.FromMinutes(minutes).Ticks);
        long end = t.Times[idx] + (forward ? TimeSpan.FromMinutes(minutes).Ticks : 0);
        int i = Array.BinarySearch(t.Times, start);
        if (i < 0) i = ~i;
        var bars = new List<Bar>();
        long bucket = long.MinValue;
        for (; i < t.Times.Length && t.Times[i] <= end; i++)
        {
            long b = t.Times[i] / (TimeSpan.TicksPerMinute * barMinutes);
            long p = t.Prices[i];
            decimal v = t.Trades[i].Volume;
            if (b != bucket)
            {
                bars.Add(new Bar(new DateTime(b * TimeSpan.TicksPerMinute * barMinutes, DateTimeKind.Utc), p, p, p, p, v));
                bucket = b;
            }
            else
            {
                var x = bars[^1];
                bars[^1] = x with { H = Math.Max(x.H, p), L = Math.Min(x.L, p), C = p, Volume = x.Volume + v };
            }
        }

        return bars;
    }

    private void Answer(int choice)
    {
        if (_card == null) return;
        bool right = choice == _card.Truth;
        _n++;
        if (right) _right++;
        try
        {
            File.AppendAllText(ScoreFile, JsonSerializer.Serialize(new { time = DateTime.UtcNow, symbol = _card.Symbol, at = new DateTime(_card.Ticks.Times[_card.Index], DateTimeKind.Utc), choice, truth = _card.Truth, right }) + Environment.NewLine);
        }
        catch (Exception) { /* the score only lives in memory then */ }

        string Name(int v) => v > 0 ? "UP" : v < 0 ? "DOWN" : "FLAT";
        string claude = "";
        if (_claudeCall is { } cc)
        {
            _claudeN++;
            if (cc == _card.Truth) _claudeRight++;
            claude = $" Claude said {Name(cc)}: {(cc == _card.Truth ? "right" : "wrong")}.";
            try
            {
                File.AppendAllText(Path.Combine(AppPaths.Root, "flashcards-claude.jsonl"), JsonSerializer.Serialize(new { time = DateTime.UtcNow, symbol = _card.Symbol, call = cc, truth = _card.Truth, right = cc == _card.Truth }) + Environment.NewLine);
            }
            catch (Exception) { /* the tally only lives in memory then */ }
        }
        _info.Text = $"You said {Name(choice)}. In the next 30 minutes {_card.Symbol} moved {_card.Move * (double)_card.Ticks.Tick:+0.##;-0.##;0} ({_card.Move:+0;-0;0} ticks): {Name(_card.Truth)}"
                     + $" (flat = a move of {_sources.First(s => s.Symbol == _card.Symbol).Threshold} ticks or less). {(right ? "Right." : "Wrong.")}{claude}";
        Draw(true);
        ShowScore();
        SetButtons(false);
    }

    private void Draw(bool reveal)
    {
        var c = _card!;
        const int W = 940, H = 520, left = 8, right = 70, top = 18, volH = 70;
        int slots = PastBars + FutureBars;
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(14, 16, 20));
        var all = reveal ? c.Past.Concat(c.Future).ToList() : c.Past;
        long lo = all.Min(b => b.L), hi = all.Max(b => b.H);
        if (hi == lo) hi = lo + 1;
        long pad = Math.Max(2, (hi - lo) / 12);
        lo -= pad; hi += pad;
        float plotW = W - left - right, plotH = H - top - volH - 20, slot = plotW / slots;
        float Y(decimal ticks) => top + plotH * (1 - (float)((ticks - lo) / (hi - lo)));
        using var font = new SKFont(SKTypeface.Default, 12);
        using var text = new SKPaint { Color = new SKColor(180, 186, 196), IsAntialias = true };
        using var line = new SKPaint { Color = new SKColor(40, 44, 52), StrokeWidth = 1 };
        for (int g = 0; g <= 5; g++)
        {
            long price = lo + (hi - lo) * g / 5;
            float y = Y(price);
            canvas.DrawLine(left, y, W - right, y, line);
            canvas.DrawText(((decimal)price * c.Ticks.Tick).ToString("0.##"), W - right + 6, y + 4, font, text);
        }

        // levels as known at that moment
        foreach (var (price, buy, vol) in c.Levels)
        {
            long ticks = (long)Math.Round(price / c.Ticks.Tick);
            if (ticks < lo || ticks > hi) continue;
            using var lp = new SKPaint { Color = buy ? new SKColor(60, 140, 230, 200) : new SKColor(230, 80, 80, 200), StrokeWidth = 1.5f, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 4f }, 0) };
            canvas.DrawLine(left, Y(ticks), W - right, Y(ticks), lp);
            using var lt = new SKPaint { Color = lp.Color, IsAntialias = true };
            canvas.DrawText($"{(buy ? "B" : "S")} {vol:0.#}", left + 4, Y(ticks) - 3, font, lt);
        }

        decimal maxVol = Math.Max(1, all.Max(b => b.Volume));
        void DrawBar(Bar b, int slotIndex, bool future)
        {
            float x = left + slot * slotIndex + slot / 2;
            bool up = b.C >= b.O;
            var col = future ? (up ? new SKColor(110, 210, 150) : new SKColor(240, 130, 130)) : (up ? new SKColor(40, 170, 110) : new SKColor(220, 70, 70));
            using var p = new SKPaint { Color = col, StrokeWidth = 1.5f, IsAntialias = true };
            canvas.DrawLine(x, Y(b.H), x, Y(b.L), p);
            float yo = Y(b.O), yc = Y(b.C);
            canvas.DrawRect(x - slot * 0.35f, Math.Min(yo, yc), slot * 0.7f, Math.Max(1.5f, Math.Abs(yo - yc)), p);
            float vh = (float)(b.Volume / maxVol) * volH;
            using var vp = new SKPaint { Color = col.WithAlpha(120) };
            canvas.DrawRect(x - slot * 0.35f, H - 20 - vh, slot * 0.7f, vh, vp);
        }

        for (int i = 0; i < c.Past.Count; i++) DrawBar(c.Past[i], PastBars - c.Past.Count + i, false);
        float nowX = left + slot * PastBars;
        using var nowPen = new SKPaint { Color = new SKColor(255, 200, 60), StrokeWidth = 1.5f, PathEffect = SKPathEffect.CreateDash(new[] { 3f, 3f }, 0) };
        canvas.DrawLine(nowX, top, nowX, H - 20, nowPen);
        canvas.DrawText("now", nowX + 4, top + 12, font, text);
        if (reveal)
            foreach (var b in c.Future)
            {
                int s = (int)Math.Round((b.Time - c.Past[^1].Time).TotalMinutes / BarMinutes);
                DrawBar(b, PastBars - 1 + Math.Clamp(s, 1, FutureBars), true);
            }

        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Png, 90);
        _png = data.ToArray();
        _image.Source = new Bitmap(new MemoryStream(_png));
    }

    /// <summary>Claude gets the picture of the card (never the answer) and replies with one word. The call stays hidden until the user answers.</summary>
    private async Task AskClaudeAsync()
    {
        if (_card == null || _asking || _png.Length == 0) return;
        _asking = true;
        SetButtons(true);
        var before = _info.Text;
        _info.Text = "Asking Claude (a few seconds)... you can answer without waiting; its call is shown after you answer.";
        try
        {
            var dir = Path.Combine(AppPaths.Root, "ask-chart");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"flashcard-{Guid.NewGuid():N}.png");
            await File.WriteAllBytesAsync(file, _png);
            long threshold = _sources.First(s => s.Symbol == _card.Symbol).Threshold;
            var res = await new ClaudeAgent().RunAsync(new ClaudeAgentRequest
            {
                Role = "flashcard",
                Prompt = $"Look at the chart picture {Path.GetFileName(file)} in your folder (use the Read tool). It shows 3-minute candles of the 3 hours before a moment marked 'now' (the yellow dashed line), volume below, and dashed horizontal lines for large market orders that price has not come back to (blue = buy, red = sell). Predict what price does over the next 30 minutes: UP, DOWN, or FLAT (FLAT means it ends within {threshold} ticks of where it is now). Reply with exactly one word: UP, DOWN or FLAT. No explanation.",
                SystemPrompt = "You answer with exactly one word: UP, DOWN or FLAT.",
                WorkDir = dir,
                Tools = new[] { "Read" },
                PermissionMode = "dontAsk",
                Model = ClaudeCodeBridge.Model,
                Effort = "low",
                MaxUsd = 0.3,
                Timeout = TimeSpan.FromMinutes(2),
            }, CancellationToken.None);
            string t = (res.Text ?? "").ToUpperInvariant();
            _claudeCall = t.Contains("DOWN") ? -1 : t.Contains("UP") ? 1 : t.Contains("FLAT") ? 0 : null;
            _info.Text = _claudeCall == null ? "Claude did not give a usable answer; carry on without it. " + before : "Claude has made its call. " + before;
            try { File.Delete(file); } catch (Exception) { /* a leftover picture is harmless */ }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            _info.Text = "Could not ask Claude. " + before;
        }
        finally
        {
            _asking = false;
            if (_card != null && _up.IsEnabled) SetButtons(true);
        }
    }
}
