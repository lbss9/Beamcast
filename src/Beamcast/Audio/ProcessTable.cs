using System.Runtime.InteropServices;

namespace Beamcast.Audio;

/// <summary>One row of the process table: enough to walk parent chains and match executable names.</summary>
public readonly record struct ProcessRow(int Pid, int ParentPid, string Name);

/// <summary>Snapshot of running processes (name + parent) through ToolHelp, which needs no special rights.</summary>
public static class ProcessTable
{
    private const uint SnapProcess = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    public static Dictionary<int, ProcessRow> Snapshot()
    {
        var rows = new Dictionary<int, ProcessRow>();
        var snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return rows;

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry))
                return rows;
            do
            {
                var name = Path.GetFileNameWithoutExtension(entry.ExeFile ?? string.Empty).ToLowerInvariant();
                rows[(int)entry.ProcessId] = new ProcessRow((int)entry.ProcessId, (int)entry.ParentProcessId, name);
            } while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return rows;
    }
}
