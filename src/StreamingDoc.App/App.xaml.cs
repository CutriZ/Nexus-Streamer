using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace StreamingDoc.App;

public partial class App : Application
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    public App()
    {
        Localization.Loc.Set(Localization.AppSettings.LoadLanguage()); // lingua salvata prima di creare la finestra
        timeBeginPeriod(1); // risoluzione timer 1ms: Sleep/timer precisi, niente jitter di frame
        Exit += (_, _) => { try { timeEndPeriod(1); } catch { } };
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log((e.ExceptionObject as Exception)?.ToString() ?? "unknown");
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception.ToString());
        // lascia crashare per non mascherare il problema in dev
    }

    private static void Log(string text)
    {
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "nexus_streamer_crash.log"), text); } catch { }
    }
}
