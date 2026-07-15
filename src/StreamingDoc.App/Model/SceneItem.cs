using System.ComponentModel;
using System.Runtime.CompilerServices;
using StreamingDoc.App.Sources;

namespace StreamingDoc.App.Model;

/// <summary>Istanza di una sorgente in una scena: trasformazione (rettangolo nel canvas) + visibilita'.</summary>
public sealed class SceneItem : INotifyPropertyChanged
{
    public ISource Source { get; private set; }

    /// <summary>Sostituisce la sorgente (es. dopo modifica proprietà). Il chiamante dispone la vecchia.</summary>
    public void ReplaceSource(ISource source)
    {
        Source = source;
        OnChanged(nameof(Name));
    }

    /// <summary>Ricetta per ricreare la sorgente al load (null = non persistibile).</summary>
    public SourceDescriptor? Descriptor { get; set; }

    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }

    private bool _visible = true;
    public bool Visible
    {
        get => _visible;
        set { if (_visible != value) { _visible = value; OnChanged(); } }
    }

    private bool _locked;
    public bool Locked
    {
        get => _locked;
        set { if (_locked != value) { _locked = value; OnChanged(); } }
    }

    // Filtri (letti dal render thread)
    public double Opacity { get; set; } = 1.0;
    public bool ChromaKey { get; set; }
    public double ChromaR { get; set; }
    public double ChromaG { get; set; } = 1.0;
    public double ChromaB { get; set; }
    public double ChromaSimilarity { get; set; } = 0.4;
    public double ChromaSmoothness { get; set; } = 0.1;

    public string Name => Source.Name;

    public string Icon => Descriptor?.Kind switch
    {
        SourceKind.Monitor => "🖥",
        SourceKind.Window => "🪟",
        SourceKind.Webcam => "📷",
        SourceKind.Image => "🖼",
        SourceKind.Text => "🔤",
        SourceKind.Color => "🎨",
        SourceKind.Media => "🎬",
        SourceKind.AudioDevice => "🎚",
        SourceKind.Ndi => "📡",
        _ => "▦",
    };

    public SceneItem(ISource source) => Source = source;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
