using System.ComponentModel;

namespace TradingHub.Strategies;

/// <summary>Market condition based on the trend efficiency of the last 24 hours.</summary>
public enum MarketRegime
{
    [Description("No data")] Unknown,
    [Description("Range")] Range,
    [Description("Mixed")] Mixed,
    [Description("Trend")] Trend,
}

/// <summary>
/// Under which market conditions a strategy can open trades (context filter): any combination of
/// range, mixed, up trend and down trend. New values always go at the end (they are stored as a number).
/// </summary>
public enum RegimeFilter
{
    [Description("All")] All,
    [Description("Range only")] RangeOnly,
    [Description("No trend (range or mixed)")] NoTrend,
    [Description("Trend only")] TrendOnly,
    [Description("No range (mixed or trend)")] NoRange,
    [Description("Up trend only")] UpTrendOnly,
    [Description("Down trend only")] DownTrendOnly,
    [Description("Mixed only")] MixedOnly,
    [Description("Range or up trend")] RangeOrUpTrend,
    [Description("Range or down trend")] RangeOrDownTrend,
    [Description("Mixed or up trend")] MixedOrUpTrend,
    [Description("Mixed or down trend")] MixedOrDownTrend,
    [Description("Everything but down trend")] NoDownTrend,
    [Description("Everything but up trend")] NoUpTrend,
    [Description("Range or trend (no mixed)")] NoMixed,
}

/// <summary>The four market conditions, combinable.</summary>
[Flags]
public enum RegimeSet
{
    None = 0,
    Range = 1,
    Mixed = 2,
    UpTrend = 4,
    DownTrend = 8,
    All = Range | Mixed | UpTrend | DownTrend,
}

/// <summary>
/// Market context for the strategies and their filters, using closed candles only (no peeking at the future):
/// <list type="bullet">
/// <item>Trend: efficiency of the hourly closes of the last 24 hours, |net move| / distance travelled (0 = pure
/// range, 1 = clean trend; a random walk gives ~0.2). Range up to 0.15, trend from 0.35.</item>
/// <item>Direction: whether it rose or fell over those 24 hours (a trend is up or down based on that).</item>
/// <item>Volatility: standard deviation of the hourly returns of those 24 hours, in %.</item>
/// <item>Volatility percentile: where that volatility sits among the last 30 days' (0 = the lowest).</item>
/// </list>
/// With candles of an hour or more, the last 25 candles are used on their own scale. It updates candle by candle.
/// </summary>
public sealed class MarketContextTracker
{
    public const double RangeLimit = 0.15, TrendLimit = 0.35;

    /// <summary>Volatility percentiles (of the last month) up to which it is low and from which it is high.</summary>
    public const double LowVolatility = 33, HighVolatility = 67;
    private const int RankHours = 30 * 24;

    private readonly bool _coarse;
    private readonly List<decimal> _closes = new(); // hourly closes (or per candle if they are an hour or more)
    private readonly Queue<double> _history = new(); // 24-hour volatility sampled once per hour (30 days)
    private long _hour = long.MinValue;

    public MarketContextTracker(bool coarse = false) => _coarse = coarse;

    /// <summary>Trend efficiency (null if there are not 7 closes yet).</summary>
    public double? Trend { get; private set; }

    /// <summary>Which way it moved over those 24 hours: 1 up, -1 down, 0 flat (null with no data).</summary>
    public int? Direction { get; private set; }

    /// <summary>Net move of those 24 hours, in % (null with no data).</summary>
    public double? Change { get; private set; }

    /// <summary>Volatility of the last 24 hours in % (null if there is no data yet).</summary>
    public double? Volatility { get; private set; }

    /// <summary>Percentile of the current volatility among the last 30 days' (null with less than a day of history).</summary>
    public double? VolatilityRank { get; private set; }

    public MarketRegime Regime => Trend is not { } t ? MarketRegime.Unknown : t <= RangeLimit ? MarketRegime.Range : t >= TrendLimit ? MarketRegime.Trend : MarketRegime.Mixed;

    /// <summary>"Up trend", "Range", "Mixed"… for display.</summary>
    public string RegimeLabel => Label(Regime, Direction);

    public static string Label(MarketRegime regime, int? direction) => regime switch
    {
        MarketRegime.Range => "Range",
        MarketRegime.Mixed => "Mixed",
        MarketRegime.Trend => direction > 0 ? "Up trend" : direction < 0 ? "Down trend" : "Trend",
        _ => "No data",
    };

