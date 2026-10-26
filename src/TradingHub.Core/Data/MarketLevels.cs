using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>A price the market is organised around (an options wall, a liquidation cluster): shown on the chart and in the Insights panel. <see cref="Tone"/>: +1 support-like (green), -1 resistance-like (red), 0 neutral.</summary>
public sealed record LevelMark(string Label, decimal Price, int Tone, string Source, string? Short = null, decimal Weight = 0)
{
    // Weight: how much money stands behind it (liquidation clusters), so alerts can ask for a minimum
    /// <summary>The few words written beside the line on the chart.</summary>
    public string ChartText => Short ?? Label;
}

/// <summary>
/// A shared board of levels filled by background services and read by the panel and the chart overlay (which cannot reach the services directly).
/// Keys are an underlying such as "BTC" (options) or an instrument key such as "bybit-linear:BTCUSDT" (liquidations).
/// </summary>
public static class MarketLevels
{
    private static readonly ConcurrentDictionary<(string Key, string Source), IReadOnlyList<LevelMark>> Board = new();

    public static void Set(string key, string source, IReadOnlyList<LevelMark> marks) => Board[(key, source)] = marks;

    /// <summary>The same by symbol alone (the chart indicators know the symbol, not the connection): liquidations of that symbol on any feed and the options of its underlying.</summary>
    public static IReadOnlyList<LevelMark> ForSymbol(string symbol)
    {
        var list = new List<LevelMark>();
        string? currency = OptionsLevelService.CurrencyOfSymbol(symbol);
        foreach (var kv in Board)
            if (kv.Key.Key.EndsWith(":" + symbol, StringComparison.Ordinal) || (currency != null && kv.Key.Key == currency)) list.AddRange(kv.Value);
        return list;
    }

    /// <summary>Everything known for an instrument: its own marks (liquidations) and those of its underlying (options).</summary>
    public static IReadOnlyList<LevelMark> For(Instrument inst)
    {
        var list = new List<LevelMark>();
        string? currency = OptionsLevelService.CurrencyOf(inst);
        foreach (var kv in Board)
            if (kv.Key.Key == inst.Key || (currency != null && kv.Key.Key == currency)) list.AddRange(kv.Value);
        return list;
    }

    public static void Clear() => Board.Clear();
}

/// <summary>One forced closing of a leveraged position (from the exchange's public liquidation stream).</summary>
public readonly record struct Liquidation(DateTime Time, long PriceTicks, decimal Size, bool LongLiquidated);

/// <summary>Turns the recent liquidations into the prices where most positions were forced out, one cluster of each kind.</summary>
public static class LiquidationClusters
{
    public static List<LevelMark> Marks(IReadOnlyList<Liquidation> liquidations, Instrument inst, DateTime now, TimeSpan window)
    {
        var marks = new List<LevelMark>();
        var recent = liquidations.Where(l => l.Time >= now - window).ToList();
        if (recent.Count == 0) return marks;
        decimal ref0 = inst.FromTicks(recent[^1].PriceTicks);
        long band = Math.Max(1, (long)Math.Round(ref0 * 0.0015m / inst.TickSize)); // clusters are 0.15% of the price wide
        foreach (bool longs in new[] { true, false })
        {
            // the densest price band: slide a window of one band's width over the liquidations in price order and keep the one holding most money
            var side = recent.Where(l => l.LongLiquidated == longs).OrderBy(l => l.PriceTicks).ToList();
            decimal bestMoney = 0, bestPrice = 0;
            int bestCount = 0;
            for (int i = 0; i < side.Count; i++)
            {
                decimal money = 0, weighted = 0, size = 0;
                int n = 0;
                for (int k = i; k < side.Count && side[k].PriceTicks - side[i].PriceTicks <= band; k++)
                {
                    decimal price = inst.FromTicks(side[k].PriceTicks);
                    money += price * side[k].Size * inst.PointValue;
                    weighted += price * side[k].Size;
                    size += side[k].Size;
                    n++;
                }

                if (money > bestMoney) (bestMoney, bestPrice, bestCount) = (money, weighted / Math.Max(size, 0.0000001m), n);
            }

            if (bestMoney <= 0) continue;
            marks.Add(new LevelMark($"{(longs ? "longs" : "shorts")} liquidated here {Money(bestMoney)} ({bestCount}×, {(int)window.TotalHours} h)", bestPrice, longs ? -1 : 1, "liquidations",
                $"{(longs ? "longs" : "shorts")} liq {Money(bestMoney)}", bestMoney));
        }

        return marks;
    }

    public static string Money(decimal v) => v >= 1_000_000 ? $"${v / 1_000_000:0.##}M" : v >= 1_000 ? $"${v / 1_000:0.#}K" : $"${v:0}";
}

