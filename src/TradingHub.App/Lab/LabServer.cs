using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingHub.App.Services;
using TradingHub.Data;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Lab;

/// <summary>
/// Lab for Claude: Trading Hub runs as an MCP server (JSON-RPC over stdin/stdout) when it is launched with
/// <c>--mcp</c>. Claude Code uses it to list and compile strategies, view and download data, run
/// backtests, optimizations, walk-forwards and Monte Carlo on its own, and save a report.
/// Everything it runs ends up in the strategy's history, exactly as if the user had run it.
/// </summary>
internal static class LabServer
{
    private static StrategyCatalog? _catalog;
    private static TickData? _ticks;
    private static string? _ticksKey;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task<int> RunAsync()
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        // any Console.WriteLine from elsewhere goes to stderr: stdout belongs to the protocol alone
        Console.SetOut(Console.Error);
        var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        StrategyStore.Root = StoreRoot;

        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode? msg;
            try
            {
                msg = JsonNode.Parse(line);
            }
            catch
            {
                continue;
            }

            var id = msg?["id"];
            string? method = msg?["method"]?.GetValue<string>();
            if (id == null) continue; // a notification: it carries no response

            JsonNode? result = null;
            (int Code, string Message)? error = null;
            try
            {
                switch (method)
                {
                    case "initialize":
                        result = Initialize(msg!["params"]);
                        break;
                    case "ping":
                        result = new JsonObject();
                        break;
                    case "tools/list":
                        result = new JsonObject { ["tools"] = Tools() };
                        break;
                    case "tools/call":
                        result = await CallAsync(msg!["params"]);
                        break;
                    default:
                        error = (-32601, $"Unknown method: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                error = (-32603, ex.Message);
            }

            var resp = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
            if (error is { } e) resp["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
            else resp["result"] = result;
            await stdout.WriteLineAsync(resp.ToJsonString());
        }

        return 0;
    }

    private static JsonNode Initialize(JsonNode? p) => new JsonObject
    {
        ["protocolVersion"] = p?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
        ["serverInfo"] = new JsonObject { ["name"] = "tradinghub-lab", ["version"] = "1.0" },
        ["instructions"] = "Trading Hub lab: C# strategies, stored tick-by-tick data, backtest, optimization, walk-forward, Monte Carlo and reports.",
    };

    // ===================================================================== tools

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, desc, req) in props)
        {
            properties[name] = new JsonObject { ["type"] = type, ["description"] = desc };
            if (req) required.Add(name);
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static readonly (string Name, string Type, string Description, bool Required)[] RangeProps =
    {
        ("file", "string", "Strategy file without .cs (e.g. MaCross)", true),
        ("feed", "string", "The data's connection (e.g. binance-futures), see list_data", true),
        ("symbol", "string", "Symbol (e.g. BTCUSDT)", true),
        ("from", "string", "From (YYYY-MM-DD, UTC)", true),
        ("to", "string", "To (YYYY-MM-DD, UTC, inclusive)", true),
        ("period", "string", "Candles: M1, M5, M15, H1, Tick500, Volume100, Range20… (M5 by default)", false),
        ("parameters", "object", "Fixed parameters {Name: value}; whatever is missing uses the default value", false),
        ("capital", "number", "Starting capital (10000)", false),
        ("commission_percent", "number", "Commission % of the notional per side (0.04). For futures use 0 and commission_per_unit", false),
        ("commission_per_unit", "number", "Fixed commission per contract or unit and per side (e.g. 2.5 on CME futures); it is added to the percentage one", false),
        ("slippage_ticks", "integer", "Slippage in ticks on market orders and stops (1)", false),
        ("timezone", "string", "The session's time zone (e.g. America/New_York); UTC by default", false),
        ("session_start_hour", "number", "Local session start hour (0)", false),
    };

    private static JsonArray Tools()
    {
        JsonObject Tool(string name, string description, JsonObject schema) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = schema,
        };

        var optimizeProps = RangeProps.Concat(new[]
        {
            ("ranges", "object", "Ranges {Parameter: {min, max, step}}; without this it uses the strategy's [Optimize] ones", false),
            ("metric", "string", "Metric to maximize: NetProfit, ProfitFactor, Sharpe, Sortino, RecoveryFactor, NetProfitOverDrawdown, WinRate, Expectancy (NetProfitOverDrawdown by default)", false),
            ("min_trades", "integer", "Minimum trades for a combination to count (10)", false),
        }).ToArray();

        return new JsonArray
        {
            Tool("list_strategies", "Lists the strategies (.cs files) with their compilation status, their parameters (with the [Optimize] ranges) and their warm-up: " +
                                    "the indicators created in OnInit with their period (for example ATR(14), code constants included) and the candles they take to be ready " +
                                    "(with the default parameters and, if it differs, with the [Optimize] maximums). Each walk-forward window starts with no history and spends those first candles warming up.", Schema()),
            Tool("compile_strategy", "Compiles a strategy and returns the errors (line and message), its parameters and its warm-up (indicators with their period and the candles they need).", Schema(("file", "string", "File without .cs", true))),
            Tool("chart_snapshot", "Look at the chart: it runs the strategy over a range and saves an image with the candles, its entries and exits, its drawings and its lines. It returns the image path (look at it with Read) and, in local time, the range's candles (with the values of the strategy's lines), the trades, the drawings and its log. Useful to understand why it entered or exited at a given time.",
                Schema(("file", "string", "Strategy (file without .cs)", true), ("feed", "string", "Connection (as in list_data)", true), ("symbol", "string", "Symbol", true),
                    ("period", "string", "Period: M1, M5, M15, H1…", true), ("from", "string", "From, local time: YYYY-MM-DD HH:mm (or just the date)", true),
                    ("to", "string", "To, local time: YYYY-MM-DD HH:mm (or just the date = that whole day)", true),
                    ("parameters", "object", "Fixed parameters {Name: value}; whatever is missing uses the default value", false),
                    ("warmup_days", "number", "Previous days to warm up the indicators (3 by default)", false),
                    ("timezone", "string", "Time zone (Trading Hub's by default)", false))),
            Tool("list_indicators", "The user's own indicators (their indicators folder): files, classes and display names.", Schema()),
            Tool("compile_indicator", "Compiles the user's own indicators and returns the errors of the requested file (line and message). If it compiles, it shows up on the chart under Indicators → “My indicators”.",
                Schema(("file", "string", "File without .cs (in the indicators folder)", true))),
            Tool("list_data", "Tick-by-tick data stored per instrument (available days). The backtests use only this data.", Schema()),
            Tool("download_data", "Downloads complete days of ticks from the public files (Binance Futures/Spot and Bybit; ~5 s per day). Up to 62 days per call.",
                Schema(("feed", "string", "binance-futures, binance-spot or bybit-linear", true), ("symbol", "string", "Symbol", true),
                    ("from", "string", "First day YYYY-MM-DD", true), ("to", "string", "Last day YYYY-MM-DD (inclusive)", true))),
            Tool("run_backtest", "Tick-by-tick backtest with commissions and slippage. It returns the full metrics and the run's id (it is kept in the history).",
                Schema(RangeProps.Concat(new[]
                {
                    ("monte_carlo_runs", "integer", "If > 0, it adds Monte Carlo (reshuffling trades) with that many runs", false),
                    ("include_trades", "boolean", "Include the first and last trades", false),
                }).ToArray())),
            Tool("run_optimization", "Grid optimization (in parallel) of the parameters. It returns the best combinations. Watch out for overfitting: validate with walk-forward.",
                Schema(optimizeProps.Concat(new[]
                {
                    ("top", "integer", "How many combinations to return (10)", false),
                    ("max_combinations", "integer", "Cap on combinations (5000)", false),
                }).ToArray())),
            Tool("run_walk_forward", "Walk-forward: in each window it chooses parameters looking only at that window and tests them out of sample on the next one. It runs each combination just once " +
                                     "over the whole history (repeated ticks merged, indicators warm as they are live) and slices it per window, searching the full grid if there are ≤ 300 combinations " +
                                     "or with Bayesian optimization (TPE) beyond that, picks the best plateau (not the best peak) and measures overfitting: PBO and the paths of combinatorial purged " +
                                     "cross-validation (CPCV). It returns the chained out-of-sample result. If you request the same one while it is already running, it waits for the running one (do not relaunch it).",
                Schema(optimizeProps.Concat(new[]
                {
                    ("in_sample_days", "integer", "Days in each optimization window", true),
                    ("out_sample_days", "integer", "Days in each out-of-sample window", true),
                    ("anchored", "boolean", "Windows anchored to the start (false = rolling)", false),
                    ("search", "string", "Search: auto (grid up to 300 combinations, otherwise Bayesian), grid or bayesian", false),
                    ("budget", "integer", "Combinations the Bayesian search measures in each window (80)", false),
                    ("estimate_only", "boolean", "Only estimate how long it will take (it runs one combination to measure; it stores nothing)", false),
                }).ToArray())),
            Tool("list_runs", "A strategy's stored runs (backtests, optimizations, walk-forwards) with their metrics.",
                Schema(("file", "string", "File without .cs", true), ("limit", "integer", "How many (20)", false))),
            Tool("analyze_portfolio", "Combines several strategies (stored runs) into a portfolio: joint metrics, the correlation matrix of the daily results and the benefit of diversifying.",
                Schema(("components", "array", "List of {file, run_id (optional: if missing, the last backtest), weight (optional, 1)}", true),
                    ("capital", "number", "Portfolio capital (10000)", false))),
            Tool("suggest_portfolio", "Searches the latest backtests of every strategy for the lightly correlated combination that improves the group's Sharpe the most.",
                Schema(("max_components", "integer", "Maximum strategies (5)", false), ("max_correlation", "number", "Maximum correlation between two strategies (0.5)", false),
                    ("capital", "number", "Portfolio capital (10000)", false))),
            Tool("live_portfolio", "The live book (read only): every strategy trading (or that has traded) algorithmically in Trading Hub, with its live parameters, current size, position, result, trades, win rate, profit factor (total and over the last 20), current and maximum drawdown, and its latest trades (with prices and quantity). " +
                                   "It says which contract each one executes on (for example MNQ for an NQ strategy): quantities and result come out in that contract.",
                Schema(("recent_trades", "integer", "How many recent trades per strategy (10)", false))),
            Tool("propose_sizing", "Proposes size changes for strategies in the live book. It applies NOTHING: they stay pending and the user approves or rejects each one in Trading Hub (Live → Proposals).",
                Schema(("summary", "string", "Summary of the analysis in 1 to 3 lines", true),
                    ("items", "array", "List of {key (from live_portfolio), proposed_size (multiplier: 1 = normal, 0 = pause, maximum 3), reason}", true))),
            Tool("save_report", "Builds and stores the PROFESSIONAL REPORT (Trading Hub generates it with every metric, table and chart from the runs: summary, data and assumptions, profitability, trades, risk, longs/shorts, monthly, by hour and day, drawdowns, optimization with sensitivity, walk-forward, Monte Carlo and portfolio). You contribute the verdict and the analysis text. It is viewed in Backtesting → Reports.",
                Schema(("title", "string", "Short report title", true),
                    ("verdict", "string", "winner, promising, loser or discarded", true),
                    ("file", "string", "Strategy evaluated (without .cs); if the run_ids are missing, its latest runs are used", false),
                    ("backtest_run_id", "string", "Main backtest run (ideally with Monte Carlo)", false),
                    ("optimization_run_id", "string", "Optimization run", false),
                    ("walk_forward_run_id", "string", "Walk-forward run", false),
                    ("portfolio", "array", "Optional: portfolio components [{file, run_id, weight}] to include the portfolio section", false),
                    ("capital", "number", "Portfolio capital (10000)", false),
                    ("summary", "string", "Executive summary of 2 to 5 lines with the conclusion", true),
                    ("analysis", "string", "Analysis in Markdown: what works, what doesn't, robustness, overfitting, comparison between versions", true),
                    ("risks", "string", "Risks in Markdown (a list)", false),
                    ("next_steps", "string", "Next steps in Markdown (a list)", false),
                    ("extra_markdown", "string", "Optional extra sections (for example, code changes)", false))),
        };
    }

    private static async Task<JsonNode> CallAsync(JsonNode? p)
    {
        string name = p?["name"]?.GetValue<string>() ?? "";
        var args = p?["arguments"] as JsonObject ?? new JsonObject();
        try
        {
            JsonNode result = name switch
            {
                "list_strategies" => ListStrategies(),
                "compile_strategy" => CompileStrategy(Str(args, "file")),
                "chart_snapshot" => await ChartSnapshotAsync(args),
                "list_indicators" => ListIndicators(),
                "compile_indicator" => CompileIndicator(Str(args, "file")),
                "list_data" => ListData(),
                "download_data" => await DownloadDataAsync(args),
                "run_backtest" => await RunBacktestAsync(args),
                "run_optimization" => await RunOptimizationAsync(args),
                "run_walk_forward" => await RunWalkForwardAsync(args),
                "list_runs" => ListRuns(args),
                "analyze_portfolio" => AnalyzePortfolio(args),
                "live_portfolio" => LivePortfolio(args),
                "propose_sizing" => ProposeSizing(args),
                "suggest_portfolio" => SuggestPortfolio(args),
                "save_report" => SaveReport(args),
                _ => throw new ArgumentException($"Unknown tool: {name}"),
            };
            return Content(result.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), false);
        }
        catch (Exception ex)
        {
            return Content("Error: " + ex.Message, true);
        }
    }

    private static JsonObject Content(string text, bool isError) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError,
    };

    // ===================================================================== arguments

    private static string Str(JsonObject a, string key, string? def = null)
    {
        var v = a[key];
        if (v == null) return def ?? throw new ArgumentException($"“{key}” is missing.");
        return v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
    }

    private static double Num(JsonObject a, string key, double def)
    {
        var v = a[key];
        if (v == null) return def;
        return v.GetValueKind() == JsonValueKind.String ? double.Parse(v.GetValue<string>(), Inv) : v.GetValue<double>();
    }

    private static bool Bool(JsonObject a, string key, bool def) => a[key] is { } v ? v.GetValueKind() == JsonValueKind.True || (v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == "true") : def;

    private static DateTime Date(JsonObject a, string key, bool endOfDay)
    {
        var s = Str(a, key);
        if (!DateTime.TryParse(s, Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
            throw new ArgumentException($"Invalid date in “{key}”: {s} (use YYYY-MM-DD)");
        d = DateTime.SpecifyKind(d, DateTimeKind.Utc);
        // "to" with a date only includes that whole day
        return endOfDay && d.TimeOfDay == TimeSpan.Zero ? d.AddDays(1) : d;
    }

    private static Dictionary<string, object?> Params(JsonObject a)
    {
        var res = new Dictionary<string, object?>();
        if (a["parameters"] is not JsonObject o) return res;
        foreach (var (k, v) in o)
        {
            if (v == null) continue;
            res[k] = v.GetValueKind() switch
            {
                JsonValueKind.Number => v.GetValue<double>(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => v.ToString(),
            };
        }

        return res;
    }

    // ===================================================================== strategies

    /// <summary>History folder (TRADINGHUB_BACKTESTS_DIR changes it, for tests).</summary>
    private static string StoreRoot => Environment.GetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR") is { Length: > 0 } dir ? dir : AppPaths.Backtests;

    private static StrategyCatalog Catalog
    {
        get
        {
            if (_catalog != null) return _catalog;
            _catalog = new StrategyCatalog();
            StrategyStore.Root = StoreRoot; // the catalog overwrites it when it is created
            return _catalog;
        }
    }

    private static StrategyFile FileOf(string file)
    {
        Catalog.Refresh();
        file = Path.GetFileNameWithoutExtension(file);
        var f = Catalog.Files.FirstOrDefault(x => x.FileName.Equals(file, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The strategy “{file}” does not exist. Use list_strategies.");
        var res = Catalog.Compile(f);
        if (!res.Success || f.Type == null)
        {
            var errs = res.Diagnostics.Where(d => d.Severity == "error").Take(10).Select(d => $"line {d.Line}: {d.Message}");
            throw new ArgumentException($"“{file}” does not compile: {string.Join(" | ", errs)}");
        }

        return f;
    }

    private static JsonArray ParametersOf(Type t)
    {
        var arr = new JsonArray();
        var instance = (Strategy)Activator.CreateInstance(t)!;
        foreach (var p in Strategy.Parameters(t))
        {
            var o = new JsonObject
            {
                ["name"] = p.Name,
                ["label"] = Strategy.ParameterName(p),
                ["type"] = p.PropertyType.IsEnum ? "enum" : p.PropertyType.Name.ToLowerInvariant(),
                ["value"] = JsonValue.Create(p.GetValue(instance) is IFormattable fv ? fv.ToString(null, Inv) : p.GetValue(instance)?.ToString()),
            };
            if (p.PropertyType.IsEnum) o["options"] = new JsonArray(Enum.GetNames(p.PropertyType).Select(n => (JsonNode)n).ToArray());
            if (p.GetCustomAttributes(typeof(OptimizeAttribute), true).FirstOrDefault() is OptimizeAttribute opt)
                o["optimize"] = new JsonObject { ["min"] = opt.Min, ["max"] = opt.Max, ["step"] = opt.Step };
            arr.Add(o);
        }

        return arr;
    }

    /// <summary>
    /// Warm-up candles: until the indicators created in OnInit are ready (with the default parameters and,
    /// if it differs, with the [Optimize] maximums). Each walk-forward window starts with no history and spends them.
    /// </summary>
    private static JsonObject WarmupOf(Type t)
    {
        var w = StrategyWarmup.Measure(t);
        var o = new JsonObject
        {
            ["bars"] = w.Bars,
            ["indicators"] = new JsonArray(w.Indicators.Select(n => (JsonNode)n).ToArray()),
        };
        var max = new Dictionary<string, object?>();
        foreach (var p in Strategy.Parameters(t))
            if (p.GetCustomAttributes(typeof(OptimizeAttribute), true).FirstOrDefault() is OptimizeAttribute opt)
                max[p.Name] = opt.Max;
        if (max.Count > 0 && StrategyWarmup.Measure(t, max) is { Error: null } wm && wm.Bars != w.Bars) o["bars_at_optimize_max"] = wm.Bars;
        // “bars” is not the whole warm-up: this is always spelled out to the agent
        var note = "“bars” counts only the indicators the strategy creates in OnInit: it does not include anything it reads from the candles by hand (Bars[n]) " +
                   "or indicators it creates later. If the code does that, add those candles yourself.";
        if (!w.Complete) note = (w.Error != null ? $"It could not be measured in full ({w.Error}). " : $"At {StrategyWarmup.MaxBars} candles they were still not ready. ") + note;
        o["note"] = note;
        return o;
    }

    private static JsonNode ListStrategies()
    {
        Catalog.Refresh();
        var arr = new JsonArray();
        foreach (var f in Catalog.Files)
        {
            var res = Catalog.Compile(f);
            var o = new JsonObject
            {
                ["file"] = f.FileName,
                ["name"] = f.DisplayName,
                ["compiles"] = res.Success && f.Type != null,
                ["errors"] = res.Diagnostics.Count(d => d.Severity == "error"),
            };
            if (f.Type != null)
            {
                o["description"] = Strategy.Description(f.Type);
                o["parameters"] = ParametersOf(f.Type);
                o["warmup"] = WarmupOf(f.Type);
                o["runs"] = StrategyStore.List(f.Id).Count;
            }

            arr.Add(o);
        }

        return new JsonObject { ["folder"] = AppPaths.Strategies, ["strategies"] = arr };
    }

    internal static JsonNode CompileStrategy(string file)
    {
        Catalog.Refresh();
        file = Path.GetFileNameWithoutExtension(file);
        var f = Catalog.Files.FirstOrDefault(x => x.FileName.Equals(file, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"The strategy “{file}” does not exist.");
        var res = Catalog.Compile(f, force: true);
        var errors = new JsonArray(res.Diagnostics.Where(d => d.Severity == "error").Take(30)
            .Select(d => (JsonNode)new JsonObject { ["line"] = d.Line, ["column"] = d.Column, ["id"] = d.Id, ["message"] = d.Message }).ToArray());
        var o = new JsonObject { ["file"] = f.FileName, ["ok"] = res.Success && f.Type != null, ["errors"] = errors };
        if (f.Type != null)
        {
            o["parameters"] = ParametersOf(f.Type);
            o["warmup"] = WarmupOf(f.Type);
        }

        return o;
    }

    // ===================================================================== the chart (what Claude looks at)

    /// <summary>Folder for the chart captures Claude looks at (the last 60 are kept).</summary>
    internal static string CapturesDir => Path.Combine(AppPaths.Root, "Lab captures");

    private static DateTime LocalArg(JsonObject a, string key, TimeZoneInfo tz, bool endOfDay)
    {
        string text = Str(a, key).Trim().Replace('T', ' ');
        string[] formats = { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm", "yyyy-MM-dd" };
        if (!DateTime.TryParseExact(text, formats, Inv, DateTimeStyles.None, out var local)) throw new ArgumentException($"“{key}” has to be YYYY-MM-DD HH:mm (local time).");
        if (endOfDay && text.Length <= 10) local = local.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
    }

    /// <summary>
    /// Runs the strategy over the range (with previous days for the indicators), builds the chart with its trades, drawings
    /// and lines, saves it as an image and returns what happened candle by candle (local time).
    /// </summary>
    internal static async Task<JsonNode> ChartSnapshotAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        string tzId = a["timezone"] is { } tzNode ? tzNode.GetValue<string>() : AppSettings.Load().TimeZoneId ?? TimeZoneInfo.Local.Id;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { tz = TimeZoneInfo.Local; }
        string periodCode = Str(a, "period", "M5");
        if (!ChartPeriod.TryParse(periodCode, out var period)) throw new ArgumentException($"Invalid period: {periodCode}");
        var fromUtc = LocalArg(a, "from", tz, false);
        var toUtc = LocalArg(a, "to", tz, true);
        if (toUtc <= fromUtc) throw new ArgumentException("“to” has to be later than “from”.");
        double warmDays = Num(a, "warmup_days", period.IsTimeBased && period.ApproxDuration >= TimeSpan.FromHours(1) ? 15 : 3);
        var inst = await ResolveInstrumentAsync(Str(a, "feed"), Str(a, "symbol").ToUpperInvariant());
        var ticks = await Task.Run(() => TickData.Load(AppPaths.Ticks, inst, fromUtc.AddDays(-warmDays), toUtc, null, CancellationToken.None));
        if (ticks.Count == 0) throw new ArgumentException($"There are no stored ticks for {inst.Symbol} ({inst.FeedId}) in that range. Check list_data or download days with download_data.");

        // the strategy over everything loaded (as on the chart: no costs and no closing at the end)
        var strategy = file.Create();
        strategy.SetParameters(ParamsFor(a, file));
        var settings = new BacktestSettings
        {
            FeedId = inst.FeedId, Symbol = inst.Symbol, FromUtc = ticks.FirstTime, ToUtc = ticks.LastTime.AddSeconds(1), Period = periodCode,
            CommissionPercent = 0, SlippageTicks = 0, TimeZoneId = tz.Id, CloseAtEnd = false, InitialCapital = 10_000,
        };
        var result = await Task.Run(() => new BacktestEngine(strategy, ticks, settings) { ContextHistory = MarketConditions.HourlyBefore(inst.FeedId, inst.Symbol, ticks.FirstTime) }.Run(CancellationToken.None));
        if (!result.Ok) throw new InvalidOperationException("The strategy failed: " + result.Error);

        // the chart, just like in the app
        var data = new InstrumentData(inst, new TradingHub.Feeds.SimulatedFeed());
        for (int i = ticks.Start; i < ticks.End; i++) data.Trades.Add(ticks.Get(i));
        var model = new TradingHub.Charting.ChartModel(data, period, new TradingHub.Charting.ChartSettings { TimeZoneId = tz.Id, Style = TradingHub.Charting.ChartStyle.Candles });
        model.Viewport.BarWidth = 12;
        model.Rebuild();
        var color = new SkiaSharp.SKColor(180, 120, 255);
        var (signals, links) = StrategyDrawingMap.Trades(result.Trades, result.OpenPosition);
        var drawings = strategy.Canvas.Drawings();
        var plots = strategy.Canvas.Plots();
        model.Strategies.Add(new TradingHub.Charting.StrategyOverlay
        {
            Name = file.DisplayName,
            Status = "lab capture",
            Color = color,
            Signals = signals,
            Links = links,
            Shapes = drawings.Select(d => StrategyDrawingMap.ToShape(d, color)).ToList(),
            Lines = plots.Select(pl => StrategyDrawingMap.ToLine(pl.Name, pl.Color, pl.Width, pl.Style, pl.Points, color)).ToList(),
        });
        const int W = 1600, H = 900;
        var renderer = new TradingHub.Charting.Rendering.ChartRenderer();
        var state = new TradingHub.Charting.Rendering.ChartRenderState { Crosshair = false };
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(W, H));
        renderer.Render(surface.Canvas, model, W, H, state);
        model.CenterOn(fromUtc, toUtc);
        renderer.Render(surface.Canvas, model, W, H, state);
        Directory.CreateDirectory(CapturesDir);
        string name = $"{file.FileName}-{inst.Symbol}-{periodCode}-{TimeZoneInfo.ConvertTimeFromUtc(fromUtc, tz):yyyyMMdd-HHmm}.png";
        string path = Path.Combine(CapturesDir, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)));
        using (var image = surface.Snapshot())
        using (var png = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 92))
            await File.WriteAllBytesAsync(path, png.ToArray());
        foreach (var old in new DirectoryInfo(CapturesDir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Skip(60)) old.Delete();

        // what happened in the range, in local time
        string L(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, tz).ToString("yyyy-MM-dd HH:mm", Inv);
        var plotAt = plots.Select(pl => (pl.Name, Values: pl.Points.Where(x => x.Value != null).GroupBy(x => x.Time).ToDictionary(g => g.Key, g => g.Last().Value!.Value))).ToList();
        var bars = new JsonArray();
        var series = model.Series;
        for (int i = 0, n = 0; i < series.Count && n < 200; i++)
        {
            var c = series[i];
            if (c.Time < fromUtc || c.Time >= toUtc) continue;
            n++;
            var bar = new JsonObject
            {
                ["time"] = L(c.Time),
                ["open"] = series.TicksToPrice(c.OpenTicks),
                ["high"] = series.TicksToPrice(c.HighTicks),
                ["low"] = series.TicksToPrice(c.LowTicks),
                ["close"] = series.TicksToPrice(c.CloseTicks),
                ["volume"] = c.Volume,
            };
            foreach (var (plotName, values) in plotAt.Take(6))
                if (values.TryGetValue(c.Time, out var v)) bar[plotName] = v;
            bars.Add(bar);
        }

        bool InWindow(DateTime t) => t >= fromUtc && t < toUtc;
        var trades = new JsonArray(result.Trades.Where(t => InWindow(t.EntryTime) || InWindow(t.ExitTime)).Take(50).Select(t => (JsonNode)new JsonObject
        {
            ["side"] = t.IsLong ? "long" : "short",
            ["entry"] = L(t.EntryTime), ["entry_price"] = t.EntryPrice, ["entry_order"] = t.EntryName,
            ["exit"] = L(t.ExitTime), ["exit_price"] = t.ExitPrice, ["exit_order"] = t.ExitName,
            ["net_pnl"] = Math.Round(t.NetPnl, 2),
        }).ToArray());
        var shapes = new JsonArray(drawings.Where(d => InWindow(d.Time1) || InWindow(d.Time2) || d.Kind == DrawingKind.HorizontalLine).Take(80).Select(d => (JsonNode)new JsonObject
        {
            ["id"] = d.Id, ["kind"] = d.Kind.ToString(), ["time"] = d.Time1 == DateTime.MinValue ? "" : L(d.Time1), ["time2"] = d.Time2 == DateTime.MinValue ? "" : L(d.Time2),
            ["price"] = d.Price1, ["price2"] = d.Price2, ["text"] = d.Text,
        }).ToArray());
        var log = new JsonArray(result.Log.Select(line =>
                DateTime.TryParseExact(line.Length >= 19 ? line[..19] : "", "yyyy-MM-dd HH:mm:ss", Inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                    ? (Time: (DateTime?)t, Line: line) : (Time: null, Line: line))
            .Where(x => x.Time is { } t && InWindow(t)).Take(100).Select(x => (JsonNode)$"{L(x.Time!.Value)}  {x.Line[19..].Trim()}").ToArray());
        return new JsonObject
        {
            ["image"] = path,
            ["timezone"] = tz.Id,
            ["note"] = "Look at the image with Read (it is a PNG). The candles carry their open time; a market order sent on a candle's close fills on the next one.",
            ["bars"] = bars,
            ["trades"] = trades,
            ["open_position"] = result.OpenPosition is { } op ? new JsonObject { ["side"] = op.IsLong ? "long" : "short", ["since"] = L(op.EntryTime), ["price"] = op.AveragePrice, ["quantity"] = op.Quantity } : null,
            ["drawings"] = shapes,
            ["log"] = log,
        };
    }

    // ===================================================================== the user's own indicators

    private static List<string> IndicatorFiles() =>
        Directory.Exists(AppPaths.UserIndicators)
            ? Directory.EnumerateFiles(AppPaths.UserIndicators, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains("/bin/") && !f.Contains("/obj/")).OrderBy(f => f).ToList()
            : new List<string>();

    /// <summary>Compiles all of the user's own indicators together (like the app): the display names of the ones that compile.</summary>
    private static (TradingHub.Scripting.ScriptCompileResult Result, List<(string Class, string Name)> Indicators) CompileIndicators()
    {
        var res = new TradingHub.Scripting.ScriptCompiler().Compile(IndicatorFiles(), "TradingHubUserIndicatorsLab");
        var list = new List<(string, string)>();
        if (res.Success && res.Assembly != null)
            foreach (var t in res.Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(TradingHub.Indicators.Indicator).IsAssignableFrom(t)))
                list.Add((t.Name, TradingHub.Indicators.Indicator.DefaultName(t)));
        return (res, list);
    }

    internal static JsonNode ListIndicators()
    {
        var files = IndicatorFiles();
        var (res, indicators) = CompileIndicators();
        return new JsonObject
        {
            ["folder"] = AppPaths.UserIndicators,
            ["files"] = new JsonArray(files.Select(f => (JsonNode)Path.GetRelativePath(AppPaths.UserIndicators, f)).ToArray()),
            ["compiles"] = res.Success,
            ["indicators"] = new JsonArray(indicators.Select(i => (JsonNode)new JsonObject { ["class"] = i.Class, ["name"] = i.Name }).ToArray()),
        };
    }

    internal static JsonNode CompileIndicator(string file)
    {
        file = Path.GetFileNameWithoutExtension(file);
        var path = IndicatorFiles().FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(file, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ArgumentException($"The indicator “{file}” does not exist in {AppPaths.UserIndicators}.");
        var (res, indicators) = CompileIndicators();
        var errors = res.Diagnostics.Where(d => d.Severity == "error").ToList();
        // this file's errors first; errors in other files also stop everything from compiling
        var mine = errors.Where(d => string.IsNullOrEmpty(d.File) || Path.GetFullPath(d.File) == Path.GetFullPath(path)).Take(30);
        var others = errors.Where(d => !string.IsNullOrEmpty(d.File) && Path.GetFullPath(d.File) != Path.GetFullPath(path)).Take(10);
        return new JsonObject
        {
            ["file"] = Path.GetFileName(path),
            ["ok"] = res.Success,
            ["errors"] = new JsonArray(mine.Select(d => (JsonNode)new JsonObject { ["line"] = d.Line, ["column"] = d.Column, ["id"] = d.Id, ["message"] = d.Message }).ToArray()),
            ["errors_in_other_files"] = new JsonArray(others.Select(d => (JsonNode)new JsonObject { ["file"] = Path.GetFileName(d.File), ["line"] = d.Line, ["message"] = d.Message }).ToArray()),
            ["indicators"] = new JsonArray(indicators.Select(i => (JsonNode)new JsonObject { ["class"] = i.Class, ["name"] = i.Name }).ToArray()),
        };
    }

    // ===================================================================== data

    private static JsonNode ListData()
    {
        var arr = new JsonArray();
        foreach (var s in HistorySources.Datasets(AppPaths.Ticks))
        {
            decimal? tick = HistorySources.StoredTickSize(AppPaths.Ticks, s.FeedId, s.Symbol);
            arr.Add(new JsonObject
            {
                ["feed"] = s.FeedId,
                ["symbol"] = s.Symbol,
                ["source"] = HistorySources.Describe(s),
                ["tick_size"] = tick is { } t ? (double)t : null,
                ["first_day"] = s.First.ToString("yyyy-MM-dd", Inv),
                ["last_day"] = s.Last.ToString("yyyy-MM-dd", Inv),
                ["days"] = s.Days.Count,
                ["size_mb"] = Math.Round(s.Bytes / 1048576.0, 1),
            });
        }

        return new JsonObject
        {
            ["instruments"] = arr,
            ["note"] = "Days with stored ticks. If consecutive days are missing, the range can still be used but those days end up with no trades. For Binance/Bybit you can download more with download_data. " +
                       "Futures with Databento history: “rithmic” is the combined one (Rithmic and, where it is missing, Databento; use that one unless asked otherwise), “rithmic-only” is Rithmic alone and “databento” is Databento alone.",
        };
    }

    private static async Task<JsonNode> DownloadDataAsync(JsonObject a)
    {
        string feed = Str(a, "feed"), symbol = Str(a, "symbol").ToUpperInvariant();
        if (!PublicArchives.Supports(feed)) throw new ArgumentException("Public files are only available for binance-futures, binance-spot and bybit-linear.");
        var from = DateOnly.FromDateTime(Date(a, "from", false));
        var to = DateOnly.FromDateTime(Date(a, "to", false));
        if (to.DayNumber - from.DayNumber > 61) throw new ArgumentException("Up to 62 days per call.");
        var inst = await ResolveInstrumentAsync(feed, symbol, preferExchange: true);
        int ok = 0, skipped = 0, missing = 0;
        long trades = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            if (HistoricalData.HasDay(AppPaths.Ticks, feed, symbol, d))
            {
                skipped++;
                continue;
            }

            var list = await PublicArchives.DownloadDayAsync(inst, d, null, CancellationToken.None);
            if (list == null)
            {
                missing++;
                continue;
            }

            HistoricalData.WriteDay(AppPaths.Ticks, inst, d, list);
            trades += list.Count;
            ok++;
        }

        _ticks = null;
        return new JsonObject { ["downloaded_days"] = ok, ["already_had"] = skipped, ["not_published"] = missing, ["trades"] = trades };
    }

    /// <summary>Instrument with its tick and point value (from the exchange if possible; otherwise, from the file).</summary>
    private static async Task<Instrument> ResolveInstrumentAsync(string feed, string symbol, bool preferExchange = false)
    {
        if (preferExchange || feed.StartsWith("binance") || feed.StartsWith("bybit"))
        {
            try
            {
                TradingHub.Feeds.IMarketDataFeed? f = feed switch
                {
                    "binance-futures" => new TradingHub.Feeds.Binance.BinanceFeed(true),
                    "binance-spot" => new TradingHub.Feeds.Binance.BinanceFeed(false),
                    "bybit-linear" => new TradingHub.Feeds.Bybit.BybitFeed(),
                    _ => null,
                };
                if (f != null)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var list = await f.GetInstrumentsAsync(cts.Token);
                    f.Dispose();
                    var inst = list.FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                    if (inst != null) return inst;
                }
            }
            catch
            {
                // no network: it is built from the file
            }
        }

        decimal tick = HistorySources.StoredTickSize(AppPaths.Ticks, feed, symbol) ?? 0.01m;
        var spec = TradingHub.Feeds.Rithmic.RithmicFeed.SpecFor(symbol.Split(' ', ':')[0]);
        bool futures = MarketInfo.IsFutures(feed);
        return new Instrument
        {
            Symbol = symbol,
            FeedId = feed,
            Exchange = futures ? TradingHub.Market.FuturesCatalog.Find(symbol)?.Exchange ?? "CME" : feed,
            TickSize = spec?.Tick ?? tick,
            VolumeStep = futures || feed is TradingHub.Feeds.Alpaca.AlpacaFeed.FeedId or TradingHub.Feeds.Oanda.OandaFeed.FeedId ? 1 : 0.001m,
            PointValue = futures ? spec?.PointValue ?? 1 : 1,
        };
    }

    private static async Task<(BacktestSettings Settings, TickData Data)> PrepareAsync(JsonObject a, bool compress = false)
    {
        var settings = new BacktestSettings
        {
            FeedId = Str(a, "feed"),
            Symbol = Str(a, "symbol").ToUpperInvariant(),
            FromUtc = Date(a, "from", false),
            ToUtc = Date(a, "to", true),
            Period = Str(a, "period", "M5"),
            InitialCapital = (decimal)Num(a, "capital", 10_000),
            CommissionPercent = (decimal)Num(a, "commission_percent", 0.04),
            CommissionPerUnit = (decimal)Num(a, "commission_per_unit", 0),
            SlippageTicks = (int)Num(a, "slippage_ticks", 1),
            TimeZoneId = a["timezone"] is { } tz ? tz.GetValue<string>() : null,
            SessionStart = TimeSpan.FromHours(Num(a, "session_start_hour", 0)),
        };
        if (!ChartPeriod.TryParse(settings.Period, out _)) throw new ArgumentException($"Invalid period: {settings.Period}");
        if (settings.ToUtc <= settings.FromUtc) throw new ArgumentException("“to” has to be later than “from”.");

        // compressed (repeated ticks merged) for optimizing and validating: the same history with far fewer events
        string key = $"{settings.FeedId}|{settings.Symbol}|{settings.FromUtc:O}|{settings.ToUtc:O}|{(compress ? "c" : "")}";
        if (_ticks == null || _ticksKey != key)
        {
            _ticks = null;
            GC.Collect();
            var inst = await ResolveInstrumentAsync(settings.FeedId, settings.Symbol);
            var data = await Task.Run(() => TickData.Load(AppPaths.Ticks, inst, settings.FromUtc, settings.ToUtc, null, CancellationToken.None, compress).WithDepth(AppPaths.Ticks));
            if (data.Count == 0)
                throw new ArgumentException($"There are no stored ticks for {settings.Symbol} ({settings.FeedId}) in that range. Check list_data or download days with download_data.");
            _ticks = data;
            _ticksKey = key;
        }

        return (settings, _ticks);
    }

    // ===================================================================== runs

    private static JsonObject MetricsJson(PerformanceMetrics m) => new()
    {
        ["net_profit"] = Math.Round(m.NetProfit, 2),
        ["return_percent"] = Math.Round(m.ReturnPercent, 2),
        ["profit_factor"] = Math.Round(m.ProfitFactor, 2),
        ["trades"] = m.TotalTrades,
        ["win_rate"] = Math.Round(m.WinRate, 1),
        ["average_trade"] = Math.Round(m.AverageTrade, 2),
        ["payoff_ratio"] = Math.Round(m.PayoffRatio, 2),
        ["expectancy"] = Math.Round(m.Expectancy, 2),
        ["max_drawdown"] = Math.Round(m.MaxDrawdown, 2),
        ["max_drawdown_percent"] = Math.Round(m.MaxDrawdownPercent, 2),
        ["recovery_factor"] = Math.Round(m.RecoveryFactor, 2),
        ["sharpe"] = Math.Round(m.SharpeRatio, 2),
        ["sortino"] = Math.Round(m.SortinoRatio, 2),
        ["long_net_profit"] = Math.Round(m.LongNetProfit, 2),
        ["short_net_profit"] = Math.Round(m.ShortNetProfit, 2),
        ["max_consecutive_losses"] = m.MaxConsecutiveLosses,
        ["exposure_percent"] = Math.Round(m.ExposurePercent, 1),
        ["commission"] = Math.Round(m.Commission, 2),
    };

    private static JsonObject ParamsJson(IReadOnlyDictionary<string, object?> p)
    {
        var o = new JsonObject();
        foreach (var (k, v) in p) o[k] = JsonValue.Create(v is IFormattable f ? f.ToString(null, Inv) : v?.ToString());
        return o;
    }

    private static void Save(StrategyRun run, StrategyFile file)
    {
        run.Notes = string.IsNullOrEmpty(run.Notes) ? "Claude's lab" : run.Notes;
        StrategyStore.Save(run, File.ReadAllText(file.Path));
    }

    /// <summary>File holding a strategy's fixed parameters while it is being validated (a variant's filter).</summary>
    internal static string PinsPath => Path.Combine(AppPaths.Root, "lab-parametros-fijos.json");

    /// <summary>This strategy's fixed parameters (they cannot be changed or optimized).</summary>
    internal static Dictionary<string, object?> PinnedFor(StrategyFile file)
    {
        var res = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(PinsPath)) return res;
            using var doc = JsonDocument.Parse(File.ReadAllText(PinsPath));
            if (!doc.RootElement.TryGetProperty(file.FileName, out var pins) || pins.ValueKind != JsonValueKind.Object) return res;
            foreach (var p in pins.EnumerateObject()) res[p.Name] = p.Value.Clone();
        }
        catch (Exception)
        {
            // no readable fixed parameters: the ones the agent passed
        }

        return res;
    }

    /// <summary>The request's parameters with the fixed ones on top.</summary>
    private static Dictionary<string, object?> ParamsFor(JsonObject a, StrategyFile file)
    {
        var res = Params(a);
        foreach (var (k, v) in PinnedFor(file)) res[k] = v;
        return res;
    }

    private static async Task<JsonNode> RunBacktestAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        var (settings, data) = await PrepareAsync(a);
        var strategy = file.Create();
        strategy.SetParameters(ParamsFor(a, file));
        // the market context starts with the previous month, as it does live
        var res = await Task.Run(() => new BacktestEngine(strategy, data, settings) { ContextHistory = MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc) }.Run(CancellationToken.None));
        if (!res.Ok) throw new InvalidOperationException("The strategy failed: " + res.Error);
        var run = new StrategyRun
        {
            StrategyId = file.Id,
            StrategyName = file.DisplayName,
            Kind = RunKind.Backtest,
            Settings = settings,
            Parameters = res.Parameters,
            Metrics = res.Metrics,
            Trades = res.Trades,
            Equity = BacktestResult.Downsample(res.Equity, 2000),
            Ticks = res.Ticks,
            Bars = res.Bars,
            Seconds = res.Seconds,
        };

        var o = new JsonObject
        {
            ["run_id"] = run.Id,
            ["parameters"] = ParamsJson(res.Parameters),
            ["metrics"] = MetricsJson(res.Metrics),
            ["ticks"] = res.Ticks,
            ["bars"] = res.Bars,
            ["seconds"] = Math.Round(res.Seconds, 1),
            ["data_from"] = data.FirstTime.ToString("yyyy-MM-dd HH:mm", Inv),
            ["data_to"] = data.LastTime.ToString("yyyy-MM-dd HH:mm", Inv),
        };

        int mcRuns = (int)Num(a, "monte_carlo_runs", 0);
        if (mcRuns > 0 && res.Trades.Count > 2)
        {
            var mc = Optimizer.MonteCarlo(res.Trades, settings.InitialCapital, Math.Clamp(mcRuns, 100, 20000));
            run.MonteCarlo = mc;
            o["monte_carlo"] = new JsonObject
            {
                ["runs"] = mc.Runs,
                ["drawdown_p50"] = Math.Round(mc.DrawdownP50, 2),
                ["drawdown_p95"] = Math.Round(mc.DrawdownP95, 2),
                ["drawdown_p99"] = Math.Round(mc.DrawdownP99, 2),
                ["worst_drawdown"] = Math.Round(mc.WorstDrawdown, 2),
                ["ruin_probability_percent"] = Math.Round(mc.RuinProbabilityPercent, 2),
            };
        }

        if (Bool(a, "include_trades", false))
        {
            var sample = res.Trades.Take(5).Concat(res.Trades.Count > 10 ? res.Trades.Skip(res.Trades.Count - 5) : Array.Empty<ClosedTrade>());
            o["sample_trades"] = JsonSerializer.SerializeToNode(sample);
        }

        Save(run, file);
        return o;
    }

    private static List<ParameterRange> Ranges(JsonObject a, StrategyFile file)
    {
        var ranges = ParameterRange.FromStrategy(file.Type!, file.Create());
        if (a["ranges"] is JsonObject custom)
        {
            foreach (var r in ranges) r.Enabled = false;
            foreach (var (name, v) in custom)
            {
                var r = ranges.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"“{name}” is not a numeric parameter of the strategy.");
                if (v is not JsonObject spec) continue;
                r.Enabled = true;
                r.Min = Num(spec, "min", r.Min);
                r.Max = Num(spec, "max", r.Max);
                r.Step = Num(spec, "step", r.Step);
            }
        }

        // the fixed ones are not optimized
        var pinned = PinnedFor(file);
        foreach (var r in ranges.Where(r => pinned.ContainsKey(r.Name))) r.Enabled = false;
        if (!ranges.Any(r => r.Enabled)) throw new ArgumentException("There are no parameters to optimize: pass “ranges” or mark parameters with [Optimize].");
        return ranges;
    }

    private static FitnessMetric Metric(JsonObject a) =>
        Enum.TryParse<FitnessMetric>(Str(a, "metric", nameof(FitnessMetric.NetProfitOverDrawdown)), true, out var m) ? m : FitnessMetric.NetProfitOverDrawdown;

    private static async Task<JsonNode> RunOptimizationAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        var ranges = Ranges(a, file);
        long combos = Optimizer.Combinations(ranges.Where(r => r.Enabled));
        long max = (long)Num(a, "max_combinations", 5000);
        if (combos > max) throw new ArgumentException($"That is {combos:N0} combinations (cap {max:N0}). Narrow the ranges or widen the steps.");
        var (settings, data) = await PrepareAsync(a, Optimizer.CanCompress(file.Type!, Str(a, "period", "M5")));
        var metric = Metric(a);
        int minTrades = (int)Num(a, "min_trades", 10);
        var baseParams = ParamsFor(a, file);
        var res = await Task.Run(() => Optimizer.Optimize(file.Create, baseParams, ranges, data, settings, metric, minTrades, null, CancellationToken.None,
            contextHistory: MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc)));
        int top = (int)Num(a, "top", 10);
        var run = new StrategyRun
        {
            StrategyId = file.Id,
            StrategyName = file.DisplayName,
            Kind = RunKind.Optimization,
            Settings = settings,
            Parameters = res.Best?.Parameters ?? new(),
            Metrics = res.Best?.Metrics,
            Optimization = new OptimizationResult
            {
                Metric = res.Metric, Ranges = res.Ranges, Rows = res.Rows.Take(2000).ToList(), Combinations = res.Combinations, Seconds = res.Seconds,
                RecommendedIndex = res.RecommendedIndex < 2000 ? res.RecommendedIndex : -1,
            },
            Seconds = res.Seconds,
        };
        Save(run, file);
        return new JsonObject
        {
            ["run_id"] = run.Id,
            ["combinations"] = res.Combinations,
            ["seconds"] = Math.Round(res.Seconds, 1),
            ["metric"] = metric.ToString(),
            ["top"] = new JsonArray(res.Rows.Take(top).Select(r => (JsonNode)new JsonObject
            {
                ["parameters"] = ParamsJson(r.Parameters),
                ["fitness"] = double.IsFinite(r.Fitness) ? Math.Round(r.Fitness, 4) : null,
                ["metrics"] = MetricsJson(r.Metrics),
            }).ToArray()),
            ["profitable_combinations_percent"] = res.Rows.Count > 0 ? Math.Round(100.0 * res.Rows.Count(r => r.Metrics.NetProfit > 0) / res.Rows.Count, 1) : 0,
            ["recommended"] = res.Recommended is { } rec ? new JsonObject
            {
                ["parameters"] = ParamsJson(rec.Parameters),
                ["metrics"] = MetricsJson(rec.Metrics),
                ["note"] = "The best plateau (the average with its grid neighbours), not the best peak: that is the one worth using.",
            } : null,
        };
    }

    /// <summary>Walk-forwards currently running (the same request again waits for the running one, it does not start another).</summary>
    private static readonly ConcurrentDictionary<string, Task<JsonNode>> RunningWalkForwards = new();

    private static async Task<JsonNode> RunWalkForwardAsync(JsonObject a)
    {
        if (Bool(a, "estimate_only", false)) return await EstimateWalkForwardAsync(a);
        string key = a.ToJsonString();
        var task = RunningWalkForwards.GetOrAdd(key, _ => Task.Run(() => WalkForwardCoreAsync(a)));
        try
        {
            return await task;
        }
        finally
        {
            RunningWalkForwards.TryRemove(new KeyValuePair<string, Task<JsonNode>>(key, task));
        }
    }

    private static SearchMode SearchOf(JsonObject a) => Str(a, "search", "auto").Trim().ToLowerInvariant() switch
    {
        "grid" or "grilla" => SearchMode.Grid,
        "bayesian" or "bayesiana" or "tpe" => SearchMode.Bayesian,
        _ => SearchMode.Auto,
    };

    /// <summary>How long it will take: it runs one combination over the whole range and multiplies by the number to run.</summary>
    private static async Task<JsonNode> EstimateWalkForwardAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        var ranges = Ranges(a, file);
        bool compress = Optimizer.CanCompress(file.Type!, Str(a, "period", "M5"));
        var (settings, data) = await PrepareAsync(a, compress);
        var space = new ParameterSpace(ranges, file.Type!);
        int inDays = Math.Max(1, (int)Num(a, "in_sample_days", 30)), outDays = Math.Max(1, (int)Num(a, "out_sample_days", 10));
        double total = (settings.ToUtc - settings.FromUtc).TotalDays;
        int windows = Math.Max(1, (int)Math.Ceiling((total - inDays) / outDays));
        var mode = SearchOf(a);
        bool grid = mode == SearchMode.Grid || (mode == SearchMode.Auto && space.Size <= Optimizer.GridLimit);
        int budget = (int)Num(a, "budget", Optimizer.DefaultBudget);
        long configs = grid ? space.Size : Math.Min(space.Size, (long)budget * windows / 2 + budget);
        var s = file.Create();
        s.SetParameters(ParamsFor(a, file));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() => new BacktestEngine(s, data, settings) { ContextHistory = MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc) }.Run());
        double one = sw.Elapsed.TotalSeconds;
        int cores = Math.Max(1, Environment.ProcessorCount - 1);
        double estimate = one * configs / Math.Min(cores, Math.Max(1, configs)) * 1.15 + 3;
        return new JsonObject
        {
            ["events"] = data.Count,
            ["compressed"] = data.Compressed,
            ["search"] = grid ? "grid" : "bayesian",
            ["combinations_in_space"] = space.Size,
            ["combinations_to_run"] = configs,
            ["windows"] = windows,
            ["seconds_per_combination"] = Math.Round(one, 2),
            ["estimated_seconds"] = Math.Round(estimate),
            ["note"] = "Each combination is run just once over the whole range. If it takes too long, shrink the grid or the budget, or use a shorter range.",
        };
    }

    private static async Task<JsonNode> WalkForwardCoreAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        var ranges = Ranges(a, file);
        var (settings, data) = await PrepareAsync(a, Optimizer.CanCompress(file.Type!, Str(a, "period", "M5")));
        var metric = Metric(a);
        int inDays = (int)Num(a, "in_sample_days", 0), outDays = (int)Num(a, "out_sample_days", 0);
        if (inDays <= 0 || outDays <= 0) throw new ArgumentException("in_sample_days and out_sample_days have to be greater than zero.");
        int minTrades = (int)Num(a, "min_trades", 5);
        var baseParams = ParamsFor(a, file);
        bool anchored = Bool(a, "anchored", false);
        var search = SearchOf(a);
        int budget = (int)Num(a, "budget", 0);
        // each window starts its market context with whatever came before it (the month before the range included), as it does live
        var res = await Task.Run(() => Optimizer.WalkForward(file.Create, baseParams, ranges, data, settings, metric, inDays, outDays, anchored, minTrades, null, CancellationToken.None,
            MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc), search, budget));
        var run = new StrategyRun
        {
            StrategyId = file.Id,
            StrategyName = file.DisplayName,
            Kind = RunKind.WalkForward,
            Settings = settings,
            Parameters = baseParams, // the fixed ones (for example the context filter); the optimized ones live in each window
            Metrics = res.OutSampleMetrics,
            Trades = res.OutSampleTrades,
            Equity = BacktestResult.Downsample(res.OutSampleEquity, 2000),
            WalkForward = res,
            Seconds = res.Seconds,
        };
        Save(run, file);
        return new JsonObject
        {
            ["run_id"] = run.Id,
            ["windows"] = new JsonArray(res.Windows.Select(w => (JsonNode)new JsonObject
            {
                ["n"] = w.Number,
                ["in_sample"] = $"{w.InSampleFrom:yyyy-MM-dd} → {w.InSampleTo:yyyy-MM-dd}",
                ["out_sample"] = $"{w.OutSampleFrom:yyyy-MM-dd} → {w.OutSampleTo:yyyy-MM-dd}",
                ["best_parameters"] = ParamsJson(w.BestParameters),
                ["in_sample_net"] = Math.Round(w.InSample.NetProfit, 2),
                ["out_sample_net"] = Math.Round(w.OutSample.NetProfit, 2),
                ["out_sample_trades"] = w.OutSample.TotalTrades,
                ["efficiency"] = Math.Round(w.Efficiency, 2),
            }).ToArray()),
            ["out_of_sample_metrics"] = MetricsJson(res.OutSampleMetrics),
            ["walk_forward_efficiency"] = Math.Round(res.WalkForwardEfficiency, 2),
            ["profitable_windows_percent"] = Math.Round(res.ProfitableWindowsPercent, 1),
            ["search"] = res.Search == "grid" ? "grid" : "bayesian",
            ["combinations_run"] = res.Evaluated,
            ["combinations_in_space"] = res.SpaceSize,
            ["compression"] = Math.Round(res.Compression, 3),
            ["overfitting"] = res.Pbo is { } pbo ? new JsonObject
            {
                ["pbo"] = Math.Round(pbo, 3),
                ["best_loses_out_of_sample"] = res.PboLoss is { } l ? Math.Round(l, 3) : null,
                ["splits"] = res.PboSplits,
                ["note"] = "Half the history is used to pick the best combination and the other half is examined, in every possible way. best_loses_out_of_sample = the proportion in which it lost money " +
                           "(< 0.2 good, ≥ 0.5 overoptimized); pbo = the proportion in which it landed in the bottom half of the rest (a high value with everything winning is a plateau, not a problem).",
            } : null,
            ["cpcv_paths"] = res.CpcvPaths.Count > 0 ? new JsonArray(res.CpcvPaths.Select(p => (JsonNode)new JsonObject
            {
                ["n"] = p.Number,
                ["net"] = Math.Round(p.Net, 2),
                ["sharpe"] = Math.Round(p.Sharpe, 2),
                ["max_drawdown"] = Math.Round(p.MaxDrawdown, 2),
            }).ToArray()) : null,
            ["seconds"] = Math.Round(res.Seconds, 1),
        };
    }

    private static JsonNode ListRuns(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        int limit = (int)Num(a, "limit", 20);
        return new JsonObject
        {
            ["runs"] = new JsonArray(StrategyStore.List(file.Id).Take(limit).Select(r => (JsonNode)new JsonObject
            {
                ["run_id"] = r.Id,
                ["kind"] = r.Kind.ToString(),
                ["created"] = r.CreatedUtc.ToString("yyyy-MM-dd HH:mm", Inv),
                ["data"] = $"{r.FeedId} {r.Symbol} {r.Period} {r.FromUtc:yyyy-MM-dd} → {r.ToUtc:yyyy-MM-dd}",
                ["net_profit"] = Math.Round(r.NetProfit, 2),
                ["profit_factor"] = Math.Round(r.ProfitFactor, 2),
                ["max_drawdown"] = Math.Round(r.MaxDrawdown, 2),
                ["trades"] = r.Trades,
                ["win_rate"] = Math.Round(r.WinRate, 1),
                ["sharpe"] = Math.Round(r.Sharpe, 2),
                ["efficiency"] = r.Efficiency is double e ? Math.Round(e, 2) : null,
                ["parameters"] = r.ParametersText,
                ["notes"] = r.Notes,
            }).ToArray()),
        };
    }

    // ===================================================================== reports

    private static StrategyRun? LoadRun(StrategyFile file, string? runId, RunKind kind)
    {
        if (!string.IsNullOrWhiteSpace(runId))
            return StrategyStore.Load(file.Id, runId) ?? throw new ArgumentException($"Run {runId} of {file.FileName} does not exist. Use list_runs.");
        var latest = StrategyStore.List(file.Id).Where(r => r.Kind == kind).OrderByDescending(r => r.CreatedUtc).FirstOrDefault();
        return latest == null ? null : StrategyStore.Load(file.Id, latest.Id);
    }

    private static List<PortfolioComponent> Components(JsonArray list)
    {
        var res = new List<PortfolioComponent>();
        foreach (var item in list.OfType<JsonObject>())
        {
            var file = FileOf(Str(item, "file"));
            var run = LoadRun(file, item["run_id"]?.GetValue<string>(), RunKind.Backtest)
                      ?? throw new ArgumentException($"{file.FileName} has no stored backtests: run run_backtest first.");
            if (run.Trades is not { Count: > 0 }) throw new ArgumentException($"Run {run.Id} of {file.FileName} has no trades.");
            res.Add(PortfolioComponent.FromRun(run, (decimal)Num(item, "weight", 1)));
        }

        if (res.Count == 0) throw new ArgumentException("Pass at least one strategy in “components”.");
        return res;
    }

    private static JsonObject PortfolioJson(PortfolioResult r)
    {
        int n = r.Components.Count;
        var matrix = new JsonArray();
        for (int i = 0; i < n; i++)
        {
            var row = new JsonArray();
            for (int j = 0; j < n; j++) row.Add(Math.Round(r.Correlation[i, j], 3));
            matrix.Add(row);
        }

        return new JsonObject
        {
            ["period"] = $"{r.From:yyyy-MM-dd} → {r.To:yyyy-MM-dd}",
            ["no_overlap"] = r.NoOverlap,
            ["components"] = new JsonArray(r.Components.Select((c, i) => (JsonNode)new JsonObject
            {
                ["n"] = i + 1,
                ["strategy"] = c.StrategyName,
                ["run_id"] = c.RunId,
                ["label"] = c.Label,
                ["weight"] = c.Weight,
                ["metrics_in_common_period"] = MetricsJson(r.ComponentMetrics[i]),
            }).ToArray()),
            ["portfolio_metrics"] = MetricsJson(r.Metrics),
            ["correlation_matrix"] = matrix,
            ["average_correlation"] = Math.Round(r.AverageCorrelation, 3),
            ["max_correlation"] = Math.Round(r.MaxCorrelation, 3),
            ["sum_of_component_drawdowns"] = Math.Round(r.SumOfComponentDrawdowns, 2),
            ["drawdown_reduction_percent"] = Math.Round(r.DrawdownReductionPercent, 1),
            ["average_component_sharpe"] = Math.Round(r.AverageComponentSharpe, 2),
        };
    }

    private static JsonNode AnalyzePortfolio(JsonObject a)
    {
        var list = a["components"] as JsonArray ?? throw new ArgumentException("“components” is missing.");
        var comps = Components(list);
        return PortfolioJson(PortfolioAnalyzer.Analyze(comps, (decimal)Num(a, "capital", 10_000)));
    }

    private static JsonNode SuggestPortfolio(JsonObject a)
    {
        decimal capital = (decimal)Num(a, "capital", 10_000);
        Catalog.Refresh();
        var candidates = new List<PortfolioComponent>();
        foreach (var f in Catalog.Files)
        {
            if (!Catalog.Compile(f).Success || f.Type == null) continue;
            // the last backtest of each instrument and period
            foreach (var g in StrategyStore.List(f.Id).Where(r => r.Kind == RunKind.Backtest).GroupBy(r => (r.Symbol, r.Period)))
            {
                var run = StrategyStore.Load(f.Id, g.OrderByDescending(r => r.CreatedUtc).First().Id);
                if (run?.Trades is { Count: >= 5 }) candidates.Add(PortfolioComponent.FromRun(run));
            }
        }

        if (candidates.Count < 2) throw new ArgumentException("Backtests from at least two strategies (or instruments) are needed to build a portfolio.");
        var chosen = PortfolioAnalyzer.SuggestUncorrelated(candidates, capital, (int)Num(a, "max_components", 5), Num(a, "max_correlation", 0.5));
        if (chosen.Count == 0) throw new ArgumentException("No strategy has a positive result with at least 5 trades.");
        var result = PortfolioAnalyzer.Analyze(chosen.Select(i => candidates[i]).ToList(), capital);
        var o = PortfolioJson(result);
        o["candidates_considered"] = candidates.Count;
        o["candidates"] = new JsonArray(candidates.Select(c => (JsonNode)new JsonObject
        {
            ["strategy"] = c.StrategyName,
            ["run_id"] = c.RunId,
            ["label"] = c.Label,
            ["net_profit"] = Math.Round(c.Metrics.NetProfit, 2),
            ["sharpe"] = Math.Round(c.Metrics.SharpeRatio, 2),
        }).ToArray());
        return o;
    }

    private static string LiveRoot => Environment.GetEnvironmentVariable("TRADINGHUB_LIVE_DIR") is { Length: > 0 } dir ? dir : Path.Combine(AppPaths.Root, "Live");

    /// <summary>How many <paramref name="contract"/> equal 1 <paramref name="symbol"/> (10 MNQ = 1 NQ); null if unknown.</summary>
    private static decimal? ContractRatio(string symbol, string contract)
    {
        if (contract.Equals(symbol, StringComparison.OrdinalIgnoreCase)) return 1;
        decimal a = FuturesCatalog.Find(symbol)?.PointValue ?? 0, b = FuturesCatalog.Find(contract)?.PointValue ?? 0;
        return a > 0 && b > 0 ? a / b : null;
    }

    /// <summary>Latest live trades, each with the contract it was executed on (that of the fill that opened it).</summary>
    private static JsonArray LiveTrades(LiveRunnerInfo info, int recent)
    {
        var ledger = LiveStore.LoadLedger(info.Key, info.PointValue);
        return new JsonArray(ledger.Trades.TakeLast(recent).Select(t =>
        {
            // old fills carry no contract: they belong to the strategy's instrument
            string contract = ledger.Fills.FirstOrDefault(f => f.Time == t.EntryTime)?.Symbol ?? info.Symbol;
            var ratio = ContractRatio(info.Symbol, contract);
            return (JsonNode)new JsonObject
            {
                ["side"] = t.IsLong ? "long" : "short",
                ["entry"] = t.EntryTime.ToString("yyyy-MM-dd HH:mm", Inv),
                ["entry_price"] = t.EntryPrice,
                ["exit"] = t.ExitTime.ToString("yyyy-MM-dd HH:mm", Inv),
                ["exit_price"] = t.ExitPrice,
                ["contract"] = contract,
                ["quantity"] = t.Quantity,
                ["instrument_quantity"] = ratio is { } r ? Math.Round(t.Quantity / r, 4) : null,
                ["pnl"] = Math.Round(t.NetPnl, 2),
            };
        }).ToArray());
    }

    internal static JsonNode LivePortfolio(JsonObject a)
    {
        LiveStore.Root = LiveRoot;
        int recent = (int)Num(a, "recent_trades", 10);
        var stats = LiveStore.Stats();
        return new JsonObject
        {
            ["strategies"] = new JsonArray(stats.Select(s => (JsonNode)new JsonObject
            {
                ["key"] = s.Info.Key,
                ["strategy"] = s.Info.Strategy,
                ["instrument"] = $"{s.Info.FeedId}:{s.Info.Symbol} {s.Info.Period}",
                // which contract it sends orders on (MNQ for an NQ one with a small size) and how many of those make 1 of the instrument
                ["exec_symbol"] = s.Info.ExecSymbol ?? s.Info.Symbol,
                ["exec_ratio"] = ContractRatio(s.Info.Symbol, s.Info.ExecSymbol ?? s.Info.Symbol),
                ["account"] = s.Info.Account,
                ["running"] = s.Info.Running,
                ["parameters"] = JsonSerializer.SerializeToNode(s.Info.Parameters),
                ["size"] = s.Info.Size,
                ["position"] = s.Info.Position,
                ["open_pnl"] = Math.Round(s.Info.OpenPnL, 2),
                ["net"] = Math.Round(s.Net, 2),
                ["metrics"] = MetricsJson(s.Metrics),
                ["recent_profit_factor"] = s.RecentTrades > 0 ? Math.Round(Math.Min(s.RecentProfitFactor, 999), 2) : null,
                ["recent_expectancy"] = Math.Round(s.RecentExpectancy, 2),
                ["recent_trades_count"] = s.RecentTrades,
                ["current_drawdown"] = Math.Round(s.CurrentDrawdown, 2),
                ["last_trades"] = LiveTrades(s.Info, recent),
            }).ToArray()),
            ["pending_proposals"] = LiveStore.LoadProposals().Count(p => p.IsPending),
            ["note"] = "The sizes are multipliers of the quantity each strategy asks for (1 = normal, 0 = paused). To change them use propose_sizing: the user approves each change. " +
                       "Times in UTC. entry_price and exit_price are the last market price when the strategy recorded each fill (not the price the broker reports), " +
                       "so they are not good for measuring exact slippage. The live result comes from those prices and does not deduct commissions (each strategy's accounting does not record them): " +
                       "to compare it with a backtest, run that one without commission or add it yourself. " +
                       "position, quantity and pnl belong to the contract it was executed on (exec_symbol on each strategy, contract on each trade: for example MNQ for an NQ strategy) " +
                       "and already include the size multiplier. instrument_quantity is that quantity converted to the strategy's instrument (5 MNQ = 0.5 NQ; exec_ratio says how many execution contracts make 1 of the instrument) " +
                       "and pnl is what that quantity produced: to compare it with a backtest on the instrument, scale it (pnl × the backtest's quantity / instrument_quantity).",
        };
    }

    private static JsonNode ProposeSizing(JsonObject a)
    {
        LiveStore.Root = LiveRoot;
        var runners = LiveStore.LoadRunners();
        var list = a["items"] as JsonArray ?? throw new ArgumentException("“items” is missing.");
        var proposal = new SizingProposal { Source = "claude", Summary = Str(a, "summary", "") };
        foreach (var item in list.OfType<JsonObject>())
        {
            string key = Str(item, "key");
            var info = runners.FirstOrDefault(r => r.Key == key) ?? throw new ArgumentException($"The strategy “{key}” does not exist in the book. Use the keys from live_portfolio.");
            decimal size = Math.Clamp((decimal)Num(item, "proposed_size", (double)info.Size), 0, LiveSizing.MaxSize);
            if (size == info.Size) continue;
            proposal.Items.Add(new SizingItem
            {
                Key = key,
                Strategy = $"{info.Strategy} · {info.Symbol}",
                CurrentSize = info.Size,
                ProposedSize = Math.Round(size, 2),
                Reason = Str(item, "reason", ""),
            });
        }

        if (proposal.Items.Count == 0) return new JsonObject { ["created"] = false, ["note"] = "There are no changes compared with the current sizes." };
        LiveStore.AddProposal(proposal);
        return new JsonObject
        {
            ["created"] = true,
            ["proposal_id"] = proposal.Id,
            ["items"] = proposal.Items.Count,
            ["note"] = "It is pending: the user sees it in Live → Proposals and approves or rejects each change. Nothing has been applied yet.",
        };
    }

    private static JsonNode SaveReport(JsonObject a)
    {
        string title = Str(a, "title").Trim();
        string verdict = (a["verdict"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
        StrategyFile? file = a["file"] is { } fn ? FileOf(fn.GetValue<string>()) : null;
        var input = new ReportInput
        {
            Title = title,
            Verdict = ReportStore.Verdicts.FirstOrDefault(v => verdict.StartsWith(v[..5])) ?? "",
            StrategyFile = file?.FileName,
            StrategyName = file?.DisplayName,
            Description = file?.Type != null ? Strategy.Description(file.Type) : null,
            Summary = a["summary"]?.GetValue<string>() ?? "",
            Analysis = a["analysis"]?.GetValue<string>() ?? a["markdown"]?.GetValue<string>() ?? "",
            Risks = a["risks"]?.GetValue<string>() ?? "",
            NextSteps = a["next_steps"]?.GetValue<string>() ?? "",
            Extra = a["extra_markdown"]?.GetValue<string>() ?? "",
            PortfolioCapital = (decimal)Num(a, "capital", 10_000),
        };
        if (file != null)
        {
            input.Backtest = LoadRun(file, a["backtest_run_id"]?.GetValue<string>(), RunKind.Backtest);
            input.Optimization = LoadRun(file, a["optimization_run_id"]?.GetValue<string>(), RunKind.Optimization);
            input.WalkForward = LoadRun(file, a["walk_forward_run_id"]?.GetValue<string>(), RunKind.WalkForward);
        }

        if (a["portfolio"] is JsonArray pl && pl.Count > 0) input.Portfolio = PortfolioAnalyzer.Analyze(Components(pl), input.PortfolioCapital);
        if (input.Backtest == null && input.Portfolio == null && input.WalkForward == null)
            throw new ArgumentException("There are no runs for the report: run run_backtest (or pass a portfolio) before save_report.");
        var (markdown, images) = ReportBuilder.Build(input);
        var report = ReportStore.Save(title, markdown, file?.FileName, input.Verdict, images);
        return new JsonObject { ["saved"] = report.Path, ["verdict"] = report.Verdict, ["charts"] = images.Count };
    }
}
