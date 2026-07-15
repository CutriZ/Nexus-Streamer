using System.Runtime.InteropServices;
using System.Text;

namespace StreamingDoc.App.Interop;

public readonly record struct WindowInfo(IntPtr Handle, string Title);

internal static class WindowEnum
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern int GetWindowLongW(IntPtr hWnd, int index);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Finestre top-level visibili con titolo, escluse le tool window.</summary>
    public static List<WindowInfo> Enumerate(IntPtr exclude)
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (h == exclude || !IsWindowVisible(h)) return true;
            if ((GetWindowLongW(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true;
            int len = GetWindowTextLengthW(h);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(h, sb, sb.Capacity);
            var title = sb.ToString();
            if (!string.IsNullOrWhiteSpace(title))
                list.Add(new WindowInfo(h, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
