using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TradingHub.App.Services;

/// <summary>A request to a Claude Code agent (the CLI with the user's session).</summary>
public sealed class ClaudeAgentRequest
{
    public required string Role { get; init; }
    public required string Prompt { get; init; }
    public required string SystemPrompt { get; init; }
    public required string WorkDir { get; init; }

    /// <summary>Claude Code's own tools that are enabled (Read, WebSearch…); empty = none.</summary>
    public string[] Tools { get; init; } = Array.Empty<string>();

    /// <summary>Trading Hub lab tools (without the mcp__tradinghub__ prefix).</summary>
    public string[] LabTools { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Allowed commands with their pattern (for example "Bash(dotnet build:*)"): if there are any, Bash is enabled only for
    /// those commands and any other one is rejected.
    /// </summary>
    public string[] AllowedPatterns { get; init; } = Array.Empty<string>();

    /// <summary>Extra environment variables for the agent (for example, where dotnet is).</summary>
    public Dictionary<string, string> Environment { get; init; } = new();

    /// <summary>acceptEdits for the one that writes files; dontAsk for the rest (anything not enabled is rejected).</summary>
    public string PermissionMode { get; init; } = "dontAsk";
    public string Model { get; init; } = "claude-opus-5-5";
    public string? Effort { get; init; }
    public double MaxUsd { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);

    /// <summary>New session with this id (it is stored so it can be continued if it drops). null = not stored.</summary>
    public string? SessionId { get; init; }

    /// <summary>Continue a session that dropped (the request is the new message).</summary>
    public string? ResumeSessionId { get; init; }
}

/// <summary>What an agent returned.</summary>
public sealed record ClaudeAgentResult(bool Ok, string Text, JsonElement? Json, double CostUsd, double Seconds, string? Error, bool NeedsLogin, int ToolCalls);

/// <summary>
/// Runs a Claude agent: Claude Code with no window (claude -p) using the user's subscription session, the chosen
/// model, the role as instructions, only the tools it is meant to have and Trading Hub's lab as an MCP server.
/// It runs in restricted mode (without the user's configuration, hooks or MCPs, files only in its own folder) and
/// without storing the session. At the end the ```json block of the response is read.
/// </summary>
public sealed class ClaudeAgent
{

    /// <summary>It used a tool (name and detail). It arrives from another thread.</summary>
    public event Action<string, string?>? ToolUsed;

    /// <summary>It wrote text. It arrives from another thread.</summary>
    public event Action<string>? TextWritten;

    /// <summary>CLI arguments for a request (the options the installed CLI does not know are omitted).</summary>
    public static List<string> BuildArgs(ClaudeAgentRequest req, string? labConfig, Func<string, bool> supports)
    {
        var args = new List<string>
        {
            "-p", req.Prompt,
            "--output-format", "stream-json",
            "--verbose",
            "--model", req.Model,
            "--append-system-prompt", req.SystemPrompt,
            "--permission-mode", req.PermissionMode,
            "--strict-mcp-config",
        };

        // without the user's configuration, hooks, plugins or MCPs; files only in the agent's folder
        if (supports("--restricted")) args.Add("--restricted");
        // with a stored session (to continue it if the Mac shuts down) or storing nothing
        if (req.ResumeSessionId != null && supports("--resume")) args.AddRange(new[] { "--resume", req.ResumeSessionId });
        else if (req.SessionId != null && supports("--session-id")) args.AddRange(new[] { "--session-id", req.SessionId });
        else if (supports("--no-session-persistence")) args.Add("--no-session-persistence");
        if (supports("--disable-slash-commands")) args.Add("--disable-slash-commands");
        if (req.Effort != null && supports("--effort")) args.AddRange(new[] { "--effort", req.Effort });
        if (req.MaxUsd > 0 && supports("--max-budget-usd")) args.AddRange(new[] { "--max-budget-usd", req.MaxUsd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) });

