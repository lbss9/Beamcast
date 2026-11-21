using System.Collections.ObjectModel;
using Beamcast.Audio;
using Beamcast.Capture;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Controls;
using Beamcast.Net;
using Beamcast.Services;
using Beamcast.Ui;
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
    private static readonly BroadcastProfile[] Profiles = [BroadcastProfile.Manual, BroadcastProfile.Recommended, BroadcastProfile.Performance, BroadcastProfile.Balanced, BroadcastProfile.Quality];

    private bool _loading = true;
    private bool _syncingSelection;
    private bool _applyingProfile;
    private CancellationTokenSource? _calibrateCts;
    private RoomDialogs.RoomForm? _settingsForm;
    private bool _favorite;

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
        Tabs.SetItems(Loc.Get("Room_WatchTab/Header"), Loc.Get("Room_BroadcastTab/Header"), Loc.Get("Room_SettingsTab/Header"));

        EncoderBox.Items.Clear();
        EncoderBox.Items.Add(MfCodecs.HasHardwareEncoder(VideoCodec.H264)
            ? Loc.Format("Encoder_AutoGpu", MfCodecs.HardwareEncoderVendor(VideoCodec.H264))
            : Loc.Get("Encoder_AutoCpu"));
        EncoderBox.Items.Add(EncoderLabel(VideoCodec.H264, "H.264"));
        EncoderBox.Items.Add(EncoderLabel(VideoCodec.Hevc, "HEVC"));
        EncoderBox.Items.Add(EncoderLabel(VideoCodec.Av1, "AV1"));
        EncoderBox.Items.Add(Loc.Get("Encoder_Vp8"));
        EncoderBox.SelectedIndex = Math.Max(0, Array.IndexOf(EncoderPreference.All, _broadcast.EncoderPreferenceValue));
        UpdateEncoderHint();

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
        ScreenSwitch.IsOn = MfCodecs.Tuning.ScreenContent;
        VbrSwitch.IsOn = MfCodecs.Tuning.CappedVbr;

        ProfileBox.Items.Clear();
        ProfileBox.Items.Add(Loc.Get("Profile_Manual"));
        ProfileBox.Items.Add(Loc.Get("Profile_Recommended"));
        ProfileBox.Items.Add(Loc.Get("Profile_Performance"));
        ProfileBox.Items.Add(Loc.Get("Profile_Balanced"));
        ProfileBox.Items.Add(Loc.Get("Profile_Quality"));
        ProfileBox.SelectedIndex = Math.Max(0, Array.IndexOf(Profiles, BroadcastProfileNames.Parse(settings.BroadcastProfile)));
        CalibrateButton.IsEnabled = ProfileBox.SelectedIndex > 0;
        AdaptiveSwitch.IsOn = _broadcast.AdaptiveQuality;
        ViewerSoundsSwitch.IsOn = _broadcast.ViewerSounds;
        StandbySwitch.IsOn = _broadcast.StandbyWithoutViewers;
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
        _lounge.PathChanged += OnPathChanged;
        if (App.Main is not null)
            App.Main.FullscreenExited += OnFullscreenExited;

        ApplyRoomInfo(_lounge.Room);
        ApplyFavorite(LoungeService.IsFavorite(_lounge.ServerUrl, _lounge.Code));
        OnLoungeState(_lounge.State);
        Preview.Bind(_broadcast.Preview);
        SyncTiles();
        Diag.Log($"room: page loaded, watching {_watch.Watching.Count}, live {_broadcast.State == BroadcastState.Live}");

        _loading = false;
        OnMembersChanged();
        OnStreamsChanged();
        ApplyBroadcastState(_broadcast.State);
        OnBroadcastStats(_broadcast.LastStats);
        UpdateAudioHint();
        Tabs.SelectedIndex = _watch.IsWatching || _broadcast.State != BroadcastState.Live ? 0 : 1;
        ApplyTab();
        Beam.LabelButtons(this);
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
        _lounge.PathChanged -= OnPathChanged;
        if (App.Main is not null)
            App.Main.FullscreenExited -= OnFullscreenExited;
        Preview.Unbind();
        DetachTiles();
        PersistInputs();
    }

    private static string EncoderLabel(VideoCodec codec, string name) =>
        MfCodecs.HasHardwareEncoder(codec)
            ? Loc.Format("Encoder_Gpu", name, MfCodecs.HardwareEncoderVendor(codec))
            : Loc.Format("Encoder_Unavailable", name);

    private static string CodecTitle(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "H.264",
        VideoCodec.Hevc => "HEVC",
        VideoCodec.Av1 => "AV1",
        _ => "VP8",
    };

    /// <summary>Says under the codec box where the video gets encoded (which chip) and what that costs.</summary>
    private void UpdateEncoderHint()
    {
        var preference = EncoderPreference.All[Math.Max(0, EncoderBox.SelectedIndex)];
        var resolved = MfCodecs.Resolve(preference);
        var text = resolved == VideoCodec.Vp8
            ? Loc.Get("Encoder_HintCpu")
            : Loc.Format("Encoder_HintGpu", CodecTitle(resolved), MfCodecs.HardwareEncoderVendor(resolved));
        if (resolved == VideoCodec.Hevc)
            text += " " + Loc.Get("Encoder_HintHevc");
        else if (resolved == VideoCodec.Av1)
            text += " " + Loc.Get("Encoder_HintAv1");
        var asked = preference switch
        {
            EncoderPreference.H264 => VideoCodec.H264,
            EncoderPreference.Hevc => VideoCodec.Hevc,
            EncoderPreference.Av1 => VideoCodec.Av1,
            _ => resolved,
        };
        if (asked != resolved)
            text = Loc.Format("Encoder_HintFallback", CodecTitle(asked), CodecTitle(resolved)) + " " + text;
        EncoderHintText.Text = text;
        var driver = resolved == VideoCodec.Vp8 ? string.Empty : MfCodecs.HardwareEncoderName(resolved);
        ToolTipService.SetToolTip(EncoderBox, driver.Length > 0 ? driver : null);
    }

    private void PersistInputs()
    {
        SettingsStore.Update(s =>
        {
            s.QualityPreset = _broadcast.Preset;
            s.Fps = _broadcast.Fps;
            s.BitrateKbps = _broadcast.BitrateKbps;
            s.ShowCursor = _broadcast.ShowCursor;
            s.BroadcastProfile = BroadcastProfileNames.ToName(Profiles[Math.Max(0, ProfileBox.SelectedIndex)]);
            s.AdaptiveQuality = _broadcast.AdaptiveQuality;
            s.ViewerSounds = _broadcast.ViewerSounds;
            s.StandbyWithoutViewers = _broadcast.StandbyWithoutViewers;
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
        UpdateRoomLine();
    }

    private UIElement BuildMemberRow(LoungeMember member)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 6, 4, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Beam.Avatar(member.Name, 30, accent: member.IsMe));

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Beam.Text(member.IsMe ? Loc.Format("Room_You", member.Name) : member.Name, 13.5));
        if (member.IsOwner)
        {
            var crown = Beam.Icon("\uE735", 11, "Warn");
            ToolTipService.SetToolTip(crown, Loc.Get("Room_OwnerBadge"));
            name.Children.Add(crown);
        }
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        if (_lounge.Streams.Any(s => s.OwnerId == member.Id))
        {
            var live = Beam.Chip(Loc.Get("Room_MemberLive"), "Text2", "Surface2", strong: false);
            ((StackPanel)live.Child).Children.Insert(0, new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 6, Height = 6, Fill = Beam.Brush("Live"), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(live, 2);
            grid.Children.Add(live);
        }
        else if (_lounge.IsOwner && !member.IsMe)
        {
            var kick = new Button { Content = new FontIcon { Glyph = "\uE8BB", FontSize = 11 }, Style = Beam.Style("GhostButtonStyle"), Width = 28, Height = 28 };
            ToolTipService.SetToolTip(kick, Loc.Get("Room_Kick"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(kick, Loc.Get("Room_Kick"));
            var id = member.Id;
            kick.Click += (_, _) => _lounge.Kick(id);
            Grid.SetColumn(kick, 2);
            grid.Children.Add(kick);
        }
        if (_lounge.IsOwner && !member.IsMe)
        {
            var menu = new MenuFlyout();
            var kickItem = new MenuFlyoutItem { Text = Loc.Get("Room_Kick"), Icon = new FontIcon { Glyph = "\uE8BB" } };
            var kickId = member.Id;
            kickItem.Click += (_, _) => _lounge.Kick(kickId);
            menu.Items.Add(kickItem);
            grid.ContextFlyout = menu;
            grid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        return grid;
    }

    private void ApplyRoomInfo(RoomInfo room)
    {
        var title = room.Name.Length > 0 ? room.Name : room.Code;
        LoungeNameText.Text = title;
        CodeText.Text = room.Code;
        RoomAvatar.Content = Beam.Avatar(title, 40, square: true);

        RoomBadges.Children.Clear();
        if (_lounge.IsOwner)
            RoomBadges.Children.Add(Beam.Chip(Loc.Get("Room_OwnerBadge"), "Warn", "WarnSoft", glyph: "\uE735"));
        if (room.HasPassword)
        {
            var locked = Beam.Chip(null, glyph: "\uE72E");
            ToolTipService.SetToolTip(locked, Loc.Get("Lounge_Locked"));
            RoomBadges.Children.Add(locked);
        }
        if (room.IsTemporary)
            RoomBadges.Children.Add(Beam.Chip(Loc.Get("Room_TemporaryBadge"), strong: false));
        if (room.Broadcast == BroadcastPolicy.Owner)
            RoomBadges.Children.Add(Beam.Chip(Loc.Get("Broadcast_Owner"), strong: false));

        UpdateRoomLine();
        RebuildSettingsTab(room);
        var canBroadcast = _lounge.CanBroadcast;
        BroadcastBlockedText.Visibility = canBroadcast ? Visibility.Collapsed : Visibility.Visible;
        GoLiveButton.IsEnabled = canBroadcast && _broadcast.State == BroadcastState.Preview;
        UpdateWatchHint();
        OnMembersChanged();
    }

    /// <summary>The line under the room name: which host, how many people, how many live.</summary>
    private void UpdateRoomLine()
    {
        var parts = new List<string>
        {
            LoungeProtocol.DisplayHost(_lounge.ServerUrl),
            Loc.Format(_lounge.Members.Count == 1 ? "Room_PeopleOne" : "Room_PeopleMany", _lounge.Members.Count),
        };
        if (_lounge.Streams.Count > 0)
            parts.Add(Loc.Format("Room_LiveCount", _lounge.Streams.Count));
        LoungeInfoText.Text = string.Join("  ·  ", parts);
    }

    /// <summary>What the empty stage says: pick one of the live streams, or nobody is live yet.</summary>
    private void UpdateWatchHint()
    {
        var others = _lounge.Streams.Count(s => !s.IsMine);
        WatchHintTitle.Text = Loc.Get(others > 0 ? "Room_WatchPickTitle" : "Room_WatchEmptyTitle");
        WatchHintBody.Text = Loc.Get(others > 0 ? "Room_WatchHint/Text" : "Room_WatchEmptyBody");
        WatchHintShare.Visibility = _lounge.CanBroadcast && _broadcast.State != BroadcastState.Live ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnShareMyScreen(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;

    private void ApplyFavorite(bool favorite)
    {
        _favorite = favorite;
        FavoriteIcon.Glyph = favorite ? "\uE735" : "\uE734";
        FavoriteIcon.Foreground = Beam.Brush(favorite ? "Warn" : "Text2");
    }

    private void OnFavoriteToggled(object sender, RoutedEventArgs e)
    {
        var favorite = !_favorite;
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
}

/// <summary>Maps a capture source kind to a Segoe Fluent glyph.</summary>
public sealed class KindGlyphConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is CaptureSourceKind.Monitor ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
