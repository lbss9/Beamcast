using System.Diagnostics;
using System.Runtime.InteropServices;
using Beamcast.Capture;
using Beamcast.Net;

namespace Beamcast.Audio;

/// <summary>What the audio broadcaster is capturing right now, for the UI to describe.</summary>
public sealed record AudioCaptureInfo(string Mode, string? AppName, IReadOnlyList<string> ExcludedApps, bool AppGone, int Sources)
{
    public static readonly AudioCaptureInfo None = new(AudioMode.Off, null, [], false, 0);
}

/// <summary>
/// Turns "share the game's sound but not the voice call" into packets. In App mode it captures the
/// shared window's process tree; in System mode it captures every process tree that owns an audio
/// session except voice apps and Beamcast itself, re-scanning as apps start and stop. Everything is
/// mixed into 20 ms frames and encoded with Opus on a dedicated thread.
/// </summary>
public sealed class AudioBroadcaster : IDisposable
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly AudioMixer _mixer = new();
    private readonly Dictionary<int, ProcessLoopbackCapture> _captures = new();
    private readonly object _sync = new();
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private string _mode = AudioMode.Off;
    private int _appPid;
    private uint _sequence;
    private volatile AudioCaptureInfo _info = AudioCaptureInfo.None;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    /// <summary>Encoded audio packet bodies (header + Opus), raised on the audio thread.</summary>
    public event Action<byte[]>? PacketReady;

    public event Action<string>? Faulted;

    public bool IsRunning => _thread is not null;

    /// <summary>What is being captured, for the UI.</summary>
    public AudioCaptureInfo Info => _info;

    public static bool IsSupported => ProcessLoopbackCapture.IsSupported;

    /// <summary>Resolves Auto against the kind of source being shared.</summary>
    public static string Resolve(string mode, CaptureSource? source)
    {
        var normalized = AudioMode.Normalize(mode);
        if (normalized != AudioMode.Auto)
            return normalized;
        return source?.Kind == CaptureSourceKind.Window ? AudioMode.App : AudioMode.System;
    }

    public void Start(string mode, CaptureSource? source)
    {
        Stop();
        _mode = Resolve(mode, source);
        if (_mode == AudioMode.Off || !IsSupported)
            return;
        _appPid = source?.ProcessId ?? 0;
        if (_mode == AudioMode.App && _appPid <= 0)
            _mode = AudioMode.System;

        _cts = new CancellationTokenSource();
        _thread = new Thread(() => Loop(_cts.Token)) { Name = "Beamcast audio", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Follows a change of shared source while live.</summary>
    public void Retarget(string mode, CaptureSource? source)
    {
        if (!IsRunning)
            return;
        var resolved = Resolve(mode, source);
        var pid = source?.ProcessId ?? 0;
        if (resolved == _mode && (resolved != AudioMode.App || pid == _appPid))
            return;
        Start(mode, source);
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        cts?.Dispose();
        lock (_sync)
        {
            foreach (var capture in _captures.Values)
                capture.Dispose();
            _captures.Clear();
        }
        _info = AudioCaptureInfo.None;
    }

    private void Loop(CancellationToken ct)
    {
        timeBeginPeriod(1);
        try
        {
            using var encoder = new OpusAudioEncoder();
            var frame = new float[AudioMixer.FrameSamples];
            var period = TimeSpan.FromMilliseconds(20);
            var next = Stopwatch.GetTimestamp();
            var lastScan = 0L;
            var silentFrames = 0;

            while (!ct.IsCancellationRequested)
            {
                if (Stopwatch.GetElapsedTime(lastScan) >= ScanInterval)
                {
                    lastScan = Stopwatch.GetTimestamp();
                    Rescan();
                }

                var has = _mixer.Mix(frame);
                if (has)
                    silentFrames = 0;
                else
                    silentFrames++;

                // Keep the receiver's clock fed through short gaps, but stop sending after a second
                // of nothing so an idle desktop costs no bandwidth.
                if (has || silentFrames < 50)
                {
                    var packet = encoder.Encode(frame);
                    if (packet.Length > 0)
                    {
                        var header = new AudioPacketHeader(++_sequence, Environment.TickCount64, AudioMixer.SampleRate, AudioMixer.Channels);
                        PacketReady?.Invoke(AudioPacket.Build(header, packet));
                    }
                }

                next += (long)(period.TotalSeconds * Stopwatch.Frequency);
                var wait = next - Stopwatch.GetTimestamp();
                if (wait > 0)
                {
                    var ms = (int)(wait * 1000 / Stopwatch.Frequency);
                    if (ms > 1)
                        Thread.Sleep(ms - 1);
                    while (Stopwatch.GetTimestamp() < next)
                        Thread.SpinWait(50);
                }
                else if (wait < -(long)(Stopwatch.Frequency / 5))
                {
                    next = Stopwatch.GetTimestamp();
                }
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex.Message);
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    private void Rescan()
    {
        IReadOnlyList<int> wanted;
        var processes = ProcessTable.Snapshot();
        if (_mode == AudioMode.App)
        {
            var present = processes.TryGetValue(_appPid, out var row);
            wanted = present ? [_appPid] : [];
            _info = new AudioCaptureInfo(AudioMode.App, present ? row.Name : null, [], !present, wanted.Count);
        }
        else
        {
            var sessions = AudioSessionScanner.SessionProcessIds();
            wanted = AudioSourceSelector.SelectSystemSources(sessions, processes, Environment.ProcessId);
            _info = new AudioCaptureInfo(AudioMode.System, null, AudioSourceSelector.RunningVoiceApps(processes), false, wanted.Count);
        }

        lock (_sync)
        {
            foreach (var pid in _captures.Where(kv => !wanted.Contains(kv.Key) || kv.Value.IsStale).Select(kv => kv.Key).ToList())
            {
                _captures[pid].Dispose();
                _captures.Remove(pid);
                _mixer.RemoveSource(pid);
            }

            foreach (var pid in wanted)
            {
                if (_captures.ContainsKey(pid))
                    continue;
                var capture = new ProcessLoopbackCapture(pid, excludeTree: false, AudioMixer.SampleRate, AudioMixer.Channels);
                var id = pid;
                capture.SamplesArrived += samples => _mixer.Write(id, samples.Span);
                capture.Faulted += _ => Post(() => Drop(id));
                try
                {
                    _mixer.AddSource(pid);
                    capture.Start();
                    _captures[pid] = capture;
                }
                catch (Exception)
                {
                    _mixer.RemoveSource(pid);
                    capture.Dispose();
                }
            }
        }
    }

    private void Drop(int pid)
    {
        lock (_sync)
        {
            if (_captures.Remove(pid, out var capture))
                capture.Dispose();
            _mixer.RemoveSource(pid);
        }
    }

    private static void Post(Action action) => ThreadPool.QueueUserWorkItem(_ => action());

    public void Dispose() => Stop();
}
