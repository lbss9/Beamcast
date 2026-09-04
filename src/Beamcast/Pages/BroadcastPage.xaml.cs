using System.Collections.ObjectModel;
using Beamcast.Capture;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Net;
using Beamcast.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;

namespace Beamcast.Pages;

public sealed partial class BroadcastPage : Page
{
    private static readonly string PublicIpPending = "…";

    private readonly ObservableCollection<CaptureSource> _monitors = [];
    private readonly ObservableCollection<CaptureSource> _windows = [];
    private readonly ObservableCollection<ViewerInfo> _viewers = [];
    private readonly BroadcastService _service = BroadcastService.Instance;
    private bool _loading = true;
    private bool _syncingSelection;
    private string? _publicIp;

    public BroadcastPage()
    {
        Resources["KindGlyph"] = new KindGlyphConverter();
        InitializeComponent();
        MonitorsList.ItemsSource = _monitors;
        WindowsList.ItemsSource = _windows;
        ViewersList.ItemsSource = _viewers;
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
        EncoderBox.SelectedIndex = Math.Max(0, Array.IndexOf(EncoderPreference.All, _service.EncoderPreferenceValue));

        PresetBox.Items.Clear();
        foreach (var preset in QualityPreset.All)
            PresetBox.Items.Add(preset == QualityPreset.Source ? Loc.Get("Quality_PresetSource") : preset);
        PresetBox.SelectedIndex = Array.IndexOf(QualityPreset.All, _service.Preset);

        FpsBox.Items.Clear();
        foreach (var fps in QualityPreset.FpsOptions)
            FpsBox.Items.Add($"{fps} fps");
        FpsBox.SelectedIndex = Array.IndexOf(QualityPreset.FpsOptions, _service.Fps);

        BitrateBox.Value = _service.BitrateKbps;
        CursorSwitch.IsOn = _service.ShowCursor;
        SessionNameBox.Text = _service.Options?.SessionName ?? settings.SessionName;
        PortBox.Value = _service.Options?.Port ?? settings.Port;
        PasswordBox.Password = _service.Options?.Password ?? settings.Password;

        FillAddresses(settings);
        RefreshSources();
        SyncSelectionFromService();

        _service.StateChanged += OnStateChanged;
        _service.PreviewStarted += OnPreviewStarted;
        _service.StatsChanged += OnStats;
        _service.ViewersChanged += OnViewersChanged;
        _service.Error += OnError;

        Preview.Bind(_service.Preview);

        _loading = false;
        ApplyState(_service.State);
        OnStats(_service.LastStats);
        OnViewersChanged();
        UpdateInviteCode();
    }

