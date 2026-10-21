using TradingHub.Charting.Drawings;
using TradingHub.Charting.Rendering;
using TradingHub.Common.Logging;
using TradingHub.Rendering;
using TradingHub.Rendering.Control;
using TradingHub.Trading;

namespace TradingHub.Charting;

public enum PointerButton
{
    Left,
    Right,
    Middle,
}

[Flags]
public enum InputModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
    Command = 8,
}

/// <summary>
/// Interaction with the chart (independent of the UI): panning, zooming, scaling axes,
/// draw and move objects, and forward the mouse to the indicators.
/// </summary>
public sealed class ChartController
{
    private enum DragMode
    {
        None,
        Pan,
        PriceScale,
        TimeScale,
        CreateDrawing,
        MoveDrawing,
        PanelResize,
        MoveOrder,
        Bracket,
    }

    private readonly ChartModel _model;
    private readonly ChartRenderState _state;
    private DragMode _drag;
    private float _downX, _downY, _lastX, _lastY;
    private double _startScroll, _startTop, _startBottom, _startBarWidth;
    private ChartDrawing? _active;
    private int _activeHandle;
    private List<ChartPoint>? _startPoints;
    private SubPanel? _resizePanel;
    private Order? _dragOrder;

    public ChartController(ChartModel model, ChartRenderState state)
    {
        _model = model;
        _state = state;
    }

    /// <summary>Active drawing tool (null = normal cursor).</summary>
    public DrawingKind? ActiveTool { get; set; }

    /// <summary>Whether the tool stays active after drawing (if not, it goes back to the cursor).</summary>
    public bool StickyTool { get; set; }

    public ChartDrawing? Selected => _model.Drawings.FirstOrDefault(d => d.Selected);

    /// <summary>Suggested cursor for the current position.</summary>
    public StdCursor Cursor { get; private set; } = StdCursor.Cross;

    /// <summary>Default style of each kind of drawing (what the user saved as the default).</summary>
    public Func<DrawingKind, ChartDrawing?>? DrawingDefaults { get; set; }

    public event Action? ToolFinished;
    public event Action<ChartDrawing>? DrawingDoubleClicked;
    public event Action? DrawingsChanged;

    /// <summary>An indicator was hidden, shown or removed from the chart's legend.</summary>
    public event Action? IndicatorsChanged;

    /// <summary>An indicator's settings were requested from the legend (gear or double click on the name).</summary>
    public event Action<IndicatorSlot>? IndicatorSettingsRequested;

    /// <summary>Configure or remove a strategy from the chart, from the legend.</summary>
    public event Action<StrategyOverlay>? StrategySettingsRequested;

    public event Action<StrategyOverlay>? StrategyRemoveRequested;

    /// <summary>One of your own orders dragged to a new price (the view decides whether to confirm).</summary>
    public event Action<Order, decimal>? OrderDragged;

    /// <summary>A stop/target dragged from the position or a pending order was dropped.</summary>
    public event Action<BracketDrag, decimal>? BracketDropped;

    /// <summary>Stop/target drag zone under the mouse (buttons first, then the lines).</summary>
    public BracketHandle? HandleAt(float x, float y, bool buttonsOnly)
    {
        foreach (var h in _state.Handles)
            if (h.IsButton && h.Rect.Contains(x, y)) return h;
        if (buttonsOnly) return null;
        foreach (var h in _state.Handles)
            if (!h.IsButton && h.Rect.Contains(x, y)) return h;
        return null;
    }

    /// <summary>One of your own orders whose line is under the mouse (within 5 px).</summary>
    public Order? OrderAt(float x, float y)
    {
        var l = L;
        var acc = _model.Account;
        if (l == null || acc == null || !l.Main.Rect.Contains(x, y)) return null;
        Order? best = null;
        float bestD = 5;
        foreach (var o in acc.ActiveOrders)
        {
            decimal price = o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price;
            float d = Math.Abs(l.Main.Y(price) - y);
            if (d <= bestD)
            {
                bestD = d;
                best = o;
            }
        }

        return best;
    }

    private ChartLayout? L => _model.LastLayout;

    // ======================================================================= mouse

