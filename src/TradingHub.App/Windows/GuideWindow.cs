using Avalonia;
using Avalonia.Controls;
using TradingHub.App.Controls;

namespace TradingHub.App.Windows;

/// <summary>The Insights guide (docs/insights.md, embedded in the app) in a window of its own.</summary>
internal sealed class GuideWindow : Window
{
    public GuideWindow()
    {
        Title = "Insights guide";
        Width = 640;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        string text;
        try
        {
            using var s = typeof(GuideWindow).Assembly.GetManifestResourceStream("insights.md");
            text = s == null ? "The guide is not part of this build." : new StreamReader(s).ReadToEnd();
        }
        catch (Exception ex)
        {
            text = "The guide could not be read: " + ex.Message;
        }

        var md = new MarkdownView { Markdown = text };
        Content = new ScrollViewer { Content = new Border { Child = md, Padding = new Thickness(20) } };
    }
}
