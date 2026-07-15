using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace StreamingDoc.App.Controls;

/// <summary>
/// Con WindowStyle=None + WindowChrome la finestra massimizzata copre la taskbar e sfora i bordi.
/// Questo aggancia WM_GETMINMAXINFO per limitare il massimo all'area di lavoro del monitor corrente.
/// </summary>
public static class WindowMaximizeHelper
{
    public static void Enable(Window w)
    {
        var handle = new WindowInteropHelper(w).Handle;
        if (handle != IntPtr.Zero) Hook(w);
        else w.SourceInitialized += (_, _) => Hook(w);
    }

    private static void Hook(Window w)
    {
        var handle = new WindowInteropHelper(w).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        RoundCorners(handle);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    // Win11: arrotonda gli angoli della finestra a livello DWM.
    private static void RoundCorners(IntPtr hwnd)
    {
        try { int pref = DWMWCP_ROUND; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        catch { /* < Win11: ignora */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        RECT work = mi.rcWork, mon = mi.rcMonitor;
        mmi.ptMaxPosition.X = work.left - mon.left;
        mmi.ptMaxPosition.Y = work.top - mon.top;
        mmi.ptMaxSize.X = work.right - work.left;
        mmi.ptMaxSize.Y = work.bottom - work.top;
        Marshal.StructureToPtr(mmi, lParam, true);

        handled = true;
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
