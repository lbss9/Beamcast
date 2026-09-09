using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Beamcast.Codec.Gpu;

/// <summary>
/// Hardware H.264/HEVC encoder driven through an asynchronous Media Foundation transform
/// (NVENC, AMD VCN, Intel QSV or the D3D12 generic encoder, whichever the driver registers).
/// Input is an NV12 texture on the pipeline's device, so nothing is copied to system memory
/// until the compressed bitstream comes out.
/// </summary>
public sealed class MfVideoEncoder : IDisposable
{
    private const int EventNeedInput = 601;
    private const int EventHaveOutput = 602;
    private static readonly Guid Texture2DIid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MfMtMpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");

    private readonly GpuDevice _gpu;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly CodecApiSetter _codecApi;
    private readonly Thread _eventThread;
    private readonly bool _providesSamples;
    private readonly int _outputSize;
    private readonly long _frameDuration;
    private readonly object _inputLock = new();
    private int _needInput;
    private int _forceKeyframe;
    private int _framesSinceKeyframeRequest = -1;
    private uint _sequence;
    private long _submittedTicks;
    private volatile bool _disposed;

    public MfVideoEncoder(GpuDevice gpu, VideoCodec codec, int width, int height, int fps, int bitrateKbps)
    {
        _gpu = gpu;
        Codec = codec;
        Width = width;
        Height = height;
        Fps = Math.Max(1, fps);
        _frameDuration = 10_000_000L / Fps;

        _transform = MfCodecs.CreateHardwareEncoder(codec, out var name)
            ?? throw new NotSupportedException("No hardware encoder for " + codec);
        Name = name;

        var attributes = _transform.Attributes;
        if (attributes is not null)
        {
            SafeTry.Run(() => attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u));
            SafeTry.Run(() => attributes.Set(MfLowLatency, 1u));
        }

