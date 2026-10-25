using System.Globalization;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;

namespace PhotoTri.Core.Viewer;

/// <summary>Une ligne de la chronologie : titre de mois, titre de jour, ou rangée de miniatures.</summary>
public abstract record TimelineRow;

public sealed record MonthHeaderRow(int Year, int Month, string Title, int Count) : TimelineRow;

/// <summary>Titre d'un jour, avec un résumé : « Genève · avec Amélia · 5 photos ».</summary>
public sealed record DayHeaderRow(DateOnly Day, string Title, string Subtitle = "") : TimelineRow;

public sealed record ThumbnailRow(IReadOnlyList<CatalogEntry> Items) : TimelineRow;

/// <summary>Filtres de la visionneuse ; null = pas de filtre.</summary>
public sealed record ViewerFilter(MediaKind? Kind = null, MediaOrigin? Origin = null, string? DeviceId = null)
{
    public bool Accepts(CatalogEntry e) =>
        (Kind is null || e.Kind == Kind)
        && (Origin is null || e.Origin == Origin)
        && (DeviceId is null || e.DeviceId == DeviceId);
}

public static class Timeline
{
    static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>
    /// Découpe des entrées triées de la plus récente à la plus ancienne en lignes : un titre par mois,
    /// un titre par jour, puis des rangées d'au plus <paramref name="columns"/> miniatures.
    /// </summary>
    public static List<TimelineRow> Build(IReadOnlyList<CatalogEntry> entries, int columns)
    {
        columns = Math.Max(1, columns);
        var rows = new List<TimelineRow>();
        foreach (var month in entries.GroupBy(e => (e.TakenAt.Year, e.TakenAt.Month)))
        {
            var items = month.ToList();
            rows.Add(new MonthHeaderRow(month.Key.Year, month.Key.Month, MonthTitle(month.Key.Year, month.Key.Month), items.Count));
            foreach (var day in items.GroupBy(e => DateOnly.FromDateTime(e.TakenAt)))
            {
                var dayItems = day.ToList();
                rows.Add(new DayHeaderRow(day.Key, DayTitle(day.Key), DaySummary(dayItems)));
                rows.AddRange(dayItems.Chunk(columns).Select(chunk => new ThumbnailRow(chunk)));
            }
        }
        return rows;
    }

    /// <summary>Lieu le plus fréquent, personnes reconnues (trois au plus) et nombre d'éléments de la journée.</summary>
    public static string DaySummary(IReadOnlyList<CatalogEntry> day)
    {
        var parts = new List<string>();
        var city = day.Where(e => e.Place is not null).GroupBy(e => e.Place!.City).MaxBy(g => g.Count())?.Key;
        if (city is not null) parts.Add(city);

        var people = day.SelectMany(e => e.People ?? Array.Empty<string>())
            .GroupBy(n => n).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        if (people.Count > 0)
        {
            var shown = people.Take(3).ToList();
            var names = shown.Count == 1 ? shown[0] : string.Join(", ", shown.Take(shown.Count - 1)) + " et " + shown[^1];
            parts.Add("avec " + names + (people.Count > 3 ? "…" : ""));
        }

        var videos = day.Count(e => e.Kind == MediaKind.Video);
        var photos = day.Count - videos;
        if (photos > 0) parts.Add(photos == 1 ? "1 photo" : $"{photos} photos");
        if (videos > 0) parts.Add(videos == 1 ? "1 vidéo" : $"{videos} vidéos");
        return string.Join(" · ", parts);
    }

    public static string MonthTitle(int year, int month) =>
        Capitalize(new DateTime(year, month, 1).ToString("MMMM yyyy", French));

    public static string DayTitle(DateOnly day) =>
        Capitalize(day.ToString("dddd d MMMM yyyy", French));

    static string Capitalize(string s) => char.ToUpper(s[0], French) + s[1..];
}
