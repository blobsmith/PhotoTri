using PhotoTri.Core.Library;

namespace PhotoTri.Core.Places;

/// <summary>
/// Lit la position GPS des fichiers de la bibliothèque qui n'ont pas encore été traités et leur
/// attribue la ville la plus proche (sans Internet). Lancé après chaque transfert et au démarrage.
/// </summary>
public static class LocationIndexer
{
    const int BatchSize = 200;

    /// <summary>Renvoie le nombre de fichiers traités.</summary>
    public static int Run(LibraryIndex index, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = index.FilesWithoutGeo(BatchSize);
            if (batch.Count == 0) break;
            foreach (var (id, relativePath) in batch)
            {
                if (ct.IsCancellationRequested) break;
                var path = Path.Combine(index.LibraryRoot, relativePath);
                // Fichier supprimé à la main dans l'Explorateur : on le marque traité, sans lieu.
                var point = File.Exists(path) ? GpsReader.Read(path) : null;
                var place = point is { } p ? OfflineGeocoder.Instance.Find(p) : null;
                index.SetGeo(id, point, place);
                done++;
                progress?.Report(done);
            }
        }
        return done;
    }
}
