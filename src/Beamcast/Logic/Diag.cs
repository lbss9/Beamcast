using System.Diagnostics;

namespace Beamcast;

/// <summary>
/// Lightweight trace to %LOCALAPPDATA%\Beamcast\diag.log. Off unless the file "diag.on" exists in
/// that folder or BEAMCAST_DIAG=1, so a normal run never touches the disk from the hot paths.
/// </summary>
public static class Diag
{
    private static readonly object Sync = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly string Directory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Beamcast"
    );
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("BEAMCAST_DIAG") == "1"
        || File.Exists(Path.Combine(Directory, "diag.on"));

    public static bool IsEnabled => Enabled;

    public static void Log(string message)
    {
        if (!Enabled)
            return;
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(
                    Path.Combine(Directory, "diag.log"),
                    $"{Clock.Elapsed.TotalSeconds,9:F3} [{Environment.CurrentManagedThreadId,3}] {message}\n"
                );
            }
        }
        catch { }
    }
}
