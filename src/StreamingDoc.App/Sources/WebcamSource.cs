using FFmpeg.AutoGen;
using StreamingDoc.App.Audio;
using StreamingDoc.App.Graphics;
using StreamingDoc.App.Recording;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using ID3D11ShaderResourceView = Vortice.Direct3D11.ID3D11ShaderResourceView;

namespace StreamingDoc.App.Sources;

/// <summary>
/// Webcam / capture device (incl. playout: Mainlevel ecc.) via FFmpeg dshow: video -> texture D3D11
/// (BGRA via swscale). Opzionale: audio da un SECONDO device dshow (es. "Mainlevel AudioSource 1")
/// aperto nello stesso grafo (video=...:audio=...) e inviato al mixer. Lettura su thread dedicato.
/// </summary>
public sealed unsafe class WebcamSource : ISource, IAudioSource
{
    private readonly GraphicsDevice _gd;
    private readonly Thread _thread;
    private volatile bool _running = true;

    private AVFormatContext* _fmt;
    private AVCodecContext* _dec;
    private SwsContext* _sws;
    private int _vidx = -1;

    private AVCodecContext* _adec;
    private SwrContext* _swr;
    private int _aidx = -1;

    private ID3D11Texture2D? _tex;
    private ID3D11ShaderResourceView? _srv;
    private int _texW, _texH;

    public string Name { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Canale audio del device (null se nessun audio personalizzato). Aggiunto al mixer dal chiamante.</summary>
    public AudioInput? AudioChannel { get; }

    public WebcamSource(GraphicsDevice gd, string deviceName, string? audioDeviceName = null)
    {
        FfmpegLoader.EnsureLoaded();
        _gd = gd;
        Name = "Cattura: " + deviceName;

        var ifmt = ffmpeg.av_find_input_format("dshow");
        if (ifmt == null) throw new InvalidOperationException("Cattura video non disponibile su questo sistema.");

        // rtbufsize alto: i device pro (capture card/playout) buttano molti dati, evita overflow in apertura.
        AVDictionary* opts = null;
        ffmpeg.av_dict_set(&opts, "rtbufsize", "256M", 0);

        // Apertura combinata video+audio nello stesso grafo dshow. Se fallisce, ripiega su solo video.
        string combined = string.IsNullOrEmpty(audioDeviceName)
            ? "video=" + deviceName
            : $"video={deviceName}:audio={audioDeviceName}";

        AVFormatContext* fmt = null;
        int ret = ffmpeg.avformat_open_input(&fmt, combined, ifmt, &opts);
        if (ret < 0 && !string.IsNullOrEmpty(audioDeviceName))
        {
            // Audio non agganciabile: riprova solo video così almeno l'immagine c'è.
            if (fmt != null) { var f = fmt; ffmpeg.avformat_close_input(&f); fmt = null; }
            ffmpeg.av_dict_free(&opts);
            opts = null; ffmpeg.av_dict_set(&opts, "rtbufsize", "256M", 0);
            ret = ffmpeg.avformat_open_input(&fmt, "video=" + deviceName, ifmt, &opts);
        }
        ffmpeg.av_dict_free(&opts);
        if (ret < 0)
        {
            var b = stackalloc byte[256];
            ffmpeg.av_strerror(ret, b, 256);
            string err = new string((sbyte*)b);
            throw new InvalidOperationException($"Apertura '{deviceName}' fallita: {err} (codice {ret})");
        }
        _fmt = fmt;

        if (ffmpeg.avformat_find_stream_info(_fmt, null) < 0)
            throw new InvalidOperationException("find_stream_info fallito.");

        for (int i = 0; i < (int)_fmt->nb_streams; i++)
        {
            var type = _fmt->streams[i]->codecpar->codec_type;
            if (type == AVMediaType.AVMEDIA_TYPE_VIDEO && _vidx < 0) _vidx = i;
            else if (type == AVMediaType.AVMEDIA_TYPE_AUDIO && _aidx < 0) _aidx = i;
        }
        if (_vidx < 0) throw new InvalidOperationException("Nessuno stream video dal device.");

        var par = _fmt->streams[_vidx]->codecpar;
        Width = par->width;
        Height = par->height;

        var dec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (dec == null) throw new InvalidOperationException("Decoder webcam non trovato.");
        _dec = ffmpeg.avcodec_alloc_context3(dec);
        ffmpeg.avcodec_parameters_to_context(_dec, par);
        if (ffmpeg.avcodec_open2(_dec, dec, null) < 0)
            throw new InvalidOperationException("avcodec_open2 (webcam) fallito.");

        if (_aidx >= 0) AudioChannel = OpenAudio(deviceName);

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Webcam" };
        _thread.Start();
    }

    private AudioInput? OpenAudio(string label)
    {
        var par = _fmt->streams[_aidx]->codecpar;
        var dec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (dec == null) { _aidx = -1; return null; }
        _adec = ffmpeg.avcodec_alloc_context3(dec);
        ffmpeg.avcodec_parameters_to_context(_adec, par);
        if (ffmpeg.avcodec_open2(_adec, dec, null) < 0)
        {
            fixed (AVCodecContext** p = &_adec) ffmpeg.avcodec_free_context(p);
            _aidx = -1; return null;
        }

        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, 2);
        SwrContext* swr = null;
        ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, AudioInput.Rate,
            &_adec->ch_layout, _adec->sample_fmt, _adec->sample_rate, 0, null);
        if (swr == null || ffmpeg.swr_init(swr) < 0) { _aidx = -1; return null; }
        _swr = swr;

