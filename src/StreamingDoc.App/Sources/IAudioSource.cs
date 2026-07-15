using StreamingDoc.App.Audio;

namespace StreamingDoc.App.Sources;

/// <summary>
/// Sorgente che porta con sé un canale audio da aggiungere al mixer (Media, cattura device, NDI).
/// Il chiamante aggiunge AudioChannel al mixer dopo la creazione e lo rimuove al Dispose.
/// </summary>
public interface IAudioSource
{
    AudioInput? AudioChannel { get; }
}
