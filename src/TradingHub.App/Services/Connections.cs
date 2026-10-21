using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Feeds;
using TradingHub.Feeds.Binance;
using TradingHub.Feeds.Bybit;
using TradingHub.Feeds.InteractiveBrokers;
using TradingHub.Feeds.Rithmic;
using TradingHub.Trading;

namespace TradingHub.App.Services;

/// <summary>
/// Stores secrets (passwords, API secrets) in the macOS keychain. The test tools set
/// TRADINGHUB_KEYCHAIN_FILE and use their own file with fake data: they never read the real keychain.
/// </summary>
public static class Keychain
{
    private const string Service = "Trading Hub";
    private static readonly string? IsolatedFile = Environment.GetEnvironmentVariable("TRADINGHUB_KEYCHAIN_FILE") is { Length: > 0 } f ? f : null;

    private static Dictionary<string, string> LoadIsolated()
    {
        try
        {
            return File.Exists(IsolatedFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IsolatedFile!)) ?? new() : new();
        }
        catch
        {
            return new();
        }
    }

    private static string Run(params string[] args)
    {
        var psi = new ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(5000);
        return p.ExitCode == 0 ? output.TrimEnd('\n') : "";
    }

    public static void Set(string account, string? secret)
    {
        if (IsolatedFile != null)
        {
            var all = LoadIsolated();
            if (string.IsNullOrEmpty(secret)) all.Remove(account);
            else all[account] = secret;
            File.WriteAllText(IsolatedFile, JsonSerializer.Serialize(all));
            return;
        }

        if (string.IsNullOrEmpty(secret))
        {
            Run("delete-generic-password", "-s", Service, "-a", account);
            return;
        }

        Run("add-generic-password", "-U", "-s", Service, "-a", account, "-w", secret);
    }

    public static string Get(string account) =>
        IsolatedFile != null
            ? LoadIsolated().GetValueOrDefault(account, "")
            : Run("find-generic-password", "-s", Service, "-a", account, "-w");
}

public enum ConnectionKind
{
    [Display(Name = "Simulator")] Simulator,
    [Display(Name = "Binance Futures")] BinanceFutures,
    [Display(Name = "Binance Spot")] BinanceSpot,
    [Display(Name = "Bybit")] Bybit,
    [Display(Name = "Rithmic")] Rithmic,
    [Display(Name = "Interactive Brokers")] InteractiveBrokers,
    [Display(Name = "AMP Futures")] AmpFutures,
    [Display(Name = "Tradovate")] Tradovate,
    [Display(Name = "cTrader")] CTrader,
    [Display(Name = "OANDA")] Oanda,
    [Display(Name = "Alpaca")] Alpaca,
    [Display(Name = "Databento")] Databento,
}

/// <summary>A configured connection (the secrets go to the keychain, not to this file).</summary>
public sealed class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public ConnectionKind Kind { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Hours of history to load when opening an instrument.</summary>
    public double HistoryHours { get; set; } = 1.5;

    // Binance / Bybit
    public bool Testnet { get; set; } = true;
    public string ApiKey { get; set; } = "";
    public bool AllowRealOrders { get; set; }
    public bool ConfirmOrders { get; set; } = true;
    public decimal MaxOrderQuantity { get; set; }

    // Rithmic
    public string RithmicUri { get; set; } = RithmicGateways.TestUri;
    public string RithmicSystem { get; set; } = "Rithmic Test";
    public bool RithmicAggregatedQuotes { get; set; } = true;
    public int RithmicTimeoutMs { get; set; } = 10000;
    public string RithmicUser { get; set; } = "";
    public string RithmicAppName { get; set; } = "TradingHub";
    public List<string> RithmicProducts { get; set; } = new RithmicSettings().Products;

    // Tradovate
    public string TradovateUser { get; set; } = "";
    public string TradovateCid { get; set; } = "";
    public string TradovateAppId { get; set; } = "TradingHub";
    public List<string> TradovateProducts { get; set; } = new TradingHub.Feeds.Tradovate.TradovateSettings().Products;

    /// <summary>Fixed id of this machine for the connection (Tradovate limits devices).</summary>
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();

    // cTrader
    public string CTraderClientId { get; set; } = "";

