namespace TradingHub.Data;

/// <summary>A remembered level, as far as the model cares.</summary>
public sealed record LevelSample(DateTime Time, decimal Price, decimal Volume, bool Buy);

/// <summary>
/// A small logistic regression that learns, from levels whose result is known, how likely a new level is to HOLD
/// (price comes back and moves away on the order's side) rather than BREAK. It uses the order's size against the usual size,
/// its side, the time of day, and how far the nearest other level is. Fitted on the user's own data, on their own PC, and
/// checked on the most recent quarter of the results it did not train on.
/// </summary>
public sealed class HoldModel
{
    private const int Features = 6; // bias + 5
    private readonly double[] _w, _mean, _std;
    private readonly double _medianVolume;
    private readonly double[] _sortedPrices;
    private readonly double _tick;

    /// <summary>How many labelled levels the final model was fitted on.</summary>
    public int TrainedOn { get; private set; }

    /// <summary>Levels in the held-out test part (0 if there were too few results to hold any out).</summary>
    public int TestedOn { get; private set; }

    /// <summary>Share of the test levels the model called right (hold if probability above 50%).</summary>
    public double Accuracy { get; private set; }

    /// <summary>Share of the test levels that held: what always guessing the more common result would score is max(this, 1 - this).</summary>
    public double TestHoldRate { get; private set; }

    /// <summary>
    /// True only when the model beat the "always guess the more common result" baseline by at least 3 points on at least 40
    /// results it had not trained on. Anything else is noise and should not be shown as a probability.
    /// </summary>
    public bool HasSkill => TestedOn >= 40 && Accuracy >= Math.Max(TestHoldRate, 1 - TestHoldRate) + 0.03;

    private HoldModel(double[] w, double[] mean, double[] std, double medianVolume, double[] sortedPrices, double tick)
    {
        _w = w;
        _mean = mean;
        _std = std;
        _medianVolume = medianVolume;
        _sortedPrices = sortedPrices;
        _tick = tick;
    }

    /// <summary>Null when there are too few results or only one kind of result to learn from.</summary>
    public static HoldModel? Train(IReadOnlyList<(LevelSample Level, bool Held)> labelled, IReadOnlyList<LevelSample> allLevels, decimal tickSize, int minimum = 30)
    {
        if (labelled.Count < minimum || labelled.All(l => l.Held) || labelled.All(l => !l.Held)) return null;
        double tick = Math.Max((double)tickSize, 1e-9);
        var prices = allLevels.Select(l => (double)l.Price).OrderBy(p => p).ToArray();
        double median = allLevels.Select(l => (double)l.Volume).OrderBy(v => v).ElementAt(allLevels.Count / 2);
        var ordered = labelled.OrderBy(l => l.Level.Time).ToList();

        int test = ordered.Count >= 40 ? ordered.Count / 4 : 0;
        var trainPart = ordered.Take(ordered.Count - test).ToList();
        var fitted = Fit(trainPart, prices, median, tick);
        if (fitted == null) return null;
        double acc = 0, held = 0;
        if (test > 0)
        {
            var part = ordered.Skip(ordered.Count - test).ToList();
            acc = part.Count(l => (fitted.Predict(l.Level) > 0.5) == l.Held) / (double)part.Count;
            held = part.Count(l => l.Held) / (double)part.Count;
        }

        var final = Fit(ordered, prices, median, tick) ?? fitted;
        final.TrainedOn = ordered.Count;
        final.TestedOn = test;
        final.Accuracy = acc;
        final.TestHoldRate = held;
        return final;
    }

    private double[] Describe(LevelSample s)
    {
        double hour = s.Time.TimeOfDay.TotalHours;
        // distance to the nearest other level, in ticks (the level itself is in the list: skip one copy of its own price)
        double price = (double)s.Price;
        int i = Array.BinarySearch(_sortedPrices, price);
        if (i < 0) i = ~i;
        double near = double.MaxValue;
        for (int j = Math.Max(0, i - 2); j < Math.Min(_sortedPrices.Length, i + 3); j++)
        {
            double d = Math.Abs(_sortedPrices[j] - price);
            if (d > 0 && d < near) near = d;
        }

        if (near == double.MaxValue) near = 1000 * _tick;
        return new[]
        {
            1.0,
            Math.Log(Math.Max((double)s.Volume / Math.Max(_medianVolume, 1e-9), 1e-3)),
            s.Buy ? 1.0 : 0.0,
            Math.Sin(2 * Math.PI * hour / 24),
            Math.Cos(2 * Math.PI * hour / 24),
            Math.Log(1 + near / _tick),
        };
    }

    private static HoldModel? Fit(List<(LevelSample Level, bool Held)> rows, double[] prices, double median, double tick)
    {
        if (rows.Count == 0) return null;
        var probe = new HoldModel(new double[Features], new double[Features], Enumerable.Repeat(1.0, Features).ToArray(), median, prices, tick);
        var x = rows.Select(r => probe.Describe(r.Level)).ToList();
        var mean = new double[Features];
        var std = Enumerable.Repeat(1.0, Features).ToArray();
        for (int f = 1; f < Features; f++)
        {
            mean[f] = x.Average(v => v[f]);
            std[f] = Math.Max(Math.Sqrt(x.Average(v => (v[f] - mean[f]) * (v[f] - mean[f]))), 1e-9);
        }

        var z = x.Select(v => v.Select((value, f) => f == 0 ? 1.0 : (value - mean[f]) / std[f]).ToArray()).ToList();
        var w = new double[Features];
        for (int iter = 0; iter < 500; iter++)
        {
            var grad = new double[Features];
            for (int n = 0; n < z.Count; n++)
            {
                double p = Sigmoid(Dot(w, z[n]));
                double err = p - (rows[n].Held ? 1 : 0);
                for (int f = 0; f < Features; f++) grad[f] += err * z[n][f];
            }

            for (int f = 0; f < Features; f++) w[f] -= 0.3 * (grad[f] / z.Count + (f == 0 ? 0 : 0.01 * w[f]));
        }

        return new HoldModel(w, mean, std, median, prices, tick);
    }

    /// <summary>Probability (0 to 1) that the level holds once price comes back to it.</summary>
    public double Predict(LevelSample s)
    {
        var v = Describe(s);
        var z = new double[Features];
        z[0] = 1;
        for (int f = 1; f < Features; f++) z[f] = (v[f] - _mean[f]) / _std[f];
        return Sigmoid(Dot(_w, z));
    }

    private static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    private static double Sigmoid(double v) => 1 / (1 + Math.Exp(-Math.Clamp(v, -30, 30)));
}
