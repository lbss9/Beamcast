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

/// <summary>The room's Settings tab: the owner's form, invites and the danger zone; a read-only summary for everyone else.</summary>
public sealed partial class RoomPage
{
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
            room.IsTemporary ? Loc.Get("Kind_Temporary") : Loc.Get("Kind_Permanent"),
            room.HasPassword ? Loc.Get("Room_SummaryPassword") : Loc.Get("Room_SummaryNoPassword"),
            Loc.Format("Room_SummaryBroadcast", room.Broadcast == BroadcastPolicy.Owner ? Loc.Get("Broadcast_Owner") : Loc.Get("Broadcast_Everyone")),
            room.MaxMembers > 0 ? Loc.Format("Room_SummaryMaxMembers", room.MaxMembers) : Loc.Get("Room_SummaryNoLimit"),
        ];
        foreach (var line in lines)
            SettingsSummary.Children.Add(new TextBlock { Text = "\u2022  " + line, TextWrapping = TextWrapping.Wrap, Foreground = Beam.Brush("Text") });
    }

    private async void OnSaveRoom(object sender, RoutedEventArgs e)
    {
        Diag.Log("ui: save room settings clicked");
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
        Diag.Log("ui: delete room clicked");
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
        StreamsCountText.Text = streams.Count == 0 ? string.Empty : streams.Count.ToString();
        UpdateTileTitles();
        UpdateWatchHint();
        OnMembersChanged();
    }

    private UIElement BuildStreamCard(LoungeStream stream)
    {
        var watching = _watch.IsWatchingStream(stream.Id);
        var paused = stream.Meta.State == StreamStates.Paused;
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(8, 8, 6, 8), CornerRadius = new CornerRadius(10) };
        if (watching)
            grid.Background = Beam.Brush("AccentSoft");
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Beam.Avatar(stream.OwnerName, 34, square: true));

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Beam.Text(stream.Meta.Title, 13, strong: true));
        var detail = $"{stream.OwnerName} · {stream.Meta.Width}×{stream.Meta.Height}";
        if (stream.Meta.Audio is not null)
            detail += " · ♪";
        if (paused)
            detail += " · " + Loc.Get("Room_Paused");
        var caption = Beam.Text(detail, 11.5, "Text2");
        ToolTipService.SetToolTip(caption, $"{stream.Meta.Codec.ToUpperInvariant()} {stream.Meta.Width}×{stream.Meta.Height}");
        text.Children.Add(caption);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        FrameworkElement action;
        if (stream.IsMine)
        {
            action = Beam.Chip(Loc.Get("Room_Mine"), "Accent", "AccentSoft");
        }
        else if (watching)
        {
            var stop = new Button { Content = new FontIcon { Glyph = "\uE71A", FontSize = 12 }, Style = Beam.Style("GhostButtonStyle"), Width = 30, Height = 30 };
            ToolTipService.SetToolTip(stop, Loc.Get("Room_StopWatching/Content"));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(stop, Loc.Get("Room_StopWatching/Content"));
            var stopId = stream.Id;
            stop.Click += (_, _) => { Diag.Log($"ui: stop watching #{stopId} clicked"); _watch.StopWatching(stopId); };
            action = stop;
        }
        else
        {
            var watch = new Button { Content = Loc.Get("Room_Watch"), Style = Beam.Style("BeamPrimaryButtonStyle"), Height = 30, Padding = new Thickness(12, 0, 12, 0), FontSize = 13 };
            var id = stream.Id;
            watch.Click += (_, _) =>
            {
                Diag.Log($"ui: watch #{id} clicked");
                _watch.Watch(id);
                Tabs.SelectedIndex = 0;
            };
            action = watch;
        }
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 2);
        grid.Children.Add(action);
        return grid;
    }

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_lounge.Code);
        Clipboard.SetContent(package);
        CodeIcon.Glyph = "\uE73E";
        _ = ResetCodeIconAsync();
    }

    private async Task ResetCodeIconAsync()
    {
        await Task.Delay(1500);
        CodeIcon.Glyph = "\uE8C8";
    }

    private void OnCopyInvite(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(_lounge.InviteCode);
        Clipboard.SetContent(package);
        CopyInviteText.Text = Loc.Get("Invite_Copied");
        _ = ResetCopyLabelAsync();
    }

    private async Task ResetCopyLabelAsync()
    {
        await Task.Delay(1500);
        CopyInviteText.Text = Loc.Get("Room_InviteText/Text");
    }

    private async void OnLeave(object sender, RoutedEventArgs e)
    {
        Diag.Log("ui: leave room clicked");
        if (App.Main?.IsFullscreen == true)
            App.Main.ExitFullscreen();
        _broadcast.StopLive();
        _watch.StopAll("left");
        await _lounge.LeaveAsync();
    }

    private void OnTabChanged(object? sender, EventArgs e)
    {
        if (_loading)
            return;
        Diag.Log($"ui: room tab {Tabs.SelectedIndex}");
        ApplyTab();
    }

    /// <summary>Shows the panel of the chosen tab and re-attaches its video surface.</summary>
    private void ApplyTab()
    {
        var tab = Tabs.SelectedIndex;
        WatchPanel.Visibility = tab == 0 ? Visibility.Visible : Visibility.Collapsed;
        BroadcastPanel.Visibility = tab == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tab == 2 ? Visibility.Visible : Visibility.Collapsed;
        // The SwapChainPanels live in different panels; re-attach whichever became visible.
        if (tab == 0)
            BindTiles();
        else if (tab == 1)
            Preview.Bind(_broadcast.Preview);
        // A panel that was collapsed had no visual tree yet, so its buttons are named when it shows.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Beam.LabelButtons(this));
    }
}
