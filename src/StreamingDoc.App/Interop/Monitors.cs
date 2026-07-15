using System.Runtime.InteropServices;

namespace StreamingDoc.App.Interop;

public readonly record struct MonitorInfo(IntPtr Handle, string Name, int Width, int Height, bool Primary);

internal static class Monitors
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMon, ref MonitorInfoEx info);

    private const int MONITORINFOF_PRIMARY = 1;

    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Rect r, IntPtr d) =>
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfoW(h, ref info))
            {
                bool primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                list.Add(new MonitorInfo(
                    h,
                    info.szDevice,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top,
                    primary));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