    private static string EncoderLabel(VideoCodec codec, string name) =>
        MfCodecs.HasHardwareEncoder(codec)
            ? Loc.Format("Encoder_Gpu", name)
            : Loc.Format("Encoder_Unavailable", name);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _service.StateChanged -= OnStateChanged;
        _service.PreviewStarted -= OnPreviewStarted;
        _service.StatsChanged -= OnStats;
        _service.ViewersChanged -= OnViewersChanged;
        _service.Error -= OnError;
        Preview.Unbind();
        PersistInputs();
    }

    private void PersistInputs()
    {
        SettingsStore.Update(s =>
        {
            s.SessionName = SessionNameBox.Text.Trim();
            s.Port = PortValue();
            s.Password = PasswordBox.Password;
            s.QualityPreset = _service.Preset;
            s.Fps = _service.Fps;
            s.BitrateKbps = _service.BitrateKbps;
            s.ShowCursor = _service.ShowCursor;
            s.Encoder = _service.EncoderPreferenceValue;
        });
    }

    private void FillAddresses(AppSettings settings)
    {
        AddressBox.Items.Clear();
        foreach (var address in NetworkInfo.LocalAddresses())
            AddressBox.Items.Add(address);
        if (_publicIp is not null)
            AddressBox.Items.Add(_publicIp);
        if (AddressBox.Items.Count == 0)
            AddressBox.Items.Add("127.0.0.1");
        AddressBox.SelectedIndex = 0;
    }

    private void RefreshSources()
    {
        var selectedKey = _service.Source?.Key;
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
        var key = _service.Source?.Key;
        _syncingSelection = true;
        MonitorsList.SelectedItem = key is null ? null : _monitors.FirstOrDefault(m => m.Key == key);
        WindowsList.SelectedItem = key is null ? null : _windows.FirstOrDefault(w => w.Key == key);
        _syncingSelection = false;
    }

    private void OnRefreshSources(object sender, RoutedEventArgs e) => RefreshSources();

    private void OnMonitorSelected(object sender, SelectionChangedEventArgs e) =>
        SelectFromList(MonitorsList, WindowsList);

    private void OnWindowSelected(object sender, SelectionChangedEventArgs e) =>
        SelectFromList(WindowsList, MonitorsList);

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
            _service.SelectSource(source);
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
        _service.EncoderPreferenceValue = EncoderPreference.All[EncoderBox.SelectedIndex];
        BitrateBox.Value = QualityPreset.SuggestedBitrate(_service.Preset, _service.Fps, ResolvedCodecName());
    }

    private string ResolvedCodecName() => MfCodecs.Resolve(_service.EncoderPreferenceValue).ToWireName();

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        if (PresetBox.SelectedIndex >= 0)
            _service.Preset = QualityPreset.All[PresetBox.SelectedIndex];
        if (FpsBox.SelectedIndex >= 0)
            _service.Fps = QualityPreset.FpsOptions[FpsBox.SelectedIndex];
        if (ReferenceEquals(sender, PresetBox) || ReferenceEquals(sender, FpsBox))
            BitrateBox.Value = QualityPreset.SuggestedBitrate(_service.Preset, _service.Fps, ResolvedCodecName());
    }

    private void OnBitrateChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value))
            return;
        _service.BitrateKbps = (int)sender.Value;
    }

    private void OnCursorToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _service.ShowCursor = CursorSwitch.IsOn;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => UpdateInviteCode();

    private void OnInviteInputChanged(object sender, object e) => UpdateInviteCode();

    private int PortValue()
    {
        var value = double.IsNaN(PortBox.Value) ? AppInfo.DefaultPort : (int)PortBox.Value;
        return InviteCode.IsValidPort(value) ? value : AppInfo.DefaultPort;
    }

    private void UpdateInviteCode()
    {
        if (_loading)
            return;
        var address = AddressBox.SelectedItem as string;
        if (string.IsNullOrEmpty(address) || address == PublicIpPending)
        {
            InviteCodeBox.Text = string.Empty;
            return;
        }

        var password = PasswordBox.Password;
        InviteCodeBox.Text = InviteCode.Encode(
            new InviteTarget(address, PortValue(), password.Length == 0 ? null : password)
        );
    }

    private async void OnFindPublicIp(object sender, RoutedEventArgs e)
    {
        PublicIpButton.IsEnabled = false;
        try
        {
            var ip = await NetworkInfo.PublicAddressAsync(CancellationToken.None);
            if (ip is null)
            {
                ErrorText.Text = Loc.Get("Error_PublicIp");
                return;
            }

            _publicIp = ip;
            if (!AddressBox.Items.Contains(ip))
                AddressBox.Items.Add(ip);
            AddressBox.SelectedItem = ip;
        }
        finally
        {
            PublicIpButton.IsEnabled = true;
        }
    }

    private void OnCopyInvite(object sender, RoutedEventArgs e)
    {
        if (InviteCodeBox.Text.Length == 0)
            return;
        var package = new DataPackage();
        package.SetText(InviteCodeBox.Text);
        Clipboard.SetContent(package);
        CopyButton.Content = Loc.Get("Invite_Copied");
        _ = ResetCopyLabelAsync();
    }

    private async Task ResetCopyLabelAsync()
    {
        await Task.Delay(1500);
        CopyButton.Content = Loc.Get("Invite_Copy/Content");
    }

    private void OnGoLive(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (_service.Source is null)
        {
            ErrorText.Text = Loc.Get("Error_NoSource");
            return;
        }

        var settings = SettingsStore.Load();
        var name = SessionNameBox.Text.Trim();
        if (name.Length == 0)
            name = Loc.Format("Session_DefaultName", settings.DisplayName);

        var password = PasswordBox.Password;
        var options = new HostOptions(
            PortValue(),
            password.Length == 0 ? null : password,
            name,
            settings.DisplayName,
            settings.MaxViewers
        );

        try
        {
            _service.GoLive(options);
            PersistInputs();
        }
        catch (Exception ex)
        {
            ErrorText.Text = Loc.Format("Error_GoLive", ex.Message);
        }
    }

    private void OnPause(object sender, RoutedEventArgs e) => _service.SetPaused(!_service.IsPaused);

    private void OnStop(object sender, RoutedEventArgs e) => _service.StopLive();

    private void OnStateChanged(BroadcastState state) => ApplyState(state);

    private void ApplyState(BroadcastState state)
    {
        var live = state == BroadcastState.Live;
        LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        PausedBadge.Visibility = live && _service.IsPaused ? Visibility.Visible : Visibility.Collapsed;
        GoLiveButton.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
        GoLiveButton.IsEnabled = state == BroadcastState.Preview;
        StopButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = Loc.Get(_service.IsPaused ? "Action_Resume" : "Action_Pause/Content");
        PortBox.IsEnabled = !live;
        PasswordBox.IsEnabled = !live;
        SessionNameBox.IsEnabled = !live;
        EncoderBox.IsEnabled = !live;
        PreviewHint.Visibility = state == BroadcastState.Idle ? Visibility.Visible : Visibility.Collapsed;
        if (state == BroadcastState.Idle)
        {
            Preview.Clear();
            SyncSelectionFromService();
        }
        OnStats(_service.LastStats);
    }

    private void OnPreviewStarted()
    {
        Preview.MarkFrame();
        PreviewHint.Visibility = Visibility.Collapsed;
    }

    private void OnStats(HostStats stats)
    {
        StatsText.Text = _service.State == BroadcastState.Live
            ? $"{stats.Codec}  {stats.Width}×{stats.Height}  {stats.Fps:F0} fps  {stats.Kbps / 1000:F1} Mbps  {stats.EncodeMs:F1} ms"
            : _service.Source is null
                ? string.Empty
                : _service.Source.SizeLabel;
        StatsBadge.Visibility = StatsText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnViewersChanged()
    {
        _viewers.Clear();
        foreach (var viewer in _service.Viewers)
            _viewers.Add(viewer);
        ViewersCountText.Text = _viewers.Count.ToString();
        ViewersEmptyText.Visibility = _viewers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnError(string message) => ErrorText.Text = message;
}

/// <summary>Maps a capture source kind to a Segoe Fluent glyph.</summary>
public sealed class KindGlyphConverter : Microsoft.UI.Xaml.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is CaptureSourceKind.Monitor ? "" : "";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
