using System.Runtime.InteropServices;
using StreamingDoc.App.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StreamingDoc.App.Sources;

/// <summary>Sorgente statica da pixel BGRA (immagine, testo): texture immutabile + SRV.</summary>
public abstract class PixelSource : ISource
{
    private readonly GraphicsDevice _gd;
    private readonly ID3D11Texture2D _tex;
    private readonly ID3D11ShaderResourceView _srv;

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    protected PixelSource(GraphicsDevice gd, string name, byte[] bgra, int width, int height)
    {
        _gd = gd;
        Name = name;
        Width = width;
        Height = height;

        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
                BindFlags.ShaderResource, ResourceUsage.Immutable);
            _tex = gd.Device.CreateTexture2D(desc,
                new[] { new SubresourceData(handle.AddrOfPinnedObject(), (uint)(width * 4)) });
        }
        finally { handle.Free(); }

        _srv = gd.Device.CreateShaderResourceView(_tex);
    }

    public ID3D11ShaderResourceView GetSrv() => _srv;

    public void Dispose()
    {
        lock (_gd.ContextLock)
        {
            _srv.Dispose();
            _tex.Dispose();
        }
    }
}
