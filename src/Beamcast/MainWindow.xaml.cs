using Beamcast.Pages;
using Beamcast.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace Beamcast;

public sealed partial class MainWindow : Window
{
    private static readonly string IconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Beamcast.ico");
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private UIElement? _fullscreenContent;
    private DispatcherQueueTimer? _updateTimer;
    private UpdateOffer? _pendingOffer;
    private string? _notifiedVersion;
    private bool _barRestarts;
    private bool _disclaimerShown;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (File.Exists(IconPath))
            AppWindow.SetIcon(IconPath);

        AppWindow.Resize(new SizeInt32(1180, 820));
        SystemBackdrop = MicaController.IsSupported() ? new MicaBackdrop() : new DesktopAcrylicBackdrop();

        LoungeService.Instance.Initialize(DispatcherQueue);
        BroadcastService.Instance.Initialize(DispatcherQueue);
        WatchService.Instance.Initialize(DispatcherQueue);
        LoungeService.Instance.StateChanged += OnLoungeStateChanged;
        BroadcastService.Instance.ApplySettings(SettingsStore.Load());
        BroadcastService.Instance.StateChanged += _ => UpdateLiveBadge();

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            if (_fullscreenContent is null)
                return;
            ExitFullscreen();
            e.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(escape);
        // WinUI shows an automatic "Esc" tooltip for accelerators on hover; not wanted for the whole window.
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

