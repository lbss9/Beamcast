using Beamcast.Net;
using Beamcast.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Beamcast.Pages;

/// <summary>Entry screen: pick a server, then create a lounge or join one with a code.</summary>
public sealed partial class LoungePage : Page
{
    private const int MinPasswordLength = 4;

    private readonly LoungeService _lounge = LoungeService.Instance;
    private bool _busy;

    public LoungePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var settings = SettingsStore.Load();
        ServerBox.Text = settings.RelayUrl;
        NameBox.Text = settings.DisplayName;
        LoungeNameBox.Text = settings.LastLoungeName;
        CodeBox.Text = settings.LastLoungeCode;
        _lounge.Closed += OnClosed;
        Validate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _lounge.Closed -= OnClosed;
    }

    private void OnClosed(string reason)
    {
        ErrorText.Text = reason switch
        {
            "lost" => Loc.Get("Lounge_Lost"),
            _ => string.Empty,
        };
    }

    private void OnInputChanged(object sender, object e) => Validate();

    private bool ServerValid => LoungeProtocol.TryNormalizeServer(ServerBox.Text, out _);

    private void Validate()
    {
        var server = ServerValid;
        CreateButton.IsEnabled = !_busy && server && LoungeNameBox.Text.Trim().Length > 0 && CreatePasswordBox.Password.Length >= MinPasswordLength;
        var codeOk = LoungeInvite.TryDecode(CodeBox.Text, ServerBox.Text, out _);
        JoinButton.IsEnabled = !_busy && codeOk && JoinPasswordBox.Password.Length > 0;
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (!LoungeProtocol.TryNormalizeServer(ServerBox.Text, out var server))
            return;
        await RunAsync(async ct =>
        {
            await _lounge.CreateAsync(server, LoungeNameBox.Text, CreatePasswordBox.Password, NameBox.Text, ct);
            SettingsStore.Update(s =>
            {
                s.RelayUrl = server;
                s.LastLoungeName = LoungeNameBox.Text.Trim();
                s.LastLoungeCode = _lounge.Code;
            });
        });
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (!LoungeInvite.TryDecode(CodeBox.Text, ServerBox.Text, out var target))
        {
            ErrorText.Text = Loc.Get("Lounge_BadCode");
            return;
        }
        await RunAsync(async ct =>
        {
            await _lounge.JoinAsync(target.ServerUrl, target.Code, JoinPasswordBox.Password, NameBox.Text, ct);
            SettingsStore.Update(s =>
            {
                s.RelayUrl = target.ServerUrl;
                s.LastLoungeCode = target.Code;
            });
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        ErrorText.Text = string.Empty;
        var name = NameBox.Text.Trim();
        if (name.Length > 0)
            SettingsStore.Update(s => s.DisplayName = name);

        _busy = true;
        Validate();
        Progress.IsActive = true;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await action(cts.Token);
        }
        catch (LoungeException ex)
        {
            ErrorText.Text = ex.Reason switch
            {
                LoungeProtocol.ReasonBadPassword => Loc.Get("Lounge_WrongPassword"),
                LoungeProtocol.ReasonNoLounge => Loc.Get("Lounge_NotFound"),
                LoungeProtocol.ReasonBadKey => Loc.Get("Error_AppKey"),
                LoungeProtocol.ReasonVersion => Loc.Get("Error_Version"),
                "unreachable" or "timeout" => Loc.Get("Lounge_Unreachable"),
                _ => Loc.Format("Error_Generic", ex.Reason),
            };
        }
        catch (Exception ex)
        {
            ErrorText.Text = Loc.Format("Error_Generic", ex.Message);
        }
        finally
        {
            Progress.IsActive = false;
            _busy = false;
            Validate();
        }
    }
}