    public void MouseDown(float x, float y, PointerButton button, int clicks, InputModifiers mods)
    {
        var l = L;
        if (l == null) return;
        _downX = _lastX = x;
        _downY = _lastY = y;
        _state.MouseX = x;
        _state.MouseY = y;

        // price axis
        if (l.PriceAxis.Contains(x, y))
        {
            if (clicks >= 2)
            {
                _model.Viewport.AutoScale = true;
                _model.Invalidate();
                return;
            }

            _drag = DragMode.PriceScale;
            _startTop = l.Main.Top;
            _startBottom = l.Main.Bottom;
            return;
        }

        // time axis
        if (l.TimeAxis.Contains(x, y))
        {
            if (clicks >= 2)
            {
                _model.Viewport.ScrollBars = 0;
                _model.Invalidate();
                return;
            }

            _drag = DragMode.TimeScale;
            _startBarWidth = _model.Viewport.BarWidth;
            _startScroll = _model.Viewport.ScrollBars;
            return;
        }

        if (!l.Plot.Contains(x, y)) return;

        // button to jump back to the latest bars and indicator legend (eye, settings, remove, collapse)
        if (button == PointerButton.Left)
        {
            if (_state.GoLatestButton is { } gl && gl.Contains(x, y))
            {
                _model.Viewport.ScrollBars = 0;
                _model.Viewport.AutoScale = true;
                _model.Invalidate();
                return;
            }

            // the X of the highlighted trade
            if (_state.FocusTradeClose is { } fc && fc.Contains(x, y) && _model.FocusTrade != null)
            {
                _model.FocusTrade = null;
                _model.Invalidate();
                return;
            }

            foreach (var h in _state.Legend)
            {
                if (!h.Rect.Contains(x, y)) continue;
                if (h.Action == LegendAction.Name && clicks < 2) break; // a single click on the name: the chart is panned as usual
                LegendClicked(h);
                return;
            }
        }

        // panel separators
        foreach (var p in l.Panels.Skip(1))
        {
            if (Math.Abs(y - p.Rect.Top) <= 3 && p.SubPanel != null)
            {
                _drag = DragMode.PanelResize;
                _resizePanel = p.SubPanel;
                return;
            }
        }

        // indicators (in case they want the click)
        if (button == PointerButton.Left && ForwardToIndicators(x, y, button, clicks, mods, MouseKind.Down)) return;

        // SL/TP buttons of the position or of a pending order
        if (button == PointerButton.Left && ActiveTool == null && HandleAt(x, y, buttonsOnly: true) is { } button1)
        {
            StartBracketDrag(button1, y);
            return;
        }

        // drag one of your own orders to another price
        if (button == PointerButton.Left && ActiveTool == null && OrderAt(x, y) is { } order)
        {
            _dragOrder = order;
            _state.DragOrder = order;
            _state.DragPrice = order.Type == OrderTypes.Stop ? order.TriggerPrice : order.Price;
            _drag = DragMode.MoveOrder;
            _model.Invalidate();
            return;
        }

        // line of the position or of a stop/target attached to a pending order
        if (button == PointerButton.Left && ActiveTool == null && HandleAt(x, y, buttonsOnly: false) is { } band)
        {
            StartBracketDrag(band, y);
            return;
        }

        if (button == PointerButton.Left && ActiveTool is { } tool && l.Main.Rect.Contains(x, y))
        {
            var pt = PointAt(x, y, mods);
            _active = new ChartDrawing { Kind = tool, Points = { pt }, Color = _model.Theme.DrawingDefault };
            if (DrawingDefaults?.Invoke(tool) is { } template) _active.CopyStyleFrom(template);
            if (_active.IsPosition)
            {
                // position with a 1:2 stop and target to start with; it is adjusted afterwards with the handlers
                double tick = (double)_model.Instrument.TickSize;
                double risk = Math.Max(Math.Abs(l.Main.Top - l.Main.Bottom) * 0.06, tick * 4);
                if (tick > 0) risk = Math.Round(risk / tick) * tick;
                float span = (float)Math.Min(Math.Max(_model.Viewport.BarWidth * 24, 180), l.Plot.Width * 0.4);
                var end = ChartDrawing.TimeAt(l, _model.Series, x + span);
                _active.InitPosition(risk, end);
            }

            if (_active.RequiredPoints > 1) _active.Points.Add(pt);
            foreach (var d in _model.Drawings) d.Selected = false;
            _active.Selected = true;
            _model.Drawings.Add(_active);
            _activeHandle = _active.Points.Count - 1;
            if (_active.RequiredPoints == 1)
            {
                FinishDrawing();
            }
            else
            {
                _drag = DragMode.CreateDrawing;
            }

            _model.Invalidate();
            return;
        }

        if (button == PointerButton.Left)
        {
            // did it touch a drawn object?
            for (int i = _model.Drawings.Count - 1; i >= 0; i--)
            {
                var d = _model.Drawings[i];
                if (!d.HitTest(l, _model.Series, x, y, out int handle)) continue;
                foreach (var o in _model.Drawings) o.Selected = false;
                d.Selected = true;
                if (clicks == 2) // a triple click does not open two panels
                {
                    DrawingDoubleClicked?.Invoke(d);
                    _model.Invalidate();
                    return;
                }

                if (!d.Locked)
                {
                    _active = d;
                    _activeHandle = handle;
                    _startPoints = d.Points.ToList();
                    _drag = DragMode.MoveDrawing;
                }

                _model.Invalidate();
                return;
            }

            bool hadSelection = _model.Drawings.Any(d => d.Selected);
            foreach (var o in _model.Drawings) o.Selected = false;
            if (hadSelection) _model.Invalidate();

            if (clicks >= 2)
            {
                _model.Viewport.AutoScale = true;
                _model.Invalidate();
                return;
            }

            _drag = DragMode.Pan;
            _startScroll = _model.Viewport.ScrollBars;
            _startTop = l.Main.Top;
            _startBottom = l.Main.Bottom;
        }
    }