        VisibilityChanged += (_, e) => Diag.Log($"ui: window {(e.Visible ? "visible" : "hidden/minimized")}");
        AppWindow.Closing += OnClosing;
        NavView.Loaded += (_, _) =>
        {
            NavView.SelectedItem = LoungeNav;
            _ = ShowDisclaimerIfNeededAsync();
        };
        StartUpdateTimer();
    }

    public bool IsFullscreen => _fullscreenContent is not null;

    /// <summary>The element currently shown in the full-window layer, or null.</summary>
    public UIElement? FullscreenContent => _fullscreenContent;

    public ElementTheme RootTheme => RootGrid.ActualTheme;

    public void ApplyTheme(string theme)
    {
        Diag.Log($"ui: theme {theme}");
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    public void ReloadForLanguage(string language)
    {
        Diag.Log($"ui: language {language}");
        App.ApplyCulture(language);
        Loc.Reset();
        LoungeNav.Content = Loc.Get("Nav_Lounge/Content");
        AboutNav.Content = Loc.Get("Nav_About/Content");
        var current = ContentFrame.CurrentSourcePageType;
        if (current is not null)
        {
            ContentFrame.Navigate(current);
            ContentFrame.BackStack.Clear();
        }
    }

    public void NavigateTo(string tag)
    {
        NavView.SelectedItem = tag switch
        {
            "about" => AboutNav,
            _ => LoungeNav,
        };
    }

    /// <summary>
    /// The study-only notice. Shown until accepted; declining closes the app, because using it
    /// without reading the notice is exactly what the notice is there to prevent.
    /// </summary>
    public async Task ShowDisclaimerIfNeededAsync(bool force = false)
    {
        if (_disclaimerShown)
            return;
        var settings = SettingsStore.Load();
        if (settings.DisclaimerAccepted && !force)
            return;

        _disclaimerShown = true;
        try
        {
            var body = new StackPanel { Spacing = 10 };
            foreach (var key in new[] { "Disclaimer_P1", "Disclaimer_P2", "Disclaimer_P3", "Disclaimer_P4" })
                body.Children.Add(new TextBlock { Text = Loc.Get(key), TextWrapping = TextWrapping.Wrap });

            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = Loc.Get("Disclaimer_Title"),
                Content = new ScrollViewer { Content = body, MaxHeight = 420 },
                PrimaryButtonText = Loc.Get("Disclaimer_Accept"),
                CloseButtonText = force ? Loc.Get("Disclaimer_Close") : Loc.Get("Disclaimer_Quit"),
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                SettingsStore.Update(s => s.DisclaimerAccepted = true);
            }
            else if (!force)
            {
                ExitApplication();
            }
        }
        finally
        {
            _disclaimerShown = false;
        }
    }

    public void ExitApplication()
    {
        _updateTimer?.Stop();
        BroadcastService.Instance.Shutdown();
        _ = LoungeService.Instance.LeaveAsync();
        Close();
    }

    /// <summary>Announces a newer build with a discreet bar under the title, never a pop-up.</summary>
    public void NotifyUpdate(UpdateOffer offer, bool staged = false, bool show = true)
    {
        _pendingOffer = offer;
        var key = offer.Version + (staged ? " ready" : string.Empty);
        if (!show || _notifiedVersion == key)
            return;
        _notifiedVersion = key;
        _barRestarts = staged;
        UpdateBar.Title = Loc.Get(staged ? "Update_ReadyTitle" : "Update_BarTitle");
        UpdateBar.Message = Loc.Format(staged ? "Update_ReadyBody" : "Update_BarBody", offer.Version);
        UpdateBarButton.Content = Loc.Get(staged ? "Update_Restart" : "Update_BarAction");
        UpdateBar.IsOpen = true;
    }

    /// <summary>
    /// What to do with a finished check. With automatic updates on (and the connection not metered)
    /// the new version is fetched in the background and kept for the moment the app closes, so a
    /// broadcast or a room is never cut short. Nothing here ever restarts the app by itself.
    /// </summary>
    public async Task HandleCheckAsync(UpdateCheck check)
    {
        if (check.Offer is null)
            return;
        var settings = SettingsStore.Load();
        if (check.Kind == UpdateCheckKind.ReadyToRestart)
        {
            NotifyUpdate(check.Offer, staged: true, settings.UpdateNotifications);
            return;
        }
        if (check.Kind != UpdateCheckKind.Available)
            return;
        if (settings.AutoUpdate)
        {
            if (NetworkCost.IsMetered())
                Diag.Log("update: metered connection, leaving the download for later");
            else if (await UpdateService.DownloadAsync())
            {
                NotifyUpdate(check.Offer with { Downloaded = true }, staged: true, settings.UpdateNotifications);
                return;
            }
        }
        NotifyUpdate(check.Offer, staged: false, settings.UpdateNotifications);
    }

    public void ShowUpdate(UpdateOffer offer)
    {
        _pendingOffer = offer;
        var window = new UpdateWindow(offer);
        window.Activate();
    }

    private async void OnUpdateBarAction(object sender, RoutedEventArgs e)
    {
        UpdateBar.IsOpen = false;
        if (_pendingOffer is null)
            return;
        if (!_barRestarts)
        {
            ShowUpdate(_pendingOffer);
            return;
        }
        // The version is already on disk: the only thing left is to restart into it. Ask first when
        // that would cut a broadcast or something being watched.
        if (BroadcastService.Instance.State == BroadcastState.Live || WatchService.Instance.IsWatching)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = Loc.Get("Update_RestartBusyTitle"),
                Content = Loc.Get("Update_RestartBusyBody"),
                PrimaryButtonText = Loc.Get("Update_Restart"),
                CloseButtonText = Loc.Get("Dialog_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                UpdateBar.IsOpen = true;
                return;
            }
        }
        Diag.Log("ui: restart into the staged update");
        await UpdateService.DownloadAndApplyAsync();
    }

    private void StartUpdateTimer()
    {
        _updateTimer = DispatcherQueue.CreateTimer();
        _updateTimer.Interval = UpdateCheckInterval;
        _updateTimer.IsRepeating = true;
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync();
        _updateTimer.Start();
    }

    private async Task CheckForUpdatesAsync()
    {
        if (!SettingsStore.Load().CheckUpdatesOnLaunch)
            return;
        await HandleCheckAsync(await UpdateService.CheckAsync());
    }

    /// <summary>Moves <paramref name="content"/> into a black full-window layer and goes borderless.</summary>
    public void EnterFullscreen(UIElement content)
    {
        if (_fullscreenContent is not null)
            return;
        _fullscreenContent = content;
        FullscreenHost.Children.Add(content);
        FullscreenHost.Visibility = Visibility.Visible;
        NavView.Visibility = Visibility.Collapsed;
        AppTitleBar.Visibility = Visibility.Collapsed;
        UpdateBar.Visibility = Visibility.Collapsed;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    /// <summary>Returns the element that was in the fullscreen layer, or null if not fullscreen.</summary>
    public UIElement? ExitFullscreen()
    {
        var content = _fullscreenContent;
        if (content is null)
            return null;
        _fullscreenContent = null;
        FullscreenHost.Children.Remove(content);
        FullscreenHost.Visibility = Visibility.Collapsed;
        NavView.Visibility = Visibility.Visible;
        AppTitleBar.Visibility = Visibility.Visible;
        UpdateBar.Visibility = Visibility.Visible;
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        FullscreenExited?.Invoke(content);
        return content;
    }

    public event Action<UIElement>? FullscreenExited;

    private void UpdateLiveBadge()
    {
        TitleLiveBadge.Visibility = BroadcastService.Instance.State == BroadcastState.Live
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Diag.Log($"ui: nav -> {(args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString() ?? "?")}");
        Type page = LoungePageType;
        if (args.IsSettingsSelected)
            page = typeof(SettingsPage);
        else if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            page = tag switch
            {
                "about" => typeof(AboutPage),
                _ => LoungePageType,
            };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
    }

    /// <summary>Outside a lounge the tab shows the entry screen; inside, the room.</summary>
    private static Type LoungePageType =>
        LoungeService.Instance.InRoom ? typeof(RoomPage) : typeof(LoungePage);

    private void OnLoungeStateChanged(LoungeState state)
    {
        if (state is LoungeState.Connecting or LoungeState.Reconnecting)
            return;
        if (ContentFrame.CurrentSourcePageType == typeof(LoungePage) || ContentFrame.CurrentSourcePageType == typeof(RoomPage))
        {
            var page = LoungePageType;
            if (ContentFrame.CurrentSourcePageType != page)
                ContentFrame.Navigate(page);
        }
        if (state == LoungeState.Disconnected && _fullscreenContent is not null)
            ExitFullscreen();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        _updateTimer?.Stop();
        // A version downloaded in the background is installed now, with the app on its way out.
        if (SettingsStore.Load().AutoUpdate && UpdateService.HasStagedUpdate)
            UpdateService.ApplyWhenClosed();
        BroadcastService.Instance.Shutdown();
        _ = LoungeService.Instance.LeaveAsync();
    }
}
