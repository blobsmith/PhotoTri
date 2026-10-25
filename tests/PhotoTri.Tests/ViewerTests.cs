using System.Text;
using PhotoTri.Core.Devices;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using PhotoTri.Core.Places;
using PhotoTri.Core.Transfer;
using PhotoTri.Core.Viewer;

namespace PhotoTri.Tests;

public sealed class ViewerTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "phototri-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    static CatalogEntry Entry(long id, DateTime at, MediaKind kind = MediaKind.Photo,
        MediaOrigin origin = MediaOrigin.Camera, string device = "p1") =>
        new(id, $"sha{id}", $"f{id}.jpg", at, kind, origin, device);

    [Fact]
    public void Timeline_groups_by_month_then_day_in_rows()
    {
        var entries = new[]
        {
            Entry(1, new DateTime(2026, 10, 24, 18, 0, 0)),
            Entry(2, new DateTime(2026, 10, 24, 9, 0, 0)),
            Entry(3, new DateTime(2026, 10, 24, 8, 0, 0)),
            Entry(4, new DateTime(2026, 10, 2, 8, 0, 0)),
            Entry(5, new DateTime(2026, 9, 30, 8, 0, 0)),
        };

        var rows = Timeline.Build(entries, columns: 2);

        Assert.Collection(rows,
            r => Assert.Equal(new MonthHeaderRow(2026, 10, "Octobre 2026", 4), r),
            r => Assert.Equal("Samedi 24 octobre 2026", Assert.IsType<DayHeaderRow>(r).Title),
            r => Assert.Equal(new long[] { 1, 2 }, Assert.IsType<ThumbnailRow>(r).Items.Select(i => i.Id)),
            r => Assert.Equal(new long[] { 3 }, Assert.IsType<ThumbnailRow>(r).Items.Select(i => i.Id)),
            r => Assert.Equal("Vendredi 2 octobre 2026", Assert.IsType<DayHeaderRow>(r).Title),
            r => Assert.Equal(new long[] { 4 }, Assert.IsType<ThumbnailRow>(r).Items.Select(i => i.Id)),
            r => Assert.Equal(new MonthHeaderRow(2026, 9, "Septembre 2026", 1), r),
            r => Assert.IsType<DayHeaderRow>(r),
            r => Assert.Equal(new long[] { 5 }, Assert.IsType<ThumbnailRow>(r).Items.Select(i => i.Id)));
    }

    [Fact]
    public void Day_summary_names_the_place_the_people_and_the_count()
    {
        var geneve = new Place("Genève", "Canton de Genève", "CH", "Suisse", 0);
        var annecy = new Place("Annecy", "Auvergne-Rhône-Alpes", "FR", "France", 0);
        var day = new[]
        {
            Entry(1, new DateTime(2026, 10, 10, 9, 0, 0)) with { Place = geneve, People = new[] { "Amélia" } },
            Entry(2, new DateTime(2026, 10, 10, 10, 0, 0)) with { Place = geneve, People = new[] { "Amélia", "Aurélien" } },
            Entry(3, new DateTime(2026, 10, 10, 11, 0, 0)) with { Place = annecy },
            Entry(4, new DateTime(2026, 10, 10, 12, 0, 0), MediaKind.Video),
        };
        Assert.Equal("Genève · avec Amélia et Aurélien · 3 photos · 1 vidéo", Timeline.DaySummary(day));
        Assert.Equal("1 photo", Timeline.DaySummary(new[] { Entry(5, DateTime.Today) }));
    }

    [Fact]
    public void Search_matches_people_places_and_file_names_ignoring_accents()
    {
        var entry = Entry(1, DateTime.Today) with
        {
            Place = new Place("Genève", "Canton de Genève", "CH", "Suisse", 0),
            Address = "Place du Molard, Genève",
            People = new[] { "Amélia" },
        };
        Assert.True(LibrarySearch.Matcher("amelia")(entry));
        Assert.True(LibrarySearch.Matcher("GENEVE amélia")(entry));
        Assert.True(LibrarySearch.Matcher("molard")(entry));
        Assert.True(LibrarySearch.Matcher("f1.jpg")(entry));
        Assert.True(LibrarySearch.Matcher("   ")(entry));
        Assert.False(LibrarySearch.Matcher("amelia tignes")(entry));
    }

    [Fact]
    public void Filter_combines_kind_origin_and_phone()
    {
        var video = Entry(1, DateTime.Now, MediaKind.Video, MediaOrigin.WhatsApp, "p2");
        Assert.True(new ViewerFilter().Accepts(video));
        Assert.True(new ViewerFilter(MediaKind.Video, MediaOrigin.WhatsApp, "p2").Accepts(video));
        Assert.False(new ViewerFilter(Kind: MediaKind.Photo).Accepts(video));
        Assert.False(new ViewerFilter(Origin: MediaOrigin.Camera).Accepts(video));
        Assert.False(new ViewerFilter(DeviceId: "p1").Accepts(video));
    }

    [Fact]
    public void Catalog_reads_transferred_files_newest_first_and_survives_as_snapshot()
    {
        var phoneDir = Path.Combine(_work, "phone");
        var library = Directory.CreateDirectory(Path.Combine(_work, "library")).FullName;
        foreach (var (name, text) in new[] { ("IMG_20240101_100000.jpg", "a"), ("VID_20250601_100000.mp4", "b") })
        {
            var path = Path.Combine(phoneDir, "DCIM", "Camera", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, Encoding.UTF8);
        }
        var phone = new FolderPhoneSource(phoneDir, "p1", "Téléphone de Paul");
        using (var index = new LibraryIndex(library))
        {
            var engine = new TransferEngine(index);
            engine.Execute(phone, engine.Plan(phone, new MediaFilter()), deleteFromPhone: false);
        }

        var catalog = LibraryCatalog.Load(LibraryCatalog.DatabasePath(library));
        Assert.Equal(new[] { "VID_20250601_100000.mp4", "IMG_20240101_100000.jpg" },
            catalog.Entries.Select(e => Path.GetFileName(e.RelativePath)));
        Assert.Equal(MediaKind.Video, catalog.Entries[0].Kind);
        Assert.Equal(new DateTime(2025, 6, 1, 10, 0, 0), catalog.Entries[0].TakenAt);
        Assert.Equal("Téléphone de Paul", catalog.Devices["p1"]);

        // Copie locale : lisible même quand la bibliothèque a disparu (disque débranché).
        var location = new LibraryLocation("\\\\?\\Volume{test-" + Guid.NewGuid() + "}\\", "Photos", library);
        LibraryCatalog.SaveSnapshot(library, location);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(library, recursive: true);
        try
        {
            var offline = LibraryCatalog.Load(LibraryCatalog.SnapshotPath(location));
            Assert.Equal(2, offline.Entries.Count);
        }
        finally
        {
            File.Delete(LibraryCatalog.SnapshotPath(location));
        }
    }

    [Fact]
    public void Missing_database_gives_empty_catalog()
    {
        Assert.Empty(LibraryCatalog.Load(Path.Combine(_work, "nothing.db")).Entries);
    }
}