    public void MouseMove(float x, float y, InputModifiers mods)
    {
        var l = L;
        _state.MouseX = x;
        _state.MouseY = y;
        if (l == null) return;
        var vp = _model.Viewport;

        switch (_drag)
        {
            case DragMode.Pan:
            {
                vp.ScrollBars = _startScroll + (x - _downX) / vp.BarWidth;
                if (!vp.AutoScale)
                {
                    double perPx = (_startTop - _startBottom) / l.Main.Rect.Height;
                    double dv = (y - _downY) * perPx;
                    vp.PriceTop = _startTop + dv;
                    vp.PriceBottom = _startBottom + dv;
                }
                else if (Math.Abs(y - _downY) > 40 && Math.Abs(y - _downY) > Math.Abs(x - _downX) * 1.5)
                {
                    // strong vertical drag: it switches to manual scale so the price can be moved
                    vp.AutoScale = false;
                    vp.PriceTop = _startTop;
                    vp.PriceBottom = _startBottom;
                }

                break;
            }
            case DragMode.PriceScale:
            {
                double factor = Math.Exp((y - _downY) * 0.006);
                double mid = (_startTop + _startBottom) / 2;
                double half = (_startTop - _startBottom) / 2 * factor;
                vp.AutoScale = false;
                vp.PriceTop = mid + half;
                vp.PriceBottom = mid - half;
                break;
            }
            case DragMode.TimeScale:
            {
                double factor = Math.Exp((x - _downX) * 0.008);
                vp.BarWidth = Math.Clamp(_startBarWidth * factor, ChartViewport.MinBarWidth, ChartViewport.MaxBarWidth);
                break;
            }
            case DragMode.PanelResize when _resizePanel != null:
            {
                var plot = l.Plot;
                var p = l.Panels.FirstOrDefault(pp => pp.SubPanel == _resizePanel);
                if (p != null)
                {
                    float bottom = p.Rect.Bottom;
                    double ratio = Math.Clamp((bottom - y) / plot.Height, 0.06, 0.6);
                    _resizePanel.HeightRatio = ratio;
                }

                break;
            }
            case DragMode.CreateDrawing when _active != null:
                _active.Points[_activeHandle] = PointAt(x, y, mods);
                break;
            case DragMode.MoveOrder when _dragOrder != null:
            case DragMode.Bracket:
                _state.DragPrice = _model.Instrument.RoundPrice((decimal)l.Main.ValueAt(y));
                break;
            case DragMode.MoveDrawing when _active != null && _startPoints != null:
            {
                if (_activeHandle >= 0)
                {
                    _active.MoveHandle(_activeHandle, PointAt(x, y, mods));
                }
                else
                {
                    // moves the whole object
                    double dPrice = l.Main.ValueAt(y) - l.Main.ValueAt(_downY);
                    for (int i = 0; i < _active.Points.Count; i++)
                    {
                        var sp = _startPoints[i];
                        float sx = ChartDrawing.XOf(l, _model.Series, sp.Time);
                        var t = ChartDrawing.TimeAt(l, _model.Series, sx + (x - _downX));
                        _active.Points[i] = new ChartPoint(t, sp.Price + dPrice);
                    }
                }

                break;
            }
            default:
                ForwardToIndicators(x, y, PointerButton.Left, 0, mods, MouseKind.Move);
                break;
        }

        UpdateCursor(x, y);
        _lastX = x;
        _lastY = y;
        _model.Invalidate();
    }

