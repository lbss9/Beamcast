using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beamcast.Pages;

/// <summary>"What's new": every version's notes, newest first. Updates live in Settings.</summary>
public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        VersionText.Text = Loc.Format("News_Subtitle", AppInfo.Version);
        MarkdownLite.Render(ChangelogView, ChangelogStore.Read());
    }

    private async void OnReadDisclaimer(object sender, RoutedEventArgs e)
    {
        if (App.Main is not null)
            await App.Main.ShowDisclaimerIfNeededAsync(force: true);
    }
}
