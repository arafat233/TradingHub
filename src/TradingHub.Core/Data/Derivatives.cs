using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Where big resting orders keep being cancelled instead of traded.</summary>
public static class PullStats
{
    /// <summary>The price where walls were pulled (or flashed) most often in the window, with the count; null when no price was hit twice.</summary>
    public static (long Ticks, bool Ask, int Count)? Hotspot(IEnumerable<BookEvent> events, DateTime now, TimeSpan window)
    {
        var best = events
            .Where(e => e.Time >= now - window && e.Time <= now && e.Kind is BookEventKind.Pulled or BookEventKind.Flash)
            .GroupBy(e => (e.Ticks, e.Ask))
            .Select(g => (g.Key.Ticks, g.Key.Ask, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .FirstOrDefault();
        return best.Count >= 2 ? best : null;
    }
}

/// <summary>
/// Estimated liquidation prices, from where the biggest orders of the last hours were placed. A big buy at price p is taken as a long at 10x, 25x,
/// 50x and 100x: it is liquidated near p × (1 − 1/L + maintenance margin). A position whose liquidation price has already traded through is gone.
/// It is a guess about leverage nobody can see: the label says "est.".
/// </summary>
public static class LiquidationProjection
{
    public static readonly int[] Leverages = { 10, 25, 50, 100 };
    private const decimal Maintenance = 0.005m;

    public static List<LevelMark> Marks(TradeStore store, Instrument inst, DateTime now, TimeSpan window)
    {
        var marks = new List<LevelMark>();
        int i0 = store.LowerBound(now - window), i1 = store.Count;
        if (i1 - i0 < 1000) return marks;
        // "big" = top 1% by size, from a sample so a busy day stays cheap
        int stride = Math.Max(1, (i1 - i0) / 20000);
        var sample = new List<decimal>();
        long lo = long.MaxValue, hi = long.MinValue;
        for (int i = i0; i < i1; i++)
        {
            long p = store.GetPriceTicks(i);
            lo = Math.Min(lo, p);
            hi = Math.Max(hi, p);
            if ((i - i0) % stride == 0) sample.Add(store.GetVolume(i));
        }

        sample.Sort();
        decimal big = sample[Math.Min(sample.Count - 1, (int)(sample.Count * 0.99))];
        decimal last = store.GetPrice(i1 - 1), low = inst.FromTicks(lo), high = inst.FromTicks(hi);
        var longs = new List<(decimal Price, decimal Money)>();
        var shorts = new List<(decimal Price, decimal Money)>();
        for (int i = i0; i < i1; i++)
        {
            decimal v = store.GetVolume(i);
            if (v < big) continue;
            decimal p = store.GetPrice(i), money = p * v * inst.PointValue;
            bool buy = store.GetSide(i) == TradeSide.Buy;
            foreach (int l in Leverages)
            {
                decimal liq = buy ? p * (1 - 1m / l + Maintenance) : p * (1 + 1m / l - Maintenance);
                // still alive: the market has not traded through it since
                if (buy && liq < last && liq < low) longs.Add((liq, money / Leverages.Length));
                else if (!buy && liq > last && liq > high) shorts.Add((liq, money / Leverages.Length));
            }
        }

        decimal band = last * 0.0015m;
        if (Densest(longs, band) is { } l1) marks.Add(new LevelMark($"longs liquidate ~{l1.Price:0.####} (est. {LiquidationClusters.Money(l1.Money)})", l1.Price, -1, "liq-estimate", "longs liquidate (est.)"));
        if (Densest(shorts, band) is { } s1) marks.Add(new LevelMark($"shorts liquidate ~{s1.Price:0.####} (est. {LiquidationClusters.Money(s1.Money)})", s1.Price, 1, "liq-estimate", "shorts liquidate (est.)"));
        return marks;
    }

    /// <summary>The price band holding the most money.</summary>
    internal static (decimal Price, decimal Money)? Densest(List<(decimal Price, decimal Money)> pts, decimal band)
    {
        if (pts.Count == 0 || band <= 0) return null;
        pts.Sort((a, b) => a.Price.CompareTo(b.Price));
        decimal best = 0, bestPrice = 0;
        int j = 0;
        decimal money = 0, weighted = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            money += pts[i].Money;
            weighted += pts[i].Money * pts[i].Price;
            while (pts[i].Price - pts[j].Price > band)
            {
                money -= pts[j].Money;
                weighted -= pts[j].Money * pts[j].Price;
                j++;
            }

            if (money > best) (best, bestPrice) = (money, weighted / money);
        }

        return best > 0 ? (bestPrice, best) : null;
    }
}

/// <summary>Funding, open interest, basis, long/short ratio and the fear and greed index for a crypto perpetual.</summary>
public sealed record DerivativesStats(
    string Symbol, decimal Funding, DateTime NextFunding, decimal OpenInterestValue, decimal? OiChange24hPct, decimal BasisPct,
    decimal? LongShortBuyShare, int? FearGreed, string? FearGreedLabel, DateTime At)
{
    public string Text()
    {
        var lines = new List<string>
        {
            $"Funding {Funding * 100:+0.0000;-0.0000}% per 8 h" + (Funding > 0.0003m ? " (longs paying a lot)" : Funding < -0.0003m ? " (shorts paying a lot)" : "") + $" · next in {Math.Max(0, (NextFunding - DateTime.UtcNow).TotalMinutes):0} min",
            $"Open interest {LiquidationClusters.Money(OpenInterestValue)}" + (OiChange24hPct is { } c ? $" ({c:+0.0;-0.0}% in 24 h)" : ""),
            $"Basis (last vs index) {BasisPct:+0.000;-0.000}%",
        };
        if (LongShortBuyShare is { } r) lines.Add($"Accounts long vs short: {r:P0} long / {1 - r:P0} short");
        if (FearGreed is { } f) lines.Add($"Fear and greed {f} ({FearGreedLabel})");
        return string.Join("\n", lines);
    }
}

/// <summary>Free public Bybit data (and alternative.me's fear and greed index), refreshed in the background at most once a minute per symbol.</summary>
public static class DerivativesService
{
    /// <summary>Tests point these at a fake server.</summary>
    public static string BybitUrl { get; set; } = "https://api.bybit.com/v5/market";
    public static string FearGreedUrl { get; set; } = "https://api.alternative.me/fng/?limit=1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly ConcurrentDictionary<string, DateTime> Asked = new();
    private static readonly ConcurrentDictionary<string, DerivativesStats> Latest = new();