    /// <summary>Low volatility (up to the 33rd percentile of the last month), normal or high (from the 67th).</summary>
    public static string VolatilityLabel(double? rank) => rank is not { } r ? "no data" : r <= LowVolatility ? "low" : r >= HighVolatility ? "high" : "normal";

    /// <summary>
    /// Adds earlier hourly history (to start live with the full 24 hours and month of volatility, as in a long
    /// backtest). With candles of an hour or more it is grouped into <paramref name="barPeriod"/> candles.
    /// </summary>
    public void AddHistory(IEnumerable<(DateTime Time, decimal Close)> hourly, TimeSpan barPeriod)
    {
        if (!_coarse)
        {
            foreach (var (t, c) in hourly) Add(t, c);
            return;
        }

        long size = Math.Max(TimeSpan.TicksPerHour, barPeriod.Ticks), bucket = long.MinValue;
        decimal close = 0;
        foreach (var (t, c) in hourly)
        {
            long b = t.Ticks - t.Ticks % size;
            if (b != bucket && bucket != long.MinValue) Add(new DateTime(bucket, DateTimeKind.Utc), close);
            bucket = b;
            close = c;
        }

        if (bucket != long.MinValue) Add(new DateTime(bucket, DateTimeKind.Utc), close);
    }

    /// <summary>Adds a closed candle.</summary>
    public void Add(DateTime time, decimal close)
    {
        if (_coarse)
        {
            _closes.Add(close);
            if (_closes.Count > 25) _closes.RemoveAt(0);
            Recompute(sample: true);
            return;
        }

        long hour = time.Ticks / TimeSpan.TicksPerHour;
        if (hour != _hour)
        {
            // new hour: the previous one is now closed (its volatility is stored for the percentile)
            bool sample = _hour != long.MinValue;
            _closes.Add(close);
            _hour = hour;
            if (_closes.Count > 25) _closes.RemoveAt(0);
            Recompute(sample);
        }
        else if (_closes.Count > 0)
        {
            _closes[^1] = close; // the hour's last candle is its close
            Recompute(sample: false);
        }
    }

    private void Recompute(bool sample)
    {
        if (_closes.Count < 7)
        {
            Trend = Volatility = VolatilityRank = Change = null;
            Direction = null;
            return;
        }

        decimal path = 0;
        double sum = 0, sumSq = 0;
        int n = 0;
        for (int i = 1; i < _closes.Count; i++)
        {
            path += Math.Abs(_closes[i] - _closes[i - 1]);
            if (_closes[i - 1] <= 0) continue;
            double r = (double)((_closes[i] - _closes[i - 1]) / _closes[i - 1]);
            sum += r;
            sumSq += r * r;
            n++;
        }

        Trend = path > 0 ? Math.Round((double)(Math.Abs(_closes[^1] - _closes[0]) / path), 4) : 0;
        Direction = Math.Sign(_closes[^1] - _closes[0]);
        Change = _closes[0] > 0 ? Math.Round((double)((_closes[^1] - _closes[0]) / _closes[0]) * 100, 3) : null;
        double vol = n > 1 ? Math.Sqrt(Math.Max(0, (sumSq - sum * sum / n) / (n - 1))) * 100 : 0;
        Volatility = Math.Round(vol, 5);
        if (sample)
        {
            _history.Enqueue(vol);
            if (_history.Count > RankHours) _history.Dequeue();
        }

        if (_history.Count < 24)
        {
            VolatilityRank = null;
            return;
        }

        int below = 0;
        foreach (var v in _history)
            if (v < vol)
                below++;
        VolatilityRank = Math.Round(100.0 * below / _history.Count, 1);
    }

    /// <summary>Does the filter allow opening a trade in this context? With no data to judge, it does not block.</summary>
    public bool Allows(RegimeFilter filter, double minRank, double maxRank)
    {
        bool regimeOk = Regime == MarketRegime.Unknown || RegimeAllows(filter, Regime, Direction);
        bool volOk = VolatilityRank is not { } rank || (rank >= minRank && rank <= maxRank);
        return regimeOk && volOk;
    }

    /// <summary>Does the regime filter allow entering in that market condition (ignoring volatility)?</summary>
    public static bool RegimeAllows(RegimeFilter filter, MarketRegime regime, int? direction)
    {
        var now = SetOf(regime, direction);
        return now == RegimeSet.None || (SetOf(filter) & now) != 0;
    }

