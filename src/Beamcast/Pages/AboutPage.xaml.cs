using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beamcast.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = Loc.Format("About_Version", AppInfo.Version);
        MarkdownLite.Render(ChangelogView, ChangelogStore.Read());
    }

    private async void OnReadDisclaimer(object sender, RoutedEventArgs e)
    {
        if (App.Main is not null)
            await App.Main.ShowDisclaimerIfNeededAsync(force: true);
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateSpinner.IsActive = true;
        UpdateStatus.Text = Loc.Get("About_UpdateChecking");
        try
        {
            var check = await UpdateService.CheckAsync();
            UpdateStatus.Text = check.Kind switch
            {
                UpdateCheckKind.Available => Loc.Format("About_UpdateAvailable", check.Offer?.Version ?? string.Empty),
                UpdateCheckKind.ReadyToRestart => Loc.Format("About_UpdateReady", check.Offer?.Version ?? string.Empty),
                UpdateCheckKind.UpToDate => Loc.Get("About_UpdateUpToDate"),
                UpdateCheckKind.NotInstalled => Loc.Get("About_UpdateNotInstalled"),
                _ => Loc.Get("About_UpdateFailed"),
            };
            if (check.Kind is UpdateCheckKind.Available or UpdateCheckKind.ReadyToRestart && check.Offer is not null)
                App.Main?.ShowUpdate(check.Offer);
        }
        finally
        {
            UpdateSpinner.IsActive = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }
}