    /// <summary>The Bybit linear perpetual that matches a crypto instrument ("BTCUSDT"), or null for other markets.</summary>
    public static string? SymbolOf(Instrument inst)
    {
        string s = (inst.BaseAsset.Length > 0 ? inst.BaseAsset : inst.Symbol).ToUpperInvariant();
        if (s.StartsWith("XBT")) s = "BTC" + s[3..];
        string? cur = OptionsLevelService.CurrencyOfSymbol(s);
        if (cur != null) return cur + "USDT";
        if (s.EndsWith("USDT") && s.Length > 4 && s.All(char.IsLetterOrDigit)) return s;
        return null;
    }

    public static DerivativesStats? LatestFor(string symbol) => Latest.TryGetValue(symbol, out var v) ? v : null;

    public static void Request(string symbol)
    {
        var now = DateTime.UtcNow;
        if (Asked.TryGetValue(symbol, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
        Asked[symbol] = now;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(symbol, CancellationToken.None); }
            catch (Exception) { /* offline or blocked: try again in a minute */ }
        });
    }

    public static async Task<DerivativesStats?> RefreshAsync(string symbol, CancellationToken ct)
    {
        string tickers = await Http.GetStringAsync($"{BybitUrl}/tickers?category=linear&symbol={symbol}", ct).ConfigureAwait(false);
        var t = ParseTicker(tickers);
        if (t == null) return null;
        // the rest are extras: a failure leaves the field empty instead of dropping the whole panel
        async Task<string?> Try(string url)
        {
            try { return await Http.GetStringAsync(url, ct).ConfigureAwait(false); } catch (Exception) { return null; }
        }

        var oi = Try($"{BybitUrl}/open-interest?category=linear&symbol={symbol}&intervalTime=1h&limit=25");
        var ratio = Try($"{BybitUrl}/account-ratio?category=linear&symbol={symbol}&period=1h&limit=1");
        var fng = Try(FearGreedUrl);
        var (fg, label) = ParseFearGreed(await fng.ConfigureAwait(false));
        var stats = t with
        {
            OiChange24hPct = ParseOiChange(await oi.ConfigureAwait(false)),
            LongShortBuyShare = ParseBuyRatio(await ratio.ConfigureAwait(false)),
            FearGreed = fg,
            FearGreedLabel = label,
        };
        Latest[symbol] = stats;
        return stats;
    }

    private static decimal Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && decimal.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static JsonElement? FirstOfList(string json, string array = "list")
    {
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("result", out var r) && r.TryGetProperty(array, out var l) && l.ValueKind == JsonValueKind.Array && l.GetArrayLength() > 0)
            return l[0].Clone();
        return null;
    }

    public static DerivativesStats? ParseTicker(string json)
    {
        if (FirstOfList(json) is not { } e) return null;
        decimal last = Num(e, "lastPrice"), index = Num(e, "indexPrice");
        if (last <= 0) return null;
        long next = (long)Num(e, "nextFundingTime");
        return new DerivativesStats(e.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "", Num(e, "fundingRate"),
            next > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(next).UtcDateTime : DateTime.UtcNow, Num(e, "openInterestValue"), null,
            index > 0 ? (last - index) / index * 100 : 0, null, null, null, DateTime.UtcNow);
    }

    /// <summary>Bybit lists open interest newest first: the change is newest against the oldest in the list.</summary>
    public static decimal? ParseOiChange(string? json)
    {
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var list = doc.RootElement.GetProperty("result").GetProperty("list");
            int n = list.GetArrayLength();
            if (n < 2) return null;
            decimal newest = Num(list[0], "openInterest"), oldest = Num(list[n - 1], "openInterest");
            return oldest > 0 ? (newest - oldest) / oldest * 100 : null;
        }
        catch (Exception) { return null; }
    }

    public static decimal? ParseBuyRatio(string? json)
    {
        if (json == null) return null;
        try { return FirstOfList(json) is { } e && Num(e, "buyRatio") is > 0 and var b ? b : null; } catch (Exception) { return null; }
    }

    public static (int?, string?) ParseFearGreed(string? json)
    {
        if (json == null) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var d = doc.RootElement.GetProperty("data")[0];
            return (int.Parse(d.GetProperty("value").GetString()!, CultureInfo.InvariantCulture), d.GetProperty("value_classification").GetString());
        }
        catch (Exception) { return (null, null); }
    }
}
