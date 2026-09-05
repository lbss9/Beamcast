using Beamcast.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        NameBox.Text = settings.DisplayName;
        PortBox.Value = settings.Port;
        MaxViewersBox.Value = settings.MaxViewers;

        LanguageBox.Items.Clear();
        LanguageBox.Items.Add(Loc.Get("Settings_LanguageSystem"));
        LanguageBox.Items.Add("Português (Brasil)");
        LanguageBox.Items.Add("English");
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, settings.Language));

        ThemeBox.Items.Clear();
        ThemeBox.Items.Add(Loc.Get("Settings_ThemeSystem"));
        ThemeBox.Items.Add(Loc.Get("Settings_ThemeLight"));
        ThemeBox.Items.Add(Loc.Get("Settings_ThemeDark"));
        ThemeBox.SelectedIndex = Math.Max(0, Array.IndexOf(Themes, settings.Theme));
        UpdatesSwitch.IsOn = settings.CheckUpdatesOnLaunch;
        RelayUrlBox.Text = settings.RelayUrl;
        RelayKeyBox.Password = settings.RelayAppKey;
        _loading = false;
    }

    private void OnRelayUrlChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        var url = RelayUrlBox.Text.Trim();
        if (!InviteCode.IsValidRelayUrl(url))
            return;
        SettingsStore.Update(s => s.RelayUrl = url);
    }

    private void OnRelayKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var key = RelayKeyBox.Password.Trim();
        SettingsStore.Update(s => s.RelayAppKey = key);
    }

    private void OnRelayReset(object sender, RoutedEventArgs e)
    {
        _loading = true;
        RelayUrlBox.Text = AppInfo.DefaultRelayUrl;
        RelayKeyBox.Password = AppInfo.DefaultRelayAppKey;
        _loading = false;
        SettingsStore.Update(s =>
        {
            s.RelayUrl = AppInfo.DefaultRelayUrl;
            s.RelayAppKey = AppInfo.DefaultRelayAppKey;
        });
    }

    private void OnUpdatesToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var on = UpdatesSwitch.IsOn;
        SettingsStore.Update(s => s.CheckUpdatesOnLaunch = on);
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
            return;
        SettingsStore.Update(s => s.DisplayName = name);
    }

    private void OnPortChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value))
            return;
        var port = (int)sender.Value;
        if (!InviteCode.IsValidPort(port))
            return;
        SettingsStore.Update(s => s.Port = port);
    }

    private void OnMaxViewersChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value))
            return;
        SettingsStore.Update(s => s.MaxViewers = (int)sender.Value);
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
}
