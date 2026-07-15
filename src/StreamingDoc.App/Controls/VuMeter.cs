using System;
using System.Windows;
using System.Windows.Media;

namespace StreamingDoc.App.Controls;

/// <summary>
/// Indicatore di livello audio (VU) verticale a rendering diretto: riempimento dal basso
/// proporzionale a <see cref="Level"/> (0..1), gradiente verde→giallo→rosso per zona dB.
/// Sostituisce la ProgressBar verticale (il cui indicatore custom non viene dimensionato da WPF).
/// </summary>
public sealed class VuMeter : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(double), typeof(VuMeter),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public static readonly DependencyProperty HorizontalProperty =
        DependencyProperty.Register(nameof(Horizontal), typeof(bool), typeof(VuMeter),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool Horizontal
    {
        get => (bool)GetValue(HorizontalProperty);
        set => SetValue(HorizontalProperty, value);
    }

    // Se true, Level (ampiezza 0..1) viene mappato su una scala in dB (-60..0) come in OBS.
    public static readonly DependencyProperty DbScaleProperty =
        DependencyProperty.Register(nameof(DbScale), typeof(bool), typeof(VuMeter),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool DbScale
    {
        get => (bool)GetValue(DbScaleProperty);
        set => SetValue(DbScaleProperty, value);
    }

    // Posizione del fader in dB (-60..0): disegna una tacca verticale sul meter, così la
    // posizione del volume "corrisponde" visivamente al livello. NaN = nessuna tacca.
    public static readonly DependencyProperty MarkerDbProperty =
        DependencyProperty.Register(nameof(MarkerDb), typeof(double), typeof(VuMeter),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public double MarkerDb
    {
        get => (double)GetValue(MarkerDbProperty);
        set => SetValue(MarkerDbProperty, value);
    }

    private const double MinDb = -60.0;

    private static readonly Brush Background;
    private static readonly Brush FillV; // gradiente basso->alto (verticale)
    private static readonly Brush FillH; // gradiente sinistra->destra (orizzontale)
    private static readonly Pen MarkerPen;

    static VuMeter()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x13, 0x1B));
        Background.Freeze();
        FillV = MakeGradient(new Point(0, 1), new Point(0, 0));
        FillH = MakeGradient(new Point(0, 0), new Point(1, 0));
        MarkerPen = new Pen(new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF8)), 2);
        MarkerPen.Freeze();
    }

    private static Brush MakeGradient(Point start, Point end)
    {
        // Zone come OBS: verde fino a ~-20dB (0.667), giallo fino a ~-9dB (0.85), rosso fino a 0.
        var g = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x26, 0xA6, 0x5B), 0.0));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0x3D, 0xDC, 0x84), 0.60));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE0, 0xB0, 0x20), 0.70));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xE0, 0xB0, 0x20), 0.85));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xD6, 0x42, 0x42), 0.90));
        g.GradientStops.Add(new GradientStop(Color.FromRgb(0xD6, 0x42, 0x42), 1.0));
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var rect = new Rect(0, 0, w, h);
        dc.DrawRoundedRectangle(Background, null, rect, 2, 2);

        // Traccia tenue: l'intera scala colore a bassa opacità. Così lo spazio non riempito
        // sembra una scala sfumata e non un "buco" tra il livello e la tacca del fader.
        dc.PushClip(new RectangleGeometry(rect, 2, 2));
        dc.PushOpacity(0.15);
        dc.DrawRectangle(Horizontal ? FillH : FillV, null, rect);
        dc.Pop();
        dc.Pop();

        double lvl = Math.Clamp(Level, 0, 1);
        if (DbScale && lvl > 0)
        {
            double db = 20.0 * Math.Log10(lvl);          // ampiezza -> dB
            lvl = Math.Clamp((db - MinDb) / (0 - MinDb), 0, 1); // -60..0 dB -> 0..1
        }

        // Livello reale (brillante), gradiente a piena dimensione: le zone colore restano assolute.
        if (lvl > 0)
        {
            Rect fill = Horizontal ? new Rect(0, 0, w * lvl, h) : new Rect(0, h - h * lvl, w, h * lvl);
            dc.PushClip(new RectangleGeometry(fill, 2, 2));
            dc.DrawRectangle(Horizontal ? FillH : FillV, null, rect);
            dc.Pop();
        }

        // Tacca del fader: posizione del volume sulla stessa scala -60..0 dB del meter (sempre visibile).
        if (!double.IsNaN(MarkerDb))
        {
            double p = Math.Clamp((MarkerDb - MinDb) / (0 - MinDb), 0, 1);
            if (Horizontal)
            {
                double x = Math.Round(p * w) + 0.5;
                dc.DrawLine(MarkerPen, new Point(x, 0), new Point(x, h));
            }
            else
            {
                double y = Math.Round(h - p * h) + 0.5;
                dc.DrawLine(MarkerPen, new Point(0, y), new Point(w, y));
            }
        }
    }
}
