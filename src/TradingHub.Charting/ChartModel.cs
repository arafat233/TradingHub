using System.Drawing;
using System.Globalization;
using System.Reflection;
using TradingHub.Common.Logging;
using TradingHub.Data;
using TradingHub.Indicators;
using TradingHub.Market;
using TradingHub.Trading;
using TradingHub.Trading.Statistics;
using IndicatorCandle = TradingHub.Indicators.IndicatorCandle;

namespace TradingHub.Charting;

/// <summary>An indicator added to the chart.</summary>
public sealed class IndicatorSlot
{
    internal IndicatorSlot(Indicator indicator)
    {
        Indicator = indicator;
        var t = indicator.GetType();
        WantsTrades = Overrides(t, "OnNewTrade") || Overrides(t, "OnNewTrades");
        WantsDepth = Overrides(t, "MarketDepthChanged") || Overrides(t, "MarketDepthsChanged");
        WantsBestBidAsk = Overrides(t, "OnBestBidAskChanged");
        WantsMyTrades = Overrides(t, "OnNewMyTrade");
    }

    public Indicator Indicator { get; }
    public SubPanel? Panel { get; internal set; }
    public bool Visible { get; set; } = true;
    public string? Error { get; internal set; }
    internal bool NeedsRecalc = true;
    internal bool Initialized;
    internal bool WantsTrades, WantsDepth, WantsBestBidAsk, WantsMyTrades;
    internal bool WantsCumulative;
    internal List<CumulativeTradesRequest> PendingCumulative { get; } = new();

    private static bool Overrides(Type t, string method)
    {
        for (var c = t; c != null && c != typeof(Indicator); c = c.BaseType)
        {
            if (c.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly).Any(m => m.Name == method))
                return true;
        }

        return false;
    }

    public override string ToString() => Indicator.Name;
}

/// <summary>Indicator panel below the price chart.</summary>
public sealed class SubPanel
{
    public List<IndicatorSlot> Slots { get; } = new();
    public double HeightRatio { get; set; } = 0.2;
}

public sealed record AlertInfo(DateTime Time, string Instrument, string Message, string SoundFile, Color Background, Color Foreground, string Source);

/// <summary>An entry or exit of a strategy placed on the chart.</summary>
public readonly record struct StrategySignal(DateTime Time, decimal Price, bool Buy, bool Entry, string Label);

/// <summary>
/// A strategy placed on the chart like an indicator: it marks where it enters and where it exits (the historical and the new part).
/// The app runs it (visual only or executing on an account) and leaves here whatever has to be drawn.
/// </summary>
public sealed class StrategyOverlay
{
    public string Name { get; set; } = "";

    /// <summary>"visual", "live · account", "calculating…"</summary>
    public string Status { get; set; } = "";

    public bool Visible { get; set; } = true;
    public bool Live { get; set; }
    public string? Error { get; set; }
    public SkiaSharp.SKColor Color { get; set; } = new(180, 120, 255);
    public IReadOnlyList<StrategySignal> Signals { get; set; } = Array.Empty<StrategySignal>();
    public IReadOnlyList<TradeLink> Links { get; set; } = Array.Empty<TradeLink>();

    /// <summary>What the strategy draws: ranges, lines, levels, markers and texts.</summary>
    public IReadOnlyList<StrategyShape> Shapes { get; set; } = Array.Empty<StrategyShape>();

    /// <summary>Continuous lines of the strategy (one value per candle).</summary>
    public IReadOnlyList<StrategyLine> Lines { get; set; } = Array.Empty<StrategyLine>();

    /// <summary>App object that manages it.</summary>
    public object? Tag { get; set; }
}

public enum StrategyShapeKind
{
    Line,
    Box,
    HorizontalLine,
    Marker,
    Text,
}

public enum StrategyMarker
{
    ArrowUp,
    ArrowDown,
    Circle,
    Square,
    Diamond,
    Cross,
}

/// <summary>A strategy drawing ready to paint. Dash: 0 solid, 1 dashed, 2 dotted.</summary>
public sealed record StrategyShape(StrategyShapeKind Kind, DateTime Time1, decimal Price1, DateTime Time2, decimal Price2, SkiaSharp.SKColor Color,
    byte FillAlpha, float Width, int Dash, StrategyMarker Marker, string Text, bool ExtendRight);

/// <summary>A continuous line of a strategy (null breaks the line).</summary>
public sealed record StrategyLine(string Name, SkiaSharp.SKColor Color, float Width, int Dash, IReadOnlyList<(DateTime Time, decimal? Value)> Points);

