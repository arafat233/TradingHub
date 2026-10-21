using System.Collections.Concurrent;
using TradingHub.Market;

namespace TradingHub.Feeds.Rithmic;

/// <summary>Fields of the history plant (official R|Protocol definitions).</summary>
internal static class RpH
{
    // templates
    public const int RequestTimeBarReplay = 202;
    public const int ResponseTimeBarReplay = 203;
    public const int RequestTickBarReplay = 206;
    public const int ResponseTickBarReplay = 207;

    // request
    public const int BarType = 119200;
    public const int BarSubType = 119208;
    public const int BarTypeSpecifier = 148162;
    public const int BarTypePeriod = 119112;
    public const int StartIndex = 153002;
    public const int FinishIndex = 153003;
    public const int TimeOrder = 149307;

    // response
    public const int RqHandlerRpCode = 132764;
    public const int DataBarSsboe = 119202;
    public const int DataBarUsecs = 119203;
    public const int TickVolume = 119205;
    public const int TickBidVolume = 119213;
    public const int TickAskVolume = 119214;
    public const int Marker = 119100;
    public const int BarVolume = 119110;
    public const int BarBidVolume = 119117;
    public const int BarAskVolume = 119118;
    public const int Open = 100019;
    public const int Close = 100021;
    public const int High = 100012;
    public const int Low = 100013;
    public const int TradingExchange = 157096;
}

/// <summary>
/// Rithmic history (history plant): tick by tick for the recent part (full footprint) and 1-minute
/// candles for the old part. With the market closed, the last session is shown.
/// </summary>
public sealed partial class RithmicFeed : IKlineHistory, ISessionAwareFeed, IDayDownloader
{
    /// <summary>Front-month contract of each instrument (reported by the shared quote session).</summary>
    private readonly ConcurrentDictionary<string, RithmicContract> _contracts = new();

    /// <summary>Last error when requesting history (to display).</summary>
    public string? LastHistoryError { get; private set; }

    /// <summary>
    /// Id based on the time (microseconds x 8 + tie-breaker): it grows with time and is the same in the history and in the
    /// live feed, so no trades are duplicated when they are spliced.
    /// </summary>
    internal static long TimeId(long ssboe, long usecs, ref long last)
    {
        long id = (ssboe * 1_000_000 + usecs) * 8;
        if (id <= last) id = last + 1;
        last = id;
        return id;
    }

    /// <summary>Reads messages with a deadline that is renewed on each one (a single timer per request, not one per message).</summary>
    private sealed class Reader(RithmicClient client, CancellationToken ct) : IDisposable
    {
        private readonly CancellationTokenSource _watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);

        public async Task<ProtoMessage?> NextAsync(TimeSpan timeout)
        {
            _watchdog.CancelAfter(timeout);
            try
            {
                return await client.ReceiveAsync(_watchdog.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null;
            }
        }

        public void Dispose() => _watchdog.Dispose();
    }

    private void EnsureUser()
    {
        if (string.IsNullOrWhiteSpace(_settings.User)) throw new InvalidOperationException("Set your Rithmic user in Connections");
    }

    /// <summary>
    /// Front-month contract and last trade, via the shared quote session (from the live subscription if
    /// the chart is open; otherwise, a short query over the same session).
    /// </summary>
    private async Task<RithmicContract> ResolveAsync(Instrument instrument, CancellationToken ct, bool fresh = false)
    {
        if (_contracts.TryGetValue(instrument.Key, out var known) && (!fresh || DateTime.UtcNow - known.When < TimeSpan.FromMinutes(2)))
            return known;
        EnsureUser();
        var c = await _ticker.ResolveAsync(instrument, ct).ConfigureAwait(false);
        _contracts[instrument.Key] = c;
        return c;
    }

    /// <summary>Is the response from the current request? (on the shared session each request carries its own tag)</summary>
    private static bool Mine(ProtoMessage m, string tag) => m.Strings(Rp.UserMsg).FirstOrDefault() is not { } t || t == tag;

