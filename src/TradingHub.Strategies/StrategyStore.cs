using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingHub.Strategies;

public enum RunKind
{
    Backtest,
    Optimization,
    WalkForward,
}

/// <summary>A stored run (backtest, optimization or walk-forward) with all its detail.</summary>
public sealed class StrategyRun
{
    public string Id { get; set; } = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
    public string StrategyId { get; set; } = "";
    public string StrategyName { get; set; } = "";
    public RunKind Kind { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Notes { get; set; } = "";
    public bool Starred { get; set; }
    public string CodeHash { get; set; } = "";
    public BacktestSettings Settings { get; set; } = new();
    public Dictionary<string, object?> Parameters { get; set; } = new();
    public PerformanceMetrics? Metrics { get; set; }
    public List<ClosedTrade>? Trades { get; set; }
    public List<EquityPoint>? Equity { get; set; }
    public OptimizationResult? Optimization { get; set; }
    public WalkForwardResult? WalkForward { get; set; }
    public MonteCarloResult? MonteCarlo { get; set; }
    public long Ticks { get; set; }
    public int Bars { get; set; }
    public double Seconds { get; set; }

    public StrategyRunSummary Summary() => new()
    {
        Id = Id,
        Kind = Kind,
        CreatedUtc = CreatedUtc,
        Symbol = Settings.Symbol,
        FeedId = Settings.FeedId,
        Period = Settings.Period,
        FromUtc = Settings.FromUtc,
        ToUtc = Settings.ToUtc,
        NetProfit = Metrics?.NetProfit ?? 0,
        ProfitFactor = Metrics?.ProfitFactor ?? 0,
        MaxDrawdown = Metrics?.MaxDrawdown ?? 0,
        Trades = Metrics?.TotalTrades ?? 0,
        WinRate = Metrics?.WinRate ?? 0,
        Sharpe = Metrics?.SharpeRatio ?? 0,
        Efficiency = WalkForward?.WalkForwardEfficiency,
        Combinations = Optimization?.Combinations,
        Notes = Notes,
        Starred = Starred,
        CodeHash = CodeHash,
        ParametersText = string.Join(", ", Parameters.Select(kv => $"{kv.Key}={kv.Value}")),
    };
}

/// <summary>Lightweight summary for the history list.</summary>
public sealed class StrategyRunSummary
{
    public string Id { get; set; } = "";
    public RunKind Kind { get; set; }
    public DateTime CreatedUtc { get; set; }
    public string FeedId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Period { get; set; } = "";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public decimal NetProfit { get; set; }
    public decimal ProfitFactor { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int Trades { get; set; }
    public decimal WinRate { get; set; }
    public double Sharpe { get; set; }
    public double? Efficiency { get; set; }
    public int? Combinations { get; set; }
    public string Notes { get; set; } = "";
    public bool Starred { get; set; }
    public string CodeHash { get; set; } = "";
    public string ParametersText { get; set; } = "";
}

/// <summary>
/// Stores the run history per strategy on disk ({root}/{strategy}/...), with a copy
/// of the exact code that produced each result.
/// </summary>
public static class StrategyStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static readonly object Lock = new();

    public static string Root { get; set; } = Path.Combine(TradingHub.Common.Product.AppData, "Backtests");

    private static string Folder(string strategyId)
    {
        var safe = string.Concat(strategyId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Root, safe);
    }

    public static string HashCode(string source)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source.Replace("\r\n", "\n").Trim()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    /// <summary>Stores a run (and the code if it is passed).</summary>
    public static void Save(StrategyRun run, string? source = null)
    {
        lock (Lock)
        {
            var dir = Folder(run.StrategyId);
            Directory.CreateDirectory(dir);
            if (source != null)
            {
                run.CodeHash = HashCode(source);
                var codeDir = Path.Combine(dir, "codigo");
                Directory.CreateDirectory(codeDir);
                var codePath = Path.Combine(codeDir, run.CodeHash + ".cs");
                if (!File.Exists(codePath)) File.WriteAllText(codePath, source);
            }

            if (run.Equity != null) run.Equity = BacktestResult.Downsample(run.Equity, 3000);
            if (run.Optimization != null && run.Optimization.Rows.Count > 1000) run.Optimization.Rows = run.Optimization.Rows.Take(1000).ToList();
            if (run.WalkForward != null) run.WalkForward.OutSampleEquity = BacktestResult.Downsample(run.WalkForward.OutSampleEquity, 3000);
            File.WriteAllText(Path.Combine(dir, run.Id + ".json"), JsonSerializer.Serialize(run, Json));
            var index = LoadIndex(run.StrategyId);
            index.RemoveAll(s => s.Id == run.Id);
            index.Insert(0, run.Summary());
            SaveIndex(run.StrategyId, index);
        }
    }

    public static List<StrategyRunSummary> List(string strategyId)
    {
        lock (Lock)
        {
            var dir = Folder(strategyId);
            var index = LoadIndex(strategyId);
            int before = index.Count;
            index.RemoveAll(s => !File.Exists(Path.Combine(dir, s.Id + ".json")));
            if (index.Count != before) SaveIndex(strategyId, index);
            return index.OrderByDescending(s => s.CreatedUtc).ToList();
        }
    }

    public static StrategyRun? Load(string strategyId, string runId)
    {
        var path = Path.Combine(Folder(strategyId), runId + ".json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<StrategyRun>(File.ReadAllText(path), Json);
        }
        catch
        {
            return null;
        }
    }

    public static string? LoadCode(string strategyId, string hash)
    {
        var path = Path.Combine(Folder(strategyId), "codigo", hash + ".cs");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public static void Delete(string strategyId, string runId)
    {
        lock (Lock)
        {
            var path = Path.Combine(Folder(strategyId), runId + ".json");
            if (File.Exists(path)) File.Delete(path);
            var index = LoadIndex(strategyId);
            index.RemoveAll(s => s.Id == runId);
            SaveIndex(strategyId, index);
        }
    }

    /// <summary>Updates a run's notes or favorite flag.</summary>
    public static void Annotate(string strategyId, string runId, string? notes, bool? starred)
    {
        lock (Lock)
        {
            var run = Load(strategyId, runId);
            if (run == null) return;
            if (notes != null) run.Notes = notes;
            if (starred != null) run.Starred = starred.Value;
            File.WriteAllText(Path.Combine(Folder(strategyId), runId + ".json"), JsonSerializer.Serialize(run, Json));
            var index = LoadIndex(strategyId);
            int i = index.FindIndex(s => s.Id == runId);
            if (i >= 0) index[i] = run.Summary();
            SaveIndex(strategyId, index);
        }
    }

    private static List<StrategyRunSummary> LoadIndex(string strategyId)
    {
        var path = Path.Combine(Folder(strategyId), "index.json");
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<List<StrategyRunSummary>>(File.ReadAllText(path), Json) ?? new();
        }
        catch
        {
            // corrupt index: it is rebuilt
        }

        var list = new List<StrategyRunSummary>();
        var dir = Folder(strategyId);
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            // runs only (they start with the date); index.json, ui.json and the rest are ignored
            if (!char.IsDigit(Path.GetFileName(f)[0])) continue;
            try
            {
                var run = JsonSerializer.Deserialize<StrategyRun>(File.ReadAllText(f), Json);
                if (run != null) list.Add(run.Summary());
            }
            catch
            {
                // invalid file
            }
        }

        return list;
    }

    private static void SaveIndex(string strategyId, List<StrategyRunSummary> index)
    {
        var dir = Folder(strategyId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.json"), JsonSerializer.Serialize(index, Json));
    }
}
