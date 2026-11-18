using PhotoTri.Core.Library;
using PhotoTri.Core.Media;

namespace PhotoTri.Core.Documents;

/// <summary>
/// Lit le texte des photos pas encore analysées et repère les documents. Lancé en arrière-plan
/// après chaque transfert et au démarrage, comme les lieux et les visages.
/// </summary>
public static class DocumentIndexer
{
    /// <summary>
    /// Renvoie le nombre de photos lues, ou null si Windows n'a pas de langue de reconnaissance de texte.
    /// <paramref name="progress"/> reçoit (faites, total).
    /// </summary>
    public static async Task<int?> RunAsync(LibraryIndex index, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var total = index.CountPhotosWithoutDocumentCheck();
        if (total == 0) return 0;
        var reader = PhotoTextReader.TryCreate();
        if (reader is null) return null;

        var done = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = index.PhotosWithoutDocumentCheck(50);
            if (batch.Count == 0) break;
            foreach (var (id, relativePath, origin) in batch)
            {
                if (ct.IsCancellationRequested) break;
                // Disque débranché : on s'arrête sans marquer les photos comme lues.
                if (!Directory.Exists(index.LibraryRoot)) return done;

                TextReading reading;
                try
                {
                    reading = await reader.ReadWithLayoutAsync(Path.Combine(index.LibraryRoot, relativePath));
                }
                catch (Exception)
                {
                    reading = new TextReading(Array.Empty<string>(), 0); // photo absente ou illisible : pas de document
                }
                index.SaveDocument(id, Classify(reading.Lines, origin, reading.Coverage), string.Join('\n', reading.Lines));
                done++;
                progress?.Report((done, total));
            }
        }
        return done;
    }

    /// <summary>
    /// Une capture d'écran contient presque toujours du texte : elle ne compte comme document
    /// que si c'est une pièce officielle (capture d'une carte d'identité, d'un permis…).
    /// </summary>
    public static DocumentKind? Classify(IReadOnlyList<string> lines, MediaOrigin origin, double coverage = 1)
    {
        var kind = DocumentClassifier.Classify(lines, coverage);
        if (origin == MediaOrigin.Screenshot && kind is { } k && !DocumentClassifier.IsSensitive(k)) return null;
        return kind;
    }
}
