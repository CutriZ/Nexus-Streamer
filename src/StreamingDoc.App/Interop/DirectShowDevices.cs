using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace StreamingDoc.App.Interop;

/// <summary>
/// Enumera i device della categoria DirectShow "VideoInputDevice" tramite il System Device Enumerator
/// (COM puro, nessuna dipendenza esterna). Vede anche i filtri virtuali dei playout/regie (es.
/// "Mainlevel VideoSource 1..4", OBS Virtual Camera) che l'enumerazione WinRT/Media Foundation ignora.
/// I FriendlyName restituiti coincidono con quelli che FFmpeg dshow usa come "video=&lt;nome&gt;".
/// </summary>
internal static class DirectShowDevices
{
    private static readonly Guid CLSID_SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    private static readonly Guid CLSID_VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    private static readonly Guid CLSID_AudioInputDeviceCategory = new("33D9A762-90C8-11d0-BD43-00A0C911CE86");

    public static List<string> VideoInputNames() => Enumerate(CLSID_VideoInputDeviceCategory);
    public static List<string> AudioInputNames() => Enumerate(CLSID_AudioInputDeviceCategory);

    private static List<string> Enumerate(Guid category)
    {
        var list = new List<string>();
        object? devEnumObj = null;
        try
        {
            var srvType = Type.GetTypeFromCLSID(CLSID_SystemDeviceEnum);
            if (srvType is null) return list;
            devEnumObj = Activator.CreateInstance(srvType);
            if (devEnumObj is not ICreateDevEnum devEnum) return list;

            int hr = devEnum.CreateClassEnumerator(ref category, out var en, 0);
            if (hr != 0 || en is null) return list; // S_FALSE (1) = categoria vuota

            var monikers = new IMoniker[1];
            Guid bagId = typeof(IPropertyBag).GUID;
            try
            {
                while (en.Next(1, monikers, IntPtr.Zero) == 0)
                {
                    var mon = monikers[0];
                    try
                    {
                        var name = ReadName(mon, bagId);
                        if (!string.IsNullOrEmpty(name) && !list.Contains(name!))
                            list.Add(name!);
                    }
                    catch { /* moniker non leggibile: salta */ }
                    finally { if (mon is not null) Marshal.ReleaseComObject(mon); }
                }
            }
            finally { Marshal.ReleaseComObject(en); }
        }
        catch { /* COM non disponibile: lista vuota */ }
        finally { if (devEnumObj is not null) Marshal.ReleaseComObject(devEnumObj); }
        return list;
    }

    /// <summary>
    /// Nome leggibile di un device. Alcuni filtri pro (es. varianti "HD") falliscono "FriendlyName":
    /// fallback su "Description" e poi sul display name del moniker, così non vengono persi (OBS fa uguale).
    /// </summary>
    private static string? ReadName(IMoniker mon, Guid bagId)
    {
        try
        {
            mon.BindToStorage(null!, null!, ref bagId, out var bagObj);
            if (bagObj is IPropertyBag bag)
            {
                object? v = null;
                if (bag.Read("FriendlyName", ref v, IntPtr.Zero) == 0 && v is string fn && fn.Length > 0) return fn;
                v = null;
                if (bag.Read("Description", ref v, IntPtr.Zero) == 0 && v is string ds && ds.Length > 0) return ds;
            }
        }
        catch { /* BindToStorage/Read fallito: prova il display name */ }

        try { mon.GetDisplayName(null!, null!, out var dn); if (!string.IsNullOrEmpty(dn)) return dn; }
        catch { }
        return null;
    }

    [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICreateDevEnum
    {
        [PreserveSig] int CreateClassEnumerator([In] ref Guid pType, out IEnumMoniker? ppEnumMoniker, int dwFlags);
    }

    [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyBag
    {
        [PreserveSig] int Read([MarshalAs(UnmanagedType.LPWStr)] string name, [In, Out] ref object? value, IntPtr errorLog);
        [PreserveSig] int Write([MarshalAs(UnmanagedType.LPWStr)] string name, [In] ref object value);
    }
}
