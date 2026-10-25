using System.Globalization;
using System.Text.RegularExpressions;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.QuickTime;

namespace PhotoTri.Core.Media;

public enum DateSource { Exif, VideoMetadata, FileName, PhoneModified, ImportTime }

public readonly record struct ResolvedDate(DateTime Value, DateSource Source);

/// <summary>
/// Détermine la date de prise de vue : EXIF, puis métadonnées vidéo, puis nom du fichier,
/// puis date de modification sur le téléphone.
/// </summary>
public static partial class DateResolver
{
    static readonly DateTime MinValid = new(1990, 1, 1);

    public static ResolvedDate Resolve(string localFile, string originalName, DateTime? phoneModified)
    {
        if (TryFromMetadata(localFile, out var date, out var source))
            return new(date, source);
        if (TryFromFileName(originalName, out date))
            return new(date, DateSource.FileName);
        if (phoneModified is { } m && IsPlausible(m))
            return new(m, DateSource.PhoneModified);
        return new(DateTime.Now, DateSource.ImportTime);
    }

    public static bool TryFromMetadata(string localFile, out DateTime date, out DateSource source)
    {
        date = default;
        source = default;
        IReadOnlyList<MetadataExtractor.Directory> dirs;
        try
        {
            dirs = ImageMetadataReader.ReadMetadata(localFile);
        }
        catch (Exception)
        {
            // Format inconnu ou fichier abîmé : on se rabat sur le nom du fichier.
            return false;
        }

        foreach (var exif in dirs.OfType<ExifSubIfdDirectory>())
        {
            if ((exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out date)
                 || exif.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out date))
                && IsPlausible(date))
            {
                source = DateSource.Exif;
                return true;
            }
        }

        // Les vidéos stockent leur date de création en UTC.
        foreach (var movie in dirs.OfType<QuickTimeMovieHeaderDirectory>())
        {
            if (movie.TryGetDateTime(QuickTimeMovieHeaderDirectory.TagCreated, out date) && IsPlausible(date))
            {
                date = DateTime.SpecifyKind(date, DateTimeKind.Utc).ToLocalTime();
                source = DateSource.VideoMetadata;
                return true;
            }
        }

        return false;
    }

    // IMG_20261024_143012, VID_20261024_143012, PXL_20261024_143012345, Screenshot_20261024-143012
    [GeneratedRegex(@"(?<!\d)(20\d{2})(\d{2})(\d{2})[_-](\d{2})(\d{2})(\d{2})")]
    private static partial Regex CompactDateTime();

    // Screenshot_2026-10-24-14-30-12
    [GeneratedRegex(@"(?<!\d)(20\d{2})-(\d{2})-(\d{2})[-_ ](\d{2})[-.](\d{2})[-.](\d{2})")]
    private static partial Regex DashedDateTime();

    // IMG-20261024-WA0001 (WhatsApp) et autres noms qui ne portent que la date
    [GeneratedRegex(@"(?<!\d)(20\d{2})(\d{2})(\d{2})(?!\d)")]
    private static partial Regex CompactDate();

    public static bool TryFromFileName(string name, out DateTime date)
    {
        foreach (var regex in new[] { CompactDateTime(), DashedDateTime() })
        {
            var m = regex.Match(name);
            if (m.Success && TryBuild(m, withTime: true, out date)) return true;
        }
        var d = CompactDate().Match(name);
        if (d.Success && TryBuild(d, withTime: false, out date)) return true;
        date = default;
        return false;
    }

    static bool TryBuild(Match m, bool withTime, out DateTime date)
    {
        var text = withTime
            ? $"{m.Groups[1]}{m.Groups[2]}{m.Groups[3]}{m.Groups[4]}{m.Groups[5]}{m.Groups[6]}"
            : $"{m.Groups[1]}{m.Groups[2]}{m.Groups[3]}120000";
        return DateTime.TryParseExact(text, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeLocal, out date)
               && IsPlausible(date);
    }

    static bool IsPlausible(DateTime d) => d >= MinValid && d <= DateTime.Now.AddDays(2);
}