        args.Add("--tools");
        args.Add(string.Join(",", req.Tools));
        // with patterns, Bash is not enabled wholesale: only the commands in the patterns
        var allowed = req.Tools.Where(t => !(t == "Bash" && req.AllowedPatterns.Length > 0)).Concat(req.AllowedPatterns)
            .Concat(req.LabTools.Select(t => "mcp__tradinghub__" + t)).ToList();
        if (req.LabTools.Length > 0 && labConfig != null)
        {
            args.Add("--mcp-config");
            args.Add(labConfig);
        }

        if (allowed.Count > 0)
        {
            args.Add("--allowedTools");
            args.Add(string.Join(",", allowed));
        }

        return args;
    }

    /// <summary>Does the installed CLI know this option? (its --help is read once)</summary>
    public static Task<Func<string, bool>> SupportsAsync(string cli) => ClaudeCodeBridge.SupportsAsync(cli);

    public async Task<ClaudeAgentResult> RunAsync(ClaudeAgentRequest req, CancellationToken ct)
    {
        var cli = ClaudeCodeBridge.FindCli();
        if (cli == null) return new ClaudeAgentResult(false, "", null, 0, 0, "Claude Code was not found on this Mac (install it from claude.com/claude-code).", false, 0);

        var supports = await SupportsAsync(cli);
        Directory.CreateDirectory(req.WorkDir);
        var psi = ClaudeCodeBridge.Prepare(cli, req.WorkDir);
        foreach (var (key, value) in req.Environment) psi.Environment[key] = value;
        string? labConfig = req.LabTools.Length > 0 ? ClaudeCodeBridge.WriteLabConfig() : null;
        foreach (var a in BuildArgs(req, labConfig, supports)) psi.ArgumentList.Add(a);
        if (labConfig != null)
        {
            // an optimization or a walk-forward can take a while
            psi.Environment["MCP_TOOL_TIMEOUT"] = "3600000";
            psi.Environment["MCP_TIMEOUT"] = "60000";
        }

        var sw = Stopwatch.StartNew();
        var finalText = new StringBuilder();
        var allText = new StringBuilder();
        var stderr = new StringBuilder();
        bool isError = false, gotResult = false;
        double cost = 0;
        int tools = 0;
        string? subtype = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(req.Timeout);
        Process? proc = null;
        try
        {
            proc = Process.Start(psi)!;
            proc.StandardInput.Close();
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null && stderr.Length < 20000) stderr.AppendLine(e.Data);
            };
            proc.BeginErrorReadLine();
            using var reg = timeout.Token.Register(() =>
            {
                try
                {
                    if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true);
                }
                catch
                {
                    // already finished
                }
            });

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    if (type == "assistant") tools += HandleAssistant(root, allText);
                    else if (type == "result")
                    {
                        gotResult = true;
                        isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                        subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                        if (root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String) finalText.Append(r.GetString());
                        if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) cost = c.GetDouble();
                    }
                }
                catch (JsonException)
                {
                    // line that is not JSON
                }
            }

            await proc.WaitForExitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            return new ClaudeAgentResult(false, allText.ToString(), null, cost, sw.Elapsed.TotalSeconds, "Could not run Claude Code: " + ex.Message, false, tools);
        }
        finally
        {
            proc?.Dispose();
        }

        string text = finalText.Length > 0 ? finalText.ToString() : allText.ToString();
        if (ct.IsCancellationRequested) return new ClaudeAgentResult(false, text, null, cost, sw.Elapsed.TotalSeconds, "Stopped.", false, tools);
        if (timeout.IsCancellationRequested)
            return new ClaudeAgentResult(false, text, null, cost, sw.Elapsed.TotalSeconds, $"It went over the time limit ({req.Timeout.TotalMinutes:0} min) and was cut off.", false, tools);

        string? error = null;
        if (!gotResult) error = stderr.Length > 0 ? stderr.ToString().Trim() : "Claude Code finished with no response.";
        else if (isError) error = subtype is { Length: > 0 } s && s != "success" ? Explain(s, text) : text.Trim();
        string all = (error ?? "") + "\n" + text + "\n" + stderr;
        bool needsLogin = !gotResult || isError
            ? all.Contains("/login", StringComparison.OrdinalIgnoreCase) || all.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
              all.Contains("authenticate", StringComparison.OrdinalIgnoreCase) || all.Contains("OAuth", StringComparison.OrdinalIgnoreCase)
            : false;

        // even if it was cut off by the spend cap, if it already left the JSON it is used
        var json = ExtractJson(text) ?? ExtractJson(allText.ToString());
        bool ok = json != null && (error == null || subtype == "error_max_budget_usd" || subtype == "error_max_turns");
        return new ClaudeAgentResult(ok, text, json, cost, sw.Elapsed.TotalSeconds, ok ? null : error ?? "The agent did not return the requested JSON block.", needsLogin, tools);
    }

    private static string Explain(string subtype, string text) => subtype switch
    {
        "error_max_budget_usd" => "It hit the per-batch spending cap before finishing.",
        "error_max_turns" => "It hit the maximum number of steps before finishing.",
        "error_during_execution" => "Claude Code failed while working" + (text.Length > 0 ? ": " + text.Trim() : "."),
        _ => text.Length > 0 ? text.Trim() : subtype,
    };

    private int HandleAssistant(JsonElement root, StringBuilder allText)
    {
        int tools = 0;
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return 0;
        foreach (var block in content.EnumerateArray())
        {
            string bt = block.TryGetProperty("type", out var b) ? b.GetString() ?? "" : "";
            if (bt == "text" && block.TryGetProperty("text", out var txt))
            {
                string s = txt.GetString() ?? "";
                allText.AppendLine(s);
                TextWritten?.Invoke(s);
            }
            else if (bt == "tool_use")
            {
                tools++;
                string name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                ToolUsed?.Invoke(name, Detail(block));
            }
        }

        return tools;
    }

    /// <summary>Short detail of a tool for the channel (search, page, file or run).</summary>
    private static string? Detail(JsonElement block)
    {
        if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return null;
        string S(string k) => input.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        foreach (var key in new[] { "query", "url", "file_path", "pattern" })
            if (S(key) is { Length: > 0 } v) return key == "file_path" ? Path.GetFileName(v) : v;
        var parts = new[] { S("file"), S("feed").Length > 0 ? $"{S("feed")}:{S("symbol")}" : S("symbol"), S("period"), S("from").Length > 0 ? $"{S("from")} → {S("to")}" : "", S("title") }
            .Where(x => x.Length > 0);
        var detail = string.Join(" · ", parts);
        return detail.Length > 0 ? detail : null;
    }

    /// <summary>The last ```json block of a text (or the last JSON object that can be read).</summary>
    public static JsonElement? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var opts = new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
        int idx = text.LastIndexOf("```json", StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            int start = idx + 7;
            int end = text.IndexOf("```", start, StringComparison.Ordinal);
            string body = end > start ? text[start..end] : text[start..];
            if (TryParse(body, opts) is { } el) return el;
            idx = idx > 0 ? text.LastIndexOf("```json", idx - 1, StringComparison.OrdinalIgnoreCase) : -1;
        }

        // with no block: the last object that closes properly
        int close = text.LastIndexOf('}');
        if (close < 0) return null;
        int tries = 0;
        for (int open = text.LastIndexOf('{', close); open >= 0 && tries < 400; open = open > 0 ? text.LastIndexOf('{', open - 1) : -1, tries++)
            if (TryParse(text[open..(close + 1)], opts) is { } el) return el;
        return null;
    }

    private static JsonElement? TryParse(string body, JsonDocumentOptions opts)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.Trim(), opts);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
