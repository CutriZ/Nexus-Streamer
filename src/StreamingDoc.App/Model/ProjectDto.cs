namespace StreamingDoc.App.Model;

/// <summary>DTO serializzabili (System.Text.Json) per salvare/caricare un progetto.</summary>
public sealed class ProjectDto
{
    public int CanvasWidth { get; set; } = 1920;
    public int CanvasHeight { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    public long RecordBitrate { get; set; } = 10_000_000;
    public long StreamBitrate { get; set; } = 6_000_000;
    public string RecordingFolder { get; set; } = "";
    public string RecordingFormat { get; set; } = "mp4";
    public string Encoder { get; set; } = "auto"; // auto | h264_nvenc | libx264
    public string StreamProtocol { get; set; } = "rtmp"; // none | rtmp | srt
    public string StreamUrl { get; set; } = "rtmp://localhost/live";
    public string StreamKey { get; set; } = "";
    public string StreamUser { get; set; } = "";
    public string StreamPass { get; set; } = "";
    public bool StreamUseAuth { get; set; }
    // SRT
    public string SrtHost { get; set; } = "";
    public int SrtPort { get; set; } = 9000;
    public string SrtMode { get; set; } = "caller"; // caller | listener
    public int SrtLatencyMs { get; set; } = 200;
    public string SrtUrl { get; set; } = ""; // se valorizzato, usato tale e quale (streamid/token/transtype)
    // SCTE-35 (ad marker, solo SRT/MPEG-TS)
    public bool ScteEnabled { get; set; }
    public string SctePlayoutFile { get; set; } = "";
    public List<SceneDto> Scenes { get; set; } = new();
}

public sealed class SceneDto
{
    public string Name { get; set; } = "Scena";
    public List<ItemDto> Items { get; set; } = new();
}

public sealed class ItemDto
{
    public SourceDescriptor Source { get; set; } = new();
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public double Opacity { get; set; } = 1.0;
    public bool ChromaKey { get; set; }
    public double ChromaR { get; set; }
    public double ChromaG { get; set; } = 1.0;
    public double ChromaB { get; set; }
    public double ChromaSimilarity { get; set; } = 0.4;
    public double ChromaSmoothness { get; set; } = 0.1;
}
