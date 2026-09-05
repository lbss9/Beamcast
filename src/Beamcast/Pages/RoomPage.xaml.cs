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
    private static GpuVideoView? _sharedVideo;
    private static Grid? _overlay;
    private static TextBlock? _overlayText;

    private static GpuVideoView SharedVideo => _sharedVideo ??= CreateVideo();
    private static Grid Overlay => _overlay ??= CreateOverlay();

    private readonly LoungeService _lounge = LoungeService.Instance;
    private readonly BroadcastService _broadcast = BroadcastService.Instance;
    private readonly WatchService _watch = WatchService.Instance;
    private readonly ObservableCollection<CaptureSource> _monitors = [];
    private readonly ObservableCollection<CaptureSource> _windows = [];
    private bool _loading = true;
    private bool _syncingSelection;

    public RoomPage()
    {
        Resources["KindGlyph"] = new KindGlyphConverter();
        InitializeComponent();
        MonitorsList.ItemsSource = _monitors;
        WindowsList.ItemsSource = _windows;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static GpuVideoView CreateVideo()
    {
        var view = new GpuVideoView();
        view.DoubleTapped += (_, _) => App.Main?.DispatcherQueue.TryEnqueue(ToggleFullscreenStatic);
        return view;
    }

    private static Grid CreateOverlay()
    {
        _overlayText = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White),
            Opacity = 0.8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var overlay = new Grid { Visibility = Visibility.Collapsed };
        overlay.Children.Add(_overlayText);
        return overlay;
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
        if (App.Main is not null)
            App.Main.FullscreenExited += OnFullscreenExited;

        MenuInvite.Text = Loc.Get("Room_MenuInvite");
        MenuEdit.Text = Loc.Get("Room_MenuEdit");
        MenuRevoke.Text = Loc.Get("Room_MenuRevoke");
        MenuDelete.Text = Loc.Get("Room_MenuDelete");
        ApplyRoomInfo(_lounge.Room);
        ApplyFavorite(LoungeService.IsFavorite(_lounge.ServerUrl, _lounge.Code));
        OnLoungeState(_lounge.State);
        Preview.Bind(_broadcast.Preview);
        AttachVideo();
        SharedVideo.Bind(_watch.Presenter);

        _loading = false;
        OnMembersChanged();
        OnStreamsChanged();
        ApplyBroadcastState(_broadcast.State);
        OnBroadcastStats(_broadcast.LastStats);
        ApplyWatching(_watch.StreamId);
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
        if (App.Main is not null)
            App.Main.FullscreenExited -= OnFullscreenExited;
        Preview.Unbind();
        DetachVideo();
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
        OwnerMenu.Visibility = _lounge.IsOwner ? Visibility.Visible : Visibility.Collapsed;
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

    private async void OnMenuInvite(object sender, RoutedEventArgs e)
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
            CopyInviteButton.Content = Loc.Get("Invite_Copied");
            _ = ResetCopyLabelAsync();
        }
        catch (Exception)
        {
            ErrorText.Text = Loc.Get("Invite_Failed");
        }
    }

    private async void OnMenuEdit(object sender, RoutedEventArgs e)
    {
        var result = await RoomDialogs.EditAsync(XamlRoot, _lounge.Room);
        if (result is null)
            return;
        _lounge.UpdateRoom(result.Value.Update);
        if (result.Value.NewPassword is { } password)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await _lounge.ChangePasswordAsync(password, cts.Token);
            }
            catch (Exception ex)
            {
                ErrorText.Text = Loc.Format("Error_Generic", ex.Message);
            }
        }
    }

    private void OnMenuRevoke(object sender, RoutedEventArgs e) => _lounge.RevokeInvites();

    private async void OnMenuDelete(object sender, RoutedEventArgs e)
    {
        if (!await RoomDialogs.ConfirmDeleteAsync(XamlRoot, _lounge.Name))
            return;
        if (App.Main?.IsFullscreen == true)
            App.Main.ExitFullscreen();
        _broadcast.StopLive();
        _watch.StopWatching("left");
        await _lounge.DeleteRoomAsync();
    }

    private void OnStreamsChanged()
    {
        var streams = _lounge.Streams;
        StreamsList.Items.Clear();
        foreach (var stream in streams)
            StreamsList.Items.Add(BuildStreamCard(stream));
        StreamsEmptyText.Visibility = streams.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_watch.IsWatching)
            UpdateWatchTitle();
    }

    private UIElement BuildStreamCard(LoungeStream stream)
    {
        var watching = _watch.StreamId == stream.Id;
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
            button.Click += (_, _) => _watch.StopWatching();
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
        _watch.StopWatching("left");
        await _lounge.LeaveAsync();
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        // The SwapChainPanels live in different tabs; re-attach whichever became visible.
        if (Tabs.SelectedIndex == 0)
            SharedVideo.Bind(_watch.Presenter);
        else
            Preview.Bind(_broadcast.Preview);
    }

    // ----- watching -----

    private void AttachVideo()
    {
        if (App.Main?.IsFullscreen == true)
            return;
        DetachVideoFromParent();
        VideoHost.Children.Insert(0, SharedVideo);
        VideoHost.Children.Add(Overlay);
    }

    private void DetachVideo()
    {
        if (VideoHost.Children.Contains(SharedVideo))
            VideoHost.Children.Remove(SharedVideo);
        if (VideoHost.Children.Contains(Overlay))
            VideoHost.Children.Remove(Overlay);
    }

    private static void DetachVideoFromParent()
    {
        if (SharedVideo.Parent is Panel panel)
            panel.Children.Remove(SharedVideo);
        if (Overlay.Parent is Panel overlayPanel)
            overlayPanel.Children.Remove(Overlay);
    }

    private void OnWatchingChanged(uint streamId)
    {
        ApplyWatching(streamId);
        OnStreamsChanged();
    }

    private void ApplyWatching(uint streamId)
    {
        var watching = streamId != 0;
        WatchHint.Visibility = watching ? Visibility.Collapsed : Visibility.Visible;
        StopWatchButton.IsEnabled = watching;
        FullscreenButton.IsEnabled = watching;
        if (!watching)
        {
            WatchTitleText.Text = string.Empty;
            WatchStatsText.Text = string.Empty;
            Overlay.Visibility = Visibility.Collapsed;
            SharedVideo.Clear();
            return;
        }
        UpdateWatchTitle();
        if (!_watch.HasFrame)
            ShowOverlay(Loc.Get("Watch_Waiting"));
    }

    private void UpdateWatchTitle()
    {
        var stream = _lounge.FindStream(_watch.StreamId);
        if (stream is null)
            return;
        WatchTitleText.Text = Loc.Format("Room_WatchingTitle", stream.Meta.Title, stream.OwnerName);
        if (stream.Meta.State == StreamStates.Paused)
            ShowOverlay(Loc.Get("Watch_PausedByHost"));
        else if (_watch.HasFrame)
            Overlay.Visibility = Visibility.Collapsed;
    }

    private void OnFirstFrame()
    {
        SharedVideo.MarkFrame();
        var stream = _lounge.FindStream(_watch.StreamId);
        if (stream is null || stream.Meta.State != StreamStates.Paused)
            Overlay.Visibility = Visibility.Collapsed;
    }

    private void OnWatchStats(ViewerStats stats)
    {
        var audio = stats.AudioKbps > 0 ? $"  ♪ {stats.AudioKbps:F0} kbps" : string.Empty;
        WatchStatsText.Text = $"{stats.Width}×{stats.Height}  {stats.Fps:F0} fps  {stats.Kbps / 1000:F1} Mbps  dec {stats.DecodeMs:F1} ms{audio}";
    }

    private void OnWatchStopped(string reason)
    {
        if (App.Main?.IsFullscreen == true)
            App.Main.ExitFullscreen();
        if (reason == "ended")
            ShowOverlay(Loc.Get("Watch_Ended"));
    }

    private void OnStopWatching(object sender, RoutedEventArgs e) => _watch.StopWatching();

    private void OnMuteToggled(object sender, RoutedEventArgs e) => _watch.IsMuted = MuteButton.IsChecked == true;

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
            return;
        _watch.Volume = (float)(e.NewValue / 100.0);
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreenStatic();

    private static void ToggleFullscreenStatic()
    {
        var main = App.Main;
        if (main is null || !WatchService.Instance.IsWatching)
            return;
        if (main.IsFullscreen)
        {
            main.ExitFullscreen();
            return;
        }
        DetachVideoFromParent();
        var host = new Grid();
        host.Children.Add(SharedVideo);
        host.Children.Add(Overlay);
        main.EnterFullscreen(host);
    }

    private void OnFullscreenExited(UIElement content)
    {
        if (content is Panel panel)
            panel.Children.Clear();
        AttachVideo();
    }

    private static void ShowOverlay(string text)
    {
        var overlay = Overlay;
        if (_overlayText is not null)
            _overlayText.Text = text;
        overlay.Visibility = Visibility.Visible;
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
            StatsText.Text = $"{stats.Codec}  {stats.Width}×{stats.Height}  {stats.Fps:F0} fps  {stats.Kbps / 1000:F1} Mbps  {stats.EncodeMs:F1} ms{audio}";
        }
        else
        {
            StatsText.Text = _broadcast.Source?.SizeLabel ?? string.Empty;
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
