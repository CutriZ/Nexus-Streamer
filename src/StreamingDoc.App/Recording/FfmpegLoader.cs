using System.IO;
using FFmpeg.AutoGen;

namespace StreamingDoc.App.Recording;

/// <summary>Punta FFmpeg.AutoGen alle DLL native in &lt;output&gt;\ffmpeg e ne forza il caricamento una volta.</summary>
public static class FfmpegLoader
{
    private static readonly object Lock = new();
    private static bool _loaded;

    public static void EnsureLoaded()
    {
        lock (Lock)
        {
            if (_loaded) return;
            ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            // Forza il binding: lancia un'eccezione chiara qui se le DLL mancano.
            _ = ffmpeg.avformat_version();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            // Registra i demuxer/muxer di libavdevice (dshow, gdigrab...). SENZA questo
            // av_find_input_format("dshow") torna null e webcam/capture card non si aprono.
            try { ffmpeg.avdevice_register_all(); } catch { /* avdevice assente: capture device non disponibili */ }
            _loaded = true;
        }
    }
}
