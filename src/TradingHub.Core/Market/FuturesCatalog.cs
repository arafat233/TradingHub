namespace TradingHub.Market;

/// <summary>A CME Group future: root, exchange (as Rithmic asks for it), tick, point value, name and sector.</summary>
public sealed record FuturesProduct(string Root, string Exchange, decimal Tick, decimal PointValue, string Name, string Sector);

/// <summary>
/// The CME Group futures traded through Rithmic and Tradovate: indices, energy, metals, currencies, rates, grains,
/// livestock and crypto. Prices as the exchange sends them (grains in cents per bushel).
/// </summary>
public static class FuturesCatalog
{
    public static IReadOnlyList<FuturesProduct> All { get; } = new FuturesProduct[]
    {
        // indices
        new("ES", "CME", 0.25m, 50m, "E-mini S&P 500", "Indices"),
        new("MES", "CME", 0.25m, 5m, "Micro E-mini S&P 500", "Indices"),
        new("NQ", "CME", 0.25m, 20m, "E-mini Nasdaq-100", "Indices"),
        new("MNQ", "CME", 0.25m, 2m, "Micro E-mini Nasdaq-100", "Indices"),
        new("YM", "CBOT", 1m, 5m, "E-mini Dow", "Indices"),
        new("MYM", "CBOT", 1m, 0.5m, "Micro E-mini Dow", "Indices"),
        new("RTY", "CME", 0.1m, 50m, "E-mini Russell 2000", "Indices"),
        new("M2K", "CME", 0.1m, 5m, "Micro E-mini Russell 2000", "Indices"),
        new("EMD", "CME", 0.1m, 100m, "E-mini S&P MidCap 400", "Indices"),
        new("NKD", "CME", 5m, 5m, "Nikkei 225 in dollars", "Indices"),

        // energy
        new("CL", "NYMEX", 0.01m, 1000m, "WTI crude", "Energy"),
        new("MCL", "NYMEX", 0.01m, 100m, "Micro WTI crude", "Energy"),
        new("QM", "NYMEX", 0.025m, 500m, "E-mini WTI crude", "Energy"),
        new("BZ", "NYMEX", 0.01m, 1000m, "Brent crude", "Energy"),
        new("NG", "NYMEX", 0.001m, 10000m, "Henry Hub natural gas", "Energy"),
        new("QG", "NYMEX", 0.005m, 2500m, "E-mini natural gas", "Energy"),
        new("HO", "NYMEX", 0.0001m, 42000m, "Gasoil (heating oil)", "Energy"),
        new("RB", "NYMEX", 0.0001m, 42000m, "RBOB gasoline", "Energy"),

        // metals
        new("GC", "COMEX", 0.1m, 100m, "Gold", "Metals"),
        new("MGC", "COMEX", 0.1m, 10m, "Micro gold", "Metals"),
        new("SI", "COMEX", 0.005m, 5000m, "Silver", "Metals"),
        new("SIL", "COMEX", 0.005m, 1000m, "Micro silver", "Metals"),
        new("HG", "COMEX", 0.0005m, 25000m, "Copper", "Metals"),
        new("MHG", "COMEX", 0.0005m, 2500m, "Micro copper", "Metals"),
        new("PL", "NYMEX", 0.1m, 50m, "Platinum", "Metals"),
        new("PA", "NYMEX", 0.5m, 100m, "Palladium", "Metals"),

        // currencies
        new("6E", "CME", 0.00005m, 125000m, "Euro FX", "Currencies"),
        new("M6E", "CME", 0.0001m, 12500m, "Micro euro", "Currencies"),
        new("6B", "CME", 0.0001m, 62500m, "British pound", "Currencies"),
        new("M6B", "CME", 0.0001m, 6250m, "Micro British pound", "Currencies"),
        new("6J", "CME", 0.0000005m, 12500000m, "Japanese yen", "Currencies"),
        new("6A", "CME", 0.00005m, 100000m, "Australian dollar", "Currencies"),
        new("M6A", "CME", 0.0001m, 10000m, "Micro Australian dollar", "Currencies"),
        new("6C", "CME", 0.00005m, 100000m, "Canadian dollar", "Currencies"),
        new("6S", "CME", 0.00005m, 125000m, "Swiss franc", "Currencies"),
        new("6N", "CME", 0.00005m, 100000m, "New Zealand dollar", "Currencies"),
        new("6M", "CME", 0.00001m, 500000m, "Mexican peso", "Currencies"),

        // rates
        new("ZT", "CBOT", 0.00390625m, 2000m, "2-year Treasury note", "Rates"),
        new("ZF", "CBOT", 0.0078125m, 1000m, "5-year Treasury note", "Rates"),
        new("ZN", "CBOT", 0.015625m, 1000m, "10-year Treasury note", "Rates"),
        new("TN", "CBOT", 0.015625m, 1000m, "Ultra 10-year note", "Rates"),
        new("ZB", "CBOT", 0.03125m, 1000m, "30-year Treasury bond", "Rates"),
        new("UB", "CBOT", 0.03125m, 1000m, "Ultra Treasury bond", "Rates"),
        new("SR3", "CME", 0.0025m, 2500m, "3-month SOFR", "Rates"),

        // grains (cents per bushel; meal in dollars per ton; oil in cents per pound)
        new("ZC", "CBOT", 0.25m, 50m, "Corn", "Grains"),
        new("ZS", "CBOT", 0.25m, 50m, "Soybeans", "Grains"),
        new("ZW", "CBOT", 0.25m, 50m, "Wheat", "Grains"),
        new("KE", "CBOT", 0.25m, 50m, "Kansas hard wheat", "Grains"),
        new("ZM", "CBOT", 0.1m, 100m, "Soybean meal", "Grains"),
        new("ZL", "CBOT", 0.01m, 600m, "Soybean oil", "Grains"),
        new("ZO", "CBOT", 0.25m, 50m, "Oats", "Grains"),

        // livestock (cents per pound)
        new("LE", "CME", 0.025m, 400m, "Live cattle", "Livestock"),
        new("GF", "CME", 0.025m, 500m, "Feeder cattle", "Livestock"),
        new("HE", "CME", 0.025m, 400m, "Lean hogs", "Livestock"),

        // CME crypto
        new("BTC", "CME", 5m, 5m, "Bitcoin (CME)", "CME crypto"),
        new("MBT", "CME", 5m, 0.1m, "Micro bitcoin (CME)", "CME crypto"),
        new("ETH", "CME", 0.5m, 50m, "Ether (CME)", "CME crypto"),
        new("MET", "CME", 0.5m, 0.1m, "Micro ether (CME)", "CME crypto"),
    };

    private static readonly Dictionary<string, FuturesProduct> ByRoot = All.ToDictionary(p => p.Root, StringComparer.OrdinalIgnoreCase);

    public static FuturesProduct? Find(string root) => ByRoot.TryGetValue(root.Trim(), out var p) ? p : null;
}
