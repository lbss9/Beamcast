using Beamcast.Net;
using Beamcast.Pages;
using Beamcast.Services;
using Beamcast.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace Beamcast;

/// <summary>What the side bar knows about a host: drives the dot next to it.</summary>
public enum HostState
{
    Unknown,
    Loading,
    Online,
    Offline,
    NeedsKey,
}

/// <summary>
/// The side bar: the saved hosts with their state, What's new / Settings, and the display name.
/// Inside a room it shrinks to a rail of icons so the video gets the width.
/// </summary>
public sealed partial class MainWindow
{
    private const double SideBarWidth = 248;
    private const double RailWidth = 56;

    /// <summary>The host whose rooms the lounge shows; empty when there is none.</summary>
    public string SelectedHost { get; private set; } = string.Empty;

    /// <summary>Raised when the person picks a host (or it needs to be loaded again) while the lounge is open.</summary>
    public event Action<string>? HostSelected;

    private bool IsRail => ContentFrame.CurrentSourcePageType == typeof(RoomPage);

    public void SetHostState(string url, HostState state)
    {
        if (url.Length == 0 || (_hostStates.TryGetValue(url, out var current) && current == state))
            return;
        _hostStates[url] = state;
        RefreshSideBar();
    }

    /// <summary>Makes a host the current one, opens the lounge and has it load the rooms.</summary>
    public void SelectHost(string url)
    {
        Diag.Log($"ui: host {(url.Length == 0 ? "(none)" : LoungeProtocol.DisplayHost(url))} selected");
        SelectedHost = url;
        if (url.Length > 0)
            LoungeService.RememberHost(url);
        var onLounge = ContentFrame.CurrentSourcePageType == typeof(LoungePage);
        NavigateTo("lounge");
        if (onLounge)
            HostSelected?.Invoke(url);
    }

