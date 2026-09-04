using Beamcast.Controls;
using Beamcast.Net;
using Beamcast.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beamcast.Pages;

public sealed partial class WatchPage : Page
{
    // One video surface for the whole process, so fullscreen and page re-creation keep the
    // picture. Created lazily on the UI thread the first time the page loads.
    private static GpuVideoView? _sharedVideo;
    private static Grid? _overlay;
    private static TextBlock? _overlayText;

    private static GpuVideoView SharedVideo => _sharedVideo ??= CreateVideo();
    private static Grid Overlay => _overlay ??= CreateOverlay();

    private readonly WatchService _service = WatchService.Instance;
    private CancellationTokenSource? _connectCts;

    public WatchPage()
    {
        InitializeComponent();
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
        };
        var overlay = new Grid { Visibility = Visibility.Collapsed };
        overlay.Children.Add(_overlayText);
        return overlay;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = SettingsStore.Load();
        CodeBox.Text = settings.LastInvite;
        NameBox.Text = settings.DisplayName;

        _service.StateChanged += OnStateChanged;
        _service.FirstFrame += OnFirstFrame;
        _service.ViewersChanged += OnViewersChanged;
        _service.StatsChanged += OnStats;
        _service.StreamStateChanged += OnStreamState;
        _service.Closed += OnClosed;
        if (App.Main is not null)
            App.Main.FullscreenExited += OnFullscreenExited;

        AttachVideo();
        SharedVideo.Bind(_service.Presenter);
        ApplyState(_service.State);
        OnCodeChanged(CodeBox, null!);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _service.StateChanged -= OnStateChanged;
        _service.FirstFrame -= OnFirstFrame;
        _service.ViewersChanged -= OnViewersChanged;
        _service.StatsChanged -= OnStats;
        _service.StreamStateChanged -= OnStreamState;
        _service.Closed -= OnClosed;
        if (App.Main is not null)
            App.Main.FullscreenExited -= OnFullscreenExited;
        DetachVideo();
    }

    private void AttachVideo()
    {
        if (App.Main?.IsFullscreen == true)
            return;
        DetachVideoFromParent();
        VideoHost.Children.Add(SharedVideo);
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

    private void OnCodeChanged(object sender, TextChangedEventArgs e)
    {
        var ok = InviteCode.TryDecode(CodeBox.Text, out var target);
        JoinButton.IsEnabled = ok && _service.State == WatchState.Disconnected;
        PasswordBox.Visibility = ok && target.HasPassword ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;
        if (!InviteCode.TryDecode(CodeBox.Text, out var target))
        {
            ErrorText.Text = Loc.Get("Error_BadCode");
            return;
        }

        if (!target.HasPassword && PasswordBox.Password.Length > 0)
            target = target with { Password = PasswordBox.Password };

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
            name = Environment.UserName;

        SettingsStore.Update(s =>
        {
            s.LastInvite = CodeBox.Text.Trim();
            s.DisplayName = name;
        });

        JoinButton.IsEnabled = false;
        JoinProgress.IsActive = true;
        _connectCts = new CancellationTokenSource();
        try
        {
            await _service.ConnectAsync(target, name, _connectCts.Token);
        }
        catch (ConnectException ex)
        {
            ErrorText.Text = ex.Reason switch
            {
                RejectReasons.Password => Loc.Get("Error_Password"),
                RejectReasons.Full => Loc.Get("Error_Full"),
                RejectReasons.Version => Loc.Get("Error_Version"),
                "timeout" => Loc.Get("Error_Timeout"),
                "unreachable" => Loc.Get("Error_Unreachable"),
                "codec" => Loc.Get("Error_Codec"),
                _ => Loc.Format("Error_Generic", ex.Message),
            };
        }
        catch (Exception ex)
        {
            ErrorText.Text = Loc.Format("Error_Generic", ex.Message);
        }
        finally
        {
            JoinProgress.IsActive = false;
            _connectCts?.Dispose();
            _connectCts = null;
            OnCodeChanged(CodeBox, null!);
        }
    }

    private async void OnLeave(object sender, RoutedEventArgs e)
    {
        if (App.Main?.IsFullscreen == true)
            App.Main.ExitFullscreen();
        await _service.DisconnectAsync();
    }

    private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreenStatic();

    private static void ToggleFullscreenStatic()
    {
        var main = App.Main;
        if (main is null || WatchService.Instance.State != WatchState.Watching)
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

    private void OnStateChanged(WatchState state) => ApplyState(state);

    private void ApplyState(WatchState state)
    {
        var watching = state == WatchState.Watching;
        JoinPanel.Visibility = watching ? Visibility.Collapsed : Visibility.Visible;
        PlayerPanel.Visibility = watching ? Visibility.Visible : Visibility.Collapsed;
        JoinButton.IsEnabled = state == WatchState.Disconnected && InviteCode.TryDecode(CodeBox.Text, out _);
        JoinProgress.IsActive = state == WatchState.Connecting;

        if (watching)
        {
            var welcome = _service.Welcome;
            TitleText.Text = welcome?.SessionName ?? string.Empty;
            OnViewersChanged(_service.Viewers);
            OnStreamState(_service.StreamState);
            if (_service.LastStats is { } stats)
                OnStats(stats);
            else
                StatsText.Text = string.Empty;
            if (!_service.HasFrame)
                ShowOverlay(Loc.Get("Watch_Waiting"));
        }
        else
        {
            SharedVideo.Clear();
        }
    }

    private void OnFirstFrame()
    {
        SharedVideo.MarkFrame();
        if (_service.StreamState == StreamStates.Live)
            Overlay.Visibility = Visibility.Collapsed;
    }

    private void OnViewersChanged(IReadOnlyList<string> viewers)
    {
        var host = _service.Welcome?.HostName ?? string.Empty;
        SubtitleText.Text = Loc.Format("Watch_Subtitle", host, viewers.Count);
    }

    private void OnStats(ViewerStats stats)
    {
        var codec = (_service.Welcome?.Codec ?? string.Empty).ToUpperInvariant();
        StatsText.Text = $"{codec}  {stats.Width}×{stats.Height}  {stats.Fps:F0} fps  {stats.Kbps / 1000:F1} Mbps  dec {stats.DecodeMs:F1} ms  rtt {stats.RttMs:F0} ms";
    }

    private void OnStreamState(string state)
    {
        switch (state)
        {
            case StreamStates.Paused:
                ShowOverlay(Loc.Get("Watch_PausedByHost"));
                break;
            case StreamStates.Ended:
                ShowOverlay(Loc.Get("Watch_Ended"));
                break;
            default:
                if (_service.HasFrame)
                    Overlay.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private static void ShowOverlay(string text)
    {
        var overlay = Overlay;
        if (_overlayText is not null)
            _overlayText.Text = text;
        overlay.Visibility = Visibility.Visible;
    }

    private void OnClosed(string reason)
    {
        if (App.Main?.IsFullscreen == true)
            App.Main.ExitFullscreen();
        ErrorText.Text = reason switch
        {
            "ended" or "closed" => Loc.Get("Watch_Ended"),
            "lost" => Loc.Get("Watch_Lost"),
            _ => string.Empty,
        };
    }
}
