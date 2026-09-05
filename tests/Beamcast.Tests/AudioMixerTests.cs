using Beamcast.Audio;
using Xunit;

namespace Beamcast.Tests;

public class AudioMixerTests
{
    [Fact]
    public void SilentUntilASourceIsPrimed()
    {
        var mixer = new AudioMixer();
        mixer.AddSource(1);
        var frame = new float[AudioMixer.FrameSamples];
        Assert.False(mixer.Mix(frame));

        mixer.Write(1, new float[AudioMixer.PrimeSamples / 2]);
        Assert.False(mixer.Mix(frame));

        var half = new float[AudioMixer.PrimeSamples];
        Array.Fill(half, 0.5f);
        mixer.Write(1, half);
        Assert.True(mixer.Mix(frame));
    }

    [Fact]
    public void SourcesAreSummedAndClamped()
    {
        var mixer = new AudioMixer();
        mixer.AddSource(1);
        mixer.AddSource(2);
        var a = new float[AudioMixer.FrameSamples];
        var b = new float[AudioMixer.FrameSamples];
        Array.Fill(a, 0.75f);
        Array.Fill(b, 0.75f);
        mixer.Write(1, a);
        mixer.Write(2, b);

        var frame = new float[AudioMixer.FrameSamples];
        Assert.True(mixer.Mix(frame));
        Assert.All(frame, s => Assert.Equal(1f, s));
    }

    [Fact]
    public void DropsOldestWhenASourceFallsBehind()
    {
        var mixer = new AudioMixer();
        mixer.AddSource(1);
        var burst = new float[AudioMixer.FrameSamples * (AudioMixer.MaxBufferedFrames + 3)];
        for (var i = 0; i < burst.Length; i++)
            burst[i] = i < AudioMixer.FrameSamples * 2 ? 1f : 0.25f;
        mixer.Write(1, burst);

        var frame = new float[AudioMixer.FrameSamples];
        Assert.True(mixer.Mix(frame));
        Assert.All(frame, s => Assert.Equal(0.25f, s));
    }

    [Fact]
    public void RemovedSourceStopsContributing()
    {
        var mixer = new AudioMixer();
        mixer.AddSource(1);
        var a = new float[AudioMixer.FrameSamples * 2];
        Array.Fill(a, 0.5f);
        mixer.Write(1, a);
        mixer.RemoveSource(1);
        Assert.Equal(0, mixer.SourceCount);
        Assert.False(mixer.Mix(new float[AudioMixer.FrameSamples]));
    }
}