    public void MouseUp(float x, float y, PointerButton button, InputModifiers mods)
    {
        if (_drag is DragMode.MoveOrder or DragMode.Bracket && L is { } lu)
            _state.DragPrice = _model.Instrument.RoundPrice((decimal)lu.Main.ValueAt(y));

        if (_drag == DragMode.CreateDrawing && _active != null)
        {
            // if it barely moved, it is read as "click, click": it waits for the second click
            if (Math.Abs(x - _downX) < 3 && Math.Abs(y - _downY) < 3)
            {
                _drag = DragMode.None;
                _pendingSecondClick = true;
                return;
            }

            FinishDrawing();
        }
        else if (_drag == DragMode.MoveDrawing)
        {
            DrawingsChanged?.Invoke();
        }
        else if (_drag == DragMode.MoveOrder && _dragOrder != null)
        {
            var o = _dragOrder;
            decimal from = o.Type == OrderTypes.Stop ? o.TriggerPrice : o.Price;
            decimal to = _state.DragPrice;
            _dragOrder = null;
            _state.DragOrder = null;
            if (to > 0 && to != from) OrderDragged?.Invoke(o, to);
        }

        else if (_drag == DragMode.Bracket && _state.BracketDrag is { } bd)
        {
            decimal to = _state.DragPrice;
            _state.BracketDrag = null;
            // a click without dragging does not create orders
            if (to > 0 && Math.Abs(y - _downY) >= 4) BracketDropped?.Invoke(bd, to);
        }

        if (_drag == DragMode.None) ForwardToIndicators(x, y, button, 1, mods, MouseKind.Up);
        _drag = DragMode.None;
        _active = _pendingSecondClick ? _active : null;
        _model.Invalidate();
    }

    private bool _pendingSecondClick;

    private void StartBracketDrag(BracketHandle h, float y)
    {
        var l = L;
        if (l == null) return;
        _state.BracketDrag = h.Drag;
        _state.DragPrice = _model.Instrument.RoundPrice((decimal)l.Main.ValueAt(y));
        _drag = DragMode.Bracket;
        _model.Invalidate();
    }

    /// <summary>Second click of a two-point tool (click-click mode).</summary>
    public bool TryCompletePending(float x, float y, InputModifiers mods)
    {
        if (!_pendingSecondClick || _active == null) return false;
        _active.Points[^1] = PointAt(x, y, mods);
        _pendingSecondClick = false;
        FinishDrawing();
        _active = null;
        _model.Invalidate();
        return true;
    }

    public void HoverPending(float x, float y, InputModifiers mods)
    {
        if (_pendingSecondClick && _active != null) _active.Points[^1] = PointAt(x, y, mods);
    }

    public bool HasPending => _pendingSecondClick;

    private void FinishDrawing()
    {
        _drag = DragMode.None;
        if (!StickyTool) ActiveTool = null;
        ToolFinished?.Invoke();
        DrawingsChanged?.Invoke();
    }

    public void Wheel(float x, float y, float dx, float dy, InputModifiers mods)
    {
        var l = L;
        if (l == null) return;
        var vp = _model.Viewport;

        if (l.PriceAxis.Contains(x, y) || (mods & (InputModifiers.Control | InputModifiers.Command)) != 0)
        {
            // zoom vertical
            double factor = Math.Pow(1.12, -dy);
            double top = l.Main.Top, bottom = l.Main.Bottom;
            double anchor = l.Main.ValueAt(y);
            vp.AutoScale = false;
            vp.PriceTop = anchor + (top - anchor) * factor;
            vp.PriceBottom = anchor - (anchor - bottom) * factor;
            _model.Invalidate();
            return;
        }

        if (Math.Abs(dx) > Math.Abs(dy) || (mods & InputModifiers.Shift) != 0)
        {
            double d = Math.Abs(dx) > Math.Abs(dy) ? dx : dy;
            vp.ScrollBars += d * 25 / vp.BarWidth;
            _model.Invalidate();
            return;
        }

        // horizontal zoom around the mouse
        double before = l.BarAtExact(x);
        double newWidth = Math.Clamp(vp.BarWidth * Math.Pow(1.12, dy), ChartViewport.MinBarWidth, ChartViewport.MaxBarWidth);
        if (Math.Abs(newWidth - vp.BarWidth) < 1e-9) return;
        bool followingLive = l.ScrollBars <= 0.01;
        vp.BarWidth = newWidth;
        if (!followingLive)
        {
            // keeps the bar under the mouse fixed: x = R - (N-1-b+M-S+0.5)*W  =>  S = N-1-b+M+0.5-(R-x)/W
            vp.ScrollBars = (l.Count - 1 - before + vp.RightMarginBars + 0.5) - (l.Plot.Right - x) / newWidth;
        }

        _model.Invalidate();
    }

