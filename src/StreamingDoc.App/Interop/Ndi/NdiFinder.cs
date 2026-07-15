using System.Runtime.InteropServices;

namespace StreamingDoc.App.Interop.Ndi;

/// <summary>
/// Scoperta delle sorgenti NDI in rete. Un'istanza di find a lunga vita accumula le sorgenti viste;
/// <see cref="ListSources"/> restituisce lo snapshot corrente dei nomi.
/// </summary>
public static class NdiFinder
{
    private static IntPtr _find;
    private static readonly object _lock = new();

    private static bool EnsureFinder()
    {
        if (!NdiLib.IsAvailable) return false;
        lock (_lock)
        {
            if (_find != IntPtr.Zero) return true;
            var c = new NdiLib.FindCreate { show_local_sources = 1, p_groups = IntPtr.Zero, p_extra_ips = IntPtr.Zero };
            _find = NdiLib.NDIlib_find_create_v2(ref c);
            return _find != IntPtr.Zero;
        }
    }

    /// <summary>Nomi delle sorgenti NDI attualmente visibili. Lista vuota se runtime assente.</summary>
    public static List<string> ListSources(int waitMs = 500)
    {
        var result = new List<string>();
        if (!EnsureFinder()) return result;

        lock (_lock)
        {
            try
            {
                NdiLib.NDIlib_find_wait_for_sources(_find, (uint)Math.Max(0, waitMs));
                uint count = 0;
                IntPtr arr = NdiLib.NDIlib_find_get_current_sources(_find, ref count);
                if (arr == IntPtr.Zero) return result;

                int stride = Marshal.SizeOf<NdiLib.Source>();
                for (int i = 0; i < count; i++)
                {
                    var s = Marshal.PtrToStructure<NdiLib.Source>(arr + i * stride);
                    var name = NdiLib.FromUtf8(s.p_ndi_name);
                    if (!string.IsNullOrEmpty(name)) result.Add(name!);
                }
            }
            catch { /* runtime instabile: snapshot parziale */ }
        }
        return result;
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            if (_find != IntPtr.Zero) { try { NdiLib.NDIlib_find_destroy(_find); } catch { } _find = IntPtr.Zero; }
        }
    }
}
