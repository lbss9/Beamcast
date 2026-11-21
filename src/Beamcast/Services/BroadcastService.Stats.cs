using System.Buffers;
using System.Diagnostics;
using Beamcast.Audio;
using Beamcast.Capture;
using Beamcast.Codec;
using Beamcast.Codec.Gpu;
using Beamcast.Net;
using Beamcast.Render;
using Microsoft.UI.Dispatching;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Beamcast.Services;

/// <summary>The once-a-second numbers shown while live.</summary>
public sealed partial class BroadcastService
{
    private void ResetStats()
    {
        _statsWindowStart = Stopwatch.GetTimestamp();
        _statsFrames = 0;
        _statsBytes = 0;
        _statsEncodeMs = 0;
        Interlocked.Exchange(ref _statsAudioBytes, 0);
    }

    private void AccountFrame(EncodedFrame frame, double encodeMs)
    {
        _statsFrames++;
        _statsBytes += frame.Data.Length;
        _statsEncodeMs += encodeMs;
        var elapsed = Stopwatch.GetElapsedTime(_statsWindowStart);
        if (elapsed.TotalMilliseconds < 1000)
            return;

        AdaptTick();
        var seconds = elapsed.TotalSeconds;
        var audioBytes = Interlocked.Exchange(ref _statsAudioBytes, 0);
        var worstDelay = Interlocked.Exchange(ref _worstViewerDelayMs, 0);
        var stats = new HostStats(
            _statsFrames / seconds,
            _statsBytes * 8 / 1000.0 / seconds,
            audioBytes * 8 / 1000.0 / seconds,
            _statsFrames > 0 ? _statsEncodeMs / _statsFrames : 0,
            frame.Width,
            frame.Height,
            ActiveCodec.ToWireName().ToUpperInvariant(),
            ViewerCount,
            EffectiveBitrateKbps,
            _adaptiveEnabled && _adaptive.IsAdapted,
            false,
            worstDelay
        );
        LastStats = stats;
        _statsWindowStart = Stopwatch.GetTimestamp();
        _statsFrames = 0;
        _statsBytes = 0;
        _statsEncodeMs = 0;
        if (Environment.TickCount64 - _lastStatsLogTicks >= 5000)
        {
            _lastStatsLogTicks = Environment.TickCount64;
            Diag.Log($"broadcast: stats {stats.Codec} {stats.Width}x{stats.Height} {stats.Fps:F1} fps {stats.Kbps:F0} kbps enc {stats.EncodeMs:F1} ms audio {stats.AudioKbps:F0} kbps viewers {stats.Viewers} target {stats.TargetKbps} adapted {stats.Adapted} worst viewer {stats.WorstViewerDelayMs} ms pending {_lounge.PendingVideo(_streamId)}");
        }
        Post(() => StatsChanged?.Invoke(stats));
    }
}
