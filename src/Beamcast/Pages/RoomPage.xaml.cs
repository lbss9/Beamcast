using System.Collections.ObjectModel;
using Beamcast.Audio;
using Beamcast.Capture;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Controls;
using Beamcast.Net;
using Beamcast.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace Beamcast.Pages;

/// <summary>Inside a lounge: who is here, what is being streamed, watch one, broadcast your own.</summary>
public sealed partial class RoomPage : Page
{
    // One video surface for the whole process, so fullscreen and page re-creation keep the picture.
    /// <summary>
    /// One tile per watched stream. Static so the SwapChainPanels survive navigating away from
    /// the room page and back (the streams keep playing in the meantime).
    /// </summary>
    private static readonly Dictionary<uint, WatchTile> Tiles = new();

    private readonly LoungeService _lounge = LoungeService.Instance;
    private readonly BroadcastService _broadcast = BroadcastService.Instance;
    private readonly WatchService _watch = WatchService.Instance;
    private readonly ObservableCollection<CaptureSource> _monitors = [];
    private readonly ObservableCollection<CaptureSource> _windows = [];
    private bool _loading = true;
    private bool _syncingSelection;
    private RoomDialogs.RoomForm? _settingsForm;

    public RoomPage()
    {
        Resources["KindGlyph"] = new KindGlyphConverter();
        InitializeComponent();
        MonitorsList.ItemsSource = _monitors;
        WindowsList.ItemsSource = _windows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        var settings = SettingsStore.Load();

        EncoderBox.Items.Clear();
        EncoderBox.Items.Add(Loc.Get("Encoder_Auto"));
        EncoderBox.Items.Add(EncoderLabel(VideoCodec.H264, "H.264"));
        EncoderBox.Items.Add(EncoderLabel(VideoCodec.Hevc, "HEVC"));
        EncoderBox.Items.Add(Loc.Get("Encoder_Vp8"));
        EncoderBox.SelectedIndex = Math.Max(0, Array.IndexOf(EncoderPreference.All, _broadcast.EncoderPreferenceValue));

        PresetBox.Items.Clear();
        foreach (var preset in QualityPreset.All)
            PresetBox.Items.Add(preset == QualityPreset.Source ? Loc.Get("Quality_PresetSource") : preset);
        PresetBox.SelectedIndex = Array.IndexOf(QualityPreset.All, _broadcast.Preset);

        FpsBox.Items.Clear();
        foreach (var fps in QualityPreset.FpsOptions)
            FpsBox.Items.Add($"{fps} fps");
        FpsBox.SelectedIndex = Array.IndexOf(QualityPreset.FpsOptions, _broadcast.Fps);

        AudioBox.Items.Clear();
        AudioBox.Items.Add(Loc.Get("Audio_Auto"));
        AudioBox.Items.Add(Loc.Get("Audio_System"));
        AudioBox.Items.Add(Loc.Get("Audio_App"));
        AudioBox.Items.Add(Loc.Get("Audio_Off"));
        AudioBox.SelectedIndex = Math.Max(0, Array.IndexOf(AudioMode.All, _broadcast.AudioModeValue));
        AudioBox.IsEnabled = AudioBroadcaster.IsSupported;

        BitrateBox.Value = _broadcast.BitrateKbps;
        CursorSwitch.IsOn = _broadcast.ShowCursor;
        AdaptiveSwitch.IsOn = _broadcast.AdaptiveQuality;
        ViewerSoundsSwitch.IsOn = _broadcast.ViewerSounds;
        TitleBox.Text = settings.StreamTitle;
        VolumeSlider.Value = settings.Volume;
        _watch.Volume = settings.Volume / 100f;
        MuteButton.IsChecked = _watch.IsMuted;

        RefreshSources();
        SyncSelectionFromService();

        _lounge.MembersChanged += OnMembersChanged;
        _lounge.StreamsChanged += OnStreamsChanged;
        _lounge.StateChanged += OnLoungeState;
        _lounge.RoomChanged += ApplyRoomInfo;
        _lounge.Notice += OnNotice;
        _broadcast.StateChanged += OnBroadcastState;
        _broadcast.PreviewStarted += OnPreviewStarted;
        _broadcast.StatsChanged += OnBroadcastStats;
        _broadcast.Error += OnError;
        _watch.WatchingChanged += OnWatchingChanged;
        _watch.FirstFrame += OnFirstFrame;
        _watch.StatsChanged += OnWatchStats;
        _watch.Stopped += OnWatchStopped;
        _broadcast.ViewerChanged += OnViewerChanged;
        if (App.Main is not null)
            App.Main.FullscreenExited += OnFullscreenExited;

        ApplyRoomInfo(_lounge.Room);
        ApplyFavorite(LoungeService.IsFavorite(_lounge.ServerUrl, _lounge.Code));
        OnLoungeState(_lounge.State);
        Preview.Bind(_broadcast.Preview);
        SyncTiles();

        _loading = false;
        OnMembersChanged();
        OnStreamsChanged();
        ApplyBroadcastState(_broadcast.State);
        OnBroadcastStats(_broadcast.LastStats);
        UpdateAudioHint();
        Tabs.SelectedIndex = _watch.IsWatching || _broadcast.State != BroadcastState.Live ? 0 : 1;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lounge.MembersChanged -= OnMembersChanged;
        _lounge.StreamsChanged -= OnStreamsChanged;
        _lounge.StateChanged -= OnLoungeState;
        _lounge.RoomChanged -= ApplyRoomInfo;
        _lounge.Notice -= OnNotice;
        _broadcast.StateChanged -= OnBroadcastState;
        _broadcast.PreviewStarted -= OnPreviewStarted;
        _broadcast.StatsChanged -= OnBroadcastStats;
        _broadcast.Error -= OnError;
        _watch.WatchingChanged -= OnWatchingChanged;
        _watch.FirstFrame -= OnFirstFrame;
        _watch.StatsChanged -= OnWatchStats;
        _watch.Stopped -= OnWatchStopped;
        _broadcast.ViewerChanged -= OnViewerChanged;
        if (App.Main is not null)
            App.Main.FullscreenExited -= OnFullscreenExited;
        Preview.Unbind();
        DetachTiles();
        PersistInputs();
    }

