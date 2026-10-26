using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>One strike of the options profile: open interest of calls and puts (in contracts of the underlying) and the net dealer gamma exposure there.</summary>
public sealed record StrikeRow(decimal Strike, decimal CallOi, decimal PutOi, double Gex);

/// <summary>
/// Open interest by strike, calls against puts, with the dealer gamma exposure (GEX) of each strike: sign x open interest x gamma x price², the sign
/// being + for calls and − for puts (dealers assumed long the calls and short the puts: a convention, not something measured). Only options up to 45 days
/// out and strikes within 20% of the price are used, the ones that move hedging now.
/// </summary>
public static class OptionsProfile
{
    public static List<StrikeRow> Build(IReadOnlyList<OptionQuote> quotes, decimal spot, DateTime now)
    {
        var rows = new List<StrikeRow>();
        if (spot <= 0) return rows;
        var near = quotes.Where(q => q.Expiry > now.AddHours(1) && q.Expiry <= now.AddDays(45) && q.OpenInterest > 0 && Math.Abs(q.Strike - spot) <= spot * 0.2m);
        foreach (var g in near.GroupBy(q => q.Strike).OrderBy(g => g.Key))
        {
            decimal calls = g.Where(q => q.IsCall).Sum(q => q.OpenInterest), puts = g.Where(q => !q.IsCall).Sum(q => q.OpenInterest);
            double gex = 0;
            foreach (var q in g)
                gex += (q.IsCall ? 1 : -1) * (double)q.OpenInterest * OptionLevelSet.Gamma((double)spot, (double)q.Strike, (double)q.MarkIv / 100.0, (q.Expiry - now).TotalDays / 365) * (double)spot * (double)spot;
            rows.Add(new StrikeRow(g.Key, calls, puts, gex));
        }

        return rows;
    }
}

/// <summary>The user's own price levels, kept per instrument and put on the shared board so the chart lines and the panel show them and a touch can alert.</summary>
public static class MyLevels
{
    public const string Source = "mine";

    public static void Publish(string instrumentKey, IEnumerable<decimal> prices) =>
        MarketLevels.Set(instrumentKey, Source, prices.Distinct().Select(p => new LevelMark("my level", p, 0, Source, "mine")).ToList());
}
