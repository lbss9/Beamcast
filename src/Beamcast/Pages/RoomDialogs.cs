using Beamcast.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beamcast.Pages;

/// <summary>Dialogs for creating and editing a room, generating invites and confirming deletion.</summary>
internal static class RoomDialogs
{
    private static readonly double[] TtlChoices = [1, 6, 24, 72, 168];

    public static async Task<RoomCreateOptions?> CreateAsync(XamlRoot root)
    {
        var form = new RoomForm(null);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Create_Title"),
            Content = form.Panel,
            PrimaryButtonText = Loc.Get("Create_Button"),
            CloseButtonText = Loc.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        form.NameBox.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = form.NameBox.Text.Trim().Length > 0;
        dialog.IsPrimaryButtonEnabled = false;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return new RoomCreateOptions
        {
            Name = form.NameBox.Text.Trim(),
            Visibility = form.SelectedVisibility,
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
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Edit_Title"),
            Content = form.Panel,
            PrimaryButtonText = Loc.Get("Edit_Save"),
            CloseButtonText = Loc.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        return form.Read();
    }

    public static async Task<(TimeSpan? ExpiresIn, int MaxUses)?> InviteAsync(XamlRoot root)
    {
        var expiry = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = Loc.Get("Invite_Expiry") };
        expiry.Items.Add(Loc.Get("Expiry_1h"));
        expiry.Items.Add(Loc.Get("Expiry_24h"));
        expiry.Items.Add(Loc.Get("Expiry_7d"));
        expiry.Items.Add(Loc.Get("Expiry_Never"));
        expiry.SelectedIndex = 1;
        var uses = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Header = Loc.Get("Invite_Uses") };
        uses.Items.Add(Loc.Get("Uses_1"));
        uses.Items.Add(Loc.Get("Uses_5"));
        uses.Items.Add(Loc.Get("Uses_Unlimited"));
        uses.SelectedIndex = 2;
        var panel = new StackPanel { Spacing = 10, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Invite_DialogHint"), TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
        panel.Children.Add(expiry);
        panel.Children.Add(uses);
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Invite_Title"),
            Content = panel,
            PrimaryButtonText = Loc.Get("Invite_Generate"),
            CloseButtonText = Loc.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
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
        var dialog = new ContentDialog
        {
            Title = Loc.Get("Delete_Title"),
            Content = new TextBlock { Text = Loc.Format("Delete_Body", roomName), TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.Get("Delete_Confirm"),
            CloseButtonText = Loc.Get("Dialog_Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>The room fields, shared by the create/edit dialogs and the room's Settings tab.</summary>
    internal sealed class RoomForm
    {
        public RoomForm(RoomInfo? existing)
        {
            Panel = new StackPanel { Spacing = 10, MinWidth = 360 };
            NameBox = new TextBox { Header = Loc.Get("Lounge_Name/Header"), MaxLength = LoungeProtocol.MaxNameLength, Text = existing?.Name ?? string.Empty };
            Panel.Children.Add(NameBox);

            VisibilityBox = new ComboBox { Header = Loc.Get("Create_Visibility"), HorizontalAlignment = HorizontalAlignment.Stretch };
            VisibilityBox.Items.Add(Loc.Get("Visibility_Public"));
            VisibilityBox.Items.Add(Loc.Get("Visibility_Private"));
            VisibilityBox.SelectedIndex = existing?.IsPublic == true ? 0 : 1;
            Panel.Children.Add(VisibilityBox);

            KindBox = new ComboBox { Header = Loc.Get("Create_Kind"), HorizontalAlignment = HorizontalAlignment.Stretch };
            KindBox.Items.Add(Loc.Get("Kind_Permanent"));
            KindBox.Items.Add(Loc.Get("Kind_Temporary"));
            KindBox.SelectedIndex = existing?.IsTemporary == true ? 1 : 0;
            Panel.Children.Add(KindBox);

            TtlBox = new ComboBox { Header = Loc.Get("Create_Ttl"), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var hours in TtlChoices)
                TtlBox.Items.Add(hours < 24 ? Loc.Format("Ttl_Hours", hours) : Loc.Format("Ttl_Days", hours / 24));
            var currentTtl = existing?.TtlHours ?? LoungeProtocol.DefaultTtlHours;
            TtlBox.SelectedIndex = Math.Max(0, Array.FindIndex(TtlChoices, h => h >= currentTtl));
            TtlBox.Visibility = KindBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            KindBox.SelectionChanged += (_, _) => TtlBox.Visibility = KindBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            Panel.Children.Add(TtlBox);

            PasswordBox = new PasswordBox
            {
                Header = existing is null ? Loc.Get("Create_Password") : Loc.Get("Edit_Password"),
                PasswordRevealMode = PasswordRevealMode.Peek,
                MaxLength = 128,
            };
            Panel.Children.Add(PasswordBox);
            ClearPasswordBox = new CheckBox { Content = Loc.Get("Edit_ClearPassword"), Visibility = existing?.HasPassword == true ? Visibility.Visible : Visibility.Collapsed };
            ClearPasswordBox.Checked += (_, _) => PasswordBox.IsEnabled = false;
            ClearPasswordBox.Unchecked += (_, _) => PasswordBox.IsEnabled = true;
            Panel.Children.Add(ClearPasswordBox);
            if (existing is not null)
                Panel.Children.Add(new TextBlock { Text = Loc.Get("Edit_PasswordNote"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });

            BroadcastBox = new ComboBox { Header = Loc.Get("Create_Broadcast"), HorizontalAlignment = HorizontalAlignment.Stretch };
            BroadcastBox.Items.Add(Loc.Get("Broadcast_Everyone"));
            BroadcastBox.Items.Add(Loc.Get("Broadcast_Owner"));
            BroadcastBox.SelectedIndex = existing?.Broadcast == BroadcastPolicy.Owner ? 1 : 0;
            Panel.Children.Add(BroadcastBox);

            MaxMembersBox = new NumberBox
            {
                Header = Loc.Get("Create_MaxMembers"),
                Minimum = 0,
                Maximum = LoungeProtocol.MaxMembersCap,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                Value = existing?.MaxMembers ?? 0,
            };
            Panel.Children.Add(MaxMembersBox);
        }

        public StackPanel Panel { get; }
        public TextBox NameBox { get; }
        public ComboBox VisibilityBox { get; }
        public ComboBox KindBox { get; }
        public ComboBox TtlBox { get; }
        public PasswordBox PasswordBox { get; }
        public CheckBox ClearPasswordBox { get; }
        public ComboBox BroadcastBox { get; }
        public NumberBox MaxMembersBox { get; }

        public string SelectedVisibility => VisibilityBox.SelectedIndex == 0 ? RoomVisibility.Public : RoomVisibility.Private;
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
                Visibility = SelectedVisibility,
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
