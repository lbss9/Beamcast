using System.Runtime.InteropServices;
using Beamcast.Net;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Beamcast.Audio;

/// <summary>
/// Decodes Opus packets and plays them through WASAPI shared mode with a small jitter buffer.
/// Packets are pushed from the network thread; the device is opened lazily on the first one.
/// Lost packets are concealed by the decoder; a growing buffer is trimmed so latency never creeps.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private static readonly TimeSpan MaxBuffered = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan TrimTo = TimeSpan.FromMilliseconds(60);

    private readonly object _sync = new();
    private OpusAudioDecoder? _decoder;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private readonly float[] _pcm = new float[AudioMixer.FrameSamples];
    private readonly byte[] _bytes = new byte[AudioMixer.FrameSamples * 4];
    private uint _lastSequence;
    private float _volume = 1f;
    private bool _disposed;

    public float Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsMuted { get; set; }

    public bool IsActive => _output is not null;

    public void Push(AudioPacketHeader header, ReadOnlySpan<byte> opus)
    {
        if (_disposed)
            return;

        lock (_sync)
        {
            if (_output is null)
                Open();
            if (_buffer is null || _decoder is null)
                return;

            if (_buffer.BufferedDuration > MaxBuffered)
            {
                _buffer.ClearBuffer();
            }

            // Conceal small gaps so a lost packet does not click.
            if (_lastSequence != 0 && header.Sequence > _lastSequence + 1)
            {
                var missing = Math.Min(3, (int)(header.Sequence - _lastSequence - 1));
                for (var i = 0; i < missing; i++)
                    Emit(_decoder.Decode(ReadOnlySpan<byte>.Empty, _pcm));
            }
            _lastSequence = header.Sequence;

            var samples = _decoder.Decode(opus, _pcm);
            Emit(samples);
        }
    }

    private void Emit(int samples)
    {
        if (samples <= 0 || _buffer is null)
            return;
        var gain = IsMuted ? 0f : _volume;
        if (gain != 1f)
        {
            for (var i = 0; i < samples; i++)
                _pcm[i] *= gain;
        }
        MemoryMarshal.AsBytes(_pcm.AsSpan(0, samples)).CopyTo(_bytes);
        _buffer.AddSamples(_bytes, 0, samples * 4);
    }

    private void Open()
    {
        try
        {
            _decoder = new OpusAudioDecoder();
            _buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(AudioMixer.SampleRate, AudioMixer.Channels))
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            _output = new WasapiOut(AudioClientShareMode.Shared, true, 30);
            _output.Init(_buffer);
            _output.Play();
        }
        catch (Exception)
        {
            _output?.Dispose();
            _output = null;
            _buffer = null;
            _decoder = null;
            _disposed = true;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            SafeTry.Run(() => _output?.Stop());
            SafeTry.Run(() => _output?.Dispose());
            _output = null;
            _buffer = null;
            _decoder?.Dispose();
            _decoder = null;
        }
    }
}
