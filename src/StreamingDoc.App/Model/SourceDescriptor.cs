namespace StreamingDoc.App.Model;

public enum SourceKind { Monitor, Window, Webcam, Image, Text, Color, Media, AudioDevice, Ndi }

/// <summary>
/// "Ricetta" per ricreare una sorgente al caricamento del progetto. Indipendente dall'ISource vivo.
/// </summary>
public sealed class SourceDescriptor
{
    public SourceKind Kind { get; set; }

    /// <summary>Monitor=device name, Window=titolo, Webcam=nome, Image=path, Text=contenuto,
    /// AudioDevice=nome friendly, Ndi=nome sorgente NDI.</summary>
    public string? Name { get; set; }

    public double FontSize { get; set; } = 96;
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public int ColorW { get; set; } = 1920;
    public int ColorH { get; set; } = 1080;

    /// <summary>AudioDevice: id endpoint WASAPI (stabile tra riavvii).</summary>
    public string? DeviceId { get; set; }

    /// <summary>AudioDevice: true = cattura l'uscita di un device di riproduzione (loopback playout).</summary>
    public bool Loopback { get; set; }

    /// <summary>Webcam/cattura: true = prendi l'audio da un device dshow separato (es. playout).</summary>
    public bool UseCustomAudio { get; set; }

    /// <summary>Webcam/cattura: nome del device audio dshow (es. "Mainlevel AudioSource 1").</summary>
    public string? AudioDeviceName { get; set; }

    public SourceDescriptor Clone() => new()
    {
        Kind = Kind, Name = Name, FontSize = FontSize,
        R = R, G = G, B = B, ColorW = ColorW, ColorH = ColorH,
        DeviceId = DeviceId, Loopback = Loopback,
        UseCustomAudio = UseCustomAudio, AudioDeviceName = AudioDeviceName,
    };
}
