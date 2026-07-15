namespace StreamingDoc.App.Graphics;

/// <summary>
/// Destinazione dei frame compositati (registrazione/streaming). WriteFrame viene chiamato
/// sul thread di render mentre il canvas e' mappato in CPU: i dati sono validi solo per la
/// durata della chiamata, il sink deve copiarli subito (es. memcpy nell'AVFrame).
/// </summary>
public interface IFrameSink : IDisposable
{
    /// <param name="data">Puntatore ai pixel BGRA (B8G8R8A8) del canvas.</param>
    /// <param name="rowPitch">Byte per riga (stride), puo' essere maggiore di width*4.</param>
    /// <param name="timestamp100ns">Timestamp del frame in unita' da 100ns dall'inizio.</param>
    void WriteFrame(IntPtr data, int rowPitch, int width, int height, long timestamp100ns);

    /// <summary>True se il sink preferisce frame NV12 (conversione colore su GPU) invece di BGRA.</summary>
    bool WantsNv12 => false;

    /// <summary>
    /// Frame NV12: piano Y (yStride byte/riga, width*height) e piano UV interlacciato U,V
    /// (uvStride byte/riga, width*height/2). Validi solo per la durata della chiamata.
    /// </summary>
    void WriteFrameNv12(IntPtr y, int yStride, IntPtr uv, int uvStride, int width, int height, long timestamp100ns) { }
}
