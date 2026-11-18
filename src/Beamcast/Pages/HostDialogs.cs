using Beamcast.Net;
using Beamcast.Services;
using Beamcast.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beamcast.Pages;

/// <summary>Dialogs opened from the side bar: add a host, type a host's key, change the display name.</summary>
internal static class HostDialogs
{
    /// <summary>Returns the normalized address of the host just saved, or null when cancelled.</summary>
    public static async Task<string?> AddAsync(XamlRoot root)
    {
        var box = new TextBox { Header = Loc.Get("AddHost_Field"), PlaceholderText = Loc.Get("AddHost_Placeholder") };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("AddHost_Body"), Style = Beam.Style("HintTextStyle") });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = Loc.Get("AddHost_Hint"), Style = Beam.Style("FaintTextStyle") });
        var dialog = RoomDialogs.Build(root, Loc.Get("AddHost_Title"), panel, Loc.Get("AddHost_Confirm"));
        dialog.IsPrimaryButtonEnabled = false;
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = LoungeProtocol.TryNormalizeServer(box.Text, out _);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !LoungeProtocol.TryNormalizeServer(box.Text, out var url))
            return null;
        LoungeService.RememberHost(url);
        return url;
    }

    /// <summary>Lets the person type the key this host demands (BEAMCAST_APP_KEY on that server). True when saved.</summary>
    public static async Task<bool> EditKeyAsync(XamlRoot root, string url)
    {
        var host = SettingsStore.Load().Hosts.FirstOrDefault(h => string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));
        var box = new PasswordBox
        {
            PasswordRevealMode = PasswordRevealMode.Peek,
            MaxLength = 256,
            Header = Loc.Get("Lounge_HostKeyField"),
            Password = LoungeService.AppKeyFor(url),
        };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = LoungeProtocol.DisplayHost(url), Style = Beam.Style("FaintTextStyle") });
        panel.Children.Add(new TextBlock { Text = Loc.Get("Lounge_HostKeyHint"), Style = Beam.Style("HintTextStyle") });
        panel.Children.Add(box);
        var dialog = RoomDialogs.Build(root, host?.Name is { Length: > 0 } name ? name : LoungeProtocol.DisplayHost(url), panel, Loc.Get("Edit_Save"));
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return false;
        LoungeService.RememberHost(url, appKey: box.Password);
        return true;
    }

    /// <summary>Returns the new display name, or null when cancelled or left empty.</summary>
    public static async Task<string?> EditNameAsync(XamlRoot root, string current)
    {
        var box = new TextBox { MaxLength = 32, Text = current, PlaceholderText = Loc.Get("Name_Placeholder") };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Name_Body"), Style = Beam.Style("HintTextStyle") });
        panel.Children.Add(box);
        var dialog = RoomDialogs.Build(root, Loc.Get("Name_Title"), panel, Loc.Get("Edit_Save"));
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        var name = box.Text.Trim();
        return name.Length > 0 ? name : null;
    }
}
