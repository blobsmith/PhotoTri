using MetadataExtractor;
using MetadataExtractor.Formats.QuickTime;

namespace PhotoTri.Core.Media;

/// <summary>
/// Les téléphones enregistrent les vidéos en portrait « couchées », avec un angle de rotation dans les
/// métadonnées. Le lecteur de Windows (MediaElement) ignore cet angle : il faut redresser l'image soi-même.
/// </summary>
public static class VideoRotation
{
    /// <summary>Angle de rotation à appliquer (0, 90, 180 ou 270 degrés dans le sens horaire).</summary>
    public static int Read(string path)
    {
        try
        {
            foreach (var track in ImageMetadataReader.ReadMetadata(path).OfType<QuickTimeTrackHeaderDirectory>())
            {
                // La piste vidéo est celle qui a une taille d'image ; la piste audio a une largeur nulle.
                if (!track.TryGetDouble(QuickTimeTrackHeaderDirectory.TagWidth, out var width) || width <= 0) continue;
                if (!track.TryGetDouble(QuickTimeTrackHeaderDirectory.TagRotation, out var rotation)) return 0;
                // MetadataExtractor compte l'angle dans le sens inverse des aiguilles d'une montre
                // (vérifié sur une vidéo portrait d'un Galaxy S21) ; l'affichage tourne dans le sens horaire.
                return Normalize(-rotation);
            }
        }
        catch (Exception)
        {
            // Fichier illisible : on affiche la vidéo telle quelle.
        }
        return 0;
    }

    public static int Normalize(double degrees)
    {
        var quarter = (int)Math.Round(degrees / 90.0) * 90;
        return ((quarter % 360) + 360) % 360;
    }
}
