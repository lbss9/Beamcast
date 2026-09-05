using System.Runtime.InteropServices;
using static Beamcast.Audio.WasapiInterop;

namespace Beamcast.Audio;

/// <summary>
/// Captures the audio one process tree renders (or everything except one tree) through the
/// Windows process-loopback virtual device. This is the mechanism Discord, Teams and OBS use so a
/// shared screen carries the game's sound but not the voice call playing on the same speakers.
/// Requires Windows 10 build 20348 / Windows 11. Delivers interleaved 32-bit float at the
/// requested rate on a dedicated thread.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);

    private readonly int _sampleRate;
    private readonly int _channels;
    private IAudioClient? _client;
    private IAudioCaptureClient? _captureClient;
    private AutoResetEvent? _sampleReady;
    private Thread? _thread;
    private volatile bool _running;
    private long _packets;
    private long _startedAt;

    public ProcessLoopbackCapture(int targetProcessId, bool excludeTree, int sampleRate = 48000, int channels = 2)
    {
        TargetProcessId = targetProcessId;
        ExcludeTree = excludeTree;
        _sampleRate = sampleRate;
        _channels = channels;
    }

    public int TargetProcessId { get; }
    public bool ExcludeTree { get; }

    /// <summary>Buffers delivered since <see cref="Start"/>.</summary>
    public long Packets => Interlocked.Read(ref _packets);

    /// <summary>
    /// True when the stream has been running for a while without ever delivering a buffer. The
    /// process-loopback device occasionally comes up dead for a process that is clearly rendering;
    /// activating it again fixes it, so the owner should recreate a stale capture.
    /// </summary>
    public bool IsStale => _running && Packets == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_startedAt) > StaleAfter;

    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    /// <summary>Interleaved float samples, called on the capture thread.</summary>
    public event Action<ReadOnlyMemory<float>>? SamplesArrived;

    public event Action<Exception>? Faulted;

    public static bool IsSupported => Environment.OSVersion.Version >= new Version(10, 0, 20348);

    public void Start()
    {
        if (_client is not null)
            throw new InvalidOperationException("Already started.");

        _client = Activate(TargetProcessId, ExcludeTree);
        var format = WaveFormatExtensible.Float(_sampleRate, _channels);
        var formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>());
        try
        {
            Marshal.StructureToPtr(format, formatPtr, false);
            var flags = StreamFlagsLoopback | StreamFlagsEventCallback | StreamFlagsAutoConvertPcm | StreamFlagsSrcDefaultQuality;
            Marshal.ThrowExceptionForHR(_client.Initialize(ShareModeShared, flags, 0, 0, formatPtr, IntPtr.Zero));
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }

        _sampleReady = new AutoResetEvent(false);
        Marshal.ThrowExceptionForHR(_client.SetEventHandle(_sampleReady.SafeWaitHandle.DangerousGetHandle()));
        var iid = IidAudioCaptureClient;
        Marshal.ThrowExceptionForHR(_client.GetService(ref iid, out var service));
        _captureClient = (IAudioCaptureClient)service;

        _running = true;
        _startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _thread = new Thread(CaptureLoop) { Name = $"Beamcast audio pid {TargetProcessId}", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
        Marshal.ThrowExceptionForHR(_client.Start());
    }

    private static IAudioClient Activate(int processId, bool excludeTree)
    {
        var parameters = new AudioClientActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = excludeTree ? LoopbackModeExcludeTree : LoopbackModeIncludeTree,
        };
        var size = Marshal.SizeOf<AudioClientActivationParams>();
        var blob = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(parameters, blob, false);
            var propVariant = new BlobPropVariant { Type = BlobPropVariant.VtBlob, BlobSize = (uint)size, BlobData = blob };
            var handler = new CompletionHandler();
            var iid = IidAudioClient;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(VirtualAudioDeviceProcessLoopback, ref iid, ref propVariant, handler, out var operation));

            if (!handler.Completed.Wait(ActivationTimeout))
                throw new TimeoutException("Audio activation timed out.");
            Marshal.ThrowExceptionForHR(handler.Result);
            return handler.Client ?? throw new InvalidOperationException("Audio activation returned no client.");
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    private unsafe void CaptureLoop()
    {
        var client = _captureClient!;
        var ready = _sampleReady!;
        var scratch = new float[_sampleRate * _channels / 10];
        try
        {
            while (_running)
            {
                if (!ready.WaitOne(100))
                    continue;

                while (_running)
                {
                    Marshal.ThrowExceptionForHR(client.GetNextPacketSize(out var packetFrames));
                    if (packetFrames == 0)
                        break;

                    Marshal.ThrowExceptionForHR(client.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                    Interlocked.Increment(ref _packets);
                    try
                    {
                        var samples = (int)frames * _channels;
                        if (scratch.Length < samples)
                            scratch = new float[samples];
                        if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
                            Array.Clear(scratch, 0, samples);
                        else
                            new ReadOnlySpan<float>((void*)data, samples).CopyTo(scratch);
                        SamplesArrived?.Invoke(new ReadOnlyMemory<float>(scratch, 0, samples));
                    }
                    finally
                    {
                        client.ReleaseBuffer(frames);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (_running)
                Faulted?.Invoke(ex);
        }
    }

    public void Dispose()
    {
        _running = false;
        SafeTry.Run(() => _client?.Stop());
        _sampleReady?.Set();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        if (_captureClient is not null)
            SafeTry.Run(() => Marshal.ReleaseComObject(_captureClient));
        if (_client is not null)
            SafeTry.Run(() => Marshal.ReleaseComObject(_client));
        _captureClient = null;
        _client = null;
        _sampleReady?.Dispose();
        _sampleReady = null;
    }

    /// <summary>Receives the async activation result on a COM worker thread.</summary>
    [ComVisible(true)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public ManualResetEventSlim Completed { get; } = new(false);
        public int Result { get; private set; } = unchecked((int)0x80004005);
        public IAudioClient? Client { get; private set; }

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            try
            {
                var hr = activateOperation.GetActivateResult(out var result, out var activated);
                Result = hr < 0 ? hr : result;
                if (Result >= 0 && activated is IAudioClient client)
                    Client = client;
                else if (Result >= 0)
                    Result = unchecked((int)0x80004002);
            }
            catch (Exception ex)
            {
                Result = ex.HResult;
            }
            finally
            {
                Completed.Set();
            }
            return 0;
        }
    }
}
