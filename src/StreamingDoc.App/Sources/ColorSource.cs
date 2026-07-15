using System.Runtime.InteropServices;
using StreamingDoc.App.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace StreamingDoc.App.Sources;

/// <summary>Sorgente a tinta unita (sfondo). Texture 1x1 stirata sul rettangolo dello scene item.</summary>
public sealed class ColorSource : ISource
{
    private readonly GraphicsDevice _gd;
    private readonly ID3D11Texture2D _tex;
    private readonly ID3D11ShaderResourceView _srv;

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }

    public ColorSource(GraphicsDevice gd, byte r, byte g, byte b, int width, int height, string name = "Colore")
    {
        _gd = gd;
        Name = name;
        Width = width;
        Height = height;

        // BGRA, premoltiplicato non necessario (alpha=255).
        byte[] px = { b, g, r, 255 };
        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, 1, 1, 1, 1,
                BindFlags.ShaderResource, ResourceUsage.Immutable);
            var data = new SubresourceData(handle.AddrOfPinnedObject(), 4u);
            _tex = gd.Device.CreateTexture2D(desc, new[] { data });
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