/// <summary>One of your own fills to mark on the chart.</summary>
public readonly record struct TradeMark(DateTime Time, decimal Price, bool Buy, decimal Quantity);

/// <summary>A closed trade (entry -> exit) to join with a line on the chart.</summary>
public readonly record struct TradeLink(DateTime OpenTime, decimal OpenPrice, DateTime CloseTime, decimal ClosePrice, bool IsLong, decimal Pnl);

/// <summary>
/// Highlighted trade (when picked from a trade list): a box from entry to exit, markers, the worst and
/// the best price it went through (MAE and MFE) and a label with the detail.
/// </summary>
public sealed record TradeFocus(DateTime EntryTime, decimal EntryPrice, DateTime ExitTime, decimal ExitPrice, bool IsLong, decimal Quantity, decimal Pnl, string Label);

/// <summary>
/// Complete state of a chart: data, candles, indicators, settings and navigation.
/// It implements <see cref="IIndicatorHost"/> so the indicators can reach the candles and the chart.
/// Everything is used from the UI thread.
/// </summary>
public sealed class ChartModel : IIndicatorHost, IIndicatorDataProvider, IDisposable
{
    private readonly List<IndicatorCandle> _wrappers = new();
    private readonly List<(Indicator Ind, TimeSpan Interval, Action Action, DateTime Next)> _timers = new();
    private readonly Queue<Action> _invokes = new();
    private int? _autoStep;
    private CumulativeTrade? _openCumulative;
    private long _lastBestBid = long.MinValue, _lastBestAsk = long.MaxValue;
    private readonly ChartInfo _chartInfo;
    private readonly InstrumentInfo _instrumentInfo = new();
    private readonly MarketDepthInfo _depthInfo;

    public ChartModel(InstrumentData data, ChartPeriod period, ChartSettings? settings = null)
    {
        Data = data;
        Period = period;
        Settings = settings ?? new ChartSettings();
        Theme = ChartTheme.With(Settings.Colors);
        Series = new CandleSeries(data.Instrument, period, 1);
        _chartInfo = new ChartInfo(() => (IChartContainer?)LastLayout?.Main ?? EmptyContainer.Instance);
        _depthInfo = new MarketDepthInfo(DepthSnapshotArgs, () => SumDepth(BookSide.Ask), () => SumDepth(BookSide.Bid));

        data.TradesAppended += OnTradesAppended;
        data.HistoryLoaded += OnHistoryLoaded;
        data.DepthUpdated += OnDepthUpdated;
        data.StatusChanged += () => Invalidate();
        Rebuild();
    }

    public InstrumentData Data { get; }
    public Instrument Instrument => Data.Instrument;
    public ChartPeriod Period { get; private set; }
    public ChartSettings Settings { get; }
    public ChartViewport Viewport { get; } = new();
    public ChartTheme Theme { get; set; } = ChartTheme.Base;
    public CandleSeries Series { get; private set; }
    public List<IndicatorSlot> Indicators { get; } = new();
    public List<SubPanel> SubPanels { get; } = new();
    /// <summary>Chart drawings. Charts of the same instrument share the same list (they stay in sync).</summary>
    public List<Drawings.ChartDrawing> Drawings { get; set; } = new();

    /// <summary>Trading manager (simulated broker) — optional.</summary>
    public TradingManager? Trading { get; set; }

    /// <summary>Account traded from the chart (orders and position drawn).</summary>
    public ITradingAccount? Account { get; set; }

    /// <summary>Strategies placed on the chart (they show up in the legend and mark their trades).</summary>
    public List<StrategyOverlay> Strategies { get; } = new();

    /// <summary>Your own fills on this instrument (they are marked on the chart).</summary>
    public IReadOnlyList<TradeMark> TradeMarks { get; set; } = Array.Empty<TradeMark>();

    /// <summary>Closed trades: a line from entry to exit.</summary>
    public IReadOnlyList<TradeLink> TradeLinks { get; set; } = Array.Empty<TradeLink>();

    /// <summary>Highlighted trade (null = none).</summary>
    public TradeFocus? FocusTrade { get; set; }

