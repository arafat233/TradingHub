using System.Globalization;
using System.Text.RegularExpressions;
using TradingHub.Data;
using TradingHub.Feeds.Rithmic;

namespace TradingHub.App.Services;

/// <summary>Realistic costs for testing a market.</summary>
public sealed record MarketCosts(decimal CommissionPercent, decimal CommissionPerUnit, int SlippageTicks, string? TimeZoneId, double SessionStartHours);

/// <summary>What each market is, how much it costs to trade it and where it runs live.</summary>
public static class MarketInfo
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Imported CFD -> CME future with the same underlying (to run it live).</summary>
    private static readonly Dictionary<string, string> CfdFutures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NAS100"] = "NQ", ["US100"] = "NQ", ["USTEC"] = "NQ", ["USTECH"] = "NQ", ["NDX"] = "NQ", ["NASDAQ"] = "NQ", ["USATECHIDXUSD"] = "NQ",
        ["US500"] = "ES", ["SPX500"] = "ES", ["SP500"] = "ES", ["USA500"] = "ES", ["SPX"] = "ES", ["USA500IDXUSD"] = "ES",
        ["US30"] = "YM", ["DJ30"] = "YM", ["WS30"] = "YM", ["USA30IDXUSD"] = "YM",
        ["US2000"] = "RTY", ["RUSSELL2000"] = "RTY",
        ["XAUUSD"] = "GC", ["GOLD"] = "GC",
        ["WTI"] = "CL", ["USOIL"] = "CL", ["XTIUSD"] = "CL", ["LIGHTCMDUSD"] = "CL",
    };

    /// <summary>Big future -> its micro (the same price with a tenth of the value).</summary>
    private static readonly Dictionary<string, string> Micros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NQ"] = "MNQ", ["ES"] = "MES", ["YM"] = "MYM", ["RTY"] = "M2K", ["GC"] = "MGC", ["CL"] = "MCL",
        ["SI"] = "SIL", ["HG"] = "MHG", ["6E"] = "M6E", ["6B"] = "M6B", ["6A"] = "M6A", ["BTC"] = "MBT", ["ETH"] = "MET",
    };

    public static (string Feed, string Symbol) Split(string market)
    {
        int i = market.IndexOf(':');
        return i < 0 ? ("", market.Trim().ToUpperInvariant()) : (market[..i].Trim().ToLowerInvariant(), market[(i + 1)..].Trim().ToUpperInvariant());
    }

    public static string Key(string feed, string symbol) => $"{feed}:{symbol}";

    /// <summary>Normalizes what an agent writes ("btcusdt", "Binance-Futures:BTCUSDT") against a list of markets.</summary>
    public static string? Match(string written, IEnumerable<string> markets)
    {
        if (string.IsNullOrWhiteSpace(written)) return null;
        var list = markets.ToList();
        var exact = list.FirstOrDefault(m => m.Equals(written.Trim(), StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var sym = Split(written).Symbol;
        return list.FirstOrDefault(m => Split(m).Symbol.Equals(sym, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsCrypto(string feed) => feed is "binance-futures" or "binance-spot" or "bybit-linear" or "kraken-spot" or "coinbase-spot" or "bitfinex";

    /// <summary>
    /// CME futures (Rithmic or Tradovate: same contracts, same continuous history; also Databento's history and
    /// the one from a single connection, see <see cref="HistorySources"/>).
    /// </summary>
    public static bool IsFutures(string feed) => HistorySources.Folder(feed) is RithmicFeed.FeedId or TradingHub.Feeds.Tradovate.TradovateFeed.FeedId or HistorySources.Databento or TradingHub.Feeds.DxFeed.DxFeedFeed.FeedId;

    /// <summary>CFDs (imported files, OANDA or a cTrader broker): with no real trades, the spread goes in as slippage.</summary>
    public static bool IsCfd(string feed) => feed == CsvImport.FeedId || feed == TradingHub.Feeds.Oanda.OandaFeed.FeedId || feed.StartsWith(TradingHub.Feeds.CTrader.CTraderFeed.FeedId, StringComparison.Ordinal);

    public static bool IsStocks(string feed) => feed == TradingHub.Feeds.Alpaca.AlpacaFeed.FeedId;

    /// <summary>Root of a CFD symbol to look up its future (NAS100_USD -> NAS100, US100.cash -> US100).</summary>
    private static string CfdRoot(string symbol) => symbol.Split('_', '.', '-', ' ')[0].ToUpperInvariant();

    /// <summary>Description for the agents.</summary>
    public static string Describe(string market)
    {
        var (feed, sym) = Split(market);
        if (feed is "kraken-spot" or "coinbase-spot" or "bitfinex") return (feed == "kraken-spot" ? "Kraken" : feed == "bitfinex" ? "Bitfinex" : "Coinbase") + " spot crypto, 24/7";
        if (IsCrypto(feed)) return feed == "binance-spot" ? "Binance spot crypto, 24/7" : feed == "bybit-linear" ? "Bybit perpetual future, 24/7" : "Binance perpetual future, 24/7";
        if (IsFutures(feed)) return (TradingHub.Market.FuturesCatalog.Find(sym) is { } p ? $"{p.Name} future ({p.Exchange}, Globex hours)" : "CME Group future") + FuturesSource(feed);
        if (feed == CsvImport.FeedId) return "CFD imported from files (1 minute candles converted into trades: no real volume or aggressor side, not usable for order flow)";
        if (feed == TradingHub.Feeds.Oanda.OandaFeed.FeedId) return "OANDA CFD/forex (5 second candles converted into trades: no real volume or aggressor side, not usable for order flow)";
        if (feed.StartsWith(TradingHub.Feeds.CTrader.CTraderFeed.FeedId, StringComparison.Ordinal)) return "CFD/forex from a cTrader broker (bid ticks: no real volume or aggressor side, not usable for order flow)";
        if (IsStocks(feed)) return "US stock/ETF via Alpaca (real trades from every exchange, side by tick rule; 9:30-16:00 New York hours plus pre and post)";
        return feed;
    }

    private static string FuturesSource(string feed) => feed switch
    {
        RithmicFeed.FeedId => "",
        HistorySources.Databento => " (Databento history)",
        _ when feed.EndsWith(HistorySources.OnlySuffix, StringComparison.Ordinal) => $" (only the history from {(HistorySources.Folder(feed) == RithmicFeed.FeedId ? "Rithmic" : "Tradovate")})",
        _ => " through Tradovate",
    };

    /// <summary>
    /// The spec of a future with stored history without depending on its connection being open (Databento gives no live
    /// data): tick, point value and exchange from the CME catalog.
    /// </summary>
    public static TradingHub.Market.Instrument FuturesInstrument(string feed, string symbol) => new()
    {
        Symbol = symbol,
        FeedId = feed,
        Exchange = TradingHub.Market.FuturesCatalog.Find(symbol)?.Exchange ?? "CME",
        TickSize = RithmicFeed.SpecFor(symbol)?.Tick ?? HistorySources.StoredTickSize(AppPaths.Ticks, feed, symbol) ?? 0.25m,
        PointValue = PointValue(feed, symbol),
        VolumeStep = 1,
    };

    public static decimal PointValue(string feed, string symbol) =>
        IsFutures(feed) ? RithmicFeed.SpecFor(symbol)?.PointValue ?? 1 : 1;

    /// <summary>Market volume step (whole contracts on futures, whole shares).</summary>
    public static decimal VolumeStep(string feed, string symbol) => feed switch
    {
        _ when IsFutures(feed) || IsStocks(feed) || feed == TradingHub.Feeds.Oanda.OandaFeed.FeedId => 1,
        _ when IsCrypto(feed) => symbol.StartsWith("BTC", StringComparison.OrdinalIgnoreCase) || symbol.StartsWith("ETH", StringComparison.OrdinalIgnoreCase) ? 0.001m : 0.1m,
        _ => 0.01m,
    };

    /// <summary>Tick stored in the files (or the one from the future's specification).</summary>
    public static decimal TickSize(string feed, string symbol)
    {
        if (IsFutures(feed) && RithmicFeed.SpecFor(symbol) is { } s) return s.Tick;
        return HistorySources.StoredTickSize(AppPaths.Ticks, feed, symbol) ?? 0.01m;
    }

    /// <summary>Commission per side the trader confirmed for a futures root (null = the estimated one).</summary>
    public static Func<string, decimal?> UserCommission { get; set; } = _ => null;

    /// <summary>Whether it is the root of a known future (NQ, MNQ, ES, CL…).</summary>
    public static bool IsFuturesRoot(string root) => RithmicFeed.SpecFor(root.ToUpperInvariant()) != null;

    /// <summary>CME micro (MNQ, MES, MYM, M2K, MCL, MGC…).</summary>
    public static bool IsMicro(string root)
    {
        root = root.ToUpperInvariant();
        return root.Length > 2 && root[0] == 'M' && (root == "M2K" || RithmicFeed.SpecFor(root[1..]) != null);
    }

    /// <summary>Estimated commission per side, all in, until the trader confirms their own: 2.5 on the big ones and 0.75 on the micros.</summary>
    public static decimal EstimatedCommission(string root) => IsMicro(root) ? 0.75m : 2.5m;

    /// <summary>Commission per side of a future and whether it is the one the trader confirmed.</summary>
    public static (decimal PerSide, bool Confirmed) Commission(string root) =>
        UserCommission(root.ToUpperInvariant()) is { } v ? (v, true) : (EstimatedCommission(root), false);

    /// <summary>
    /// Futures roots written in capitals in a text ("History of NQ and MES" -> MES, NQ). Capitals only: "es" or
    /// "si" in a sentence are not the ES or silver.
    /// </summary>
    public static List<string> RootsIn(string text) =>
        Regex.Matches(text, @"(?<![A-Za-z0-9])[A-Z0-9]{2,4}(?![A-Za-z0-9])").Select(m => m.Value)
            .Where(t => t.Any(char.IsLetter) && RithmicFeed.SpecFor(t) != null).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();

    /// <summary>Costs for testing: realistic commission and slippage, and each market's session.</summary>
    public static MarketCosts Costs(string feed, string symbol)
    {
        if (IsCrypto(feed)) return new MarketCosts(0.04m, 0, 1, null, 0);
        if (IsFutures(feed)) return new MarketCosts(0, Commission(symbol).PerSide, 1, "America/New_York", 18);
        // stocks: no commission on Alpaca, a cent of slippage and the New York session from the pre-market
        if (IsStocks(feed)) return new MarketCosts(0, 0, 1, "America/New_York", 4);
        // CFD: the spread is paid as slippage (around a point on indices); the US index ones with the
        // New York session, same as their future (so a time-of-day strategy runs the same on the simulated account)
        decimal tick = TickSize(feed, symbol);
        int slip = tick > 0 ? (int)Math.Clamp(Math.Ceiling(1m / tick), 1, 200) : 1;
        return FutureForCfd(CfdRoot(symbol)) is "NQ" or "ES" or "YM" or "RTY" ? new MarketCosts(0, 0, slip, "America/New_York", 18) : new MarketCosts(0, 0, slip, null, 0);
    }

    public static string CostsText(string feed, string symbol)
    {
        var c = Costs(feed, symbol);
        var parts = new List<string>
        {
            $"commission_percent {c.CommissionPercent.ToString(Inv)}",
        };
        if (c.CommissionPerUnit > 0)
            parts.Add($"commission_per_unit {c.CommissionPerUnit.ToString(Inv)}" +
                      (!IsFutures(feed) ? "" : Commission(symbol).Confirmed ? " (the real one: the trader confirmed it)" : " (estimated: the trader has not confirmed it yet; do not ask them)"));
        parts.Add($"slippage_ticks {c.SlippageTicks}");
        parts.Add(c.TimeZoneId != null ? $"timezone {c.TimeZoneId} · session_start_hour {c.SessionStartHours.ToString(Inv)}" : "timezone UTC");
        return string.Join(" · ", parts);
    }

    /// <summary>CME future to run an imported CFD live (null if there is no equivalent).</summary>
    public static string? FutureForCfd(string symbol) => CfdFutures.TryGetValue(symbol, out var f) || CfdFutures.TryGetValue(CfdRoot(symbol), out f) ? f : null;

    public static string? MicroOf(string root) => Micros.TryGetValue(root, out var m) ? m : null;

    /// <summary>The most common CFD of a future (NQ -> NAS100, ES -> US500), to get free history.</summary>
    public static string? CfdFor(string futureRoot) => CfdFutures.FirstOrDefault(kv => kv.Value.Equals(futureRoot, StringComparison.OrdinalIgnoreCase)).Key;

    /// <summary>Underlying of a market: the mini and the micro of the same future give the same thing (rithmic:MNQ -> rithmic:NQ).</summary>
    public static string Root(string market)
    {
        var (feed, symbol) = Split(market);
        return IsFutures(feed) ? Key(feed, MiniOf(symbol)) : market.ToLowerInvariant();
    }

    public static bool SameRoot(string a, string b) => Root(a).Equals(Root(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Like <see cref="Match"/>, but if the micro was asked for and the mini is in the list (or the other way round) it finds it too:
    /// they are the same price (to pick where to validate, not to read what an agent wrote).
    /// </summary>
    public static string? MatchUnderlying(string written, IEnumerable<string> markets)
    {
        var list = markets.ToList();
        if (Match(written, list) is { } m) return m;
        var (feed, symbol) = Split(written);
        string mini = MiniOf(symbol);
        return list.FirstOrDefault(x => Split(x) is var (f2, s2) && IsFutures(f2) && (feed.Length == 0 || IsFutures(feed)) && MiniOf(s2).Equals(mini, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The mini of a micro (MNQ -> NQ) or the same symbol.</summary>
    public static string MiniOf(string root) => Micros.FirstOrDefault(kv => kv.Value.Equals(root, StringComparison.OrdinalIgnoreCase)).Key ?? root;

    /// <summary>
    /// Markets tracking the same underlying, out of <paramref name="available"/> (the ones with history or that can be
    /// downloaded): the mini and the micro of the same future, the imported CFDs of that index (NQ <-> NAS100) and the same crypto on
    /// other exchanges. They are useful for gathering sample when a market has little history.
    /// </summary>
    public static List<string> Equivalents(string market, IEnumerable<string> available)
    {
        var (feed, symbol) = Split(market);
        var list = new List<string>();
        foreach (var other in available.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (other.Equals(market, StringComparison.OrdinalIgnoreCase)) continue;
            var (f2, s2) = Split(other);
            bool same;
            if (IsCrypto(feed)) same = IsCrypto(f2) && s2.Equals(symbol, StringComparison.OrdinalIgnoreCase);
            else
            {
                // root future of each side: the contract's (without the micro) or the one the CFD tracks
                string? root = IsCfd(feed) ? FutureForCfd(symbol) : MiniOf(symbol);
                string? root2 = IsCfd(f2) ? FutureForCfd(s2) : IsFutures(f2) ? MiniOf(s2) : null;
                same = root != null && root2 != null && root.Equals(root2, StringComparison.OrdinalIgnoreCase);
            }

            if (same) list.Add(other);
        }

        return list;
    }

    /// <summary>Commission per side on a simulated account (rate on the notional and a fixed one per unit).</summary>
    public static (decimal Rate, decimal PerUnit) PaperCommission(string feed, string symbol) =>
        IsCrypto(feed) ? (0.0004m, 0) : IsFutures(feed) ? (0, Commission(symbol).PerSide) : (0, 0);
}