    /// <summary>Asks every other saved host once whether it answers, so the dots mean something.</summary>
    private async Task ProbeHostsAsync()
    {
        foreach (var host in SettingsStore.Load().Hosts.ToList())
        {
            if (string.Equals(host.Url, SelectedHost, StringComparison.OrdinalIgnoreCase) || _hostStates.ContainsKey(host.Url))
                continue;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await LoungeService.Instance.ListRoomsAsync(host.Url, cts.Token);
                SetHostState(host.Url, HostState.Online);
            }
            catch (LoungeException ex)
            {
                SetHostState(host.Url, ex.Reason == LoungeProtocol.ReasonBadKey ? HostState.NeedsKey : HostState.Offline);
            }
            catch (Exception)
            {
                SetHostState(host.Url, HostState.Offline);
            }
        }
    }

    private void OnFrameNavigated(object sender, NavigationEventArgs e) => RefreshSideBar();

    public void RefreshSideBar()
    {
        if (HostsPanel is null)
            return;
        var rail = IsRail;
        SideBar.Width = rail ? RailWidth : SideBarWidth;
        HostsHeader.Visibility = rail ? Visibility.Collapsed : Visibility.Visible;

        var hosts = SettingsStore.Load().Hosts;
        if (SelectedHost.Length > 0 && !hosts.Any(h => string.Equals(h.Url, SelectedHost, StringComparison.OrdinalIgnoreCase)))
            SelectedHost = hosts.FirstOrDefault()?.Url ?? string.Empty;

        HostsPanel.Children.Clear();
        HostsPanel.Margin = rail ? new Thickness(8, 12, 8, 8) : new Thickness(8, 4, 8, 8);
        foreach (var host in hosts.OrderByDescending(h => h.Favorite))
            HostsPanel.Children.Add(BuildHostRow(host, rail));
        if (hosts.Count == 0 && !rail)
            HostsPanel.Children.Add(new TextBlock { Text = Loc.Get("Side_NoHosts"), Style = Beam.Style("FaintTextStyle"), Margin = new Thickness(12, 4, 12, 0) });
        if (rail)
        {
            var add = new Button { Style = Beam.Style("GhostButtonStyle"), Content = new FontIcon { Glyph = "", FontSize = 14 }, HorizontalAlignment = HorizontalAlignment.Center };
            ToolTipService.SetToolTip(add, Loc.Get("AddHost_Title"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(add, Loc.Get("AddHost_Title"));
            add.Click += OnAddHost;
            HostsPanel.Children.Add(add);
        }

        NavPanel.Children.Clear();
        NavPanel.Children.Add(BuildNavButton("about", "", Loc.Get("Nav_About/Content"), rail));
        NavPanel.Children.Add(BuildNavButton("settings", "", Loc.Get("Nav_Settings"), rail));

        RefreshProfile();
    }

    public void RefreshProfile()
    {
        var name = SettingsStore.Load().DisplayName.Trim();
        var shown = name.Length > 0 ? name : Loc.Get("Side_NoName");
        var avatar = Beam.Avatar(name, 34, accent: true);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ProfileButton, Loc.Format("Side_ProfileName", shown));
        if (IsRail)
        {
            ProfileButton.Padding = new Thickness(3, 6, 3, 6);
            ProfileButton.Content = avatar;
            return;
        }
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(avatar);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Beam.Text(shown, 13.5, strong: true));
        text.Children.Add(Beam.Text(Loc.Get("Side_NameHint"), 11.5, "Text3"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var pencil = Beam.Icon("", 13, "Text3");
        Grid.SetColumn(pencil, 2);
        grid.Children.Add(pencil);
        ProfileButton.Padding = new Thickness(8, 6, 8, 6);
        ProfileButton.Content = grid;
    }

    private UIElement BuildNavButton(string tag, string glyph, string label, bool rail)
    {
        var on = _page == tag;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(Beam.Icon(glyph, 16, on ? "Text" : "Text2"));
        if (!rail)
            content.Children.Add(Beam.Text(label, 13.5, on ? "Text" : "Text2", strong: on));
        var button = new Button
        {
            Content = content,
            Height = 36,
            Padding = rail ? new Thickness(0) : new Thickness(12, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = rail ? HorizontalAlignment.Center : HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(0),
            Background = on ? Beam.Brush("Surface2") : Beam.Brush("Clear"),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
        if (rail)
            ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => NavigateTo(tag);
        return WithSelectionBar(button, on, 36);
    }

    /// <summary>Puts the coral bar of the selected item on the left edge of a row.</summary>
    private static UIElement WithSelectionBar(FrameworkElement row, bool selected, double height)
    {
        if (!selected)
            return row;
        var grid = new Grid();
        grid.Children.Add(row);
        grid.Children.Add(new Border
        {
            Width = 3,
            Height = height - 16,
            CornerRadius = new CornerRadius(2),
            Background = Beam.Brush("Accent"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
        return grid;
    }

    private UIElement BuildHostRow(SavedHost host, bool rail)
    {
        var selected = _page == "lounge" && string.Equals(host.Url, SelectedHost, StringComparison.OrdinalIgnoreCase);
        var state = _hostStates.TryGetValue(host.Url, out var known) ? known : HostState.Unknown;

        var avatar = new Grid { Width = 34, Height = 34 };
        var tile = Beam.Avatar(host.Name, 32, square: true);
        tile.HorizontalAlignment = HorizontalAlignment.Left;
        tile.VerticalAlignment = VerticalAlignment.Top;
        avatar.Children.Add(tile);
        avatar.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 11,
            Height = 11,
            StrokeThickness = 2,
            Stroke = Beam.Brush(selected && !rail ? "Surface2" : "Side"),
            Fill = Beam.Brush(state switch
            {
                HostState.Online => "Ok",
                HostState.Loading or HostState.NeedsKey => "Warn",
                _ => "Text3",
            }),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
        });

        var menu = BuildHostMenu(host);
        var button = new Button
        {
            Height = rail ? 44 : 52,
            Padding = rail ? new Thickness(0) : new Thickness(10, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = rail ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0),
            Background = selected && !rail ? Beam.Brush("Surface2") : Beam.Brush("Clear"),
            ContextFlyout = menu,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, host.Name);
        button.Click += (_, _) => SelectHost(host.Url);

        if (rail)
        {
            button.Content = avatar;
            ToolTipService.SetToolTip(button, host.Name);
            return WithSelectionBar(button, selected, 44);
        }

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(avatar);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Beam.Text(host.Name, 13.5, strong: true));
        text.Children.Add(Beam.Text(LoungeProtocol.DisplayHost(host.Url), 11.5, "Text3"));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (host.Favorite)
        {
            var star = Beam.Icon("", 12, "Warn");
            Grid.SetColumn(star, 2);
            grid.Children.Add(star);
        }
        var more = new Button { Style = Beam.Style("GhostButtonStyle"), Width = 28, Height = 28, Content = new FontIcon { Glyph = "", FontSize = 12 } };
        ToolTipService.SetToolTip(more, Loc.Get("Lounge_HostMore"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, Loc.Get("Lounge_HostMore"));
        more.Click += (_, _) => menu.ShowAt(more);
        Grid.SetColumn(more, 3);
        // Only the current host shows the menu button; the others keep the width for the address
        // and still open the same menu with a right click.
        if (selected)
            grid.Children.Add(more);
        button.Content = grid;
        return WithSelectionBar(button, selected, 52);
    }

    private MenuFlyout BuildHostMenu(SavedHost host)
    {
        var menu = new MenuFlyout();
        var favorite = new MenuFlyoutItem { Text = Loc.Get(host.Favorite ? "Side_HostUnfavorite" : "Lounge_HostFavorite"), Icon = new FontIcon { Glyph = host.Favorite ? "" : "" } };
        favorite.Click += (_, _) =>
        {
            SettingsStore.Update(s =>
            {
                var saved = s.Hosts.FirstOrDefault(h => string.Equals(h.Url, host.Url, StringComparison.OrdinalIgnoreCase));
                if (saved is not null)
                    saved.Favorite = !host.Favorite;
            });
            RefreshSideBar();
        };
        var key = new MenuFlyoutItem { Text = Loc.Get("Lounge_HostKeyMenu"), Icon = new FontIcon { Glyph = "" } };
        key.Click += async (_, _) =>
        {
            if (await HostDialogs.EditKeyAsync(RootGrid.XamlRoot, host.Url))
                ReloadHost(host.Url);
        };
        var remove = new MenuFlyoutItem { Text = Loc.Get("Lounge_HostRemove"), Icon = new FontIcon { Glyph = "" } };
        remove.Click += (_, _) =>
        {
            LoungeService.ForgetHost(host.Url);
            _hostStates.Remove(host.Url);
            if (string.Equals(host.Url, SelectedHost, StringComparison.OrdinalIgnoreCase))
            {
                var next = SettingsStore.Load().Hosts.FirstOrDefault()?.Url ?? string.Empty;
                SelectedHost = next;
                SettingsStore.Update(s => s.RelayUrl = next);
                HostSelected?.Invoke(next);
            }
            RefreshSideBar();
        };
        menu.Items.Add(favorite);
        menu.Items.Add(key);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
        return menu;
    }

    /// <summary>Loads a host again after its key changed.</summary>
    public void ReloadHost(string url)
    {
        _hostStates.Remove(url);
        if (string.Equals(url, SelectedHost, StringComparison.OrdinalIgnoreCase))
            HostSelected?.Invoke(url);
        else
            _ = ProbeHostsAsync();
        RefreshSideBar();
    }

    private async void OnAddHost(object sender, RoutedEventArgs e)
    {
        var url = await HostDialogs.AddAsync(RootGrid.XamlRoot);
        if (url is not null)
            SelectHost(url);
    }

    private async void OnEditName(object sender, RoutedEventArgs e)
    {
        var name = await HostDialogs.EditNameAsync(RootGrid.XamlRoot, SettingsStore.Load().DisplayName);
        if (name is null)
            return;
        SettingsStore.Update(s => s.DisplayName = name);
        RefreshProfile();
    }
}
