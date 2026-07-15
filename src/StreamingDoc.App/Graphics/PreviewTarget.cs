using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Dx9 = Vortice.Direct3D9;

namespace StreamingDoc.App.Graphics;

/// <summary>
/// Bersaglio di preview condiviso D3D11↔D3D9 per WPF D3DImage (niente HwndHost/airspace).
/// Una texture D3D11 (render target) viene aperta come surface D3D9: il compositor disegna in D3D11,
/// WPF mostra la surface D3D9 via D3DImage.
/// </summary>
public sealed class PreviewTarget : IDisposable
{
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    public int Width { get; }
    public int Height { get; }
    public ID3D11Texture2D Texture { get; }
    public ID3D11RenderTargetView Rtv { get; }
    public IntPtr Surface9Pointer => _surface9.NativePointer;

    private readonly Dx9.IDirect3D9Ex _d3d9;
    private readonly Dx9.IDirect3DDevice9Ex _device9;
    private readonly Dx9.IDirect3DTexture9 _texture9;
    private readonly Dx9.IDirect3DSurface9 _surface9;

    public PreviewTarget(ID3D11Device device11, int width, int height)
    {
        Width = width;
        Height = height;

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.Shared,
        };
        Texture = device11.CreateTexture2D(desc);
        Rtv = device11.CreateRenderTargetView(Texture);

        using var dxgiRes = Texture.QueryInterface<IDXGIResource>();
        IntPtr sharedHandle = dxgiRes.SharedHandle;

        _d3d9 = Dx9.D3D9.Direct3DCreate9Ex();
        var pp = new Dx9.PresentParameters
        {
            Windowed = true,
            SwapEffect = Dx9.SwapEffect.Discard,
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = Dx9.Format.Unknown,
            DeviceWindowHandle = GetDesktopWindow(),
            PresentationInterval = Dx9.PresentInterval.Immediate,
        };
        _device9 = _d3d9.CreateDeviceEx(0, Dx9.DeviceType.Hardware, GetDesktopWindow(),
            Dx9.CreateFlags.HardwareVertexProcessing | Dx9.CreateFlags.Multithreaded | Dx9.CreateFlags.FpuPreserve, pp);

        _texture9 = _device9.CreateTexture((uint)width, (uint)height, 1, Dx9.Usage.RenderTarget,
            Dx9.Format.A8R8G8B8, Dx9.Pool.Default, ref sharedHandle);
        _surface9 = _texture9.GetSurfaceLevel(0);
    }

    public void Dispose()
    {
        _surface9.Dispose();
        _texture9.Dispose();
        _device9.Dispose();
        _d3d9.Dispose();
        Rtv.Dispose();
        Texture.Dispose();
    }
}
