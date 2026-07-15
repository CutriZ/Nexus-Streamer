using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Vortice.Direct3D11;

namespace StreamingDoc.App.Graphics;

/// <summary>
/// Anteprima zero-copy: avvolge un <see cref="PreviewTarget"/> (texture D3D11 condivisa ↔ surface D3D9)
/// in un <see cref="D3DImage"/> WPF. Il compositor scrive sulla texture D3D11 in VRAM, WPF la legge
/// direttamente in GPU — nessun readback in RAM. Vive sul thread UI.
/// </summary>
public sealed class D3DImagePreview : IDisposable
{
    private readonly PreviewTarget _target;
    private readonly D3DImage _image;

    public ImageSource Image => _image;
    public ID3D11Texture2D SharedTexture => _target.Texture;
    public int Width => _target.Width;
    public int Height => _target.Height;

    public D3DImagePreview(GraphicsDevice gd, int width, int height)
    {
        _target = new PreviewTarget(gd.Device, width, height);
        _image = new D3DImage();
        _image.IsFrontBufferAvailableChanged += (_, _) => SetBackBuffer();
        SetBackBuffer();
    }

    private void SetBackBuffer()
    {
        _image.Lock();
        try
        {
            _image.SetBackBuffer(
                _image.IsFrontBufferAvailable ? D3DResourceType.IDirect3DSurface9 : D3DResourceType.IDirect3DSurface9,
                _image.IsFrontBufferAvailable ? _target.Surface9Pointer : IntPtr.Zero);
        }
        finally { _image.Unlock(); }
    }

    /// <summary>Segnala a WPF che la texture condivisa è cambiata (chiamare sul thread UI dopo Flush del compositor).</summary>
    public void Invalidate()
    {
        if (!_image.IsFrontBufferAvailable) return;
        _image.Lock();
        try { _image.AddDirtyRect(new Int32Rect(0, 0, _target.Width, _target.Height)); }
        finally { _image.Unlock(); }
    }

    public void Dispose() => _target.Dispose();
}
