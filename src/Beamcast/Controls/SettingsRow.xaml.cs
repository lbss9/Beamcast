using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beamcast.Controls;

/// <summary>
/// One settings line in the Windows/PowerToys style: an icon, a title with a short description,
/// and the control that acts on it pinned to the right. Plain properties are enough for XAML;
/// nothing here is bound.
/// </summary>
public sealed partial class SettingsRow : UserControl
{
    public SettingsRow()
    {
        InitializeComponent();
    }

    /// <summary>Segoe Fluent glyph, e.g. "". Empty hides the icon column.</summary>
    public string Glyph
    {
        get => Icon.Glyph;
        set
        {
            Icon.Glyph = value;
            Icon.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public string Header
    {
        get => HeaderText.Text;
        set => HeaderText.Text = value;
    }

    public string Description
    {
        get => DescriptionText.Text;
        set
        {
            DescriptionText.Text = value;
            DescriptionText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>The control on the right (toggle, combo, button, text box).</summary>
    public object? ActionContent
    {
        get => Action.Content;
        set => Action.Content = value;
    }

    /// <summary>No border or background: for use as an Expander header, which draws its own.</summary>
    public bool Bare
    {
        set
        {
            Root.BorderThickness = new Thickness(0);
            Root.Background = null;
            Root.Padding = new Thickness(0, 4, 0, 4);
        }
    }

    /// <summary>Flat look for rows nested inside an expander (no own border).</summary>
    public bool Nested
    {
        set
        {
            Root.BorderThickness = new Thickness(value ? 0 : 1);
            Root.CornerRadius = new CornerRadius(value ? 0 : 8);
            Root.Background = value ? null : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            Root.Padding = value ? new Thickness(46, 10, 14, 10) : new Thickness(14, 12, 14, 12);
        }
    }
}
