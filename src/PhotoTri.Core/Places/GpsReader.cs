using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace PhotoTri.Core.Places;

/// <summary>Position GPS d'une photo (EXIF) ou d'une vidéo (métadonnées QuickTime, format ISO 6709).</summary>
public static partial class GpsReader
{
    public static GeoPoint? Read(string path)
    {
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try
        {
            dirs = ImageMetadataReader.ReadMetadata(path);
        }
        catch (Exception)
        {
            return null;
        }

        foreach (var gps in dirs.OfType<GpsDirectory>())
        {
            if (gps.GetGeoLocation() is { IsZero: false } loc && IsValid(loc.Latitude, loc.Longitude))
                return new GeoPoint(loc.Latitude, loc.Longitude);
        }
        foreach (var meta in dirs.OfType<QuickTimeMetadataHeaderDirectory>())
        {
            if (ParseIso6709(meta.GetString(QuickTimeMetadataHeaderDirectory.TagGpsLocation)) is { } point)
                return point;
        }
        return null;
    }

    // « +45.9051+006.4242/ » ou « +45.9051+006.4242+1234.5/ » (avec altitude)
    [GeneratedRegex(@"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)")]
    private static partial Regex Iso6709();

    public static GeoPoint? ParseIso6709(string? value)
    {
        if (value is null) return null;
        var m = Iso6709().Match(value.Trim());
        if (!m.Success) return null;
        var lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var lon = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return IsValid(lat, lon) ? new GeoPoint(lat, lon) : null;
    }

    // 0,0 est la valeur des téléphones qui n'avaient pas de position.
    static bool IsValid(double lat, double lon) =>
        lat is >= -90 and <= 90 && lon is >= -180 and <= 180 && !(lat == 0 && lon == 0);
}
