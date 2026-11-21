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

/// <summary>The Broadcast tab: source, quality, encoder options, profiles, viewers and going live.</summary>
public sealed partial class RoomPage
{
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

    // ----- broadcast profile -----

    private void OnProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _applyingProfile || ProfileBox.SelectedIndex < 0)
            return;
        CalibrateButton.IsEnabled = ProfileBox.SelectedIndex > 0;
        if (ProfileBox.SelectedIndex == 0)
        {
            ProfileStatus.Text = string.Empty;
            return;
        }
        _ = RunCalibrationAsync();
    }

    private void OnCalibrate(object sender, RoutedEventArgs e) => _ = RunCalibrationAsync();

    /// <summary>A hand edit of any quality control means the person wants manual settings.</summary>
    private void DropToManual()
    {
        if (_loading || _applyingProfile || ProfileBox.SelectedIndex <= 0)
            return;
        _applyingProfile = true;
        ProfileBox.SelectedIndex = 0;
        CalibrateButton.IsEnabled = false;
        ProfileStatus.Text = string.Empty;
        _applyingProfile = false;
    }

    private async Task RunCalibrationAsync()
    {
        var profile = Profiles[Math.Max(0, ProfileBox.SelectedIndex)];
        if (profile == BroadcastProfile.Manual)
            return;
        if (_broadcast.Source is null)
        {
            ProfileStatus.Text = Loc.Get("Profile_NoSource");
            return;
        }
        if (_broadcast.State == BroadcastState.Live)
        {
            ProfileStatus.Text = Loc.Get("Profile_WhileLive");
            return;
        }
        _calibrateCts?.Cancel();
        var cts = _calibrateCts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        CalibrateButton.IsEnabled = false;
        ProfileBox.IsEnabled = false;
        CalibrateSpinner.IsActive = true;
        var progress = new Progress<string>(step =>
            ProfileStatus.Text = step == "upload" ? Loc.Get("Profile_MeasuringUpload") : Loc.Format("Profile_Measuring", step == QualityPreset.Source ? Loc.Get("Quality_PresetSource") : step));
        try
        {
            var choice = await _broadcast.CalibrateAsync(profile, progress, cts.Token);
            if (choice is null)
            {
                ProfileStatus.Text = Loc.Get("Profile_Failed");
                return;
            }
            _applyingProfile = true;
            PresetBox.SelectedIndex = Array.IndexOf(QualityPreset.All, _broadcast.Preset);
            FpsBox.SelectedIndex = Array.IndexOf(QualityPreset.FpsOptions, _broadcast.Fps);
            EncoderBox.SelectedIndex = Math.Max(0, Array.IndexOf(EncoderPreference.All, _broadcast.EncoderPreferenceValue));
            BitrateBox.Value = _broadcast.BitrateKbps;
            _applyingProfile = false;
            var uplink = choice.UplinkKbps > 0 ? Loc.Format("Profile_UplinkPart", (choice.UplinkKbps / 1000.0).ToString("F0")) : string.Empty;
            var encoderName = choice.Encoder == EncoderPreference.Hevc ? "HEVC" : choice.Encoder == EncoderPreference.Vp8 ? "VP8" : "H.264";
            var tierName = Loc.Get(choice.Tier switch { BroadcastProfile.Performance => "Profile_TierPerformance", BroadcastProfile.Balanced => "Profile_TierBalanced", _ => "Profile_TierQuality" });
            var label = profile == BroadcastProfile.Recommended ? Loc.Format("Profile_RecommendedTier", tierName) : tierName;
            ProfileStatus.Text = Loc.Format("Profile_Result", label, choice.Height, choice.Fps, choice.BitrateKbps, encoderName, choice.EncodeMs.ToString("F1"), uplink);
        }
        catch (OperationCanceledException)
        {
            ProfileStatus.Text = string.Empty;
        }
        catch (InvalidOperationException ex) when (ex.Message == "live")
        {
            ProfileStatus.Text = Loc.Get("Profile_WhileLive");
        }
        catch (InvalidOperationException ex) when (ex.Message == "no-source")
        {
            ProfileStatus.Text = Loc.Get("Profile_NoSource");
        }
        catch (Exception ex)
        {
            Diag.Log("profile: calibration failed: " + ex);
            ProfileStatus.Text = Loc.Format("Error_Generic", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_calibrateCts, cts))
            {
                CalibrateSpinner.IsActive = false;
                ProfileBox.IsEnabled = true;
                CalibrateButton.IsEnabled = ProfileBox.SelectedIndex > 0;
            }
            PersistInputs();
        }
    }

    private void OnEncoderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EncoderBox.SelectedIndex < 0)
            return;
        DropToManual();
        _broadcast.EncoderPreferenceValue = EncoderPreference.All[EncoderBox.SelectedIndex];
        UpdateEncoderHint();
        BitrateBox.Value = QualityPreset.SuggestedBitrate(_broadcast.Preset, _broadcast.Fps, ResolvedCodecName());
    }

    private string ResolvedCodecName() => MfCodecs.Resolve(_broadcast.EncoderPreferenceValue).ToWireName();

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        DropToManual();
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
        DropToManual();
        _broadcast.BitrateKbps = (int)sender.Value;
    }

    /// <summary>Screen-content and variable-bitrate switches: they shape the next encoder that is built.</summary>
    private void OnTuningToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var tuning = new EncoderTuning(ScreenSwitch.IsOn, VbrSwitch.IsOn);
        MfCodecs.Tuning = tuning;
        SettingsStore.Update(s =>
        {
            s.ScreenContent = tuning.ScreenContent;
            s.VariableBitrate = tuning.CappedVbr;
        });
    }

    /// <summary>One line per viewer of my stream: how it is connected and whether it is struggling.</summary>
    private void RefreshViewerList(bool live)
    {
        ViewerList.Children.Clear();
        if (!live)
            return;
        foreach (var viewer in _lounge.ViewerPaths(_broadcast.StreamId))
        {
            var text = Loc.Format(viewer.Direct ? "Viewer_RowDirect" : "Viewer_RowRelay", viewer.Name);
            if (viewer.Reduced)
                text += Loc.Get("Viewer_RowReduced");
            if (viewer.DelayMs > 0)
                text += Loc.Format("Viewer_RowDelay", viewer.DelayMs);
            if (viewer.Dropped > 0)
                text += Loc.Format("Viewer_RowDropped", viewer.Dropped);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Beam.Icon(viewer.Direct ? "\uE703" : "\uE968", 12, viewer.DelayMs > 0 ? "Warn" : viewer.Direct ? "Ok" : "Text3"));
            row.Children.Add(Beam.Text(text, 12, "Text2"));
            ViewerList.Children.Add(row);
        }
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

    private void OnStandbyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _broadcast.StandbyWithoutViewers = StandbySwitch.IsOn;
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
        Diag.Log("ui: go live clicked");
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

    private void OnPause(object sender, RoutedEventArgs e)
    {
        Diag.Log("ui: pause/resume clicked");
        _broadcast.SetPaused(!_broadcast.IsPaused);
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        Diag.Log("ui: stop broadcast clicked");
        _broadcast.StopLive();
    }

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
        ScreenSwitch.IsEnabled = !live;
        VbrSwitch.IsEnabled = !live;
        TitleBox.IsEnabled = !live;
        PreviewHint.Visibility = state == BroadcastState.Idle ? Visibility.Visible : Visibility.Collapsed;
        if (state == BroadcastState.Idle)
        {
            Preview.Clear();
            SyncSelectionFromService();
        }
        OnBroadcastStats(_broadcast.LastStats);
        UpdateAudioHint();
        UpdateWatchHint();
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
            var lag = stats.WorstViewerDelayMs > 0 ? "  " + Loc.Format("Stream_ViewerLag", stats.WorstViewerDelayMs) : string.Empty;
            StatsText.Text = stats.Standby
                ? Loc.Get("Stream_StandbyStats")
                : $"{stats.Codec}  {stats.Width}×{stats.Height}  {stats.Fps:F0} fps  {stats.Kbps / 1000:F1} Mbps  {stats.EncodeMs:F1} ms{audio}{adapted}{lag}";
            var direct = _lounge.DirectViewers(_broadcast.StreamId);
            ViewersText.Text = direct > 0
                ? Loc.Format("Stream_ViewersDirect", _broadcast.ViewerCount, direct)
                : Loc.Format("Stream_Viewers", _broadcast.ViewerCount);
            ViewersBadge.Visibility = Visibility.Visible;
            StandbyBadge.Visibility = stats.Standby ? Visibility.Visible : Visibility.Collapsed;
            RefreshViewerList(live: true);
        }
        else
        {
            RefreshViewerList(live: false);
            StatsText.Text = _broadcast.Source?.SizeLabel ?? string.Empty;
            ViewersBadge.Visibility = Visibility.Collapsed;
            StandbyBadge.Visibility = Visibility.Collapsed;
            ViewerNoteText.Text = string.Empty;
        }
        StatsBadge.Visibility = StatsText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnError(string message) => ErrorText.Text = message;
}
