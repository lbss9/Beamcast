using Beamcast.Net;
using Beamcast.Services;
using Beamcast.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Beamcast.Pages;

/// <summary>
/// Entry screen: the rooms of the host picked in the side bar, in one grid (every room, the ones
/// this person owns, the starred ones), plus "join with a code" and "create room". When there is
/// nothing to list it says why and offers the way out: add a host, retry, type the key.
/// </summary>
public sealed partial class LoungePage : Page
{
    private enum View
    {
        NoHost,
        Loading,
        Rooms,
        Offline,
        NeedsKey,
        Empty,
        NoMatch,
    }

    /// <summary>A room as the grid shows it, merged from the host's list, owned rooms and favorites.</summary>
    private sealed record RoomEntry(string Name, string Code, bool HasPassword, bool Temporary, int? Members, int? Streams, bool Owned, bool Favorite);

    private readonly LoungeService _lounge = LoungeService.Instance;
    private string _server = string.Empty;
    private HostInfo? _hostInfo;
    private string? _hostError;
    private bool _loadedOnce;
    private bool _busy;
    private View _view = View.NoHost;
    private Action? _noticeAction;
    private CancellationTokenSource? _listCts;

    public LoungePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static string DisplayName => SettingsStore.Load().DisplayName;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FilterTabs.SetItems([(Loc.Get("Lounge_FilterAll"), null), (Loc.Get("Lounge_FilterMine"), null), (Loc.Get("Lounge_FilterFavorites"), null)]);
        SkeletonRepeater.ItemsSource = Enumerable.Range(0, 6).Select(BuildSkeleton).ToList();
        _lounge.Closed += OnClosed;
        if (App.Main is not null)
            App.Main.HostSelected += SelectHost;
        SelectHost(App.Main?.SelectedHost ?? string.Empty);
        Beam.LabelButtons(this);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lounge.Closed -= OnClosed;
        if (App.Main is not null)
            App.Main.HostSelected -= SelectHost;
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
        LoungeProtocol.ReasonReplaced => Loc.Get("Lounge_Replaced"),
        "not_owner" => Loc.Get("Lounge_NotOwner"),
        "unreachable" => Loc.Get("Lounge_Unreachable"),
        "left" or "disposed" or "" => string.Empty,
        _ => Loc.Format("Error_Generic", reason),
    };

    // ----- notices -----

    /// <summary>Shows the failure text; a retry button comes with it when trying again can help.</summary>
    private void ShowError(string? reason)
    {
        if (reason is null)
        {
            NoticeBar.IsOpen = false;
            return;
        }
        if (reason == LoungeProtocol.ReasonBadKey)
        {
            NoticeBar.IsOpen = false;
            RefreshRooms();
            return;
        }
        ShowErrorText(DescribeReason(reason));
    }

    private void ShowErrorText(string text) => ShowNotice(text, InfoBarSeverity.Error);

    private void ShowNotice(string text, InfoBarSeverity severity, string? actionText = null, Action? action = null)
    {
        if (text.Length == 0)
        {
            NoticeBar.IsOpen = false;
            return;
        }
        NoticeBar.Severity = severity;
        NoticeBar.Message = text;
        _noticeAction = action;
        NoticeAction.Content = actionText;
        NoticeAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        NoticeBar.IsOpen = true;
        if (severity == InfoBarSeverity.Success)
            _ = CloseNoticeLaterAsync(text);
    }

    private async Task CloseNoticeLaterAsync(string text)
    {
        await Task.Delay(2500);
        if (NoticeBar.Message == text)
            NoticeBar.IsOpen = false;
    }

    private void OnNoticeAction(object sender, RoutedEventArgs e)
    {
        NoticeBar.IsOpen = false;
        _noticeAction?.Invoke();
    }

    // ----- host -----

    private void SelectHost(string url)
    {
        _server = url;
        _hostInfo = null;
        _hostError = null;
        _loadedOnce = false;
        NoticeBar.IsOpen = false;
        SearchBox.Text = string.Empty;
        if (url.Length == 0)
        {
            _listCts?.Cancel();
            Render();
            return;
        }
        RefreshRooms();
    }

    private string HostName =>
        _hostInfo?.Name is { Length: > 0 } live
            ? live
            : SettingsStore.Load().Hosts.FirstOrDefault(h => string.Equals(h.Url, _server, StringComparison.OrdinalIgnoreCase))?.Name ?? LoungeProtocol.DisplayHost(_server);

    private void OnRefreshHost(object sender, RoutedEventArgs e) => RefreshRooms();

    private async void RefreshRooms()
    {
        if (_server.Length == 0)
            return;
        var server = _server;
        _listCts?.Cancel();
        var cts = _listCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        HostProgress.IsActive = true;
        if (!_loadedOnce)
        {
            App.Main?.SetHostState(server, HostState.Loading);
            Render();
        }
        try
        {
            var info = await _lounge.ListRoomsAsync(server, cts.Token);
            if (cts.IsCancellationRequested || server != _server)
                return;
            _hostInfo = info;
            _hostError = null;
            LoungeService.RememberHost(server, name: info.Name);
            App.Main?.SetHostState(server, HostState.Online);
            App.Main?.RefreshSideBar();
        }
        catch (LoungeException ex) when (!cts.IsCancellationRequested)
        {
            if (server != _server)
                return;
            _hostInfo = null;
            _hostError = ex.Reason;
            App.Main?.SetHostState(server, ex.Reason == LoungeProtocol.ReasonBadKey ? HostState.NeedsKey : HostState.Offline);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (ReferenceEquals(_listCts, cts))
                HostProgress.IsActive = false;
        }
        _loadedOnce = true;
        Render();
    }

    // ----- what the page shows -----

    /// <summary>Every room this person can reach on the host: listed ones, plus owned and starred ones the list lacks.</summary>
    private List<RoomEntry> CollectRooms()
    {
        var settings = SettingsStore.Load();
        bool Here(string url) => string.Equals(url, _server, StringComparison.OrdinalIgnoreCase);
        var owned = settings.OwnedRooms.Where(r => Here(r.ServerUrl)).ToList();
        var favorites = settings.FavoriteRooms.Where(r => Here(r.ServerUrl)).ToList();
        var rooms = new Dictionary<string, RoomEntry>(StringComparer.Ordinal);
        foreach (var room in _hostInfo?.Rooms ?? [])
            rooms[room.Code] = new RoomEntry(room.Name, room.Code, room.HasPassword, room.IsTemporary, room.Members, room.Streams, false, false);
        foreach (var room in favorites)
            rooms.TryAdd(room.Code, new RoomEntry(room.Name, room.Code, room.HasPassword, false, null, null, false, true));
        foreach (var room in owned)
            rooms.TryAdd(room.Code, new RoomEntry(room.Name, room.Code, favorites.FirstOrDefault(f => f.Code == room.Code)?.HasPassword ?? false, false, null, null, true, false));
        return rooms.Values
            .Select(r => r with
            {
                Owned = LoungeService.OwnerTokenFor(_server, r.Code) is not null,
                Favorite = favorites.Any(f => f.Code == r.Code),
            })
            .OrderByDescending(r => r.Streams ?? 0)
            .ThenByDescending(r => r.Members ?? -1)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void Render()
    {
        var hasHost = _server.Length > 0;
        HeaderPanel.Visibility = hasHost ? Visibility.Visible : Visibility.Collapsed;
        if (!hasHost)
        {
            ShowState(View.NoHost);
            return;
        }

        HostTitleText.Text = HostName;
        var address = LoungeProtocol.DisplayHost(_server);
        var online = _hostInfo is not null;
        JoinCodeButton.IsEnabled = !_busy;
        CreateButton.IsEnabled = online && !_busy;

        if (!_loadedOnce)
        {
            HostChip.Content = Beam.Chip(Loc.Get("Lounge_StateConnecting"), "Warn", "WarnSoft", dot: true);
            HostStatusText.Text = address;
            ShowState(View.Loading);
            return;
        }
        if (_hostError == LoungeProtocol.ReasonBadKey)
        {
            HostChip.Content = Beam.Chip(Loc.Get("Lounge_StateNeedsKey"), "Warn", "WarnSoft", glyph: "\uE192");
            HostStatusText.Text = address;
            ShowState(View.NeedsKey);
            return;
        }
        if (!online)
        {
            HostChip.Content = Beam.Chip(Loc.Get("Lounge_StateOffline"), "Text2", "Surface2", dot: true);
            HostStatusText.Text = address;
            ShowState(View.Offline);
            return;
        }

        HostChip.Content = Beam.Chip(Loc.Get("Lounge_StateOnline"), "Ok", "OkSoft", dot: true);
        HostStatusText.Text = Loc.Format("Lounge_HostStatus", address, _hostInfo!.Version, _hostInfo.MembersOnline);

        var all = CollectRooms();
        FilterTabs.SetCounts(all.Count, all.Count(r => r.Owned), all.Count(r => r.Favorite));
        if (all.Count == 0)
        {
            ShowState(View.Empty);
            return;
        }
        var query = SearchBox.Text.Trim();
        var shown = all
            .Where(r => FilterTabs.SelectedIndex switch { 1 => r.Owned, 2 => r.Favorite, _ => true })
            .Where(r => query.Length == 0 || r.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || r.Code.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        RoomsRepeater.ItemsSource = shown.Select(BuildRoomTile).ToList();
        ShowState(shown.Count == 0 ? View.NoMatch : View.Rooms);
    }

    private void ShowState(View view)
    {
        _view = view;
        var lists = view is View.Rooms or View.NoMatch or View.Loading;
        ToolsPanel.Visibility = lists ? Visibility.Visible : Visibility.Collapsed;
        ToolsPanel.Opacity = view == View.Loading ? 0.5 : 1;
        ToolsPanel.IsHitTestVisible = view != View.Loading;
        RoomsScroll.Visibility = view == View.Rooms ? Visibility.Visible : Visibility.Collapsed;
        SkeletonRepeater.Visibility = view == View.Loading ? Visibility.Visible : Visibility.Collapsed;
        StatePanel.Visibility = view is View.Rooms or View.Loading ? Visibility.Collapsed : Visibility.Visible;
        if (StatePanel.Visibility == Visibility.Collapsed)
            return;

        StateFieldRow.Visibility = Visibility.Collapsed;
        StateTextBox.Visibility = Visibility.Collapsed;
        StatePasswordBox.Visibility = Visibility.Collapsed;
        StateActions.Visibility = Visibility.Collapsed;
        StateSecondary.Visibility = Visibility.Collapsed;
        StateNote.Text = string.Empty;
        StateNote.Visibility = Visibility.Collapsed;

        void Lead(string glyph, string tone, string soft, string title, string body)
        {
            StateRing.Background = Beam.Brush(soft);
            StateIcon.Glyph = glyph;
            StateIcon.Foreground = Beam.Brush(tone);
            StateTitle.Text = Loc.Get(title);
            StateBody.Text = Loc.Get(body);
        }
        void Note(string key)
        {
            StateNote.Text = Loc.Get(key);
            StateNote.Visibility = Visibility.Visible;
        }
        void Actions(string primary, string? secondary = null)
        {
            StateActions.Visibility = Visibility.Visible;
            StatePrimary.Content = Loc.Get(primary);
            if (secondary is not null)
            {
                StateSecondary.Content = Loc.Get(secondary);
                StateSecondary.Visibility = Visibility.Visible;
            }
        }

        switch (view)
        {
            case View.NoHost:
                Lead("\uE968", "Accent", "AccentSoft", "Welcome_Title", "Welcome_Body");
                StateFieldRow.Visibility = Visibility.Visible;
                StateTextBox.Visibility = Visibility.Visible;
                StateTextBox.PlaceholderText = Loc.Get("AddHost_Placeholder");
                StateFieldButton.Content = Loc.Get("AddHost_Confirm");
                OnStateTextChanged(this, null!);
                Note("Welcome_Note");
                break;
            case View.NeedsKey:
                Lead("\uE192", "Warn", "WarnSoft", "NeedsKey_Title", "NeedsKey_Body");
                StateFieldRow.Visibility = Visibility.Visible;
                StatePasswordBox.Visibility = Visibility.Visible;
                StatePasswordBox.PlaceholderText = Loc.Get("Lounge_HostKeyField");
                StatePasswordBox.Password = string.Empty;
                StateFieldButton.Content = Loc.Get("Edit_Save");
                StateFieldButton.IsEnabled = false;
                Note(LoungeService.AppKeyFor(_server).Length > 0 ? "NeedsKey_Wrong" : "NeedsKey_Note");
                break;
            case View.Offline:
                Lead("\uEB5E", "Text2", "Surface2", "Offline_Title", "Offline_Body");
                Actions("Action_Retry");
                if (_hostError is { } reason && reason is not ("unreachable" or "timeout"))
                {
                    StateNote.Text = DescribeReason(reason);
                    StateNote.Visibility = Visibility.Visible;
                }
                break;
            case View.Empty:
                Lead("\uEC15", "Accent", "AccentSoft", "EmptyHost_Title", "EmptyHost_Body");
                Actions("Lounge_CreateButton/Content", "Lounge_JoinCodeText/Text");
                break;
            case View.NoMatch:
                Lead("\uE721", "Text2", "Surface2", "NoMatch_Title", "NoMatch_Body");
                Actions("NoMatch_Clear");
                break;
        }
    }

    private void OnFilterChanged(object? sender, EventArgs e)
    {
        if (_loadedOnce)
            Render();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_loadedOnce && _hostInfo is not null)
            Render();
    }

    private void OnStatePrimary(object sender, RoutedEventArgs e)
    {
        switch (_view)
        {
            case View.Offline:
                _loadedOnce = false;
                RefreshRooms();
                break;
            case View.Empty:
                OnCreate(sender, e);
                break;
            case View.NoMatch:
                SearchBox.Text = string.Empty;
                FilterTabs.SelectedIndex = 0;
                Render();
                break;
        }
    }

    private void OnStateSecondary(object sender, RoutedEventArgs e)
    {
        if (_view == View.Empty)
            OnJoinCode(sender, e);
    }

    private void OnStateTextChanged(object sender, TextChangedEventArgs e) =>
        StateFieldButton.IsEnabled = LoungeProtocol.TryNormalizeServer(StateTextBox.Text, out _) || LoungeInvite.TryDecode(StateTextBox.Text, string.Empty, out _);

    private void OnStatePasswordChanged(object sender, RoutedEventArgs e) =>
        StateFieldButton.IsEnabled = StatePasswordBox.Password.Length > 0;

    private void OnStateFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && StateFieldButton.IsEnabled)
            OnStateFieldAction(sender, e);
    }

    /// <summary>The field of the state panel: a host address (or an invite) on first use, the host key when asked.</summary>
    private async void OnStateFieldAction(object sender, RoutedEventArgs e)
    {
        if (_view == View.NeedsKey)
        {
            LoungeService.RememberHost(_server, appKey: StatePasswordBox.Password);
            _loadedOnce = false;
            RefreshRooms();
            return;
        }
        if (_view != View.NoHost)
            return;
        var text = StateTextBox.Text;
        // An invite carries its host: save it and go straight into the room.
        if (LoungeInvite.TryDecode(text, string.Empty, out var invite))
        {
            StateTextBox.Text = string.Empty;
            LoungeService.RememberHost(invite.ServerUrl);
            App.Main?.SelectHost(invite.ServerUrl);
            await JoinRoomAsync(invite, hasPassword: null, roomName: string.Empty);
            return;
        }
        if (!LoungeProtocol.TryNormalizeServer(text, out var url))
            return;
        StateTextBox.Text = string.Empty;
        LoungeService.RememberHost(url);
        App.Main?.SelectHost(url);
    }

    // ----- room tiles -----

    private static Border BuildSkeleton(int index)
    {
        var grid = new Grid();
        void Bar(double x, double y, double width, double height, string brush, double radius = 5) =>
            grid.Children.Add(new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(radius),
                Background = Beam.Brush(brush),
                Margin = new Thickness(x, y, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            });
        Bar(0, 0, 40, 40, "Surface3", 11);
        Bar(52, 3, 130, 14, "Surface3");
        Bar(52, 26, 70, 10, "Surface2");
        Bar(0, 78, 96, 12, "Surface2");
        return new Border
        {
            Height = 128,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            Background = Beam.Brush("Surface"),
            BorderBrush = Beam.Brush("Stroke"),
            BorderThickness = new Thickness(1),
            Opacity = Math.Max(0.3, 1 - index * 0.13),
            Child = grid,
        };
    }

    private void RefreshLists() => Render();

    private Border BuildRoomTile(RoomEntry room)
    {
        var title = room.Name.Length == 0 ? room.Code : room.Name;
        var target = new LoungeTarget(_server, room.Code);

        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Beam.Avatar(title, 40, square: true));

        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Beam.Text(title, 15, strong: true));
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        meta.Children.Add(Beam.Text(room.Code, 12, "Text3", mono: true));
        if (room.HasPassword)
        {
            var lockIcon = Beam.Icon("\uE72E", 11, "Text3");
            ToolTipService.SetToolTip(lockIcon, Loc.Get("Lounge_Locked"));
            meta.Children.Add(lockIcon);
        }
        if (room.Owned)
        {
            var crown = Beam.Icon("\uE735", 11, "Warn");
            ToolTipService.SetToolTip(crown, Loc.Get("Lounge_RoomOwned"));
            meta.Children.Add(crown);
        }
        if (room.Temporary)
            meta.Children.Add(Beam.Chip(Loc.Get("Lounge_Temporary"), strong: false));
        text.Children.Add(meta);
        Grid.SetColumn(text, 1);
        top.Children.Add(text);

        var star = new Button
        {
            Style = Beam.Style("GhostButtonStyle"),
            Width = 30,
            Height = 30,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -4, -6, 0),
            Content = new FontIcon { Glyph = room.Favorite ? "\uE735" : "\uE734", FontSize = 14, Foreground = Beam.Brush(room.Favorite ? "Warn" : "Text3") },
        };
        var starLabel = Loc.Get(room.Favorite ? "Lounge_Unfavorite" : "Lounge_Favorite");
        ToolTipService.SetToolTip(star, starLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(star, starLabel);
        star.Click += (_, _) =>
        {
            LoungeService.SetFavorite(_server, room.Code, room.Name, room.HasPassword, !room.Favorite);
            RefreshLists();
        };
        star.Tapped += (_, e) => e.Handled = true;
        Grid.SetColumn(star, 2);
        top.Children.Add(star);

        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        if (room.Streams > 0)
            status.Children.Add(Beam.LiveChip());
        if (room.Members is { } members)
        {
            var people = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            people.Children.Add(Beam.Icon("\uE716", 13));
            people.Children.Add(Beam.Text(members == 0 ? Loc.Get("Lounge_RoomEmpty") : Loc.Format("Lounge_RoomOnline", members), 12.5, "Text2"));
            status.Children.Add(people);
        }
        else
        {
            status.Children.Add(Beam.Text(Loc.Get("Lounge_RoomUnlisted"), 12.5, "Text3"));
        }

        var menu = BuildRoomMenu(room, target);
        var enter = new Button { Content = Loc.Get("Lounge_Enter"), Style = Beam.Style("BeamPrimaryButtonStyle"), Height = 30, MinWidth = 76, Padding = new Thickness(12, 0, 12, 0), FontSize = 13, Visibility = Visibility.Collapsed, IsEnabled = !_busy };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(enter, Loc.Format("Lounge_EnterRoom", title));
        enter.Click += async (_, _) => await JoinRoomAsync(target, room.HasPassword, room.Name);
        enter.Tapped += (_, e) => e.Handled = true;
        var more = new Button { Style = Beam.Style("GhostButtonStyle"), Width = 30, Height = 30, Content = new FontIcon { Glyph = "\uE712", FontSize = 13 }, IsEnabled = !_busy };
        ToolTipService.SetToolTip(more, Loc.Get("Lounge_RoomMore"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more, Loc.Get("Lounge_RoomMore"));
        more.Click += (_, _) => menu.ShowAt(more);
        more.Tapped += (_, e) => e.Handled = true;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, -6, 0) };
        actions.Children.Add(enter);
        actions.Children.Add(more);

        var bottom = new Grid();
        bottom.Children.Add(status);
        bottom.Children.Add(actions);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        layout.Children.Add(top);
        var line = new Border { Height = 1, Background = Beam.Brush("Stroke"), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 9), Opacity = 0.7 };
        Grid.SetRow(line, 1);
        layout.Children.Add(line);
        Grid.SetRow(bottom, 2);
        layout.Children.Add(bottom);

        var tile = new Border
        {
            Height = 128,
            Padding = new Thickness(16, 16, 16, 9),
            CornerRadius = new CornerRadius(14),
            Background = Beam.Brush("Surface"),
            BorderBrush = Beam.Brush("Stroke"),
            BorderThickness = new Thickness(1),
            Child = layout,
            ContextFlyout = menu,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, title);
        // The whole tile is the way in; the button only shows up to say so while the mouse is over it.
        tile.PointerEntered += (_, _) =>
        {
            tile.Background = Beam.Brush("Surface2");
            tile.BorderBrush = Beam.Brush("AccentLine");
            enter.Visibility = Visibility.Visible;
        };
        tile.PointerExited += (_, _) =>
        {
            tile.Background = Beam.Brush("Surface");
            tile.BorderBrush = Beam.Brush("Stroke");
            enter.Visibility = Visibility.Collapsed;
        };
        tile.Tapped += async (_, _) =>
        {
            if (!_busy)
                await JoinRoomAsync(target, room.HasPassword, room.Name);
        };
        return tile;
    }

    private MenuFlyout BuildRoomMenu(RoomEntry room, LoungeTarget target)
    {
        var menu = new MenuFlyout();
        var enterItem = new MenuFlyoutItem { Text = Loc.Get("Lounge_Enter"), Icon = new FontIcon { Glyph = "\uE8AD" } };
        enterItem.Click += async (_, _) => await JoinRoomAsync(target, room.HasPassword, room.Name);
        menu.Items.Add(enterItem);
        var copyItem = new MenuFlyoutItem { Text = Loc.Get("Lounge_RoomCopyInvite"), Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyItem.Click += (_, _) =>
        {
            var package = new DataPackage();
            package.SetText(LoungeInvite.Encode(target));
            Clipboard.SetContent(package);
            ShowNotice(Loc.Get("Lounge_InviteCopied"), InfoBarSeverity.Success);
        };
        menu.Items.Add(copyItem);
        var favoriteItem = new MenuFlyoutItem { Text = Loc.Get(room.Favorite ? "Lounge_Unfavorite" : "Lounge_Favorite"), Icon = new FontIcon { Glyph = room.Favorite ? "\uE8D9" : "\uE734" } };
        favoriteItem.Click += (_, _) =>
        {
            LoungeService.SetFavorite(_server, room.Code, room.Name, room.HasPassword, !room.Favorite);
            RefreshLists();
        };
        menu.Items.Add(favoriteItem);
        if (room.Owned)
        {
            var editItem = new MenuFlyoutItem { Text = Loc.Get("Room_MenuEdit"), Icon = new FontIcon { Glyph = "\uE70F" } };
            editItem.Click += async (_, _) => await EditRoomAsync(room.Code, room.Name, room.HasPassword);
            var deleteItem = new MenuFlyoutItem { Text = Loc.Get("Room_MenuDelete"), Icon = new FontIcon { Glyph = "\uE74D" }, Foreground = Beam.Brush("Danger") };
            deleteItem.Click += async (_, _) => await DeleteRoomAsync(room.Code, room.Name, room.HasPassword);
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(editItem);
            menu.Items.Add(deleteItem);
        }
        return menu;
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
            var asked = await RoomDialogs.PasswordAsync(XamlRoot, roomName);
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
                var asked = await RoomDialogs.PasswordAsync(XamlRoot, roomName, wrong: ex.Reason == LoungeProtocol.ReasonBadPassword);
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
            LoungeClient? client;
            try
            {
                client = await OpenOwnerSessionAsync(code, roomName, hasPassword, ct);
            }
            catch (LoungeException ex) when (ex.Reason == LoungeProtocol.ReasonNoLounge)
            {
                return; // already gone from the host: it was just dropped from our list, which is what deleting wanted
            }
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

    private async void OnJoinCode(object sender, RoutedEventArgs e)
    {
        Diag.Log("ui: join with code clicked");
        var text = await RoomDialogs.JoinCodeAsync(XamlRoot, _server);
        if (text is null)
            return;
        if (!LoungeInvite.TryDecode(text, _server, out var target))
        {
            ShowErrorText(Loc.Get("Lounge_BadCode"));
            return;
        }
        await JoinRoomAsync(target, hasPassword: null, roomName: string.Empty);
    }

    private async Task JoinRoomAsync(LoungeTarget target, bool? hasPassword, string roomName, string typedPassword = "")
    {
        Diag.Log($"ui: join {target.Code} at {LoungeProtocol.DisplayHost(target.ServerUrl)} (invite {(target.InviteToken is null ? "no" : "yes")})");
        var password = typedPassword;
        var remember = false;
        if (password.Length == 0 && target.InviteKey() is null)
            password = LoungeService.RememberedPassword(target.ServerUrl, target.Code);
        if (password.Length == 0 && hasPassword == true && target.InviteToken is null)
        {
            var asked = await RoomDialogs.PasswordAsync(XamlRoot, roomName);
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
            var outcome = await RunAsync(ct => _lounge.JoinAsync(target.ServerUrl, options, DisplayName, ct));
            if (outcome is null)
            {
                if (remember && password.Length > 0)
                    LoungeService.SetFavorite(target.ServerUrl, target.Code, _lounge.Name, true, true, password);
                return;
            }
            if (outcome is LoungeException.PasswordRequired or LoungeProtocol.ReasonBadPassword)
            {
                var asked = await RoomDialogs.PasswordAsync(XamlRoot, roomName, wrong: outcome == LoungeProtocol.ReasonBadPassword);
                if (asked is null)
                    return;
                (password, remember) = asked.Value;
                continue;
            }
            return;
        }
    }

    // ----- creating -----

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        Diag.Log("ui: create room clicked");
        var options = await RoomDialogs.CreateAsync(XamlRoot);
        if (options is null)
            return;
        await RunAsync(ct => _lounge.CreateAsync(_server, options, DisplayName, ct));
    }

    /// <summary>Runs a connect attempt; returns null on success or the failure reason.</summary>
    private async Task<string?> RunAsync(Func<CancellationToken, Task> action)
    {
        ShowError(null);
        _busy = true;
        JoinCodeButton.IsEnabled = false;
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
            JoinCodeButton.IsEnabled = true;
            CreateButton.IsEnabled = _hostInfo is not null;
        }
    }
}

internal static class LoungeTargetExtensions
{
    public static byte[]? InviteKey(this LoungeTarget target) => target.ContentKey;
}
