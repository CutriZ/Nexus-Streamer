using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace StreamingDoc.App.Interop;

/// <summary>
/// Glue tra le proiezioni WinRT (Windows.Graphics.Capture) e i tipi D3D11 nativi (Vortice).
/// </summary>
internal static class CaptureInterop
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid ID3D11Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = false)]
    private static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>Avvolge un ID3D11Device (via il suo IDXGIDevice) in un IDirect3DDevice WinRT.</summary>
    public static IDirect3DDevice CreateWinRTDevice(IDXGIDevice dxgiDevice)
    {
        CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr ptr);
        try { return MarshalInspectable<IDirect3DDevice>.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
    }

    /// <summary>Crea un GraphicsCaptureItem da un HMONITOR (cattura schermo intero, senza picker).</summary>
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var interop = GetInterop();
        var iid = GraphicsCaptureItemIid;
        IntPtr itemPtr = interop.CreateForMonitor(hmon, ref iid);
        try { return GraphicsCaptureItem.FromAbi(itemPtr); }
        finally { Marshal.Release(itemPtr); }
    }

    /// <summary>Crea un GraphicsCaptureItem da un HWND (cattura singola finestra).</summary>
    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var interop = GetInterop();
        var iid = GraphicsCaptureItemIid;
        IntPtr itemPtr = interop.CreateForWindow(hwnd, ref iid);
        try { return GraphicsCaptureItem.FromAbi(itemPtr); }
        finally { Marshal.Release(itemPtr); }
    }

    private static IGraphicsCaptureItemInterop GetInterop()
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        return factory.AsInterface<IGraphicsCaptureItemInterop>();
    }

    /// <summary>Estrae l'ID3D11Texture2D nativo da una IDirect3DSurface di un frame catturato.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = MarshalInspectable<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var iidAccess = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, ref iidAccess, out IntPtr accessPtr));
            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPtr);
                var iidTex = ID3D11Texture2DIid;
                IntPtr texPtr = access.GetInterface(ref iidTex);
                return new ID3D11Texture2D(texPtr);
            }
            finally { Marshal.Release(accessPtr); }
        }
        finally { Marshal.Release(surfacePtr); }
    }
}
