using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using TradingHub.App.Services;
using TradingHub.Data;

namespace TradingHub.App.Windows;

/// <summary>
/// Profile flashcards: a past session's TPO letters and volume bars (the date is hidden), with a question from the auction method: the
/// shape of the day, what time against volume says at a marked price, or where the next session opens against this one's value.
/// Your score is kept per kind of question against the best constant guess, so you can see whether your reading beats a simple rule.
/// </summary>
internal sealed class ProfileFlashcardWindow : Window
{
    private sealed record Card(string Kind, string Question, string[] Options, int Answer, ResearchAuction.Sess Session, int MarkRow, string Explanation);

    private readonly List<ResearchAuction.Sess> _sessions = new();
    private decimal _rowSize;
    private readonly Random _rng = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _question = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _score = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Dictionary<string, double> _baseline = new();
    private Card? _card;

    private static string ScoreFile => Path.Combine(AppPaths.Root, "profile-flashcards.jsonl");

    public ProfileFlashcardWindow()
    {
        Title = "Profile flashcards";
        Width = 1000;
        Height = 800;
        Background = Ui.Bg2;
        DockPanel.SetDock(_question, Dock.Top);
        DockPanel.SetDock(_buttons, Dock.Bottom);
        DockPanel.SetDock(_score, Dock.Bottom);
        DockPanel.SetDock(_result, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(14), Children = { _question, _buttons, _result, _score, _image } };
        _question.Text = "Building the profiles from your recorded candles...";
        Opened += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await Task.Run(() =>
        {
            var series = ResearchHistory.Load().FirstOrDefault();
            if (series == null) return;
            _sessions.AddRange(ResearchAuction.Sessions(series, out _rowSize));
        });
        if (_sessions.Count < 60)
        {
            _question.Text = "Not enough cached candle history yet (open a chart with a long history first).";
            return;
        }

        // the best constant guess for each question: what you would score by always picking the commonest answer
        _baseline["shape"] = _sessions.GroupBy(s => s.R.Shape).Max(g => g.Count()) / (double)_sessions.Count;
        _baseline["case"] = 0.25;
        var opens = Enumerable.Range(1, _sessions.Count - 1).Select(i => OpenType(_sessions[i - 1], _sessions[i])).ToList();
        _baseline["open"] = opens.GroupBy(o => o).Max(g => g.Count()) / (double)opens.Count;
        NextCard();
    }

    private string OpenType(ResearchAuction.Sess prev, ResearchAuction.Sess cur) =>
        cur.Open > prev.R.VolumeVah + _rowSize ? "above" : cur.Open < prev.R.VolumeVal ? "below" : "inside";

    private void NextCard()
    {
        _result.Text = "";
        _buttons.Children.Clear();
        for (int attempt = 0; attempt < 50; attempt++)
        {
            int i = _rng.Next(1, _sessions.Count - 1);
            var s = _sessions[i];
            int kind = _rng.Next(3);
            if (kind == 0)
            {
                string[] options = { "D", "P", "b", "Trend" };
                _card = new Card("shape", "What shape is this profile? (D = balance, P = short covering, b = long liquidation, Trend = long and thin)", options, Array.IndexOf(options, s.R.Shape), s, -1,
                    $"{s.R.Shape}: {s.R.ShapeMeaning}.");
            }
            else if (kind == 1)
            {
                var want = (RowCase)_rng.Next(4);
                var candidates = Enumerable.Range(0, s.Input.Rows.Length).Where(r => s.R.Cases[r] == want).ToList();
                if (candidates.Count == 0) continue;
                int row = candidates[_rng.Next(candidates.Count)];
                string[] options = { "Regulars (time + volume)", "The rush (neither)", "The camper (time, little volume)", "The big spender (volume, little time)" };
                _card = new Card("case", "Reading time (letters) against volume (bars) at the marked price, what is it?", options, (int)want, s, row,
                    $"Letters {s.Input.Slots[row].Length} against a busiest row of {s.Input.Slots.Max(x => x.Length)}; volume {s.Input.Volume[row]:0} against a busiest row of {s.Input.Volume.Max():0}.");
            }
            else
            {
                var next = _sessions[i + 1];
                if (next.DayIndex - s.DayIndex > 3) continue;
                string[] options = { "inside", "above", "below" };
                _card = new Card("open", "The next session opens... relative to THIS session's volume value area?", options, Array.IndexOf(options, OpenType(s, next)), s, -1,
                    $"It opened {OpenType(s, next)} (value area {s.R.VolumeVal} - {s.R.VolumeVah + _rowSize}).");
            }

            break;
        }

        if (_card == null) return;
        _question.Text = _card.Question;
        for (int o = 0; o < _card.Options.Length; o++)
        {
            int chosen = o;
            _buttons.Children.Add(Ui.Button(_card.Options[o], Material.Icons.MaterialIconKind.ChevronRight, () => Answer(chosen)));
        }

        Draw(_card);
        ShowScore();
    }

