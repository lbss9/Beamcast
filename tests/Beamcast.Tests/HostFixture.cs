using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Beamcast.Tests;

/// <summary>
/// A real Beamcast host for the tests: the server built from this repository, started as its own
/// process on 127.0.0.1 with a free port and a throwaway data folder, and killed afterwards.
/// Nothing leaves the machine and no real host address is involved.
/// </summary>
public sealed class HostFixture : IAsyncLifetime
{
    private Process? _process;
    private string? _dataDir;
    private readonly List<string> _output = [];

    public string Url { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        var dll = FindServerDll();
        var port = FreePort();
        _dataDir = Path.Combine(Path.GetTempPath(), "beamcast-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);

        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add($"http://127.0.0.1:{port}");
        start.Environment["BEAMCAST_DATA"] = _dataDir;
        start.Environment["BEAMCAST_HOST_NAME"] = "test-host";
        start.Environment.Remove("BEAMCAST_APP_KEY");

        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the host.");
        _process.OutputDataReceived += (_, e) => Keep(e.Data);
        _process.ErrorDataReceived += (_, e) => Keep(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Url = $"ws://127.0.0.1:{port}/ws";
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                break;
            try
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(150);
        }
        throw new InvalidOperationException("The host did not come up. Output:\n" + Output());
    }

    public Task DisposeAsync()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (Exception) { }
        _process?.Dispose();
        try
        {
            if (_dataDir is not null && Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (Exception) { }
        return Task.CompletedTask;
    }

    private void Keep(string? line)
    {
        if (line is null)
            return;
        lock (_output)
            _output.Add(line);
    }

    private string Output()
    {
        lock (_output)
            return string.Join('\n', _output);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>src/Beamcast.Server/bin/&lt;same configuration as the tests&gt;/net8.0/Beamcast.Server.dll.</summary>
    private static string FindServerDll()
    {
        var bin = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var framework = bin.Name;
        var configuration = bin.Parent?.Name ?? "Debug";
        var root = bin;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Beamcast.sln")))
            root = root.Parent;
        if (root is null)
            throw new InvalidOperationException("Repository root not found from " + AppContext.BaseDirectory);
        var dll = Path.Combine(root.FullName, "src", "Beamcast.Server", "bin", configuration, framework, "Beamcast.Server.dll");
        return File.Exists(dll) ? dll : throw new FileNotFoundException("Build src/Beamcast.Server first.", dll);
    }
}
