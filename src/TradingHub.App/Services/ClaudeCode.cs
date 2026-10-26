using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace TradingHub.App.Services;

/// <summary>Result of a request to Claude Code.</summary>
public sealed record ClaudeResult(bool IsError, string Text, double CostUsd, double Seconds, bool NeedsLogin);

/// <summary>
/// Bridge to Claude Code (the <c>claude</c> CLI installed on the Mac, using the user's subscription
/// session). Runs non-interactively inside the strategies folder, with permission only to read and
/// edit files in that folder, and streams the response as it arrives.
/// </summary>
public sealed class ClaudeCodeBridge
{
    private static string? _helpText;
    private static string? _helpCli;

    /// <summary>Does the installed CLI know this option? (its --help is read once)</summary>
    public static async Task<Func<string, bool>> SupportsAsync(string cli)
    {
        if (_helpText == null || _helpCli != cli)
        {
            string text = "";
            try
            {
                var psi = Prepare(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                psi.ArgumentList.Add("--help");
                using var p = Process.Start(psi)!;
                p.StandardInput.Close();
                var read = p.StandardOutput.ReadToEndAsync();
                if (await Task.WhenAny(read, Task.Delay(15000)) == read) text = await read;
                else p.Kill(true);
            }
            catch
            {
                // no help available: only the basic options
            }

            _helpText = text;
            _helpCli = cli;
        }

        var help = _helpText;
        return flag => help.Contains(flag + " ", StringComparison.Ordinal) || help.Contains(flag + ",", StringComparison.Ordinal) || help.Contains(flag + "\n", StringComparison.Ordinal);
    }

    private Process? _proc;
    private bool _streamedText;

    /// <summary>Conversation id (to continue it with --resume).</summary>
    public string? SessionId { get; private set; }

    public bool IsRunning => _proc is { HasExited: false };

    /// <summary>Claude's text as it is written.</summary>
    public event Action<string>? Text;

    /// <summary>Claude used a tool (read, created or edited a file).</summary>
    public event Action<string, string?>? ToolUsed;

    /// <summary>A tool finished (for example, the file has already been written).</summary>
    public event Action? ToolFinished;

    public event Action<ClaudeResult>? Finished;

    public void NewConversation() => SessionId = null;

    /// <summary>Continues a saved conversation (the next message goes with --resume to that session).</summary>
    public void Resume(string? sessionId) => SessionId = sessionId;

    private static string? _best;
    private static DateTime _bestAt;
    private static readonly Dictionary<string, (DateTime Stamp, Version? Version)> Versions = new();

    /// <summary>Model and effort Claude works with in Trading Hub (Opus 5.5 with extra effort).</summary>
    public static string Model { get; set; } = "claude-opus-5-5";
    public static string Effort { get; set; } = "xhigh";

    /// <summary>
    /// Path to the Claude Code CLI (or null if it is not installed). If there are several (the Terminal one, the one bundled
    /// with the Claude app…) it uses the newest: the new models (Opus 5.5) require a recent version.
    /// </summary>
    public static string? FindCli()
    {
        var custom = Environment.GetEnvironmentVariable("TRADINGHUB_CLAUDE_CLI");
        if (!string.IsNullOrEmpty(custom) && File.Exists(custom)) return custom;
        lock (Versions)
        {
            if (_best != null && File.Exists(_best) && (DateTime.UtcNow - _bestAt).TotalMinutes < 10) return _best;
            var found = Candidates().Where(File.Exists).Distinct().ToList();
            _best = found.Select(p => (Path: p, Version: VersionOf(p))).OrderByDescending(x => x.Version ?? new Version(0, 0)).Select(x => x.Path).FirstOrDefault();
            _bestAt = DateTime.UtcNow;
            return _best;
        }
    }

    private static IEnumerable<string> Candidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var list = new List<string>
        {
            "/usr/local/bin/claude",
            "/opt/homebrew/bin/claude",
            Path.Combine(home, ".claude", "local", "claude"),
            Path.Combine(home, ".local", "bin", "claude"),
            Path.Combine(home, ".npm-global", "bin", "claude"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            list.Add(Path.Combine(dir, "claude"));
        // the one bundled with the Claude app (self-updating): …/claude-code/2.1.295/<id>/claude.app/Contents/MacOS/claude
        var bundled = Path.Combine(home, "Library", "Application Support", "Claude", "claude-code");
        try
        {
            if (Directory.Exists(bundled))
                foreach (var v in Directory.GetDirectories(bundled))
                    foreach (var h in Directory.GetDirectories(v))
                        list.Add(Path.Combine(h, "claude.app", "Contents", "MacOS", "claude"));
        }
        catch (Exception)
        {
            // no permission to look: the others are used
        }

        return list;
    }

    /// <summary>Version of a CLI ("2.1.295 (Claude Code)" → 2.1.295), cached per file.</summary>
    private static Version? VersionOf(string path)
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            if (Versions.TryGetValue(path, out var v) && v.Stamp == stamp) return v.Version;
            var psi = Prepare(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var read = p.StandardOutput.ReadToEndAsync();
            Version? version = null;
            if (read.Wait(TimeSpan.FromSeconds(8)) && Version.TryParse(read.Result.Trim().Split(' ')[0], out var parsed)) version = parsed;
            else if (!p.HasExited) p.Kill(true);
            Versions[path] = (stamp, version);
            return version;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Installed version ("2.1.251 (Claude Code)") or null.</summary>
    public static async Task<string?> VersionAsync()
    {
        var cli = FindCli();
        if (cli == null) return null;
        try
        {
            var psi = Prepare(cli, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            psi.ArgumentList.Add("--version");
            using var p = Process.Start(psi)!;
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Opens the Terminal with Claude Code to log in (/login) with the subscription.</summary>
    public static void OpenLoginTerminal()
    {
        var cli = FindCli() ?? "claude";
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add($"tell application \"Terminal\" to do script \"'{cli}' /login\"");
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("tell application \"Terminal\" to activate");
            Process.Start(psi);
        }
        catch
        {
            // no Terminal: the user can type "claude" by hand
        }
    }

    internal static ProcessStartInfo Prepare(string cli, string workDir)
    {
        var psi = new ProcessStartInfo(cli)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };

        // clean environment: do not inherit temporary credentials from another Claude session
        foreach (var key in psi.Environment.Keys.ToList())
            if (key.StartsWith("CLAUDE", StringComparison.Ordinal) || key.StartsWith("ANTHROPIC", StringComparison.Ordinal))
                psi.Environment.Remove(key);
        psi.Environment["PATH"] = "/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin:/usr/sbin:/sbin:" + (Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["TERM"] = "dumb";
        return psi;
    }

    /// <summary>Lab tools (Trading Hub MCP server).</summary>
    public static readonly string[] LabTools =
    {
        "list_strategies", "compile_strategy", "list_data", "download_data", "run_backtest", "run_optimization", "run_walk_forward", "list_runs",
        "analyze_portfolio", "suggest_portfolio", "live_portfolio", "propose_sizing", "save_report", "list_indicators", "compile_indicator", "chart_snapshot",
        "today_report", "list_levels", "next_events", "similar_days", "stress_test", "list_accounts", "trade_autopsy", "event_study", "fill_quality",
    };

    /// <summary>MCP config that starts Trading Hub in lab mode (the same executable with --mcp).</summary>
    internal static string WriteLabConfig()
    {
        // TRADINGHUB_MCP_COMMAND: another Trading Hub executable (for testing); otherwise this same one
        var exe = Environment.GetEnvironmentVariable("TRADINGHUB_MCP_COMMAND") is { Length: > 0 } custom ? custom : Environment.ProcessPath ?? "TradingHub";
        var env = new Dictionary<string, string>();
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root) env["DOTNET_ROOT"] = root;
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["tradinghub"] = new Dictionary<string, object> { ["type"] = "stdio", ["command"] = exe, ["args"] = new[] { "--mcp" }, ["env"] = env },
            },
        };
        Directory.CreateDirectory(AppPaths.Root);
        var path = Path.Combine(AppPaths.Root, "claude-lab-mcp.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Sends a request. Events arrive on the UI thread. With <paramref name="lab"/> Claude can run tests.</summary>
    public async Task SendAsync(string prompt, string workDir, string systemPrompt, bool lab = false, IReadOnlyList<string>? extraDirs = null)
    {
        if (IsRunning) return;
        var cli = FindCli();
        if (cli == null)
        {
            Post(() => Finished?.Invoke(new ClaudeResult(true, "Claude Code was not found on this Mac. Install it from claude.com/claude-code and try again.", 0, 0, false)));
            return;
        }

        var psi = Prepare(cli, workDir);
        foreach (var a in new[]
                 {
                     "-p", prompt,
                     "--output-format", "stream-json",
                     "--verbose",
                     "--include-partial-messages",
                     "--append-system-prompt", systemPrompt,
                     "--tools", "Read,Write,Edit,MultiEdit,Glob,Grep",
                     "--permission-mode", "acceptEdits",
                     "--strict-mcp-config",
                 })
            psi.ArgumentList.Add(a);
        // Opus 5.5 with extra effort (options this CLI does not know are omitted)
        var supports = await SupportsAsync(cli);
        if (!string.IsNullOrWhiteSpace(Model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(Model);
        }

        if (!string.IsNullOrWhiteSpace(Effort) && supports("--effort"))
        {
            psi.ArgumentList.Add("--effort");
            psi.ArgumentList.Add(Effort);
        }

        // other folders it may write to (for example the indicators one)
        foreach (var dir in extraDirs ?? Array.Empty<string>())
        {
            if (!Directory.Exists(dir) || !supports("--add-dir")) continue;
            psi.ArgumentList.Add("--add-dir");
            psi.ArgumentList.Add(dir);
        }

        if (lab)
        {
            // lab: Trading Hub as MCP server (backtest, optimization, walk-forward, reports)
            psi.ArgumentList.Add("--mcp-config");
            psi.ArgumentList.Add(WriteLabConfig());
            psi.ArgumentList.Add("--allowedTools");
            psi.ArgumentList.Add(string.Join(",", LabTools.Select(t => "mcp__tradinghub__" + t)));
            // an optimization or a walk-forward can take several minutes
            psi.Environment["MCP_TOOL_TIMEOUT"] = "3600000";
            psi.Environment["MCP_TIMEOUT"] = "60000";
        }

        if (SessionId != null)
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(SessionId);
        }

        var sw = Stopwatch.StartNew();
        var finalText = new StringBuilder();
        var stderr = new StringBuilder();
        bool isError = false, gotResult = false;
        double cost = 0;
        try
        {
            _proc = Process.Start(psi)!;
            _proc.StandardInput.Close();
            _proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) stderr.AppendLine(e.Data);
            };
            _proc.BeginErrorReadLine();
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String) SessionId = sid.GetString();
                    switch (type)
                    {
                        case "stream_event":
                            HandleStreamEvent(root);
                            break;
                        case "assistant":
                            HandleAssistant(root);
                            break;
                        case "user":
                            Post(() => ToolFinished?.Invoke());
                            break;
                        case "result":
                            gotResult = true;
                            isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                            if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String) finalText.Append(r.GetString());
                            if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) cost = c.GetDouble();
                            break;
                    }
                }
                catch (JsonException)
                {
                    // line that is not JSON: ignored
                }
            }

            await _proc.WaitForExitAsync();
            if (!gotResult)
            {
                isError = true;
                finalText.Append(stderr.Length > 0 ? stderr.ToString().Trim() : $"Claude Code finished with no response (exit code {_proc.ExitCode}).");
            }
        }
        catch (Exception ex)
        {
            isError = true;
            finalText.Append("Could not run Claude Code: " + ex.Message);
        }
        finally
        {
            _proc?.Dispose();
            _proc = null;
        }

        string text = finalText.ToString();
        bool needsLogin = isError && (text.Contains("authenticate", StringComparison.OrdinalIgnoreCase) || text.Contains("/login", StringComparison.OrdinalIgnoreCase) || text.Contains("not logged in", StringComparison.OrdinalIgnoreCase) || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase));
        var result = new ClaudeResult(isError, text, cost, sw.Elapsed.TotalSeconds, needsLogin);
        Post(() => Finished?.Invoke(result));
    }

    private void HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev)) return;
        string evType = ev.TryGetProperty("type", out var et) ? et.GetString() ?? "" : "";
        if (evType == "message_start") _streamedText = false;
        if (evType != "content_block_delta" || !ev.TryGetProperty("delta", out var delta)) return;
        if (delta.TryGetProperty("type", out var dt) && dt.GetString() == "text_delta" && delta.TryGetProperty("text", out var txt))
        {
            _streamedText = true;
            string s = txt.GetString() ?? "";
            Post(() => Text?.Invoke(s));
        }
    }

    private void HandleAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        foreach (var block in content.EnumerateArray())
        {
            string bt = block.TryGetProperty("type", out var b) ? b.GetString() ?? "" : "";
            if (bt == "text" && !_streamedText && block.TryGetProperty("text", out var txt))
            {
                string s = txt.GetString() ?? "";
                Post(() => Text?.Invoke(s));
            }
            else if (bt == "tool_use")
            {
                string name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                string? file = null;
                if (block.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                {
                    if (input.TryGetProperty("file_path", out var fp)) file = fp.GetString();
                    else if (input.TryGetProperty("pattern", out var pat)) file = pat.GetString();
                    else if (name.StartsWith("mcp__", StringComparison.Ordinal))
                    {
                        // lab: strategy, instrument and range
                        string S(string k) => input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                        var parts = new[] { S("file"), S("symbol"), S("from").Length > 0 ? $"{S("from")} → {S("to")}" : "", S("title") }.Where(x => x.Length > 0);
                        file = string.Join(" · ", parts);
                    }
                }

                Post(() => ToolUsed?.Invoke(name, file));
            }
        }

        _streamedText = false;
    }

    public void Cancel()
    {
        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // already finished
        }
    }

    private static void Post(Action a) => Dispatcher.UIThread.Post(a);

    /// <summary>Lab working method (added to the strategy instructions).</summary>
    public const string LabSystemPrompt = """

        LAB MODE. You have the mcp__tradinghub__* tools to evaluate strategies on your own, without asking the user for anything:
        list_strategies, compile_strategy, list_data, download_data, run_backtest (with monte_carlo_runs), run_optimization,
        run_walk_forward, list_runs and save_report. Everything you run is saved in the strategy history in Trading Hub.

        Method (you decide the parameters, ranges and data; iterate until you have a firm conclusion):
        1. Review the strategy (list_strategies / read the file) and the data (list_data). If there are few days and the feed is
           binance-futures, binance-spot or bybit-linear, download more with download_data (aim for 30-90 days; each day takes ~5 s).
        2. Baseline backtest with the default parameters (0.04% commission and 1 tick of slippage, unless the user says otherwise).
        3. Optimization with reasonable ranges (fewer than ~2000 combinations), NetProfitOverDrawdown or Sharpe metric and
           min_trades >= 30. Look at how many combinations are profitable (robustness), not just the best one.
        4. Walk-forward with the same ranges (optimization window 3-4 times the test one). What matters is the out-of-sample
           result: efficiency, % of winning windows, out-of-sample PF and drawdown.
        5. Monte Carlo (monte_carlo_runs: 1000) on the best validated parameters.
        6. If the logic has clear flaws, improve the code (edit the .cs), compile with compile_strategy and repeat the evaluation.
           Always compare against the previous version and do not keep changes that only improve in-sample results.
        7. Verdict:
           - winner: positive out-of-sample, walk-forward efficiency >= 0.5, out-of-sample PF >= 1.2, >= 30 trades,
             Monte Carlo p95 drawdown reasonable for the capital and stable results across windows.
           - promising: positive results but with a small sample or some instability: more data is needed.
           - loser: loses out-of-sample or only wins when overfitted.
           - discarded: does not compile, does not trade or the idea makes no sense with this data.
        8. Portfolios: if there are several strategies (or the same one on several instruments) with backtests, use analyze_portfolio
           to measure the correlation of their daily results and suggest_portfolio to look for lightly correlated combinations
           (correlation < 0.5) that improve the Sharpe and lower the drawdown of the whole.
        Live portfolio: if you are asked to review the strategies trading live, use live_portfolio (result, total PF and PF of
        the last 20 trades, current and maximum drawdown of each one) and make proposals with propose_sizing: more size (up to x3)
        for the ones performing consistently, less or a pause (0) for the ones degrading, always with a concrete reason based on
        the numbers. With fewer than ~10 trades do not draw conclusions. Nothing is ever applied on its own: the user approves
        every change. In that case save_report is not needed unless they ask for it.
        9. ALWAYS finish with save_report (when you evaluated strategies). Trading Hub builds the professional report on its own with ALL the metrics, tables and charts
           from the runs (pass it backtest_run_id -the best validated one, with Monte Carlo-, optimization_run_id and
           walk_forward_run_id; if you evaluated a portfolio, also "portfolio"). You write: verdict, summary (2 to 5 lines),
           analysis (what works and what does not, robustness based on the sensitivity of the optimization, efficiency and
           stability of the walk-forward, Monte Carlo, comparison between code versions), risks and next_steps, in Markdown and in English.
           If you changed the code, explain it in extra_markdown.
        Do not make up results: every number you write has to come from a tool. In the chat, at the end, answer in
        2 to 4 lines with the verdict and the key numbers (the detail goes in the report).
        Seeing the chart: with chart_snapshot you run the strategy over a range and get an image of the chart (candles, entries
        and exits, its drawings and lines) that you look at with Read, plus the list of candles, trades, drawings and log with
        local time. Use it when the user asks you about something they see on their chart ("it marks the breakout at 7:15 but
        enters at 8:45") or to check that what you programmed does what it says: look at the exact candle, the values of its lines and the order.

        ORDER-FLOW IDEAS (when the user has an idea about the tape rather than a finished strategy): test it BEFORE writing any code.
        1. list_data to see which instruments and how many days of ticks are stored.
        2. event_study with the event that matches the idea (lean, stacked_imbalance, absorption, big_trade, delta_divergence). It
           reports basis points before and after costs at 1, 5, 15 and 30 minutes, split into train and test, with the opposite trade, an
           always-long baseline, splits by time of day, volatility and blocks of days, and a multiple-testing correction. Pass tests_tried
           with how many events and horizons you have looked at so far on that data (the number grows every time you try another).
        3. Judge ONLY on the test rows, after the correction: an idea has something only if the test net is positive, the opposite trade
           does worse, it is positive in most blocks of days, and the corrected p-value is under 0.05 with enough events (at least 100).
           If you tried several events, say how many; the best of five is not a finding. With fewer than about 15 days of ticks say the
           sample is short.
        4. Only if it survives, write it as a strategy (compile_strategy) and run the usual method above, because an event that works
           on average still has to survive costs, stops and walk-forward as a real strategy. If it does not survive, say plainly that
           nothing worked and what the numbers were; do not tune thresholds until something looks good.
        fill_quality shows what the user's fills really cost (slippage against the tape plus commissions) and the round trip in basis points:
        compare it with the costs a backtest assumed before trusting that backtest.
        """;

    /// <summary>Instructions for Claude to write custom indicators (in addition to strategies).</summary>
    public const string IndicatorSystemPrompt = """

        INDICATORS (when you are asked for an indicator and not a strategy):
        - They go in the user's indicators folder (I give you the path in the context; you have permission to write there), one
          .cs file per indicator with a class of the same name. They compile on their own when saved and show up on the chart
          under Indicators -> "My indicators". Do not put them in the strategies folder.
        - usings: System.ComponentModel, System.ComponentModel.DataAnnotations, System.Drawing (colors: Color.Orange,
          Color.FromArgb(a, r, g, b)), TradingHub.Indicators, TradingHub.Indicators.Drawing and, if you draw yourself, TradingHub.Rendering.Context and
          TradingHub.Rendering.Tools. System, System.Linq and System.Collections.Generic are already there.
        - `[DisplayName("Display name")] [Category("My indicators")] public class MyIndicator : Indicator` with a parameterless
          constructor.
        - Series that draw themselves (fields): ValueDataSeries (line or histogram:
          `new ValueDataSeries("Id", "Name") { Color = Color.Orange, Width = 2, VisualType = VisualMode.Line }`, also
          VisualMode.Histogram), RangeDataSeries (bands: `_band[bar] = new RangeValue { Upper = a, Lower = b }`) and
          PaintbarsDataSeries (painting candles: `_candles[bar] = Color.Lime`). In the constructor: `DataSeries[0] = _series;` (the
          main one) and `DataSeries.Add(_other);` for the rest.
        - Panel: on the price by default; for your own one below (oscillators) `Panel = IndicatorDataProvider.NewPanel;`
          in the constructor.
        - Parameters: public properties with `[Display(Name = "Period", GroupName = "Settings")]` and `[Range(1, 1000)]`;
          in the setter store the value and call `RecalculateValues();`.
        - Calculation: `protected override void OnCalculate(int bar, decimal value)` is called for each candle (bar = index from 0,
          value = close). `GetCandle(bar)` -> Open, High, Low, Close, Volume, Ask, Bid, Delta, MaxDelta, MinDelta, Time.
          `CurrentBar` = number of candles. Write `_series[bar] = value;` and read the previous ones with `_series[bar - 1]` (watch
          out for negative indices: at the start there are not enough candles).
        - Your own drawing (optional, for example ranges or labels): in the constructor `EnableCustomDrawing = true;
          SubscribeToDrawingEvents(DrawingLayouts.Final);` and `protected override void OnRender(RenderContext context,
          DrawingLayouts layout)` with `ChartInfo.GetXByBar(bar)`, `ChartInfo.GetYByPrice(price)`,
          `context.DrawLine(new RenderPen(color, 1), x1, y1, x2, y2)`, `context.FillRectangle(color, new Rectangle(x, y, w, h))`,
          `context.DrawString(text, new RenderFont("Arial", 9), color, x, y)`. Visible candles: FirstVisibleBarNumber and
          LastVisibleBarNumber.
        - With the lab tools, compile it with compile_indicator (and list_indicators to see the existing ones) and
          fix it until it compiles. Without the lab, Trading Hub compiles it when you finish and passes you the errors.
        """;

    /// <summary>Instructions for Claude to write strategies that compile in Trading Hub.</summary>
    public const string StrategySystemPrompt = """
        You are the strategy assistant of Trading Hub, a desktop trading platform. You work inside the user's
        strategies folder (the current directory). Always answer in English, briefly.

        What a Trading Hub strategy looks like:
        - One .cs file per strategy in the current directory. The class name is the same as the file name.
        - `using TradingHub.Strategies;` (System, System.Linq and System.Collections.Generic are already available).
        - `[Strategy("Display name", Description = "...")] public class MyStrategy : Strategy { ... }` with a parameterless constructor.
        - Editable parameters: public properties with `[Parameter("Name")]`; to optimize them, also `[Optimize(min, max, step)]`.
        - Lifecycle: `protected override void OnInit()` (create indicators), `protected override void OnBar(Bar bar)` (when each candle closes),
          optional `OnTick(Trade trade)`, `OnOrderFilled(StrategyOrder order)`, `OnSessionStart(DateOnly session)`, `OnStop()`.
        - Data: `Bars[0]` last closed candle, `Bars[1]` the previous one. `Bar`: Open, High, Low, Close, Volume, BuyVolume, SellVolume,
          Delta, MaxDelta, MinDelta, Trades, PocPrice, IsBullish, IsBearish, Range, Body, Typical, Time, CloseTime, Index.
          `bar.Levels` (price -> (Buy, Sell)) only if `UseFootprint = true` is set in OnInit.
          Also: Price, Time (UTC), LocalTime, TickSize, CurrentBar, Instrument, IsBacktest.
        - Indicators (create in OnInit and store in fields): Sma(n), Ema(n), Rsi(n), Atr(n), Vwap(), Bollinger(n, k) (.Upper/.Lower/.Middle),
          Macd(12,26,9) (.Signal/.Histogram), Highest(n), Lowest(n), StdDev(n), CumulativeDelta(), Custom("name", bars => value).
          They are read as a series: `ind[0]` current, `ind[1]` previous, `ind.Value`, `ind.IsReady`. PriceSource: Close, Open, High, Low, Typical, Median, Volume, Delta.
        - Orders: EnterLong(), EnterShort() (they reverse if needed), Buy(qty), Sell(qty), BuyLimit(price), SellLimit(price),
          BuyStop(price), SellStop(price), ClosePosition(), CancelAll(), Cancel(order), Modify(order, price).
          Automatic management (OCO): StopLossTicks, TakeProfitTicks, TrailingStopTicks properties (they can be set in the constructor).
          Quantity = default size.
        - Position: Position (>0 long, <0 short), IsFlat, IsLong, IsShort, AveragePrice, OpenPnL, NetProfit, ActiveOrders.
        - Helpers: CrossAbove(a, b), CrossBelow(a, b) (series, or a series against a number), Ticks(n), Round(price),
          TimeBetween(TimeSpan from, TimeSpan to) (local session time), Log("text").
        - Prices and volumes are decimal (cast double with (decimal) when needed).
        - Chart drawings (visible when you put it on a chart, in history and live). UTC times (bar.Time,
          Bars[n].Time); color "#RRGGBB", "#AARRGGBB" or a name ("green", "red", "blue", "cyan", "yellow", "orange",
          "purple", "pink", "gray", "white"); with no color, the strategy's own. Repeating the same id replaces the drawing (handy
          for stretching the day range candle by candle); use different ids for different things, for example $"range-{bar.Time:yyyyMMdd}".
            DrawBox(id, from, price1, to, price2, color, fillOpacity: 15, text) -> rectangle (ranges, zones)
            DrawLine(id, time1, price1, time2, price2, color, width, LineStyle.Solid|Dashed|Dotted)
            DrawHorizontalLine(id, price, from: null, color, width, style, text) -> level up to the right edge
            DrawArrowUp(id, time, price, color, text) / DrawArrowDown(...) -> arrow with its tip at the price
            DrawMarker(id, time, price, MarkerShape.Circle|Square|Diamond|Cross|ArrowUp|ArrowDown, color, text)
            DrawText(id, time, price, "text", color)
            Plot("name", value, color, width, style) -> continuous line candle by candle (call it in OnBar; null breaks it)
            RemoveDrawing(id), ClearDrawings()
          Example: in OnBar, `Plot("Ceiling", _max[0], "green"); Plot("Floor", _min[0], "red");` and on a breakout
          `DrawArrowUp($"breakout-{bar.Time:yyyyMMddHHmm}", bar.Time, bar.Close, "green", "breakout");`. There is a cap of
          3,000 drawings (the oldest ones are dropped): do not draw something on every candle that does not need it.
        - Market context (with closed candles only, no peeking at the future): MarketTrend (trend efficiency of the
          last 24 h: 0 = pure range, 1 = clean trend; random is ~0.2), Regime (MarketRegime.Range if <= 0.15, Mixed,
          Trend if >= 0.35, Unknown with no data), MarketDirection (1 if it rose over those 24 h, -1 if it fell: a trend is
          up or down based on that), MarketChange (net 24 h move, in %), MarketVolatility (standard deviation of the
          hourly returns over 24 h, in %) and VolatilityRank (percentile of that volatility over the last month: 0 = the lowest,
          100 = the highest). They can be null. Live, they start with the last month of hourly history.
        - Context filter (every strategy has it, with no code to write): RegimeFilter parameters (All, RangeOnly,
          NoTrend, TrendOnly, NoRange, UpTrendOnly, DownTrendOnly, MixedOnly, RangeOrUpTrend, RangeOrDownTrend, MixedOrUpTrend,
          MixedOrDownTrend, NoDownTrend, NoUpTrend, NoMixed: any combination of range, mixed and up or down trend),
          MinVolatilityRank and MaxVolatilityRank (0 to 100). Outside that context the strategy
          does not open or add to positions (exits always go through). They can be set as parameters in the backtest, the
          optimization, the walk-forward and live. ContextAllowsEntries tells you whether entries are allowed right now.

        - Order book (depth): Book (DepthSnapshot? with Bids and Asks, each a BookLevel[] of PriceTicks and Size, best first;
          price = Instrument.FromTicks is not available to strategies, use PriceTicks * TickSize) is the live book, or in a
          backtest the recorded one (4 snapshots a second, 20 levels per side; only for days the app recorded with "Record the
          order book" on). It is null when there is none: ALWAYS check for null and do nothing (or fall back) without it.
          BookImbalance(levels = 5) is (bid - ask) / (bid + ask) in -1..+1 (0 with no book). Set TrackIcebergs = true in OnInit
          to fill Icebergs (IcebergEvent: Ticks, Ask = resting sell, Volume, Start, Last), levels that absorbed much more than
          they showed. Check the result log for "Using the recorded order book" to know the backtest had a book.

        Rules:
        - Write code that compiles in C# 13 with no external packages. No files, network, threads or unseeded Random.
        - Short comments in English explaining the logic.
        - To create a new strategy, create a new file with a clear PascalCase name; to modify one, edit the file indicated.
        - If you are given compilation errors, fix the file.
        - When you finish, explain in 2 to 5 lines what the strategy does or what you changed (without pasting the code: the user sees it in the editor).
        """;
}
