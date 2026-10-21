using System.Text.Json;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Market;
using TradingHub.Strategies;

namespace TradingHub.App.Services;

/// <summary>One hour of a market's context (for the recent-hours strip of the panel).</summary>
internal readonly record struct MarketHour(DateTime Hour, MarketRegime Regime, int? Direction, double? VolatilityRank);

/// <summary>How a market is right now: range or trend (up or down), volatility and the recent hours.</summary>
internal sealed class MarketCondition
{
    public required string FeedId { get; init; }
    public required string Symbol { get; init; }
    public string Market => $"{FeedId}:{Symbol}";

    /// <summary>How far the data reaches (if the market is closed or is not recorded, it can be from earlier).</summary>
    public DateTime DataUntilUtc { get; init; }

    public decimal LastPrice { get; init; }
    public double? Trend { get; init; }
    public int? Direction { get; init; }
    public double? Change { get; init; }
    public double? Volatility { get; init; }
    public double? VolatilityRank { get; init; }
    public MarketRegime Regime { get; init; }

    /// <summary>Days of hourly history it was computed with.</summary>
    public int Days { get; init; }

    /// <summary>Consecutive hours in the current regime (within the last 48).</summary>
    public int RegimeHours { get; init; }

    public List<MarketHour> Recent { get; init; } = new();
    public string RegimeLabel => MarketContextTracker.Label(Regime, Direction);
    public string VolatilityLabel => MarketContextTracker.VolatilityLabel(VolatilityRank);

    /// <summary>The current context, to ask a filter whether it allows entering.</summary>
    public MarketContextTracker? Tracker { get; init; }
}

/// <summary>
/// Market condition using the same measure the strategies and their context filters use (trend of the last
/// 24 hours and last month's volatility percentile), built from the hourly closes of the last 32 days: from the
/// stored history (cached per day), from the exchange's candles for the missing days (if the market is already
/// open) and from today's live data. It is used by Live's "Market" panel and by the live strategies on startup.
/// </summary>
internal sealed class MarketConditions
{
    public const int HistoryDays = 32;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly AppServices _svc;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MarketConditions(AppServices svc) => _svc = svc;

    private sealed class DayCloses
    {
        /// <summary>Length of the day's file when it was computed (-1 = it came from the exchange's candles).</summary>
        public long Length { get; set; }

        public decimal?[] Closes { get; set; } = new decimal?[24];
    }

    private static string CacheDir => Path.Combine(AppPaths.Root, "Contexto");

    private static string CachePath(string feed, string symbol)
    {
        static string Clean(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
        return Path.Combine(CacheDir, $"{Clean(feed)}_{Clean(symbol)}.json");
    }

    /// <summary>Hourly closes (UTC, start of each hour) of the last 32 days up to now, in order.</summary>
    public async Task<List<(DateTime Time, decimal Close)>> HourlyAsync(string feed, string symbol, CancellationToken ct = default)
    {
        SortedDictionary<DateTime, decimal> hours;
        await _gate.WaitAsync(ct);
        try
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            hours = await StoredAsync(feed, symbol, today.AddDays(-(HistoryDays - 1)), today, ct);
        }
        finally
        {
            _gate.Release();
        }

        // today, with whatever the open market brings (on the UI thread, where trades are added to it)
        var live = _svc.Hub.Items.FirstOrDefault(x => Same(x.Instrument, feed, symbol));
        if (live is { Trades.Count: > 0 })
        {
            var store = live.Trades;
            long since = DateTime.UtcNow.AddHours(-30).Ticks;
            var seen = new HashSet<long>();
            for (int i = store.Count - 1; i >= 0; i--)
            {
                long t = store.GetTimeTicks(i);
                if (t < since) break;
                long hour = t - t % TimeSpan.TicksPerHour;
                if (seen.Add(hour)) hours[new DateTime(hour, DateTimeKind.Utc)] = store.GetPrice(i);
            }
        }

        return hours.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>How the market is right now (null if there is no data at all).</summary>
    public async Task<MarketCondition?> ConditionAsync(string feed, string symbol, CancellationToken ct = default)
    {
        var hourly = await HourlyAsync(feed, symbol, ct);
        if (hourly.Count == 0) return null;
        var tracker = new MarketContextTracker();
        var recent = new List<MarketHour>();
        var cut = hourly[^1].Time.AddHours(-47);
        foreach (var (t, c) in hourly)
        {
            tracker.Add(t, c);
            if (t >= cut) recent.Add(new MarketHour(t, tracker.Regime, tracker.Direction, tracker.VolatilityRank));
        }

        int same = 0;
        for (int i = recent.Count - 1; i >= 0 && recent[i].Regime == tracker.Regime && (tracker.Regime != MarketRegime.Trend || recent[i].Direction == tracker.Direction); i--) same++;
        var live = _svc.Hub.Items.FirstOrDefault(x => Same(x.Instrument, feed, symbol));
        return new MarketCondition
        {
            FeedId = feed,
            Symbol = symbol,
            DataUntilUtc = live is { Trades.Count: > 0 } ? live.Trades.LastTime : hourly[^1].Time,
            LastPrice = live is { Trades.Count: > 0 } ? live.LastPrice : hourly[^1].Close,
            Trend = tracker.Trend,
            Direction = tracker.Direction,
            Change = tracker.Change,
            Volatility = tracker.Volatility,
            VolatilityRank = tracker.VolatilityRank,
            Regime = tracker.Regime,
            Days = (int)Math.Round((hourly[^1].Time - hourly[0].Time).TotalDays),
            RegimeHours = same,
            Recent = recent,
            Tracker = tracker,
        };
    }

    /// <summary>For a strategy that started live, it builds the context with the full month (in the background).</summary>
    public async Task SeedAsync(LiveStrategyRunner runner)
    {
        try
        {
            var inst = runner.Data.Instrument;
            var hourly = await HourlyAsync(inst.FeedId, inst.Symbol);
            if (runner.IsRunning && hourly.Count > 0) runner.SeedContext(hourly);
        }
        catch (Exception ex)
        {
            this.LogWarn($"Could not build the market context for {runner.StrategyName}: {ex.Message}");
        }
    }

    private static bool Same(Instrument i, string feed, string symbol) =>
        i.FeedId.Equals(feed, StringComparison.OrdinalIgnoreCase) && i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase);

