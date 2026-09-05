using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace Beamcast.Capture;

/// <summary>
/// Windows draws a coloured border around anything being captured. Setting
/// <see cref="GraphicsCaptureSession.IsBorderRequired"/> to false only takes effect after the app
/// has asked for borderless access once per process; without that call Windows silently keeps
/// the border (Windows 10 build 20348 / Windows 11 and later; older builds cannot remove it).
/// </summary>
public static class CaptureAccess
{
    private static readonly object Sync = new();
    private static Task<bool>? _borderless;

    public static bool IsBorderlessSupported =>
        ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess")
        && ApiInformation.IsEnumNamedValuePresent("Windows.Graphics.Capture.GraphicsCaptureAccessKind", "Borderless");

    /// <summary>Asks once (cached) for permission to capture without the border. Never throws.</summary>
    public static Task<bool> EnsureBorderlessAsync()
    {
        lock (Sync)
            return _borderless ??= RequestAsync();
    }

    /// <summary>True when the request already completed successfully.</summary>
    public static bool BorderlessGranted => _borderless is { IsCompletedSuccessfully: true, Result: true };

    private static async Task<bool> RequestAsync()
    {
        if (!IsBorderlessSupported)
        {
            Diag.Log("capture: borderless capture not supported on this Windows build");
            return false;
        }
        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            Diag.Log($"capture: borderless access {status}");
            return status == AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Diag.Log("capture: borderless access request failed: " + ex.Message);
            return false;
        }
    }
}
