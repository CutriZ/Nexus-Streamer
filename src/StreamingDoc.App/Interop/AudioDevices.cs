using NAudio.CoreAudioApi;

namespace StreamingDoc.App.Interop;

/// <summary>
/// Enumera gli endpoint audio WASAPI. I playout/regie TV (vMix, VoiceMeeter, VB-Cable, schede SDI,
/// audio NDI virtuale) registrano device virtuali qui: cattura (recording) o riproduzione (loopback).
/// </summary>
public static class AudioDevices
{
    public readonly record struct Endpoint(string Id, string Name, bool Loopback);

    /// <summary>Device di acquisizione (microfoni, ingressi schede, cavi virtuali in cattura).</summary>
    public static List<Endpoint> CaptureDevices() => Enumerate(DataFlow.Capture, loopback: false);

    /// <summary>Device di riproduzione: catturabili in loopback (uscita playout, monitor, cavi virtuali).</summary>
    public static List<Endpoint> RenderDevices() => Enumerate(DataFlow.Render, loopback: true);

    private static List<Endpoint> Enumerate(DataFlow flow, bool loopback)
    {
        var list = new List<Endpoint>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                try { list.Add(new Endpoint(d.ID, d.FriendlyName, loopback)); }
                finally { d.Dispose(); }
            }
        }
        catch { /* WASAPI non disponibile: lista vuota */ }
        return list;
    }

    /// <summary>Risolve un device per id; null se non più presente (device staccato).</summary>
    public static MMDevice? FindById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            {
                if (d.ID == id) return d;
                d.Dispose();
            }
        }
        catch { }
        return null;
    }
}
