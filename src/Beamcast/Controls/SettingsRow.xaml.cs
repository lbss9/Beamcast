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

    /// <summary>
    /// Shows a check box at the left instead of the icon, for a list of options that belong
    /// together (the shape Windows uses for update preferences).
    /// </summary>
    public bool Checkable
    {
        set
        {
            Check.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            if (!value)
                return;
            Icon.Visibility = Visibility.Collapsed;
            Root.Padding = new Thickness(14, 10, 14, 10);
        }
    }

    /// <summary>State of the check box. Setting it does not raise <see cref="CheckChanged"/>.</summary>
    public bool IsChecked
    {
        get => Check.IsChecked == true;
        set
        {
            _quiet = true;
            Check.IsChecked = value;
            _quiet = false;
        }
    }

    /// <summary>The person ticked or unticked the box.</summary>
    public event RoutedEventHandler? CheckChanged;

    private bool _quiet;

    private void OnCheckChanged(object sender, RoutedEventArgs e)
    {
        if (!_quiet)
            CheckChanged?.Invoke(this, e);
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
