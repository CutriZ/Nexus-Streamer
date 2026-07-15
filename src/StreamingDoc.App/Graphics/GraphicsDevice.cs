using StreamingDoc.App.Interop;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using static Vortice.Direct3D11.D3D11;

namespace StreamingDoc.App.Graphics;

/// <summary>
/// Device D3D11 unico condiviso tra cattura (WGC) e rendering. L'immediate context non e'
/// thread-safe: ogni uso (copy dei frame, draw, present) va serializzato con <see cref="ContextLock"/>.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDirect3DDevice WinRTDevice { get; }

    /// <summary>Serializza tutti gli accessi all'immediate context.</summary>
    public readonly object ContextLock = new();

    public GraphicsDevice()
    {
        D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, null,
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device;
        Context = context;

        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        WinRTDevice = CaptureInterop.CreateWinRTDevice(dxgi);
    }

    public void Dispose()
    {
        (WinRTDevice as IDisposable)?.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
