using Avalonia;
using Avalonia.Input;
using SkiaSharp;
using TradingHub.Charting.Dom;
using TradingHub.Trading;

namespace TradingHub.App.Controls;

/// <summary>DOM (price ladder) you can trade with the mouse.</summary>
public sealed class DomView : SkiaView
{
    private readonly DomRenderer _renderer = new();
    private float? _mouseY;
    private bool _dragging;

    public DomModel? Dom { get; set; }

    /// <summary>
    /// Order request: column, price and whether it is a cancellation (right click / Alt).
    /// If nobody is listening, the DOM trades straight on the account.
    /// </summary>
    public event Action<DomColumn, decimal, bool>? OrderRequested;

    /// <summary>One of your own orders dragged to another price.</summary>
    public event Action<Order, decimal>? OrderMoved;

    /// <summary>Message for the last action (for the status bar).</summary>
    public event Action<string>? Message;

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        if (Dom == null)
        {
            canvas.Clear(TradingHub.Charting.ChartTheme.Base.BackgroundTop);
            return;
        }

        _renderer.Render(canvas, Dom, width, height, _mouseY);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Dom == null) return;
        var p = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        bool cancel = props.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        // dragging one of your own orders
        if (!cancel && _renderer.OrderAt(Dom, (float)p.X, (float)p.Y) is { } order)
        {
            Dom.DragOrder = order;
            Dom.DragPrice = order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price;
            _dragging = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
            return;
        }

        // with "double click to trade" a single click sends no orders (cancelling still works)
        if (!cancel && Dom.Settings.DoubleClickToTrade && e.ClickCount < 2) return;

        if (OrderRequested != null)
        {
            if (_renderer.HitTest(Dom, (float)p.X, (float)p.Y) is { } hit) OrderRequested(hit.Column, hit.Price, cancel);
        }
        else
        {
            var msg = _renderer.Click(Dom, (float)p.X, (float)p.Y, props.IsRightButtonPressed, e.KeyModifiers.HasFlag(KeyModifiers.Alt));
            if (msg != null) Message?.Invoke(msg);
        }

        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        _mouseY = (float)p.Y;
        if (_dragging && Dom != null && _renderer.HitTest(Dom, 1, (float)p.Y) is { } hit) Dom.DragPrice = hit.Price;
        Cursor = _dragging || (Dom != null && _renderer.OrderAt(Dom, (float)p.X, (float)p.Y) != null) ? new Cursor(StandardCursorType.SizeNorthSouth) : Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging || Dom?.DragOrder == null) return;
        if (_renderer.HitTest(Dom, 1, (float)e.GetPosition(this).Y) is { } drop) Dom.DragPrice = drop.Price;
        var order = Dom.DragOrder;
        decimal from = order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price;
        decimal to = Dom.DragPrice;
        Dom.DragOrder = null;
        _dragging = false;
        e.Pointer.Capture(null);
        if (to > 0 && to != from)
        {
            if (OrderMoved != null) OrderMoved(order, to);
            else Dom.Broker?.Modify(order, to);
        }

        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _mouseY = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Dom == null) return;
        _renderer.Scroll(Dom, (int)Math.Round(e.Delta.Y * 3));
        e.Handled = true;
        InvalidateVisual();
    }

    public void Recenter()
    {
        if (Dom == null) return;
        Dom.AutoCenter = Dom.Settings.AutoCenter;
        Dom.CenterTicks = 0;
        InvalidateVisual();
    }

    /// <summary>Applies a new configuration (columns, sizes, colors).</summary>
    public void ApplySettings(DomSettings settings)
    {
        if (Dom == null) return;
        Dom.Settings = settings;
        Dom.AutoCenter = settings.AutoCenter;
        InvalidateVisual();
    }
}

/// <summary>Time and sales tape (Time &amp; Sales).</summary>
public sealed class TapeView : SkiaView
{
    private readonly TapeRenderer _renderer = new();

    public TapeModel? Tape { get; set; }

    protected override void Draw(SKCanvas canvas, float width, float height)
    {
        if (Tape == null)
        {
            canvas.Clear(TradingHub.Charting.ChartTheme.Base.BackgroundTop);
            return;
        }

        _renderer.Render(canvas, Tape, width, height);
    }

    // wheel or trackpad: downwards shows older trades (and it holds still); upwards returns to the latest
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Tape == null) return;
        _wheel += e.Delta.Y * 3;
        int rows = (int)Math.Truncate(_wheel);
        if (rows == 0) return;
        _wheel -= rows;
        TapeRenderer.Scroll(Tape, -rows);
        e.Handled = true;
        InvalidateVisual();
    }

    private double _wheel;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Tape?.Anchor == null || e.GetPosition(this).Y > TapeRenderer.HeaderHeight) return;
        Tape.Anchor = null;
        InvalidateVisual();
    }

    /// <summary>Returns to the latest.</summary>
    public void Live()
    {
        if (Tape == null) return;
        Tape.Anchor = null;
        InvalidateVisual();
    }
}
