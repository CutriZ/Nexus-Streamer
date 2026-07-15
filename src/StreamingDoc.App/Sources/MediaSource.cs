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
/// Sorgente media (file video/audio) via FFmpeg: decodifica il video in una texture D3D11 (BGRA via
/// swscale) e l'audio in un canale del mixer (swresample -> float stereo 48kHz). Riproduce in tempo
/// reale (pacing sul PTS) e va in loop a fine file. Lettura/decodifica su thread dedicato.
/// </summary>
public sealed unsafe class MediaSource : ISource, IAudioSource
{
    private readonly GraphicsDevice _gd;
    private readonly Thread _thread;
    private volatile bool _running = true;

    private AVFormatContext* _fmt;
    private AVCodecContext* _vdec;
    private SwsContext* _sws;
    private int _vidx = -1;
    private AVRational _vtb;

    private AVCodecContext* _adec;
    private SwrContext* _swr;
    private int _aidx = -1;
    private AVRational _atb;

    private ID3D11Texture2D? _tex;
    private ID3D11ShaderResourceView? _srv;
    private int _texW, _texH;

    private readonly System.Diagnostics.Stopwatch _clock = new();
    private long _vFirst = ffmpeg.AV_NOPTS_VALUE, _aFirst = ffmpeg.AV_NOPTS_VALUE;

    private readonly bool _isUrl;

    public string Name { get; }
    public int Width { get; private set; } = 16;
    public int Height { get; private set; } = 16;

    /// <summary>Canale audio del file (null se il file non ha audio). Va aggiunto al mixer dal chiamante.</summary>
    public AudioInput? AudioChannel { get; }

    public MediaSource(GraphicsDevice gd, string path)
    {
        FfmpegLoader.EnsureLoaded();
        _gd = gd;
        _isUrl = path.Contains("://"); // http/https (m3u8 HLS), rtmp, rtsp, udp...
        Name = _isUrl ? "Stream: " + ShortUrl(path) : "Media: " + System.IO.Path.GetFileName(path);

        AVDictionary* opts = null;
        if (_isUrl)
        {
            ffmpeg.avformat_network_init();
            ffmpeg.av_dict_set(&opts, "rw_timeout", "10000000", 0);  // 10s, evita hang infinito
            ffmpeg.av_dict_set(&opts, "reconnect", "1", 0);          // riconnessione HTTP/HLS
            ffmpeg.av_dict_set(&opts, "reconnect_streamed", "1", 0);
            ffmpeg.av_dict_set(&opts, "reconnect_delay_max", "5", 0);
            ffmpeg.av_dict_set(&opts, "rtmp_live", "live", 0);
        }

        AVFormatContext* fmt = null;
        int openRet = ffmpeg.avformat_open_input(&fmt, path, null, &opts);
        ffmpeg.av_dict_free(&opts);
        if (openRet < 0)
            throw new InvalidOperationException(_isUrl ? "Apertura flusso fallita: " + path : "Apertura file fallita: " + path);
        _fmt = fmt;
        if (ffmpeg.avformat_find_stream_info(_fmt, null) < 0)
            throw new InvalidOperationException("find_stream_info fallito.");

        for (int i = 0; i < (int)_fmt->nb_streams; i++)
        {
            var t = _fmt->streams[i]->codecpar->codec_type;
            if (t == AVMediaType.AVMEDIA_TYPE_VIDEO && _vidx < 0) _vidx = i;
            else if (t == AVMediaType.AVMEDIA_TYPE_AUDIO && _aidx < 0) _aidx = i;
        }
        if (_vidx < 0 && _aidx < 0)
            throw new InvalidOperationException("Nessuno stream audio/video nel file.");

        if (_vidx >= 0) OpenVideo();
        if (_aidx >= 0) AudioChannel = OpenAudio(System.IO.Path.GetFileName(path));

        _thread = new Thread(DecodeLoop) { IsBackground = true, Name = "Media" };
        _thread.Start();
    }

