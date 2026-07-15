using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace StreamingDoc.App.Controls;

/// <summary>
/// Ospita un HWND Win32 figlio (target swapchain DXGI) dentro WPF e inoltra mouse + resize.
/// Le coordinate sono in pixel fisici del client (coerenti con la swapchain), non DIP.
/// </summary>
public sealed class D3DPreviewHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;

    private const int WM_SIZE = 0x0005;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground, lpszMenuName, lpszClassName, hIconSm;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, IntPtr className, IntPtr windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();

    private static readonly WndProcDelegate s_wndProc = StaticWndProc;
    private static readonly ConcurrentDictionary<IntPtr, D3DPreviewHost> s_instances = new();
    private static IntPtr s_classNamePtr;
    private static readonly object s_classLock = new();

    public IntPtr Hwnd { get; private set; }
    public int ClientW { get; private set; } = 1;
    public int ClientH { get; private set; } = 1;

    public event Action? HwndReady;
    public event Action<int, int>? ClientResized;
    public event Action<int, int>? MouseDownAt;
    public event Action<int, int>? MouseMoveAt;
    public event Action<int, int>? MouseUpAt;

    private static short LoWord(IntPtr v) => unchecked((short)((long)v & 0xFFFF));
    private static short HiWord(IntPtr v) => unchecked((short)(((long)v >> 16) & 0xFFFF));

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr w, IntPtr l)
    {
        if (s_instances.TryGetValue(hWnd, out var inst) && inst.OnMessage(msg, l))
            return IntPtr.Zero;
        return DefWindowProcW(hWnd, msg, w, l);
    }

    private bool OnMessage(uint msg, IntPtr l)
    {
        switch (msg)
        {
            case WM_SIZE:
                ClientW = Math.Max(1, (int)LoWord(l));
                ClientH = Math.Max(1, (int)HiWord(l));
                ClientResized?.Invoke(ClientW, ClientH);
                return false; // lascia anche il default
            case WM_LBUTTONDOWN:
                SetCapture(Hwnd);
                MouseDownAt?.Invoke(LoWord(l), HiWord(l));
                return true;
            case WM_MOUSEMOVE:
                MouseMoveAt?.Invoke(LoWord(l), HiWord(l));
                return false;
            case WM_LBUTTONUP:
                ReleaseCapture();
                MouseUpAt?.Invoke(LoWord(l), HiWord(l));
                return true;
            default:
                return false;
        }
    }

    private static void EnsureClass()
    {
        lock (s_classLock)
        {
            if (s_classNamePtr != IntPtr.Zero) return;
            var namePtr = Marshal.StringToHGlobalUni("StreamingDocPreviewHost");
            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(s_wndProc),
                hInstance = GetModuleHandleW(IntPtr.Zero),
                lpszClassName = namePtr,
            };
            RegisterClassExW(ref cls);
            s_classNamePtr = namePtr;
        }
    }

    // Non richiedere spazio nel layout: l'host riempie il rettangolo che gli viene assegnato
    // in arrange, senza forzare il Grid genitore a crescere (evita che gonfi/schiacci i fratelli).
    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        => new System.Windows.Size(0, 0);

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        EnsureClass();
        Hwnd = CreateWindowExW(0, s_classNamePtr, IntPtr.Zero,
            WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN, 0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        s_instances[Hwnd] = this;
        HwndReady?.Invoke();
        return new HandleRef(this, Hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (Hwnd != IntPtr.Zero)
        {
            s_instances.TryRemove(Hwnd, out _);
            DestroyWindow(Hwnd);
            Hwnd = IntPtr.Zero;
        }
    }
}