        return new AudioInput(label, AudioInput.Rate, 2) { Removable = true };
    }

    public ID3D11ShaderResourceView? GetSrv() => _srv;

    private void CaptureLoop()
    {
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            while (_running)
            {
                if (ffmpeg.av_read_frame(_fmt, pkt) < 0) break;
                if (pkt->stream_index == _vidx && ffmpeg.avcodec_send_packet(_dec, pkt) >= 0)
                {
                    while (ffmpeg.avcodec_receive_frame(_dec, frame) >= 0)
                    {
                        Upload(frame);
                        ffmpeg.av_frame_unref(frame);
                    }
                }
                else if (pkt->stream_index == _aidx && _adec != null && ffmpeg.avcodec_send_packet(_adec, pkt) >= 0)
                {
                    while (ffmpeg.avcodec_receive_frame(_adec, frame) >= 0)
                    {
                        PushAudio(frame);
                        ffmpeg.av_frame_unref(frame);
                    }
                }
                ffmpeg.av_packet_unref(pkt);
            }
        }
        catch { /* device staccato: termina il loop */ }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&pkt);
        }
    }

    private void PushAudio(AVFrame* f)
    {
        if (_swr == null || AudioChannel is null || f->nb_samples <= 0) return;
        long outMax = ffmpeg.av_rescale_rnd(
            ffmpeg.swr_get_delay(_swr, f->sample_rate) + f->nb_samples,
            AudioInput.Rate, f->sample_rate == 0 ? AudioInput.Rate : f->sample_rate, AVRounding.AV_ROUND_UP);
        if (outMax <= 0) return;

        var buf = new float[outMax * 2];
        fixed (float* pb = buf)
        {
            byte** outPlanes = stackalloc byte*[1];
            outPlanes[0] = (byte*)pb;
            int got = ffmpeg.swr_convert(_swr, outPlanes, (int)outMax, f->extended_data, f->nb_samples);
            if (got > 0) AudioChannel.PushInterleaved(buf, got);
        }
    }

    private void Upload(AVFrame* frame)
    {
        int w = frame->width, h = frame->height;
        if (w <= 0 || h <= 0) return;
        Width = w; Height = h;

        lock (_gd.ContextLock)
        {
            if (!_running) return;
            EnsureTarget(w, h, (AVPixelFormat)frame->format);

            var map = _gd.Context.Map(_tex!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var dst = new byte*[] { (byte*)map.DataPointer, null, null, null };
                var dstStride = new[] { (int)map.RowPitch, 0, 0, 0 };
                var src = new byte*[] { frame->data[0], frame->data[1], frame->data[2], frame->data[3] };
                var srcStride = new[] { frame->linesize[0], frame->linesize[1], frame->linesize[2], frame->linesize[3] };
                ffmpeg.sws_scale(_sws, src, srcStride, 0, h, dst, dstStride);
            }
            finally { _gd.Context.Unmap(_tex!, 0); }
        }
    }

    private void EnsureTarget(int w, int h, AVPixelFormat srcFmt)
    {
        if (_tex is not null && _texW == w && _texH == h) return;

        _srv?.Dispose();
        _tex?.Dispose();
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }

        var desc = new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Dynamic, CpuAccessFlags.Write);
        _tex = _gd.Device.CreateTexture2D(desc);
        _srv = _gd.Device.CreateShaderResourceView(_tex);
        _texW = w; _texH = h;

        _sws = ffmpeg.sws_getContext(w, h, srcFmt, w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
            ffmpeg.SWS_BILINEAR, null, null, null);
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join(1000);

        lock (_gd.ContextLock)
        {
            _srv?.Dispose();
            _tex?.Dispose();
            if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
            if (_swr != null) { fixed (SwrContext** p = &_swr) ffmpeg.swr_free(p); }
            if (_adec != null) { fixed (AVCodecContext** p = &_adec) ffmpeg.avcodec_free_context(p); }
            fixed (AVCodecContext** p = &_dec) ffmpeg.avcodec_free_context(p);
            fixed (AVFormatContext** p = &_fmt) ffmpeg.avformat_close_input(p);
        }
        // AudioChannel rimosso/disposto dal mixer (come Media/NDI).
    }
}
