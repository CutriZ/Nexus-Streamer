using StreamingDoc.App.Audio;
using StreamingDoc.App.Graphics;
using StreamingDoc.App.Interop.Ndi;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11ShaderResourceView = Vortice.Direct3D11.ID3D11ShaderResourceView;

namespace StreamingDoc.App.Sources;

/// <summary>
/// Riceve una sorgente NDI (regia/playout/telecamera in rete) come video (BGRA in texture D3D11) +
/// audio (canale del mixer). Cattura su thread dedicato. Richiede la runtime NDI installata.
/// </summary>
public sealed unsafe class NdiSource : ISource, IAudioSource
{
    private readonly GraphicsDevice _gd;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private IntPtr _recv;

    private ID3D11Texture2D? _tex;
    private ID3D11ShaderResourceView? _srv;
    private int _texW, _texH;

    public string Name { get; }
    public int Width { get; private set; } = 16;
    public int Height { get; private set; } = 16;

    public AudioInput? AudioChannel { get; }

    public NdiSource(GraphicsDevice gd, string ndiName)
    {
        if (!NdiLib.IsAvailable)
            throw new InvalidOperationException("Runtime NDI non installata (installa NDI Tools / NDI Runtime).");

        _gd = gd;
        Name = "NDI: " + ndiName;

        IntPtr namePtr = NdiLib.Utf8(ndiName);
        IntPtr recvName = NdiLib.Utf8("Nexus Streamer");
        try
        {
            var create = new NdiLib.RecvCreateV3
            {
                source_to_connect_to = new NdiLib.Source { p_ndi_name = namePtr, p_url_address = IntPtr.Zero },
                color_format = (int)NdiLib.RecvColorFormat.BGRX_BGRA,
                bandwidth = (int)NdiLib.RecvBandwidth.Highest,
                allow_video_fields = 0,
                p_ndi_recv_name = recvName,
            };
            _recv = NdiLib.NDIlib_recv_create_v3(ref create);
        }
        finally { NdiLib.FreeUtf8(namePtr); NdiLib.FreeUtf8(recvName); }

        if (_recv == IntPtr.Zero)
            throw new InvalidOperationException("Connessione NDI fallita: " + ndiName);

        // Canale audio push: NDI audio è quasi sempre 48k; il resample del canale assorbe differenze.
        AudioChannel = new AudioInput("NDI " + ndiName, AudioInput.Rate, 2) { Removable = true };

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "NDI" };
        _thread.Start();
    }

    public ID3D11ShaderResourceView? GetSrv() => _srv;

    private void CaptureLoop()
    {
        try
        {
            while (_running)
            {
                NdiLib.VideoFrameV2 v = default;
                NdiLib.AudioFrameV2 a = default;
                int t = NdiLib.NDIlib_recv_capture_v2(_recv, ref v, ref a, IntPtr.Zero, 200);
                switch ((NdiLib.FrameType)t)
                {
                    case NdiLib.FrameType.Video:
                        try { if (v.p_data != IntPtr.Zero) UploadVideo(ref v); }
                        finally { NdiLib.NDIlib_recv_free_video_v2(_recv, ref v); }
                        break;
                    case NdiLib.FrameType.Audio:
                        try { if (a.p_data != IntPtr.Zero) PushAudio(ref a); }
                        finally { NdiLib.NDIlib_recv_free_audio_v2(_recv, ref a); }
                        break;
                    // None/StatusChange/Metadata: nessun dato da liberare
                }
            }
        }
        catch { /* runtime/rete: termina il loop */ }
    }

    private void UploadVideo(ref NdiLib.VideoFrameV2 v)
    {
        int w = v.xres, h = v.yres;
        if (w <= 0 || h <= 0) return;
        Width = w; Height = h;

        lock (_gd.ContextLock)
        {
            if (!_running) return;
            EnsureTarget(w, h);

            var map = _gd.Context.Map(_tex!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte* src = (byte*)v.p_data;
                byte* dst = (byte*)map.DataPointer;
                int srcStride = v.line_stride_in_bytes > 0 ? v.line_stride_in_bytes : w * 4;
                int copy = Math.Min(srcStride, (int)map.RowPitch);
                for (int r = 0; r < h; r++)
                    Buffer.MemoryCopy(src + (long)r * srcStride, dst + (long)r * map.RowPitch, map.RowPitch, copy);
            }
            finally { _gd.Context.Unmap(_tex!, 0); }
        }
    }

    private void PushAudio(ref NdiLib.AudioFrameV2 a)
    {
        int frames = a.no_samples, ch = a.no_channels;
        if (frames <= 0 || ch <= 0) return;

        // NDI audio = float planare. Downmix/copia a stereo interleaved per il canale.
        var inter = new float[frames * 2];
        byte* baseP = (byte*)a.p_data;
        int stride = a.channel_stride_in_bytes > 0 ? a.channel_stride_in_bytes : frames * sizeof(float);
        float* p0 = (float*)baseP;
        float* p1 = ch > 1 ? (float*)(baseP + stride) : p0;
        for (int f = 0; f < frames; f++)
        {
            inter[f * 2] = p0[f];
            inter[f * 2 + 1] = p1[f];
        }
        AudioChannel?.PushInterleaved(inter, frames);
    }

    private void EnsureTarget(int w, int h)
    {
        if (_tex is not null && _texW == w && _texH == h) return;
        _srv?.Dispose();
        _tex?.Dispose();

        var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _tex = _gd.Device.CreateTexture2D(desc);
        _srv = _gd.Device.CreateShaderResourceView(_tex);
        _texW = w; _texH = h;
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join(1000);

        if (_recv != IntPtr.Zero) { try { NdiLib.NDIlib_recv_destroy(_recv); } catch { } _recv = IntPtr.Zero; }

        lock (_gd.ContextLock)
        {
            _srv?.Dispose();
            _tex?.Dispose();
        }
        // AudioChannel viene rimosso/disposto dal mixer (come Media): qui niente.
    }
}
