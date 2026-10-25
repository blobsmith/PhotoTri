using System.Text.Json;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;

namespace PhotoTri.Core;

/// <summary>Réglages de l'application, dans %AppData%\PhotoTri\settings.json.</summary>
public sealed class AppSettings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoTri", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public LibraryLocation? Library { get; set; }

    public MediaFilter Filter { get; set; } = new();

    public bool DeleteFromPhone { get; set; }

    /// <summary>Recherche des adresses précises sur OpenStreetMap (envoie les positions GPS) : désactivée par défaut.</summary>
    public bool OnlineAddresses { get; set; }

    public static string DefaultLibraryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Photos téléphone");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (JsonException)
        {
            // Fichier de réglages abîmé : on repart des valeurs par défaut.
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }
}