    public void Leave()
    {
        _state.MouseX = null;
        _state.MouseY = null;
        var ci = _model.ChartInfo.MouseLocationInfo;
        ci.IsMouseLeave = true;
        _model.Invalidate();
    }

    public bool KeyDown(string key, InputModifiers mods)
    {
        var vp = _model.Viewport;
        switch (key)
        {
            case "Delete":
            case "Back":
            {
                var sel = _model.Drawings.Where(d => d.Selected).ToList();
                if (sel.Count == 0) return false;
                foreach (var d in sel) _model.Drawings.Remove(d);
                DrawingsChanged?.Invoke();
                _model.Invalidate();
                return true;
            }
            case "Escape":
                // cancels an order or stop/target drag
                if (_drag is DragMode.MoveOrder or DragMode.Bracket)
                {
                    _dragOrder = null;
                    _state.DragOrder = null;
                    _state.BracketDrag = null;
                    _drag = DragMode.None;
                    _model.Invalidate();
                    return true;
                }

                ActiveTool = null;
                if (_pendingSecondClick && _active != null) _model.Drawings.Remove(_active);
                _pendingSecondClick = false;
                _active = null;
                foreach (var d in _model.Drawings) d.Selected = false;
                ToolFinished?.Invoke();
                _model.Invalidate();
                return true;
            case "Home":
            case "End":
                vp.ScrollBars = 0;
                vp.AutoScale = true;
                _model.Invalidate();
                return true;
            case "Left":
                vp.ScrollBars += Math.Max(1, 60 / vp.BarWidth);
                _model.Invalidate();
                return true;
            case "Right":
                vp.ScrollBars -= Math.Max(1, 60 / vp.BarWidth);
                _model.Invalidate();
                return true;
            case "Add":
            case "OemPlus":
                vp.BarWidth = Math.Min(ChartViewport.MaxBarWidth, vp.BarWidth * 1.2);
                _model.Invalidate();
                return true;
            case "Subtract":
            case "OemMinus":
                vp.BarWidth = Math.Max(ChartViewport.MinBarWidth, vp.BarWidth / 1.2);
                _model.Invalidate();
                return true;
        }

        return false;
    }

    // ======================================================================= helpers

    private ChartPoint PointAt(float x, float y, InputModifiers mods)
    {
        var l = L!;
        var series = _model.Series;
        var time = ChartDrawing.TimeAt(l, series, x);
        double price = l.Main.ValueAt(y);
        // magnet: with Shift it snaps to the nearest high/low/close of the candle
        if ((mods & InputModifiers.Shift) != 0)
        {
            int bar = Math.Clamp(l.BarAt(x), 0, Math.Max(0, series.Count - 1));
            if (series.Count > 0)
            {
                var c = series[bar];
                var candidates = new[] { c.HighTicks, c.LowTicks, c.OpenTicks, c.CloseTicks }.Select(t => (double)series.TicksToPrice(t));
                price = candidates.OrderBy(p => Math.Abs(l.Main.Y(p) - y)).First();
                time = series[bar].Time;
            }
        }
        else
        {
            double tick = (double)_model.Instrument.TickSize;
            price = Math.Round(price / tick) * tick;
        }

        return new ChartPoint(time, price);
    }

    private void LegendClicked(LegendHit h)
    {
        if (h.Strategy is { } st)
        {
            switch (h.Action)
            {
                case LegendAction.ToggleVisible:
                    st.Visible = !st.Visible;
                    IndicatorsChanged?.Invoke();
                    break;
                case LegendAction.Remove:
                    StrategyRemoveRequested?.Invoke(st);
                    break;
                case LegendAction.Settings or LegendAction.Name:
                    StrategySettingsRequested?.Invoke(st);
                    break;
            }

            _model.Invalidate();
            return;
        }

        switch (h.Action)
        {
            case LegendAction.Collapse:
                _model.Settings.ShowIndicatorNames = !_model.Settings.ShowIndicatorNames;
                IndicatorsChanged?.Invoke();
                break;
            case LegendAction.ToggleVisible when h.Slot != null:
                h.Slot.Visible = !h.Slot.Visible;
                IndicatorsChanged?.Invoke();
                break;
            case LegendAction.Remove when h.Slot != null:
                _model.RemoveIndicator(h.Slot);
                IndicatorsChanged?.Invoke();
                break;
            case LegendAction.Settings or LegendAction.Name when h.Slot != null:
                IndicatorSettingsRequested?.Invoke(h.Slot);
                break;
        }

        _model.Invalidate();
    }

