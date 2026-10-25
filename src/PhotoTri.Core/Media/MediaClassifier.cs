namespace PhotoTri.Core.Media;

public enum MediaKind { Photo, Video }

public enum MediaOrigin { Camera, Screenshot, WhatsApp, OtherApp, Other }

/// <summary>Quels fichiers inclure dans un transfert (cases à cocher des réglages).</summary>
public sealed record MediaFilter(
    bool Photos = true,
    bool Videos = true,
    bool Screenshots = true,
    bool Apps = true)
{
    public bool Accepts(MediaKind kind, MediaOrigin origin)
    {
        if (kind == MediaKind.Photo && !Photos) return false;
        if (kind == MediaKind.Video && !Videos) return false;
        return origin switch
        {
            MediaOrigin.Screenshot => Screenshots,
            MediaOrigin.WhatsApp or MediaOrigin.OtherApp => Apps,
            _ => true,
        };
    }
}

/// <summary>Reconnaît le type et l'origine d'un fichier à partir de son chemin sur le téléphone.</summary>
public static class MediaClassifier
{
    static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".heic", ".heif", ".png", ".webp", ".gif", ".dng" };

    static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".3gp", ".mkv", ".webm", ".avi", ".m4v" };

    /// <summary>Dossiers parcourus sur chaque stockage du téléphone, relatifs à sa racine.</summary>
    public static readonly string[] ScanRoots =
    {
        "DCIM",
        "Pictures",
        "Movies",
        "Android/media/com.whatsapp/WhatsApp/Media",
        "WhatsApp/Media",
        "Telegram",
    };

    // Les photos « envoyées » de WhatsApp sont des copies recompressées des photos de l'appareil,
    // et les statuts, stickers, GIF animés, photos de profil ou captures de jeux ne sont pas des photos personnelles.
    static readonly string[] ExcludedSegments =
    {
        "/sent/", "/.statuses/", "/whatsapp stickers/", "/whatsapp profile photos/", "/whatsapp animated gifs/",
        "/.thumbnails/", "/game media/", "/pokemongo/",
    };

    static readonly string[] OtherAppMarkers =
        { "messenger", "telegram", "signal", "instagram", "snapchat", "facebook", "viber" };

    public static MediaKind? KindOf(string path)
    {
        var ext = Path.GetExtension(path);
        if (PhotoExtensions.Contains(ext)) return MediaKind.Photo;
        if (VideoExtensions.Contains(ext)) return MediaKind.Video;
        return null;
    }

    public static bool IsExcluded(string path)
    {
        var p = Normalize(path);
        var name = Path.GetFileName(p);
        // Fichiers en attente ou dans la corbeille d'Android, dossiers cachés.
        if (name.StartsWith('.')) return true;
        if (p.Contains("/.")) return true;
        return ExcludedSegments.Any(p.Contains);
    }

    public static MediaOrigin OriginOf(string path)
    {
        var p = Normalize(path);
        if (p.Contains("/whatsapp")) return MediaOrigin.WhatsApp;
        if (p.Contains("screenshot") || p.Contains("capture")) return MediaOrigin.Screenshot;
        if (OtherAppMarkers.Any(p.Contains)) return MediaOrigin.OtherApp;
        if (p.Contains("/dcim/")) return MediaOrigin.Camera;
        return MediaOrigin.Other;
    }

    public static string Label(MediaOrigin origin) => origin switch
    {
        MediaOrigin.Camera => "Appareil photo",
        MediaOrigin.Screenshot => "Capture d'écran",
        MediaOrigin.WhatsApp => "WhatsApp",
        MediaOrigin.OtherApp => "Autre application",
        _ => "Autre",
    };

    static string Normalize(string path) => "/" + path.Replace('\\', '/').Trim('/').ToLowerInvariant();
}
