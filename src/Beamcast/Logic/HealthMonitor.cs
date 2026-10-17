using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Vortice.DXGI;

namespace Beamcast;

/// <summary>
/// Watches the app and the machine from its own thread, so the log still says something when the
/// window freezes, the graphics driver resets or the laptop throttles:
/// <list type="bullet">
/// <item>UI hangs: the UI thread is pinged twice a second; an answer later than 2 s is a hang,
/// logged when it starts and when it ends (and in crash.log from 5 s on).</item>
/// <item>GPU loss: every pipeline device is asked for its removed reason (TDR, driver crash, update).</item>
/// <item>Load, every 10 s with diag on: CPU of the app and of the machine, clock against the maximum
/// (thermal or power throttling), memory, GPU engines (3D, encode, decode) of the app and in total,
/// video memory against the budget, and power (battery, battery saver).</item>
/// <item>The previous run: a marker that only a clean exit removes tells a native crash or a kill
/// apart, and Windows' own reports (application error, application hang, display driver reset) from
/// the last days are copied in.</item>
/// </list>
/// </summary>
public static class HealthMonitor
{
    private static readonly TimeSpan PingInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HangThreshold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HangIncident = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LoadInterval = TimeSpan.FromSeconds(10);

    private static readonly List<WeakReference<Codec.Gpu.GpuDevice>> Devices = [];
    private static readonly HashSet<string> ReportedRemovals = [];
    private static DispatcherQueue? _ui;
    private static Func<string>? _state;
    private static Thread? _thread;
    private static long _pingSent;
    private static long _pingAnswered;

    private static string MarkerPath => Path.Combine(Path.GetDirectoryName(Diag.LogPath)!, "running.marker");

