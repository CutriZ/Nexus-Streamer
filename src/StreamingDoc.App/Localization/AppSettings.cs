using System.Globalization;
using System.IO;
using System.Text.Json;

namespace StreamingDoc.App.Localization;

/// <summary>Preferenze app indipendenti dal progetto (lingua). File in Documenti\Nexus Streamer\app.json.</summary>
public static class AppSettings
{
    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Nexus Streamer");
    private static string FilePath => Path.Combine(Dir, "app.json");

    private sealed class Dto { public string Lang { get; set; } = "it"; }

    /// <summary>Lingua iniziale: file utente -> lingua di sistema (se supportata) -> italiano.</summary>
    public static string LoadLanguage()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
                if (!string.IsNullOrEmpty(d?.Lang)) return d!.Lang;
            }
        }
        catch { }

        try
        {
            var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (two is "en" or "es" or "fr" or "de") return two;
        }
        catch { }

        return "it";
    }

    public static void SaveLanguage(string code)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new Dto { Lang = code }));
        }
        catch { }
    }
}
