using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using TradingHub.App.Services;

namespace TradingHub.App.Views;

/// <summary>Claude Code panel inside the Code tab: you ask for strategies and see the code in the editor.</summary>
internal sealed partial class StrategiesView
{
    private readonly ClaudeCodeBridge _claude = new();
    private readonly StackPanel _chat = new() { Spacing = 10, Margin = new Thickness(14, 12) };
    private readonly ScrollViewer _chatScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBox _prompt = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 64,
        MaxHeight = 160,
        Watermark = "Ask Claude for a new strategy or a change to the open one… (Win+H dictates by voice)",
    };
    private readonly TextBlock _claudeStatus = new() { Classes = { "faint", "small" }, TextTrimming = TextTrimming.CharacterEllipsis };
    private Button _sendButton = null!;
    private Button _stopButton2 = null!;
    private SelectableTextBlock? _currentReply;
    private Dictionary<string, DateTime> _filesBefore = new();
    private Dictionary<string, DateTime> _indicatorsBefore = new();
    private DateTime _requestStartedUtc;
    private bool _gotReplyText;
    private DateTime _loadedFileTime;
    private readonly ToggleButton _labToggle = new();
    private readonly TextBlock _claudeHint = new() { Classes = { "faint", "small" }, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private HashSet<string> _reportsBefore = new();

    // current conversation (stored in the history) and the chat's separate window
    private ChatConversation _conv = new();
    private Grid? _claudeGrid;
    private GridSplitter? _claudeSplit;
    private Control? _claudePanel;
    private Window? _claudeWindow;
    private Button? _claudeDetach;

    private Control BuildClaudePanel()
    {
        var newChat = Ui.IconButton(MaterialIconKind.ChatPlusOutline, "New conversation", () =>
        {
            if (_claude.IsRunning) return;
            _claude.NewConversation();
            _conv = new ChatConversation();
            _chat.Children.Clear();
            ShowSuggestions();
        }, 17);
        var history = new Button { Content = Ui.Icon(MaterialIconKind.History, 17) }.Classes("icon");
        ToolTip.SetTip(history, "Conversation history (they can be picked up where they left off)");
        history.Click += (_, _) => ShowHistoryMenu(history);
        _claudeDetach = Ui.IconButton(MaterialIconKind.OpenInNew, "Pull the chat into a separate window", ToggleClaudeWindow, 17);
        var title = Ui.V(1, Ui.H(8, Ui.Icon(MaterialIconKind.Creation, 18, Ui.Accent), new TextBlock { Text = "Claude", FontWeight = FontWeight.SemiBold, FontSize = 14 }), _claudeStatus);
        // lab: Claude runs backtests, optimizations and walk-forwards on its own
        _labToggle.Content = Ui.H(6, Ui.Icon(MaterialIconKind.FlaskOutline, 15), new TextBlock { Text = "Lab", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _labToggle.Padding = new Thickness(8, 4);
        ToolTip.SetTip(_labToggle, "Lab mode: Claude can download data, run backtests, optimizations, walk-forwards and Monte Carlo on its own, decide whether the strategy is a winner or a loser and leave a report in the Reports tab.");
        _labToggle.IsCheckedChanged += (_, _) => UpdateClaudeHint();
        var headRight = Ui.H(6, _labToggle, history, newChat, _claudeDetach);
        var head = new DockPanel { Margin = new Thickness(14, 10, 10, 10) };
        DockPanel.SetDock(headRight, Dock.Right);
        head.Children.Add(headRight);
        head.Children.Add(title);

        _sendButton = Ui.Button("Send", MaterialIconKind.Send, () => _ = SendToClaudeAsync(_prompt.Text ?? ""), "primary");
        ToolTip.SetTip(_sendButton, Plat.Keys(Plat.Cmd, "Enter"));
        _stopButton2 = Ui.Button("Stop", MaterialIconKind.Stop, () => _claude.Cancel());
        _stopButton2.IsVisible = false;
        _prompt.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(Plat.Cmd))
            {
                _ = SendToClaudeAsync(_prompt.Text ?? "");
                e.Handled = true;
            }
        };
        var actions = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var right = Ui.H(8, _stopButton2, _sendButton);
        DockPanel.SetDock(right, Dock.Right);
        actions.Children.Add(right);
        UpdateClaudeHint();
        actions.Children.Add(_claudeHint);
        var input = new Border { Child = Ui.V(0, _prompt, actions), Padding = new Thickness(12), BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 1, 0, 0) };

        _chatScroll.Content = _chat;
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(new Border { Child = head, BorderBrush = Ui.Line, BorderThickness = new Thickness(0, 0, 0, 1) }.Also(b => DockPanel.SetDock(b, Dock.Top)));
        DockPanel.SetDock(input, Dock.Bottom);
        root.Children.Add(input);
        root.Children.Add(_chatScroll);

        _claude.Text += OnClaudeText;
        _claude.ToolUsed += OnClaudeTool;
        _claude.ToolFinished += ReloadIfChangedOnDisk;
        _claude.Finished += r => _ = OnClaudeFinishedAsync(r);
        _ = DetectClaudeAsync();
        ShowSuggestions();
        return new Border { Child = root, Background = Ui.Bg1, BorderBrush = Ui.Line, BorderThickness = new Thickness(1, 0, 0, 0) };
    }

    private void UpdateClaudeHint()
    {
        bool lab = _labToggle.IsChecked == true;
        _claudeHint.Text = lab
            ? "Lab: Claude runs the tests on its own (they can take several minutes) and stores a report in Reports."
            : "It uses your Claude Code (subscription). It can only read and edit files in the strategies folder.";
        _claudeHint.Foreground = lab ? Ui.Accent : null;
        _prompt.Watermark = lab ? "e.g. research and optimize the open strategy with 60 days of BTCUSDT and tell me whether it is a winner" : "Ask for a strategy or a change…";
    }

    private async Task DetectClaudeAsync()
    {
        var v = await ClaudeCodeBridge.VersionAsync();
        _claudeStatus.Text = v != null ? $"Claude Code {v.Replace("(Claude Code)", "").Trim()} · your subscription" : "Claude Code is not installed";
        _sendButton.IsEnabled = v != null;
        if (v == null)
            AddNote(MaterialIconKind.AlertCircleOutline, "Claude Code was not found on this Mac. Install it (claude.com/claude-code), sign in with your account and open Trading Hub again.", Ui.Warn);
    }

    private void ShowSuggestions()
    {
        if (_chat.Children.Count > 0) return;
        var intro = new TextBlock
        {
            Text = "Describe the strategy you want and Claude writes it here: you will see the code appear in the editor and Trading Hub compiles it when it finishes.",
            Classes = { "muted", "small" },
            TextWrapping = TextWrapping.Wrap,
        };
        var chips = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        foreach (var (s, lab) in new[]
                 {
                     ("Research and optimize the open strategy: backtest, optimization, walk-forward and Monte Carlo, and build the report with the verdict", true),
                     ("Review the live book and propose size changes (raise the ones performing, lower the ones that are not)", true),
                     ("Interview me about how I trade: ask me five questions one at a time, using my journal (list_accounts, trade_autopsy) as evidence, then write my playbook as playbook.md in the strategies folder and test each rule on my past trades, saying honestly which rules show an edge and which are just a story", true),
                     ("Test an order-flow idea of mine on my stored ticks with event_study (lean, stacked imbalances, absorption, big trades or delta divergence): judge only on the test rows after the multiple-testing correction, write it as a strategy and run the full evaluation only if it survives, and say plainly if nothing worked", true),
                     ("Check what my fills really cost with fill_quality and tell me whether the costs my backtests assume are realistic", true),
                     ("Build an indicator for me that marks where price came back to the previous day's high or low, with an alert, and tell me when it is on the chart", false),
                     ("Create a strategy that trades the breakout of the first 30 minutes' range", false),
                     ("Explain what the open strategy does", false),
                     ("Add a filter so it only trades between 10 and 17", false),
                     ("Improve the risk management with an ATR stop", false),
                 })
        {
            var content = lab
                ? (Control)Ui.H(6, Ui.Icon(MaterialIconKind.FlaskOutline, 14, Ui.Accent), new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxWidth = 300 })
                : new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
            var b = new Button { Content = content, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6) }.Classes("ghost");
            b.Click += (_, _) =>
            {
                if (lab) _labToggle.IsChecked = true;
                _prompt.Text = s;
                _prompt.Focus();
            };
            chips.Children.Add(b);
        }

        _chat.Children.Add(Ui.V(10, intro, chips));
    }

    private void AddBubble(string text, bool user)
    {
        if (_chat.Children.Count == 1 && _chat.Children[0] is StackPanel { Children.Count: 2 } sp && sp.Children[1] is WrapPanel) _chat.Children.Clear();
        var tb = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        var bubble = new Border
        {
            Child = tb,
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(10),
            Background = user ? Ui.Bg3 : Ui.Bg2,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            Margin = user ? new Thickness(40, 0, 0, 0) : new Thickness(0),
        };
        _chat.Children.Add(bubble);
        if (!user) _currentReply = tb;
        ScrollChatToEnd();
    }

    private void AddNote(MaterialIconKind icon, string text, IBrush? color = null)
    {
        var row = Ui.H(6, Ui.Icon(icon, 14, color ?? Ui.Text2), new TextBlock { Text = text, Classes = { "small" }, Foreground = color ?? Ui.Text2, TextWrapping = TextWrapping.Wrap, MaxWidth = 330 });
        _chat.Children.Add(row);
        ScrollChatToEnd();
    }

    private void ScrollChatToEnd() => Dispatcher.UIThread.Post(() => _chatScroll.ScrollToEnd(), DispatcherPriority.Background);

    private async Task SendToClaudeAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || _claude.IsRunning) return;
        if (_editor.IsDirty) Save();
        _prompt.Text = "";
        AddBubble(text, user: true);
        if (_conv.Messages.Count == 0)
        {
            _conv.Title = text.Length > 70 ? text[..70].TrimEnd() + "…" : text;
            _conv.Lab = _labToggle.IsChecked == true;
            _conv.Strategy = _current != null ? Path.GetFileName(_current.Path) : null;
        }

        _conv.Messages.Add(new ChatMessage { Role = "user", Text = text });
        ChatHistory.Save(_conv);
        _currentReply = null;
        _gotReplyText = false;
        _filesBefore = Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").ToDictionary(f => f, File.GetLastWriteTimeUtc);
        Directory.CreateDirectory(AppPaths.UserIndicators);
        _indicatorsBefore = Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);
        _requestStartedUtc = DateTime.UtcNow;
        _loadedFileTime = _current != null && File.Exists(_current.Path) ? File.GetLastWriteTimeUtc(_current.Path) : default;

        // context: open file and current errors
        var context = new List<string>();
        if (_current != null) context.Add($"File open in the editor: {Path.GetFileName(_current.Path)}");
        context.Add($"Indicators folder (for indicators, not strategies): {AppPaths.UserIndicators}");
        var errors = _current?.LastCompile?.Diagnostics.Where(d => d.Severity == "error").Take(15).ToList();
        if (errors is { Count: > 0 }) context.Add("Current compilation errors:\n" + string.Join('\n', errors.Select(d => $"- Line {d.Line}: {d.Message} ({d.Id})")));
        string prompt = context.Count > 0 ? $"[Trading Hub context]\n{string.Join("\n", context)}\n\n[Request]\n{text}" : text;

        _sendButton.IsVisible = false;
        _stopButton2.IsVisible = true;
        _editor.IsReadOnly = true;
        bool lab = _labToggle.IsChecked == true;
        _reportsBefore = Lab.ReportStore.List().Select(r => r.Path).ToHashSet();
        AddNote(MaterialIconKind.ProgressClock, "Claude is working…");
        await _claude.SendAsync(prompt, AppPaths.Strategies, ClaudeCodeBridge.StrategySystemPrompt + ClaudeCodeBridge.IndicatorSystemPrompt + (lab ? ClaudeCodeBridge.LabSystemPrompt : ""), lab,
            new[] { AppPaths.UserIndicators, Directory.CreateDirectory(Lab.LabServer.CapturesDir).FullName });
    }

    /// <summary>The chart snapshots Claude looked at in this request, in the chat (click one and it opens large).</summary>
    private void ShowSnapshots()
    {
        var dir = Lab.LabServer.CapturesDir;
        if (!Directory.Exists(dir)) return;
        foreach (var f in new DirectoryInfo(dir).GetFiles("*.png").Where(f => f.LastWriteTimeUtc >= _requestStartedUtc).OrderBy(f => f.LastWriteTimeUtc).Take(4))
        {
            try
            {
                var bmp = new Avalonia.Media.Imaging.Bitmap(f.FullName);
                var img = new Image { Source = bmp, MaxWidth = 420, Stretch = Stretch.Uniform, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
                var path = f.FullName;
                img.PointerReleased += (_, _) => TradingHub.App.Windows.Shell.Open(path);
                ToolTip.SetTip(img, "What Claude looked at (click to see it large)");
                _chat.Children.Add(Ui.V(6, Ui.H(8, Ui.Icon(MaterialIconKind.ChartBoxOutline, 15, Ui.Accent), new TextBlock { Text = "What it looked at on the chart", Classes = { "small" } }),
                    new Border { Child = img, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left }));
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex);
            }
        }
    }

    /// <summary>Indicators Claude created or changed in this request: it compiles them and leaves the result in the chat.</summary>
    private void ReportIndicators()
    {
        if (!Directory.Exists(AppPaths.UserIndicators)) return;
        var changed = Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories)
            .Where(f => !_indicatorsBefore.TryGetValue(f, out var t) || File.GetLastWriteTimeUtc(f) > t).ToList();
        if (changed.Count == 0 || _svc.Scripts is not { } scripts) return;
        var res = scripts.CompileAll();
        string names = string.Join(", ", changed.Select(Path.GetFileNameWithoutExtension));
        var errors = res?.Diagnostics.Where(d => d.Severity == "error" && changed.Any(f => string.IsNullOrEmpty(d.File) || Path.GetFullPath(d.File) == Path.GetFullPath(f))).ToList() ?? new();
        var open = Ui.Button("Open in the indicator editor", MaterialIconKind.CodeBraces, () =>
        {
            if (TopLevel.GetTopLevel(this) is Window w) new Windows.CodeEditorWindow(_svc, Windows.CodeKind.Indicator).Show(w);
        });
        open.Padding = new Thickness(10, 4);
        if (res?.Success == true)
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CheckCircle, 15, Ui.Up),
                new TextBlock { Text = $"Indicator {names} compiled: it is already on the chart under Indicators → “My indicators”", Classes = { "small" }, Foreground = Ui.Up, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }), open));
        else
        {
            string detail = string.Join("\n", (errors.Count > 0 ? errors : res?.Diagnostics.Where(d => d.Severity == "error").ToList() ?? new()).Take(12)
                .Select(d => $"- {Path.GetFileName(d.File)} line {d.Line}: {d.Message} ({d.Id})"));
            var fix = Ui.Button("Ask it to fix them", MaterialIconKind.AutoFix, () => _ = SendToClaudeAsync($"Fix the compilation errors in the indicator {names} (in the indicators folder):\n{detail}"), "primary");
            fix.Padding = new Thickness(10, 4);
            int count = errors.Count > 0 ? errors.Count : res?.Diagnostics.Count(d => d.Severity == "error") ?? 0;
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CloseCircle, 15, Ui.Down),
                new TextBlock { Text = $"The indicator {names} has {count} compilation error(s)", Classes = { "small" }, Foreground = Ui.Down, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }), Ui.H(8, fix, open)));
        }
    }

    private void OnClaudeText(string chunk)
    {
        if (_conv.Messages.LastOrDefault() is { Role: "assistant" } last && _gotReplyText) last.Text += chunk;
        else _conv.Messages.Add(new ChatMessage { Role = "assistant", Text = chunk });
        _gotReplyText = true;
        RemoveWorkingNote();
        if (_currentReply == null) AddBubble(chunk, user: false);
        else _currentReply.Text += chunk;
        ScrollChatToEnd();
    }

    private void OnClaudeTool(string tool, string? file)
    {
        RemoveWorkingNote();
        _currentReply = null; // whatever it writes next goes in another bubble
        if (tool.StartsWith("mcp__tradinghub__", StringComparison.Ordinal))
        {
            var (labIcon, labVerb) = tool["mcp__tradinghub__".Length..] switch
            {
                "list_strategies" => (MaterialIconKind.RobotOutline, "Reviewing the strategies"),
                "compile_strategy" => (MaterialIconKind.HammerWrench, "Compiling"),
                "compile_indicator" => (MaterialIconKind.HammerWrench, "Compiling the indicator"),
                "chart_snapshot" => (MaterialIconKind.ChartBoxOutline, "Looking at the chart"),
                "list_indicators" => (MaterialIconKind.ChartBellCurveCumulative, "Reviewing your indicators"),
                "list_data" => (MaterialIconKind.DatabaseOutline, "Looking at the stored data"),
                "download_data" => (MaterialIconKind.Download, "Downloading data"),
                "run_backtest" => (MaterialIconKind.PlayOutline, "Backtest"),
                "run_optimization" => (MaterialIconKind.Tune, "Optimizing parameters"),
                "run_walk_forward" => (MaterialIconKind.ChartTimelineVariant, "Walk-forward"),
                "list_runs" => (MaterialIconKind.History, "Reviewing earlier runs"),
                "analyze_portfolio" => (MaterialIconKind.ChartTimelineVariant, "Analyzing the portfolio"),
                "suggest_portfolio" => (MaterialIconKind.ScaleBalance, "Looking for uncorrelated strategies"),
                "live_portfolio" => (MaterialIconKind.Pulse, "Reviewing the live book"),
                "propose_sizing" => (MaterialIconKind.ScaleBalance, "Leaving size proposals (you approve them)"),
                "save_report" => (MaterialIconKind.FileDocumentOutline, "Saving the report"),
                var other => (MaterialIconKind.FlaskOutline, other),
            };
            AddNote(labIcon, string.IsNullOrEmpty(file) ? labVerb : $"{labVerb} · {file}", Ui.Accent);
            return;
        }

        string name = file != null ? Path.GetFileName(file) : "";
        var (icon, verb) = tool switch
        {
            "Write" => (MaterialIconKind.FilePlusOutline, "Writing"),
            "Edit" or "MultiEdit" => (MaterialIconKind.FileEditOutline, "Editing"),
            "Read" => (MaterialIconKind.FileEyeOutline, "Reading"),
            _ => (MaterialIconKind.FolderSearchOutline, "Searching"),
        };
        AddNote(icon, $"{verb} {name}".Trim());
    }

    private void RemoveWorkingNote()
    {
        var note = _chat.Children.OfType<StackPanel>().LastOrDefault(p => p.Children.Count == 2 && p.Children[1] is TextBlock { Text: "Claude is working…" });
        if (note != null) _chat.Children.Remove(note);
    }

    /// <summary>If Claude changed the open file, it shows up instantly in the editor.</summary>
    private void ReloadIfChangedOnDisk()
    {
        if (_current == null || !File.Exists(_current.Path)) return;
        var t = File.GetLastWriteTimeUtc(_current.Path);
        if (t == _loadedFileTime) return;
        _loadedFileTime = t;
        _editor.Text = File.ReadAllText(_current.Path);
        UpdateFileInfo();
    }

    private async Task OnClaudeFinishedAsync(ClaudeResult r)
    {
        _conv.SessionId = _claude.SessionId ?? _conv.SessionId;
        if (r.IsError) _conv.Messages.Add(new ChatMessage { Role = "note", Text = r.NeedsLogin ? "Claude Code asked you to sign in." : r.Text.Length > 400 ? r.Text[..400] + "…" : r.Text });
        else if (!_gotReplyText && !string.IsNullOrWhiteSpace(r.Text)) _conv.Messages.Add(new ChatMessage { Role = "assistant", Text = r.Text });
        ChatHistory.Save(_conv);
        RemoveWorkingNote();
        _sendButton.IsVisible = true;
        _stopButton2.IsVisible = false;
        _editor.IsReadOnly = false;
        if (r.IsError)
        {
            if (r.NeedsLogin)
            {
                var login = Ui.Button("Sign in to Claude Code", MaterialIconKind.Login, ClaudeCodeBridge.OpenLoginTerminal, "primary");
                var card = Ui.Card(Ui.V(8,
                    new TextBlock { Text = "Claude Code needs you to sign in", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Terminal opens with Claude Code: choose your Claude account (subscription) and follow the steps in the browser. Then come back here and repeat the request.", Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap },
                    login), 12);
                _chat.Children.Add(card);
            }
            else
            {
                AddNote(MaterialIconKind.AlertCircleOutline, r.Text.Length > 400 ? r.Text[..400] + "…" : r.Text, Ui.Down);
            }

            ScrollChatToEnd();
            return;
        }

        if (!_gotReplyText && !string.IsNullOrWhiteSpace(r.Text)) AddBubble(r.Text, user: false);

        // did it create a new strategy? it opens on its own
        _catalog.Refresh();
        var created = Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").Where(f => !_filesBefore.ContainsKey(f)).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        var target = created != null ? _catalog.Files.FirstOrDefault(f => f.Path == created) : _current;
        if (target != null && !ReferenceEquals(target, _current)) await SelectAsync(target);
        else
        {
            ReloadIfChangedOnDisk();
            await CompileAsync(silent: true);
        }

        var res = _current?.LastCompile;
        int errors = res?.Diagnostics.Count(d => d.Severity == "error") ?? 0;
        if (res?.Success == true && _current?.Type != null)
        {
            var test = Ui.Button("Test in a backtest", MaterialIconKind.PlayOutline, () =>
            {
                _tabs.Select("backtest");
                ShowTab("backtest");
                _ = RunBacktestAsync();
            });
            test.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CheckCircle, 15, Ui.Up), new TextBlock { Text = $"{_current.DisplayName} compiles with no errors", Classes = { "small" }, Foreground = Ui.Up, TextWrapping = TextWrapping.Wrap, MaxWidth = 320 }), test));
        }
        else if (errors > 0)
        {
            var fix = Ui.Button("Ask it to fix them", MaterialIconKind.AutoFix, () => _ = SendToClaudeAsync("Fix the compilation errors in the open file."), "primary");
            fix.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.CloseCircle, 15, Ui.Down), new TextBlock { Text = $"{errors} compilation error(s)", Classes = { "small" }, Foreground = Ui.Down }), fix));
        }

        // did it create or change indicators? they are compiled and it reports whether they landed in "My indicators" or what is failing
        ReportIndicators();

        // what it looked at on the chart: the same image, so you see what it is talking about
        ShowSnapshots();

        // new reports from the lab
        foreach (var rep in Lab.ReportStore.List().Where(x => !_reportsBefore.Contains(x.Path)).Reverse())
        {
            var path = rep.Path;
            var open = Ui.Button("View report", MaterialIconKind.FileDocumentOutline, () => OpenReport(path), "primary");
            open.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.FileChartOutline, 15, Ui.Accent), new TextBlock { Text = rep.Title, Classes = { "small" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 260 }, VerdictPill(rep.Verdict)), open));
            BuildStrategyMenu();
        }

        if (_labToggle.IsChecked == true) RefreshHistory();
        if (TradingHub.Strategies.LiveStore.LoadProposals().Any(p => p.Source == "claude" && p.IsPending && p.CreatedUtc > DateTime.UtcNow.AddMinutes(-30)))
        {
            var review = Ui.Button("Review and approve", MaterialIconKind.ScaleBalance, () =>
            {
                if (TopLevel.GetTopLevel(this) is not TradingHub.App.Windows.MainWindow main) return;
                main.Navigate("live");
                (main.Page("live") as LiveView)?.Select("proposals");
            }, "primary");
            review.Padding = new Thickness(10, 4);
            _chat.Children.Add(Ui.V(8, Ui.H(8, Ui.Icon(MaterialIconKind.ScaleBalance, 15, Ui.Accent), new TextBlock { Text = "Claude left size changes pending your approval", Classes = { "small" }, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 }), review));
        }
        if (r.Seconds > 0) AddNote(MaterialIconKind.TimerOutline, r.Seconds >= 120 ? $"{r.Seconds / 60:0.#} min" : $"{r.Seconds:0} s");
        ScrollChatToEnd();
    }

    // ===================================================================== conversation history

    private void ShowHistoryMenu(Control anchor)
    {
        var list = new StackPanel { Spacing = 2, Width = 360 };
        var convs = ChatHistory.List();
        if (convs.Count == 0)
            list.Children.Add(new TextBlock { Text = "There are no stored conversations yet.", Classes = { "muted", "small" }, Margin = new Thickness(8) });
        var flyout = new Flyout { Content = new ScrollViewer { Content = list, MaxHeight = 460 }, Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var c in convs.Take(60))
        {
            var conv = c;
            var title = new TextBlock { Text = string.IsNullOrWhiteSpace(conv.Title) ? "(untitled)" : conv.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5, FontWeight = ReferenceEquals(conv.Id, _conv.Id) || conv.Id == _conv.Id ? FontWeight.SemiBold : FontWeight.Normal };
            var meta = new TextBlock
            {
                Text = $"{Ui.Ago(conv.UpdatedUtc)} · {conv.Messages.Count(m => m.Role == "user")} message(s){(conv.Lab ? " · lab" : "")}{(conv.Strategy != null ? " · " + conv.Strategy : "")}",
                Classes = { "faint", "small" },
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var open = new Button { Content = Ui.V(1, title, meta), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5) }.Classes("ghost");
            open.Click += (_, _) =>
            {
                flyout.Hide();
                LoadConversation(conv);
            };
            var delete = Ui.IconButton(MaterialIconKind.Close, "Delete this conversation from the history", () =>
            {
                ChatHistory.Delete(conv.Id);
                flyout.Hide();
                if (conv.Id == _conv.Id) _conv = new ChatConversation { Title = _conv.Title, Lab = _conv.Lab };
            }, 14);
            var row = new DockPanel();
            DockPanel.SetDock(delete, Dock.Right);
            row.Children.Add(delete);
            row.Children.Add(open);
            list.Children.Add(row);
        }

        flyout.ShowAt(anchor);
    }

    /// <summary>Shows a stored conversation and leaves it ready to continue (Claude picks its session back up).</summary>
    private void LoadConversation(ChatConversation conv)
    {
        if (_claude.IsRunning) return;
        _conv = conv;
        _claude.Resume(conv.SessionId);
        _labToggle.IsChecked = conv.Lab;
        _chat.Children.Clear();
        foreach (var m in conv.Messages)
        {
            if (m.Role == "note") AddNote(MaterialIconKind.InformationOutline, m.Text);
            else AddBubble(m.Text, user: m.Role == "user");
        }

        _currentReply = null;
        AddNote(MaterialIconKind.History, conv.SessionId != null
            ? "Conversation resumed: whatever you write carries on from here, with everything Claude already knew."
            : "Stored conversation (no Claude session): whatever you write starts a new session.");
        ScrollChatToEnd();
    }

    // ===================================================================== chat in a separate window

    private void ToggleClaudeWindow()
    {
        if (_claudeWindow != null) DockClaude();
        else DetachClaude();
    }

    private void DetachClaude()
    {
        if (_claudePanel == null || _claudeGrid == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var s = App.Services.Settings;
        _claudeGrid.Children.Remove(_claudePanel);
        _claudeGrid.ColumnDefinitions[2].Width = new GridLength(0);
        if (_claudeSplit != null) _claudeSplit.IsVisible = false;
        var w = new Window
        {
            Title = "Claude · strategies",
            Width = Math.Clamp(s.ClaudeWindowWidth, 320, 1600),
            Height = Math.Clamp(s.ClaudeWindowHeight, 360, 2400),
            MinWidth = 320,
            MinHeight = 360,
            Background = Ui.Bg1,
            Content = _claudePanel,
        };
        var pos = new PixelPoint(s.ClaudeWindowX, s.ClaudeWindowY);
        if (s.ClaudeWindowX >= 0 && w.Screens.All.Any(sc => sc.WorkingArea.Contains(pos)))
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Position = pos;
        }
        else
        {
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        w.Closing += (_, _) =>
        {
            s.ClaudeWindowX = w.Position.X;
            s.ClaudeWindowY = w.Position.Y;
            if (w.Width > 100) s.ClaudeWindowWidth = w.Width;
            if (w.Height > 100) s.ClaudeWindowHeight = w.Height;
            s.Save();
        };
        w.Closed += (_, _) => DockClaude();
        _claudeWindow = w;
        if (_claudeDetach != null)
        {
            _claudeDetach.Content = Ui.Icon(MaterialIconKind.DockRight, 17);
            ToolTip.SetTip(_claudeDetach, "Put the chat back beside the editor");
        }

        w.Show(owner);
    }

    private void DockClaude()
    {
        var w = _claudeWindow;
        if (w == null || _claudePanel == null || _claudeGrid == null) return;
        _claudeWindow = null;
        w.Content = null;
        if (_claudePanel.Parent == null)
        {
            Grid.SetColumn(_claudePanel, 2);
            _claudeGrid.Children.Add(_claudePanel);
        }

        _claudeGrid.ColumnDefinitions[2].Width = new GridLength(400);
        if (_claudeSplit != null) _claudeSplit.IsVisible = true;
        if (_claudeDetach != null)
        {
            _claudeDetach.Content = Ui.Icon(MaterialIconKind.OpenInNew, 17);
            ToolTip.SetTip(_claudeDetach, "Pull the chat into a separate window");
        }

        if (w.IsVisible) w.Close();
    }

    /// <summary>Asks the lab to analyze the live portfolio and propose sizes (you approve them in Live -> Proposals).</summary>
    internal void AskClaudeForSizing()
    {
        _labToggle.IsChecked = true;
        _tabs.Select("code");
        ShowTab("code");
        _ = SendToClaudeAsync("Analyse the live book with live_portfolio (each strategy's result, trades, drawdown and streak) and, if appropriate, propose size changes with propose_sizing explaining the reason for each one. Apply nothing: I approve each change in Trading Hub (Live → Proposals).");
    }
}
