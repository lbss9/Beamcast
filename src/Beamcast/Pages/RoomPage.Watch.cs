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

/// <summary>The Watch tab: the tiles of the streams being watched, their overlays, stats and fullscreen.</summary>
public sealed partial class RoomPage
{
    // ----- watching -----

    /// <summary>Creates tiles for new streams, drops tiles for stopped ones, lays them out and binds them.</summary>
    private void SyncTiles()
    {
        var wanted = _watch.Watching;
        foreach (var id in Tiles.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            var gone = Tiles[id];
            Tiles.Remove(id);
            // A tile that vanishes while it fills the fullscreen layer (stopped, ended, or "stop
            // all") must leave fullscreen with it; otherwise the layer keeps the dead tile and only
            // Escape gets the user out. OnWatchStopped can't do this: it runs after this removal, so
            // its Tiles lookup already fails.
            if (App.Main?.IsFullscreen == true && ReferenceEquals(App.Main.FullscreenContent, gone.Root))
                App.Main.ExitFullscreen();
            gone.Detach();
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
            try
            {
                tile.AttachTo(VideoHost);
            }
            catch (Exception ex)
            {
                // Never let a stray parent take the page down; the tile is skipped this pass.
                Diag.Log($"room: could not attach tile {tile.StreamId}: {ex.Message}");
                continue;
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
            if (ReferenceEquals(tile.Host, VideoHost))
                tile.Detach();
        }
        Diag.Log($"room: page unloaded, {Tiles.Count} tile(s) kept");
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
            // The host ended this stream and the tile is holding the last frame while its publisher
            // reconnects: say so instead of leaving a frozen picture with no explanation.
            if (_watch.WaitingOwner(id) is { } absent)
            {
                tile.ShowOverlay(Loc.Format("Watch_PublisherBack", absent));
                continue;
            }
            var stream = _lounge.FindStream(id);
            if (stream is null)
                continue;
            tile.TitleText.Text = Loc.Format("Room_WatchingTitle", stream.Meta.Title, stream.OwnerName);
            if (stream.Meta.State == StreamStates.Paused)
                tile.ShowOverlay(Loc.Get("Watch_PausedByHost"));
            else if (_watch.UndecodableCodec(id) is { } codec)
                tile.ShowOverlay(Loc.Format(codec == "AV1" ? "Watch_NoDecoderAv1" : codec == "HEVC" ? "Watch_NoDecoderHevc" : "Watch_NoDecoder", codec));
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
        var path = Loc.Get(_lounge.IsDirect(streamId) ? "Watch_PathDirect" : "Watch_PathRelay");
        tile.StatsText.Text = $"{path}  {stats.Width}×{stats.Height}  {stats.Fps:F0} fps  {stats.Kbps / 1000:F1} Mbps  dec {stats.DecodeMs:F1} ms{audio}{latency}";
        UpdatePingText();
    }

    /// <summary>A video path switched between direct and the server: tell the person, briefly.</summary>
    private void OnPathChanged(DirectPathChange change)
    {
        if (change.AsPublisher)
        {
            ViewerNoteText.Text = Loc.Format(change.Direct ? "Path_PublisherDirect" : "Path_PublisherRelay", change.PeerName);
            OnBroadcastStats(_broadcast.LastStats);
            return;
        }
        if (!_watch.IsWatchingStream(change.StreamId))
            return;
        var message = Loc.Format(change.Direct ? "Path_ViewerDirect" : "Path_ViewerRelay", change.PeerName);
        PathBar.Severity = change.Direct ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        PathBar.Message = message;
        PathBar.IsOpen = true;
        _ = ClosePathBarLaterAsync(message);
    }

    private async Task ClosePathBarLaterAsync(string message)
    {
        await Task.Delay(TimeSpan.FromSeconds(6));
        if (PathBar.Message == message)
            PathBar.IsOpen = false;
    }

    private void OnWatchStopped(uint streamId, string reason)
    {
        if (Tiles.TryGetValue(streamId, out var tile) && App.Main?.IsFullscreen == true && ReferenceEquals(App.Main.FullscreenContent, tile.Root))
            App.Main.ExitFullscreen();
        SyncTiles();
    }

    private void OnStopWatching(object sender, RoutedEventArgs e)
    {
        Diag.Log("ui: stop all watching clicked");
        _watch.StopAll();
    }

    private void OnMuteToggled(object sender, RoutedEventArgs e) => _watch.IsMuted = MuteButton.IsChecked == true;

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading)
            return;
        _watch.Volume = (float)(e.NewValue / 100.0);
    }

    private static void ToggleFullscreen(uint streamId)
    {
        Diag.Log($"ui: fullscreen toggle for #{streamId}");
        var main = App.Main;
        if (main is null || !Tiles.TryGetValue(streamId, out var tile))
            return;
        if (main.IsFullscreen)
        {
            main.ExitFullscreen();
            return;
        }
        tile.Detach();
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

        /// <summary>
        /// The panel <see cref="Root"/> currently sits in. Tracked here rather than read from
        /// <c>Root.Parent</c>: while a page is being torn down Parent already reads null even though
        /// the element is still in the old panel's Children, and adding it to the next page then
        /// fails with WinUI's "No installed components were detected" (crash of 2.4.0).
        /// </summary>
        public Panel? Host { get; private set; }

        public void AttachTo(Panel host)
        {
            if (ReferenceEquals(Host, host))
                return;
            Detach();
            host.Children.Add(Root);
            Host = host;
        }

        public void Detach()
        {
            var host = Host;
            Host = null;
            if (host is not null)
                SafeTry.Run(() => host.Children.Remove(Root));
        }
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
}