    private void Answer(int choice)
    {
        if (_card == null) return;
        bool right = choice == _card.Answer;
        try
        {
            File.AppendAllText(ScoreFile, JsonSerializer.Serialize(new { time = DateTime.UtcNow, kind = _card.Kind, right }) + Environment.NewLine);
        }
        catch (Exception) { /* the score only lives on screen then */ }

        _result.Text = (right ? "Right. " : $"Wrong: it was {_card.Options[_card.Answer]}. ") + _card.Explanation;
        _buttons.Children.Clear();
        _buttons.Children.Add(Ui.Button("Next card", Material.Icons.MaterialIconKind.SkipNext, NextCard));
        ShowScore();
    }

    private void ShowScore()
    {
        var parts = new List<string>();
        try
        {
            var rows = File.Exists(ScoreFile) ? File.ReadLines(ScoreFile).Select(l => JsonDocument.Parse(l).RootElement).ToList() : new();
            foreach (var kind in new[] { "shape", "case", "open" })
            {
                var mine = rows.Where(r => r.GetProperty("kind").GetString() == kind).ToList();
                if (mine.Count == 0) continue;
                int right = mine.Count(r => r.GetProperty("right").GetBoolean());
                double baseline = _baseline.GetValueOrDefault(kind), p = right / (double)mine.Count;
                double z = (right - mine.Count * baseline) / Math.Sqrt(Math.Max(1e-9, mine.Count * baseline * (1 - baseline)));
                string name = kind == "shape" ? "shape" : kind == "case" ? "time against volume" : "next open";
                parts.Add($"{name}: {right}/{mine.Count} = {100 * p:0}% (always guessing the commonest answer scores {100 * baseline:0}%; {z:+0.0;-0.0;0.0} standard errors{(mine.Count < 40 ? ", too few cards yet" : "")})");
            }
        }
        catch (Exception) { /* a bad score file starts the count again */ }

        _score.Text = parts.Count == 0 ? "No cards answered yet." : "Your score — " + string.Join(" · ", parts);
    }

    private void Draw(Card c)
    {
        const int W = 940, H = 560, left = 20, barLeft = 520, barMax = 380, top = 14;
        var s = c.Session;
        int n = s.Input.Rows.Length;
        float rowH = Math.Min(16, (H - top - 10f) / n);
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(14, 16, 20));
        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), Math.Max(7, rowH * 0.85f));
        using var letter = new SKPaint { Color = new SKColor(150, 170, 200), IsAntialias = true };
        using var poc = new SKPaint { Color = new SKColor(255, 196, 0), IsAntialias = true };
        using var bar = new SKPaint { Color = new SKColor(200, 160, 90, 200) };
        decimal maxVol = Math.Max(1, s.Input.Volume.Max());
        for (int i = 0; i < n; i++)
        {
            int rowFromTop = n - 1 - i;
            float y = top + rowFromTop * rowH;
            string letters = string.Concat(s.Input.Slots[i].Select(TradingHub.Indicators.Native.TpoProfile.LetterOf));
            canvas.DrawText(letters, left, y + rowH * 0.85f, font, s.Input.Rows[i] == s.R.TpoPoc ? poc : letter);
            canvas.DrawRect(barLeft, y + 1, (float)(barMax * s.Input.Volume[i] / maxVol), Math.Max(1, rowH - 2), bar);
        }

        if (c.MarkRow >= 0)
        {
            float y = top + (n - 1 - c.MarkRow) * rowH;
            using var mark = new SKPaint { Color = new SKColor(255, 80, 120), Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
            canvas.DrawRect(left - 6, y, W - left - 14, rowH, mark);
            using var arrow = new SKPaint { Color = new SKColor(255, 80, 120), IsAntialias = true };
            canvas.DrawText("<-- this price", barLeft + barMax + 10, y + rowH * 0.85f, font, arrow);
        }

        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Png, 90);
        _image.Source = new Bitmap(new MemoryStream(data.ToArray()));
    }
}
