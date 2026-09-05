namespace Beamcast.Audio;

/// <summary>
/// Sums several capture sources into fixed 20 ms frames. Each source has its own ring; a source
/// only contributes once it has buffered a little (so a late packet does not tear the sound) and
/// never more than <see cref="MaxBufferedFrames"/> (so latency stays bounded). Pure logic.
/// </summary>
public sealed class AudioMixer
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSamples = SampleRate / 50 * Channels;

    /// <summary>Samples a source must hold before it starts contributing (20 ms).</summary>
    public const int PrimeSamples = FrameSamples;

    /// <summary>Upper bound on buffered audio per source: 100 ms.</summary>
    public const int MaxBufferedFrames = 5;

    private readonly Dictionary<int, Ring> _sources = new();
    private readonly object _sync = new();

    public int SourceCount
    {
        get
        {
            lock (_sync)
                return _sources.Count;
        }
    }

    public void AddSource(int id)
    {
        lock (_sync)
            _sources[id] = new Ring(FrameSamples * (MaxBufferedFrames + 1));
    }

    public void RemoveSource(int id)
    {
        lock (_sync)
            _sources.Remove(id);
    }

    public void Write(int id, ReadOnlySpan<float> samples)
    {
        lock (_sync)
        {
            if (_sources.TryGetValue(id, out var ring))
                ring.Write(samples);
        }
    }

    /// <summary>
    /// Fills <paramref name="frame"/> (<see cref="FrameSamples"/> floats) with the sum of every
    /// primed source. Returns false when nothing contributed (the frame is silence).
    /// </summary>
    public bool Mix(Span<float> frame)
    {
        frame.Clear();
        var any = false;
        lock (_sync)
        {
            foreach (var ring in _sources.Values)
            {
                if (!ring.Primed)
                {
                    if (ring.Available >= PrimeSamples)
                        ring.Primed = true;
                    else
                        continue;
                }

                var read = ring.ReadInto(frame);
                if (read < frame.Length)
                    ring.Primed = false;
                if (read > 0)
                    any = true;
            }
        }

        if (any)
        {
            for (var i = 0; i < frame.Length; i++)
                frame[i] = Math.Clamp(frame[i], -1f, 1f);
        }
        return any;
    }

    /// <summary>Ring of floats that adds into a destination and drops the oldest audio when full.</summary>
    private sealed class Ring
    {
        private readonly float[] _buffer;
        private int _head;
        private int _count;

        public Ring(int capacity)
        {
            _buffer = new float[capacity];
        }

        public bool Primed;

        public int Available => _count;

        public void Write(ReadOnlySpan<float> samples)
        {
            foreach (var sample in samples)
            {
                if (_count == _buffer.Length)
                {
                    _head = (_head + 1) % _buffer.Length;
                    _count--;
                }
                _buffer[(_head + _count) % _buffer.Length] = sample;
                _count++;
            }
        }

        public int ReadInto(Span<float> destination)
        {
            var n = Math.Min(destination.Length, _count);
            for (var i = 0; i < n; i++)
            {
                destination[i] += _buffer[_head];
                _head = (_head + 1) % _buffer.Length;
            }
            _count -= n;
            return n;
        }
    }
}
