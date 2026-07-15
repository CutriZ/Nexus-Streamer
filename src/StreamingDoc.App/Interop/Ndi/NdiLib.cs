using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace StreamingDoc.App.Interop.Ndi;

/// <summary>
/// Binding P/Invoke per la runtime NDI (Processing.NDI.Lib.x64.dll). La DLL NON è ridistribuita:
/// va installata "NDI Tools" / "NDI Runtime". Caricamento dinamico: se la runtime non c'è,
/// <see cref="IsAvailable"/> resta false e nessuna funzione viene chiamata.
/// </summary>
internal static unsafe class NdiLib
{
    private const string Lib = "Processing.NDI.Lib.x64";

    // --- enum ---
    public enum FrameType { None = 0, Video = 1, Audio = 2, Metadata = 3, Error = 4, StatusChange = 100 }

    public enum RecvColorFormat { BGRX_BGRA = 0, UYVY_BGRA = 1, RGBX_RGBA = 2, UYVY_RGBA = 3, Fastest = 100, Best = 101 }

    public enum RecvBandwidth { MetadataOnly = -10, AudioOnly = 10, Lowest = 0, Highest = 100 }

    public enum FrameFormat { Progressive = 1, Interleaved = 0, Field0 = 2, Field1 = 3 }

    // FourCC video
    public const int FourCC_BGRA = ('B') | ('G' << 8) | ('R' << 16) | ('A' << 24);
    public const int FourCC_BGRX = ('B') | ('G' << 8) | ('R' << 16) | ('X' << 24);
    // FourCC audio float planare
    public const int FourCC_FLTp = ('F') | ('L' << 8) | ('T' << 16) | ('p' << 24);

    // --- struct ---
    [StructLayout(LayoutKind.Sequential)]
    public struct Source
    {
        public IntPtr p_ndi_name;     // const char* (UTF-8)
        public IntPtr p_url_address;  // const char*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FindCreate
    {
        public byte show_local_sources; // bool (1 byte)
        public IntPtr p_groups;
        public IntPtr p_extra_ips;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RecvCreateV3
    {
        public Source source_to_connect_to;
        public int color_format;        // RecvColorFormat
        public int bandwidth;           // RecvBandwidth
        public byte allow_video_fields; // bool
        public IntPtr p_ndi_recv_name;  // const char*
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrameV2
    {
        public int xres;
        public int yres;
        public int FourCC;
        public int frame_rate_N;
        public int frame_rate_D;
        public float picture_aspect_ratio;
        public int frame_format_type;
        public long timecode;
        public IntPtr p_data;
        public int line_stride_in_bytes; // (unione con data_size per compressi)
        public IntPtr p_metadata;
        public long timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrameV2
    {
        public int sample_rate;
        public int no_channels;
        public int no_samples;
        public long timecode;
        public IntPtr p_data;             // float* planare
        public int channel_stride_in_bytes;
        public IntPtr p_metadata;
        public long timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr p_ndi_name;
        public IntPtr p_groups;
        public byte clock_video;
        public byte clock_audio;
    }

    // --- import ---
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte NDIlib_initialize();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_destroy();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_find_create_v2(ref FindCreate p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_find_destroy(IntPtr inst);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_find_get_current_sources(IntPtr inst, ref uint count);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern byte NDIlib_find_wait_for_sources(IntPtr inst, uint timeoutMs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_recv_create_v3(ref RecvCreateV3 p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_destroy(IntPtr inst);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int NDIlib_recv_capture_v2(IntPtr inst, ref VideoFrameV2 v, ref AudioFrameV2 a, IntPtr meta, uint timeoutMs);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_free_video_v2(IntPtr inst, ref VideoFrameV2 v);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_recv_free_audio_v2(IntPtr inst, ref AudioFrameV2 a);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr NDIlib_send_create(ref SendCreate p);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_destroy(IntPtr inst);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_video_v2(IntPtr inst, ref VideoFrameV2 v);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void NDIlib_send_send_audio_v2(IntPtr inst, ref AudioFrameV2 a);

    // --- caricamento dinamico della runtime ---
    private static int _avail = -1; // -1 sconosciuto, 0 no, 1 sì

    static NdiLib()
    {
        try { NativeLibrary.SetDllImportResolver(typeof(NdiLib).Assembly, Resolve); } catch { }
    }

    private static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (!name.StartsWith("Processing.NDI", StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero;

        foreach (var p in CandidatePaths())
            if (File.Exists(p) && NativeLibrary.TryLoad(p, out var h)) return h;

        // ultimo tentativo: lascia che il loader cerchi in PATH
        return NativeLibrary.TryLoad("Processing.NDI.Lib.x64.dll", out var h2) ? h2 : IntPtr.Zero;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        const string dll = "Processing.NDI.Lib.x64.dll";
        foreach (var v in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5", "NDI_RUNTIME_DIR_V4" })
        {
            var d = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(d)) yield return Path.Combine(d, dll);
        }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var rel in new[]
        {
            @"NDI\NDI 6 Runtime\v6", @"NDI\NDI 5 Runtime\v5", @"NDI\NDI 4 Runtime\v4",
            @"NewTek\NDI 5 Runtime\v5", @"NewTek\NDI 4 Runtime\v4",
        })
            yield return Path.Combine(pf, rel, dll);
    }

    /// <summary>true se la runtime NDI è presente e inizializzabile (test eseguito una sola volta).</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_avail >= 0) return _avail == 1;
            try { _avail = NDIlib_initialize() != 0 ? 1 : 0; }
            catch { _avail = 0; }
            return _avail == 1;
        }
    }

    public static IntPtr Utf8(string s) => Marshal.StringToCoTaskMemUTF8(s);
    public static void FreeUtf8(IntPtr p) { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
    public static string? FromUtf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);
}
