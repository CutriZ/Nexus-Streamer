using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamingDoc.App.Graphics;

namespace StreamingDoc.App.Sources;

/// <summary>Immagine da file (PNG/JPG/...), decodificata via WIC in BGRA.</summary>
public sealed class ImageSource : PixelSource
{
    public ImageSource(GraphicsDevice gd, string path)
        : this(gd, Path.GetFileName(path), Load(path)) { }

    private ImageSource(GraphicsDevice gd, string name, (byte[] px, int w, int h) d)
        : base(gd, "Immagine: " + name, d.px, d.w, d.h) { }

    private static (byte[] px, int w, int h) Load(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        BitmapSource src = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[h * stride];
        src.CopyPixels(px, stride, 0);
        return (px, w, h);
    }
}
