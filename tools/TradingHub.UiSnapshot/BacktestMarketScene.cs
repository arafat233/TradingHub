// "market" scene: the backtest by market (range, mixed, up or down trend and volatility) and running it
// again only in the chosen conditions, with the context measured in the moment (as it is live).
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TradingHub.App;
using TradingHub.App.Controls;
using TradingHub.App.Services;
using TradingHub.App.Windows;
using TradingHub.Strategies;

internal static class BacktestMarketScene
{
    private static readonly List<string> Failures = new();

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"{(ok ? "OK " : "FAIL")}  {what}");
        if (!ok) Failures.Add(what);
    }

    private const string Code = """
        using TradingHub.Strategies;

        [Strategy("Test by market regime")]
        public class BtMarket : Strategy
        {
            private int _entry = -100;

            protected override void OnBar(Bar bar)
            {
                if (IsFlat && CurrentBar % 6 == 0)
                {
                    Buy(1);
                    _entry = CurrentBar;
                }
                else if (IsLong && CurrentBar - _entry >= 4) ClosePosition();
            }
        }
        """;

    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// With no stored BTC history (for example, the code freshly cloned from the repository): 45 synthetic days are built
    /// with trends, ranges and changing volatility, in the scene's temp folder.
    /// </summary>
    private static void SyntheticBtcIfMissing()
    {
        var folder = Path.Combine(AppPaths.Ticks, "binance-futures", "BTCUSDT");
        if (Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.vtk").Count(f => new FileInfo(f).Length > 500_000) >= 12) return;
        var inst = new TradingHub.Market.Instrument { Symbol = "BTCUSDT", FeedId = "binance-futures", Exchange = "binance-futures", TickSize = 0.1m, VolumeStep = 0.001m };
        var rnd = new Random(7);
        double price = 60_000;
        long id = 1;
        const int perDay = 120_000;
        var today = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        for (var d = today.AddDays(-44); d <= today; d = d.AddDays(1))
        {
            int week = d.DayNumber / 7 % 4;
            double drift = week switch { 0 => 0.025, 1 => -0.025, _ => 0 } / perDay, sigma = (week == 3 ? 0.045 : 0.02) / Math.Sqrt(perDay);
            var start = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var trades = new List<TradingHub.Market.Trade>(perDay);
            double last = price;
            for (int i = 0; i < perDay; i++)
            {
                double z = Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
                price *= 1 + drift + sigma * z;
                decimal px = Math.Round((decimal)price / inst.TickSize) * inst.TickSize;
                var side = price > last ? TradingHub.Market.TradeSide.Buy : price < last ? TradingHub.Market.TradeSide.Sell : (rnd.Next(2) == 0 ? TradingHub.Market.TradeSide.Buy : TradingHub.Market.TradeSide.Sell);
                last = price;
                trades.Add(new TradingHub.Market.Trade(id++, start.AddTicks((long)(TimeSpan.TicksPerDay * (i + rnd.NextDouble() * 0.9) / perDay)), px, Math.Round((decimal)(0.001 + rnd.NextDouble() * rnd.NextDouble() * 0.8), 3), side));
            }

            TradingHub.Data.HistoricalData.WriteDay(AppPaths.Ticks, inst, d, trades, replace: true);
        }
    }

    public static int Run(MainWindow window, Action<double> pump, string outDir)
    {
        Check(!AppPaths.Strategies.Contains("/Documents/", StringComparison.Ordinal), "the strategies are a temporary copy");
        SyntheticBtcIfMissing();
        File.WriteAllText(Path.Combine(AppPaths.Strategies, "BtMarket.cs"), Code);
        window.Navigate("strategies");
        pump(1.5);
        var view = window.Page("strategies")!;
        var t = view.GetType();
        var catalog = App.Services.Strategies;
        catalog.Refresh();
        var file = catalog.Files.First(f => f.FileName == "BtMarket");
        var sel = (Task)t.GetMethod("SelectAsync", Any)!.Invoke(view, new object[] { file })!;
        for (int i = 0; i < 200 && !sel.IsCompleted; i++) pump(0.05);
        for (int i = 0; i < 100 && file.Type == null; i++) pump(0.1);
        t.GetMethod("ShowTab", Any)!.Invoke(view, new object[] { "backtest" });
        pump(0.5);

        // data: BTC with stored history (ten full days, plus the previous month for the context)
        var source = (ComboBox)t.GetField("_dataSource", Any)!.GetValue(view)!;
        var btc = source.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is TradingHub.Data.SymbolData { FeedId: "binance-futures", Symbol: "BTCUSDT" });
        if (btc?.Tag is not TradingHub.Data.SymbolData data)
        {
            Check(false, "there is BTCUSDT history for the backtest");
            return Failures.Count;
        }

        source.SelectedItem = btc;
        var days = data.Days.Where(d => d.Bytes > 500_000).Select(d => d.Day).OrderBy(d => d).ToList();
        var last = days.Where(d => days.Contains(d.AddDays(1))).LastOrDefault();
        ((TextBox)t.GetField("_period", Any)!.GetValue(view)!).Text = "M15";
        ((CalendarDatePicker)t.GetField("_from", Any)!.GetValue(view)!).SelectedDate = last.AddDays(-9).ToDateTime(TimeOnly.MinValue);
        ((CalendarDatePicker)t.GetField("_to", Any)!.GetValue(view)!).SelectedDate = last.ToDateTime(TimeOnly.MinValue);

        BacktestResult? Result() => (BacktestResult?)t.GetField("_lastResult", Any)!.GetValue(view);
        void RunAndWait(Func<Task> start)
        {
            var before = Result();
            var task = start();
            var limit = DateTime.UtcNow.AddMinutes(4);
            while (!task.IsCompleted && DateTime.UtcNow < limit) pump(0.1);
            for (int i = 0; i < 600 && ReferenceEquals(Result(), before); i++) pump(0.1);
            pump(0.8);
        }

        // with no context filter (an earlier run of the scene may have left one stored in the strategy)
        var noFilter = new Dictionary<string, object?>
        {
            [nameof(Strategy.RegimeFilter)] = RegimeFilter.All, [nameof(Strategy.MinVolatilityRank)] = 0, [nameof(Strategy.MaxVolatilityRank)] = 100,
        };
        RunAndWait(() =>
        {
            t.GetMethod("ApplyParameters", Any)!.Invoke(view, new object[] { noFilter });
            return Task.CompletedTask;
        });
        var all = Result();
        Check(all is { Ok: true } && all.Trades.Count >= 10, $"it runs the backtest ({all?.Trades.Count} trades)");
        if (all == null) return Failures.Count;
        Check(all.Trades.All(x => x.EntryTrend != null),
            $"with the previous month, every trade (the first ones included) knows what the market looked like ({all.Trades.Count(x => x.EntryTrend == null)} with no context)");

        var tabs = (Ui.Segmented<string>)t.GetField("_resultTabs", Any)!.GetValue(view)!;
        tabs.Choose("market");
        pump(1);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "backtest_market.png"));
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text ?? "").ToList();
        Check(texts.Contains("Range") && texts.Contains("Mixed") && texts.Contains("Up trend") && texts.Contains("Down trend") &&
              texts.Any(x => x.EndsWith(" volatility", StringComparison.Ordinal)) && texts.Contains("HOW IT DID BY MARKET REGIME"),
            "the “By market regime” tab shows how it did in each condition and volatility");

        // range only: a fresh backtest with the filter (not a trimmed version of the previous one)
        var picker = (MarketConditionPicker)t.GetField("_btConditions", Any)!.GetValue(view)!;
        picker.Load(RegimeFilter.RangeOnly, 0, 100);
        var rerun = view.GetVisualDescendants().OfType<Button>().First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Re-run only in these conditions"));
        RunAndWait(() =>
        {
            rerun.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        });
        var only = Result();
        Check(only != null && !ReferenceEquals(only, all) && Equals(only.Parameters[nameof(Strategy.RegimeFilter)], RegimeFilter.RangeOnly),
            "it re-runs the backtest with the “range only” filter");
        if (only != null)
        {
            Check(only.Trades.Count > 0 && only.Trades.Count < all.Trades.Count && only.Trades.All(x => x.EntryTrend <= Robustness.RangeTrend),
                $"it only enters when the market it saw at that moment was a range ({only.Trades.Count} of {all.Trades.Count} trades; max efficiency {only.Trades.Max(x => x.EntryTrend ?? 0):0.00})");
            // it is not a trim: with the filter the trades change (another entry when the previous one was not taken)
            var cut = all.Trades.Where(x => x.EntryTrend <= Robustness.RangeTrend).Select(x => x.EntryTime).ToHashSet();
            Check(only.Trades.Any(x => !cut.Contains(x.EntryTime)) || only.Trades.Count == cut.Count,
                "it is a new run (the strategy decides with the filter on each candle), not the same trades filtered");
        }

        pump(0.5);
        window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "backtest_market_filtered.png"));
        texts = view.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text ?? "").ToList();
        Check(texts.Contains("In every condition") && texts.Any(x => x.StartsWith("With the filter (range only)", StringComparison.Ordinal)),
            "it compares every condition against only the chosen ones");

        // and it goes back to all of them
        var reset = view.GetVisualDescendants().OfType<Button>().First(b => b.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == "Run in every condition"));
        RunAndWait(() =>
        {
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return Task.CompletedTask;
        });
        Check(Result() is { } back && Equals(back.Parameters[nameof(Strategy.RegimeFilter)], RegimeFilter.All) && back.Trades.Count == all.Trades.Count,
            "“Run in every condition” goes back to the unfiltered run");

        // the optimizer also tries candle periods, like one more parameter
        t.GetMethod("ShowTab", Any)!.Invoke(view, new object[] { "optimize" });
        pump(0.8);
        var periodCheck = view.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Content as string == "Candle period" && c.IsEffectivelyVisible);
        var comboText = (TextBlock)t.GetField("_comboCount", Any)!.GetValue(view)!;
        string before = comboText.Text ?? "";
        Check(periodCheck != null, "the optimizer has “Candle period” with the periods to tick");
        if (periodCheck != null)
        {
            periodCheck.IsChecked = true;
            pump(0.3);
            var chips = view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(b => b.Content is "M15" or "H1" && b.IsEffectivelyVisible).ToList();
            foreach (var c in chips) c.IsChecked = true;
            pump(0.3);
            var ranges = (System.Collections.IList)t.GetField("_state", Any)!.GetValue(view)!.GetType().GetProperty("Ranges")!.GetValue(t.GetField("_state", Any)!.GetValue(view))!;
            var period = ranges.OfType<ParameterRange>().FirstOrDefault(r => r.IsPeriod);
            Check(period is { Enabled: true } && period.Choices.SequenceEqual(new[] { "M15", "H1" }) && comboText.Text != before,
                $"ticking M15 and H1 tests both ({string.Join(", ", period?.Choices ?? new())}; {before} → {comboText.Text})");
            window.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "optimize_periods.png"));

            // "Use in Backtest" with a row that picked H1 puts that period in the backtest
            var pars = new Dictionary<string, object?>(((Dictionary<string, object?>)t.GetMethod("BaseParameters", Any)!.Invoke(view, null)!)) { [ParameterRange.PeriodName] = "H1" };
            RunAndWait(() =>
            {
                t.GetMethod("ApplyParameters", Any)!.Invoke(view, new object[] { pars });
                return Task.CompletedTask;
            });
            Check(((TextBox)t.GetField("_period", Any)!.GetValue(view)!).Text == "H1" && Result()?.Settings?.Period == "H1",
                $"“Use in Backtest” on a row with the H1 period runs the backtest on H1 ({Result()?.Settings?.Period})");
            periodCheck.IsChecked = false;
            pump(0.3);
        }

        // favorites: the star in the strategy list moves it up to "Favorites" (and it is stored in the settings)
        var menu = (Flyout)t.GetField("_strategyMenu", Any)!.GetValue(view)!;
        var pickerButton = (Button)t.GetField("_strategyPicker", Any)!.GetValue(view)!;
        menu.ShowAt(pickerButton);
        pump(0.6);
        int Rows() => (menu.Content as Control)?.GetVisualDescendants().OfType<TextBlock>().Count(x => x.Text == file.DisplayName) ?? 0;
        Button? Star() => (menu.Content as Control)?.GetVisualDescendants().OfType<DockPanel>()
            .FirstOrDefault(d => d.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == file.DisplayName))?.Children.OfType<Button>().FirstOrDefault();
        App.Services.Settings.FavoriteStrategies.Remove(file.FileName);
        int rowsBefore = Rows();
        Star()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(0.4);
        Check(App.Services.Settings.FavoriteStrategies.Contains(file.FileName) && Rows() == rowsBefore + 1 && menu.IsOpen,
            $"the star puts the strategy in “Favourites”, above everything, without closing the list ({rowsBefore} → {Rows()} rows)");
        if (menu.Content is Control flyoutContent) TopLevel.GetTopLevel(flyoutContent)?.CaptureRenderedFrame()?.Save(Path.Combine(outDir, "strategies_favorites.png"));
        Star()?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pump(0.4);
        Check(!App.Services.Settings.FavoriteStrategies.Contains(file.FileName) && Rows() == rowsBefore, "clicking it again removes it from favourites");
        menu.Hide();
        pump(0.3);

        Console.WriteLine(Failures.Count == 0 ? "BY MARKET REGIME: all OK" : $"BY MARKET REGIME: {Failures.Count} failure(s)");
        return Failures.Count;
    }
}
