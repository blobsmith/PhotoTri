using System.Globalization;
using System.Text;
using PhotoTri.Core.Library;

namespace PhotoTri.Core.Viewer;

/// <summary>
/// Recherche dans la bibliothèque : chaque mot tapé doit se retrouver dans le nom d'une personne,
/// le lieu (ville, région, pays, adresse) ou le nom du fichier. Sans tenir compte des accents ni des majuscules.
/// </summary>
public static class LibrarySearch
{
    public static Func<CatalogEntry, bool> Matcher(string query)
    {
        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return _ => true;
        return entry =>
        {
            var text = Haystack(entry);
            return words.All(text.Contains);
        };
    }

    static string Haystack(CatalogEntry e)
    {
        var parts = new List<string?> { Path.GetFileName(e.RelativePath), e.Address };
        if (e.Place is { } p) parts.AddRange(new[] { p.City, p.Region, p.Country });
        if (e.People is { } people) parts.AddRange(people);
        return Normalize(string.Join(' ', parts.Where(s => s is not null)));
    }

    /// <summary>Minuscules sans accents : « Amélia » et « amelia » se valent.</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