    /// <summary>cTrader account number (0 = the first one of the environment).</summary>
    public long CTraderLogin { get; set; }

    // OANDA (account 101-001-…; empty = the first one of the token)
    public string OandaAccount { get; set; } = "";

    // Alpaca: live data iex (free) or sip (paid)
    public string AlpacaLiveFeed { get; set; } = "iex";

    // Interactive Brokers
    public string IbHost { get; set; } = "127.0.0.1";
    public int IbPort { get; set; } = 7497;
    public int IbClientId { get; set; } = 17;

    [JsonIgnore] public string SecretAccount => $"{Id}:secret";

    /// <summary>Other secrets of the connection (Tradovate API secret, cTrader tokens).</summary>
    public string SecretAccountFor(string name) => $"{Id}:{name}";

    /// <summary>Id of the market data (they share tick files and profiles of the same market).</summary>
    [JsonIgnore]
    public string MarketDataId => Kind switch
    {
        ConnectionKind.Simulator => SimulatedFeed.FeedId,
        ConnectionKind.BinanceFutures => BinanceFeed.FuturesId,
        ConnectionKind.BinanceSpot => BinanceFeed.SpotId,
        ConnectionKind.Bybit => BybitFeed.LinearId,
        ConnectionKind.Rithmic or ConnectionKind.AmpFutures => RithmicFeed.FeedId,
        ConnectionKind.Tradovate => TradingHub.Feeds.Tradovate.TradovateFeed.FeedId,
        // each cTrader broker has its own prices: the first one uses "ctrader" and the rest their own id
        ConnectionKind.CTrader => Id == TradingHub.Feeds.CTrader.CTraderFeed.FeedId ? Id : $"{TradingHub.Feeds.CTrader.CTraderFeed.FeedId}-{Id}",
        ConnectionKind.Oanda => TradingHub.Feeds.Oanda.OandaFeed.FeedId,
        ConnectionKind.Alpaca => TradingHub.Feeds.Alpaca.AlpacaFeed.FeedId,
        ConnectionKind.Databento => "databento",
        _ => "ib",
    };

    /// <summary>Provides market data for charts (Databento and IB do not).</summary>
    [JsonIgnore] public bool HasMarketData => Kind is not (ConnectionKind.InteractiveBrokers or ConnectionKind.Databento);

    /// <summary>Default id of the first profile of each type (the same as its data's).</summary>
    public static string DefaultId(ConnectionKind kind) => kind switch
    {
        ConnectionKind.Tradovate => TradingHub.Feeds.Tradovate.TradovateFeed.FeedId,
        ConnectionKind.CTrader => TradingHub.Feeds.CTrader.CTraderFeed.FeedId,
        ConnectionKind.Oanda => TradingHub.Feeds.Oanda.OandaFeed.FeedId,
        ConnectionKind.Alpaca => TradingHub.Feeds.Alpaca.AlpacaFeed.FeedId,
        ConnectionKind.Databento => "databento",
        _ => Guid.NewGuid().ToString("N")[..8],
    };

    /// <summary>Uses the Rithmic connection (Rithmic directly or AMP Futures, which provides Rithmic accounts).</summary>
    [JsonIgnore] public bool IsRithmic => Kind is ConnectionKind.Rithmic or ConnectionKind.AmpFutures;

    [JsonIgnore] public bool SupportsTrading => Kind is ConnectionKind.BinanceFutures or ConnectionKind.BinanceSpot;

    public override string ToString() => Name;
}

