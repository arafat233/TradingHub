using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using TradingHub.App.Services;
using TradingHub.App.Windows;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TradingHub.UiSnapshot")]

namespace TradingHub.App;

public sealed class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    public override void Initialize()
    {
        Name = TradingHub.Common.Product.Name;
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://TradingHub/")) { Source = new Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml") });
        Styles.Add(new StyleInclude(new Uri("avares://TradingHub/")) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") });
        Styles.Add(new Material.Icons.Avalonia.MaterialIconStyles(null));
        Styles.Add(new StyleInclude(new Uri("avares://TradingHub/")) { Source = new Uri("avares://TradingHub/Styles/TradingHubTheme.axaml") });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        TradingHub.Data.InsightsState.EdgeFolder = System.IO.Path.Combine(AppPaths.Root, "Insights"); // the edge meters: how the Insights panel's own calls turned out
        Services = new AppServices();
        foreach (var kv in Services.Settings.MyLevels) TradingHub.Data.MyLevels.Publish(kv.Key, kv.Value); // the chart lines read them without the panel
        Briefing.Start(Services);
        LseBackfill.Start(Services);
        BybitBackfill.Start(Services);
        NightlyResearch.Start(Services);
        TelegramBot.Start(Services);
        Services.Journal.Changed += id => ProcessCoach.OnFill(Services, id);
        _ = Task.Run(() => DailyRecap.AddCommentAsync(Services));
        TradingHub.App.Services.AppTheme.Apply(Services.Settings.ThemeMode);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var main = new MainWindow(Services);
            desktop.MainWindow = main;
            desktop.ShutdownRequested += (_, _) => Services.Shutdown();
            // the icon in the Mac menu bar (closing the window leaves Trading Hub running there)
            TradingHub.App.Services.MenuBar.Install(this, Services, main);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
