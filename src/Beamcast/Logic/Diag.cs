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
    private static volatile bool Enabled =
        Environment.GetEnvironmentVariable("BEAMCAST_DIAG") == "1"
        || File.Exists(SwitchPath);

    public static bool IsEnabled => Enabled;

    public static string LogPath => Path.Combine(Directory, "diag.log");
    public static string SwitchPath => Path.Combine(Directory, "diag.on");

    /// <summary>Turns the log on or off for this run and the next ones (creates or deletes diag.on).</summary>
    public static void SetEnabled(bool on)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (on)
                File.WriteAllText(SwitchPath, string.Empty);
            else if (File.Exists(SwitchPath))
                File.Delete(SwitchPath);
        }
        catch { }
        if (on && !Enabled)
        {
            Enabled = true;
            Log("diag: enabled from Settings");
        }
        else if (!on && Enabled)
        {
            Log("diag: disabled from Settings");
            Enabled = false;
        }
    }

    public static string CrashLogPath => Path.Combine(Directory, "crash.log");

    /// <summary>
    /// Writes a crash (or a swallowed fault) to crash.log, always, and to diag.log when enabled.
    /// crash.log keeps the last few reports, newest first, so a second crash does not erase the first.
    /// </summary>
    public static void RecordCrash(string source, Exception? exception, string? note = null)
    {
        var report =
            $"==== {DateTime.UtcNow:O}  Beamcast {AppInfo.Version}  {source}{(string.IsNullOrEmpty(note) ? "" : "  (" + note + ")")}\n" +
            (exception is null ? "(no exception object)\n" : exception.ToString() + "\n") +
            $"thread {Environment.CurrentManagedThreadId}, uptime {Clock.Elapsed.TotalSeconds:F1} s\n\n";
        Log("CRASH " + source + ": " + (exception?.GetType().Name ?? "?") + ": " + (exception?.Message ?? note));
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var previous = File.Exists(CrashLogPath) ? File.ReadAllText(CrashLogPath) : string.Empty;
            if (previous.Length > 64 * 1024)
                previous = previous[..(64 * 1024)];
            File.WriteAllText(CrashLogPath, report + previous);
        }
        catch { }
    }

    /// <summary>At startup: if the previous run left a crash report, mention it in the diag so the two files line up.</summary>
    public static void NoteCrashLogAtStartup()
    {
        try
        {
            if (File.Exists(CrashLogPath))
                Log($"startup: crash.log present ({new FileInfo(CrashLogPath).Length} B, last write {File.GetLastWriteTimeUtc(CrashLogPath):O})");
        }
        catch { }
    }

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
