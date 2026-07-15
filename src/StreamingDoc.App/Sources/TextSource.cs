using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamingDoc.App.Graphics;

namespace StreamingDoc.App.Sources;

/// <summary>Testo renderizzato (WPF FormattedText) in una texture BGRA con alpha.</summary>
public sealed class TextSource : PixelSource
{
    public TextSource(GraphicsDevice gd, string text, double fontSize = 96)
        : this(gd, text, Render(text, fontSize)) { }

    private TextSource(GraphicsDevice gd, string text, (byte[] px, int w, int h) d)
        : base(gd, "Testo: " + Short(text), d.px, d.w, d.h) { }

    private static string Short(string t) => t.Length <= 20 ? t : t[..20] + "...";

    private static (byte[] px, int w, int h) Render(string text, double fontSize)
    {
        var typeface = new Typeface("Segoe UI");
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, fontSize, Brushes.White, 1.0);

        int w = Math.Max(1, (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace));
        int h = Math.Max(1, (int)Math.Ceiling(ft.Height));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawText(ft, new Point(0, 0));

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        // Pbgra (premoltiplicato) -> Bgra32 (alpha dritto) per il blend del compositor.
        var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        int stride = w * 4;
        var px = new byte[h * stride];
        conv.CopyPixels(px, stride, 0);
        return (px, w, h);
    }
}
