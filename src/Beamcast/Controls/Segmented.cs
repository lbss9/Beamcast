using Beamcast.Ui;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Beamcast.Controls;

/// <summary>
/// A row of mutually exclusive choices in one rounded track (the "segmented" piece of the design
/// system). Used where a ComboBox would hide two or three short options behind a click.
/// </summary>
public sealed class Segmented : ContentControl
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Border _track;
    private readonly List<(string Label, string? Count)> _items = [];
    private int _selected = -1;

    public Segmented()
    {
        _track = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(3), Child = _row, HorizontalAlignment = HorizontalAlignment.Left };
        Content = _track;
        IsTabStop = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        ActualThemeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => Rebuild();
    }

    public event EventHandler? SelectionChanged;

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var clamped = _items.Count == 0 ? -1 : Math.Clamp(value, 0, _items.Count - 1);
            if (clamped == _selected)
                return;
            _selected = clamped;
            Rebuild();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Replaces the choices. A count, when given, shows faint after the label.</summary>
    public void SetItems(IEnumerable<(string Label, string? Count)> items, int selected = 0)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = _items.Count == 0 ? -1 : Math.Clamp(selected, 0, _items.Count - 1);
        Rebuild();
    }

    public void SetItems(params string[] labels) => SetItems(labels.Select(l => (l, (string?)null)));

    /// <summary>Updates the counts without touching the selection.</summary>
    public void SetCounts(params int?[] counts)
    {
        for (var i = 0; i < _items.Count && i < counts.Length; i++)
            _items[i] = (_items[i].Label, counts[i]?.ToString());
        Rebuild();
    }

    private void Rebuild()
    {
        _track.Background = Beam.Brush("Surface2");
        _row.Children.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var index = i;
            var on = i == _selected;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            content.Children.Add(new TextBlock
            {
                Text = _items[i].Label,
                FontSize = 13,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Beam.Brush(on ? "Text" : "Text2"),
            });
            if (_items[i].Count is { } count)
                content.Children.Add(new TextBlock { Text = count, FontSize = 12, Foreground = Beam.Brush("Text3"), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Content = content,
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0),
                CornerRadius = new CornerRadius(7),
                BorderThickness = new Thickness(0),
                Background = on ? Beam.Brush("SegOn") : Beam.Brush("Clear"),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, _items[i].Label);
            button.Click += (_, _) => SelectedIndex = index;
            _row.Children.Add(button);
        }
    }
}
