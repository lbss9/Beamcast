using Beamcast.Controls;
using Beamcast.Net;
using Beamcast.Ui;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beamcast.Pages;

/// <summary>Dialogs for creating and editing a room, generating invites and confirming deletion.</summary>
internal static class RoomDialogs
{
    private static readonly double[] TtlChoices = [1, 6, 24, 72, 168];

    /// <summary>A dialog in the app's look: coral primary button (red when the action cannot be undone).</summary>
    public static ContentDialog Build(XamlRoot root, string title, object content, string primaryText, bool danger = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = Loc.Get("Dialog_Cancel"),
            // No default button on a destructive dialog: Enter must not delete, and a default
            // "Cancel" would be painted in the accent colour right next to the red button.
            DefaultButton = danger ? ContentDialogButton.None : ContentDialogButton.Primary,
            PrimaryButtonStyle = Beam.Style(danger ? "BeamDangerButtonStyle" : "BeamPrimaryButtonStyle"),
            CloseButtonStyle = Beam.Style("BeamButtonStyle"),
            XamlRoot = root,
        };
        if (App.Main is not null)
            dialog.RequestedTheme = App.Main.RootTheme;
        return dialog;
    }

    public static async Task<RoomCreateOptions?> CreateAsync(XamlRoot root)
    {
        var form = new RoomForm(null);
        var dialog = Build(root, Loc.Get("Create_Title"), form.Panel, Loc.Get("Lounge_CreateButton/Content"));
        form.NameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = form.NameBox.Text.Trim().Length > 0;
        form.NameBox.Loaded += (_, _) => form.NameBox.Focus(FocusState.Programmatic);
        dialog.IsPrimaryButtonEnabled = false;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return new RoomCreateOptions
        {
            Name = form.NameBox.Text.Trim(),
            Visibility = RoomVisibility.Public,
            Kind = form.SelectedKind,
            TtlHours = form.TtlHours,
            Password = form.PasswordBox.Password,
            Broadcast = form.SelectedBroadcast,
            MaxMembers = form.MaxMembers,
        };
    }

    /// <summary>Returns the update to send plus the new password (null = keep, "" = clear).</summary>
    public static async Task<(RoomUpdateMessage Update, string? NewPassword)?> EditAsync(XamlRoot root, RoomInfo room)
    {
        var form = new RoomForm(room);
        var dialog = Build(root, Loc.Get("Edit_Title"), new ScrollViewer { Content = form.Panel, MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Loc.Get("Edit_Save"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return form.Read();
    }

    /// <summary>Asks for a room code or a BC-… invite. Returns the text typed, or null when cancelled.</summary>
    public static async Task<string?> JoinCodeAsync(XamlRoot root, string server)
    {
        var box = new TextBox { PlaceholderText = Loc.Get("Lounge_Code/PlaceholderText"), FontFamily = Beam.Mono };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Join_Body"), Style = Beam.Style("HintTextStyle") });
        panel.Children.Add(box);
        panel.Children.Add(new TextBlock { Text = Loc.Get("Join_Hint"), Style = Beam.Style("FaintTextStyle") });
        var dialog = Build(root, Loc.Get("Join_Title"), panel, Loc.Get("Lounge_Enter"));
        dialog.IsPrimaryButtonEnabled = false;
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = LoungeInvite.TryDecode(box.Text, server, out _);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>Asks for the room password. Null = cancelled.</summary>
    public static async Task<(string Password, bool Remember)?> PasswordAsync(XamlRoot root, string roomName, bool wrong = false)
    {
        var box = new PasswordBox { PasswordRevealMode = PasswordRevealMode.Peek, MaxLength = 128 };
        var remember = new CheckBox { Content = Loc.Get("Lounge_RememberPassword") };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        var lead = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        lead.Children.Add(Beam.Icon("\uE72E", 14, wrong ? "Danger" : "Text2"));
        lead.Children.Add(new TextBlock { Text = wrong ? Loc.Get("Lounge_WrongPassword") : Loc.Get("Lounge_PasswordHint"), TextWrapping = TextWrapping.Wrap, Foreground = Beam.Brush(wrong ? "Danger" : "Text2") });
        panel.Children.Add(lead);
        panel.Children.Add(box);
        panel.Children.Add(remember);
        var dialog = Build(root, roomName.Length > 0 ? roomName : Loc.Get("Lounge_PasswordTitle"), panel, Loc.Get("Lounge_Enter"));
        dialog.IsPrimaryButtonEnabled = false;
        box.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Password.Length > 0;
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Password.Length == 0)
            return null;
        return (box.Password, remember.IsChecked == true);
    }

    public static async Task<(TimeSpan? ExpiresIn, int MaxUses)?> InviteAsync(XamlRoot root)
    {
        var expiry = new Segmented();
        expiry.SetItems([(Loc.Get("Expiry_1h"), null), (Loc.Get("Expiry_24h"), null), (Loc.Get("Expiry_7d"), null), (Loc.Get("Expiry_Never"), null)], 1);
        var uses = new Segmented();
        uses.SetItems([(Loc.Get("Uses_1"), null), (Loc.Get("Uses_5"), null), (Loc.Get("Uses_Unlimited"), null)], 2);
        var panel = new StackPanel { Spacing = 8, MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Invite_DialogHint"), Style = Beam.Style("HintTextStyle"), Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(FieldLabel(Loc.Get("Invite_Expiry")));
        panel.Children.Add(expiry);
        panel.Children.Add(FieldLabel(Loc.Get("Invite_Uses"), 8));
        panel.Children.Add(uses);
        var dialog = Build(root, Loc.Get("Invite_Title"), panel, Loc.Get("Invite_Generate"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        TimeSpan? expiresIn = expiry.SelectedIndex switch
        {
            0 => TimeSpan.FromHours(1),
            1 => TimeSpan.FromHours(24),
            2 => TimeSpan.FromDays(7),
            _ => null,
        };
        var maxUses = uses.SelectedIndex switch { 0 => 1, 1 => 5, _ => 0 };
        return (expiresIn, maxUses);
    }

    public static async Task<bool> ConfirmDeleteAsync(XamlRoot root, string roomName)
    {
        var body = new TextBlock { Text = Loc.Get("Delete_Body"), TextWrapping = TextWrapping.Wrap, Foreground = Beam.Brush("Text2"), MinWidth = 340 };
        var dialog = Build(root, Loc.Format("Delete_Title", roomName), body, Loc.Get("Room_SettingsDeleteButtonText/Text"), danger: true);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    internal static TextBlock FieldLabel(string text, double top = 0) =>
        new() { Text = text, FontSize = 13, Foreground = Beam.Brush("Text2"), Margin = new Thickness(0, top, 0, 0) };

    /// <summary>The room fields, shared by the create/edit dialogs and the room's Settings tab.</summary>
    internal sealed class RoomForm
    {
        public RoomForm(RoomInfo? existing)
        {
            Panel = new StackPanel { Spacing = 12, MinWidth = 380 };
            NameBox = new TextBox { Header = Loc.Get("Lounge_Name/Header"), PlaceholderText = Loc.Get("Lounge_Name/PlaceholderText"), MaxLength = LoungeProtocol.MaxNameLength, Text = existing?.Name ?? string.Empty };
            Panel.Children.Add(NameBox);

            PasswordBox = new PasswordBox
            {
                Header = existing is null ? Loc.Get("Create_Password") : Loc.Get("Edit_Password"),
                PlaceholderText = existing is null ? Loc.Get("Create_PasswordPlaceholder") : string.Empty,
                PasswordRevealMode = PasswordRevealMode.Peek,
                MaxLength = 128,
            };
            Panel.Children.Add(PasswordBox);
            ClearPasswordBox = new CheckBox { Content = Loc.Get("Edit_ClearPassword"), Visibility = existing?.HasPassword == true ? Visibility.Visible : Visibility.Collapsed };
            ClearPasswordBox.Checked += (_, _) => PasswordBox.IsEnabled = false;
            ClearPasswordBox.Unchecked += (_, _) => PasswordBox.IsEnabled = true;
            Panel.Children.Add(ClearPasswordBox);
            Panel.Children.Add(new TextBlock { Text = Loc.Get(existing is null ? "Create_PasswordHint" : "Edit_PasswordNote"), Style = Beam.Style("FaintTextStyle"), Margin = new Thickness(0, -6, 0, 0) });

            // The rest has sensible defaults: on "create" it stays folded so the dialog is two fields.
            var more = new StackPanel { Spacing = 8 };
            more.Children.Add(FieldLabel(Loc.Get("Create_Broadcast")));
            BroadcastBox = new Segmented();
            BroadcastBox.SetItems([(Loc.Get("Broadcast_Everyone"), null), (Loc.Get("Broadcast_Owner"), null)], existing?.Broadcast == BroadcastPolicy.Owner ? 1 : 0);
            more.Children.Add(BroadcastBox);

            more.Children.Add(FieldLabel(Loc.Get("Create_Kind"), 8));
            KindBox = new Segmented();
            KindBox.SetItems([(Loc.Get("Kind_Permanent"), null), (Loc.Get("Kind_TemporaryShort"), null)], existing?.IsTemporary == true ? 1 : 0);
            more.Children.Add(KindBox);

            TtlBox = new ComboBox { Header = Loc.Get("Create_Ttl"), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var hours in TtlChoices)
                TtlBox.Items.Add(hours < 24 ? Loc.Format("Ttl_Hours", hours) : Loc.Format("Ttl_Days", hours / 24));
            var currentTtl = existing?.TtlHours ?? LoungeProtocol.DefaultTtlHours;
            TtlBox.SelectedIndex = Math.Max(0, Array.FindIndex(TtlChoices, h => h >= currentTtl));
            TtlBox.Visibility = KindBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            KindBox.SelectionChanged += (_, _) => TtlBox.Visibility = KindBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            more.Children.Add(TtlBox);

            MaxMembersBox = new NumberBox
            {
                Header = Loc.Get("Create_MaxMembers"),
                Minimum = 0,
                Maximum = LoungeProtocol.MaxMembersCap,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = existing?.MaxMembers ?? 0,
                Margin = new Thickness(0, 8, 0, 0),
            };
            more.Children.Add(MaxMembersBox);

            if (existing is null)
            {
                var header = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
                header.Children.Add(new TextBlock { Text = Loc.Get("Create_More"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                header.Children.Add(new TextBlock { Text = Loc.Get("Create_MoreHint"), Style = Beam.Style("FaintTextStyle"), FontSize = 11.5 });
                Panel.Children.Add(new Expander
                {
                    Header = header,
                    Content = more,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(14, 12, 14, 14),
                });
            }
            else
            {
                Panel.Children.Add(more);
            }
        }

        public StackPanel Panel { get; }
        public TextBox NameBox { get; }
        public Segmented KindBox { get; }
        public ComboBox TtlBox { get; }
        public PasswordBox PasswordBox { get; }
        public CheckBox ClearPasswordBox { get; }
        public Segmented BroadcastBox { get; }
        public NumberBox MaxMembersBox { get; }

        public string SelectedKind => KindBox.SelectedIndex == 1 ? RoomKind.Temporary : RoomKind.Permanent;
        public double TtlHours => TtlChoices[Math.Clamp(TtlBox.SelectedIndex, 0, TtlChoices.Length - 1)];
        public string SelectedBroadcast => BroadcastBox.SelectedIndex == 1 ? BroadcastPolicy.Owner : BroadcastPolicy.Everyone;
        public int MaxMembers => double.IsNaN(MaxMembersBox.Value) ? 0 : (int)MaxMembersBox.Value;

        /// <summary>The update to send plus the new password (null = keep, "" = clear).</summary>
        public (RoomUpdateMessage Update, string? NewPassword) Read()
        {
            var update = new RoomUpdateMessage
            {
                Name = NameBox.Text.Trim().Length > 0 ? NameBox.Text.Trim() : null,
                Kind = SelectedKind,
                TtlHours = TtlHours,
                Broadcast = SelectedBroadcast,
                MaxMembers = MaxMembers,
            };
            string? newPassword = null;
            if (ClearPasswordBox.IsChecked == true)
                newPassword = string.Empty;
            else if (PasswordBox.Password.Length > 0)
                newPassword = PasswordBox.Password;
            return (update, newPassword);
        }
    }
}
