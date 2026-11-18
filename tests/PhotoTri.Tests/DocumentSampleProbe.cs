using System.Diagnostics;
using PhotoTri.Core;
using PhotoTri.Core.Documents;
using PhotoTri.Core.Library;
using Xunit.Abstractions;

namespace PhotoTri.Tests;

/// <summary>
/// Essai sur la vraie bibliothèque, inactif sauf si PHOTOTRI_DOCS_SAMPLE donne un nombre de photos.
/// (L'outil de diagnostic peut être bloqué par Smart App Control ; le projet de tests ne l'est pas.)
/// N'affiche que le type trouvé et le nom du fichier, jamais le texte lu.
/// </summary>
public class DocumentSampleProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task Sample_of_the_real_library()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("PHOTOTRI_DOCS_SAMPLE"), out var count)) return;
        var library = AppSettings.Load().Library?.Resolve() ?? AppSettings.DefaultLibraryPath;
        var reader = PhotoTextReader.TryCreate();
        if (reader is null)
        {
            output.WriteLine("Aucune langue de reconnaissance de texte installée dans Windows.");
            return;
        }
        output.WriteLine($"Langue de lecture : {reader.Language}");
        var sample = Directory.EnumerateFiles(library, "*.jpg", SearchOption.AllDirectories)
            .Where(f => !f.Contains(LibraryLayout.DataFolderName))
            .OrderBy(f => f.GetHashCode()).Take(count).ToList();
        var timer = Stopwatch.StartNew();
        var found = new List<(string File, DocumentKind Kind, int Words)>();
        foreach (var file in sample)
        {
            var lines = await reader.ReadAsync(file);
            if (DocumentClassifier.Classify(lines) is { } kind)
                found.Add((Path.GetFileName(file), kind, lines.Sum(l => l.Split(' ').Length)));
        }
        output.WriteLine($"{sample.Count} photos lues en {timer.Elapsed.TotalSeconds:0.0} s ({timer.Elapsed.TotalMilliseconds / sample.Count:0} ms par photo)");
        foreach (var g in found.GroupBy(f => f.Kind).OrderBy(g => g.Key))
            output.WriteLine($"  {DocumentClassifier.Label(g.Key),-20} {g.Count(),3} : {string.Join(", ", g.Take(8).Select(f => $"{f.File} ({f.Words} mots)"))}");
    }
}
