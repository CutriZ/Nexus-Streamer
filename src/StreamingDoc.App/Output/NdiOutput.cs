using System.Collections.Concurrent;
using StreamingDoc.App.Audio;
using StreamingDoc.App.Graphics;
using StreamingDoc.App.Interop.Ndi;

namespace StreamingDoc.App.Output;

/// <summary>
/// Pubblica l'output Program come sorgente NDI (video BGRA + audio dal mixer), ricevibile da
/// vMix/TriCaster/OBS/altre regie. Implementa <see cref="IFrameSink"/>: il compositor consegna i
/// frame BGRA (WantsNv12=false). Invio video su thread dedicato per non bloccare il render.
/// </summary>
public sealed unsafe class NdiOutput : IFrameSink
{
    private readonly int _width, _height, _fps;
    private readonly AudioMixer? _mixer;
    private IntPtr _send;

    private readonly BlockingCollection<(byte[] buf, int w, int h)> _queue = new(boundedCapacity: 3);
    private readonly ConcurrentQueue<byte[]> _pool = new();
    private readonly Thread _sendThread;
    private volatile bool _closing;

    private readonly object _audioLife = new();
    private bool _disposed;
    private Action<float[], int>? _audioHandler;
    private float[] _planar = Array.Empty<float>();

    public bool Failed { get; private set; }

    public NdiOutput(string ndiName, int width, int height, int fps, AudioMixer? mixer)
    {
        if (!NdiLib.IsAvailable)
            throw new InvalidOperationException("Runtime NDI non installata (installa NDI Tools / NDI Runtime).");

        _width = width; _height = height; _fps = fps; _mixer = mixer;

        IntPtr namePtr = NdiLib.Utf8(ndiName);
        try
        {
            var create = new NdiLib.SendCreate
            {
                p_ndi_name = namePtr, p_groups = IntPtr.Zero,
                clock_video = 0, clock_audio = 0, // pacing già fatto dal compositor/mixer
            };
            _send = NdiLib.NDIlib_send_create(ref create);
        }
        finally { NdiLib.FreeUtf8(namePtr); }

        if (_send == IntPtr.Zero)
            throw new InvalidOperationException("Creazione uscita NDI fallita.");

        _sendThread = new Thread(SendLoop) { IsBackground = true, Name = "NDI-Out", Priority = ThreadPriority.AboveNormal };
        _sendThread.Start();

        if (_mixer is not null)
        {
            _audioHandler = (buf, frames) => SendAudio(buf, frames);
            _mixer.MixedAvailable += _audioHandler;
        }
    }

    public bool WantsNv12 => false; // NDI invia BGRA

    // --- Render thread: copia compatta BGRA + accodamento (drop se in ritardo) ---
    public void WriteFrame(IntPtr data, int rowPitch, int width, int height, long timestamp100ns)
    {
        if (_closing || width != _width || height != _height) return;

        if (!_pool.TryDequeue(out var buf)) buf = new byte[_width * _height * 4];
        int dstStride = _width * 4;
        byte* src = (byte*)data;
        fixed (byte* dst = buf)
        {
            for (int y = 0; y < _height; y++)
                Buffer.MemoryCopy(src + (long)y * rowPitch, dst + (long)y * dstStride, dstStride, dstStride);
        }
        try { if (!_queue.TryAdd((buf, width, height))) _pool.Enqueue(buf); }
        catch (InvalidOperationException) { /* in chiusura */ }
    }

    public void WriteFrameNv12(IntPtr y, int yStride, IntPtr uv, int uvStride, int width, int height, long timestamp100ns)
    { /* non usato: WantsNv12=false */ }

    private void SendLoop()
    {
        try
        {
            foreach (var (buf, w, h) in _queue.GetConsumingEnumerable())
            {
                fixed (byte* p = buf)
                {
                    var v = new NdiLib.VideoFrameV2
                    {
                        xres = w, yres = h,
                        FourCC = NdiLib.FourCC_BGRA,
                        frame_rate_N = _fps, frame_rate_D = 1,
                        picture_aspect_ratio = 0f, // 0 = dedotto da xres/yres
                        frame_format_type = (int)NdiLib.FrameFormat.Progressive,
                        timecode = long.MinValue,  // NDIlib_send_timecode_synthesize
                        p_data = (IntPtr)p,
                        line_stride_in_bytes = w * 4,
                    };
                    NdiLib.NDIlib_send_send_video_v2(_send, ref v); // sincrono: copia/comprime prima di tornare
                }
                _pool.Enqueue(buf);
            }
        }
        catch { Failed = true; }
    }

    // --- Mixer thread: audio 48k stereo interleaved -> planare NDI ---
    private void SendAudio(float[] interleaved, int frames)
    {
        lock (_audioLife)
        {
            if (_disposed || frames <= 0) return;
            int need = frames * 2;
            if (_planar.Length < need) _planar = new float[need];

            // deinterleave: [L0,R0,L1,R1,...] -> [L0,L1,... | R0,R1,...]
            for (int f = 0; f < frames; f++)
            {
                _planar[f] = interleaved[f * 2];
                _planar[frames + f] = interleaved[f * 2 + 1];
            }
            fixed (float* p = _planar)
            {
                var a = new NdiLib.AudioFrameV2
                {
                    sample_rate = AudioMixer.Rate,
                    no_channels = 2,
                    no_samples = frames,
                    timecode = long.MinValue,
                    p_data = (IntPtr)p,
                    channel_stride_in_bytes = frames * sizeof(float),
                };
                try { NdiLib.NDIlib_send_send_audio_v2(_send, ref a); } catch { Failed = true; }
            }
        }
    }

    public void Dispose()
    {
        _closing = true;
        _queue.CompleteAdding();
        if (_sendThread.IsAlive) _sendThread.Join(1000);

        if (_audioHandler is not null && _mixer is not null) _mixer.MixedAvailable -= _audioHandler;

        lock (_audioLife) // attende una SendAudio in corso prima di distruggere il sender
        {
            _disposed = true;
            if (_send != IntPtr.Zero) { try { NdiLib.NDIlib_send_destroy(_send); } catch { } _send = IntPtr.Zero; }
        }
    }
}
