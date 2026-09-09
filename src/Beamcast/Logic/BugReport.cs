using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Vortice.DXGI;

namespace Beamcast;

/// <summary>
/// Bundles what a bug report needs into one zip on the Desktop: diag.log, crash.log, a machine
/// summary and the settings with every secret (app keys, room passwords, owner tokens) blanked.
/// </summary>
public static class BugReport
{
    public static string Create()
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var path = Path.Combine(folder, $"Beamcast-report-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        Diag.Log("report: building " + path);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            AddFile(zip, Diag.LogPath, "diag.log");
            AddFile(zip, Diag.CrashLogPath, "crash.log");
            AddText(zip, "info.txt", BuildInfo());
            AddText(zip, "settings.json", SanitizedSettings());
        }
        return path;
    }

    private static void AddFile(ZipArchive zip, string path, string name)
    {
        if (!File.Exists(path))
            return;
        try
        {
            // The diag log is open for append by this very process: share the read.
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using var target = entry.Open();
            source.CopyTo(target);
        }
        catch (Exception ex)
        {
            AddText(zip, name + ".error.txt", ex.ToString());
        }
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string BuildInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Beamcast {AppInfo.Version} (installed: {UpdateService.InstalledVersion ?? "no, running from a folder"})");
        sb.AppendLine($"generated {DateTimeOffset.Now:O}");
        sb.AppendLine($"OS {Environment.OSVersion.VersionString}, {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}, .NET {Environment.Version}");
        sb.AppendLine($"CPU cores {Environment.ProcessorCount}, process uptime {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds:F0} s");
        sb.AppendLine($"diag enabled: {Diag.IsEnabled}");
        sb.AppendLine("GPU adapters:");
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success && adapter is not null; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    sb.AppendLine($"  [{i}] {d.Description} (vendor 0x{d.VendorId:X4}, {d.DedicatedVideoMemory / 1024 / 1024} MB{((d.Flags & AdapterFlags.Software) != 0 ? ", software" : "")})");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("  (could not enumerate: " + ex.Message + ")");
        }
        sb.AppendLine("Monitors:");
        foreach (var monitor in SafeTry.Run(Capture.CaptureSourceEnumerator.Monitors) ?? [])
            sb.AppendLine($"  {monitor.Subtitle} {monitor.Width}x{monitor.Height}{(monitor.IsPrimary ? " (primary)" : "")}");
        sb.AppendLine($"hardware H.264 encoder: {SafeTry.Run(() => Codec.Gpu.MfCodecs.HasHardwareEncoder(Codec.VideoCodec.H264))}, HEVC: {SafeTry.Run(() => Codec.Gpu.MfCodecs.HasHardwareEncoder(Codec.VideoCodec.Hevc))}");
        return sb.ToString();
    }

    /// <summary>settings.json with every DPAPI blob replaced, so the file can be shared.</summary>
    private static string SanitizedSettings()
    {
        try
        {
            if (!File.Exists(SettingsStore.FilePath))
                return "(no settings file)";
            var node = JsonNode.Parse(File.ReadAllText(SettingsStore.FilePath));
            if (node is null)
                return "(unreadable)";
            Scrub(node);
            return node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex)
        {
            return "(could not read settings: " + ex.Message + ")";
        }
    }

    private static readonly string[] SecretKeys = ["protectedAppKey", "appKey", "protectedPassword", "protectedToken", "relayAppKey"];

    private static void Scrub(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (SecretKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        var value = obj[key]?.ToString();
                        // No angle brackets: System.Text.Json would escape them to < in the output.
                        obj[key] = string.IsNullOrEmpty(value) ? "" : "[protected]";
                    }
                    else if (obj[key] is { } child)
                        Scrub(child);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                        Scrub(item);
                }
                break;
        }
    }
}
