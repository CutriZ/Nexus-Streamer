using System.ComponentModel;
using System.Runtime.CompilerServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace StreamingDoc.App.Audio;

/// <summary>
/// Un canale audio del mixer (loopback desktop, microfono, o audio di una sorgente). Converte i
/// campioni in float stereo a 48kHz (resample lineare) in un ring buffer. Gain/mute applicati al
/// prelievo; Peak per il VU. Implementa INotifyPropertyChanged per il binding della strip nel mixer.
/// </summary>
public sealed class AudioInput : INotifyPropertyChanged, IDisposable
{
    public const int Rate = 48000;

    public string Name { get; }
    public bool Active { get; private set; }

    public float Peak;               // ultimo picco post-gain (scritto dal thread audio)

    public const double MinFaderDb = -60.0, MaxFaderDb = 0.0; // 0 dB = unità (come l'asse del VU)

    private float _gain = 1f;
    public float Gain                // 0..1 lineare (0 dB = 1.0)
    {
        get => _gain;
        set
        {
            value = Math.Clamp(value, 0f, 1f);
            if (_gain != value) { _gain = value; OnChanged(); OnChanged(nameof(GainDb)); OnChanged(nameof(GainDbText)); }
        }
    }

    /// <summary>Volume del fader in dB (-60..0). Mappa 1:1 sul livello del VU, quindi la lettura è "reale".</summary>
    public double GainDb
    {
        get => _gain <= 0.00001f ? MinFaderDb : Math.Clamp(20.0 * Math.Log10(_gain), MinFaderDb, MaxFaderDb);
        set
        {
            double db = Math.Clamp(value, MinFaderDb, MaxFaderDb);
            Gain = db <= MinFaderDb ? 0f : (float)Math.Pow(10.0, db / 20.0);
        }
    }

    /// <summary>Etichetta dB del fader (es. "0.0 dB", "-5.0 dB", "-∞ dB").</summary>
    public string GainDbText => _gain <= 0.00001f ? "-∞ dB" : $"{20.0 * Math.Log10(_gain):0.0} dB";

    private bool _muted;
    public bool Muted
    {
        get => _muted;
        set { if (_muted != value) { _muted = value; OnChanged(); } }
    }

    private double _displayPeak;
    public double DisplayPeak        // copia di Peak aggiornata dall'UI timer (per il binding VU)
    {
        get => _displayPeak;
        private set { if (_displayPeak != value) { _displayPeak = value; OnChanged(); } }
    }

    /// <summary>Da chiamare sul thread UI (timer): pubblica Peak come DisplayPeak per il VU.</summary>
    public void UpdateDisplay() => DisplayPeak = Peak;

    public bool Removable { get; init; }   // Desktop/Mic = false; canali di sorgenti = true

    private volatile bool _audible = true;
    /// <summary>false = la sorgente non è in onda (Program): non si sente e il VU resta a 0.</summary>
    public bool Audible { get => _audible; set => _audible = value; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private readonly IWaveIn? _cap;
    private readonly int _inRate, _inCh;
    private readonly bool _isFloat;
    private readonly int _bps;

    private readonly object _lock = new();
    private readonly float[] _ring;
    private int _head, _tail, _count;

    private double _frac;
    private float _lastL, _lastR;

    public AudioInput(string name, IWaveIn cap)
    {
        Name = name;
        _cap = cap;
        var wf = cap.WaveFormat;
        _inRate = wf.SampleRate;
        _inCh = Math.Max(1, wf.Channels);
        _bps = wf.BitsPerSample / 8;
        _isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat
            || (wf.Encoding == WaveFormatEncoding.Extensible && wf.BitsPerSample == 32);

        _ring = new float[Rate * 2 * 2]; // 2s stereo
        cap.DataAvailable += OnData;
        try { cap.StartRecording(); Active = true; }
        catch { Active = false; }
    }

    /// <summary>Canale in modalita' push (es. audio decodificato da una Media Source): nessun device.</summary>
    public AudioInput(string name, int inRate, int inChannels)
    {
        Name = name;
        _cap = null;
        _inRate = Math.Max(1, inRate);
        _inCh = Math.Max(1, inChannels);
        _ring = new float[Rate * 2 * 2];
        Active = true;
    }

    /// <summary>
    /// Inietta campioni float interleaved (a _inRate, _inCh canali) nel canale. Usato dalle sorgenti
    /// che decodificano il proprio audio. Thread-safe rispetto al prelievo del mixer.
    /// </summary>
    public void PushInterleaved(float[] data, int frames)
    {
        if (frames <= 0) return;
        var L = new float[frames];
        var R = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            L[f] = data[f * _inCh];
            R[f] = _inCh > 1 ? data[f * _inCh + 1] : L[f];
        }
        ResampleWrite(L, R, frames);
    }

