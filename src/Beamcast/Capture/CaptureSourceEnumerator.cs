using System.Diagnostics;
using System.Runtime.InteropServices;
using static Beamcast.Capture.NativeMethods;

namespace Beamcast.Capture;

/// <summary>Lists monitors and the top-level windows a person would recognise from the taskbar.</summary>
public static class CaptureSourceEnumerator
{
    private static readonly HashSet<string> HiddenClasses = new(StringComparer.Ordinal)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "ApplicationFrameWindow",
        "XamlExplorerHostIslandWindow",
    };

    public static IReadOnlyList<CaptureSource> Monitors()
    {
        var list = new List<CaptureSource>();
        var index = 0;
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr monitor, IntPtr _, ref Rect _, IntPtr _) =>
            {
                var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
                if (!GetMonitorInfo(monitor, ref info))
                    return true;

                index++;
                var primary = (info.Flags & MonitorInfoPrimary) != 0;
                var device = (info.Device ?? string.Empty).Replace(@"\\.\", string.Empty);
                list.Add(
                    new CaptureSource(
                        CaptureSourceKind.Monitor,
                        monitor,
                        $"Monitor {index}",
                        device,
                        info.Monitor.Width,
                        info.Monitor.Height,
                        primary
                    )
                );
                return true;
            },
            IntPtr.Zero
        );

        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Title).ToList();
    }

    public static IReadOnlyList<CaptureSource> Windows()
    {
        var list = new List<CaptureSource>();
        var ownPid = GetCurrentProcessId();
        var shell = GetShellWindow();
        var titleBuffer = new char[512];
        var classBuffer = new char[256];

        EnumWindows(
            (hwnd, _) =>
            {
                if (hwnd == shell || !IsWindowVisible(hwnd) || IsIconic(hwnd))
                    return true;
                if (GetAncestor(hwnd, GaRootOwner) != hwnd)
                    return true;

                var exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
                if ((exStyle & WsExToolWindow) != 0)
                    return true;
                if (IsCloaked(hwnd))
                    return true;

                var titleLength = GetWindowText(hwnd, titleBuffer, titleBuffer.Length);
                if (titleLength <= 0)
                    return true;
                var title = new string(titleBuffer, 0, titleLength);

                var classLength = GetClassName(hwnd, classBuffer, classBuffer.Length);
                var className = classLength > 0 ? new string(classBuffer, 0, classLength) : string.Empty;
                if (HiddenClasses.Contains(className))
                    return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == ownPid)
                    return true;

                var bounds = WindowBounds(hwnd);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return true;

                list.Add(
                    new CaptureSource(
                        CaptureSourceKind.Window,
                        hwnd,
                        title,
                        ProcessName(pid),
                        bounds.Width,
                        bounds.Height,
                        false
                    )
                );
                return true;
            },
            IntPtr.Zero
        );

        return list;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static Rect WindowBounds(IntPtr hwnd)
    {
        try
        {
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) == 0)
                return frame;
        }
        catch (Exception) { }

        return GetWindowRect(hwnd, out var rect) ? rect : default;
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
