using Vortice.Direct3D11;

namespace StreamingDoc.App.Sources;

/// <summary>
/// Sorgente video componibile. Espone l'ultimo frame come SRV campionabile dal compositor.
/// GetSrv viene chiamato sul thread di render mentre si tiene GraphicsDevice.ContextLock.
/// </summary>
public interface ISource : IDisposable
{
    string Name { get; }

    /// <summary>Dimensione nativa del contenuto in pixel (puo' aggiornarsi nel tempo).</summary>
    int Width { get; }
    int Height { get; }

    /// <summary>SRV del frame corrente, o null se non c'e' ancora un frame.</summary>
    ID3D11ShaderResourceView? GetSrv();
}
