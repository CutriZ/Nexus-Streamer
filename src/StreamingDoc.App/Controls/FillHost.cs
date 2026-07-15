using System.Windows;
using System.Windows.Controls;

namespace StreamingDoc.App.Controls;

/// <summary>
/// Contenitore che NON richiede spazio nel layout (DesiredSize = 0): il figlio (es. l'Image del
/// preview, 1280x720) viene comunque arrangiato a riempire l'area assegnata. Evita che la dimensione
/// intrinseca del preview schiacci/collassi i pannelli vicini.
/// </summary>
public sealed class FillHost : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(new Size(0, 0));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
