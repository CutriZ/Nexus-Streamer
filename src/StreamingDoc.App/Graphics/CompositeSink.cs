namespace StreamingDoc.App.Graphics;

/// <summary>Inoltra ogni frame a piu' sink (es. registrazione + streaming simultanei).</summary>
public sealed class CompositeSink : IFrameSink
{
    private readonly object _lock = new();
    private IFrameSink[] _sinks = Array.Empty<IFrameSink>();

    public int Count { get { lock (_lock) return _sinks.Length; } }

    public void Add(IFrameSink sink)
    {
        lock (_lock) _sinks = _sinks.Append(sink).ToArray();
    }

    public void Remove(IFrameSink sink)
    {
        lock (_lock) _sinks = _sinks.Where(s => s != sink).ToArray();
    }

    public void WriteFrame(IntPtr data, int rowPitch, int width, int height, long timestamp100ns)
    {
        var sinks = _sinks; // snapshot atomico del riferimento
        foreach (var s in sinks)
            s.WriteFrame(data, rowPitch, width, height, timestamp100ns);
    }

    // NV12 conviene solo se TUTTI i sink lo vogliono (altrimenti il compositor usa il path BGRA).
    public bool WantsNv12
    {
        get { var s = _sinks; return s.Length > 0 && s.All(x => x.WantsNv12); }
    }

    public void WriteFrameNv12(IntPtr y, int yStride, IntPtr uv, int uvStride, int width, int height, long timestamp100ns)
    {
        var sinks = _sinks;
        foreach (var s in sinks)
            s.WriteFrameNv12(y, yStride, uv, uvStride, width, height, timestamp100ns);
    }

    public void Dispose() { /* i sink figli hanno lifecycle esterno */ }
}
