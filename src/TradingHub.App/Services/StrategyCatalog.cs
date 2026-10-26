using TradingHub.Scripting;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>A strategy on disk (a .cs file) and its last compilation.</summary>
public sealed class StrategyFile
{
    public required string Path { get; init; }
    public string FileName => System.IO.Path.GetFileNameWithoutExtension(Path);
    public Type? Type { get; set; }
    public ScriptCompileResult? LastCompile { get; set; }
    public DateTime CompiledFileTime { get; set; }
    public string DisplayName => Type != null ? Strategy.DisplayName(Type) : FileName;

    /// <summary>Stable id for the history (the class name).</summary>
    public string Id => Type?.FullName ?? FileName;

    public Strategy Create() => (Strategy)Activator.CreateInstance(Type ?? throw new InvalidOperationException("The strategy is not compiled."))!;
}

/// <summary>The user's strategies: files in ~/Documents/Trading Hub/Estrategias, compiled with Roslyn.</summary>
public sealed class StrategyCatalog
{
    private readonly ScriptCompiler _compiler = new();

    public StrategyCatalog()
    {
        _ = typeof(Strategy); // makes sure TradingHub.Strategies is loaded so it can be referenced when compiling
        // TRADINGHUB_BACKTESTS_DIR: alternative history folder (tests)
        StrategyStore.Root = Environment.GetEnvironmentVariable("TRADINGHUB_BACKTESTS_DIR") is { Length: > 0 } dir ? dir : AppPaths.Backtests;
        Directory.CreateDirectory(AppPaths.Strategies);
        if (!Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").Any())
        {
            foreach (var t in StrategyTemplates.All.Where(t => t.Example))
                File.WriteAllText(System.IO.Path.Combine(AppPaths.Strategies, t.FileName + ".cs"), t.Code(t.FileName).Replace("__NAME__", t.Name));
        }

        Refresh();
    }

    public List<StrategyFile> Files { get; } = new();

    public event Action? Changed;

    public void Refresh()
    {
        var existing = Files.ToDictionary(f => f.Path);
        Files.Clear();
        var extra = Enumerable.Empty<string>();
        foreach (var path in Directory.EnumerateFiles(AppPaths.Strategies, "*.cs").Concat(extra).OrderBy(p => p))
            Files.Add(existing.TryGetValue(path, out var f) ? f : new StrategyFile { Path = path });
        Changed?.Invoke();
    }

    /// <summary>Compiles a file (if it changed since last time or if it is forced).</summary>
    public ScriptCompileResult Compile(StrategyFile file, bool force = false)
    {
        var stamp = File.GetLastWriteTimeUtc(file.Path);
        if (!force && file.LastCompile != null && stamp == file.CompiledFileTime) return file.LastCompile;
        var res = _compiler.Compile(new[] { file.Path }, "Strategy_" + file.FileName);
        file.LastCompile = res;
        file.CompiledFileTime = stamp;
        if (res.Success && res.Assembly != null)
        {
            var types = res.Assembly.GetTypes().Where(t => !t.IsAbstract && typeof(Strategy).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null).ToList();
            file.Type = types.FirstOrDefault();
            if (file.Type == null)
            {
                var diag = new ScriptDiagnostic(file.Path, 1, 1, "error", "TH01", "The file has no public class inheriting from Strategy with a parameterless constructor.");
                file.LastCompile = new ScriptCompileResult { Success = false, Diagnostics = res.Diagnostics.Append(diag).ToList(), Elapsed = res.Elapsed };
            }
        }
        else
        {
            file.Type = null;
        }

        Changed?.Invoke();
        return file.LastCompile;
    }

    /// <summary>Creates a new file from a template and returns its path.</summary>
    public StrategyFile Create(string name, StrategyTemplate template)
    {
        string className = new string(name.Where(char.IsLetterOrDigit).ToArray());
        if (className.Length == 0 || char.IsDigit(className[0])) className = "Strategy" + className;
        var path = System.IO.Path.Combine(AppPaths.Strategies, className + ".cs");
        for (int i = 2; File.Exists(path); i++) path = System.IO.Path.Combine(AppPaths.Strategies, $"{className}{i}.cs");
        className = System.IO.Path.GetFileNameWithoutExtension(path);
        File.WriteAllText(path, template.Code(className).Replace("__NAME__", name.Replace("\"", "'")));
        Refresh();
        return Files.First(f => f.Path == path);
    }

    public void Delete(StrategyFile file)
    {
        if (File.Exists(file.Path)) File.Delete(file.Path);
        Refresh();
    }
}

/// <summary>Strategy template.</summary>
public sealed record StrategyTemplate(string Name, string Description, string FileName, bool Example, Func<string, string> Code);

/// <summary>Bundled templates (they double as examples the first time).</summary>
public static class StrategyTemplates
{
    public static readonly StrategyTemplate[] All =
    {
        new("Moving average cross", "Long when the fast average crosses above the slow one and short the other way round. Ideal to get started.", "MovingAverageCross", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NAME__", Description = "A cross of two exponential averages with a stop and a target in ticks.")]
            public class {{c}} : Strategy
            {
                [Parameter("Fast average"), Optimize(5, 30, 5)]
                public int Fast { get; set; } = 10;

                [Parameter("Slow average"), Optimize(20, 100, 10)]
                public int Slow { get; set; } = 40;

                private Ema _fast = null!, _slow = null!;

                protected override void OnInit()
                {
                    _fast = Ema(Fast);
                    _slow = Ema(Slow);
                }

                protected override void OnBar(Bar bar)
                {
                    if (!_slow.IsReady) return;

                    if (CrossAbove(_fast, _slow)) EnterLong();
                    else if (CrossBelow(_fast, _slow)) EnterShort();
                }
            }
            """),

        new("Range breakout", "It buys the breakout of the N-candle high and sells the break of the low, with an ATR stop.", "RangeBreakout", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NAME__", Description = "A channel (Donchian) breakout with an ATR exit.")]
            public class {{c}} : Strategy
            {
                [Parameter("Channel candles"), Optimize(10, 60, 10)]
                public int Channel { get; set; } = 20;

                [Parameter("ATR multiple for the stop"), Optimize(1, 4, 0.5)]
                public double AtrMultiple { get; set; } = 2;

                private Highest _highest = null!;
                private Lowest _lowest = null!;
                private Atr _atr = null!;
                private decimal _stop;

                protected override void OnInit()
                {
                    _highest = Highest(Channel);
                    _lowest = Lowest(Channel);
                    _atr = Atr(14);
                }

                protected override void OnBar(Bar bar)
                {
                    if (Bars.Count <= Channel || !_atr.IsReady) return;
                    decimal ceiling = _highest[1], floor = _lowest[1];
                    decimal distance = _atr.Value * (decimal)AtrMultiple;

                    if (IsFlat)
                    {
                        if (bar.Close > ceiling) { EnterLong(); _stop = bar.Close - distance; }
                        else if (bar.Close < floor) { EnterShort(); _stop = bar.Close + distance; }
                    }
                    else if (IsLong)
                    {
                        _stop = Math.Max(_stop, bar.Close - distance);
                        if (bar.Close < _stop) ClosePosition("Stop ATR");
                    }
                    else
                    {
                        _stop = Math.Min(_stop, bar.Close + distance);
                        if (bar.Close > _stop) ClosePosition("Stop ATR");
                    }
                }
            }
            """),

        new("Delta absorption", "Order flow: a candle with a very negative delta that still closes up (sell absorption) → buy, and the other way round.", "DeltaAbsorption", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NAME__", Description = "It looks for absorption: a lot of aggressive volume against price that fails to move it.")]
            public class {{c}} : Strategy
            {
                [Parameter("Candles for the volume average")]
                public int AveragePeriod { get; set; } = 20;

                [Parameter("Minimum volume (x average)"), Optimize(1.0, 3.0, 0.5)]
                public double MinVolume { get; set; } = 1.5;

                [Parameter("Minimum delta (% of volume)"), Optimize(10, 40, 10)]
                public int MinDelta { get; set; } = 20;

                [Parameter("Maximum candles in the trade")]
                public int MaxCandles { get; set; } = 6;

                private Sma _volume = null!;
                private int _entryBar;

                public {{c}}()
                {
                    StopLossTicks = 40;
                    TakeProfitTicks = 80;
                }

                protected override void OnInit()
                {
                    UseFootprint = true; // volume by price on each candle (bar.Levels, bar.PocPrice)
                    _volume = Sma(AveragePeriod, PriceSource.Volume);
                }

                protected override void OnBar(Bar bar)
                {
                    if (!_volume.IsReady) return;

                    if (!IsFlat && CurrentBar - _entryBar >= MaxCandles)
                    {
                        ClosePosition("Time");
                        return;
                    }

                    bool highVolume = bar.Volume >= _volume[1] * (decimal)MinVolume;
                    decimal deltaPct = bar.Volume > 0 ? 100 * bar.Delta / bar.Volume : 0;

                    // strong aggressive selling but the candle closes above its POC: somebody absorbed it
                    if (IsFlat && highVolume && deltaPct <= -MinDelta && bar.Close > bar.PocPrice && bar.IsBullish)
                    {
                        EnterLong();
                        _entryBar = CurrentBar;
                    }
                    else if (IsFlat && highVolume && deltaPct >= MinDelta && bar.Close < bar.PocPrice && bar.IsBearish)
                    {
                        EnterShort();
                        _entryBar = CurrentBar;
                    }
                }
            }
            """),

        new("Big-trade level", "Order flow: price's first return to the level of a big market order. It bounces off it (fade) or goes through it (follow).", "BigTradeLevel", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NAME__", Description = "It trades the first time price comes back to the level of a big market order.")]
            public class {{c}} : Strategy
            {
                [Parameter("Minimum order size (contracts)"), Optimize(20, 200, 20)]
                public int MinSize { get; set; } = 50;

                [Parameter("Fade the level (off = follow the break)")]
                public bool Fade { get; set; } = true;

                private readonly HashSet<DateTime> _used = new();

                public {{c}}()
                {
                    StopLossTicks = 40;
                    TakeProfitTicks = 80;
                }

                // BigTradeLevels fills with every market order of at least this size and marks a level Touched when price returns to it
                protected override void OnInit() => TrackBigTrades = MinSize;

                protected override void OnBar(Bar bar)
                {
                    if (!IsFlat) return;
                    foreach (var level in BigTradeLevels)
                    {
                        if (!level.Touched || !_used.Add(level.Time)) continue;

                        // fading: a level where buyers were aggressive is expected to hold price up (long), a sell level to cap it (short)
                        bool goLong = Fade ? level.Buy : !level.Buy;
                        if (goLong) EnterLong();
                        else EnterShort();
                        return;
                    }
                }
            }
            """),

        new("VWAP reversion", "It trades the return to the session VWAP when price strays too far, inside a time window only.", "VwapReversion", true, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NAME__", Description = "It sells extensions above the VWAP and buys below it; it exits at the VWAP.")]
            public class {{c}} : Strategy
            {
                [Parameter("Distance (x ATR)"), Optimize(1.0, 3.0, 0.5)]
                public double Distance { get; set; } = 2;

                [Parameter("From (local hour)")]
                public int FromHour { get; set; } = 10;

                [Parameter("To (local hour)")]
                public int ToHour { get; set; } = 20;

                private Vwap _vwap = null!;
                private Atr _atr = null!;

                public {{c}}()
                {
                    StopLossTicks = 60;
                }

                protected override void OnInit()
                {
                    _vwap = Vwap();
                    _atr = Atr(20);
                }

                protected override void OnBar(Bar bar)
                {
                    if (!_atr.IsReady) return;
                    bool inHours = TimeBetween(TimeSpan.FromHours(FromHour), TimeSpan.FromHours(ToHour));
                    decimal band = _atr.Value * (decimal)Distance;

                    if (IsLong && bar.Close >= _vwap.Value) ClosePosition("VWAP");
                    else if (IsShort && bar.Close <= _vwap.Value) ClosePosition("VWAP");
                    else if (IsFlat && inHours)
                    {
                        if (bar.Close < _vwap.Value - band) EnterLong();
                        else if (bar.Close > _vwap.Value + band) EnterShort();
                    }

                    // outside session hours it does not stay positioned
                    if (!inHours && !IsFlat) ClosePosition("End of session hours");
                }
            }
            """),

        new("Empty", "A minimal skeleton with the available methods.", "MyStrategy", false, c => $$"""
            using TradingHub.Strategies;

            [Strategy("__NAME__", Description = "")]
            public class {{c}} : Strategy
            {
                // [Parameter("Name"), Optimize(min, max, step)] so it shows up in the optimization
                [Parameter("Period"), Optimize(10, 50, 10)]
                public int Period { get; set; } = 20;

                private Sma _average = null!;

                protected override void OnInit()
                {
                    // indicators: Sma, Ema, Rsi, Atr, Vwap, Bollinger, Macd, Highest, Lowest, CumulativeDelta…
                    _average = Sma(Period);
                }

                protected override void OnBar(Bar bar)
                {
                    // bar = the candle that just closed (Open, High, Low, Close, Volume, BuyVolume, SellVolume, Delta…)
                    // orders: EnterLong(), EnterShort(), Buy(), Sell(), BuyLimit(price), SellStop(price), ClosePosition()
                    // automatic stops: StopLossTicks, TakeProfitTicks, TrailingStopTicks
                }
            }
            """),
    };
}
