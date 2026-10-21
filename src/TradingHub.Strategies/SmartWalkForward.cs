using System.Collections.Concurrent;
using System.Globalization;
using TradingHub.Data;

namespace TradingHub.Strategies;

/// <summary>How the parameters are searched for in each walk-forward window.</summary>
public enum SearchMode
{
    /// <summary>The full grid if it is small (the whole map, to see the plateau); Bayesian if it is large.</summary>
    Auto,

    /// <summary>Every combination.</summary>
    Grid,

    /// <summary>Bayesian optimization (TPE): it tries first where something good is most likely to be.</summary>
    Bayesian,
}

/// <summary>An out-of-sample path of the purged combinatorial cross-validation (CPCV).</summary>
public sealed class CpcvPath
{
    public int Number { get; set; }
    public decimal Net { get; set; }
    public double Sharpe { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int Days { get; set; }
}

/// <summary>The possible values of each optimized parameter (by index) and how each combination is built.</summary>
public sealed class ParameterSpace
{
    private readonly List<(string Name, Type Type, double[] Values)> _dims = new();
    private readonly Dictionary<int, string[]> _choices = new();

    public ParameterSpace(IReadOnlyList<ParameterRange> ranges, Type strategyType)
    {
        var props = Strategy.Parameters(strategyType).ToDictionary(p => p.Name);
        foreach (var r in ranges)
        {
            if (r.IsPeriod)
            {
                // the candle period: one more dimension, whose values are the slots in the list (from short to long)
                if (!r.Enabled || r.Choices.Count == 0) continue;
                _choices[_dims.Count] = r.Choices.ToArray();
                _dims.Add((r.Name, typeof(string), Enumerable.Range(0, r.Choices.Count).Select(i => (double)i).ToArray()));
            }
            else if (props.TryGetValue(r.Name, out var prop))
                _dims.Add((r.Name, prop.PropertyType, r.Values().ToArray()));
        }
    }

    /// <summary>The candle periods that are tried (empty if the period is not optimized).</summary>
    public IReadOnlyList<string> Periods => _choices.Values.FirstOrDefault() ?? Array.Empty<string>();

    /// <summary>For tests: dimensions with these sizes (values 0, 1, 2…).</summary>
    public ParameterSpace(params int[] sizes)
    {
        for (int k = 0; k < sizes.Length; k++) _dims.Add(($"p{k}", typeof(int), Enumerable.Range(0, sizes[k]).Select(v => (double)v).ToArray()));
    }

    public int Dimensions => _dims.Count;
    public int Length(int dim) => _dims[dim].Values.Length;
    public long Size => _dims.Aggregate(1L, (a, d) => a * Math.Max(1, d.Values.Length));

    public Dictionary<string, object?> Config(int[] idx)
    {
        var d = new Dictionary<string, object?>();
        for (int k = 0; k < _dims.Count; k++)
            d[_dims[k].Name] = _choices.TryGetValue(k, out var choices) ? choices[idx[k]] : Convert.ChangeType(_dims[k].Values[idx[k]], _dims[k].Type, CultureInfo.InvariantCulture);
        return d;
    }

    public static string Key(int[] idx) => string.Join(",", idx);

    /// <summary>Every combination (the grid).</summary>
    public IEnumerable<int[]> All()
    {
        var idx = new int[_dims.Count];
        while (true)
        {
            yield return (int[])idx.Clone();
            int j = 0;
            while (j < idx.Length)
            {
                if (++idx[j] < _dims[j].Values.Length) break;
                idx[j] = 0;
                j++;
            }

            if (j == idx.Length) yield break;
        }
    }

    /// <summary>How many grid steps separate them on the parameter that differs most (1 = neighbours).</summary>
    public static int Distance(int[] a, int[] b)
    {
        int d = 0;
        for (int k = 0; k < a.Length; k++) d = Math.Max(d, Math.Abs(a[k] - b[k]));
        return d;
    }
}

/// <summary>A combination run just once over the whole history.</summary>
internal sealed class ConfigRun
{
    public required int[] Index { get; init; }
    public required Dictionary<string, object?> Parameters { get; init; }

