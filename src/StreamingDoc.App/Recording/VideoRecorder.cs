using System.Collections.Concurrent;
using FFmpeg.AutoGen;
using StreamingDoc.App.Audio;
using StreamingDoc.App.Graphics;

namespace StreamingDoc.App.Recording;

/// <summary>
/// Registra i frame del compositor (BGRA) su file via FFmpeg. L'encoding gira su un thread
/// dedicato: WriteFrame copia i pixel e accoda; se l'encoder e' in ritardo i frame vengono scartati.
/// Codec: h264_nvenc se disponibile, altrimenti libx264.
/// </summary>
public sealed unsafe class VideoRecorder : IFrameSink
{
    private readonly int _width, _height, _fps;
    private readonly long _ticksPerFrame;
    private readonly string? _format;
    private readonly AudioMixer? _mixer;
    private readonly object _muxLock = new();
    private Action<float[], int>? _audioHandler;

    private AVFormatContext* _fmt;
    private AVStream* _stream;
    private TsScteInjector? _scteInjector; // iniezione SCTE-35 a livello TS (solo MPEG-TS)
    private AVCodecContext* _codec;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwsContext* _sws;
    private AudioEncoder? _audio;

    private readonly BlockingCollection<(byte[] buf, long pts, bool nv12)> _queue = new(boundedCapacity: 8);
    private readonly ConcurrentQueue<byte[]> _pool = new();
    private readonly Thread _encodeThread;
    private long _lastEnqueuedPts = -1;
    private long _lastEncodedPts = -1;
    private long _baseTicks = -1;
    private volatile bool _failed;
    private volatile bool _closing; // Dispose in corso: i write thread smettono di accodare

    public string OutputPath { get; }
    public string CodecName { get; private set; } = "?";
    public string? AudioError { get; private set; }
    public bool Failed => _failed;
    /// <summary>true se l'iniezione SCTE-35 e' attiva (output MPEG-TS con enableScte).</summary>
    public bool ScteReady => _scteInjector != null;

    private readonly string _encoder;
    private readonly bool _enableScte;

    /// <param name="format">Nome muxer FFmpeg (es. "flv" per RTMP, "mpegts" per SRT). null = dedotto dall'estensione.</param>
    /// <param name="mixer">Se non null aggiunge uno stream AAC alimentato dal mixer audio.</param>
    /// <param name="encoder">"auto" | "h264_nvenc" | "libx264".</param>
    /// <param name="enableScte">Se true (solo MPEG-TS) aggiunge uno stream dati SCTE-35 per gli ad marker.</param>
    public VideoRecorder(string outputPath, int width, int height, int fps = 30, long bitRate = 10_000_000,
        string? format = null, AudioMixer? mixer = null, string encoder = "auto", bool enableScte = false)
    {
        FfmpegLoader.EnsureLoaded();
        OutputPath = outputPath;
        _width = width;
        _height = height;
        _fps = fps;
        _format = format;
        _mixer = mixer;
        _encoder = encoder;
        _enableScte = enableScte;
        _ticksPerFrame = 10_000_000L / fps;

        Init(bitRate);

        _encodeThread = new Thread(EncodeLoop) { IsBackground = true, Name = "Encoder", Priority = ThreadPriority.AboveNormal };
        _encodeThread.Start();
    }

    private void Init(long bitRate)
    {
        AVFormatContext* fmt;
        Check(ffmpeg.avformat_alloc_output_context2(&fmt, null, _format, OutputPath), "alloc_output_context");
        _fmt = fmt;

        // Prova ad aprire davvero ogni encoder: h264_nvenc puo' esistere ma fallire l'open senza GPU NVIDIA.
        if (!OpenAnyCodec(bitRate))
            throw new InvalidOperationException("Nessun encoder H.264 apribile (provati h264_nvenc, libx264).");

        _stream = ffmpeg.avformat_new_stream(_fmt, null);
        Check(ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _codec), "parameters_from_context");
        _stream->time_base = _codec->time_base;

        // Lo stream audio va aggiunto PRIMA di write_header.
        if (_mixer is not null)
        {
            try { _audio = new AudioEncoder(_fmt, _muxLock); }
            catch (Exception ex) { _audio = null; AudioError = ex.Message; /* prosegui solo video */ }
        }

