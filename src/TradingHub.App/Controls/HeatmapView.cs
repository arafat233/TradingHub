using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using TradingHub.Charting.Heatmap;

namespace TradingHub.App.Controls;

/// <summary>
/// The liquidity heatmap. Wheel: how much time fits on the screen; Ctrl + wheel: the height of each price row;
/// drag on the picture: move the price range; drag on the price axis: zoom the rows (it stops following the price); double click: follow the price again.
/// </summary>
public sealed class HeatmapView : SkiaView
{
    private static readonly double[] Windows = { 0.5, 1, 2, 3, 5, 10, 15, 20, 30 };
    private readonly HeatmapRenderer _renderer = new();
    private float _dragY;
    private bool _dragging, _zooming;

    public HeatmapView(HeatmapModel model)
    {
        Model = model;
    }

    public HeatmapModel Model { get; }

    /// <summary>A setting changed with the mouse (the window refreshes its controls and saves).</summary>
    public event Action? SettingsChanged;

    protected override void Draw(SKCanvas canvas, float width, float height) => _renderer.Render(canvas, Model, width, height);

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        int dir = e.Delta.Y > 0 ? 1 : -1;
        var s = Model.Settings;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            s.RowPx = Math.Clamp(s.RowPx + dir, 2, 40);
        }
        else
        {
            // wheel up = zoom in = less time on the screen
            int i = Array.FindIndex(Windows, w => w >= s.WindowMinutes);
            if (i < 0) i = Windows.Length - 1;
            s.WindowMinutes = Windows[Math.Clamp(i - dir, 0, Windows.Length - 1)];
        }

        SettingsChanged?.Invoke();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2)
        {
            Model.Follow();
            InvalidateVisual();
            return;
        }

        _zooming = e.GetPosition(this).X > Model.HeatRect.Right && e.GetPosition(this).X < Model.HeatRect.Right + 62;
        _dragging = true;
        _dragY = (float)e.GetPosition(this).Y;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        Model.Hover = new SKPoint((float)pos.X, (float)pos.Y);
        InvalidateVisual();
        if (!_dragging) return;
        float y = (float)e.GetPosition(this).Y;
        if (_zooming)
        {
            // dragging up on the axis makes the rows taller (zoom in), 8 px of mouse per pixel of row
            int step = (int)((_dragY - y) / 8);
            if (step == 0) return;
            Model.Settings.RowPx = Math.Clamp(Model.Settings.RowPx + step, 2, 40);
            _dragY -= step * 8;
            SettingsChanged?.Invoke();
            InvalidateVisual();
            return;
        }

        int ticks = (int)((y - _dragY) / Model.TickPx);
        if (ticks == 0) return;
        Model.PanTicks(ticks); // dragging down shows higher prices (the picture follows the hand)
        _dragY += ticks * Model.TickPx;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Model.Hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }
}