    private static string EncoderLabel(VideoCodec codec, string name) =>
        MfCodecs.HasHardwareEncoder(codec) ? Loc.Format("Encoder_Gpu", name) : Loc.Format("Encoder_Unavailable", name);

    private void PersistInputs()
    {
        SettingsStore.Update(s =>
        {
            s.QualityPreset = _broadcast.Preset;
            s.Fps = _broadcast.Fps;
            s.BitrateKbps = _broadcast.BitrateKbps;
            s.ShowCursor = _broadcast.ShowCursor;
            s.AdaptiveQuality = _broadcast.AdaptiveQuality;
            s.ViewerSounds = _broadcast.ViewerSounds;
            s.Encoder = _broadcast.EncoderPreferenceValue;
            s.AudioMode = _broadcast.AudioModeValue;
            s.StreamTitle = TitleBox.Text.Trim();
            s.Volume = (int)VolumeSlider.Value;
        });
    }

    // ----- lounge -----

    private void OnMembersChanged()
    {
        var members = _lounge.Members;
        MembersList.Items.Clear();
        foreach (var member in members)
            MembersList.Items.Add(BuildMemberRow(member));
        MembersCountText.Text = members.Count.ToString();
    }

    private UIElement BuildMemberRow(LoungeMember member)
    {
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon { Glyph = member.IsOwner ? "\uE735" : "\uE77B", FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = member.Name });
        if (member.IsOwner)
            name.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + Loc.Get("Room_OwnerBadge"), FontSize = 11 });
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        if (_lounge.IsOwner && !member.IsMe)
        {
            var kick = new Button { Content = new FontIcon { Glyph = "\uE8BB", FontSize = 11 }, Style = (Style)Application.Current.Resources["GhostButtonStyle"] };
            ToolTipService.SetToolTip(kick, Loc.Get("Room_Kick"));
            var id = member.Id;
            kick.Click += (_, _) => _lounge.Kick(id);
            Grid.SetColumn(kick, 2);
            grid.Children.Add(kick);
        }
        return grid;
    }

    private void ApplyRoomInfo(RoomInfo room)
    {
        LoungeNameText.Text = room.Name;
        CodeText.Text = room.Code;
        var badges = new List<string> { LoungeProtocol.DisplayHost(_lounge.ServerUrl), Loc.Get(room.IsPublic ? "Room_PublicBadge" : "Room_PrivateBadge") };
        if (room.IsTemporary)
            badges.Add(Loc.Get("Room_TemporaryBadge"));
        if (room.HasPassword)
            badges.Add(Loc.Get("Lounge_Locked"));
        if (room.Broadcast == BroadcastPolicy.Owner)
            badges.Add(Loc.Get("Broadcast_Owner"));
        LoungeInfoText.Text = string.Join(" · ", badges);
        RebuildSettingsTab(room);
        var canBroadcast = _lounge.CanBroadcast;
        BroadcastBlockedText.Visibility = canBroadcast ? Visibility.Collapsed : Visibility.Visible;
        GoLiveButton.IsEnabled = canBroadcast && _broadcast.State == BroadcastState.Preview;
        OnMembersChanged();
    }

    private void ApplyFavorite(bool favorite)
    {
        FavoriteButton.IsChecked = favorite;
        FavoriteIcon.Glyph = favorite ? "\uE735" : "\uE734";
    }

    private void OnFavoriteToggled(object sender, RoutedEventArgs e)
    {
        var favorite = FavoriteButton.IsChecked == true;
        LoungeService.SetFavorite(_lounge.ServerUrl, _lounge.Code, _lounge.Name, _lounge.Room.HasPassword, favorite);
        ApplyFavorite(favorite);
    }

    private void OnLoungeState(LoungeState state)
    {
        ReconnectBar.IsOpen = state == LoungeState.Reconnecting;
        if (state == LoungeState.Connected)
            ApplyRoomInfo(_lounge.Room);
    }

    private void OnNotice(string reason) => ErrorText.Text = LoungePage.DescribeReason(reason);

    // ----- settings tab -----

    /// <summary>
    /// The owner gets the editable room form plus invites and the danger zone; everyone else sees
    /// a read-only summary. Rebuilt whenever the host sends a new RoomInfo.
    /// </summary>
    private void RebuildSettingsTab(RoomInfo room)
    {
        var owner = _lounge.IsOwner;
        SettingsOwnerOnlyText.Visibility = owner ? Visibility.Collapsed : Visibility.Visible;
        SettingsSummary.Visibility = owner ? Visibility.Collapsed : Visibility.Visible;
        SettingsFormHost.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;
        SettingsActions.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;
        InvitesCard.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;
        DangerCard.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;

        if (owner)
        {
            _settingsForm = new RoomDialogs.RoomForm(room);
            _settingsForm.Panel.MinWidth = 0;
            SettingsFormHost.Content = _settingsForm.Panel;
            SettingsStatusText.Text = string.Empty;
            return;
        }

        _settingsForm = null;
        SettingsFormHost.Content = null;
        SettingsSummary.Children.Clear();
        string[] lines =
        [
            room.IsPublic ? Loc.Get("Visibility_Public") : Loc.Get("Visibility_Private"),
            room.IsTemporary ? Loc.Get("Kind_Temporary") : Loc.Get("Kind_Permanent"),
            room.HasPassword ? Loc.Get("Room_SummaryPassword") : Loc.Get("Room_SummaryNoPassword"),
            Loc.Format("Room_SummaryBroadcast", room.Broadcast == BroadcastPolicy.Owner ? Loc.Get("Broadcast_Owner") : Loc.Get("Broadcast_Everyone")),
            room.MaxMembers > 0 ? Loc.Format("Room_SummaryMaxMembers", room.MaxMembers) : Loc.Get("Room_SummaryNoLimit"),
        ];
        foreach (var line in lines)
            SettingsSummary.Children.Add(new TextBlock { Text = "\u2022  " + line, TextWrapping = TextWrapping.Wrap });
    }

    private async void OnSaveRoom(object sender, RoutedEventArgs e)
    {
        if (_settingsForm is null)
            return;
        var (update, newPassword) = _settingsForm.Read();
        SaveRoomButton.IsEnabled = false;
        SettingsStatusText.Text = Loc.Get("Room_SettingsSaving");
        try
        {
            _lounge.UpdateRoom(update);
            if (newPassword is { } password)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _lounge.ChangePasswordAsync(password, cts.Token);
            }
            SettingsStatusText.Text = Loc.Get("Room_SettingsSaved");
        }
        catch (Exception ex)
        {
            SettingsStatusText.Text = Loc.Format("Error_Generic", ex.Message);
        }
        finally
        {
            SaveRoomButton.IsEnabled = true;
        }
    }

    private async void OnCreateInvite(object sender, RoutedEventArgs e)
    {
        var choice = await RoomDialogs.InviteAsync(XamlRoot);
        if (choice is null)
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var invite = await _lounge.CreateInviteAsync(choice.Value.ExpiresIn, choice.Value.MaxUses, cts.Token);
            var package = new DataPackage();
            package.SetText(invite);
            Clipboard.SetContent(package);
            InviteStatusText.Text = Loc.Get("Room_SettingsInviteCopied");
        }
        catch (Exception)
        {
            InviteStatusText.Text = Loc.Get("Invite_Failed");
        }
    }

    private void OnRevokeInvites(object sender, RoutedEventArgs e)
    {
        _lounge.RevokeInvites();
        InviteStatusText.Text = Loc.Get("Room_SettingsRevoked");
    }

    private async void OnDeleteRoom(object sender, RoutedEventArgs e)
    {
        if (!await RoomDialogs.ConfirmDeleteAsync(XamlRoot, _lounge.Name))
            return;
        if (App.Main?.IsFullscreen == true)
            App.Main.ExitFullscreen();
        _broadcast.StopLive();
        _watch.StopAll("left");
        await _lounge.DeleteRoomAsync();
    }

    private void OnStreamsChanged()
    {
        var streams = _lounge.Streams;
        StreamsList.Items.Clear();
        foreach (var stream in streams)
            StreamsList.Items.Add(BuildStreamCard(stream));
        StreamsEmptyText.Visibility = streams.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTileTitles();
    }

    private UIElement BuildStreamCard(LoungeStream stream)
    {
        var watching = _watch.IsWatchingStream(stream.Id);
        var paused = stream.Meta.State == StreamStates.Paused;
        var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = stream.Meta.Title, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var detail = $"{stream.OwnerName} · {stream.Meta.Codec.ToUpperInvariant()} {stream.Meta.Width}×{stream.Meta.Height}";
        if (stream.Meta.Audio is not null)
            detail += " · ♪";
        if (paused)
            detail += " · " + Loc.Get("Room_Paused");
        text.Children.Add(new TextBlock { Text = detail, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        grid.Children.Add(text);

        Button button;
        if (stream.IsMine)
        {
            button = new Button { Content = Loc.Get("Room_Mine"), IsEnabled = false };
        }
        else if (watching)
        {
            button = new Button { Content = Loc.Get("Room_StopWatching/Content") };
            var stopId = stream.Id;
            button.Click += (_, _) => _watch.StopWatching(stopId);
        }
        else
        {
            button = new Button { Content = Loc.Get("Room_Watch"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            var id = stream.Id;
            button.Click += (_, _) =>
            {
                _watch.Watch(id);
                Tabs.SelectedIndex = 0;
            };
        }
        button.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    private void OnCopyInvite(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_lounge.InviteCode);
        Clipboard.SetContent(package);
        CopyInviteButton.Content = Loc.Get("Invite_Copied");
        _ = ResetCopyLabelAsync();
    }

    private async Task ResetCopyLabelAsync()
    {
        await Task.Delay(1500);
        CopyInviteButton.Content = Loc.Get("Room_CopyInvite/Content");
    }

    private async void OnLeave(object sender, RoutedEventArgs e)
    {
        if (App.Main?.IsFullscreen == true)
            App.Main.ExitFullscreen();
        _broadcast.StopLive();
        _watch.StopAll("left");
        await _lounge.LeaveAsync();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        // The SwapChainPanels live in different tabs; re-attach whichever became visible.
        if (Tabs.SelectedIndex == 0)
            BindTiles();
        else if (Tabs.SelectedIndex == 1)
            Preview.Bind(_broadcast.Preview);
    }

    // ----- watching -----

    /// <summary>Creates tiles for new streams, drops tiles for stopped ones, lays them out and binds them.</summary>
    private void SyncTiles()
    {
        var wanted = _watch.Watching;
        foreach (var id in Tiles.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            var gone = Tiles[id];
            Tiles.Remove(id);
            RemoveFromParent(gone.Root);
            gone.Video.Unbind();
        }
        foreach (var id in wanted)
        {
            if (!Tiles.ContainsKey(id))
                Tiles[id] = CreateTile(id);
        }
        LayoutTiles();
        BindTiles();
        UpdateTileTitles();
        var count = wanted.Count;
        WatchHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StopWatchButton.IsEnabled = count > 0;
        WatchTitleText.Text = count == 0 ? string.Empty : Loc.Format("Room_WatchingCount", count);
        UpdatePingText();
    }

    /// <summary>Arranges the tiles in a grid: 1 → whole area, 2 → side by side, 3–4 → 2×2, more → 3 columns.</summary>
    private void LayoutTiles()
    {
        var fullscreenContent = App.Main?.IsFullscreen == true ? App.Main.FullscreenContent : null;
        var tiles = _watch.Watching.Where(Tiles.ContainsKey).Select(id => Tiles[id]).Where(t => !ReferenceEquals(t.Root, fullscreenContent)).ToList();
        VideoHost.RowDefinitions.Clear();
        VideoHost.ColumnDefinitions.Clear();
        var count = tiles.Count;
        var columns = count <= 1 ? 1 : count <= 4 ? 2 : 3;
        var rows = Math.Max(1, (count + columns - 1) / columns);
        for (var i = 0; i < columns; i++)
            VideoHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows; i++)
            VideoHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumnSpan(WatchHint, columns);
        Grid.SetRowSpan(WatchHint, rows);
        for (var i = 0; i < count; i++)
        {
            var tile = tiles[i];
            if (!ReferenceEquals(tile.Root.Parent, VideoHost))
            {
                RemoveFromParent(tile.Root);
                VideoHost.Children.Add(tile.Root);
            }
            Grid.SetRow(tile.Root, i / columns);
            Grid.SetColumn(tile.Root, i % columns);
            tile.Root.Margin = count == 1 ? new Thickness(0) : new Thickness(3);
        }
    }

    private void BindTiles()
    {
        foreach (var (id, tile) in Tiles)
        {
            var presenter = _watch.PresenterFor(id);
            if (presenter is not null)
                tile.Video.Bind(presenter);
        }
    }

    /// <summary>Leaves the tiles alive (static) but out of this page's tree.</summary>
    private void DetachTiles()
    {
        foreach (var tile in Tiles.Values)
        {
            if (ReferenceEquals(tile.Root.Parent, VideoHost))
                VideoHost.Children.Remove(tile.Root);
        }
    }

    private static void RemoveFromParent(FrameworkElement element)
    {
        if (element.Parent is Panel panel)
            panel.Children.Remove(element);
    }

    private WatchTile CreateTile(uint streamId)
    {
        var tile = new WatchTile(streamId);
        tile.Video.DoubleTapped += (_, _) => App.Main?.DispatcherQueue.TryEnqueue(() => ToggleFullscreen(streamId));
        tile.FullscreenButton.Click += (_, _) => ToggleFullscreen(streamId);
        tile.StopButton.Click += (_, _) => _watch.StopWatching(streamId);
        if (!_watch.HasFrame(streamId))
            tile.ShowOverlay(Loc.Get("Watch_Waiting"));
        return tile;
    }

    private void UpdateTileTitles()
    {
        foreach (var (id, tile) in Tiles)
        {
            var stream = _lounge.FindStream(id);
            if (stream is null)
                continue;
            tile.TitleText.Text = Loc.Format("Room_WatchingTitle", stream.Meta.Title, stream.OwnerName);
            if (stream.Meta.State == StreamStates.Paused)
                tile.ShowOverlay(Loc.Get("Watch_PausedByHost"));
            else if (_watch.HasFrame(id))
                tile.HideOverlay();
            else
                tile.ShowOverlay(Loc.Get("Watch_Waiting"));
        }
    }

    private void UpdatePingText()
    {
        var rtt = _lounge.RoundTripMs;
        WatchStatsText.Text = _watch.IsWatching && rtt > 0 ? Loc.Format("Watch_Ping", rtt) : string.Empty;
    }

    private void OnWatchingChanged()
    {
        SyncTiles();
        OnStreamsChanged();
    }

    private void OnFirstFrame(uint streamId)
    {
        if (!Tiles.TryGetValue(streamId, out var tile))
            return;
        tile.Video.MarkFrame();
        var stream = _lounge.FindStream(streamId);
        if (stream is null || stream.Meta.State != StreamStates.Paused)
            tile.HideOverlay();
    }

    private void OnWatchStats(uint streamId, ViewerStats stats)
    {
        if (!Tiles.TryGetValue(streamId, out var tile))
            return;
        var audio = stats.AudioKbps > 0 ? $"  ♪ {stats.AudioKbps:F0} kbps" : string.Empty;
        var latency = stats.LatencyMs >= 0 ? "  " + Loc.Format("Watch_Latency", stats.LatencyMs.ToString("F0")) : string.Empty;
        tile.StatsText.Text = $"{stats.Width}×{stats.Height}  {stats.Fps:F0} fps  {stats.Kbps / 1000:F1} Mbps  dec {stats.DecodeMs:F1} ms{audio}{latency}";
        UpdatePingText();
    }

    private void OnWatchStopped(uint streamId, string reason)
    {
        if (Tiles.TryGetValue(streamId, out var tile) && App.Main?.IsFullscreen == true && ReferenceEquals(App.Main.FullscreenContent, tile.Root))
            App.Main.ExitFullscreen();
        SyncTiles();
    }

    private void OnStopWatching(object sender, RoutedEventArgs e) => _watch.StopAll();

    private void OnMuteToggled(object sender, RoutedEventArgs e) => _watch.IsMuted = MuteButton.IsChecked == true;

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
            return;
        _watch.Volume = (float)(e.NewValue / 100.0);
    }

    private static void ToggleFullscreen(uint streamId)
    {
        var main = App.Main;
        if (main is null || !Tiles.TryGetValue(streamId, out var tile))
            return;
        if (main.IsFullscreen)
        {
            main.ExitFullscreen();
            return;
        }
        RemoveFromParent(tile.Root);
        tile.Root.Margin = new Thickness(0);
        main.EnterFullscreen(tile.Root);
    }

    private void OnFullscreenExited(UIElement content) => SyncTiles();

    /// <summary>The visual for one watched stream: video, overlay text and a small header with title, stats and buttons.</summary>
    private sealed class WatchTile
    {
        public WatchTile(uint streamId)
        {
            StreamId = streamId;
            Video = new GpuVideoView();
            var white = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);

            OverlayText = new TextBlock
            {
                Foreground = white,
                Opacity = 0.8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
            };
            Overlay = new Grid { Visibility = Visibility.Collapsed };
            Overlay.Children.Add(OverlayText);

            TitleText = new TextBlock { Foreground = white, TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] };
            StatsText = new TextBlock { Foreground = white, Opacity = 0.8, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas, Courier New"), TextTrimming = TextTrimming.CharacterEllipsis, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };
            var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(TitleText);
            texts.Children.Add(StatsText);

            FullscreenButton = HeaderButton("\uE740", Loc.Get("Watch_TileFullscreen"), white);
            StopButton = HeaderButton("\uE711", Loc.Get("Room_StopWatching/Content"), white);

            var header = new Grid { ColumnSpacing = 4, Padding = new Thickness(10, 4, 6, 4), VerticalAlignment = VerticalAlignment.Top, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0, 0, 0)) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(texts);
            Grid.SetColumn(FullscreenButton, 1);
            header.Children.Add(FullscreenButton);
            Grid.SetColumn(StopButton, 2);
            header.Children.Add(StopButton);

            var grid = new Grid();
            grid.Children.Add(Video);
            grid.Children.Add(Overlay);
            grid.Children.Add(header);
            Root = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x0A, 0x0D, 0x12)),
                Child = grid,
            };
        }

        public uint StreamId { get; }
        public Border Root { get; }
        public GpuVideoView Video { get; }
        public Grid Overlay { get; }
        public TextBlock OverlayText { get; }
        public TextBlock TitleText { get; }
        public TextBlock StatsText { get; }
        public Button FullscreenButton { get; }
        public Button StopButton { get; }

        public void ShowOverlay(string text)
        {
            OverlayText.Text = text;
            Overlay.Visibility = Visibility.Visible;
        }

        public void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

        private static Button HeaderButton(string glyph, string tooltip, Microsoft.UI.Xaml.Media.Brush foreground)
        {
            var button = new Button
            {
                Content = new FontIcon { Glyph = glyph, FontSize = 13, Foreground = foreground },
                Style = (Style)Application.Current.Resources["GhostButtonStyle"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(button, tooltip);
            return button;
        }
    }

    // ----- broadcasting -----

    private void RefreshSources()
    {
        var selectedKey = _broadcast.Source?.Key;
        _syncingSelection = true;
        _monitors.Clear();
        foreach (var monitor in SafeTry.Run(CaptureSourceEnumerator.Monitors) ?? [])
            _monitors.Add(monitor);
        _windows.Clear();
        foreach (var window in SafeTry.Run(CaptureSourceEnumerator.Windows) ?? [])
            _windows.Add(window);
        _syncingSelection = false;
        if (selectedKey is not null)
            SyncSelectionFromService();
    }

    private void SyncSelectionFromService()
    {
        var key = _broadcast.Source?.Key;
        _syncingSelection = true;
        MonitorsList.SelectedItem = key is null ? null : _monitors.FirstOrDefault(m => m.Key == key);
        WindowsList.SelectedItem = key is null ? null : _windows.FirstOrDefault(w => w.Key == key);
        _syncingSelection = false;
    }

    private void OnRefreshSources(object sender, RoutedEventArgs e) => RefreshSources();

    private void OnMonitorSelected(object sender, SelectionChangedEventArgs e) => SelectFromList(MonitorsList, WindowsList);

    private void OnWindowSelected(object sender, SelectionChangedEventArgs e) => SelectFromList(WindowsList, MonitorsList);

    private void SelectFromList(ListView list, ListView other)
    {
        if (_syncingSelection || _loading)
            return;
        if (list.SelectedItem is not CaptureSource source)
            return;

        _syncingSelection = true;
        other.SelectedItem = null;
        _syncingSelection = false;

        ErrorText.Text = string.Empty;
        try
        {
            _broadcast.SelectSource(source);
            UpdateAudioHint();
        }
        catch (Exception ex)
        {
            Diag.Log("room: selecting source failed: " + ex);
            ErrorText.Text = Loc.Format("Error_Capture", ex.Message);
            _syncingSelection = true;
            list.SelectedItem = null;
            _syncingSelection = false;
        }
    }

    private void OnEncoderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EncoderBox.SelectedIndex < 0)
            return;
        _broadcast.EncoderPreferenceValue = EncoderPreference.All[EncoderBox.SelectedIndex];
        BitrateBox.Value = QualityPreset.SuggestedBitrate(_broadcast.Preset, _broadcast.Fps, ResolvedCodecName());
    }

    private string ResolvedCodecName() => MfCodecs.Resolve(_broadcast.EncoderPreferenceValue).ToWireName();

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        if (PresetBox.SelectedIndex >= 0)
            _broadcast.Preset = QualityPreset.All[PresetBox.SelectedIndex];
        if (FpsBox.SelectedIndex >= 0)
            _broadcast.Fps = QualityPreset.FpsOptions[FpsBox.SelectedIndex];
        BitrateBox.Value = QualityPreset.SuggestedBitrate(_broadcast.Preset, _broadcast.Fps, ResolvedCodecName());
    }

    private void OnBitrateChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value))
            return;
        _broadcast.BitrateKbps = (int)sender.Value;
    }

    private void OnCursorToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _broadcast.ShowCursor = CursorSwitch.IsOn;
    }

    private void OnAdaptiveToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _broadcast.AdaptiveQuality = AdaptiveSwitch.IsOn;
    }

    private void OnViewerSoundsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _broadcast.ViewerSounds = ViewerSoundsSwitch.IsOn;
    }

    private void OnViewerChanged(string name, bool joined)
    {
        ViewerNoteText.Text = Loc.Format(joined ? "Stream_ViewerJoined" : "Stream_ViewerLeft", name);
        OnBroadcastStats(_broadcast.LastStats);
    }

    private void OnAudioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AudioBox.SelectedIndex < 0)
            return;
        _broadcast.AudioModeValue = AudioMode.All[AudioBox.SelectedIndex];
        UpdateAudioHint();
    }

    private void UpdateAudioHint()
    {
        if (!AudioBroadcaster.IsSupported)
        {
            AudioHintText.Text = Loc.Get("Audio_Unsupported");
            return;
        }
        if (_broadcast.AudioActive)
        {
            var info = _broadcast.AudioInfo;
            AudioHintText.Text = info.Mode == AudioMode.App
                ? info.AppGone ? Loc.Get("Audio_DescAppGone") : Loc.Format("Audio_DescApp", info.AppName ?? string.Empty)
                : info.ExcludedApps.Count == 0 ? Loc.Get("Audio_DescSystem") : Loc.Format("Audio_DescSystemExcept", string.Join(", ", info.ExcludedApps));
            return;
        }
        var resolved = AudioBroadcaster.Resolve(_broadcast.AudioModeValue, _broadcast.Source);
        AudioHintText.Text = resolved switch
        {
            AudioMode.Off => Loc.Get("Audio_HintOff"),
            AudioMode.App => Loc.Get("Audio_HintApp"),
            _ => Loc.Get("Audio_HintSystem"),
        };
    }

    private async void OnGoLive(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (_broadcast.Source is null)
        {
            ErrorText.Text = Loc.Get("Error_NoSource");
            return;
        }

        GoLiveButton.IsEnabled = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _broadcast.GoLiveAsync(TitleBox.Text, cts.Token);
            PersistInputs();
            UpdateAudioHint();
        }
        catch (Exception ex)
        {
            ErrorText.Text = Loc.Format("Error_GoLive", ex.Message);
        }
        finally
        {
            GoLiveButton.IsEnabled = _broadcast.State == BroadcastState.Preview;
        }
    }

    private void OnPause(object sender, RoutedEventArgs e) => _broadcast.SetPaused(!_broadcast.IsPaused);

    private void OnStop(object sender, RoutedEventArgs e) => _broadcast.StopLive();

    private void OnBroadcastState(BroadcastState state) => ApplyBroadcastState(state);

    private void ApplyBroadcastState(BroadcastState state)
    {
        var live = state == BroadcastState.Live;
        LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        PausedBadge.Visibility = live && _broadcast.IsPaused ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        GoLiveButton.IsEnabled = state == BroadcastState.Preview && _lounge.CanBroadcast;
        StopButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = Loc.Get(_broadcast.IsPaused ? "Action_Resume" : "Action_Pause/Content");
        EncoderBox.IsEnabled = !live;
        TitleBox.IsEnabled = !live;
        PreviewHint.Visibility = state == BroadcastState.Idle ? Visibility.Visible : Visibility.Collapsed;
        if (state == BroadcastState.Idle)
        {
            Preview.Clear();
            SyncSelectionFromService();
        }
        OnBroadcastStats(_broadcast.LastStats);
        UpdateAudioHint();
    }

    private void OnPreviewStarted()
    {
        Preview.MarkFrame();
        PreviewHint.Visibility = Visibility.Collapsed;
    }

    private void OnBroadcastStats(HostStats stats)
    {
        if (_broadcast.State == BroadcastState.Live)
        {
            var audio = stats.AudioKbps > 0 ? $"  ♪ {stats.AudioKbps:F0} kbps" : string.Empty;
            var adapted = stats.Adapted ? "  " + Loc.Format("Stream_Adapted", stats.TargetKbps) : string.Empty;
            StatsText.Text = $"{stats.Codec}  {stats.Width}×{stats.Height}  {stats.Fps:F0} fps  {stats.Kbps / 1000:F1} Mbps  {stats.EncodeMs:F1} ms{audio}{adapted}";
            ViewersText.Text = Loc.Format("Stream_Viewers", _broadcast.ViewerCount);
            ViewersBadge.Visibility = Visibility.Visible;
        }
        else
        {
            StatsText.Text = _broadcast.Source?.SizeLabel ?? string.Empty;
            ViewersBadge.Visibility = Visibility.Collapsed;
            ViewerNoteText.Text = string.Empty;
        }
        StatsBadge.Visibility = StatsText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnError(string message) => ErrorText.Text = message;
}

/// <summary>Maps a capture source kind to a Segoe Fluent glyph.</summary>
public sealed class KindGlyphConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is CaptureSourceKind.Monitor ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
