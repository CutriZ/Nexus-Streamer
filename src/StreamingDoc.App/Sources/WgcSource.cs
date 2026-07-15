using StreamingDoc.App.Graphics;
using StreamingDoc.App.Interop;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace StreamingDoc.App.Sources;

/// <summary>
/// Base per le sorgenti Windows Graphics Capture (monitor/finestra). I frame transitori del
/// frame pool vengono copiati in una texture persistente (sotto ContextLock) campionabile dal compositor.
/// </summary>
public abstract class WgcSource : ISource
{
    private const DirectXPixelFormat PixelFormat = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly GraphicsDevice _gd;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private SizeInt32 _poolSize;
    private ID3D11Texture2D? _frameTex;
    private ID3D11ShaderResourceView? _srv;
    private bool _disposed;

    public string Name { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    protected WgcSource(GraphicsDevice gd, GraphicsCaptureItem item, string name)
    {
        _gd = gd;
        _item = item;
        Name = name;
        _poolSize = item.Size;
        Width = item.Size.Width;
        Height = item.Size.Height;

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gd.WinRTDevice, PixelFormat, 2, _poolSize);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.StartCapture();
    }

    public ID3D11ShaderResourceView? GetSrv() => _srv;

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null || _disposed) return;

        var content = frame.ContentSize;
        if (content.Width > 0 && content.Height > 0 &&
            (content.Width != _poolSize.Width || content.Height != _poolSize.Height))
        {
            _poolSize = content;
            sender.Recreate(_gd.WinRTDevice, PixelFormat, 2, content);
            return; // il frame corrente e' della vecchia dimensione: salta, il prossimo sara' corretto
        }

        using var src = CaptureInterop.GetTexture(frame.Surface);
        var d = src.Description;

        lock (_gd.ContextLock)
        {
            if (_disposed) return;
            EnsureTarget(d);
            _gd.Context.CopyResource(_frameTex!, src);
        }

        Width = content.Width > 0 ? content.Width : (int)d.Width;
        Height = content.Height > 0 ? content.Height : (int)d.Height;
    }

    private void EnsureTarget(Texture2DDescription src)
    {
        if (_frameTex is not null &&
            _frameTex.Description.Width == src.Width &&
            _frameTex.Description.Height == src.Height)
            return;

        _srv?.Dispose();
        _frameTex?.Dispose();

        var desc = new Texture2DDescription(src.Format, src.Width, src.Height, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Default);
        _frameTex = _gd.Device.CreateTexture2D(desc);
        _srv = _gd.Device.CreateShaderResourceView(_frameTex);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.FrameArrived -= OnFrameArrived;
        _session.Dispose();
        _pool.Dispose();
        lock (_gd.ContextLock)
        {
            _srv?.Dispose();
            _frameTex?.Dispose();
        }
    }
}

public sealed class MonitorSource : WgcSource
{
    public MonitorSource(GraphicsDevice gd, MonitorInfo monitor)
        : base(gd, CaptureInterop.CreateItemForMonitor(monitor.Handle), $"Monitor: {monitor.Name}") { }
}

public sealed class WindowSource : WgcSource
{
    public WindowSource(GraphicsDevice gd, WindowInfo window)
        : base(gd, CaptureInterop.CreateItemForWindow(window.Handle), $"Finestra: {Trim(window.Title)}") { }

    private static string Trim(string t) => t.Length <= 40 ? t : t[..40] + "...";
}
