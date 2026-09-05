using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Beamcast.Capture;

#pragma warning disable CA1416 // Guarded by ApiInformation at runtime.

/// <summary>
/// Windows draws a coloured border around anything being captured. Setting
/// <see cref="GraphicsCaptureSession.IsBorderRequired"/> to false only takes effect after the process
/// obtained borderless access through <see cref="GraphicsCaptureAccess.RequestAccessAsync"/> (this is
/// what OBS does too); without that call Windows silently keeps the border. Available on Windows 10
/// build 20348 / Windows 11 and later; older builds cannot remove it. Unpackaged apps are granted
/// access without any prompt.
/// </summary>
public static class CaptureAccess
{
    private static readonly object Sync = new();
    private static bool? _granted;

    public static bool IsBorderlessSupported =>
        ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess")
        && ApiInformation.IsEnumNamedValuePresent("Windows.Graphics.Capture.GraphicsCaptureAccessKind", "Borderless");

    /// <summary>True once Windows granted borderless capture to this process.</summary>
    public static bool BorderlessGranted => _granted == true;

    /// <summary>
    /// Asks Windows for borderless capture (cached after the first success). Safe to call from any
    /// thread, including the UI thread: the request completes on a thread-pool thread and normally
    /// takes a few milliseconds. Never throws.
    /// </summary>
    public static bool EnsureBorderless(TimeSpan? timeout = null)
    {
        lock (Sync)
        {
            if (_granted == true)
                return true;
            if (!IsBorderlessSupported)
            {
                if (_granted is null)
                    Diag.Log("capture: borderless capture not supported on this Windows build");
                _granted = false;
                return false;
            }
            try
            {
                var task = GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless).AsTask();
                if (!task.Wait(timeout ?? TimeSpan.FromSeconds(3)))
                {
                    Diag.Log("capture: borderless access request timed out");
                    return false;
                }
                _granted = task.Result == AppCapabilityAccessStatus.Allowed;
                Diag.Log($"capture: borderless access {task.Result}");
                return _granted == true;
            }
            catch (Exception ex)
            {
                Diag.Log("capture: borderless access request failed: " + ex.Message);
                _granted = false;
                return false;
            }
        }
    }

    /// <summary>Fire-and-forget warm-up at launch so the first capture already runs without the border.</summary>
    public static Task EnsureBorderlessAsync() => Task.Run(() => EnsureBorderless());
}
