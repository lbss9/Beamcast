using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Beamcast.Ui;

/// <summary>
/// Small builders for the pieces of the design system that pages create in code: themed brushes,
/// initials avatars, badges and icons. Colours come from the theme dictionaries in App.xaml.
/// </summary>
internal static class Beam
{
    private static readonly Color[] AvatarPalette =
    [
        Color.FromArgb(255, 0xFF, 0x4D, 0x6D),
        Color.FromArgb(255, 0x7C, 0x6C, 0xFF),
        Color.FromArgb(255, 0x2F, 0xB8, 0xA6),
        Color.FromArgb(255, 0xF5, 0xA5, 0x24),
        Color.FromArgb(255, 0x4C, 0x9B, 0xFF),
        Color.FromArgb(255, 0xD3, 0x6B, 0xFF),
    ];

    /// <summary>
    /// A brush of the theme the window is showing. The window theme can differ from the app theme
    /// (the person picks it in Settings), so the lookup goes to the matching dictionary directly.
    /// </summary>
    public static Brush Brush(string name)
    {
        var theme = App.Main?.RootTheme == ElementTheme.Light ? "Light" : "Default";
        // The Beam dictionaries are merged into the application's (see App.xaml), newest last.
        foreach (var merged in Application.Current.Resources.MergedDictionaries.Reverse())
        {
            if (merged.ThemeDictionaries.TryGetValue(theme, out var found)
                && found is ResourceDictionary dictionary
                && dictionary.TryGetValue("Beam" + name + "Brush", out var brush)
                && brush is Brush result)
                return result;
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public static Style Style(string key) => (Style)Application.Current.Resources[key];

    public static FontFamily Mono => (FontFamily)Application.Current.Resources["BeamMonoFont"];

    public static FontIcon Icon(string glyph, double size, string brush = "Text2") =>
        new() { Glyph = glyph, FontSize = size, Foreground = Brush(brush), VerticalAlignment = VerticalAlignment.Center };

    public static TextBlock Text(string text, double size, string brush = "Text", bool strong = false, bool mono = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = Brush(brush),
            FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (mono)
            block.FontFamily = Mono;
        return block;
    }

    /// <summary>A tinted tile with the first letter of the name; the colour is stable per name.</summary>
    public static Border Avatar(string name, double size, bool square = false, bool accent = false)
    {
        var label = name.Trim();
        var letter = label.Length > 0 ? char.ToUpperInvariant(label[0]).ToString() : "?";
        var color = accent || label.Length == 0
            ? ((SolidColorBrush)Brush("Accent")).Color
            : AvatarPalette[(label[0] + label.Length) % AvatarPalette.Length];
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(square ? Math.Round(size * 0.28) : size / 2),
            Background = new SolidColorBrush(Color.FromArgb(56, color.R, color.G, color.B)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = letter,
                FontSize = Math.Round(size * 0.42),
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>A small rounded badge: optional dot or glyph, then the label.</summary>
    public static Border Chip(string? text, string foreground = "Text2", string background = "Surface2", string? glyph = null, bool dot = false, bool strong = true)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        var fore = foreground == "White" ? new SolidColorBrush(Microsoft.UI.Colors.White) : Brush(foreground);
        if (dot)
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = fore, VerticalAlignment = VerticalAlignment.Center });
        if (glyph is not null)
            row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, Foreground = fore, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(text))
            row.Children.Add(new TextBlock { Text = text, FontSize = 11, FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal, Foreground = fore, VerticalAlignment = VerticalAlignment.Center });
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(string.IsNullOrEmpty(text) ? 5 : 8, 0, string.IsNullOrEmpty(text) ? 5 : 8, 0),
            Height = 22,
            Background = Brush(background),
            VerticalAlignment = VerticalAlignment.Center,
            Child = row,
        };
    }

    /// <summary>The red "live" badge.</summary>
    public static Border LiveChip() => Chip(Loc.Get("Badge_Live"), "White", "Live", dot: true);

    /// <summary>Icon followed by a label, for buttons.</summary>
    public static StackPanel IconLabel(string glyph, string text) =>
        new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new FontIcon { Glyph = glyph, FontSize = 14 },
                new TextBlock { Text = text },
            },
        };

    /// <summary>
    /// Gives screen readers (and UI automation) a name for every button whose content is an icon or
    /// an icon plus a label: the label when there is one, otherwise the tooltip.
    /// </summary>
    public static void LabelButtons(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase button
                && string.IsNullOrEmpty(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button)))
            {
                var label = FirstText(button.Content as DependencyObject) ?? ToolTipService.GetToolTip(button) as string;
                if (!string.IsNullOrEmpty(label))
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            }
            LabelButtons(child);
        }
    }

    private static string? FirstText(DependencyObject? content)
    {
        switch (content)
        {
            case null:
                return null;
            case TextBlock { Text.Length: > 0 } text:
                return text.Text;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    if (FirstText(child) is { } found)
                        return found;
                }
                return null;
            case Border { Child: { } inner }:
                return FirstText(inner);
            default:
                return null;
        }
    }
}
