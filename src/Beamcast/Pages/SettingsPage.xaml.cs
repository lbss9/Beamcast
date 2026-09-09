using Beamcast.Audio;
using Beamcast.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Beamcast.Pages;

public sealed partial class SettingsPage : Page
{
    private static readonly string[] Languages = [AppLanguage.System, AppLanguage.Portuguese, AppLanguage.English];
    private static readonly string[] Themes = ["System", "Light", "Dark"];

    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        var settings = SettingsStore.Load();

        var repo = AppInfo.GitHubRepoUrl;
        DocsLink.NavigateUri = repo.Length > 0 ? new Uri(repo + "#readme") : null;
        RepoLink.NavigateUri = repo.Length > 0 ? new Uri(repo) : null;
        IssueLink.NavigateUri = repo.Length > 0 ? new Uri(repo + "/issues/new") : null;

        VersionText.Text = "v" + AppInfo.Version;
        RefreshLastCheck();
        AutoCheckRow.Header = Loc.Get("Settings_UpdatesLabel");
        AutoCheckRow.Description = Loc.Get("Settings_UpdatesDesc");
        AutoCheckRow.IsChecked = settings.CheckUpdatesOnLaunch;
        AutoUpdateRow.Header = Loc.Get("Settings_AutoUpdate");
        AutoUpdateRow.Description = Loc.Get("Settings_AutoUpdateDesc");
        AutoUpdateRow.IsChecked = settings.AutoUpdate;
        UpdateNotifyRow.Header = Loc.Get("Settings_UpdateNotify");
        UpdateNotifyRow.Description = Loc.Get("Settings_UpdateNotifyDesc");
        UpdateNotifyRow.IsChecked = settings.UpdateNotifications;
        UpdateNotesRow.Header = Loc.Get("Settings_UpdateNotes");
        UpdateNotesRow.Description = Loc.Get("Settings_UpdateNotesDesc");
        UpdateNotesRow.IsChecked = settings.ShowNotesAfterUpdate;

        NameRow.Header = Loc.Get("Settings_DisplayName");
        NameRow.Description = Loc.Get("Settings_DisplayNameDesc");
        NameBox.Text = settings.DisplayName;

        LanguageRow.Header = Loc.Get("Settings_Language");
        LanguageRow.Description = Loc.Get("Settings_LanguageDesc");
        LanguageBox.Items.Clear();
        LanguageBox.Items.Add(Loc.Get("Settings_LanguageSystem"));
        LanguageBox.Items.Add("Português (Brasil)");
        LanguageBox.Items.Add("English");
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, settings.Language));

        ThemeRow.Header = Loc.Get("Settings_Theme");
        ThemeRow.Description = Loc.Get("Settings_ThemeDesc");
        ThemeBox.Items.Clear();
        ThemeBox.Items.Add(Loc.Get("Settings_ThemeSystem"));
        ThemeBox.Items.Add(Loc.Get("Settings_ThemeLight"));
        ThemeBox.Items.Add(Loc.Get("Settings_ThemeDark"));
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, settings.Theme));

        StreamSoundRow.Header = Loc.Get("Settings_StreamSound");
        StreamSoundRow.Description = Loc.Get("Settings_StreamSoundDesc");
        StreamSoundPlay.Content = Loc.Get("Settings_StreamSoundTry");
        StreamSoundSwitch.IsOn = settings.StreamSounds;

        DiagRow.Header = Loc.Get("Settings_Diag");
        DiagRow.Description = Loc.Get("Settings_DiagDesc");
        DiagSwitch.IsOn = Diag.IsEnabled;
        DiagFolderRow.Header = Loc.Get("Settings_DiagFolder");
        DiagFolderRow.Description = Loc.Format("Settings_DiagFolderDesc", SettingsStore.DirectoryPath);
        ReportRow.Header = Loc.Get("Settings_BugReportTitle");
        ReportRow.Description = Loc.Get("Settings_BugReportDesc");
        _loading = false;
    }

    private void RefreshLastCheck()
    {
        var at = UpdateService.LastCheckedAt;
        LastCheckText.Text = at is null
            ? Loc.Get("Settings_NeverChecked")
            : Loc.Format("Settings_LastChecked", at.Value.ToString("g"));
    }

    // ----- updates -----

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
            RefreshLastCheck();
            if (check.Kind is UpdateCheckKind.Available or UpdateCheckKind.ReadyToRestart && check.Offer is not null)
                App.Main?.ShowUpdate(check.Offer);
        }
        finally
        {
            UpdateSpinner.IsActive = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>The notes have their own page now ("What's new"); this just goes there.</summary>
    private void OnReleaseNotes(object sender, RoutedEventArgs e) => App.Main?.NavigateTo("about");

    private void OnUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var on = AutoCheckRow.IsChecked;
        SettingsStore.Update(s => s.CheckUpdatesOnLaunch = on);
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var on = AutoUpdateRow.IsChecked;
        Diag.Log($"ui: automatic updates {(on ? "on" : "off")}");
        SettingsStore.Update(s => s.AutoUpdate = on);
    }

    private void OnUpdateNotifyToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var on = UpdateNotifyRow.IsChecked;
        SettingsStore.Update(s => s.UpdateNotifications = on);
    }

    private void OnUpdateNotesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var on = UpdateNotesRow.IsChecked;
        SettingsStore.Update(s => s.ShowNotesAfterUpdate = on);
    }

    // ----- identity and appearance -----

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
            return;
        SettingsStore.Update(s => s.DisplayName = name);
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0)
            return;
        var language = Languages[LanguageBox.SelectedIndex];
        SettingsStore.Update(s => s.Language = language);
        App.Main?.ReloadForLanguage(language);
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeBox.SelectedIndex < 0)
            return;
        var theme = Themes[ThemeBox.SelectedIndex];
        SettingsStore.Update(s => s.Theme = theme);
        App.Main?.ApplyTheme(theme);
    }

    // ----- diagnostics -----

    private void OnStreamSoundToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var on = StreamSoundSwitch.IsOn;
        LoungeService.Instance.StreamSounds = on;
        SettingsStore.Update(s => s.StreamSounds = on);
        if (on)
            SoundEffects.Play(SoundEffects.StreamStart);
    }

    private void OnPlayStreamSound(object sender, RoutedEventArgs e) => SoundEffects.Play(SoundEffects.StreamStart);

    private void OnDiagToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        Diag.SetEnabled(DiagSwitch.IsOn);
    }

    private async void OnOpenDiagFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        await Launcher.LaunchFolderPathAsync(SettingsStore.DirectoryPath);
    }

    private async void OnBugReport(object sender, RoutedEventArgs e)
    {
        ReportButton.IsEnabled = false;
        ReportStatus.Text = Loc.Get("Settings_BugReportBuilding");
        try
        {
            var path = await Task.Run(BugReport.Create);
            ReportStatus.Text = Loc.Format("Settings_BugReportDone", Path.GetFileName(path));
            await Launcher.LaunchFolderPathAsync(Path.GetDirectoryName(path)!);
        }
        catch (Exception ex)
        {
            Diag.Log("report: failed: " + ex);
            ReportStatus.Text = Loc.Format("Error_Generic", ex.Message);
        }
        finally
        {
            ReportButton.IsEnabled = true;
        }
    }
}
