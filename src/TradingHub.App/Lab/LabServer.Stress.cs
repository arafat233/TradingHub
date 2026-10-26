using System.Globalization;
using System.Text.Json.Nodes;
using TradingHub.App.Services;
using TradingHub.Strategies;

namespace TradingHub.App.Lab;

/// <summary>
/// The overfit adversary: take a strategy with its parameters and try to break it. Same ticks, but costs doubled and tripled, each half of the
/// history alone, the first and last quarter dropped, and every optimizable parameter nudged one step up and down. An edge that is real survives
/// most of this; one that was fitted to the noise dies on the first few. It only runs backtests: nothing is stored in the run history.
/// </summary>
internal static partial class LabServer
{
    private sealed record Scenario(string Name, string Group, BacktestSettings Settings, TickData Data, Dictionary<string, object?> Parameters);

    private static async Task<JsonNode> StressTestAsync(JsonObject a)
    {
        var file = FileOf(Str(a, "file"));
        var (baseSettings, data) = await PrepareAsync(a);
        var baseParams = ParamsFor(a, file);
        var scenarios = new List<Scenario>();

        // the same run first: its parameters (defaults filled in) are what the nudges start from
        var first = await RunOne(file, baseSettings, data, baseParams);
        if (!first.Ok) throw new InvalidOperationException("The strategy failed: " + first.Error);
        var values = first.Parameters;

        Scenario Cost(string name, decimal factor, int slipFactor)
        {
            var s = baseSettings.Clone();
            s.SlippageTicks = baseSettings.SlippageTicks * slipFactor + (baseSettings.SlippageTicks == 0 ? slipFactor - 1 : 0);
            s.CommissionPercent = baseSettings.CommissionPercent * factor;
            s.CommissionPerUnit = baseSettings.CommissionPerUnit * factor;
            return new Scenario(name, "costs", s, data, baseParams);
        }

        scenarios.Add(Cost("costs ×2", 2, 2));
        scenarios.Add(Cost("costs ×3", 3, 3));

        var (from, to) = (data.FirstTime, data.LastTime.AddSeconds(1));
        var span = to - from;
        Scenario Part(string name, double a0, double b0) =>
            new(name, "period", baseSettings, data.Slice(from + span * a0, from + span * b0), baseParams);
        scenarios.Add(Part("first half only", 0, 0.5));
        scenarios.Add(Part("second half only", 0.5, 1));
        scenarios.Add(Part("without the first quarter", 0.25, 1));
        scenarios.Add(Part("without the last quarter", 0, 0.75));

        // nudge each optimizable parameter one step each way (at most 3 parameters, to keep the run time sane)
        var pinned = PinnedFor(file);
        var ranges = ParameterRange.FromStrategy(file.Type!, file.Create()).Where(r => !r.IsPeriod && !pinned.ContainsKey(r.Name) && values.ContainsKey(r.Name)).Take(3).ToList();
        foreach (var r in ranges)
        {
            double v = Convert.ToDouble(values[r.Name], CultureInfo.InvariantCulture);
            foreach (var dir in new[] { -1, 1 })
            {
                double nv = Math.Clamp(v + dir * r.Step, r.Min, r.Max);
                if (nv == v) continue;
                var p = new Dictionary<string, object?>(baseParams) { [r.Name] = r.IsInteger ? (object)(long)Math.Round(nv) : nv };
                scenarios.Add(new Scenario($"{r.Label.Trim()} {(r.IsInteger ? ((long)Math.Round(nv)).ToString(CultureInfo.InvariantCulture) : nv.ToString("0.###", CultureInfo.InvariantCulture))}", "parameters", baseSettings, data, p));
            }
        }

        var results = new List<(Scenario S, PerformanceMetrics? M, string? Error)>();
        foreach (var sc in scenarios)
        {
            var r = await RunOne(file, sc.Settings, sc.Data, sc.Parameters);
            results.Add((sc, r.Ok ? r.Metrics : null, r.Ok ? null : r.Error));
        }

        var baseM = first.Metrics;
        var arr = new JsonArray();
        foreach (var (s, m, err) in results)
            arr.Add(new JsonObject
            {
                ["scenario"] = s.Name,
                ["group"] = s.Group,
                ["net_profit"] = m == null ? null : Math.Round(m.NetProfit, 2),
                ["profit_factor"] = m == null ? null : Math.Round(m.ProfitFactor, 2),
                ["trades"] = m?.TotalTrades,
                ["error"] = err,
            });

        var ok = results.Where(r => r.M != null).ToList();
        bool GroupAllPositive(string g) => ok.Where(r => r.S.Group == g).All(r => r.M!.NetProfit > 0);
        double positiveShare(string g) { var l = ok.Where(r => r.S.Group == g).ToList(); return l.Count == 0 ? 1 : (double)l.Count(r => r.M!.NetProfit > 0) / l.Count; }
        bool costsHold = ok.Where(r => r.S.Name == "costs ×2").All(r => r.M!.NetProfit > 0);
        bool halvesHold = ok.Where(r => r.S.Name.EndsWith("half only", StringComparison.Ordinal)).All(r => r.M!.NetProfit > 0);
        double nudged = positiveShare("parameters");
        string verdict = baseM.NetProfit <= 0 ? "NO EDGE: the baseline already loses money"
            : baseM.TotalTrades < 30 ? "TOO FEW TRADES to say anything (under 30)"
            : costsHold && halvesHold && nudged >= 0.75 && GroupAllPositive("period") ? "ROBUST: it survives doubled costs, each half of the history and nudged parameters"
            : !costsHold || !halvesHold || nudged < 0.5 ? "FRAGILE: " + string.Join("; ", new[] { !costsHold ? "dies when costs double" : null, !halvesHold ? "loses in one half of the history" : null, nudged < 0.5 ? "most nudged parameters lose" : null }.Where(x => x != null))
            : "MIXED: it holds on the main tests but not on all of them; look at the rows";

        return new JsonObject
        {
            ["verdict"] = verdict,
            ["baseline"] = new JsonObject { ["net_profit"] = Math.Round(baseM.NetProfit, 2), ["profit_factor"] = Math.Round(baseM.ProfitFactor, 2), ["trades"] = baseM.TotalTrades, ["parameters"] = ParamsJson(values) },
            ["scenarios"] = arr,
            ["note"] = "Each row re-runs the strategy on the same stored ticks. Surviving these tests is necessary, not sufficient: it does not rule out that the idea only worked in this market regime.",
        };
    }

    private static Task<BacktestResult> RunOne(StrategyFile file, BacktestSettings settings, TickData data, Dictionary<string, object?> parameters)
    {
        var strategy = file.Create();
        strategy.SetParameters(parameters);
        return Task.Run(() => new BacktestEngine(strategy, data, settings) { ContextHistory = TradingHub.App.Services.MarketConditions.HourlyBefore(settings.FeedId, settings.Symbol, settings.FromUtc) }.Run(CancellationToken.None));
    }
}