/// <summary>One listed option.</summary>
public sealed record OptionQuote(decimal Strike, bool IsCall, DateTime Expiry, decimal OpenInterest, decimal MarkIv);

/// <summary>The prices options positioning points at, computed from the open interest of every listed option.</summary>
public sealed record OptionLevelSet(decimal Spot, decimal? CallWall, decimal? PutWall, decimal? MaxPain, DateTime? MaxPainExpiry, decimal? GammaFlip, int Options)
{
    private const double Day = 1.0 / 365;

    private static double Phi(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(2 * Math.PI);

    /// <summary>Black-Scholes gamma with no interest rate: how fast an option's delta changes per unit of price (the same for a call and a put).</summary>
    public static double Gamma(double spot, double strike, double sigma, double years)
    {
        if (spot <= 0 || strike <= 0 || sigma <= 0 || years <= 0) return 0;
        double sd = sigma * Math.Sqrt(years), d1 = (Math.Log(spot / strike) + 0.5 * sd * sd) / sd;
        return Phi(d1) / (spot * sd);
    }

    /// <summary>
    /// Call wall: the strike above the price (within 12%) with the most call open interest (where selling pressure is stacked). Put wall: the strike
    /// below (within 12%) with the most put open interest. Max pain: the strike at which the options of the biggest expiry within a month would pay out least.
    /// Gamma flip: the price at which the market's net dealer gamma changes sign, assuming dealers are long the calls and short the puts (the usual
    /// convention, not something measured): above it dealer hedging tends to damp moves, below it to feed them.
    /// </summary>
    public static OptionLevelSet Compute(IReadOnlyList<OptionQuote> quotes, decimal spot, DateTime now)
    {
        var near = quotes.Where(q => q.Expiry > now.AddHours(1) && q.Expiry <= now.AddDays(45) && q.OpenInterest > 0).ToList();
        decimal? CallWall() => near.Where(q => q.IsCall && q.Strike > spot && q.Strike <= spot * 1.12m).GroupBy(q => q.Strike).Select(g => (g.Key, Oi: g.Sum(q => q.OpenInterest))).OrderByDescending(x => x.Oi).Select(x => (decimal?)x.Key).FirstOrDefault();
        decimal? PutWall() => near.Where(q => !q.IsCall && q.Strike < spot && q.Strike >= spot * 0.88m).GroupBy(q => q.Strike).Select(g => (g.Key, Oi: g.Sum(q => q.OpenInterest))).OrderByDescending(x => x.Oi).Select(x => (decimal?)x.Key).FirstOrDefault();

        decimal? pain = null;
        DateTime? painExpiry = null;
        var main = near.Where(q => q.Expiry <= now.AddDays(30)).GroupBy(q => q.Expiry).OrderByDescending(g => g.Sum(q => q.OpenInterest)).FirstOrDefault();
        if (main != null)
        {
            var strikes = main.Select(q => q.Strike).Distinct().OrderBy(s => s).ToList();
            decimal bestPay = decimal.MaxValue;
            foreach (var s in strikes)
            {
                decimal pay = main.Sum(q => q.IsCall ? Math.Max(0, s - q.Strike) * q.OpenInterest : Math.Max(0, q.Strike - s) * q.OpenInterest);
                if (pay < bestPay) { bestPay = pay; pain = s; }
            }

            painExpiry = main.Key;
        }

        // net dealer gamma if the price were somewhere else: sum over the options of sign x open interest x gamma x price^2
        double Net(double s)
        {
            double total = 0;
            foreach (var q in near)
            {
                double years = (q.Expiry - now).TotalDays * Day;
                total += (q.IsCall ? 1 : -1) * (double)q.OpenInterest * Gamma(s, (double)q.Strike, (double)q.MarkIv / 100.0, years) * s * s;
            }

            return total;
        }

        decimal? flip = null;
        if (near.Count > 0 && spot > 0)
        {
            double best = double.MaxValue, prevS = 0, prevN = 0;
            for (int i = 0; i <= 60; i++)
            {
                double s = (double)spot * (0.85 + 0.005 * i), n = Net(s);
                if (i > 0 && Math.Sign(n) != Math.Sign(prevN) && prevN != 0 && n != 0)
                {
                    double cross = prevS + (s - prevS) * (0 - prevN) / (n - prevN); // where the line between the two points crosses zero
                    if (Math.Abs(cross - (double)spot) < best) { best = Math.Abs(cross - (double)spot); flip = (decimal)cross; }
                }

                prevS = s;
                prevN = n;
            }
        }

        return new OptionLevelSet(spot, CallWall(), PutWall(), pain, painExpiry, flip, near.Count);
    }

    public List<LevelMark> Marks(string underlying)
    {
        var marks = new List<LevelMark>();
        if (CallWall is { } c) marks.Add(new LevelMark($"call wall (most {underlying} call open interest above the price)", c, -1, "options", "call wall"));
        if (PutWall is { } p) marks.Add(new LevelMark($"put wall (most {underlying} put open interest below the price)", p, 1, "options", "put wall"));
        if (MaxPain is { } m) marks.Add(new LevelMark($"max pain ({MaxPainExpiry:d MMM})", m, 0, "options", "max pain"));
        if (GammaFlip is { } g) marks.Add(new LevelMark("gamma flip (dealer hedging flips from damping to feeding moves)", g, 0, "options", "gamma flip"));
        return marks;
    }
}

/// <summary>
/// Options levels from Deribit's free public data (BTC and ETH options): one request lists every option with its open interest and implied volatility.
/// It is refreshed at most every five minutes, in the background, and the result goes on the <see cref="MarketLevels"/> board.
/// </summary>
public static class OptionsLevelService
{
    /// <summary>Tests point this at a fake server.</summary>
    public static string BaseUrl { get; set; } = "https://www.deribit.com/api/v2";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };
    private static readonly ConcurrentDictionary<string, DateTime> Asked = new();
    private static readonly ConcurrentDictionary<string, OptionLevelSet> Latest = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<OptionQuote>> Quotes = new();

