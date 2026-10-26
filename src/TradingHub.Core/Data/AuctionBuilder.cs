using System.Text.Json;
using TradingHub.Feeds;

namespace TradingHub.Data;

/// <summary>Builds a TPO + volume profile from 1-minute candles (when no tick-level volume by price is at hand).</summary>
public static class AuctionBuilder
{
    /// <summary>
    /// Rows are <paramref name="rowSize"/> wide. A candle puts its period letter on every row between its low and high, and its volume is
    /// spread evenly over those rows (a candle does not say where inside its range the volume traded, so this is an approximation).
    /// </summary>
    /// <param name="slotOfBar">The TPO period (0 = first) of each candle in the range, indexed from <paramref name="from"/>.</param>
    public static AuctionInput FromCandles(IReadOnlyList<Kline> bars, int from, int to, Func<int, int> slotOfBar, decimal rowSize)
    {
        decimal lo = decimal.MaxValue, hi = decimal.MinValue;
        for (int i = from; i <= to; i++) { lo = Math.Min(lo, bars[i].Low); hi = Math.Max(hi, bars[i].High); }
        long first = (long)Math.Floor(lo / rowSize), last = (long)Math.Floor(hi / rowSize);
        int n = (int)(last - first + 1);
        var slots = new SortedSet<int>[n];
        for (int r = 0; r < n; r++) slots[r] = new SortedSet<int>();
        var volume = new decimal[n];
        for (int i = from; i <= to; i++)
        {
            var b = bars[i];
            int a = (int)((long)Math.Floor(b.Low / rowSize) - first), z = (int)((long)Math.Floor(b.High / rowSize) - first);
            int slot = slotOfBar(i);
            decimal share = b.Volume / (z - a + 1);
            for (int r = a; r <= z; r++) { slots[r].Add(slot); volume[r] += share; }
        }

        var rows = Enumerable.Range(0, n).Select(r => (first + r) * rowSize).ToArray();
        return new AuctionInput(rows, slots.Select(s => s.ToArray()).ToArray(), volume, rowSize);
    }
}

/// <summary>Counts of what happened in past sessions, saved by the research and read by the Auction map indicator.</summary>
public sealed class AuctionStats
{
    public string Symbol { get; set; } = "";
    public int Sessions { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Basis { get; set; } = "";

    /// <summary>Initial-balance size cut-offs in ticks (below the first = small, above the second = large).</summary>
    public double[] IbCuts { get; set; } = Array.Empty<double>();

    /// <summary>By open location ("inside", "above", "below"): how many sessions and the counts of each outcome.</summary>
    public Dictionary<string, Bucket> Open { get; set; } = new();

    /// <summary>By open location and initial-balance size ("inside|small"...): how the days ended.</summary>
    public Dictionary<string, Bucket> DayTypes { get; set; } = new();

    public sealed class Bucket
    {
        public int N { get; set; }
        public Dictionary<string, int> Counts { get; set; } = new();
        public double Rate(string key) => N == 0 ? 0 : Counts.GetValueOrDefault(key) / (double)N;
    }

    private static string PathOf(string symbol)
    {
        string name = string.Concat(symbol.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string root = Environment.GetEnvironmentVariable("TRADINGHUB_DATA_DIR") is { Length: > 0 } d ? d : TradingHub.Common.Product.AppData;
        return System.IO.Path.Combine(root, "AuctionStats", name + ".json");
    }

    public static AuctionStats? Load(string symbol)
    {
        try
        {
            var path = PathOf(symbol);
            return File.Exists(path) ? JsonSerializer.Deserialize<AuctionStats>(File.ReadAllText(path)) : null;
        }
        catch (Exception) { return null; }
    }

    public void Save()
    {
        var path = PathOf(Symbol);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The 95% interval of a rate (Wilson score), as (low, high).</summary>
    public static (double Low, double High) Wilson(int successes, int n)
    {
        if (n == 0) return (0, 1);
        double p = successes / (double)n, z = 1.96, denom = 1 + z * z / n;
        double centre = p + z * z / (2 * n), margin = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n));
        return ((centre - margin) / denom, (centre + margin) / denom);
    }
}