        _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)_gpu.DeviceManager.NativePointer);

        // Rate control must be in place before the output type is negotiated: several vendors read
        // it at that moment and ignore later changes (Intel most of all).
        _codecApi = new CodecApiSetter(_transform.NativePointer);
        ApplyLowLatencyProfile(bitrateKbps);

        // Output before input: hardware encoders derive the input constraints from the output type.
        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, MfCodecs.Subtype(codec));
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)(bitrateKbps * 1000L));
            output.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            output.Set(MediaTypeAttributeKeys.FrameRate, Pack(Fps, 1));
            output.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            output.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            output.Set(MfMtMpeg2Profile, codec == VideoCodec.H264 ? 100u : 1u);
            _transform.SetOutputType(0, output, 0);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            input.Set(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            input.Set(MediaTypeAttributeKeys.FrameRate, Pack(Fps, 1));
            input.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            input.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            _transform.SetInputType(0, input, 0);
        }

        // Re-apply after negotiation for the vendors that reset on SetOutputType.
        ApplyLowLatencyProfile(bitrateKbps);

        var streamInfo = _transform.GetOutputStreamInfo(0);
        _providesSamples = (streamInfo.Flags & (int)(OutputStreamInfoFlags.OutputStreamProvidesSamples | OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        _outputSize = Math.Max(streamInfo.Size, width * height);

        _events = _transform.QueryInterface<IMFMediaEventGenerator>();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        _eventThread = new Thread(EventLoop) { Name = "Beamcast MF encoder", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _eventThread.Start();
    }

    public VideoCodec Codec { get; }
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public string Name { get; }

    /// <summary>Which ICodecAPI properties the driver accepted (HRESULT per property).</summary>
    public IReadOnlyDictionary<Guid, int> CodecApiResults => _codecApi.Results;

    /// <summary>Raised on the encoder thread for every compressed frame, in order.</summary>
    public event Action<EncodedFrame, double>? FrameEncoded;

    public event Action<Exception>? Faulted;

    /// <summary>True while the encoder has asked for input and nothing has been submitted yet.</summary>
    public bool WantsInput => Volatile.Read(ref _needInput) > 0;

    public void RequestKeyframe()
    {
        Diag.Log($"encoder: keyframe requested (sinceReq={Volatile.Read(ref _framesSinceKeyframeRequest)})");
        Interlocked.Exchange(ref _forceKeyframe, 1);
        if (Volatile.Read(ref _framesSinceKeyframeRequest) < 0)
            Interlocked.Exchange(ref _framesSinceKeyframeRequest, 0);
    }

    /// <summary>
    /// True when a keyframe was requested and the driver has produced ten more frames without
    /// one. Some encoders ignore the force-keyframe property; the caller then recreates the
    /// encoder, which always starts with an IDR frame.
    /// </summary>
    public bool KeyframeOverdue => Volatile.Read(ref _framesSinceKeyframeRequest) > 10;

    public void SetBitrate(int kbps)
    {
        _codecApi.SetUInt32(CodecApiGuids.AVEncCommonMeanBitRate, (uint)(kbps * 1000L));
        _codecApi.SetUInt32(CodecApiGuids.AVEncCommonMaxBitRate, (uint)(kbps * 1000L));
    }

    /// <summary>
    /// Hands one NV12 texture to the encoder if it is ready for it. Returns false when the encoder is
    /// still busy with the previous frame, in which case the caller simply drops this one: with a
    /// live source the next capture is a better frame than a stale queued one.
    /// </summary>
    public bool TrySubmit(ID3D11Texture2D nv12, long timestampMs)
    {
        if (_disposed)
            return false;

        lock (_inputLock)
        {
            if (Volatile.Read(ref _needInput) <= 0)
                return false;

            var forceKey = Interlocked.Exchange(ref _forceKeyframe, 0) == 1;
            if (forceKey)
                _codecApi.SetUInt32(CodecApiGuids.AVEncVideoForceKeyFrame, 1);

            using var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(Texture2DIid, nv12, 0, false);
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            if (forceKey)
            {
                // Belt and braces: the per-sample picture type request (IDR = 1) for drivers that
                // read it instead of the ICodecAPI property.
                SafeTry.Run(() => sample.Set(SampleAttributeKeys.VideoEncodePictureType, 1u));
            }
            sample.SampleTime = timestampMs * 10_000;
            sample.SampleDuration = _frameDuration;
            _submittedTicks = Stopwatch.GetTimestamp();
            _transform.ProcessInput(0, sample, 0);
            Interlocked.Decrement(ref _needInput);
            return true;
        }
    }

    private void ApplyLowLatencyProfile(int bitrateKbps)
    {
        if (!_codecApi.IsAvailable)
            return;

        var bits = (uint)(bitrateKbps * 1000L);
        _codecApi.SetBool(CodecApiGuids.AVLowLatencyMode, true);
        _codecApi.SetUInt32(CodecApiGuids.AVEncCommonLowLatency, 1);
        _codecApi.SetUInt32(CodecApiGuids.AVEncCommonRateControlMode, CodecApiGuids.RateControlCbr);
        _codecApi.SetUInt32(CodecApiGuids.AVEncCommonMeanBitRate, bits);
        _codecApi.SetUInt32(CodecApiGuids.AVEncCommonMaxBitRate, bits);
        // One frame worth of VBV: the encoder cannot bank bits, so every frame stays small and quick to send.
        _codecApi.SetUInt32(CodecApiGuids.AVEncCommonBufferSize, Math.Max(bits / (uint)Fps, 64_000u));
        _codecApi.SetUInt32(CodecApiGuids.AVEncMPVDefaultBPictureCount, 0);
        _codecApi.SetUInt32(CodecApiGuids.AVEncVideoMaxNumRefFrame, 1);
        _codecApi.SetUInt32(CodecApiGuids.AVEncCommonQualityVsSpeed, 0);
        if (!_codecApi.SetUInt32(CodecApiGuids.AVEncMPVGOPSize, uint.MaxValue))
            _codecApi.SetUInt32(CodecApiGuids.AVEncMPVGOPSize, (uint)(Fps * 600));
        if (Codec == VideoCodec.H264)
            _codecApi.SetBool(CodecApiGuids.AVEncH264CABACEnable, true);
    }

    private void EventLoop()
    {
        try
        {
            while (!_disposed)
            {
                using var mediaEvent = _events.GetEvent(0);
                var type = (int)mediaEvent.EventType;
                if (type == EventNeedInput)
                {
                    Interlocked.Increment(ref _needInput);
                }
                else if (type == EventHaveOutput)
                {
                    DrainOutput();
                }
            }
        }
        catch (Exception ex)
        {
            if (!_disposed)
                Faulted?.Invoke(ex);
        }
    }

    private void DrainOutput()
    {
        var buffer = new OutputDataBuffer { StreamID = 0 };
        IMFSample? ownSample = null;
        if (!_providesSamples)
        {
            ownSample = MediaFactory.MFCreateSample();
            ownSample.AddBuffer(MediaFactory.MFCreateMemoryBuffer(_outputSize));
            buffer.Sample = ownSample;
        }

        try
        {
            var result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffer, out _);
            if (result.Failure)
                return;

            var sample = buffer.Sample;
            if (sample is null)
                return;

            try
            {
                var encodeMs = Stopwatch.GetElapsedTime(_submittedTicks).TotalMilliseconds;
                var data = CopyBitstream(sample);
                if (data.Length == 0)
                    return;
                var keyframe = sample.GetUInt32(SampleAttributeKeys.CleanPoint, out var clean).Success && clean != 0;
                if (keyframe)
                    Interlocked.Exchange(ref _framesSinceKeyframeRequest, -1);
                else if (Volatile.Read(ref _framesSinceKeyframeRequest) >= 0)
                    Interlocked.Increment(ref _framesSinceKeyframeRequest);
                var timestampMs = sample.SampleTime / 10_000;
                var frame = new EncodedFrame(data, keyframe, Width, Height, timestampMs, ++_sequence);
                if (_sequence <= 3 || keyframe)
                    Diag.Log($"encoder: out #{_sequence} key={keyframe} {data.Length} B sinceReq={Volatile.Read(ref _framesSinceKeyframeRequest)}");
                FrameEncoded?.Invoke(frame, encodeMs);
            }
            finally
            {
                if (!ReferenceEquals(sample, ownSample))
                    sample.Dispose();
            }
        }
        finally
        {
            buffer.Events?.Dispose();
            ownSample?.Dispose();
        }
    }

    private static unsafe byte[] CopyBitstream(IMFSample sample)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var pointer, out _, out var length);
        try
        {
            var data = new byte[length];
            new ReadOnlySpan<byte>((void*)pointer, length).CopyTo(data);
            return data;
        }
        finally
        {
            contiguous.Unlock();
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        SafeTry.Run(() => _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero));
        SafeTry.Run(() => _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero));
        SafeTry.Run(() => MediaFactory.MFShutdownObject(_transform));
        _eventThread.Join(TimeSpan.FromSeconds(2));
        SafeTry.Run(() => _events.Dispose());
        SafeTry.Run(() => _transform.Dispose());
    }
}
