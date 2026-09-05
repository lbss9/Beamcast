using Beamcast.Net;
using Beamcast.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace Beamcast.Pages;

/// <summary>
/// Entry screen: the hosts this person uses, and for the selected host its public rooms, the
/// rooms starred there, a code/invite box and a "create room" dialog.
/// </summary>
public sealed partial class LoungePage : Page
{
    private readonly LoungeService _lounge = LoungeService.Instance;
    private string _server = string.Empty;
    private HostInfo? _hostInfo;
    private bool _busy;
    private CancellationTokenSource? _listCts;

    public LoungePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = SettingsStore.Load();
        NameBox.Text = settings.DisplayName;
        _lounge.Closed += OnClosed;
        RefreshHosts();
        if (settings.RelayUrl.Length > 0)
            SelectHost(settings.RelayUrl);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lounge.Closed -= OnClosed;
        _listCts?.Cancel();
    }

    private void OnClosed(string reason)
    {
        ErrorText.Text = DescribeReason(reason);
        if (reason is LoungeProtocol.ReasonRoomDeleted or LoungeProtocol.ReasonKicked)
            RefreshRooms();
    }

    public static string DescribeReason(string reason) => reason switch
    {
        "lost" or "timeout" or "closed" => Loc.Get("Lounge_Lost"),
        LoungeProtocol.ReasonBadPassword => Loc.Get("Lounge_WrongPassword"),
        LoungeException.PasswordRequired => Loc.Get("Lounge_PasswordRequired"),
        LoungeProtocol.ReasonNoLounge => Loc.Get("Lounge_NotFound"),
        LoungeProtocol.ReasonBadKey => Loc.Get("Error_AppKey"),
        LoungeProtocol.ReasonVersion => Loc.Get("Error_Version"),
        LoungeProtocol.ReasonInviteExpired => Loc.Get("Lounge_InviteExpired"),
        LoungeProtocol.ReasonRoomFull => Loc.Get("Lounge_RoomFull"),
        LoungeProtocol.ReasonRateLimited => Loc.Get("Lounge_RateLimited"),
        LoungeProtocol.ReasonKicked => Loc.Get("Lounge_Kicked"),
        LoungeProtocol.ReasonRoomDeleted => Loc.Get("Lounge_RoomDeleted"),
        LoungeProtocol.ReasonPasswordChanged => Loc.Get("Lounge_PasswordChanged"),
        LoungeProtocol.ReasonNoKey => Loc.Get("Lounge_NoKey"),
        LoungeProtocol.ReasonNotAllowed => Loc.Get("Lounge_NotAllowed"),
        "unreachable" => Loc.Get("Lounge_Unreachable"),
        "left" or "disposed" or "" => string.Empty,
        _ => Loc.Format("Error_Generic", reason),
    };

    // ----- hosts -----

    private void RefreshHosts()
    {
        var hosts = SettingsStore.Load().Hosts;
        HostsList.Items.Clear();
        foreach (var host in hosts)
            HostsList.Items.Add(BuildHostRow(host));
        HostsEmptyText.Visibility = hosts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildHostRow(SavedHost host)
    {
        var selected = string.Equals(host.Url, _server, StringComparison.OrdinalIgnoreCase);
        var grid = new Grid
        {
            ColumnSpacing = 6,
            Padding = new Thickness(8, 6, 4, 6),
            CornerRadius = new CornerRadius(8),
            Background = selected ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["BeamAccentSoftBrush"] : null,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = host.Name, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        text.Children.Add(new TextBlock { Text = LoungeProtocol.DisplayHost(host.Url), Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        grid.Children.Add(text);

        var star = new ToggleButton
        {
            IsChecked = host.Favorite,
            Content = new FontIcon { Glyph = host.Favorite ? "" : "", FontSize = 13 },
            Style = (Style)Application.Current.Resources["GhostToggleStyle"],
        };
        ToolTipService.SetToolTip(star, Loc.Get("Lounge_HostFavorite"));
        star.Click += (_, _) =>
        {
            LoungeService.RememberHost(host.Url, favorite: star.IsChecked == true);
            RefreshHosts();
        };
        Grid.SetColumn(star, 1);
        grid.Children.Add(star);

        var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Style = (Style)Application.Current.Resources["GhostButtonStyle"] };
        ToolTipService.SetToolTip(remove, Loc.Get("Lounge_HostRemove"));
        remove.Click += (_, _) =>
        {
            LoungeService.ForgetHost(host.Url);
            if (selected)
            {
                _server = string.Empty;
                HostPanel.Visibility = Visibility.Collapsed;
                NoHostPanel.Visibility = Visibility.Visible;
            }
            RefreshHosts();
        };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);

        grid.Tapped += (_, _) => SelectHost(host.Url);
        return grid;
    }

    private void OnAddHostChanged(object sender, TextChangedEventArgs e) =>
        AddHostButton.IsEnabled = LoungeProtocol.TryNormalizeServer(AddHostBox.Text, out _);

    private void OnAddHostKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && AddHostButton.IsEnabled)
            OnAddHost(sender, e);
    }

    private void OnAddHost(object sender, RoutedEventArgs e)
    {
        if (!LoungeProtocol.TryNormalizeServer(AddHostBox.Text, out var url))
            return;
        LoungeService.RememberHost(url);
        AddHostBox.Text = string.Empty;
        SelectHost(url);
    }

    private void SelectHost(string url)
    {
        _server = url;
        NoHostPanel.Visibility = Visibility.Collapsed;
        HostPanel.Visibility = Visibility.Visible;
        HostTitleText.Text = SettingsStore.Load().Hosts.FirstOrDefault(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase))?.Name ?? LoungeProtocol.DisplayHost(url);
        HostStatusText.Text = LoungeProtocol.DisplayHost(url);
        ErrorText.Text = string.Empty;
        CodeBox.Text = string.Empty;
        RefreshHosts();
        RefreshRooms();
    }

    private void OnRefreshHost(object sender, RoutedEventArgs e) => RefreshRooms();

    private async void RefreshRooms()
    {
        if (_server.Length == 0)
            return;
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        HostProgress.IsActive = true;
        try
        {
            _hostInfo = await _lounge.ListRoomsAsync(_server, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            HostTitleText.Text = _hostInfo.Name.Length > 0 ? _hostInfo.Name : LoungeProtocol.DisplayHost(_server);
            HostStatusText.Text = Loc.Format("Lounge_HostStatus", LoungeProtocol.DisplayHost(_server), _hostInfo.Version, _hostInfo.MembersOnline);
            LoungeService.RememberHost(_server, name: _hostInfo.Name);
            RefreshHosts();
            ErrorText.Text = string.Empty;
            FillPublicRooms(_hostInfo.Rooms);
        }
        catch (LoungeException ex) when (!cts.IsCancellationRequested)
        {
            _hostInfo = null;
            HostStatusText.Text = LoungeProtocol.DisplayHost(_server);
            ErrorText.Text = ex.Reason == LoungeProtocol.ReasonBadKey ? Loc.Get("Lounge_HostNeedsKey") : DescribeReason(ex.Reason);
            FillPublicRooms([]);
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_listCts, cts))
                HostProgress.IsActive = false;
        }
        FillFavorites();
    }

    private void FillPublicRooms(List<RoomInfo> rooms)
    {
        PublicRoomsList.Items.Clear();
        foreach (var room in rooms)
            PublicRoomsList.Items.Add(BuildRoomRow(room.Name, room.Code, room.HasPassword, room.IsTemporary, room.Members, room.Streams));
        PublicEmptyText.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FillFavorites()
    {
        var favorites = SettingsStore.Load().FavoriteRooms.Where(r => string.Equals(r.ServerUrl, _server, StringComparison.OrdinalIgnoreCase)).ToList();
        FavoriteRoomsList.Items.Clear();
        foreach (var room in favorites)
        {
            var live = _hostInfo?.Rooms.FirstOrDefault(r => r.Code == room.Code);
            FavoriteRoomsList.Items.Add(BuildRoomRow(room.Name, room.Code, live?.HasPassword ?? room.HasPassword, live?.IsTemporary ?? false, live?.Members, live?.Streams));
        }
        FavoritesEmptyText.Visibility = favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement BuildRoomRow(string name, string code, bool hasPassword, bool temporary, int? members, int? streams)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = name.Length == 0 ? code : name, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (hasPassword)
            title.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center });
        if (temporary)
            title.Children.Add(new TextBlock { Text = Loc.Get("Lounge_Temporary"), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        text.Children.Add(title);
        var detail = members is { } m
            ? Loc.Format("Lounge_RoomMeta", m, streams ?? 0) + " · " + code
            : code;
        text.Children.Add(new TextBlock { Text = detail, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        grid.Children.Add(text);

        var favorite = LoungeService.IsFavorite(_server, code);
        var star = new ToggleButton
        {
            IsChecked = favorite,
            Content = new FontIcon { Glyph = favorite ? "" : "", FontSize = 13 },
            Style = (Style)Application.Current.Resources["GhostToggleStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(star, Loc.Get(favorite ? "Lounge_Unfavorite" : "Lounge_Favorite"));
        star.Click += (_, _) =>
        {
            LoungeService.SetFavorite(_server, code, name, hasPassword, star.IsChecked == true);
            FillPublicRooms(_hostInfo?.Rooms ?? []);
            FillFavorites();
        };
        Grid.SetColumn(star, 1);
        grid.Children.Add(star);

        var enter = new Button { Content = Loc.Get("Lounge_Enter"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_busy };
        enter.Click += async (_, _) => await JoinRoomAsync(new LoungeTarget(_server, code), hasPassword, name);
        Grid.SetColumn(enter, 2);
        grid.Children.Add(enter);
        return grid;
    }

    // ----- joining -----

    private void OnJoinInputChanged(object sender, TextChangedEventArgs e) =>
        JoinButton.IsEnabled = !_busy && LoungeInvite.TryDecode(CodeBox.Text, _server, out _);

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (!LoungeInvite.TryDecode(CodeBox.Text, _server, out var target))
        {
            ErrorText.Text = Loc.Get("Lounge_BadCode");
            return;
        }
        await JoinRoomAsync(target, hasPassword: null, roomName: string.Empty, JoinPasswordBox.Password);
    }

    private async Task JoinRoomAsync(LoungeTarget target, bool? hasPassword, string roomName, string typedPassword = "")
    {
        var password = typedPassword;
        var remember = false;
        if (password.Length == 0 && target.InviteKey() is null)
            password = LoungeService.RememberedPassword(target.ServerUrl, target.Code);
        if (password.Length == 0 && hasPassword == true && target.InviteToken is null)
        {
            var asked = await AskPasswordAsync(roomName);
            if (asked is null)
                return;
            (password, remember) = asked.Value;
        }

        while (true)
        {
            var options = new RoomJoinOptions
            {
                Code = target.Code,
                Password = password,
                InviteToken = target.InviteToken,
                InviteKey = target.ContentKey,
            };
            var outcome = await RunAsync(ct => _lounge.JoinAsync(target.ServerUrl, options, NameBox.Text, ct));
            if (outcome is null)
            {
                if (remember && password.Length > 0)
                    LoungeService.SetFavorite(target.ServerUrl, target.Code, _lounge.Name, true, true, password);
                return;
            }
            if (outcome is LoungeException.PasswordRequired or LoungeProtocol.ReasonBadPassword)
            {
                var asked = await AskPasswordAsync(roomName, wrong: outcome == LoungeProtocol.ReasonBadPassword);
                if (asked is null)
                    return;
                (password, remember) = asked.Value;
                continue;
            }
            return;
        }
    }

    private async Task<(string Password, bool Remember)?> AskPasswordAsync(string roomName, bool wrong = false)
    {
        var box = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek, MaxLength = 128 };
        var remember = new CheckBox { Content = Loc.Get("Lounge_RememberPassword") };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = wrong ? Loc.Get("Lounge_WrongPassword") : Loc.Get("Lounge_PasswordHint"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(remember);
        var dialog = new ContentDialog
        {
            Title = roomName.Length > 0 ? roomName : Loc.Get("Lounge_PasswordTitle"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Dialog_Ok"),
            CloseButtonText = Loc.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || box.Password.Length == 0)
            return null;
        return (box.Password, remember.IsChecked == true);
    }

    // ----- creating -----

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        var options = await RoomDialogs.CreateAsync(XamlRoot);
        if (options is null)
            return;
        await RunAsync(ct => _lounge.CreateAsync(_server, options, NameBox.Text, ct));
    }

    /// <summary>Runs a connect attempt; returns null on success or the failure reason.</summary>
    private async Task<string?> RunAsync(Func<CancellationToken, Task> action)
    {
        ErrorText.Text = string.Empty;
        var name = NameBox.Text.Trim();
        if (name.Length > 0)
            SettingsStore.Update(s => s.DisplayName = name);

        _busy = true;
        JoinButton.IsEnabled = false;
        CreateButton.IsEnabled = false;
        HostProgress.IsActive = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            await action(cts.Token);
            return null;
        }
        catch (LoungeException ex)
        {
            ErrorText.Text = DescribeReason(ex.Reason);
            return ex.Reason;
        }
        catch (Exception ex)
        {
            ErrorText.Text = Loc.Format("Error_Generic", ex.Message);
            return "error";
        }
        finally
        {
            HostProgress.IsActive = false;
            _busy = false;
            CreateButton.IsEnabled = true;
            OnJoinInputChanged(this, null!);
        }
    }
}

internal static class LoungeTargetExtensions
{
    public static byte[]? InviteKey(this LoungeTarget target) => target.ContentKey;
}
