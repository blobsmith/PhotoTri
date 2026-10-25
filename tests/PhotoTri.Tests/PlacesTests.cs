using System.Text.Json;
using PhotoTri.Core.Devices;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using PhotoTri.Core.Places;
using PhotoTri.Core.Transfer;

namespace PhotoTri.Tests;

public sealed class PlacesTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "phototri-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    [Theory]
    [InlineData("+45.9051+006.4242/", 45.9051, 6.4242)]
    [InlineData("-33.8688+151.2093+012.000/", -33.8688, 151.2093)]
    [InlineData("+40.7128-074.0060/", 40.7128, -74.006)]
    public void Parses_video_gps_iso6709(string value, double lat, double lon)
    {
        Assert.Equal(new GeoPoint(lat, lon), GpsReader.ParseIso6709(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+00.0000+000.0000/")]
    [InlineData("n'importe quoi")]
    public void Ignores_missing_or_empty_video_gps(string? value)
    {
        Assert.Null(GpsReader.ParseIso6709(value));
    }

    [Fact]
    public void Reads_photo_gps_from_exif()
    {
        var file = Path.Combine(Directory.CreateDirectory(_work).FullName, "gps.jpg");
        File.WriteAllBytes(file, TestImages.JpegWithGps(46.2044, 6.1432));
        var point = GpsReader.Read(file)!.Value;
        Assert.Equal(46.2044, point.Latitude, precision: 4);
        Assert.Equal(6.1432, point.Longitude, precision: 4);
    }

    [Theory]
    [InlineData(46.2044, 6.1432, "Genève", "Suisse")]
    [InlineData(48.8584, 2.2945, "Paris", "France")]
    [InlineData(51.5007, -0.1246, "Londres", "Royaume-Uni")]
    [InlineData(-33.8568, 151.2153, "Sydney", "Australie")]
    public void Finds_the_nearest_city_with_french_names(double lat, double lon, string city, string country)
    {
        var place = OfflineGeocoder.Instance.Find(new GeoPoint(lat, lon))!;
        Assert.Equal(city, place.City);
        Assert.Equal(country, place.Country);
        Assert.True(place.DistanceKm < 15, $"{place.City} à {place.DistanceKm} km");
    }

    [Fact]
    public void No_city_in_the_middle_of_the_ocean()
    {
        Assert.Null(OfflineGeocoder.Instance.Find(new GeoPoint(35.0, -40.0)));
    }

    [Fact]
    public void Works_across_the_date_line()
    {
        // Suva (Fidji), à 178° E : les cases voisines à l'ouest de -180° doivent être cherchées.
        Assert.Equal("FJ", OfflineGeocoder.Instance.Find(new GeoPoint(-18.14, 178.44))!.CountryCode);
    }

    [Fact]
    public void Formats_nominatim_address()
    {
        using var json = JsonDocument.Parse("""
            { "address": { "house_number": "12", "road": "Rue de la Paix", "suburb": "Opéra",
                           "city": "Paris", "postcode": "75002", "country": "France" } }
            """);
        Assert.Equal("12 Rue de la Paix, Opéra, Paris", AddressResolver.FormatAddress(json.RootElement));
    }

    [Fact]
    public void Formats_nominatim_address_in_the_countryside()
    {
        using var json = JsonDocument.Parse("""
            { "address": { "natural": "Mont Salève", "hamlet": "Le Coin", "village": "Monnetier-Mornex" } }
            """);
        Assert.Equal("Mont Salève, Le Coin, Monnetier-Mornex", AddressResolver.FormatAddress(json.RootElement));
    }

    [Fact]
    public void Indexes_places_then_addresses_and_exposes_them_in_the_catalog()
    {
        var phone = Path.Combine(_work, "phone", "DCIM", "Camera");
        Directory.CreateDirectory(phone);
        File.WriteAllBytes(Path.Combine(phone, "IMG_20240101_100000.jpg"), TestImages.JpegWithGps(46.2044, 6.1432));
        File.WriteAllBytes(Path.Combine(phone, "IMG_20240101_110000.jpg"), TestImages.JpegWithGps(46.20441, 6.14321));
        File.WriteAllText(Path.Combine(phone, "IMG_20240101_120000.jpg"), "sans gps");
        var library = Directory.CreateDirectory(Path.Combine(_work, "library")).FullName;

        using (var index = new LibraryIndex(library))
        {
            var source = new FolderPhoneSource(Path.Combine(_work, "phone"), "p1", "Test");
            var engine = new TransferEngine(index);
            var report = engine.Execute(source, engine.Plan(source, new MediaFilter()), deleteFromPhone: false);
            Assert.Empty(report.Errors);
            Assert.Equal(3, report.Copied);

            Assert.Equal(3, index.FilesWithoutGeo(10).Count);
            Assert.Equal(3, LocationIndexer.Run(index));
            Assert.Equal(0, LocationIndexer.Run(index)); // déjà traités

            // Les deux photos à quelques mètres l'une de l'autre partagent une seule demande d'adresse.
            var keys = index.GeoKeysWithoutAddress(10);
            Assert.Single(keys);
            index.SetAddress(keys[0].GeoKey, new OsmAddress("Place du Molard, Genève", "Genève", "Genève", "CH", "Suisse"));
            Assert.Equal(0, index.CountGeoKeysWithoutAddress());
        }

        var catalog = LibraryCatalog.Load(LibraryCatalog.DatabasePath(library));
        var located = catalog.Entries.Where(e => e.Place is not null).ToList();
        Assert.Equal(2, located.Count);
        Assert.All(located, e =>
        {
            Assert.Equal("Genève", e.Place!.City);
            Assert.Equal("Place du Molard, Genève", e.Address);
        });
        Assert.Null(catalog.Entries.Single(e => e.RelativePath.EndsWith("120000.jpg")).Place);
    }

    [Fact]
    public void Small_villages_are_known_offline()
    {
        // Presinge (GE, ~700 habitants) : absente d'une base limitée aux villes de plus de 1 000 habitants,
        // la photo était attribuée à Ville-la-Grand, en France, de l'autre côté de la frontière.
        var place = OfflineGeocoder.Instance.Find(new GeoPoint(46.2201, 6.2552))!;
        Assert.Equal("Presinge", place.City);
        Assert.Equal("CH", place.CountryCode);
    }

    [Fact]
    public void Reads_the_real_commune_from_nominatim()
    {
        using var json = JsonDocument.Parse("""
            { "address": { "house_number": "1", "road": "Rue des Prairies", "neighbourhood": "Sous Cassan",
                           "town": "Annemasse", "state": "Auvergne-Rhône-Alpes", "country": "France", "country_code": "fr" } }
            """);
        var osm = AddressResolver.Parse(json.RootElement)!;
        Assert.Equal(new OsmAddress("1 Rue des Prairies, Sous Cassan, Annemasse", "Annemasse", "Auvergne-Rhône-Alpes", "FR", "France"), osm);
    }

    [Fact]
    public void Commune_from_openstreetmap_replaces_the_offline_estimate_and_survives_reindexing()
    {
        // Photo à Annemasse, mais plus près du centre de Ville-la-Grand.
        var phone = Path.Combine(_work, "phone", "DCIM", "Camera");
        Directory.CreateDirectory(phone);
        File.WriteAllBytes(Path.Combine(phone, "IMG_20230807_144435.jpg"), TestImages.JpegWithGps(46.1936, 6.2577));
        var library = Directory.CreateDirectory(Path.Combine(_work, "library")).FullName;
        using var index = new LibraryIndex(library);
        var source = new FolderPhoneSource(Path.Combine(_work, "phone"), "p1", "Test");
        var engine = new TransferEngine(index);
        engine.Execute(source, engine.Plan(source, new MediaFilter()), deleteFromPhone: false);
        LocationIndexer.Run(index);
        string City() => LibraryCatalog.Load(LibraryCatalog.DatabasePath(library)).Entries.Single().Place!.City;
        Assert.Equal("Ville-la-Grand", City());

        var key = Assert.Single(index.GeoKeysWithoutAddress(10)).GeoKey;
        index.SetAddress(key, new OsmAddress("1 Rue des Prairies, Sous Cassan, Annemasse", "Annemasse", "Auvergne-Rhône-Alpes", "FR", "France"));
        Assert.Equal("Annemasse", City());

        // Un nouveau calcul hors ligne (nouvelle base des villes) garde la commune d'OpenStreetMap.
        var id = LibraryCatalog.Load(LibraryCatalog.DatabasePath(library)).Entries.Single().Id;
        index.SetGeo(id, new GeoPoint(46.1936, 6.2577), OfflineGeocoder.Instance.Find(new GeoPoint(46.1936, 6.2577)));
        Assert.Equal("Annemasse", City());
    }

    [Fact]
    public void Opens_libraries_created_before_places_existed()
    {
        var library = Directory.CreateDirectory(Path.Combine(_work, "old")).FullName;
        var db = Path.Combine(Directory.CreateDirectory(Path.Combine(library, LibraryLayout.DataFolderName)).FullName, "library.db");
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE files (id INTEGER PRIMARY KEY, sha256 TEXT NOT NULL, size INTEGER NOT NULL, rel_path TEXT NOT NULL UNIQUE,
                    taken_at TEXT NOT NULL, date_source TEXT NOT NULL, kind TEXT NOT NULL, origin TEXT NOT NULL,
                    device_id TEXT NOT NULL, source_path TEXT NOT NULL, imported_at TEXT NOT NULL);
                CREATE TABLE devices (device_id TEXT PRIMARY KEY, name TEXT NOT NULL, last_seen TEXT NOT NULL);
                INSERT INTO files VALUES (1, 'abc', 1, '2024\x.jpg', '2024-01-01T10:00:00', 'Exif', 'Photo', 'Camera', 'p1', 'x', '2024-01-02');
                """;
            cmd.ExecuteNonQuery();
        }

        // Lecture d'une copie locale ancienne : pas de colonnes de lieux, pas d'erreur.
        Assert.Null(Assert.Single(LibraryCatalog.Load(db).Entries).Place);

        // Ouverture en écriture : les colonnes sont ajoutées et le fichier est à traiter.
        using var index = new LibraryIndex(library);
        Assert.Single(index.FilesWithoutGeo(10));
    }
}
