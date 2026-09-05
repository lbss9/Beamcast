using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Beamcast;

public sealed partial class UpdateWindow : Window
{
    private const int Width = 460;
    private const int Height = 620;

    public UpdateWindow(UpdateOffer offer)
    {
        InitializeComponent();
        SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.Resize(new SizeInt32(Width, Height));
        CenterOnWorkArea();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsMaximizable = false;

        VersionText.Text = Loc.Format("Update_VersionLine", offer.Version);
        MarkdownLite.Render(ChangelogView, offer.Notes);
        RootGrid.RequestedTheme = App.Main?.RootTheme ?? ElementTheme.Default;
        if (offer.Downloaded)
        {
            InstallButton.Content = Loc.Get("Update_Restart");
            StatusText.Text = Loc.Get("Update_Ready");
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Spinner.IsActive = true;
        StatusText.Text = Loc.Get("Update_Downloading");
        var result = await UpdateService.DownloadAndApplyAsync(percent =>
            DispatcherQueue.TryEnqueue(() => StatusText.Text = Loc.Format("Update_DownloadingPercent", percent)));
        if (result == UpdateCheckKind.Failed)
        {
            Spinner.IsActive = false;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            StatusText.Text = Loc.Get("Update_Failed");
        }
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private void CenterOnWorkArea()
    {
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var x = work.X + (work.Width - Width) / 2;
        var y = work.Y + (work.Height - Height) / 2;
        AppWindow.Move(new PointInt32(x, y));
    }
}
