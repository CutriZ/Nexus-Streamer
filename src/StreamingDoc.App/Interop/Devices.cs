using Windows.Devices.Enumeration;

namespace StreamingDoc.App.Interop;

internal static class Devices
{
    /// <summary>
    /// Nomi friendly delle sorgenti video (usati come "video=&lt;nome&gt;" per dshow). Unisce
    /// l'enumerazione DirectShow (vede i filtri virtuali dei playout: Mainlevel, OBS Virtual Cam...)
    /// con quella WinRT/Media Foundation. DirectShow per prima: è la chiave con cui FFmpeg dshow apre.
    /// </summary>
    public static List<string> VideoCaptureNames()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var n in DirectShowDevices.VideoInputNames())
                if (seen.Add(n)) result.Add(n);
        }
        catch { /* COM non disponibile */ }

        try
        {
            var col = DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).GetAwaiter().GetResult();
            foreach (var d in col)
                if (seen.Add(d.Name)) result.Add(d.Name);
        }
        catch { /* WinRT non disponibile */ }

        return result;
    }
}