    /// <summary>The conditions a filter allows entering in.</summary>
    public static RegimeSet SetOf(RegimeFilter filter) => filter switch
    {
        RegimeFilter.RangeOnly => RegimeSet.Range,
        RegimeFilter.NoTrend => RegimeSet.Range | RegimeSet.Mixed,
        RegimeFilter.TrendOnly => RegimeSet.UpTrend | RegimeSet.DownTrend,
        RegimeFilter.NoRange => RegimeSet.Mixed | RegimeSet.UpTrend | RegimeSet.DownTrend,
        RegimeFilter.UpTrendOnly => RegimeSet.UpTrend,
        RegimeFilter.DownTrendOnly => RegimeSet.DownTrend,
        RegimeFilter.MixedOnly => RegimeSet.Mixed,
        RegimeFilter.RangeOrUpTrend => RegimeSet.Range | RegimeSet.UpTrend,
        RegimeFilter.RangeOrDownTrend => RegimeSet.Range | RegimeSet.DownTrend,
        RegimeFilter.MixedOrUpTrend => RegimeSet.Mixed | RegimeSet.UpTrend,
        RegimeFilter.MixedOrDownTrend => RegimeSet.Mixed | RegimeSet.DownTrend,
        RegimeFilter.NoDownTrend => RegimeSet.Range | RegimeSet.Mixed | RegimeSet.UpTrend,
        RegimeFilter.NoUpTrend => RegimeSet.Range | RegimeSet.Mixed | RegimeSet.DownTrend,
        RegimeFilter.NoMixed => RegimeSet.Range | RegimeSet.UpTrend | RegimeSet.DownTrend,
        _ => RegimeSet.All,
    };

    /// <summary>The filter that allows entering in exactly those conditions (with none, or with all of them: no filter).</summary>
    public static RegimeFilter FilterOf(RegimeSet set)
    {
        if (set == RegimeSet.None) return RegimeFilter.All;
        foreach (var f in Enum.GetValues<RegimeFilter>())
            if (SetOf(f) == set)
                return f;
        return RegimeFilter.All;
    }

    /// <summary>The condition of a moment: range, mixed or up or down trend (a trend with no direction counts as both).</summary>
    public static RegimeSet SetOf(MarketRegime regime, int? direction) => regime switch
    {
        MarketRegime.Range => RegimeSet.Range,
        MarketRegime.Mixed => RegimeSet.Mixed,
        MarketRegime.Trend => direction > 0 ? RegimeSet.UpTrend : direction < 0 ? RegimeSet.DownTrend : RegimeSet.UpTrend | RegimeSet.DownTrend,
        _ => RegimeSet.None,
    };

    /// <summary>The condition based on the trend efficiency and its direction (the ones each trade stores).</summary>
    public static RegimeSet SetOf(double trend, int? direction) =>
        SetOf(trend <= RangeLimit ? MarketRegime.Range : trend >= TrendLimit ? MarketRegime.Trend : MarketRegime.Mixed, direction);

    /// <summary>Merges two series of hourly closes (the second overrides the first on the hours that repeat), in order.</summary>
    public static List<(DateTime Time, decimal Close)> Merge(IEnumerable<(DateTime Time, decimal Close)>? first, IEnumerable<(DateTime Time, decimal Close)> second)
    {
        var map = new SortedDictionary<DateTime, decimal>();
        if (first != null)
            foreach (var (t, c) in first)
                map[t] = c;
        foreach (var (t, c) in second) map[t] = c;
        return map.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    /// <summary>Hourly closes of some data (the last price of each hour), to give the context some history.</summary>
    public static List<(DateTime Time, decimal Close)> HourlyCloses(TickData data)
    {
        var list = new List<(DateTime, decimal)>();
        long hour = long.MinValue;
        for (int i = data.Start; i < data.End; i++)
        {
            long t = data.TimeTicks(i);
            long h = t - t % TimeSpan.TicksPerHour;
            decimal price = data.Instrument.FromTicks(data.PriceTicks(i));
            if (h != hour)
            {
                list.Add((new DateTime(h, DateTimeKind.Utc), price));
                hour = h;
            }
            else list[^1] = (list[^1].Item1, price);
        }

        return list;
    }

    /// <summary>Short text of the context: "up trend (0.42) · volatility percentile 28".</summary>
    public string Describe() =>
        Trend is not { } t ? "no context data yet"
        : $"{RegimeLabel.ToLowerInvariant()} ({t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)})" +
          (VolatilityRank is { } r ? $" · volatility percentile {r:0}" : "");
}