    /// <summary>
    /// Centers the view on a time interval (with room on the sides) and, if it does not fit, shrinks the candles. It uses the
    /// width of the last drawn frame (or a typical one if nothing has been drawn yet).
    /// </summary>
    public void CenterOn(DateTime fromUtc, DateTime toUtc)
    {
        var series = Series;
        if (series.Count == 0) return;
        int b0 = Math.Max(0, series.FindBar(fromUtc)), b1 = Math.Max(b0, series.FindBar(toUtc));
        double plot = LastLayout is { } l ? l.Plot.Width : 900;
        var vp = Viewport;
        double span = b1 - b0 + 1;
        double needed = Math.Max(30, span * 1.8 + 10);
        if (needed * vp.BarWidth > plot) vp.BarWidth = Math.Clamp(plot / needed, ChartViewport.MinBarWidth, ChartViewport.MaxBarWidth);
        double mid = (b0 + b1) / 2.0;
        double scroll = series.Count - 1 - mid + vp.RightMarginBars + 0.5 - plot / 2 / vp.BarWidth;
        vp.ScrollBars = Math.Max(0, scroll);
        vp.AutoScale = true;
        Invalidate();
    }

    /// <summary>Stop loss / targets of the account (to draw them and drag them from the position).</summary>
    public BracketManager? Brackets { get; set; }

    public TradingStatisticsProvider Statistics { get; set; } = new();

    /// <summary>Layout of the last drawn frame.</summary>
    public ChartLayout? LastLayout { get; internal set; }

    public bool IsDirty { get; private set; } = true;

    public event Action? Invalidated;
    public event Action<AlertInfo>? AlertRaised;

    /// <summary>Rebuilds the colors with this chart's own (after changing them in its settings).</summary>
    public void ApplyColors()
    {
        Theme = ChartTheme.With(Settings.Colors);
        Invalidate();
    }

    public void Invalidate()
    {
        IsDirty = true;
        Invalidated?.Invoke();
    }

    internal void ClearDirty() => IsDirty = false;

    public string Title => $"{Instrument.Symbol} · {Period.DisplayName}";

    // =================================================================== construction

    public void SetPeriod(ChartPeriod period)
    {
        if (period == Period) return;
        Period = period;
        _autoStep = null;
        Rebuild();
    }

    /// <summary>Ticks per row shown in the footprint (0 = automatic, based on the zoom).</summary>
    public void SetPriceStep(int step)
    {
        Settings.PriceStep = Math.Max(0, step);
        int baseStep = BaseStepFor(Settings.PriceStep);
        if (baseStep != Series.PriceStep) Rebuild();
        else Invalidate();
    }

    /// <summary>Ticks per row the candles are built with (base resolution of the footprint).</summary>
    public int EffectivePriceStep => Series.PriceStep;

    /// <summary>
    /// Base step: fine (~150 rows per typical candle) so you can zoom in without rebuilding.
    /// If the user fixes a step, the base one is the largest divisor of that step not exceeding the automatic one.
    /// </summary>
    private int BaseStepFor(int userStep)
    {
        if (_autoStep == null && Data.Trades.Count > 100) _autoStep = CandleSeries.SuggestPriceStep(Data.Trades, Instrument, Period, targetRows: 150);
        int auto = _autoStep ?? 1;
        if (userStep <= 0) return auto;
        if (userStep <= auto) return userStep;
        for (int d = auto; d >= 1; d--)
            if (userStep % d == 0) return d;
        return 1;
    }

    /// <summary>Rebuilds the candles from the trades and recalculates the indicators.</summary>
    public void Rebuild()
    {
        int step = BaseStepFor(Settings.PriceStep);
        Series = new CandleSeries(Instrument, Period, step);
        Series.Rebuild(Data.Trades);
        _wrappers.Clear();
        for (int i = 0; i < Series.Count; i++) _wrappers.Add(new IndicatorCandle(Series[i], Series));
        UpdateInfos();
        foreach (var s in Indicators) s.NeedsRecalc = true;
        _openCumulative = null;
        ProcessPending();
        Invalidate();
    }

