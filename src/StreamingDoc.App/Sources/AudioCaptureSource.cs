using NAudio.CoreAudioApi;
using NAudio.Wave;
using StreamingDoc.App.Audio;
using StreamingDoc.App.Interop;
using Vortice.Direct3D11;

namespace StreamingDoc.App.Sources;

/// <summary>
/// Sorgente solo-audio: cattura un endpoint WASAPI specifico (device virtuale di un playout, cavo
/// virtuale, microfono di una scheda, oppure loopback dell'uscita di un device di riproduzione) e lo
/// espone come canale del mixer. Nessun video (GetSrv = null).
/// </summary>
public sealed class AudioCaptureSource : ISource, IAudioSource
{
    public string Name { get; }
    public int Width => 0;
    public int Height => 0;

    public AudioInput? AudioChannel { get; }

    public AudioCaptureSource(string friendlyName, string deviceId, bool loopback)
    {
        Name = (loopback ? "Audio (uscita): " : "Audio: ") + friendlyName;

        var dev = AudioDevices.FindById(deviceId)
                  ?? throw new InvalidOperationException("Device audio non trovato: " + friendlyName);
        try
        {
            IWaveIn cap = loopback ? new WasapiLoopbackCapture(dev) : new WasapiCapture(dev);
            AudioChannel = new AudioInput(friendlyName, cap) { Removable = true };
        }
        finally { dev.Dispose(); }
    }

    public ID3D11ShaderResourceView? GetSrv() => null;

    // Il canale è rimosso/disposto dal mixer (come Media/NDI): qui niente.
    public void Dispose() { }
}
