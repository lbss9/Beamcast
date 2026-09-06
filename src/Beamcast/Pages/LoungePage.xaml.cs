using Beamcast.Net;
using Beamcast.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

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
    private bool _askKeyForNewHost;
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
        ShowError(reason);
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
        "not_owner" => Loc.Get("Lounge_NotOwner"),
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
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        caption.Children.Add(new TextBlock { Text = LoungeProtocol.DisplayHost(host.Url), Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        if (host.HasAppKey)
        {
            var keyIcon = new FontIcon { Glyph = "", FontSize = 10, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(keyIcon, Loc.Get("Lounge_HostHasKey"));
            caption.Children.Add(keyIcon);
        }
        text.Children.Add(caption);
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
        // Clicks on the buttons must not reach the row's Tapped handler: selecting the host rebuilds
        // the list, which would tear down the button (and any flyout anchored to it) mid-click.
        star.Tapped += (_, e) => e.Handled = true;
        Grid.SetColumn(star, 1);
        grid.Children.Add(star);

        var keyItem = new MenuFlyoutItem { Text = Loc.Get("Lounge_HostKeyMenu"), Icon = new FontIcon { Glyph = "\uE192" } };
        keyItem.Click += async (_, _) => await EditHostKeyAsync(host.Url);
        var removeItem = new MenuFlyoutItem { Text = Loc.Get("Lounge_HostRemove"), Icon = new FontIcon { Glyph = "\uE74D" } };
        removeItem.Click += (_, _) =>
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
        var menu = new MenuFlyout();
        menu.Items.Add(keyItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(removeItem);

        var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 12 }, Style = (Style)Application.Current.Resources["GhostButtonStyle"] };
        ToolTipService.SetToolTip(more, Loc.Get("Lounge_HostMore"));
        more.Tapped += (_, e) => e.Handled = true;
        more.Click += (_, _) => menu.ShowAt(more);
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);

        grid.ContextFlyout = menu;
        grid.Tapped += (_, _) => SelectHost(host.Url);
        return grid;
    }

    /// <summary>Lets the person type the key this host demands (BEAMCAST_APP_KEY on that server).</summary>
    private async Task EditHostKeyAsync(string url)
    {
        var host = SettingsStore.Load().Hosts.FirstOrDefault(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));
        var box = new PasswordBox
        {
            PasswordRevealMode = PasswordRevealMode.Peek,
            MaxLength = 256,
            Header = Loc.Get("Lounge_HostKeyField"),
            Password = LoungeService.AppKeyFor(url),
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = LoungeProtocol.DisplayHost(url), Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis });
        panel.Children.Add(new TextBlock { Text = Loc.Get("Lounge_HostKeyHint"), TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["HintTextStyle"] });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = host?.Name is { Length: > 0 } name ? name : LoungeProtocol.DisplayHost(url),
            Content = panel,
            PrimaryButtonText = Loc.Get("Edit_Save"),
            CloseButtonText = Loc.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        LoungeService.RememberHost(url, appKey: box.Password);
        RefreshHosts();
        if (string.Equals(url, _server, StringComparison.OrdinalIgnoreCase))
            RefreshRooms();
    }

    private async void OnHostKey(object sender, RoutedEventArgs e)
    {
        if (_server.Length > 0)
            await EditHostKeyAsync(_server);
    }

    /// <summary>Shows the failure text; when the host wants an app key, offers the shortcut to type it.</summary>
    private void ShowError(string? reason)
    {
        var needsKey = reason == LoungeProtocol.ReasonBadKey;
        ShowErrorText(reason is null ? string.Empty : needsKey ? Loc.Get("Lounge_HostNeedsKey") : DescribeReason(reason), needsKey);
    }

    private void ShowErrorText(string text, bool needsKey = false)
    {
        ErrorText.Text = text;
        HostKeyButton.Visibility = needsKey && _server.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _askKeyForNewHost = true;
        SelectHost(url);
    }

    private void SelectHost(string url)
    {
        _server = url;
        NoHostPanel.Visibility = Visibility.Collapsed;
        HostPanel.Visibility = Visibility.Visible;
        HostTitleText.Text = SettingsStore.Load().Hosts.FirstOrDefault(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase))?.Name ?? LoungeProtocol.DisplayHost(url);
        HostStatusText.Text = LoungeProtocol.DisplayHost(url);
        ShowError(null);
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
            ShowError(null);
            FillPublicRooms(_hostInfo.Rooms);
        }
        catch (LoungeException ex) when (!cts.IsCancellationRequested)
        {
            _hostInfo = null;
            HostStatusText.Text = LoungeProtocol.DisplayHost(_server);
            ShowError(ex.Reason);
            FillPublicRooms([]);
            if (ex.Reason == LoungeProtocol.ReasonBadKey && _askKeyForNewHost)
            {
                _askKeyForNewHost = false;
                await EditHostKeyAsync(_server);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_listCts, cts))
                HostProgress.IsActive = false;
        }
        FillOwned();
        FillFavorites();
    }

    private void FillPublicRooms(List<RoomInfo> rooms)
    {
        PublicRoomsList.Items.Clear();
        foreach (var room in rooms)
            PublicRoomsList.Items.Add(BuildRoomRow(room.Name, room.Code, room.HasPassword, room.IsTemporary, room.Members, room.Streams));
        PublicEmptyText.Visibility = rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Rooms this person created on the host: the only place private ones show up.</summary>
    private void FillOwned()
    {
        var owned = SettingsStore.Load().OwnedRooms.Where(r => string.Equals(r.ServerUrl, _server, StringComparison.OrdinalIgnoreCase)).ToList();
        OwnedRoomsList.Items.Clear();
        foreach (var room in owned)
        {
            var live = _hostInfo?.Rooms.FirstOrDefault(r => r.Code == room.Code);
            var saved = SettingsStore.Load().FavoriteRooms.FirstOrDefault(r => r.Code == room.Code && string.Equals(r.ServerUrl, _server, StringComparison.OrdinalIgnoreCase));
            OwnedRoomsList.Items.Add(BuildRoomRow(room.Name, room.Code, live?.HasPassword ?? saved?.HasPassword ?? false, live?.IsTemporary ?? false, live?.Members, live?.Streams));
        }
        OwnedEmptyText.Visibility = owned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var owned = LoungeService.OwnerTokenFor(_server, code) is not null;
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        title.Children.Add(new TextBlock { Text = name.Length == 0 ? code : name, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (owned)
        {
            var crown = new FontIcon { Glyph = "\uE735", FontSize = 11, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(crown, Loc.Get("Lounge_RoomOwned"));
            title.Children.Add(crown);
        }
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
            FillOwned();
            FillFavorites();
        };
        Grid.SetColumn(star, 1);
        grid.Children.Add(star);

        var enter = new Button { Content = Loc.Get("Lounge_Enter"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_busy };
        enter.Click += async (_, _) => await JoinRoomAsync(new LoungeTarget(_server, code), hasPassword, name);
        Grid.SetColumn(enter, 2);
        grid.Children.Add(enter);

        var menu = new MenuFlyout();
        var copyItem = new MenuFlyoutItem { Text = Loc.Get("Lounge_RoomCopyInvite"), Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyItem.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(LoungeInvite.Encode(new LoungeTarget(_server, code)));
            Clipboard.SetContent(package);
            ShowErrorText(Loc.Get("Lounge_InviteCopied"));
        };
        menu.Items.Add(copyItem);
        var favoriteItem = new MenuFlyoutItem { Text = Loc.Get(favorite ? "Lounge_Unfavorite" : "Lounge_Favorite"), Icon = new FontIcon { Glyph = favorite ? "\uE8D9" : "\uE734" } };
        favoriteItem.Click += (_, _) =>
        {
            LoungeService.SetFavorite(_server, code, name, hasPassword, !favorite);
            FillPublicRooms(_hostInfo?.Rooms ?? []);
            FillOwned();
            FillFavorites();
        };
        menu.Items.Add(favoriteItem);
        if (owned)
        {
            var editItem = new MenuFlyoutItem { Text = Loc.Get("Room_MenuEdit"), Icon = new FontIcon { Glyph = "\uE70F" } };
            editItem.Click += async (_, _) => await EditRoomAsync(code, name, hasPassword);
            var deleteItem = new MenuFlyoutItem { Text = Loc.Get("Room_MenuDelete"), Icon = new FontIcon { Glyph = "\uE74D" } };
            deleteItem.Click += async (_, _) => await DeleteRoomAsync(code, name, hasPassword);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(editItem);
            menu.Items.Add(deleteItem);
        }
        var more = new Button { Content = new FontIcon { Glyph = "\uE712", FontSize = 12 }, Style = (Style)Application.Current.Resources["GhostButtonStyle"], VerticalAlignment = VerticalAlignment.Center, IsEnabled = !_busy };
        ToolTipService.SetToolTip(more, Loc.Get("Lounge_RoomMore"));
        more.Click += (_, _) => menu.ShowAt(more);
        Grid.SetColumn(more, 3);
        grid.Children.Add(more);
        grid.ContextFlyout = menu;
        return grid;
    }

    // ----- managing rooms we own, without entering them -----

    /// <summary>
    /// Opens a short owner session for the room, asking for the password when the host demands one
    /// (owners of password rooms still prove the password: the key comes from it). Null = cancelled.
    /// </summary>
    private async Task<LoungeClient?> OpenOwnerSessionAsync(string code, string roomName, bool hasPassword, CancellationToken ct)
    {
        var password = LoungeService.RememberedPassword(_server, code);
        if (password.Length == 0 && hasPassword)
        {
            var asked = await AskPasswordAsync(roomName);
            if (asked is null)
                return null;
            password = asked.Value.Password;
        }
        while (true)
        {
            try
            {
                return await RoomManagement.OpenAsync(_server, code, password, ct);
            }
            catch (LoungeException ex) when (ex.Reason is LoungeException.PasswordRequired or LoungeProtocol.ReasonBadPassword)
            {
                var asked = await AskPasswordAsync(roomName, wrong: ex.Reason == LoungeProtocol.ReasonBadPassword);
                if (asked is null)
                    return null;
                password = asked.Value.Password;
            }
        }
    }

    private async Task EditRoomAsync(string code, string roomName, bool hasPassword)
    {
        if (_busy)
            return;
        await ManageAsync(async ct =>
        {
            var client = await OpenOwnerSessionAsync(code, roomName, hasPassword, ct);
            if (client is null)
                return;
            try
            {
                var result = await RoomDialogs.EditAsync(XamlRoot, client.Room);
                if (result is null)
                    return;
                await RoomManagement.UpdateAsync(client, result.Value.Update, result.Value.NewPassword, ct);
            }
            finally
            {
                await RoomManagement.CloseAsync(client);
            }
        });
    }

    private async Task DeleteRoomAsync(string code, string roomName, bool hasPassword)
    {
        if (_busy)
            return;
        if (!await RoomDialogs.ConfirmDeleteAsync(XamlRoot, roomName.Length > 0 ? roomName : code))
            return;
        await ManageAsync(async ct =>
        {
            var client = await OpenOwnerSessionAsync(code, roomName, hasPassword, ct);
            if (client is null)
                return;
            try
            {
                await RoomManagement.DeleteAsync(client, ct);
            }
            finally
            {
                await RoomManagement.CloseAsync(client);
            }
        });
    }

    /// <summary>Runs an owner action with the busy ring, shows the failure reason and refreshes the lists.</summary>
    private async Task ManageAsync(Func<CancellationToken, Task> action)
    {
        ShowError(null);
        _busy = true;
        HostProgress.IsActive = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await action(cts.Token);
        }
        catch (LoungeException ex)
        {
            ShowError(ex.Reason == LoungeProtocol.ReasonNotAllowed ? "not_owner" : ex.Reason);
        }
        catch (Exception ex)
        {
            ShowErrorText(Loc.Format("Error_Generic", ex.Message));
        }
        finally
        {
            HostProgress.IsActive = false;
            _busy = false;
        }
        RefreshRooms();
    }

    // ----- joining -----

    private void OnJoinInputChanged(object sender, TextChangedEventArgs e) =>
        JoinButton.IsEnabled = !_busy && LoungeInvite.TryDecode(CodeBox.Text, _server, out _);

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (!LoungeInvite.TryDecode(CodeBox.Text, _server, out var target))
        {
            ShowErrorText(Loc.Get("Lounge_BadCode"));
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
        ShowError(null);
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
            ShowError(ex.Reason);
            return ex.Reason;
        }
        catch (Exception ex)
        {
            ShowErrorText(Loc.Format("Error_Generic", ex.Message));
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
