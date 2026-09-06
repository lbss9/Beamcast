using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Beamcast;

/// <summary>
/// Offers a newer build: what it fixes and adds, how big the download is, and one button that
/// downloads, installs and restarts. Everything the person owns (rooms, favorites, settings) lives
/// outside the app folder and survives the update.
/// </summary>
public sealed partial class UpdateWindow : Window
{
    private const int Width = 520;
    private const int Height = 680;

    private readonly UpdateOffer _offer;

    public UpdateWindow(UpdateOffer offer)
    {
        _offer = offer;
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
        CurrentText.Text = string.IsNullOrEmpty(offer.CurrentVersion)
            ? string.Empty
            : Loc.Format("Update_Current", offer.CurrentVersion);
        MarkdownLite.Render(ChangelogView, offer.Notes);
        RootGrid.RequestedTheme = App.Main?.RootTheme ?? ElementTheme.Default;

        if (offer.Downloaded)
        {
            InstallButton.Content = Loc.Get("Update_Restart");
            StatusText.Text = Loc.Get("Update_Ready");
        }
        else
        {
            StatusText.Text = DescribeSize(offer);
        }
    }

    private static string DescribeSize(UpdateOffer offer)
    {
        if (offer.SizeBytes <= 0)
            return string.Empty;
        var size = offer.SizeBytes >= 10 * 1024 * 1024
            ? $"{offer.SizeBytes / 1024.0 / 1024.0:F0} MB"
            : $"{offer.SizeBytes / 1024.0 / 1024.0:F1} MB";
        return Loc.Format(offer.IsDelta ? "Update_SizeDelta" : "Update_SizeFull", size);
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Spinner.IsActive = true;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = _offer.Downloaded;
        Progress.Value = 0;
        StatusText.Text = _offer.Downloaded ? Loc.Get("Update_Applying") : Loc.Get("Update_Downloading");

        var result = await UpdateService.DownloadAndApplyAsync(percent =>
            DispatcherQueue.TryEnqueue(() =>
            {
                Progress.IsIndeterminate = false;
                Progress.Value = percent;
                StatusText.Text = percent >= 100 ? Loc.Get("Update_Applying") : Loc.Format("Update_DownloadingPercent", percent);
                if (percent >= 100)
                    Progress.IsIndeterminate = true;
            }));

        if (result == UpdateCheckKind.Failed)
        {
            Spinner.IsActive = false;
            Progress.Visibility = Visibility.Collapsed;
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
