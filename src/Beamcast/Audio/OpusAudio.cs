using Concentus;
using Concentus.Enums;

namespace Beamcast.Audio;

/// <summary>Opus at 48 kHz stereo, 20 ms frames, tuned for low delay.</summary>
public sealed class OpusAudioEncoder : IDisposable
{
    private readonly IOpusEncoder _encoder;
    private readonly byte[] _scratch = new byte[4000];

    public OpusAudioEncoder(int bitrateBps = 128_000)
    {
        _encoder = OpusCodecFactory.CreateEncoder(AudioMixer.SampleRate, AudioMixer.Channels, OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);
        _encoder.Bitrate = bitrateBps;
        _encoder.Complexity = 5;
        _encoder.UseVBR = true;
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
    }

    /// <summary>Encodes one 20 ms interleaved frame; returns the packet bytes.</summary>
    public byte[] Encode(ReadOnlySpan<float> frame)
    {
        var length = _encoder.Encode(frame, AudioMixer.FrameSamples / AudioMixer.Channels, _scratch, _scratch.Length);
        return length <= 0 ? [] : _scratch.AsSpan(0, length).ToArray();
    }

    public void Dispose() { }
}

public sealed class OpusAudioDecoder : IDisposable
{
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(AudioMixer.SampleRate, AudioMixer.Channels);

    /// <summary>Decodes one packet into interleaved floats; an empty packet asks for loss concealment.</summary>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> output)
    {
        var frames = _decoder.Decode(packet, output, AudioMixer.FrameSamples / AudioMixer.Channels, false);
        return frames <= 0 ? 0 : frames * AudioMixer.Channels;
    }

    public void Dispose() { }
}
