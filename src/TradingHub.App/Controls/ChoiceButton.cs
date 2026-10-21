using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;

namespace TradingHub.App.Controls;

/// <summary>
/// A compact button that shows an icon and the chosen value, and opens a menu with the options.
/// It takes up far less room than a ComboBox with long text.
/// </summary>
public sealed class ChoiceButton<T> : Button
{
    private readonly List<(T Value, string Label, MenuItem Item)> _options = new();
    private readonly TextBlock _label = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = Avalonia.Media.TextTrimming.None };
    private readonly MenuFlyout _menu = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private readonly Func<T, string>? _buttonLabel;
    private T? _selected;

    /// <param name="buttonLabel">Short text for the button (otherwise it uses the menu's own).</param>
    public ChoiceButton(MaterialIconKind icon, IEnumerable<(T Value, string Label)> options, string tip, Func<T, string>? buttonLabel = null)
    {
        _buttonLabel = buttonLabel;
        Content = Ui.H(7, Ui.Icon(icon, 16, Ui.Text1), _label, Ui.Icon(MaterialIconKind.ChevronDown, 14, Ui.Text2));
        ToolTip.SetTip(this, tip);

        // the items are created once, before opening the menu (if they are loaded on open, the menu comes up empty)
        foreach (var (value, label) in options)
        {
            var item = new MenuItem { Header = label };
            var v = value;
            item.Click += (_, _) =>
            {
                Select(v);
                Changed?.Invoke(v);
            };
            _menu.Items.Add(item);
            _options.Add((value, label, item));
        }

        Flyout = _menu;
        if (_options.Count > 0) Select(_options[0].Value);
    }

    // it uses Button's theme and styles (background, padding, hover and the full click area)
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The user chose an option.</summary>
    public event Action<T>? Changed;

    public T? Selected => _selected;

    /// <summary>Shows text on the button without changing the selected option (for example, a custom range).</summary>
    public void ShowText(string text) => _label.Text = text;

    public void Select(T value)
    {
        _selected = value;
        foreach (var (v, label, item) in _options)
        {
            bool current = EqualityComparer<T>.Default.Equals(v, value);
            item.Icon = current ? Ui.Icon(MaterialIconKind.Check, 15, Ui.Accent) : null;
            if (current) _label.Text = _buttonLabel?.Invoke(v) ?? label;
        }
    }
}
