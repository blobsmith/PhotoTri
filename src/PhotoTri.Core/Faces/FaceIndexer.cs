using PhotoTri.Core.Library;

namespace PhotoTri.Core.Faces;

/// <summary>
/// Cherche les visages des photos pas encore analysées, calcule leur empreinte et garde une vignette
/// de chaque visage sur le PC. Lancé en arrière-plan après chaque transfert et au démarrage.
/// </summary>
public static class FaceIndexer
{
    /// <summary>Taille de lecture des photos : assez pour les visages d'une photo de groupe, rapide à décoder.</summary>
    const int ImageSize = 1600;

    /// <summary>En dessous, le visage est trop petit pour être reconnu de façon fiable.</summary>
    const float MinFacePixels = 36;

    const int CropPixels = 160;

    /// <summary>Vignette d'un visage, gardée sur le PC (une par photo et par visage).</summary>
    public static string CropPath(string sha256, int index) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PhotoTri", "Visages", sha256[..2], $"{sha256}_{index}.jpg");

    /// <summary>Renvoie le nombre de photos analysées. <paramref name="progress"/> reçoit (faites, total).</summary>
    public static int Run(LibraryIndex index, IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default, string? modelsDir = null)
    {
        var total = index.CountPhotosWithoutFaces();
        if (total == 0) return 0;

        using var engine = new FaceEngine(modelsDir);
        var done = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = index.PhotosWithoutFaces(50);
            if (batch.Count == 0) break;
            foreach (var (id, relativePath, sha) in batch)
            {
                if (ct.IsCancellationRequested) break;
                // Disque débranché : on s'arrête sans marquer les photos comme analysées.
                if (!Directory.Exists(index.LibraryRoot)) return done;
                index.SaveFaces(id, Analyze(engine, Path.Combine(index.LibraryRoot, relativePath), sha));
                done++;
                progress?.Report((done, total));
            }
            // Les visages des personnes déjà nommées leur sont attribués au fur et à mesure.
            index.ApplyAutoAssignments();
        }
        return done;
    }

    /// <summary>
    /// Recrée les vignettes de visages absentes de ce PC (bibliothèque ouverte sur un autre ordinateur,
    /// cache effacé…), à partir des photos et des cadres enregistrés dans la base. Renvoie le nombre recréé.
    /// </summary>
    public static int RegenerateMissingCrops(LibraryIndex index, CancellationToken ct = default)
    {
        var created = 0;
        foreach (var photo in index.FaceBoxes().Where(f => !File.Exists(CropPath(f.Sha256, f.Index))).GroupBy(f => f.RelativePath))
        {
            if (ct.IsCancellationRequested || !Directory.Exists(index.LibraryRoot)) break;
            RgbImage image;
            try
            {
                image = RgbImage.Load(Path.Combine(index.LibraryRoot, photo.Key), ImageSize);
            }
            catch (Exception)
            {
                continue; // photo absente ou illisible : la vignette restera vide
            }
            foreach (var f in photo)
            {
                var (w, h) = (f.Width * image.Width, f.Height * image.Height);
                image.SaveSquareCrop(CropPath(f.Sha256, f.Index), f.X * image.Width + w / 2, f.Y * image.Height + h / 2,
                    Math.Max(w, h) * 1.6f, CropPixels);
                created++;
            }
        }
        return created;
    }

    static List<FaceToSave> Analyze(FaceEngine engine, string path, string sha)
    {
        var result = new List<FaceToSave>();
        RgbImage image;
        try
        {
            image = RgbImage.Load(path, ImageSize);
        }
        catch (Exception)
        {
            // Fichier absent ou format que Windows ne sait pas lire (HEIC sans extension…) : aucun visage.
            return result;
        }

        var faces = engine.Detect(image).Where(f => Math.Min(f.Width, f.Height) >= MinFacePixels).ToList();
        for (var i = 0; i < faces.Count; i++)
        {
            var f = faces[i];
            image.SaveSquareCrop(CropPath(sha, i), f.X + f.Width / 2, f.Y + f.Height / 2, Math.Max(f.Width, f.Height) * 1.6f, CropPixels);
            result.Add(new FaceToSave(i, f.X / image.Width, f.Y / image.Height, f.Width / image.Width, f.Height / image.Height,
                f.Score, engine.Embed(image, f)));
        }
        return result;
    }
}
