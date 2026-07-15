using System.Collections.ObjectModel;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace StreamingDoc.App.Audio;

/// <summary>
/// Mixer audio sempre attivo: somma una lista dinamica di canali (Desktop + Microfono fissi, piu'
/// eventuali canali di sorgenti) in stereo 48kHz ed emette blocchi da 10ms via <see cref="MixedAvailable"/>.
/// Cadenza data da un clock (silenzio se gli input sono vuoti) per non avere gap A/V.
/// </summary>
public sealed class AudioMixer : IDisposable
{
    public const int Rate = 48000;
    private const int Block = 480; // 10 ms

    public AudioInput Desktop { get; }
    public AudioInput? Mic { get; }

    /// <summary>Canali del mixer (per il binding UI). Modificata solo dal thread UI.</summary>
    public ObservableCollection<AudioInput> Channels { get; } = new();

    /// <summary>(blocco interleaved stereo riusato, n frame). Da consumare sincrono.</summary>
    public event Action<float[], int>? MixedAvailable;

    private readonly Thread _thread;
    private volatile bool _running = true;
    private readonly object _channelsLock = new();
    private AudioInput[] _snapshot = Array.Empty<AudioInput>();

    public AudioMixer()
    {
        Desktop = new AudioInput("Desktop", new WasapiLoopbackCapture());
        Channels.Add(Desktop);
        try { Mic = new AudioInput("Microfono", new WasapiCapture()); Channels.Add(Mic); }
        catch { Mic = null; }
        RebuildSnapshot();

        _thread = new Thread(Loop) { IsBackground = true, Name = "AudioMixer" };
        _thread.Start();
    }

    /// <summary>Aggiunge un canale (es. audio di una Media Source). Da chiamare sul thread UI.</summary>
    public void AddChannel(AudioInput ch) { Channels.Add(ch); RebuildSnapshot(); }

    /// <summary>Rimuove e dispone un canale di sorgente. Da chiamare sul thread UI.</summary>
    public void RemoveChannel(AudioInput ch)
    {
        if (!Channels.Remove(ch)) return;
        RebuildSnapshot();
        ch.Dispose();
    }

    private void RebuildSnapshot()
    {
        lock (_channelsLock) _snapshot = Channels.ToArray();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        double emitted = 0;
        var tmp = new float[Block * 2];
        var mix = new float[Block * 2];

        while (_running)
        {
            double due = sw.Elapsed.TotalSeconds * Rate;
            while (emitted < due)
            {
                AudioInput[] chans;
                lock (_channelsLock) chans = _snapshot;

                Array.Clear(mix, 0, mix.Length);
                foreach (var ch in chans)
                {
                    ch.Pull(tmp, Block);
                    for (int i = 0; i < Block * 2; i++) mix[i] += tmp[i];
                }
                for (int i = 0; i < Block * 2; i++)
                    mix[i] = mix[i] > 1f ? 1f : (mix[i] < -1f ? -1f : mix[i]);

                MixedAvailable?.Invoke(mix, Block);
                emitted += Block;
            }
            Thread.Sleep(2);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(2000);
        foreach (var ch in Channels) ch.Dispose();
    }
}