    private static long Ssboe(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>Contract an instrument trades on (ES -> ESZ6 on CME).</summary>
    public async Task<(string Symbol, string Exchange)> ContractAsync(Instrument instrument, CancellationToken ct)
    {
        var c = await ResolveAsync(instrument, ct).ConfigureAwait(false);
        return (c.Symbol, c.Exchange);
    }

    /// <summary>Time of the last trade (with the market closed, the history ends there).</summary>
    public async Task<DateTime?> LastTradeTimeAsync(Instrument instrument, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.User)) return null;
        return (await ResolveAsync(instrument, ct, fresh: true).ConfigureAwait(false)).LastTrade;
    }

    /// <summary>Trades tick by tick (each tick with its side: at the ask a buy, at the bid a sell), page by page.</summary>
    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.User))
        {
            progress?.Report(1);
            return;
        }

        LastHistoryError = null;
        string current = FuturesRolls.Supports(instrument.Symbol) ? FuturesRolls.On(instrument.Symbol, DateTime.UtcNow).Symbol : "";
        foreach (var (contract, from, to, p) in await PlanAsync(instrument, fromUtc, toUtc, progress, ct).ConfigureAwait(false))
        {
            long start = Ssboe(from), end = Ssboe(to);
            if (end <= start) continue;
            bool expired = current.Length > 0 && to <= FuturesRolls.On(instrument.Symbol, DateTime.UtcNow).From && contract.Symbol != current;
            bool stop = await SegmentAsync(contract, from, expired,
                (c, t, _) => _history.RunAsync((client, tag, ct) => TicksAsync(client, tag, instrument, c, start, end, t!, p, ct), ct),
                onBatch, null).ConfigureAwait(false);
            if (stop) return;
        }

        progress?.Report(1);
    }

    /// <summary>Progress of one stretch within the total.</summary>
    private sealed class Scaled(IProgress<double>? inner, double from, double width) : IProgress<double>
    {
        public void Report(double value) => inner?.Report(from + Math.Clamp(value, 0, 1) * width);
    }

    /// <summary>
    /// Stretches of the range with their contract. On the CME indices each date goes with the contract that traded that day
    /// (ESU6 until the September roll, ESZ6 afterwards…): without that, the old history came from the current contract, which
    /// back then barely traded. Today's stretch uses the contract Rithmic reports, same as the live feed.
    /// </summary>
    private readonly HashSet<string> _missingContracts = new();

    /// <summary>Expired contracts Rithmic returned empty (it does not keep their history) in the last download.</summary>
    public IReadOnlyList<string> MissingContracts
    {
        get
        {
            lock (_missingContracts) return _missingContracts.OrderBy(x => x).ToList();
        }
    }

    public void ClearMissingContracts()
    {
        lock (_missingContracts) _missingContracts.Clear();
    }

    /// <summary>
    /// Runs one stretch; if it belongs to an expired contract and comes back empty, it tries the symbol with the two-digit
    /// year (NQU26) and if there is nothing there either it notes it down (Rithmic does not keep the history of expired ones).
    /// </summary>
    private async Task<bool> SegmentAsync(RithmicContract contract, DateTime from, bool expired,
        Func<RithmicContract, Func<IReadOnlyList<Trade>, bool>?, Func<IReadOnlyList<Kline>, bool>?, Task> run,
        Func<IReadOnlyList<Trade>, bool>? onTrades, Func<IReadOnlyList<Kline>, bool>? onKlines)
    {
        int rows = 0;
        bool stop = false;
        Func<IReadOnlyList<Trade>, bool>? t = onTrades == null ? null : b =>
        {
            rows += b.Count;
            if (onTrades(b)) return true;
            stop = true;
            return false;
        };
        Func<IReadOnlyList<Kline>, bool>? k = onKlines == null ? null : b =>
        {
            rows += b.Count;
            if (onKlines(b)) return true;
            stop = true;
            return false;
        };
        await run(contract, t, k).ConfigureAwait(false);
        if (rows > 0 || stop || !expired) return stop;
        if (FuturesRolls.TwoDigitYear(contract.Symbol, from) is { } alt)
        {
            await run(contract with { Symbol = alt }, t, k).ConfigureAwait(false);
            if (rows > 0 || stop) return stop;
        }

        lock (_missingContracts) _missingContracts.Add(contract.Symbol);
        return false;
    }

    private async Task<List<(RithmicContract Contract, DateTime From, DateTime To, IProgress<double> Progress)>> PlanAsync(
        Instrument instrument, DateTime fromUtc, DateTime toUtc, IProgress<double>? progress, CancellationToken ct)
    {
        var plan = new List<(RithmicContract, DateTime, DateTime, IProgress<double>)>();
        double total = Math.Max(1, (toUtc - fromUtc).Ticks);
        if (toUtc <= fromUtc) return plan;
        if (!FuturesRolls.Supports(instrument.Symbol))
        {
            plan.Add((await ResolveAsync(instrument, ct).ConfigureAwait(false), fromUtc, toUtc, new Scaled(progress, 0, 1)));
            return plan;
        }

        var current = FuturesRolls.On(instrument.Symbol, DateTime.UtcNow).Symbol;
        foreach (var (symbol, from, to) in FuturesRolls.Segments(instrument.Symbol, fromUtc, toUtc))
        {
            var contract = symbol == current
                ? await ResolveAsync(instrument, ct).ConfigureAwait(false)
                : new RithmicContract(symbol, instrument.Exchange, null, DateTime.UtcNow);
            plan.Add((contract, from, to, new Scaled(progress, (from - fromUtc).Ticks / total, (to - from).Ticks / total)));
        }

        return plan;
    }

    /// <summary>Since when the current contract has traded (Rithmic gives no history for expired ones); null if it is not known.</summary>
    public static DateTime? CurrentContractSince(Instrument instrument) =>
        FuturesRolls.Supports(instrument.Symbol) ? FuturesRolls.On(instrument.Symbol, DateTime.UtcNow).From : null;

    /// <summary>Contract the history of a date is downloaded with (to display it).</summary>
    public static string ContractNameOn(Instrument instrument, DateTime utc) =>
        FuturesRolls.Supports(instrument.Symbol) ? FuturesRolls.On(instrument.Symbol, utc).Symbol : instrument.Symbol + " (active month)";

    /// <summary>A whole day tick by tick (UTC) with the contract that traded that day; null if Rithmic has no trades.</summary>
    public async Task<List<Trade>?> DownloadDayAsync(Instrument instrument, DateOnly day, IProgress<double>? progress, CancellationToken ct)
    {
        var list = new List<Trade>();
        var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        await LoadTradesAsync(instrument, from, from.AddDays(1), null, b =>
        {
            list.AddRange(b);
            return true;
        }, progress, ct).ConfigureAwait(false);
        return list.Count > 0 ? list : null;
    }

    /// <summary>The candle cache is kept separate from the old one (built with a single contract, without the rolls).</summary>
    public string CacheTag => "continuo";

    public string HistoryNote => "tick by tick from Rithmic (only since the last expiry: it does not keep expired contracts)";

    private async Task TicksAsync(RithmicClient client, string tag, Instrument instrument, RithmicContract contract, long start, long end,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {

        using var reader = new Reader(client, ct);
        long cursor = start, lastSsboe = 0, lastUs = -1, lastId = 0;
        var batch = new List<Trade>(4096);
        int emptyPages = 0;
        for (int page = 0; page < 5000 && cursor <= end; page++)
        {
            await client.SendAsync(new ProtoWriter().Int(Rp.TemplateId, RpH.RequestTickBarReplay).String(Rp.UserMsg, tag)
                .String(Rp.Symbol, contract.Symbol).String(Rp.Exchange, contract.Exchange)
                .Int(RpH.BarType, 1).Int(RpH.BarSubType, 1).String(RpH.BarTypeSpecifier, "1")
                .Int(RpH.StartIndex, cursor).Int(RpH.FinishIndex, end).Int(RpH.TimeOrder, 1), ct).ConfigureAwait(false);
            int rows = 0, fresh = 0;
            while (true)
            {
                var m = await reader.NextAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false)
                        ?? throw new TimeoutException("Rithmic stopped sending the history");
                if (m.Int(Rp.TemplateId) != RpH.ResponseTickBarReplay || !Mine(m, tag)) continue;
                var rp = m.Strings(Rp.RpCode).ToList();
                if (rp.Count > 0 || !m.Strings(RpH.RqHandlerRpCode).Any())
                {
                    // end of the page; a code other than 0 is a rejection (permission, symbol…)
                    if (rp.Count > 0 && rp[0] != "0" && page == 0 && rows == 0) LastHistoryError = string.Join(" ", rp);
                    break;
                }

                rows++;
                long ssboe = m.Ints(RpH.DataBarSsboe).FirstOrDefault(), us = m.Ints(RpH.DataBarUsecs).FirstOrDefault();
                if (ssboe < lastSsboe || (ssboe == lastSsboe && us <= lastUs)) continue; // already there (a page repeating the last second)
                var time = DateTimeOffset.FromUnixTimeSeconds(ssboe).UtcDateTime.AddTicks(us * 10);
                decimal price = instrument.TickSize > 0
                    ? Math.Round((decimal)m.Double(RpH.Close) / instrument.TickSize) * instrument.TickSize
                    : (decimal)m.Double(RpH.Close);
                long vol = m.Int(RpH.TickVolume), bid = m.Int(RpH.TickBidVolume), ask = m.Int(RpH.TickAskVolume);
                if (ask > 0) batch.Add(new Trade(TimeId(ssboe, us, ref lastId), time, price, ask, TradeSide.Buy));
                if (bid > 0) batch.Add(new Trade(TimeId(ssboe, us, ref lastId), time, price, bid, TradeSide.Sell));
                if (ask <= 0 && bid <= 0 && vol > 0) batch.Add(new Trade(TimeId(ssboe, us, ref lastId), time, price, vol, TradeSide.None));
                lastSsboe = ssboe;
                lastUs = us;
                fresh++;
                if (batch.Count >= 4096)
                {
                    if (!onBatch(batch.ToArray())) return;
                    batch.Clear();
                }
            }

            if (fresh == 0)
            {
                // nothing new: either there is no more or the page repeated the whole last second
                if (rows == 0 || ++emptyPages >= 2) break;
                cursor = lastSsboe + 1;
                continue;
            }

            emptyPages = 0;
            progress?.Report(Math.Clamp((lastSsboe - start) / (double)(end - start), 0, 1));
            if (lastSsboe >= end) break;
            cursor = lastSsboe; // the next page starts on the same second: no ticks are lost
        }

        if (batch.Count > 0) onBatch(batch.ToArray());
        progress?.Report(1);
    }

    /// <summary>1-minute (or longer) candles for the long history, with buy and sell volume.</summary>
    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.User))
        {
            progress?.Report(1);
            return;
        }

        int period = Math.Max(1, minutes);
        string current = FuturesRolls.Supports(instrument.Symbol) ? FuturesRolls.On(instrument.Symbol, DateTime.UtcNow).Symbol : "";
        foreach (var (contract, from, to, p) in await PlanAsync(instrument, fromUtc, toUtc, progress, ct).ConfigureAwait(false))
        {
            long start = Ssboe(from), end = Ssboe(to);
            if (end <= start) continue;
            bool expired = current.Length > 0 && to <= FuturesRolls.On(instrument.Symbol, DateTime.UtcNow).From && contract.Symbol != current;
            bool stop = await SegmentAsync(contract, from, expired,
                (c, _, k) => _history.RunAsync((client, tag, ct) => BarsAsync(client, tag, c, start, end, period, k!, p, ct), ct),
                null, onBatch).ConfigureAwait(false);
            if (stop) return;
        }

        progress?.Report(1);
    }

    private async Task BarsAsync(RithmicClient client, string tag, RithmicContract contract, long start, long end, int period,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        using var reader = new Reader(client, ct);
        long cursor = start, lastMarker = 0;
        var batch = new List<Kline>(2048);
        for (int page = 0; page < 5000 && cursor <= end; page++)
        {
            await client.SendAsync(new ProtoWriter().Int(Rp.TemplateId, RpH.RequestTimeBarReplay).String(Rp.UserMsg, tag)
                .String(Rp.Symbol, contract.Symbol).String(Rp.Exchange, contract.Exchange)
                .Int(RpH.BarType, 2).Int(RpH.BarTypePeriod, period)
                .Int(RpH.StartIndex, cursor).Int(RpH.FinishIndex, end).Int(RpH.TimeOrder, 1), ct).ConfigureAwait(false);
            int fresh = 0;
            while (true)
            {
                var m = await reader.NextAsync(TimeSpan.FromSeconds(25)).ConfigureAwait(false)
                        ?? throw new TimeoutException("Rithmic stopped sending the candles");
                if (m.Int(Rp.TemplateId) != RpH.ResponseTimeBarReplay || !Mine(m, tag)) continue;
                var rp = m.Strings(Rp.RpCode).ToList();
                if (rp.Count > 0 || !m.Strings(RpH.RqHandlerRpCode).Any())
                {
                    if (rp.Count > 0 && rp[0] != "0" && page == 0 && fresh == 0) LastHistoryError = string.Join(" ", rp);
                    break;
                }

                long marker = m.Int(RpH.Marker);
                if (marker <= lastMarker) continue;
                lastMarker = marker;
                fresh++;
                // the stamp is the candle's close
                long openMs = (marker - period * 60L) * 1000;
                long ask = m.Int(RpH.BarAskVolume), bid = m.Int(RpH.BarBidVolume), vol = m.Int(RpH.BarVolume);
                batch.Add(new Kline(openMs, (decimal)m.Double(RpH.Open), (decimal)m.Double(RpH.High), (decimal)m.Double(RpH.Low),
                    (decimal)m.Double(RpH.Close), vol, ask, ask + bid > 0));
                if (batch.Count >= 2048)
                {
                    if (!onBatch(batch.ToArray())) return;
                    batch.Clear();
                }
            }

            if (fresh == 0) break;
            progress?.Report(Math.Clamp((lastMarker - start) / (double)(end - start), 0, 1));
            if (lastMarker >= end) break;
            cursor = lastMarker + 1;
        }

        if (batch.Count > 0) onBatch(batch.ToArray());
        progress?.Report(1);
    }

    /// <summary>Not known in advance: "the whole history" brings whatever the contract has.</summary>
    public Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct) => Task.FromResult<DateTime?>(null);
}
