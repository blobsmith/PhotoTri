using System.Globalization;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;

namespace PhotoTri.Core.Viewer;

/// <summary>Une ligne de la chronologie : titre de mois, titre de jour, ou rangée de miniatures.</summary>
public abstract record TimelineRow;

public sealed record MonthHeaderRow(int Year, int Month, string Title, int Count) : TimelineRow;

public sealed record DayHeaderRow(DateOnly Day, string Title) : TimelineRow;

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
                rows.Add(new DayHeaderRow(day.Key, DayTitle(day.Key)));
                rows.AddRange(day.Chunk(columns).Select(chunk => new ThumbnailRow(chunk)));
            }
        }
        return rows;
    }

    public static string MonthTitle(int year, int month) =>
        Capitalize(new DateTime(year, month, 1).ToString("MMMM yyyy", French));

    public static string DayTitle(DateOnly day) =>
        Capitalize(day.ToString("dddd d MMMM yyyy", French));

    static string Capitalize(string s) => char.ToUpper(s[0], French) + s[1..];
}