    private void UpdateInfos()
    {
        _instrumentInfo.Instrument = Instrument.Symbol;
        _instrumentInfo.Exchange = Instrument.Exchange;
        _instrumentInfo.TickSize = Series.RowSize;
        _instrumentInfo.TickCost = Instrument.PointValue * Series.RowSize;
        _instrumentInfo.LotSize = Instrument.VolumeStep;
        _instrumentInfo.Description = Instrument.Description;
        _instrumentInfo.TimeZoneOffset = Settings.TimeZone.GetUtcOffset(DateTime.UtcNow);

        _chartInfo.TimeFrame = Period.Code;
        _chartInfo.ChartType = Period.Type switch
        {
            PeriodType.Seconds => "Seconds",
            PeriodType.Tick => "Tick",
            PeriodType.Volume => "Volume",
            PeriodType.Range => "Range",
            PeriodType.Delta => "Delta",
            PeriodType.Renko => "Renko",
            _ => "TimeFrame",
        };
        _chartInfo.ChartVisualMode = Settings.Style switch
        {
            ChartStyle.Footprint => ChartVisualModes.Clusters,
            ChartStyle.Line => ChartVisualModes.Line,
            ChartStyle.Bars => ChartVisualModes.Bars,
            _ => ChartVisualModes.Candles,
        };
        int dec = Instrument.DecimalPlaces(Series.RowSize);
        _chartInfo.StringFormat = "{0:F" + dec + "}";
        _chartInfo.VolumeDecimals = Instrument.VolumeDecimals;
        var c = _chartInfo.ColorsStore;
        c.UpCandleColor = ToColor(Theme.UpCandle);
        c.DownCandleColor = ToColor(Theme.DownCandle);
        c.FootprintAskColor = ToColor(Theme.Ask);
        c.FootprintBidColor = ToColor(Theme.Bid);
        c.BaseBackgroundColor = ToColor(Theme.BackgroundBottom);
        c.AxisTextColor = ToColor(Theme.AxisText);
    }

