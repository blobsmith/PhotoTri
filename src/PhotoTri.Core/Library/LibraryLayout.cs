using System.Globalization;

namespace PhotoTri.Core.Library;

/// <summary>Arborescence physique de la bibliothèque : Année\Année-Mois NomDuMois\fichier.</summary>
public static class LibraryLayout
{
    static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Dossier interne (base, fichiers en cours de copie), caché dans l'Explorateur.</summary>
    public const string DataFolderName = ".phototri";

    public static string MonthFolder(DateTime date)
    {
        var month = French.DateTimeFormat.GetMonthName(date.Month);
        month = char.ToUpper(month[0], French) + month[1..];
        return Path.Combine(date.Year.ToString("0000"), $"{date:yyyy-MM} {month}");
    }

    /// <summary>
    /// Chemin libre pour <paramref name="fileName"/> dans <paramref name="folder"/> :
    /// un fichier existant n'est jamais écrasé, on ajoute « (2) », « (3) »…
    /// </summary>
    public static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