    /// <summary>What is stored: each day's files (cached) and, for the days with no file, exchange candles.</summary>
    /// <summary>
    /// Hourly closes of the 32 days before an instant, from the stored history (with the same per-day cache): so
    /// a backtest, an optimization or a walk-forward start their market context as it is live. Only hours already
    /// closed before <paramref name="untilUtc"/>.
    /// </summary>
    public static List<(DateTime Time, decimal Close)> HourlyBefore(string feed, string symbol, DateTime untilUtc)
    {
        try
        {
            var last = DateOnly.FromDateTime(untilUtc);
            var (hours, _, cache, changed) = ReadStored(feed, symbol, last.AddDays(-HistoryDays), last, CancellationToken.None);
            if (changed) Save(CachePath(feed, symbol), Prune(cache));
            return hours.Where(kv => kv.Key.AddHours(1) <= untilUtc).Select(kv => (kv.Key, kv.Value)).ToList();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
            return new List<(DateTime, decimal)>();
        }
    }

    /// <summary>The days of the last year or so are stored (enough for the usual backtests).</summary>
    private static Dictionary<DateOnly, DayCloses> Prune(Dictionary<DateOnly, DayCloses> cache)
    {
        var oldest = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-400);
        foreach (var d in cache.Keys.Where(d => d < oldest).ToList()) cache.Remove(d);
        return cache;
    }

    /// <summary>The days stored between two dates: from the cache or from each day's files (no network).</summary>
    private static (SortedDictionary<DateTime, decimal> Hours, List<DateOnly> Missing, Dictionary<DateOnly, DayCloses> Cache, bool Changed) ReadStored(
        string feed, string symbol, DateOnly first, DateOnly last, CancellationToken ct)
    {
        var stored = Load(CachePath(feed, symbol));
        var found = new SortedDictionary<DateTime, decimal>();
        var absent = new List<DateOnly>();
        bool dirty = false;
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            ct.ThrowIfCancellationRequested();
            // from each of the day's sources (Rithmic and Databento's combined one reads both): the first one wins
            var files = HistorySources.DayFiles(AppPaths.Ticks, feed, symbol, d);
            long len = files.Count == 0 ? -1 : files.Sum(f => new FileInfo(f).Length);
            // a day no longer changes half an hour after it ends (before that, it may still be recording)
            bool final = DateTime.UtcNow - d.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) > TimeSpan.FromMinutes(30);
            decimal?[]? closes = null;
            if (stored.TryGetValue(d, out var dc) && dc.Length == len) closes = dc.Closes;
            else if (len > 0)
            {
                try
                {
                    closes = FromTicks(files);
                }
                catch (IOException)
                {
                    closes = null;
                }

                if (closes != null && final)
                {
                    stored[d] = new DayCloses { Length = len, Closes = closes };
                    dirty = true;
                }
            }

            if (closes == null)
            {
                absent.Add(d);
                continue;
            }

            Add(found, d, closes);
        }

        return (found, absent, stored, dirty);
    }

    private async Task<SortedDictionary<DateTime, decimal>> StoredAsync(string feed, string symbol, DateOnly first, DateOnly today, CancellationToken ct)
    {
        var path = CachePath(feed, symbol);
        var (hours, missing, cache, changed) = await Task.Run(() => ReadStored(feed, symbol, first, today, ct), ct);

        // days with no file: 15-minute candles from the exchange, if that market is already open in the app or it is crypto (public
        // candles); in consecutive stretches, and a day already closed stays in the cache even if it had no candles (a futures Saturday)
        if (missing.Count > 0 && await KlineSourceAsync(feed, symbol, ct) is var (klines, instrument))
        {
            foreach (var run in Runs(missing))
            {
                var from = run[0].ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                var to = run[^1].AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                if (to > DateTime.UtcNow) to = DateTime.UtcNow;
                try
                {
                    var list = await KlineHistory.LoadAsync(klines, instrument, from, to, 15, 0, null, ct);
                    var byDay = new Dictionary<DateOnly, decimal?[]>();
                    foreach (var k in list)
                    {
                        var t = DateTimeOffset.FromUnixTimeMilliseconds(k.OpenTimeMs).UtcDateTime;
                        var d = DateOnly.FromDateTime(t);
                        if (!byDay.TryGetValue(d, out var arr)) byDay[d] = arr = new decimal?[24];
                        arr[t.Hour] = k.Close;
                    }

                    foreach (var d in run)
                    {
                        byDay.TryGetValue(d, out var arr);
                        if (arr != null) Add(hours, d, arr);
                        if (DateTime.UtcNow - d.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) <= TimeSpan.FromMinutes(30)) continue;
                        cache[d] = new DayCloses { Length = -1, Closes = arr ?? new decimal?[24] };
                        changed = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.LogInfo($"No exchange candles for the context of {symbol}: {ex.Message}");
                }
            }
        }

        if (changed) Save(path, Prune(cache));

        return hours;
    }

    private readonly Dictionary<string, Instrument> _instruments = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where to get candles from: from the market open in the app or, for crypto, from that exchange's configured connection (the
    /// candles are public: it opens no session). null if there is none.
    /// </summary>
    private async Task<(IKlineHistory Source, Instrument Instrument)?> KlineSourceAsync(string feed, string symbol, CancellationToken ct)
    {
        if (_svc.Hub.Items.FirstOrDefault(x => Same(x.Instrument, feed, symbol)) is { Feed: IKlineHistory open } data) return (open, data.Instrument);
        if (!PublicArchives.Supports(feed)) return null;
        var profile = _svc.Connections.Enabled.FirstOrDefault(p => p.HasMarketData && p.MarketDataId == feed);
        if (profile == null) return null;
        try
        {
            if (_svc.Connections.GetFeed(profile.Id) is not (IKlineHistory source and IMarketDataFeed market)) return null;
            string key = $"{feed}:{symbol}";
            if (!_instruments.TryGetValue(key, out var inst))
            {
                inst = (await market.GetInstrumentsAsync(ct)).FirstOrDefault(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
                if (inst == null) return null;
                _instruments[key] = inst;
            }

            return (source, inst);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.LogInfo($"No public candles for the context of {symbol}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Consecutive days, in stretches.</summary>
    private static IEnumerable<List<DateOnly>> Runs(IEnumerable<DateOnly> days)
    {
        var run = new List<DateOnly>();
        foreach (var d in days.OrderBy(x => x))
        {
            if (run.Count > 0 && d != run[^1].AddDays(1))
            {
                yield return run;
                run = new List<DateOnly>();
            }

            run.Add(d);
        }

        if (run.Count > 0) yield return run;
    }

    private static void Add(SortedDictionary<DateTime, decimal> hours, DateOnly day, decimal?[] closes)
    {
        for (int h = 0; h < 24; h++)
            if (closes[h] is { } c)
                hours[day.ToDateTime(new TimeOnly(h, 0), DateTimeKind.Utc)] = c;
    }

    /// <summary>The last price of each hour of a stored day (the hours the first file does not have, from the following ones).</summary>
    private static decimal?[] FromTicks(IReadOnlyList<string> files)
    {
        var closes = new decimal?[24];
        foreach (var file in files)
        {
            var own = new decimal?[24];
            var last = new long[24];
            foreach (var t in TickFile.Read(file))
            {
                int h = t.Time.Hour;
                if (t.Time.Ticks < last[h]) continue;
                last[h] = t.Time.Ticks;
                own[h] = t.Price;
            }

            for (int h = 0; h < 24; h++) closes[h] ??= own[h];
        }

        return closes;
    }

    private static Dictionary<DateOnly, DayCloses> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var raw = JsonSerializer.Deserialize<Dictionary<string, DayCloses>>(File.ReadAllText(path), Json) ?? new();
            return raw.Where(kv => DateOnly.TryParse(kv.Key, System.Globalization.CultureInfo.InvariantCulture, out _) && kv.Value.Closes.Length == 24)
                .ToDictionary(kv => DateOnly.Parse(kv.Key, System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
        }
        catch
        {
            return new(); // corrupt cache: it is recomputed
        }
    }

    private static void Save(string path, Dictionary<DateOnly, DayCloses> cache)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var raw = cache.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(raw, Json));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }
}