    /// <summary>Trades sorted by entry time.</summary>
    public ClosedTrade[] Trades { get; init; } = Array.Empty<ClosedTrade>();

    /// <summary>Equity per hour: the hour of the last point, the last value and the minimum of that hour.</summary>
    public long[] HourTimes { get; init; } = Array.Empty<long>();
    public float[] HourLast { get; init; } = Array.Empty<float>();
    public float[] HourMin { get; init; } = Array.Empty<float>();
    public string? Error { get; init; }
    public bool Ok => Error == null;

    /// <summary>What was measured in each window (from, to).</summary>
    public ConcurrentDictionary<(long, long), PerformanceMetrics> Windows { get; } = new();
}

/// <summary>
/// Each combination is run just once over the whole history, with the indicators warm and the market context
/// as it is live, and the windows are measured by trimming that result: the walk-forward and the cross-validation do not repeat
/// backtests. A window only uses what happened inside it (the engine never looks ahead).
/// </summary>
internal sealed class ConfigCache
{
    private readonly ConcurrentDictionary<string, Lazy<ConfigRun>> _runs = new();
    private readonly Func<Strategy> _factory;
    private readonly Dictionary<string, object?> _base;
    private readonly TickData _data;
    private readonly BacktestSettings _settings;
    private readonly IReadOnlyList<(DateTime Time, decimal Close)>? _context;
    private readonly ParameterSpace _space;
    private readonly CancellationToken _ct;

    public ConfigCache(Func<Strategy> factory, Dictionary<string, object?> baseParameters, TickData data, BacktestSettings settings,
        IReadOnlyList<(DateTime Time, decimal Close)>? context, ParameterSpace space, CancellationToken ct)
    {
        _factory = factory;
        _base = baseParameters;
        _data = data;
        _settings = settings;
        _context = context;
        _space = space;
        _ct = ct;
        Crypto = data.Instrument.FeedId.StartsWith("binance", StringComparison.Ordinal) || data.Instrument.FeedId.StartsWith("bybit", StringComparison.Ordinal);
    }

    public bool Crypto { get; }
    public decimal Capital => _settings.InitialCapital;

    /// <summary>Distinct combinations that were run.</summary>
    public int Count => _runs.Count;

    public IEnumerable<ConfigRun> Done => _runs.Values.Where(l => l.IsValueCreated).Select(l => l.Value);

