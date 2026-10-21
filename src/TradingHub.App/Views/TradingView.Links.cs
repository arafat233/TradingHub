using TradingHub.App.Services;
using TradingHub.Common.Logging;
using TradingHub.Data;

namespace TradingHub.App.Views;

/// <summary>Charts linked by color: shared instrument, period and crosshair.</summary>
public sealed partial class TradingView
{
    private bool _linkSync;

    /// <summary>Charts (tabs and separate windows) of a color group (0 = none).</summary>
    internal IEnumerable<ChartTab> ChartsInGroup(int group) =>
        group == 0 ? Enumerable.Empty<ChartTab>() : AllCharts.Where(c => c.LinkGroup == group);

    /// <summary>Changes a chart's group; on joining it takes the group's instrument (and period).</summary>
    internal async void SetLinkGroup(ChartTab chart, int group)
    {
        try
        {
            if (chart.LinkGroup == group) return;
            var leader = ChartsInGroup(group).FirstOrDefault(c => !ReferenceEquals(c, chart) && c.Model != null);
            chart.LinkGroup = group;
            RefreshLinkButtons();
            if (group == 0)
            {
                _status("Chart unlinked");
                return;
            }

            string name = ChartLinks.NameOf(group).ToLowerInvariant();
            if (leader?.Model == null || chart.Model == null)
            {
                _status($"Group {name}: set the same color on another chart to sync them");
                return;
            }

            var s = _svc.Settings;
            _linkSync = true;
            try
            {
                if (s.LinkSymbol && !SameInstrument(chart, leader))
                    await OpenInTabAsync(chart, leader.FeedId, leader.Symbol, chart.Model.Period, chart.Model.Settings.Clone());
                if (s.LinkPeriod && chart.Model != null && leader.Model != null && chart.Model.Period != leader.Model.Period)
                    ApplyPeriod(chart, leader.Model.Period);
            }
            finally
            {
                _linkSync = false;
            }

            var synced = Synced().ToList();
            string what = synced.Count switch
            {
                0 => "nothing (enable something in the color's menu)",
                1 => synced[0],
                _ => string.Join(", ", synced.Take(synced.Count - 1)) + " y " + synced[^1],
            };
            _status($"Linked to the {name} group: {what} synced between its charts");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private IEnumerable<string> Synced()
    {
        var s = _svc.Settings;
        if (s.LinkSymbol) yield return "the instrument";
        if (s.LinkPeriod) yield return "the period";
        if (s.LinkCrosshair) yield return "the crosshair";
    }

    private static bool SameInstrument(ChartTab a, ChartTab b) =>
        a.FeedId == b.FeedId && a.Symbol.Equals(b.Symbol, StringComparison.OrdinalIgnoreCase);

    /// <summary>A chart changed period from its toolbar or a shortcut: the ones in its group follow it.</summary>
    internal void OnPeriodChanged(ChartTab chart)
    {
        if (chart.Strategies.Count > 0) RestartStrategies(chart, instrumentChanged: false);
        if (_linkSync || chart.LinkGroup == 0 || !_svc.Settings.LinkPeriod || chart.Model == null) return;
        foreach (var other in ChartsInGroup(chart.LinkGroup).ToList())
        {
            if (!ReferenceEquals(other, chart) && other.Model != null && other.Model.Period != chart.Model.Period)
                ApplyPeriod(other, chart.Model.Period);
        }
    }

    private void ApplyPeriod(ChartTab chart, ChartPeriod period)
    {
        chart.Model!.SetPeriod(period);
        UpdateTitles(chart);
        if (ReferenceEquals(chart, _active)) _toolbar.Sync();
        chart.Window?.Toolbar.Sync();
    }

    /// <summary>A chart changed instrument: the ones in its group move to the same one (each keeping its period).</summary>
    private async Task PropagateSymbolAsync(ChartTab source)
    {
        if (_linkSync || source.LinkGroup == 0 || !_svc.Settings.LinkSymbol) return;
        var targets = ChartsInGroup(source.LinkGroup).Where(c => !ReferenceEquals(c, source) && !SameInstrument(c, source)).ToList();
        if (targets.Count == 0) return;
        _linkSync = true;
        try
        {
            foreach (var t in targets)
                await OpenInTabAsync(t, source.FeedId, source.Symbol, t.Model?.Period ?? ChartPeriod.M5, t.Model?.Settings.Clone());
        }
        finally
        {
            _linkSync = false;
        }
    }

    /// <summary>A chart's crosshair is shown on the others in its group (the price too, if it is the same instrument).</summary>
    private void OnCrosshair(ChartTab source, DateTime? time, double? price)
    {
        if (source.LinkGroup == 0) return;
        bool on = _svc.Settings.LinkCrosshair;
        foreach (var other in ChartsInGroup(source.LinkGroup))
        {
            if (ReferenceEquals(other, source)) continue;
            other.View.SetLinkedCrosshair(on ? time : null, on && SameInstrument(other, source) ? price : null);
        }
    }

    internal void RefreshLinkButtons()
    {
        foreach (var c in AllCharts) c.LinkButton?.Refresh();
        _toolbar.RefreshLink();
        foreach (var w in _floating) w.Toolbar.RefreshLink();
    }
}
