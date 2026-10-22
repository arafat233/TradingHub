using TradingHub.App.Windows;

namespace TradingHub.App.Views;

/// <summary>Liquidity heatmap windows: one per instrument (a second click brings the open one to the front).</summary>
public sealed partial class TradingView
{
    private readonly Dictionary<string, HeatmapWindow> _heatmaps = new();

    /// <summary>The open heatmap window of an instrument (for the headless scenes).</summary>
    internal Avalonia.Controls.Window? HeatmapWindowOf(TradingHub.Data.InstrumentData data) => _heatmaps.GetValueOrDefault(data.Instrument.Key);

    internal void OpenHeatmap(ChartTab? chart)
    {
        if (chart?.Model?.Data is not { } data) return;
        string key = data.Instrument.Key;
        if (_heatmaps.TryGetValue(key, out var open))
        {
            open.Activate();
            return;
        }

        var w = new HeatmapWindow(data, () => _svc.Settings.Save(), _svc.Settings.Heatmap);
        _heatmaps[key] = w;
        w.Closed += (_, _) => _heatmaps.Remove(key);
        w.Show();
    }
}
