namespace TradingHub.Data;

/// <summary>
/// Finds the past sessions that look most like a given one. Each session is a vector of features known at the same moment (for example at the
/// end of the first hour); the features are standardised so no single one dominates, then ranked by weighted Euclidean distance.
/// Pure math: no market knowledge lives here.
/// </summary>
public static class SessionMatcher
{
    public sealed record Match(int Index, double Distance);

    /// <summary>
    /// The <paramref name="k"/> closest sessions to <paramref name="target"/> among <paramref name="pool"/> (the target itself and any
    /// session after it are never in the pool, so a match never uses the future).
    /// </summary>
    /// <param name="features">One row per session, all rows the same length.</param>
    /// <param name="weights">Per feature; a feature with weight 0 is ignored. Null = all 1.</param>
    public static List<Match> Nearest(IReadOnlyList<double[]> features, int target, int k, double[]? weights = null)
    {
        int n = target + 1; // only the target and what came before it: the scale of the features must not come from the future
        if (target < 1 || target >= features.Count) return new List<Match>();
        int dim = features[0].Length;
        var mean = new double[dim];
        var sd = new double[dim];
        for (int j = 0; j < dim; j++)
        {
            for (int i = 0; i < n; i++) mean[j] += features[i][j];
            mean[j] /= n;
            for (int i = 0; i < n; i++) sd[j] += (features[i][j] - mean[j]) * (features[i][j] - mean[j]);
            sd[j] = Math.Sqrt(sd[j] / n);
            if (sd[j] < 1e-12) sd[j] = 1; // a constant feature says nothing: it adds zero distance
        }

        var res = new List<Match>();
        for (int i = 0; i < target; i++)
        {
            double d = 0;
            for (int j = 0; j < dim; j++)
            {
                double diff = (features[i][j] - features[target][j]) / sd[j];
                d += (weights?[j] ?? 1) * diff * diff;
            }

            res.Add(new Match(i, Math.Sqrt(d)));
        }

        return res.OrderBy(m => m.Distance).Take(k).ToList();
    }
}