    /// <summary>The listed options behind the latest levels of an underlying (for the gamma view), or null before the first refresh.</summary>
    public static IReadOnlyList<OptionQuote>? QuotesFor(string currency) => Quotes.TryGetValue(currency, out var v) ? v : null;

    /// <summary>The underlying the options of an instrument are listed on ("BTC", "ETH"), or null if it has none.</summary>
    public static string? CurrencyOf(Instrument inst) => CurrencyOfSymbol(inst.BaseAsset.Length > 0 ? inst.BaseAsset : inst.Symbol);

    public static string? CurrencyOfSymbol(string symbol)
    {
        string s = symbol.ToUpperInvariant();
        if (s.StartsWith("XBT") || s.StartsWith("BTC")) return "BTC";
        if (s.StartsWith("ETH")) return "ETH";
        return null;
    }

    public static OptionLevelSet? LatestFor(string currency) => Latest.TryGetValue(currency, out var v) ? v : null;

    /// <summary>Starts a background refresh unless one ran in the last five minutes.</summary>
    public static void Request(string currency)
    {
        var now = DateTime.UtcNow;
        if (Asked.TryGetValue(currency, out var last) && now - last < TimeSpan.FromMinutes(5)) return;
        Asked[currency] = now;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(currency, CancellationToken.None); }
            catch (Exception) { Asked[currency] = DateTime.UtcNow.AddMinutes(-4); } // try again in a minute
        });
    }

    public static async Task<OptionLevelSet?> RefreshAsync(string currency, CancellationToken ct)
    {
        string json = await Http.GetStringAsync($"{BaseUrl}/public/get_book_summary_by_currency?currency={currency}&kind=option", ct).ConfigureAwait(false);
        var (quotes, spot) = Parse(json);
        if (quotes.Count == 0 || spot <= 0) return null;
        var set = OptionLevelSet.Compute(quotes, spot, DateTime.UtcNow);
        Latest[currency] = set;
        Quotes[currency] = quotes;
        MarketLevels.Set(currency, "options", set.Marks(currency));
        return set;
    }

    /// <summary>Deribit's book summary: instrument names like BTC-27NOV26-73000-P; the index price is each option's estimated delivery price.</summary>
    public static (List<OptionQuote> Quotes, decimal Spot) Parse(string json)
    {
        var quotes = new List<OptionQuote>();
        var spots = new List<decimal>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array) return (quotes, 0);
        foreach (var r in result.EnumerateArray())
        {
            var parts = (r.TryGetProperty("instrument_name", out var n) ? n.GetString() : null)?.Split('-');
            if (parts is not { Length: 4 }) continue;
            if (!DateTime.TryParseExact(parts[1], "dMMMyy", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)) continue;
            if (!decimal.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var strike)) continue;
            decimal Num(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0;
            quotes.Add(new OptionQuote(strike, parts[3] == "C", date.Date.AddHours(8), Num("open_interest"), Num("mark_iv")));
            if (Num("estimated_delivery_price") > 0) spots.Add(Num("estimated_delivery_price"));
        }

        spots.Sort();
        return (quotes, spots.Count == 0 ? 0 : spots[spots.Count / 2]);
    }
}
