using Beamcast.Codec;
using Beamcast.Net;
using Microsoft.UI.Dispatching;

namespace Beamcast.Services;

public enum WatchState
{
    Disconnected,
    Connecting,
    Watching,
}

/// <summary>
/// Process-wide viewer session so the stream survives page navigation. Decoded frames are
/// coalesced: if the UI is still busy with the previous frame, the newest one simply replaces it.
/// </summary>
public sealed class WatchService
{
    public static WatchService Instance { get; } = new();

    private DispatcherQueue? _ui;
    private ViewerClient? _client;
    private DecodedFrame? _pendingFrame;
    private int _uiBusy;

    private WatchService() { }

    public event Action<WatchState>? StateChanged;
    public event Action<DecodedFrame>? FrameReady;
    public event Action<IReadOnlyList<string>>? ViewersChanged;
    public event Action<ViewerStats>? StatsChanged;
    public event Action<string>? StreamStateChanged;
    public event Action<string>? Closed;

    public WatchState State { get; private set; }
    public WelcomeMessage? Welcome { get; private set; }
    public IReadOnlyList<string> Viewers { get; private set; } = [];
    public ViewerStats? LastStats { get; private set; }
    public string StreamState { get; private set; } = StreamStates.Live;
    public InviteTarget? Target { get; private set; }

    public void Initialize(DispatcherQueue ui) => _ui = ui;

    public async Task<WelcomeMessage> ConnectAsync(InviteTarget target, string displayName, CancellationToken ct)
    {
        if (State != WatchState.Disconnected)
            throw new InvalidOperationException("Already connected.");

        SetState(WatchState.Connecting);
        var client = new ViewerClient();
        client.FrameReady += OnFrame;
        client.ViewersChanged += viewers => Post(() =>
        {
            Viewers = viewers;
            ViewersChanged?.Invoke(viewers);
        });
        client.StatsUpdated += stats => Post(() =>
        {
            LastStats = stats;
            StatsChanged?.Invoke(stats);
        });
        client.StreamStateChanged += state => Post(() =>
        {
            StreamState = state;
            StreamStateChanged?.Invoke(state);
        });
        client.Closed += reason => Post(() => OnClosed(client, reason));

        try
        {
            var welcome = await client.ConnectAsync(target, displayName, ct);
            _client = client;
            Welcome = welcome;
            Target = target;
            Viewers = welcome.Viewers;
            StreamState = welcome.State;
            LastStats = null;
            SetState(WatchState.Watching);
            return welcome;
        }
        catch
        {
            client.Dispose();
            SetState(WatchState.Disconnected);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        var client = _client;
        if (client is null)
            return;
        await client.DisconnectAsync();
    }

    private void OnClosed(ViewerClient client, string reason)
    {
        if (!ReferenceEquals(_client, client))
            return;
        _client = null;
        client.Dispose();
        Welcome = null;
        Target = null;
        Interlocked.Exchange(ref _pendingFrame, null);
        SetState(WatchState.Disconnected);
        Closed?.Invoke(reason);
    }

    private void OnFrame(DecodedFrame frame)
    {
        var ui = _ui;
        if (ui is null)
            return;

        Interlocked.Exchange(ref _pendingFrame, frame);
        if (Interlocked.CompareExchange(ref _uiBusy, 1, 0) != 0)
            return;

        if (!ui.TryEnqueue(() =>
            {
                try
                {
                    var latest = Interlocked.Exchange(ref _pendingFrame, null);
                    if (latest is not null && State == WatchState.Watching)
                        FrameReady?.Invoke(latest);
                }
                finally
                {
                    Interlocked.Exchange(ref _uiBusy, 0);
                }
            }))
        {
            Interlocked.Exchange(ref _uiBusy, 0);
        }
    }

    private void SetState(WatchState state)
    {
        if (State == state)
            return;
        State = state;
        Post(() => StateChanged?.Invoke(state));
    }

    private void Post(Action action)
    {
        var ui = _ui;
        if (ui is null || ui.HasThreadAccess)
            action();
        else
            ui.TryEnqueue(() => action());
    }
}