        // Uscita: con SCTE-35 avvolgiamo l'output con un AVIOContext custom (iniezione a livello TS,
        // perche' il muxer FFmpeg non genera SCTE-35). Altrimenti apertura standard.
        if ((_fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
        {
            if (_enableScte)
            {
                _scteInjector = new TsScteInjector(OutputPath);
                _fmt->pb = _scteInjector.Pb;
                _fmt->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            }
            else
            {
                Check(ffmpeg.avio_open(&_fmt->pb, OutputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
            }
        }

        Check(ffmpeg.avformat_write_header(_fmt, null), "write_header");

        // Iscrizione al mixer dopo l'header: i blocchi PCM arrivano sul thread del mixer.
        if (_audio is not null && _mixer is not null)
        {
            var audio = _audio;
            _audioHandler = (buf, frames) => audio.Push(buf, frames);
            _mixer.MixedAvailable += _audioHandler;
        }

        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        _frame->width = _width;
        _frame->height = _height;
        Check(ffmpeg.av_frame_get_buffer(_frame, 0), "frame_get_buffer");

        _packet = ffmpeg.av_packet_alloc();

        // Solo per il path di fallback BGRA (WriteFrame): converte in NV12 su CPU.
        _sws = ffmpeg.sws_getContext(_width, _height, AVPixelFormat.AV_PIX_FMT_BGRA,
            _width, _height, AVPixelFormat.AV_PIX_FMT_NV12, ffmpeg.SWS_BILINEAR, null, null, null);
        if (_sws == null) throw new InvalidOperationException("sws_getContext fallito.");
    }

    private bool OpenAnyCodec(long bitRate)
    {
        var order = new List<string>();
        if (_encoder is "h264_nvenc" or "libx264") order.Add(_encoder);
        foreach (var d in new[] { "h264_nvenc", "libx264" }) if (!order.Contains(d)) order.Add(d);

        foreach (var name in order)
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(name);
            if (codec == null) continue;
            if (TryOpen(codec, name, bitRate)) { CodecName = name; return true; }
        }
        var def = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
        if (def != null && TryOpen(def, "h264", bitRate)) { CodecName = ffmpeg.avcodec_get_name(def->id); return true; }
        return false;
    }

    private bool TryOpen(AVCodec* codec, string name, long bitRate)
    {
        var c = ffmpeg.avcodec_alloc_context3(codec);
        c->width = _width;
        c->height = _height;
        c->time_base = new AVRational { num = 1, den = _fps };
        c->framerate = new AVRational { num = _fps, den = 1 };
        c->pix_fmt = AVPixelFormat.AV_PIX_FMT_NV12;
        c->bit_rate = bitRate;
        c->gop_size = _fps * 2;
        c->max_b_frames = 0;

        // Streaming (RTMP/SRT) richiede CBR: bitrate costante, non VBR.
        // maxrate=minrate=bitrate + VBV buffer (1s) vincolano il rate control a costante;
        // la registrazione su file resta invariata (VBR, qualita' migliore).
        bool cbr = OutputPath.StartsWith("rtmp://", StringComparison.OrdinalIgnoreCase)
                || OutputPath.StartsWith("srt://", StringComparison.OrdinalIgnoreCase);
        if (cbr)
        {
            c->rc_max_rate = bitRate;
            c->rc_min_rate = bitRate;
            c->rc_buffer_size = (int)bitRate;
        }

        if ((_fmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            c->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        AVDictionary* opts = null;
        if (name == "h264_nvenc")
        {
            ffmpeg.av_dict_set(&opts, "preset", "p5", 0);
            if (cbr) ffmpeg.av_dict_set(&opts, "rc", "cbr", 0); // NVENC: rate control CBR
        }
        else
        {
            ffmpeg.av_dict_set(&opts, "preset", "veryfast", 0);
            ffmpeg.av_dict_set(&opts, "tune", "zerolatency", 0);
            if (cbr) ffmpeg.av_dict_set(&opts, "nal-hrd", "cbr", 0); // x264: HRD CBR
        }

        int ret = ffmpeg.avcodec_open2(c, codec, &opts);
        ffmpeg.av_dict_free(&opts);
        if (ret < 0)
        {
            ffmpeg.avcodec_free_context(&c);
            return false;
        }
        _codec = c;
        return true;
    }

    public bool WantsNv12 => true;

    // Calcola il pts target dal timestamp; -1 = frame da scartare (downsample).
    private long NextPts(long timestamp100ns)
    {
        if (_baseTicks < 0) _baseTicks = timestamp100ns; // pts relativi all'inizio di QUESTO writer
        long pts = (timestamp100ns - _baseTicks) / _ticksPerFrame;
        if (pts <= _lastEnqueuedPts) return -1; // downsample al fps target
        _lastEnqueuedPts = pts;
        return pts;
    }

    // --- Render thread: NV12 (path preferito), copia compatta + accodamento, con drop ---
    public void WriteFrameNv12(IntPtr y, int yStride, IntPtr uv, int uvStride, int width, int height, long timestamp100ns)
    {
        if (_failed || _closing || width != _width || height != _height) return;
        long pts = NextPts(timestamp100ns);
        if (pts < 0) return;

        if (!_pool.TryDequeue(out var buf)) buf = new byte[_width * _height * 4];

        byte* ySrc = (byte*)y, uvSrc = (byte*)uv;
        fixed (byte* d = buf)
        {
            for (int r = 0; r < _height; r++)
                Buffer.MemoryCopy(ySrc + (long)r * yStride, d + (long)r * _width, _width, _width);
            byte* du = d + _width * _height;            // piano UV interlacciato, _width byte/riga, _height/2 righe
            for (int r = 0; r < _height / 2; r++)
                Buffer.MemoryCopy(uvSrc + (long)r * uvStride, du + (long)r * _width, _width, _width);
        }

        try { if (!_queue.TryAdd((buf, pts, true))) _pool.Enqueue(buf); }
        catch (InvalidOperationException) { /* writer in chiusura (CompleteAdding) */ }
    }

    // --- Render thread: fallback BGRA (copia + accodamento, con drop) ---
    public void WriteFrame(IntPtr data, int rowPitch, int width, int height, long timestamp100ns)
    {
        if (_failed || _closing || width != _width || height != _height) return;
        long pts = NextPts(timestamp100ns);
        if (pts < 0) return;

        if (!_pool.TryDequeue(out var buf))
            buf = new byte[_width * _height * 4];

        int dstStride = _width * 4;
        byte* src = (byte*)data;
        fixed (byte* dst = buf)
        {
            for (int y = 0; y < _height; y++)
                Buffer.MemoryCopy(src + (long)y * rowPitch, dst + (long)y * dstStride, dstStride, dstStride);
        }

        try { if (!_queue.TryAdd((buf, pts, false))) _pool.Enqueue(buf); /* coda piena: scarta */ }
        catch (InvalidOperationException) { /* writer in chiusura (CompleteAdding) */ }
    }

    // --- Encoder thread ---
    private void EncodeLoop()
    {
        try
        {
            foreach (var (buf, pts, nv12) in _queue.GetConsumingEnumerable())
            {
                EncodeOne(buf, pts, nv12);
                _pool.Enqueue(buf);
            }
        }
        catch { _failed = true; }
    }

    private void EncodeOne(byte[] buf, long pts, bool nv12)
    {
        ffmpeg.av_frame_make_writable(_frame);

        if (nv12)
        {
            // Copia diretta dei piani NV12 (Y compatto + UV interlacciato) nei piani dell'AVFrame.
            fixed (byte* pBuf = buf)
            {
                for (int r = 0; r < _height; r++)
                    Buffer.MemoryCopy(pBuf + (long)r * _width, _frame->data[0] + (long)r * _frame->linesize[0], _width, _width);
                byte* uv = pBuf + _width * _height;
                for (int r = 0; r < _height / 2; r++)
                    Buffer.MemoryCopy(uv + (long)r * _width, _frame->data[1] + (long)r * _frame->linesize[1], _width, _width);
            }
        }
        else
        {
            int srcStride = _width * 4;
            fixed (byte* pBuf = buf)
            {
                var srcData = new byte*[] { pBuf, null, null, null };
                var srcLines = new[] { srcStride, 0, 0, 0 };
                var dstData = new byte*[] { _frame->data[0], _frame->data[1], _frame->data[2], _frame->data[3] };
                var dstLines = new[] { _frame->linesize[0], _frame->linesize[1], _frame->linesize[2], _frame->linesize[3] };
                ffmpeg.sws_scale(_sws, srcData, srcLines, 0, _height, dstData, dstLines);
            }
        }

        if (pts <= _lastEncodedPts) pts = _lastEncodedPts + 1;
        _lastEncodedPts = pts;
        _frame->pts = pts;

        Encode(_frame);
    }

    private void Encode(AVFrame* frame)
    {
        int ret = ffmpeg.avcodec_send_frame(_codec, frame);
        if (ret < 0) return;
        while (ret >= 0)
        {
            ret = ffmpeg.avcodec_receive_packet(_codec, _packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
            if (ret < 0) break;

            _packet->stream_index = _stream->index;
            ffmpeg.av_packet_rescale_ts(_packet, _codec->time_base, _stream->time_base);
            lock (_muxLock) ffmpeg.av_interleaved_write_frame(_fmt, _packet);
            ffmpeg.av_packet_unref(_packet);
        }
    }

    /// <summary>
    /// Accoda una sezione SCTE-35 (splice_insert): l'iniettore TS la scrive sul PID dedicato al
    /// prossimo pacchetto. Chiamabile da qualsiasi thread. No-op se SCTE non attivo.
    /// </summary>
    public void WriteSpliceInsert(byte[] section)
    {
        if (_failed || _closing || section == null || section.Length == 0) return;
        _scteInjector?.EnqueueSplice(section);
    }

    /// <summary>
    /// Accoda un marker SCTE-35 parametrico (Cue-Out/Cue-In). Se <paramref name="immediate"/> è false,
    /// l'iniettore calcola il PTS di destinazione dal PTS video corrente + <paramref name="prerollSeconds"/>
    /// (pre-roll: il marker punta al frame futuro dello stacco). Chiamabile da qualsiasi thread.
    /// </summary>
    public void WriteScte(uint eventId, bool outOfNetwork, double durationSeconds, bool immediate, double prerollSeconds)
    {
        if (_failed || _closing) return;
        _scteInjector?.EnqueueScte(eventId, outOfNetwork, durationSeconds, immediate, prerollSeconds);
    }

    public void Dispose()
    {
        _closing = true;                  // i write thread smettono di accodare prima di CompleteAdding
        _queue.CompleteAdding();
        if (_encodeThread.IsAlive) _encodeThread.Join();

        // Scollega l'audio dal mixer PRIMA di toccare i context: niente nuove Push in arrivo.
        if (_audioHandler is not null && _mixer is not null) _mixer.MixedAvailable -= _audioHandler;
        _audio?.Dispose(); // attende l'eventuale Push in corso (lock interno) e fa il flush AAC
        _audio = null;

        // flush video dopo l'audio: tutti i writer sono fermi, write_frame/trailer non corrono piu'
        try { if (_codec != null) Encode(null); } catch { _failed = true; }

        // RTMP rotto: write_trailer puo' fallire/lanciare sulla rete -> non far crashare lo stop.
        try { if (_fmt != null) ffmpeg.av_write_trailer(_fmt); } catch { _failed = true; }

        ffmpeg.sws_freeContext(_sws); _sws = null;
        fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p);
        fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p);
        fixed (AVCodecContext** p = &_codec) ffmpeg.avcodec_free_context(p);

        if (_fmt != null)
        {
            if (_scteInjector != null)
            {
                // Con custom IO l'output reale lo chiude l'iniettore: stacca pb prima di liberare il context.
                _fmt->pb = null;
            }
            else if ((_fmt->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0 && _fmt->pb != null)
            {
                ffmpeg.avio_closep(&_fmt->pb);
            }
            ffmpeg.avformat_free_context(_fmt);
            _fmt = null;
        }

        if (_scteInjector != null) { try { _scteInjector.Dispose(); } catch { } _scteInjector = null; }
    }

    private static void Check(int code, string what)
    {
        if (code < 0) throw new InvalidOperationException($"FFmpeg {what} fallito (codice {code}).");
    }
}
