using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PhotoTri.Core.Media;
using PhotoTri.Core.Places;

namespace PhotoTri.Core.Library;

public sealed record CatalogEntry(
    long Id,
    string Sha256,
    string RelativePath,
    DateTime TakenAt,
    MediaKind Kind,
    MediaOrigin Origin,
    string DeviceId,
    GeoPoint? Location = null,
    Place? Place = null,
    string? Address = null,
    IReadOnlyList<string>? People = null);

/// <summary>
/// Contenu de la bibliothèque tel qu'affiché par la visionneuse, lu en lecture seule depuis la base.
/// Une copie de la base est gardée sur le PC (<see cref="SnapshotPath"/>) : quand le disque externe
/// n'est pas branché, la visionneuse peut encore afficher les photos et leurs miniatures.
/// </summary>
public sealed class LibraryCatalog
{
    public IReadOnlyList<CatalogEntry> Entries { get; }

    /// <summary>Nom de chaque téléphone, par identifiant.</summary>
    public IReadOnlyDictionary<string, string> Devices { get; }

    LibraryCatalog(IReadOnlyList<CatalogEntry> entries, IReadOnlyDictionary<string, string> devices)
    {
        Entries = entries;
        Devices = devices;
    }

    public static LibraryCatalog Empty { get; } = new(Array.Empty<CatalogEntry>(), new Dictionary<string, string>());

    public static string DatabasePath(string libraryRoot) =>
        Path.Combine(libraryRoot, LibraryLayout.DataFolderName, "library.db");

    /// <summary>Copie locale de la base pour une bibliothèque donnée (une par emplacement).</summary>
    public static string SnapshotPath(LibraryLocation location)
    {
        var key = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(location.VolumeId + "|" + location.RelativePath.ToLowerInvariant())))[..16];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PhotoTri", "Snapshots", key + ".db");
    }

    /// <summary>Lit la base ; renvoie un catalogue vide si elle n'existe pas encore.</summary>
    public static LibraryCatalog Load(string databasePath)
    {
        if (!File.Exists(databasePath)) return Empty;

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        db.Open();

        // Une copie locale faite avant l'apparition des lieux n'a pas leurs colonnes.
        bool hasPlaces;
        using (var check = db.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM pragma_table_info('files') WHERE name = 'address'";
            hasPlaces = check.ExecuteScalar() is not null;
        }

        // Personnes reconnues dans chaque photo.
        var people = new Dictionary<long, List<string>>();
        using (var check = db.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'faces'";
            if (check.ExecuteScalar() is not null)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    SELECT DISTINCT f.file_id, p.name FROM faces f JOIN persons p ON p.id = f.person_id ORDER BY p.name
                    """;
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!people.TryGetValue(r.GetInt64(0), out var list)) people[r.GetInt64(0)] = list = new List<string>();
                    list.Add(r.GetString(1));
                }
            }
        }

        var entries = new List<CatalogEntry>();
        using (var cmd = db.CreateCommand())
        {
            var placeColumns = hasPlaces
                ? "lat, lon, city, region, country_code, country, address"
                : "NULL, NULL, NULL, NULL, NULL, NULL, NULL";
            cmd.CommandText = $"SELECT id, sha256, rel_path, taken_at, kind, origin, device_id, {placeColumns} FROM files ORDER BY taken_at DESC, id DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                GeoPoint? location = r.IsDBNull(7) ? null : new GeoPoint(r.GetDouble(7), r.GetDouble(8));
                Place? place = r.IsDBNull(9)
                    ? null
                    : new Place(r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12), 0);
                entries.Add(new CatalogEntry(
                    r.GetInt64(0),
                    r.GetString(1),
                    r.GetString(2),
                    DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Enum.Parse<MediaKind>(r.GetString(4)),
                    Enum.Parse<MediaOrigin>(r.GetString(5)),
                    r.GetString(6),
                    location,
                    place,
                    r.IsDBNull(13) ? null : r.GetString(13),
                    people.GetValueOrDefault(r.GetInt64(0))));
            }
        }

        var devices = new Dictionary<string, string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT device_id, name FROM devices";
            using var r = cmd.ExecuteReader();
            while (r.Read()) devices[r.GetString(0)] = r.GetString(1);
        }
        return new LibraryCatalog(entries, devices);
    }

    /// <summary>Met à jour la copie locale de la base (sauvegarde SQLite, sûre même pendant l'écriture).</summary>
    public static void SaveSnapshot(string libraryRoot, LibraryLocation location)
    {
        var source = DatabasePath(libraryRoot);
        if (!File.Exists(source)) return;
        var target = SnapshotPath(location);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";

        using (var from = new SqliteConnection($"Data Source={source};Mode=ReadOnly;Pooling=False"))
        using (var to = new SqliteConnection($"Data Source={temp};Pooling=False"))
        {
            from.Open();
            to.Open();
            from.BackupDatabase(to);
        }
        File.Move(temp, target, overwrite: true);
    }
}