    private void UpdateCursor(float x, float y)
    {
        var l = L;
        if (l == null) return;
        if (_drag == DragMode.None && ((_state.GoLatestButton is { } gl && gl.Contains(x, y)) || (_state.FocusTradeClose is { } fc && fc.Contains(x, y)) ||
                                       _state.Legend.Any(h => h.Action != LegendAction.Name && h.Rect.Contains(x, y))))
        {
            Cursor = StdCursor.Hand;
            return;
        }

        var ci = _model.ChartInfo.MouseLocationInfo;
        ci.LastPosition = new System.Drawing.Point((int)x, (int)y);
        ci.IsMouseLeave = false;
        ci.BarBelowMouse = Math.Clamp(l.BarAt(x), 0, Math.Max(0, l.Count - 1));
        ci.PriceBelowMouse = (decimal)l.Main.ValueAt(y);

        if (l.PriceAxis.Contains(x, y)) Cursor = StdCursor.SizeNS;
        else if (l.TimeAxis.Contains(x, y)) Cursor = StdCursor.SizeWE;
        else if (l.Panels.Skip(1).Any(p => Math.Abs(y - p.Rect.Top) <= 3)) Cursor = StdCursor.SizeNS;
        else if (_drag == DragMode.Pan) Cursor = StdCursor.SizeAll;
        else if (_drag is DragMode.MoveOrder or DragMode.Bracket || (ActiveTool == null && (OrderAt(x, y) != null || HandleAt(x, y, false) != null))) Cursor = StdCursor.SizeNS;
        else if (_model.Drawings.Any(d => d.HitTest(l, _model.Series, x, y, out _))) Cursor = StdCursor.Hand;
        else
        {
            var c = IndicatorCursor(x, y);
            Cursor = c is StdCursor.None or StdCursor.Arrow ? StdCursor.Cross : c;
        }
    }

    private enum MouseKind
    {
        Down,
        Up,
        Move,
    }

    private bool ForwardToIndicators(float x, float y, PointerButton button, int clicks, InputModifiers mods, MouseKind kind)
    {
        var e = new RenderControlMouseEventArgs((int)x, (int)y,
            button switch { PointerButton.Right => RenderControlMouseButtons.Right, PointerButton.Middle => RenderControlMouseButtons.Middle, _ => RenderControlMouseButtons.Left },
            clicks)
        {
            Modifiers = (RenderControlKeyModifiers)(int)mods,
        };
        for (int i = _model.Indicators.Count - 1; i >= 0; i--)
        {
            var ind = _model.Indicators[i].Indicator;
            try
            {
                bool handled = kind switch
                {
                    MouseKind.Down => ind.ProcessMouseDown(e) || (clicks >= 2 ? ind.ProcessMouseDoubleClick(e) : ind.ProcessMouseClick(e)),
                    MouseKind.Up => ind.ProcessMouseUp(e),
                    _ => ind.ProcessMouseMove(e),
                };
                if (handled)
                {
                    _model.Invalidate();
                    return true;
                }
            }
            catch (Exception ex)
            {
                this.LogError($"Mouse error in {ind.Name}", ex);
            }
        }

        return false;
    }

    private StdCursor IndicatorCursor(float x, float y)
    {
        var e = new RenderControlMouseEventArgs((int)x, (int)y, RenderControlMouseButtons.None);
        foreach (var s in _model.Indicators)
        {
            try
            {
                var c = s.Indicator.GetCursor(e);
                if (c != StdCursor.None) return c;
            }
            catch
            {
                // ignore
            }
        }

        return StdCursor.None;
    }

    /// <summary>Forwards a key to the indicators.</summary>
    public bool ForwardKey(Avalonia.Input.KeyEventArgs e)
    {
        foreach (var s in _model.Indicators)
        {
            try
            {
                if (s.Indicator.ProcessKeyDown(e))
                {
                    _model.Invalidate();
                    return true;
                }
            }
            catch (Exception ex)
            {
                this.LogError($"Keyboard error in {s.Indicator.Name}", ex);
            }
        }

        return false;
    }
}