    private float ReadSample(byte[] buf, int byteOffset)
    {
        if (_isFloat) return BitConverter.ToSingle(buf, byteOffset);
        if (_bps == 2) return BitConverter.ToInt16(buf, byteOffset) / 32768f;
        return 0f;
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int frameBytes = _bps * _inCh;
        int frames = frameBytes > 0 ? e.BytesRecorded / frameBytes : 0;
        if (frames <= 0) return;

        var L = new float[frames];
        var R = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            int off = f * frameBytes;
            float l = ReadSample(e.Buffer, off);
            float r = _inCh > 1 ? ReadSample(e.Buffer, off + _bps) : l;
            L[f] = l; R[f] = r;
        }
        ResampleWrite(L, R, frames);
    }

    // Resample lineare _inRate -> 48k con continuita' tra blocchi, poi scrive nel ring.
    private void ResampleWrite(float[] L, float[] R, int frames)
    {
        double ratio = (double)_inRate / Rate;
        double pos = _frac;
        while (pos < frames)
        {
            int i = (int)Math.Floor(pos);
            double fr = pos - i;
            float l0 = i < 0 ? _lastL : L[i];
            float r0 = i < 0 ? _lastR : R[i];
            float l1 = (i + 1) < frames ? L[i + 1] : L[frames - 1];
            float r1 = (i + 1) < frames ? R[i + 1] : R[frames - 1];
            Write((float)(l0 + (l1 - l0) * fr), (float)(r0 + (r1 - r0) * fr));
            pos += ratio;
        }
        _frac = pos - frames;
        _lastL = L[frames - 1];
        _lastR = R[frames - 1];
    }

    private void Write(float l, float r)
    {
        lock (_lock)
        {
            if (_count + 2 > _ring.Length)
            {
                _head = (_head + 2) % _ring.Length; // scarta il piu' vecchio
                _count -= 2;
            }
            _ring[_tail] = l; _tail = (_tail + 1) % _ring.Length;
            _ring[_tail] = r; _tail = (_tail + 1) % _ring.Length;
            _count += 2;
        }
    }

    /// <summary>Preleva <paramref name="frames"/> frame stereo (gain/mute applicati). Zero-fill se vuoto.</summary>
    public void Pull(float[] dst, int frames)
    {
        float g = (Muted || !_audible) ? 0f : Gain;
        float peak = 0f;
        lock (_lock)
        {
            for (int k = 0; k < frames; k++)
            {
                float l = 0f, r = 0f;
                if (_count >= 2)
                {
                    l = _ring[_head]; _head = (_head + 1) % _ring.Length;
                    r = _ring[_head]; _head = (_head + 1) % _ring.Length;
                    _count -= 2;
                }
                l *= g; r *= g;
                dst[k * 2] = l;
                dst[k * 2 + 1] = r;
                float a = Math.Max(Math.Abs(l), Math.Abs(r));
                if (a > peak) peak = a;
            }
        }
        Peak = peak;
    }

    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;     // idempotente: il canale può essere disposto da sorgente e mixer
        _disposed = true;
        if (_cap is null) return;
        try { _cap.StopRecording(); } catch { }
        _cap.DataAvailable -= OnData;
        try { _cap.Dispose(); } catch { }
    }
}