    public static void Start(DispatcherQueue ui, Func<string> state)
    {
        if (_thread is not null)
            return;
        _ui = ui;
        _state = state;
        CheckPreviousRun();
        _thread = new Thread(Run) { Name = "Beamcast health", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SafeTry.Run(() => File.Delete(MarkerPath));
    }

    /// <summary>Every pipeline device registers here so a removal (driver reset) is noticed and logged.</summary>
    public static void Track(Codec.Gpu.GpuDevice device)
    {
        lock (Devices)
        {
            Devices.RemoveAll(w => !w.TryGetTarget(out _));
            Devices.Add(new WeakReference<Codec.Gpu.GpuDevice>(device));
        }
    }

    /// <summary>One line for the log: what this machine has, at startup.</summary>
    public static void LogMachine()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success && adapter is not null; i++)
            {
                using (adapter)
                {
                    var d = adapter.Description1;
                    if ((d.Flags & AdapterFlags.Software) != 0)
                        continue;
                    Diag.Log($"startup: GPU [{i}] {d.Description}, vendor 0x{d.VendorId:X4} device 0x{d.DeviceId:X4}, driver {DriverVersion(adapter)}, {d.DedicatedVideoMemory / 1024 / 1024} MB dedicated, {d.SharedSystemMemory / 1024 / 1024} MB shared");
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Log("startup: GPU list failed: " + ex.Message);
        }
        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref memory))
            Diag.Log($"startup: RAM {memory.TotalPhys / 1024 / 1024} MB, {memory.AvailPhys / 1024 / 1024} MB free; power {PowerText()}");
    }

    public static string DriverVersion(IDXGIAdapter adapter)
    {
        try
        {
            // The UMD version is what CheckInterfaceSupport hands back for IDXGIDevice.
            if (adapter.CheckInterfaceSupport(typeof(IDXGIDevice), out var umd))
                return $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";
        }
        catch { }
        return "?";
    }

    // ----- previous run -----

    private static void CheckPreviousRun()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            if (File.Exists(MarkerPath))
            {
                var previous = File.ReadAllText(MarkerPath).Trim();
                var note = $"the previous run ({previous}) did not close normally: native crash, freeze ended from Task Manager, power loss or a Windows restart";
                Diag.Log("startup: " + note);
                Diag.RecordIncident("previous run", note);
            }
            File.WriteAllText(MarkerPath, $"pid {Environment.ProcessId}, Beamcast {AppInfo.Version}, started {DateTimeOffset.Now:O}");
        }
        catch { }

        // Windows' own records take a moment to query; never on the startup path.
        _ = Task.Run(() =>
        {
            foreach (var line in RecentWindowsEvents(TimeSpan.FromDays(3)))
                Diag.Log("startup: windows event: " + line);
        });
    }

    /// <summary>
    /// Application errors and hangs of Beamcast, and display driver resets (TDR), as Windows logged
    /// them. Used at startup and in the bug report.
    /// </summary>
    public static IReadOnlyList<string> RecentWindowsEvents(TimeSpan within)
    {
        var ms = (long)within.TotalMilliseconds;
        var lines = new List<string>();
        lines.AddRange(QueryEvents("Application", $"*[System[(EventID=1000 or EventID=1002) and TimeCreated[timediff(@SystemTime) <= {ms}]]]", "Beamcast"));
        lines.AddRange(QueryEvents("System", $"*[System[Provider[@Name='Display'] and EventID=4101 and TimeCreated[timediff(@SystemTime) <= {ms}]]]", null));
        return lines;
    }

    private static IEnumerable<string> QueryEvents(string log, string query, string? mustContain)
    {
        string output;
        try
        {
            var start = new ProcessStartInfo("wevtutil.exe", $"qe {log} /q:\"{query}\" /c:20 /rd:true /f:text")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start);
            if (process is null)
                yield break;
            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
        }
        catch
        {
            yield break;
        }

        // One event per "Event[n]:" block; keep the date, the source and the first lines of the description.
        foreach (var block in output.Split("Event[", StringSplitOptions.RemoveEmptyEntries))
        {
            if (mustContain is not null && !block.Contains(mustContain, StringComparison.OrdinalIgnoreCase))
                continue;
            var fields = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            string Field(string name) => fields.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.Ordinal))?[(name.Length + 1)..].Trim() ?? "?";
            var descriptionAt = fields.FindIndex(l => l.StartsWith("Description:", StringComparison.Ordinal));
            var description = descriptionAt < 0 ? "" : string.Join(" | ", fields.Skip(descriptionAt + 1).Take(6));
            yield return $"{log} {Field("Date")} {Field("Source")} #{Field("Event ID")}: {description}";
        }
    }

    // ----- monitor thread -----

    private static void Run()
    {
        var hangStarted = 0L;
        var hangIncident = false;
        var nextLoad = Stopwatch.GetTimestamp();
        var load = new LoadSampler();

        while (true)
        {
            Thread.Sleep(PingInterval);
            try
            {
                var now = Stopwatch.GetTimestamp();
                var sent = Interlocked.Read(ref _pingSent);
                var answered = Interlocked.Read(ref _pingAnswered);

                if (sent != 0 && answered < sent)
                {
                    var late = Stopwatch.GetElapsedTime(sent, now);
                    if (hangStarted == 0 && late >= HangThreshold)
                    {
                        hangStarted = sent;
                        Diag.Log($"health: UI thread not answering for {late.TotalSeconds:F1} s ({SafeTry.Run(() => _state?.Invoke()) ?? "?"}; {load.Quick()})");
                    }
                    if (hangStarted != 0 && !hangIncident && late >= HangIncident)
                    {
                        hangIncident = true;
                        Diag.RecordIncident("ui hang", $"UI thread frozen for more than {HangIncident.TotalSeconds:F0} s; {SafeTry.Run(() => _state?.Invoke()) ?? "?"}; {load.Quick()}");
                    }
                }
                else
                {
                    if (hangStarted != 0)
                    {
                        var length = Stopwatch.GetElapsedTime(hangStarted, Stopwatch.GetTimestamp());
                        Diag.Log($"health: UI thread back after {length.TotalSeconds:F1} s");
                        if (hangIncident)
                            Diag.RecordIncident("ui hang", $"ended after {length.TotalSeconds:F1} s");
                    }
                    hangStarted = 0;
                    hangIncident = false;
                    var stamp = Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref _pingSent, stamp);
                    _ui?.TryEnqueue(DispatcherQueuePriority.High, () => Interlocked.Exchange(ref _pingAnswered, stamp));
                }

                CheckDevices();

                if (Diag.IsEnabled && now >= nextLoad)
                {
                    nextLoad = now + (long)(LoadInterval.TotalSeconds * Stopwatch.Frequency);
                    Diag.Log("health: " + load.Sample());
                }
            }
            catch (Exception ex)
            {
                Diag.Log("health: monitor error: " + ex.Message);
            }
        }
    }

    private static void CheckDevices()
    {
        List<Codec.Gpu.GpuDevice> devices;
        lock (Devices)
            devices = Devices.Select(w => w.TryGetTarget(out var d) ? d : null).OfType<Codec.Gpu.GpuDevice>().ToList();
        foreach (var device in devices)
        {
            var reason = SafeTry.Run(() => device.Device.DeviceRemovedReason.Code);
            if (reason == 0)
                continue;
            var key = $"{device.GetHashCode()}:{reason}";
            lock (ReportedRemovals)
            {
                if (!ReportedRemovals.Add(key))
                    continue;
            }
            var note = $"GPU device lost: {RemovedReasonName(reason)} (0x{reason:X8}) on {device.AdapterName}";
            Diag.Log("health: " + note);
            Diag.RecordIncident("gpu", note);
        }
    }

    private static string RemovedReasonName(int code) =>
        unchecked((uint)code) switch
        {
            0x887A0006 => "DEVICE_HUNG (the GPU took too long: driver reset/TDR)",
            0x887A0005 => "DEVICE_REMOVED (driver updated, crashed or the GPU went away)",
            0x887A0007 => "DEVICE_RESET (bad command or driver reset)",
            0x887A0020 => "DRIVER_INTERNAL_ERROR",
            0x887A0001 => "INVALID_CALL",
            _ => "unknown",
        };

    public static string PowerText()
    {
        if (!GetSystemPowerStatus(out var status))
            return "?";
        var source = status.ACLineStatus switch { 0 => "battery", 1 => "AC", _ => "AC?" };
        var battery = status.BatteryLifePercent <= 100 ? $" {status.BatteryLifePercent}%" : string.Empty;
        var saver = (status.SystemStatusFlag & 1) != 0 ? ", battery saver ON" : string.Empty;
        return source + battery + saver;
    }

    /// <summary>Everything that is measured over an interval keeps its previous reading here.</summary>
    private sealed class LoadSampler
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly GpuCounters? _gpu = SafeTry.Run(() => new GpuCounters());
        private TimeSpan _lastCpu;
        private long _lastTicks;
        private ulong _lastIdle, _lastKernel, _lastUser;
        private IDXGIAdapter3? _adapter;

        public string Quick()
        {
            _process.Refresh();
            return $"threads {_process.Threads.Count}, working set {_process.WorkingSet64 / 1024 / 1024} MB";
        }

        public string Sample()
        {
            _process.Refresh();
            var now = Stopwatch.GetTimestamp();
            var cpu = _process.TotalProcessorTime;
            var appCpu = _lastTicks == 0 ? 0 : (cpu - _lastCpu).TotalSeconds / Stopwatch.GetElapsedTime(_lastTicks, now).TotalSeconds / Environment.ProcessorCount * 100;
            _lastCpu = cpu;
            _lastTicks = now;

            var systemCpu = 0.0;
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                var total = (kernel - _lastKernel) + (user - _lastUser);
                if (_lastKernel != 0 && total > 0)
                    systemCpu = 100.0 * (total - (idle - _lastIdle)) / total;
                (_lastIdle, _lastKernel, _lastUser) = (idle, kernel, user);
            }

            var text = $"cpu app {appCpu:F0}% system {systemCpu:F0}%{ClockText()}, mem {_process.WorkingSet64 / 1024 / 1024} MB (private {_process.PrivateMemorySize64 / 1024 / 1024}, gc {GC.GetTotalMemory(false) / 1024 / 1024}), threads {_process.Threads.Count}, handles {_process.HandleCount}";
            var gpu = SafeTry.Run(() => _gpu?.Read());
            if (gpu is not null)
                text += ", gpu " + gpu;
            text += VideoMemoryText();
            text += ", power " + PowerText();
            return text;
        }

        /// <summary>Average clock of the cores against their maximum: low on a busy machine means throttling.</summary>
        private static string ClockText()
        {
            var count = Environment.ProcessorCount;
            var info = new ProcessorPowerInformation[count];
            var size = Marshal.SizeOf<ProcessorPowerInformation>() * count;
            if (CallNtPowerInformation(11, IntPtr.Zero, 0, info, size) != 0)
                return string.Empty;
            var current = info.Average(i => (double)i.CurrentMhz);
            var max = info.Max(i => i.MaxMhz);
            return max == 0 ? string.Empty : $", clock {current:F0}/{max} MHz";
        }

        private string VideoMemoryText()
        {
            try
            {
                if (_adapter is null)
                {
                    using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
                    factory.EnumAdapters1(0, out IDXGIAdapter1? first).CheckError();
                    using (first)
                        _adapter = first!.QueryInterface<IDXGIAdapter3>();
                }
                var local = _adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                var shared = _adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal);
                return $", vram app {local.CurrentUsage / 1024 / 1024}/{local.Budget / 1024 / 1024} MB local, {shared.CurrentUsage / 1024 / 1024}/{shared.Budget / 1024 / 1024} MB shared";
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// The "GPU Engine" performance counters (what Task Manager shows), summed per engine type for
    /// this process and for the whole machine.
    /// </summary>
    private sealed class GpuCounters
    {
        private readonly IntPtr _query;
        private readonly IntPtr _counter;
        private readonly string _pidTag = $"pid_{Environment.ProcessId}_";

        public GpuCounters()
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
                throw new InvalidOperationException("pdh");
            if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
                throw new InvalidOperationException("gpu counter");
            PdhCollectQueryData(_query);
        }

        public string? Read()
        {
            if (PdhCollectQueryData(_query) != 0)
                return null;
            uint size = 0;
            const uint PdhMoreData = 0x800007D2;
            const uint FormatDouble = 0x200;
            if (unchecked((uint)PdhGetFormattedCounterArray(_counter, FormatDouble, ref size, out var count, IntPtr.Zero)) != PdhMoreData)
                return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArray(_counter, FormatDouble, ref size, out count, buffer) != 0)
                    return null;
                var total = new Dictionary<string, double>();
                var mine = new Dictionary<string, double>();
                var itemSize = Marshal.SizeOf<PdhCounterItem>();
                for (var i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PdhCounterItem>(buffer + i * itemSize);
                    var name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;
                    var at = name.IndexOf("engtype_", StringComparison.Ordinal);
                    if (at < 0 || item.Status != 0)
                        continue;
                    var type = name[(at + 8)..];
                    // Summed over engines and processes, capped at 100 for the total: close enough to Task Manager.
                    total[type] = total.GetValueOrDefault(type) + item.Value;
                    if (name.StartsWith(_pidTag, StringComparison.Ordinal))
                        mine[type] = mine.GetValueOrDefault(type) + item.Value;
                }
                string Pick(string type) => $"{mine.GetValueOrDefault(type):F0}/{Math.Min(100, total.GetValueOrDefault(type)):F0}%";
                return $"3D {Pick("3D")} encode {Pick("VideoEncode")} decode {Pick("VideoDecode")} copy {Pick("Copy")} (app/total)";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    // ----- native -----

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhCounterItem
    {
        public IntPtr Name;
        public uint Status;
        public uint Padding;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number;
        public uint MaxMhz;
        public uint CurrentMhz;
        public uint MhzLimit;
        public uint MaxIdleState;
        public uint CurrentIdleState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);

    [DllImport("powrprof.dll")]
    private static extern int CallNtPowerInformation(int level, IntPtr input, int inputLength, [Out] ProcessorPowerInformation[] output, int outputLength);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