    private static Color ToColor(SkiaSharp.SKColor c) => Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);

    // =================================================================== indicators

    public IndicatorSlot AddIndicator(Indicator indicator, bool applyDefaultColors = true)
    {
        var slot = new IndicatorSlot(indicator);
        indicator.HostAttach(this);
        if (indicator.Panel == IndicatorDataProvider.NewPanel)
        {
            var panel = new SubPanel();
            panel.Slots.Add(slot);
            slot.Panel = panel;
            SubPanels.Add(panel);
        }

        Indicators.Add(slot);
        try
        {
            indicator.HostInitialize(applyDefaultColors);
            slot.Initialized = true;
        }
        catch (Exception ex)
        {
            slot.Error = ex.Message;
            this.LogError($"Error while initializing {indicator.Name}", ex);
        }

        indicator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Indicator.Panel)) MoveToPanelByName(slot, indicator.Panel);
        };
        slot.NeedsRecalc = true;
        ProcessPending();
        Invalidate();
        return slot;
    }

    /// <summary>Adds the indicator to an existing panel (to overlay indicators).</summary>
    public void MoveToPanel(IndicatorSlot slot, SubPanel? target)
    {
        if (slot.Panel == target) return;
        if (slot.Panel != null)
        {
            slot.Panel.Slots.Remove(slot);
            if (slot.Panel.Slots.Count == 0) SubPanels.Remove(slot.Panel);
        }

        slot.Panel = target;
        if (target != null)
        {
            target.Slots.Add(slot);
            if (!SubPanels.Contains(target)) SubPanels.Add(target);
        }

        Invalidate();
    }

    private void MoveToPanelByName(IndicatorSlot slot, string panel)
    {
        if (panel == IndicatorDataProvider.NewPanel && slot.Panel == null) MoveToPanel(slot, new SubPanel());
        else if (panel == IndicatorDataProvider.CandlesPanel && slot.Panel != null) MoveToPanel(slot, (SubPanel?)null);
    }

    public void RemoveIndicator(IndicatorSlot slot)
    {
        Indicators.Remove(slot);
        if (slot.Panel != null)
        {
            slot.Panel.Slots.Remove(slot);
            if (slot.Panel.Slots.Count == 0) SubPanels.Remove(slot.Panel);
        }

        _timers.RemoveAll(t => t.Ind == slot.Indicator);
        try { slot.Indicator.HostDetach(); }
        catch (Exception ex) { this.LogError("Error while removing the indicator", ex); }
        Invalidate();
    }

    /// <summary>Processes recalculations, timers and pending requests. Call it on every UI tick.</summary>
    public void Update()
    {
        RefreshFixedProfiles();
        while (_invokes.Count > 0)
        {
            var a = _invokes.Dequeue();
            try { a(); }
            catch (Exception ex) { this.LogError("Error in a deferred action", ex); }
        }

        ProcessPending();

        if (_timers.Count > 0)
        {
            var now = DateTime.UtcNow;
            for (int i = 0; i < _timers.Count; i++)
            {
                var t = _timers[i];
                if (now < t.Next) continue;
                _timers[i] = (t.Ind, t.Interval, t.Action, now + t.Interval);
                try { t.Action(); }
                catch (Exception ex) { this.LogError("Error in an indicator timer", ex); }
            }
        }
    }

    private void ProcessPending()
    {
        foreach (var slot in Indicators.ToArray())
        {
            if (slot.NeedsRecalc)
            {
                slot.NeedsRecalc = false;
                Safe(slot, () => slot.Indicator.HostRecalculate());
                UpdateLinesTillTouch(slot.Indicator, 0);
                Invalidate();
            }

            if (slot.PendingCumulative.Count > 0)
            {
                var reqs = slot.PendingCumulative.ToArray();
                slot.PendingCumulative.Clear();
                foreach (var r in reqs)
                {
                    var list = BuildCumulativeTrades(r.BeginTime, r.EndTime);
                    Safe(slot, () => slot.Indicator.HostCumulativeResponse(r, list));
                }

                slot.WantsCumulative = true;
                Invalidate();
            }
        }
    }

    private void Safe(IndicatorSlot slot, Action a)
    {
        try
        {
            a();
            slot.Error = null;
        }
        catch (Exception ex)
        {
            if (slot.Error == null) this.LogError($"Error in the indicator {slot.Indicator.Name}: {ex.Message}", ex);
            slot.Error = ex.Message;
        }
    }

    // =================================================================== live data

    private void OnHistoryLoaded()
    {
        _autoStep = null;
        Rebuild();
    }

    private void OnTradesAppended(int start, int count)
    {
        var res = Series.Append(Data.Trades);
        for (int i = _wrappers.Count; i < Series.Count; i++) _wrappers.Add(new IndicatorCandle(Series[i], Series));
        if (!res.Changed) return;

        // ticks for the indicators that ask for them
        List<MarketDataArg>? args = null;
        bool anyCum = Indicators.Any(s => s.WantsCumulative);
        if (Indicators.Any(s => s.WantsTrades) || anyCum)
        {
            args = new List<MarketDataArg>(count);
            for (int i = start; i < start + count; i++)
            {
                var side = Data.Trades.GetSide(i);
                args.Add(new MarketDataArg
                {
                    Id = Data.Trades.GetId(i),
                    Time = Data.Trades.GetTime(i),
                    Price = Data.Trades.GetPrice(i),
                    Volume = Data.Trades.GetVolume(i),
                    Direction = side == TradeSide.Buy ? TradeDirection.Buy : side == TradeSide.Sell ? TradeDirection.Sell : TradeDirection.Between,
                    DataType = MarketDataType.Trade,
                    Security = Instrument.Symbol,
                });
            }
        }

        foreach (var slot in Indicators)
        {
            if (slot.NeedsRecalc) continue;
            Safe(slot, () => slot.Indicator.HostCalculate(res.FirstChangedBar));
            if (slot.WantsTrades && args != null) Safe(slot, () => slot.Indicator.HostNewTrades(args));
            UpdateLinesTillTouch(slot.Indicator, res.FirstChangedBar);
        }

        if (anyCum && args != null) FeedCumulative(args);
        Invalidate();
    }

    private void OnDepthUpdated(DepthUpdate update)
    {
        var snap = update.Snapshot;
        bool bestChanged = snap.BestBidTicks != _lastBestBid || snap.BestAskTicks != _lastBestAsk;
        _lastBestBid = snap.BestBidTicks;
        _lastBestAsk = snap.BestAskTicks;

        if (Indicators.Any(s => s.WantsDepth) && update.Changes.Count > 0)
        {
            var args = update.Changes.Select(c => new MarketDataArg
            {
                Time = snap.Time,
                Price = Instrument.FromTicks(c.PriceTicks),
                Volume = c.Size,
                DataType = c.Side == BookSide.Bid ? MarketDataType.Bid : MarketDataType.Ask,
                Security = Instrument.Symbol,
            }).ToList();
            foreach (var slot in Indicators.Where(s => s.WantsDepth && !s.NeedsRecalc))
                Safe(slot, () => slot.Indicator.HostMarketDepths(args));
        }

        if (bestChanged && Indicators.Any(s => s.WantsBestBidAsk))
        {
            var bid = new MarketDataArg { Time = snap.Time, Price = Instrument.FromTicks(snap.BestBidTicks), Volume = snap.BestBidSize, DataType = MarketDataType.Bid };
            var ask = new MarketDataArg { Time = snap.Time, Price = Instrument.FromTicks(snap.BestAskTicks), Volume = snap.BestAskSize, DataType = MarketDataType.Ask };
            foreach (var slot in Indicators.Where(s => s.WantsBestBidAsk && !s.NeedsRecalc))
            {
                Safe(slot, () => slot.Indicator.HostBestBidAsk(bid));
                Safe(slot, () => slot.Indicator.HostBestBidAsk(ask));
            }
        }

        Invalidate();
    }

    private IEnumerable<MarketDataArg> DepthSnapshotArgs()
    {
        var snap = Data.Depth;
        foreach (var b in snap.Bids)
            yield return new MarketDataArg { Price = Instrument.FromTicks(b.PriceTicks), Volume = b.Size, DataType = MarketDataType.Bid, Time = snap.Time };
        foreach (var a in snap.Asks)
            yield return new MarketDataArg { Price = Instrument.FromTicks(a.PriceTicks), Volume = a.Size, DataType = MarketDataType.Ask, Time = snap.Time };
    }

    private decimal SumDepth(BookSide side)
    {
        decimal s = 0;
        foreach (var l in side == BookSide.Bid ? Data.Depth.Bids : Data.Depth.Asks) s += l.Size;
        return s;
    }

    // =================================================================== accumulated trades

    public const int MaxCumulativeTicks = 500_000;

    private List<CumulativeTrade> BuildCumulativeTrades(DateTime from, DateTime to)
    {
        var res = new List<CumulativeTrade>();
        var store = Data.Trades;
        // Runs on the UI thread and allocates per tick: a year of ticks froze the app. Only the newest ticks are built.
        // ponytail: fixed cap; make it a setting if someone needs older bubbles.
        int i = Math.Max(store.LowerBound(from), store.Count - MaxCumulativeTicks);
        CumulativeTrade? cur = null;
        long curTime = 0;
        for (; i < store.Count; i++)
        {
            long t = store.GetTimeTicks(i);
            if (to != DateTime.MaxValue && t > to.Ticks) break;
            if (store.GetId(i) < 0) continue; // history built from candles: these are not real trades
            var side = store.GetSide(i);
            var dir = side == TradeSide.Buy ? TradeDirection.Buy : side == TradeSide.Sell ? TradeDirection.Sell : TradeDirection.Between;
            var price = store.GetPrice(i);
            var vol = store.GetVolume(i);
            if (cur == null || t / TimeSpan.TicksPerMillisecond != curTime || cur.Direction != dir)
            {
                if (cur != null) cur.State = CumulativeTradeStates.Finished;
                cur = new CumulativeTrade { Time = new DateTime(t, DateTimeKind.Utc), FirstPrice = price, Lastprice = price, Direction = dir };
                curTime = t / TimeSpan.TicksPerMillisecond;
                res.Add(cur);
            }

            cur.Lastprice = price;
            cur.Volume += vol;
            cur.Ticks.Add(new MarketDataArg { Time = new DateTime(t, DateTimeKind.Utc), Price = price, Volume = vol, Direction = dir, DataType = MarketDataType.Trade });
        }

        _openCumulative = cur;
        return res;
    }

    private void FeedCumulative(List<MarketDataArg> ticks)
    {
        var slots = Indicators.Where(s => s.WantsCumulative && !s.NeedsRecalc).ToList();
        foreach (var tk in ticks)
        {
            var cur = _openCumulative;
            bool same = cur != null && cur.Direction == tk.Direction &&
                        cur.Time.Ticks / TimeSpan.TicksPerMillisecond == tk.Time.Ticks / TimeSpan.TicksPerMillisecond;
            if (same)
            {
                cur!.Lastprice = tk.Price;
                cur.Volume += tk.Volume;
                cur.Ticks.Add(tk);
                foreach (var s in slots) Safe(s, () => s.Indicator.HostUpdateCumulativeTrade(cur));
            }
            else
            {
                if (cur != null) cur.State = CumulativeTradeStates.Finished;
                var n = new CumulativeTrade { Time = tk.Time, FirstPrice = tk.Price, Lastprice = tk.Price, Volume = tk.Volume, Direction = tk.Direction };
                n.Ticks.Add(tk);
                _openCumulative = n;
                foreach (var s in slots) Safe(s, () => s.Indicator.HostCumulativeTrade(n));
            }
        }
    }

    // =================================================================== lines until touched

    private void UpdateLinesTillTouch(Indicator ind, int fromBar)
    {
        if (ind.HorizontalLinesTillTouch.Count == 0) return;
        int last = Series.Count - 1;
        foreach (var line in ind.HorizontalLinesTillTouch)
        {
            if (line.Finished) continue;
            long rowTicks = Instrument.ToTicks(line.FirstPrice);
            int start = Math.Max(line.FirstBar + 1, Math.Min(fromBar, line.SecondBar));
            line.SecondBar = Math.Max(line.SecondBar, line.FirstBar);
            for (int b = Math.Max(start, line.FirstBar + 1); b <= last; b++)
            {
                var c = Series[b];
                line.SecondBar = b;
                if (c.LowTicks <= rowTicks && c.HighTicks >= rowTicks)
                {
                    line.Finished = true;
                    break;
                }

                if (line.Length > 0 && b - line.FirstBar >= line.Length)
                {
                    line.Finished = true;
                    break;
                }
            }
        }
    }

    // =================================================================== IIndicatorHost

    public int BarCount => Series.Count;

    public IndicatorCandle GetCandle(int bar) => _wrappers[Math.Clamp(bar, 0, _wrappers.Count - 1)];

    public InstrumentInfo InstrumentInfo => _instrumentInfo;
    public ChartInfo ChartInfo => _chartInfo;
    public MarketDepthInfo MarketDepthInfo => _depthInfo;
    public TradingManager TradingManager => Trading!;
    public TradingStatisticsProvider TradingStatisticsProvider => Statistics;
    public int FirstVisibleBar => LastLayout?.FirstVisible ?? Math.Max(0, Series.Count - 50);
    public int LastVisibleBar => LastLayout?.LastVisible ?? Math.Max(0, Series.Count - 1);
    public int CandlesCount => Series.Count;

    public bool IsNewSession(DateTime previous, DateTime current) => Series.Session.IsNewSession(previous, current);
    public bool IsNewWeek(DateTime previous, DateTime current) => Series.Session.IsNewWeek(previous, current);
    public bool IsNewMonth(DateTime previous, DateTime current) => Series.Session.IsNewMonth(previous, current);

    public bool IsNewSession(int bar) => bar > 0 && bar < Series.Count && Series.Session.IsNewSession(Series[bar - 1].Time, Series[bar].Time);
    public bool IsNewWeek(int bar) => bar > 0 && bar < Series.Count && Series.Session.IsNewWeek(Series[bar - 1].Time, Series[bar].Time);
    public bool IsNewMonth(int bar) => bar > 0 && bar < Series.Count && Series.Session.IsNewMonth(Series[bar - 1].Time, Series[bar].Time);

    public IChartContainer GetContainer(Indicator indicator)
    {
        var layout = LastLayout;
        if (layout == null) return EmptyContainer.Instance;
        var slot = Indicators.FirstOrDefault(s => s.Indicator == indicator);
        if (slot?.Panel == null) return layout.Main;
        return layout.Panels.FirstOrDefault(p => p.SubPanel == slot.Panel) ?? (IChartContainer)layout.Main;
    }

    public void RequestRecalculate(Indicator indicator)
    {
        var slot = Indicators.FirstOrDefault(s => s.Indicator == indicator);
        if (slot != null) slot.NeedsRecalc = true;
        Invalidate();
    }

    public void RequestRedraw() => Invalidate();

    public void AddAlert(Indicator source, string soundFile, string instrument, string message, Color background, Color foreground) =>
        AlertRaised?.Invoke(new AlertInfo(DateTime.Now, instrument, message, soundFile, background, foreground, source?.Name ?? ""));

    public void BeginInvoke(Action action) => _invokes.Enqueue(action);

    public void SubscribeTimer(Indicator indicator, TimeSpan interval, Action action)
    {
        if (interval < TimeSpan.FromMilliseconds(10)) interval = TimeSpan.FromMilliseconds(10);
        _timers.Add((indicator, interval, action, DateTime.UtcNow + interval));
    }

    public void UnsubscribeTimer(Indicator indicator, TimeSpan interval, Action action) =>
        _timers.RemoveAll(t => t.Ind == indicator && t.Action == action);

    public void RequestCumulativeTrades(Indicator indicator, CumulativeTradesRequest request)
    {
        var slot = Indicators.FirstOrDefault(s => s.Indicator == indicator || ReferenceEquals(s.Indicator, indicator));
        slot?.PendingCumulative.Add(request);
    }

    // ----------------------------------------------------------------- fixed profiles (day, week, month…)

    private readonly List<(Indicator Indicator, FixedProfilePeriods Period)> _fixedProfileSubs = new();
    private int _fixedProfileBars = -1;

    public void RequestFixedProfile(Indicator indicator, FixedProfileRequest request)
    {
        if (!_fixedProfileSubs.Contains((indicator, request.Period))) _fixedProfileSubs.Add((indicator, request.Period));
        var p = request.Period;
        BeginInvoke(() => RespondFixedProfile(indicator, p));
    }

    /// <summary>Resends the profiles of the periods in progress when a new candle shows up.</summary>
    private void RefreshFixedProfiles()
    {
        if (_fixedProfileSubs.Count == 0 || Series.Count == _fixedProfileBars) return;
        _fixedProfileBars = Series.Count;
        foreach (var (ind, period) in _fixedProfileSubs.ToList())
        {
            if (Indicators.All(s => !ReferenceEquals(s.Indicator, ind)))
            {
                _fixedProfileSubs.Remove((ind, period));
                continue;
            }

            RespondFixedProfile(ind, period);
        }
    }

    private void RespondFixedProfile(Indicator indicator, FixedProfilePeriods period)
    {
        var range = FixedProfileRange(period);
        if (range is not { } r) return;
        var store = Data.Trades;
        int first = Series[r.First].FirstTradeIndex, last = Series[r.Last].LastTradeIndex;
        if (first < 0 || last < first || last >= store.Count) return;
        var origin = new CandleSeries(Instrument, Period, 1, Series.Session);
        var scaled = new TradingHub.Data.Candle { Time = Series[r.First].Time };
        var exact = new TradingHub.Data.Candle { Time = Series[r.First].Time };
        for (int i = first; i <= last; i++)
        {
            long price = store.GetPriceTicks(i);
            decimal vol = store.GetVolume(i);
            var side = store.GetSide(i);
            long t = store.GetTimeTicks(i);
            scaled.AddTrade(price, Series.RowOf(price), vol, side, t, i);
            exact.AddTrade(price, price, vol, side, t, i);
        }

        try
        {
            indicator.HostFixedProfileResponse(new IndicatorCandle(scaled, Series), new IndicatorCandle(exact, origin), period);
        }
        catch (Exception ex)
        {
            indicator.LogWarn($"Error in the fixed profile of {indicator.Name}: {ex.Message}");
        }
    }

    /// <summary>Candles (first and last) that make up the requested period.</summary>
    private (int First, int Last)? FixedProfileRange(FixedProfilePeriods period)
    {
        int n = Series.Count;
        if (n == 0) return null;
        var session = Series.Session;
        Func<DateTime, DateTime, bool> isNew = period switch
        {
            FixedProfilePeriods.CurrentWeek or FixedProfilePeriods.LastWeek => session.IsNewWeek,
            FixedProfilePeriods.CurrentMonth or FixedProfilePeriods.LastMonth => session.IsNewMonth,
            FixedProfilePeriods.Contract => (_, _) => false,
            _ => session.IsNewSession,
        };

        // start of the period in progress
        int start = n - 1;
        while (start > 0 && !isNew(Series[start - 1].Time, Series[start].Time)) start--;
        bool previous = period is FixedProfilePeriods.LastDay or FixedProfilePeriods.LastWeek or FixedProfilePeriods.LastMonth;
        if (!previous) return (start, n - 1);
        if (start == 0) return null;
        int end = start - 1, pstart = end;
        while (pstart > 0 && !isNew(Series[pstart - 1].Time, Series[pstart].Time)) pstart--;
        return (pstart, end);
    }

    public void Dispose()
    {
        Data.TradesAppended -= OnTradesAppended;
        Data.HistoryLoaded -= OnHistoryLoaded;
        Data.DepthUpdated -= OnDepthUpdated;
        foreach (var s in Indicators.ToArray())
        {
            try { s.Indicator.HostDetach(); }
            catch { /* ignorar */ }
        }

        Indicators.Clear();
    }

    /// <summary>Empty container until the first draw.</summary>
    private sealed class EmptyContainer : IChartContainer
    {
        public static readonly EmptyContainer Instance = new();
        public Rectangle Region => Rectangle.Empty;
        public Rectangle RelativeRegion => Rectangle.Empty;
        public int BarsWidth => 1;
        public int BarSpacing => 0;
        public decimal Step => 1;
        public decimal PriceRowHeight => 1;
        public int TotalBars => 0;
        public decimal High => 1;
        public decimal Low => 0;
        public decimal Minimum => 0;
        public decimal Maximum => 1;
        public int FirstVisibleBarNumber => 0;
        public int LastVisibleBarNumber => 0;
        public int GetXByBar(int bar, bool isStartOfBar = true) => 0;
        public int GetYByPrice(decimal price, bool isStartOfPrice = true) => 0;
        public decimal GetPriceByY(int y) => 0;
        public int GetBarByX(int x) => 0;
        public int GetYByValue(decimal value) => 0;
        public decimal GetValueByY(int y) => 0;
    }
}