    public ConfigRun Get(int[] idx) =>
        _runs.GetOrAdd(ParameterSpace.Key(idx), _ => new Lazy<ConfigRun>(() => Run(idx), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    public bool Has(int[] idx) => _runs.TryGetValue(ParameterSpace.Key(idx), out var l) && l.IsValueCreated;

    /// <summary>Runs the missing ones in parallel.</summary>
    public void Ensure(IReadOnlyList<int[]> list, int maxParallel, Action? done)
    {
        var options = new ParallelOptions { CancellationToken = _ct, MaxDegreeOfParallelism = maxParallel };
        Parallel.ForEach(list, options, idx =>
        {
            bool had = Has(idx);
            Get(idx);
            if (!had) done?.Invoke();
        });
    }

    private ConfigRun Run(int[] idx)
    {
        var pars = _space.Config(idx);
        var s = _factory();
        s.SetParameters(_base);
        s.SetParameters(pars);
        var settings = Optimizer.SettingsFor(_settings, pars).Clone();
        settings.CloseAtEnd = true;
        var r = new BacktestEngine(s, _data, settings) { ContextHistory = _context }.Run(_ct);
        if (!r.Ok) return new ConfigRun { Index = idx, Parameters = pars, Error = r.Error };

        // equity per hour (last and minimum): enough for the drawdown and the daily returns of any window
        var times = new List<long>();
        var last = new List<float>();
        var min = new List<float>();
        long hour = long.MinValue;
        foreach (var e in r.Equity)
        {
            long h = e.Time.Ticks / TimeSpan.TicksPerHour;
            float v = (float)e.Equity;
            if (h != hour)
            {
                hour = h;
                times.Add(e.Time.Ticks);
                last.Add(v);
                min.Add(v);
                continue;
            }

            times[^1] = e.Time.Ticks;
            last[^1] = v;
            if (v < min[^1]) min[^1] = v;
        }

        return new ConfigRun
        {
            Index = idx,
            Parameters = pars,
            Trades = r.Trades.OrderBy(t => t.EntryTime).ToArray(),
            HourTimes = times.ToArray(),
            HourLast = last.ToArray(),
            HourMin = min.ToArray(),
        };
    }

    /// <summary>
    /// How a combination did in [from, to): the trades that entered in that range and the equity of those
    /// hours (rebased to the initial capital at the start of the range).
    /// </summary>
    public PerformanceMetrics Measure(ConfigRun run, DateTime from, DateTime to) =>
        run.Windows.GetOrAdd((from.Ticks, to.Ticks), _ =>
        {
            var (trades, equity) = Slice(run, from, to, Capital);
            return PerformanceMetrics.Compute(trades, equity, Capital, Crypto, 0, 0);
        });

    /// <summary>The trades and the equity of a range, starting at <paramref name="capital"/>.</summary>
    public (List<ClosedTrade> Trades, List<EquityPoint> Equity) Slice(ConfigRun run, DateTime from, DateTime to, decimal capital)
    {
        var trades = new List<ClosedTrade>();
        int i = LowerEntry(run.Trades, from);
        for (; i < run.Trades.Length && run.Trades[i].EntryTime < to; i++) trades.Add(run.Trades[i]);
        int a = Lower(run.HourTimes, from.Ticks), b = Lower(run.HourTimes, to.Ticks);
        double start = a > 0 ? run.HourLast[a - 1] : (double)_settings.InitialCapital;
        var equity = new List<EquityPoint>(Math.Max(0, (b - a) * 2));
        for (int k = a; k < b; k++)
        {
            var t = new DateTime(run.HourTimes[k], DateTimeKind.Utc);
            equity.Add(new EquityPoint(t, capital + (decimal)(run.HourMin[k] - start), 0));
            equity.Add(new EquityPoint(t, capital + (decimal)(run.HourLast[k] - start), 0));
        }

        return (trades, equity);
    }

    /// <summary>Result of each day of <paramref name="days"/> (change in equity from end to end of the day).</summary>
    public static double[] Daily(ConfigRun run, IReadOnlyList<DateOnly> days, double capital)
    {
        var pnl = new double[days.Count];
        double prev = capital;
        int h = 0;
        for (int d = 0; d < days.Count; d++)
        {
            long end = days[d].AddDays(1).ToDateTime(TimeOnly.MinValue).Ticks;
            double last = prev;
            while (h < run.HourTimes.Length && run.HourTimes[h] < end)
            {
                last = run.HourLast[h];
                h++;
            }

            pnl[d] = last - prev;
            prev = last;
        }

        return pnl;
    }

    /// <summary>Trades that entered each day.</summary>
    public static int[] TradesPerDay(ConfigRun run, IReadOnlyList<DateOnly> days)
    {
        var map = new Dictionary<DateOnly, int>();
        for (int i = 0; i < days.Count; i++) map[days[i]] = i;
        var n = new int[days.Count];
        foreach (var t in run.Trades)
            if (map.TryGetValue(DateOnly.FromDateTime(t.EntryTime), out int i))
                n[i]++;
        return n;
    }

    private static int LowerEntry(ClosedTrade[] trades, DateTime t)
    {
        int lo = 0, hi = trades.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (trades[mid].EntryTime < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    private static int Lower(long[] a, long t)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (a[mid] < t) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}

/// <summary>
/// Bayesian optimization with TPE (Tree-structured Parzen Estimator, Bergstra et al., 2011) over a discrete grid:
/// it starts with well spread out points (Latin hypercube) and afterwards proposes where the density of the good combinations
/// beats that of the bad ones. It evaluates in batches to use every core.
/// </summary>
public static class TpeSearch
{
    /// <param name="evaluate">Returns the score of each combination (-infinity if it is no good, for example for lack of trades).</param>
    public static List<(int[] Idx, double Score)> Run(ParameterSpace space, Func<IReadOnlyList<int[]>, double[]> evaluate, int budget, int seed, int batch,
        CancellationToken ct = default)
    {
        var rnd = new Random(seed);
        var seen = new HashSet<string>();
        var done = new List<(int[] Idx, double Score)>();
        budget = (int)Math.Min(budget, space.Size);
        batch = Math.Max(1, batch);

        void Take(List<int[]> points)
        {
            if (points.Count == 0) return;
            var scores = evaluate(points);
            for (int i = 0; i < points.Count; i++) done.Add((points[i], scores[i]));
        }

        int startup = (int)Math.Min(budget, Math.Clamp(budget / 4, Math.Min(8, budget), 30));
        Take(LatinHypercube(space, startup, rnd, seen));
        while (done.Count < budget && seen.Count < space.Size)
        {
            ct.ThrowIfCancellationRequested();
            var next = Propose(space, done, Math.Min(batch, budget - done.Count), rnd, seen);
            if (next.Count == 0) break;
            Take(next);
        }

        return done;
    }

    private static List<int[]> LatinHypercube(ParameterSpace space, int n, Random rnd, HashSet<string> seen)
    {
        var points = new List<int[]>();
        var perms = Enumerable.Range(0, space.Dimensions).Select(_ => Enumerable.Range(0, n).OrderBy(_ => rnd.Next()).ToArray()).ToArray();
        for (int i = 0; i < n; i++)
        {
            var idx = new int[space.Dimensions];
            for (int k = 0; k < space.Dimensions; k++)
                idx[k] = Math.Min(space.Length(k) - 1, (int)((perms[k][i] + rnd.NextDouble()) / n * space.Length(k)));
            if (seen.Add(ParameterSpace.Key(idx))) points.Add(idx);
        }

        // if the hypercube repeated points (small dimensions), it is filled in at random
        for (int tries = 0; points.Count < n && tries < n * 20; tries++)
        {
            var idx = RandomPoint(space, rnd);
            if (seen.Add(ParameterSpace.Key(idx))) points.Add(idx);
        }

        return points;
    }

    private static int[] RandomPoint(ParameterSpace space, Random rnd)
    {
        var idx = new int[space.Dimensions];
        for (int k = 0; k < idx.Length; k++) idx[k] = rnd.Next(space.Length(k));
        return idx;
    }

    private static List<int[]> Propose(ParameterSpace space, List<(int[] Idx, double Score)> done, int want, Random rnd, HashSet<string> seen)
    {
        var finite = done.Where(d => double.IsFinite(d.Score)).OrderByDescending(d => d.Score).ToList();
        var result = new List<int[]>();
        if (finite.Count < 2)
        {
            // there is nothing good to imitate yet: at random
            for (int tries = 0; result.Count < want && tries < want * 50; tries++)
            {
                var idx = RandomPoint(space, rnd);
                if (seen.Add(ParameterSpace.Key(idx))) result.Add(idx);
            }

            return result;
        }

        int nGood = Math.Clamp((int)Math.Ceiling(0.2 * finite.Count), 1, 25);
        var good = finite.Take(nGood).Select(d => d.Idx).ToList();
        var bad = done.Except(finite.Take(nGood)).Select(d => d.Idx).ToList();
        int dims = space.Dimensions;
        var pg = new double[dims][];
        var pb = new double[dims][];
        for (int k = 0; k < dims; k++)
        {
            int len = space.Length(k);
            double h = Math.Max(0.75, len / 8.0);
            pg[k] = Parzen(good.Select(x => x[k]), len, h);
            pb[k] = Parzen(bad.Select(x => x[k]), len, h);
        }

        // many candidates drawn from the density of the good ones; the best good/bad ratio ones are kept
        var cands = new Dictionary<string, (int[] Idx, double Score)>();
        for (int c = 0; c < Math.Max(48, want * 24); c++)
        {
            var idx = new int[dims];
            double score = 0;
            for (int k = 0; k < dims; k++)
            {
                idx[k] = Sample(pg[k], rnd);
                score += Math.Log(pg[k][idx[k]] / pb[k][idx[k]]);
            }

            string key = ParameterSpace.Key(idx);
            if (!seen.Contains(key)) cands[key] = (idx, score);
        }

        foreach (var c in cands.Values.OrderByDescending(c => c.Score).Take(want))
            if (seen.Add(ParameterSpace.Key(c.Idx)))
                result.Add(c.Idx);
        for (int tries = 0; result.Count < want && tries < want * 50; tries++)
        {
            var idx = RandomPoint(space, rnd);
            if (seen.Add(ParameterSpace.Key(idx))) result.Add(idx);
        }

        return result;
    }

    /// <summary>Parzen density over the indices of a parameter (with a floor so nothing is ruled out entirely).</summary>
    private static double[] Parzen(IEnumerable<int> points, int len, double h)
    {
        var p = new double[len];
        for (int v = 0; v < len; v++) p[v] = 1.0 / len;
        int n = 0;
        foreach (var x in points)
        {
            n++;
            for (int v = 0; v < len; v++) p[v] += Math.Exp(-0.5 * Math.Pow((v - x) / h, 2));
        }

        double sum = p.Sum();
        for (int v = 0; v < len; v++) p[v] /= sum;
        return p;
    }

    private static int Sample(double[] p, Random rnd)
    {
        double u = rnd.NextDouble(), acc = 0;
        for (int v = 0; v < p.Length; v++)
        {
            acc += p[v];
            if (u <= acc) return v;
        }

        return p.Length - 1;
    }
}

/// <summary>
/// The best plateau, not the best peak: the score of each combination is the average with its neighbours (one grid step in
/// each parameter), shrunk towards the median when it has few measured neighbours. A combination that wins alone among
/// neighbours that lose is almost always luck; a whole zone that wins is not.
/// </summary>
public static class RobustSelection
{
    public static int Select(IReadOnlyList<(int[] Idx, double Score)> rows)
    {
        var finite = rows.Where(r => double.IsFinite(r.Score)).Select(r => r.Score).OrderBy(s => s).ToList();
        if (finite.Count == 0) return -1;
        double median = finite[finite.Count / 2];
        double worst = finite[0] - (finite[^1] - finite[0]) * 0.1 - Math.Abs(finite[0]) * 0.1;
        int best = -1;
        double bestScore = double.NegativeInfinity, bestOwn = double.NegativeInfinity;
        for (int i = 0; i < rows.Count; i++)
        {
            if (!double.IsFinite(rows[i].Score)) continue;
            double sum = 0;
            int n = 0;
            for (int j = 0; j < rows.Count; j++)
            {
                if (ParameterSpace.Distance(rows[i].Idx, rows[j].Idx) > 1) continue;
                sum += double.IsFinite(rows[j].Score) ? rows[j].Score : worst;
                n++;
            }

            double score = (sum + median) / (n + 1);
            if (score > bestScore || (score == bestScore && rows[i].Score > bestOwn))
            {
                best = i;
                bestScore = score;
                bestOwn = rows[i].Score;
            }
        }

        return best;
    }
}

/// <summary>Overoptimization measures over the daily result of every combination tested.</summary>
public static class Overfitting
{
    /// <summary>
    /// Probability of overfitting with combinatorially symmetric cross-validation (Bailey, Borwein, Lopez de
    /// Prado and Zhu, "The Probability of Backtest Overfitting", 2017): it splits the history into blocks and, for each way of
    /// picking half of them as "in sample", takes the combination with the best Sharpe there and looks at where it lands in the other
    /// half. PBO = share of times it lands below the median; Loss = share of times it loses money.
    /// </summary>
    public static (double Pbo, double Loss, int Splits)? Pbo(IReadOnlyList<double[]> daily, int blocks = 10)
    {
        int n = daily.Count;
        if (n < 4) return null;
        int days = daily[0].Length;
        while (blocks >= 4 && days < blocks * 5) blocks -= 2;
        if (blocks < 4) return null;
        var bounds = Enumerable.Range(0, blocks + 1).Select(b => (int)((long)b * days / blocks)).ToArray();
        var stats = new (double Sum, double Sq, int N)[n, blocks];
        for (int c = 0; c < n; c++)
            for (int b = 0; b < blocks; b++)
                for (int d = bounds[b]; d < bounds[b + 1]; d++)
                {
                    double v = daily[c][d];
                    stats[c, b] = (stats[c, b].Sum + v, stats[c, b].Sq + v * v, stats[c, b].N + 1);
                }

        int half = blocks / 2, splits = 0, below = 0, losses = 0;
        var isPerf = new double[n];
        var osPerf = new double[n];
        var osNet = new double[n];
        foreach (var subset in Combinations(blocks, half))
        {
            var inSet = new bool[blocks];
            foreach (var b in subset) inSet[b] = true;
            for (int c = 0; c < n; c++)
            {
                double si = 0, qi = 0, so = 0, qo = 0;
                int ni = 0, no = 0;
                for (int b = 0; b < blocks; b++)
                {
                    var (s, q, k) = stats[c, b];
                    if (inSet[b])
                    {
                        si += s;
                        qi += q;
                        ni += k;
                    }
                    else
                    {
                        so += s;
                        qo += q;
                        no += k;
                    }
                }

                isPerf[c] = Sharpe(si, qi, ni);
                osPerf[c] = Sharpe(so, qo, no);
                osNet[c] = so;
            }

            int star = 0;
            for (int c = 1; c < n; c++)
                if (isPerf[c] > isPerf[star])
                    star = c;
            // relative out-of-sample rank (1 = the worst): w = rank / (n + 1); lambda = log(w / (1 - w)) <= 0 -> bottom half
            int rank = 1;
            for (int c = 0; c < n; c++)
                if (c != star && osPerf[c] < osPerf[star])
                    rank++;
            double w = rank / (n + 1.0);
            if (Math.Log(w / (1 - w)) <= 0) below++;
            if (osNet[star] <= 0) losses++;
            splits++;
        }

        return splits == 0 ? null : ((double)below / splits, (double)losses / splits, splits);
    }

    /// <summary>
    /// Purged combinatorial cross-validation (Lopez de Prado, "Advances in Financial Machine Learning", 2018): it splits the
    /// history into consecutive groups; for each pair of test groups it picks the combination (by plateau) using the other days
    /// -without the ones adjacent to the test (purge and embargo)- and measures how it did on those two groups. With that, several
    /// complete out-of-sample paths are built instead of just one.
    /// </summary>
    /// <param name="index">Grid index of each combination (for the plateau).</param>
    /// <param name="daily">Daily result of each combination.</param>
    /// <param name="tradesPerDay">Trades that entered each day, per combination.</param>
    public static List<CpcvPath> Cpcv(IReadOnlyList<int[]> index, IReadOnlyList<double[]> daily, IReadOnlyList<int[]> tradesPerDay, int minTrades,
        double capital, bool crypto, int groups = 8, int embargo = 1)
    {
        var paths = new List<CpcvPath>();
        int n = daily.Count;
        if (n == 0) return paths;
        int days = daily[0].Length;
        while (groups >= 4 && days < groups * 5) groups--;
        if (groups < 4) return paths;
        var bounds = Enumerable.Range(0, groups + 1).Select(g => (int)((long)g * days / groups)).ToArray();
        var pieces = Enumerable.Range(0, groups).Select(_ => new List<double[]>()).ToArray();
        var rows = new (int[] Idx, double Score)[n];
        foreach (var pair in Combinations(groups, 2))
        {
            int a = pair[0], b = pair[1];
            // training days: the ones that are not test days and are not within "embargo" days of a test group
            var train = new bool[days];
            for (int d = 0; d < days; d++) train[d] = true;
            foreach (var g in new[] { a, b })
                for (int d = Math.Max(0, bounds[g] - embargo); d < Math.Min(days, bounds[g + 1] + embargo); d++)
                    train[d] = false;
            for (int c = 0; c < n; c++)
            {
                double s = 0, q = 0;
                int k = 0, trades = 0;
                for (int d = 0; d < days; d++)
                {
                    if (!train[d]) continue;
                    double v = daily[c][d];
                    s += v;
                    q += v * v;
                    k++;
                    trades += tradesPerDay[c][d];
                }

                rows[c] = (index[c], trades >= minTrades ? Sharpe(s, q, k) : double.NegativeInfinity);
            }

            int pick = RobustSelection.Select(rows);
            if (pick < 0) pick = 0;
            foreach (var g in new[] { a, b }) pieces[g].Add(daily[pick][bounds[g]..bounds[g + 1]]);
        }

        // each group was a test group in (groups - 1) pairs: path j takes the j-th time of each group
        int count = pieces.Min(p => p.Count);
        double ann = Math.Sqrt(crypto ? 365 : 252);
        for (int j = 0; j < count; j++)
        {
            var series = pieces.SelectMany(p => p[j]).ToArray();
            double eq = 0, peak = 0, dd = 0, sum = 0, sq = 0;
            foreach (var v in series)
            {
                eq += v;
                peak = Math.Max(peak, eq);
                dd = Math.Max(dd, peak - eq);
                sum += v;
                sq += v * v;
            }

            double mean = series.Length > 0 ? sum / series.Length : 0;
            double sd = series.Length > 1 ? Math.Sqrt(Math.Max(0, (sq - series.Length * mean * mean) / (series.Length - 1))) : 0;
            paths.Add(new CpcvPath
            {
                Number = j + 1,
                Net = (decimal)Math.Round(sum, 2),
                Sharpe = sd > 0 ? mean / sd * ann : 0,
                MaxDrawdown = (decimal)Math.Round(dd, 2),
                Days = series.Length,
            });
        }

        return paths;
    }

    /// <summary>Sharpe (not annualized) of a sum of days: mean over standard deviation.</summary>
    private static double Sharpe(double sum, double sq, int n)
    {
        if (n < 2) return n == 1 ? sum : 0;
        double mean = sum / n;
        double var = Math.Max(0, (sq - n * mean * mean) / (n - 1));
        return var > 0 ? mean / Math.Sqrt(var) : mean > 0 ? 1e6 : mean < 0 ? -1e6 : 0;
    }

    /// <summary>Every way of picking k out of n (in order).</summary>
    public static IEnumerable<int[]> Combinations(int n, int k)
    {
        var c = Enumerable.Range(0, k).ToArray();
        while (true)
        {
            yield return (int[])c.Clone();
            int i = k - 1;
            while (i >= 0 && c[i] == n - k + i) i--;
            if (i < 0) yield break;
            c[i]++;
            for (int j = i + 1; j < k; j++) c[j] = c[j - 1] + 1;
        }
    }
}
