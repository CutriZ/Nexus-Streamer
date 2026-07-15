using FFmpeg.AutoGen;

namespace StreamingDoc.App.Recording;

/// <summary>
/// Codifica in AAC i blocchi PCM (float interleaved, 48kHz stereo) ricevuti dal mixer e li scrive
/// come secondo stream del muxer. Condivide AVFormatContext + lock di muxing con <see cref="VideoRecorder"/>.
/// </summary>
public sealed unsafe class AudioEncoder : IDisposable
{
    private const int Rate = 48000;

    private readonly AVFormatContext* _fmt;
    private readonly object _muxLock;
    private readonly object _lifeLock = new(); // serializza Push vs Dispose: no use-after-free in chiusura

    private AVStream* _stream;
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwrContext* _swr;
    private AVAudioFifo* _fifo;
    private int _frameSize;
    private long _nextPts;
    private bool _disposed;

    public AudioEncoder(AVFormatContext* fmt, object muxLock)
    {
        _fmt = fmt;
        _muxLock = muxLock;

        var codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null) throw new InvalidOperationException("Encoder AAC non disponibile.");

        _stream = ffmpeg.avformat_new_stream(_fmt, null);
        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        _ctx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        _ctx->bit_rate = 160_000;
        _ctx->sample_rate = Rate;
        ffmpeg.av_channel_layout_default(&_ctx->ch_layout, 2);
        _ctx->time_base = new AVRational { num = 1, den = Rate };
        if ((_fmt->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
            _ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        if (ffmpeg.avcodec_open2(_ctx, codec, null) < 0)
            throw new InvalidOperationException("avcodec_open2 (AAC) fallito.");
        ffmpeg.avcodec_parameters_from_context(_stream->codecpar, _ctx);
        _stream->time_base = _ctx->time_base;
        _frameSize = _ctx->frame_size > 0 ? _ctx->frame_size : 1024;

        AVChannelLayout inLayout, outLayout;
        ffmpeg.av_channel_layout_default(&inLayout, 2);
        ffmpeg.av_channel_layout_default(&outLayout, 2);
        SwrContext* swr = null;
        ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, Rate,
            &inLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, Rate, 0, null);
        if (swr == null || ffmpeg.swr_init(swr) < 0)
            throw new InvalidOperationException("swr_init (audio) fallito.");
        _swr = swr;

        _fifo = ffmpeg.av_audio_fifo_alloc(AVSampleFormat.AV_SAMPLE_FMT_FLTP, 2, 1);

        _frame = ffmpeg.av_frame_alloc();
        _frame->nb_samples = _frameSize;
        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        ffmpeg.av_channel_layout_default(&_frame->ch_layout, 2);
        _frame->sample_rate = Rate;
        ffmpeg.av_frame_get_buffer(_frame, 0);

        _packet = ffmpeg.av_packet_alloc();
    }

    /// <summary>Accoda un blocco PCM (float interleaved stereo @48k). Chiamato dal thread del mixer.</summary>
    public void Push(float[] interleaved, int frames)
    {
        // Tutto il corpo sotto _lifeLock: se Dispose libera i context FFmpeg mentre la
        // mixer thread e' qui dentro -> use-after-free -> crash nativo. Il lock lo impedisce.
        lock (_lifeLock)
        {
            if (_disposed || frames <= 0) return;

            byte** outData = null;
            int outLinesize;
            ffmpeg.av_samples_alloc_array_and_samples(&outData, &outLinesize, 2, frames,
                AVSampleFormat.AV_SAMPLE_FMT_FLTP, 0);
            try
            {
                fixed (float* pIn = interleaved)
                {
                    byte** inPtr = stackalloc byte*[1];
                    inPtr[0] = (byte*)pIn;
                    int got = ffmpeg.swr_convert(_swr, outData, frames, inPtr, frames);
                    if (got > 0)
                        ffmpeg.av_audio_fifo_write(_fifo, (void**)outData, got);
                }

                while (ffmpeg.av_audio_fifo_size(_fifo) >= _frameSize)
                    EncodeOneFrame();
            }
            catch { /* glitch audio: continua */ }
            finally
            {
                if (outData != null) { ffmpeg.av_freep(&outData[0]); ffmpeg.av_freep(&outData); }
            }
        }
    }

    private void EncodeOneFrame()
    {
        ffmpeg.av_frame_make_writable(_frame);
        ffmpeg.av_audio_fifo_read(_fifo, (void**)_frame->extended_data, _frameSize);
        _frame->pts = _nextPts;
        _nextPts += _frameSize;
        Send(_frame);
    }

    private void Send(AVFrame* frame)
    {
        int ret = ffmpeg.avcodec_send_frame(_ctx, frame);
        if (ret < 0) return;
        while (ret >= 0)
        {
            ret = ffmpeg.avcodec_receive_packet(_ctx, _packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) break;
            if (ret < 0) break;

            _packet->stream_index = _stream->index;
            ffmpeg.av_packet_rescale_ts(_packet, _ctx->time_base, _stream->time_base);
            lock (_muxLock) ffmpeg.av_interleaved_write_frame(_fmt, _packet);
            ffmpeg.av_packet_unref(_packet);
        }
    }

    public void Dispose()
    {
        // Stesso lock di Push: attende una Push in corso prima di liberare i context.
        lock (_lifeLock)
        {
            if (_disposed) return;
            _disposed = true;

            Send(null); // flush

            if (_fifo != null) { ffmpeg.av_audio_fifo_free(_fifo); _fifo = null; }
            fixed (SwrContext** p = &_swr) ffmpeg.swr_free(p);
            fixed (AVFrame** p = &_frame) ffmpeg.av_frame_free(p);
            fixed (AVPacket** p = &_packet) ffmpeg.av_packet_free(p);
            fixed (AVCodecContext** p = &_ctx) ffmpeg.avcodec_free_context(p);
        }
    }
}