    private void OpenVideo()
    {
        var par = _fmt->streams[_vidx]->codecpar;
        _vtb = _fmt->streams[_vidx]->time_base;
        Width = par->width; Height = par->height;
        var dec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (dec == null) { _vidx = -1; return; }
        _vdec = ffmpeg.avcodec_alloc_context3(dec);
        ffmpeg.avcodec_parameters_to_context(_vdec, par);
        if (ffmpeg.avcodec_open2(_vdec, dec, null) < 0) { fixed (AVCodecContext** p = &_vdec) ffmpeg.avcodec_free_context(p); _vidx = -1; }
    }

    private AudioInput? OpenAudio(string label)
    {
        var par = _fmt->streams[_aidx]->codecpar;
        _atb = _fmt->streams[_aidx]->time_base;
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

    private static string ShortUrl(string url)
        => url.Length <= 44 ? url : "…" + url.Substring(url.Length - 43);

    public ID3D11ShaderResourceView? GetSrv() => _srv;

    private void DecodeLoop()
    {
        var pkt = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            while (_running)
            {
                int ret = ffmpeg.av_read_frame(_fmt, pkt);
                if (ret < 0)
                {
                    if (_isUrl) break; // flusso live terminato/disconnesso: stop (niente loop)
                    // Fine file: loop dall'inizio.
                    ffmpeg.av_seek_frame(_fmt, -1, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                    if (_vdec != null) ffmpeg.avcodec_flush_buffers(_vdec);
                    if (_adec != null) ffmpeg.avcodec_flush_buffers(_adec);
                    _clock.Reset();
                    _vFirst = _aFirst = ffmpeg.AV_NOPTS_VALUE;
                    continue;
                }

                if (pkt->stream_index == _vidx && ffmpeg.avcodec_send_packet(_vdec, pkt) >= 0)
                {
                    while (ffmpeg.avcodec_receive_frame(_vdec, frame) >= 0)
                    {
                        Pace(frame->best_effort_timestamp, _vtb, ref _vFirst);
                        Upload(frame);
                        ffmpeg.av_frame_unref(frame);
                    }
                }
                else if (pkt->stream_index == _aidx && ffmpeg.avcodec_send_packet(_adec, pkt) >= 0)
                {
                    while (ffmpeg.avcodec_receive_frame(_adec, frame) >= 0)
                    {
                        PushAudio(frame);
                        if (_vidx < 0) Pace(frame->best_effort_timestamp, _atb, ref _aFirst); // audio-only: pace sull'audio
                        ffmpeg.av_frame_unref(frame);
                    }
                }
                ffmpeg.av_packet_unref(pkt);
            }
        }
        catch { /* fine */ }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&pkt);
        }
    }

    // Attende fino al tempo di presentazione del frame (PTS) rispetto al clock di riproduzione.
    private void Pace(long pts, AVRational tb, ref long first)
    {
        if (pts == ffmpeg.AV_NOPTS_VALUE) return;
        if (!_clock.IsRunning) _clock.Start();
        if (first == ffmpeg.AV_NOPTS_VALUE) { first = pts; return; }
        double tSec = (pts - first) * ffmpeg.av_q2d(tb);
        double waitMs = tSec * 1000 - _clock.Elapsed.TotalMilliseconds;
        if (waitMs > 1 && waitMs < 5000)
        {
            int slept = 0;
            while (_running && slept < waitMs) { Thread.Sleep(5); slept += 5; }
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
            byte* outp = (byte*)pb;
            byte** outPlanes = stackalloc byte*[1];
            outPlanes[0] = outp;
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
            if (_vdec != null) { fixed (AVCodecContext** p = &_vdec) ffmpeg.avcodec_free_context(p); }
            if (_adec != null) { fixed (AVCodecContext** p = &_adec) ffmpeg.avcodec_free_context(p); }
            fixed (AVFormatContext** p = &_fmt) ffmpeg.avformat_close_input(p);
        }
        // Il canale audio viene rimosso/disposto dal mixer (RemoveChannel).
    }
}