/// <summary>Manages the connections: stores them, creates their feeds and tests them.</summary>
public sealed class ConnectionManager
{
    private static readonly string FilePath = Path.Combine(AppPaths.Root, "connections.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly Dictionary<string, IMarketDataFeed> _feeds = new();

    public List<ConnectionProfile> Profiles { get; } = new();
    public event Action? Changed;

    public ConnectionManager()
    {
        Load();
        if (Profiles.Count == 0)
        {
            // ids equal to the market data's so the old workspaces keep working
            Profiles.Add(new ConnectionProfile { Id = SimulatedFeed.FeedId, Kind = ConnectionKind.Simulator, Name = "Simulator", HistoryHours = 6 });
            Profiles.Add(new ConnectionProfile { Id = BinanceFeed.FuturesId, Kind = ConnectionKind.BinanceFutures, Name = "Binance Futures" });
            Profiles.Add(new ConnectionProfile { Id = BinanceFeed.SpotId, Kind = ConnectionKind.BinanceSpot, Name = "Binance Spot" });
            Profiles.Add(new ConnectionProfile { Id = BybitFeed.LinearId, Kind = ConnectionKind.Bybit, Name = "Bybit" });
            Profiles.Add(new ConnectionProfile { Id = RithmicFeed.FeedId, Kind = ConnectionKind.Rithmic, Name = "Rithmic", Enabled = false });
            Save();
        }
    }

    public IEnumerable<ConnectionProfile> Enabled => Profiles.Where(p => p.Enabled);

    /// <summary>Connection of a trading account ("sim", Binance's or rithmic|connection|account).</summary>
    public ConnectionProfile? FindForAccount(string accountId) =>
        (accountId.StartsWith("rithmic|", StringComparison.Ordinal) || accountId.StartsWith("tradovate|", StringComparison.Ordinal)) && accountId.Split('|') is { Length: >= 2 } parts
            ? Find(parts[1])
            : Find(accountId);

    public ConnectionProfile? Find(string id) => Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault(p => p.MarketDataId == id);

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Profiles.AddRange(JsonSerializer.Deserialize<List<ConnectionProfile>>(File.ReadAllText(FilePath), Json) ?? new());
        }
        catch (Exception ex)
        {
            this.LogWarn("Could not read the connections: " + ex.Message);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Profiles, Json));
        Changed?.Invoke();
    }

    public string GetSecret(ConnectionProfile p) => Keychain.Get(p.SecretAccount);

    public void SetSecret(ConnectionProfile p, string? secret) => Keychain.Set(p.SecretAccount, secret);

    public string GetSecret(ConnectionProfile p, string name) => Keychain.Get(p.SecretAccountFor(name));

    public void SetSecret(ConnectionProfile p, string name, string? secret) => Keychain.Set(p.SecretAccountFor(name), secret);

    /// <summary>Deletes every secret of a connection (when removing it).</summary>
    public void ClearSecrets(ConnectionProfile p)
    {
        SetSecret(p, null);
        foreach (var name in new[] { "apisecret", "token", "refresh" }) SetSecret(p, name, null);
    }

    /// <summary>A connection's configuration changed (the Rithmic ones report it so their order session updates too).</summary>
    public event Action<ConnectionProfile>? Reconfigured;

    /// <summary>Discards the created feed (for example, after changing the configuration).</summary>
    public void Invalidate(ConnectionProfile p)
    {
        // Rithmic: the same feed logs in again with the new data. Discarding it left the old session running
        // on the open charts and the new one cut its connection (Rithmic allows a single session per user).
        if (p.IsRithmic && _feeds.TryGetValue(p.Id, out var existing) && existing is RithmicFeed rf)
        {
            rf.Reconfigure(RithmicSettingsOf(p));
            Reconfigured?.Invoke(p);
            return;
        }

        if (_feeds.Remove(p.Id, out var f))
        {
            try { f.Dispose(); }
            catch { /* ignorar */ }
        }

        Reconfigured?.Invoke(p);
    }

    public IMarketDataFeed GetFeed(string profileId)
    {
        var p = Find(profileId) ?? throw new KeyNotFoundException($"Connection {profileId} does not exist");
        if (_feeds.TryGetValue(p.Id, out var f)) return f;
        f = p.Kind switch
        {
            ConnectionKind.Simulator => new SimulatedFeed(),
            ConnectionKind.BinanceFutures => new BinanceFeed(futures: true),
            ConnectionKind.BinanceSpot => new BinanceFeed(futures: false),
            ConnectionKind.Bybit => new BybitFeed(),
            ConnectionKind.Rithmic or ConnectionKind.AmpFutures => new RithmicFeed(RithmicSettingsOf(p)),
            ConnectionKind.Tradovate => TradovateFeedOf(p),
            ConnectionKind.CTrader => CTraderFeedOf(p),
            ConnectionKind.Oanda => new TradingHub.Feeds.Oanda.OandaFeed(OandaSettingsOf(p)),
            ConnectionKind.Alpaca => new TradingHub.Feeds.Alpaca.AlpacaFeed(AlpacaSettingsOf(p)),
            ConnectionKind.Databento => throw new NotSupportedException("Databento provides no live data: it is used from Data to download futures history."),
            _ => throw new NotSupportedException("Interactive Brokers does not provide data in Trading Hub yet (connection test only)."),
        };
        _feeds[p.Id] = f;
        return f;
    }

    public RithmicSettings RithmicSettingsOf(ConnectionProfile p) => new()
    {
        Uri = p.RithmicUri,
        System = p.RithmicSystem,
        User = p.RithmicUser,
        Password = GetSecret(p),
        AppName = p.RithmicAppName,
        Products = p.RithmicProducts,
        AggregatedQuotes = p.RithmicAggregatedQuotes,
        TimeoutMs = p.RithmicTimeoutMs,
    };

    public TradingHub.Feeds.Tradovate.TradovateSettings TradovateSettingsOf(ConnectionProfile p) => new()
    {
        Live = !p.Testnet,
        User = p.TradovateUser,
        Password = GetSecret(p),
        Cid = p.TradovateCid,
        Sec = GetSecret(p, "apisecret"),
        AppId = string.IsNullOrWhiteSpace(p.TradovateAppId) ? "TradingHub" : p.TradovateAppId,
        AppVersion = "1.0",
        DeviceId = p.DeviceId,
        Products = p.TradovateProducts,
        RestUrl = Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_REST") is { Length: > 0 } r ? r : null,
        UserSocketUrl = Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_WS") is { Length: > 0 } w ? w : null,
        MdSocketUrl = Environment.GetEnvironmentVariable("TRADINGHUB_TRADOVATE_MD") is { Length: > 0 } m ? m : null,
    };

    private TradingHub.Feeds.Tradovate.TradovateFeed TradovateFeedOf(ConnectionProfile p)
    {
        // the secrets are read once (every Keychain read is a process)
        var settings = TradovateSettingsOf(p);
        return new TradingHub.Feeds.Tradovate.TradovateFeed(() => settings);
    }

    public TradingHub.Feeds.CTrader.CTraderSettings CTraderSettingsOf(ConnectionProfile p) => new()
    {
        Live = !p.Testnet,
        ClientId = p.CTraderClientId,
        ClientSecret = GetSecret(p),
        AccessToken = GetSecret(p, "token"),
        RefreshToken = GetSecret(p, "refresh"),
        TraderLogin = p.CTraderLogin,
        Url = Environment.GetEnvironmentVariable("TRADINGHUB_CTRADER_URL") is { Length: > 0 } u ? u : null,
    };

    private TradingHub.Feeds.CTrader.CTraderFeed CTraderFeedOf(ConnectionProfile p)
    {
        var settings = CTraderSettingsOf(p);
        var feed = new TradingHub.Feeds.CTrader.CTraderFeed(() => settings, p.MarketDataId);
        // the Open API renews the token: the new one is stored in the Keychain
        feed.Session.TokensRefreshed += (access, refresh) =>
        {
            settings.AccessToken = access;
            settings.RefreshToken = refresh;
            SetSecret(p, "token", access);
            SetSecret(p, "refresh", refresh);
        };
        return feed;
    }

    public TradingHub.Feeds.Oanda.OandaSettings OandaSettingsOf(ConnectionProfile p) => new()
    {
        Practice = p.Testnet,
        Token = GetSecret(p),
        AccountId = p.OandaAccount,
        RestUrl = Environment.GetEnvironmentVariable("TRADINGHUB_OANDA_REST") is { Length: > 0 } r ? r : null,
        StreamUrl = Environment.GetEnvironmentVariable("TRADINGHUB_OANDA_STREAM") is { Length: > 0 } s ? s : null,
    };

    public TradingHub.Feeds.Alpaca.AlpacaSettings AlpacaSettingsOf(ConnectionProfile p) => new()
    {
        KeyId = p.ApiKey,
        Secret = GetSecret(p),
        LiveFeed = p.AlpacaLiveFeed is "sip" ? "sip" : "iex",
        DataUrl = Environment.GetEnvironmentVariable("TRADINGHUB_ALPACA_DATA") is { Length: > 0 } d ? d : null,
        TradingUrl = Environment.GetEnvironmentVariable("TRADINGHUB_ALPACA_TRADING") is { Length: > 0 } t ? t : null,
        StreamUrl = Environment.GetEnvironmentVariable("TRADINGHUB_ALPACA_STREAM") is { Length: > 0 } st ? st : null,
    };

    /// <summary>Databento client with the connection's API key (TRADINGHUB_DATABENTO_URL points it at another server).</summary>
    public TradingHub.Feeds.Databento.DatabentoClient DatabentoOf(ConnectionProfile p) =>
        new(GetSecret(p), Environment.GetEnvironmentVariable("TRADINGHUB_DATABENTO_URL") is { Length: > 0 } u ? u : null);

    public BinanceAccountSettings BinanceSettingsOf(ConnectionProfile p) => new()
    {
        Futures = p.Kind == ConnectionKind.BinanceFutures,
        Testnet = p.Testnet,
        ApiKey = p.ApiKey,
        ApiSecret = GetSecret(p),
    };

    /// <summary>Tests a connection and returns a message to display.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(ConnectionProfile p, CancellationToken ct)
    {
        try
        {
            switch (p.Kind)
            {
                case ConnectionKind.Simulator:
                    return (true, "The simulator needs no connection.");
                case ConnectionKind.BinanceFutures:
                case ConnectionKind.BinanceSpot:
                {
                    var feed = new BinanceFeed(p.Kind == ConnectionKind.BinanceFutures);
                    var inst = await feed.GetInstrumentsAsync(ct);
                    using var acc = new BinanceAccountClient(BinanceSettingsOf(p));
                    var (ok, msg) = await acc.TestAsync(ct);
                    return (ok, $"{inst.Count} instruments available. {msg}");
                }
                case ConnectionKind.Bybit:
                {
                    var inst = await new BybitFeed().GetInstrumentsAsync(ct);
                    return (true, $"{inst.Count} perpetuals available (public data).");
                }
                case ConnectionKind.Rithmic:
                case ConnectionKind.AmpFutures:
                {
                    var systems = await RithmicClient.ListSystemsAsync(p.RithmicUri, ct);
                    if (string.IsNullOrWhiteSpace(p.RithmicUser))
                        return (true, $"Server OK: {systems.Count} systems available. Fill in user and password to sign in.");
                    // using the charts' own session: a separate login would cut the one that is running
                    var (ok, message) = await ((RithmicFeed)GetFeed(p.Id)).TestAsync(ct);
                    return (ok, ok ? $"Connected to {p.RithmicSystem}. {message}" : message);
                }
                case ConnectionKind.InteractiveBrokers:
                    return await IbProbe.TestAsync(p.IbHost, p.IbPort, ct);
                case ConnectionKind.Tradovate:
                    return await ((TradingHub.Feeds.Tradovate.TradovateFeed)GetFeed(p.Id)).TestAsync(ct);
                case ConnectionKind.CTrader:
                    return await ((TradingHub.Feeds.CTrader.CTraderFeed)GetFeed(p.Id)).TestAsync(ct);
                case ConnectionKind.Oanda:
                    return await ((TradingHub.Feeds.Oanda.OandaFeed)GetFeed(p.Id)).TestAsync(ct);
                case ConnectionKind.Alpaca:
                    return await ((TradingHub.Feeds.Alpaca.AlpacaFeed)GetFeed(p.Id)).TestAsync(ct);
                case ConnectionKind.Databento:
                {
                    if (string.IsNullOrWhiteSpace(GetSecret(p))) return (false, "The Databento API key is missing.");
                    using var db = DatabentoOf(p);
                    var (start, end) = await db.RangeAsync(ct);
                    // cost of one day of NQ, so you can see how far the credits go (it uses none up)
                    var day = DateOnly.FromDateTime(end.AddDays(-3));
                    while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(-1);
                    decimal cost = await db.CostAsync("NQ", day, day, ct);
                    return (true, $"API key OK. Globex from {start:dd/MM/yyyy} to {end:dd/MM/yyyy}. One day of NQ tick by tick costs ≈ US$ {cost:0.00} of your credits.");
                }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        return (false, "Unknown connection type");
    }

    public void DisposeAll()
    {
        foreach (var f in _feeds.Values)
        {
            try { f.Dispose(); }
            catch { /* ignorar */ }
        }

        _feeds.Clear();
    }
}
