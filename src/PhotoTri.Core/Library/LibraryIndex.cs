using Microsoft.Data.Sqlite;
using PhotoTri.Core.Media;
using PhotoTri.Core.Places;

namespace PhotoTri.Core.Library;

public sealed record LibraryFile(
    string Sha256,
    long Size,
    string RelativePath,
    DateTime TakenAt,
    DateSource DateSource,
    MediaKind Kind,
    MediaOrigin Origin,
    string DeviceId,
    string SourcePath);

/// <summary>
/// Base SQLite stockée dans la bibliothèque (.phototri\library.db) : elle suit les photos
/// si la bibliothèque est déplacée ou si le disque externe est branché sur un autre PC.
/// </summary>
public sealed partial class LibraryIndex : IDisposable
{
    readonly SqliteConnection _db;

    public string LibraryRoot { get; }

    public LibraryIndex(string libraryRoot)
    {
        LibraryRoot = libraryRoot;
        var dataDir = Path.Combine(libraryRoot, LibraryLayout.DataFolderName);
        var info = System.IO.Directory.CreateDirectory(dataDir);
        info.Attributes |= FileAttributes.Hidden;

        _db = new SqliteConnection($"Data Source={Path.Combine(dataDir, "library.db")}");
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS files (
                id            INTEGER PRIMARY KEY,
                sha256        TEXT NOT NULL,
                size          INTEGER NOT NULL,
                rel_path      TEXT NOT NULL UNIQUE,
                taken_at      TEXT NOT NULL,
                date_source   TEXT NOT NULL,
                kind          TEXT NOT NULL,
                origin        TEXT NOT NULL,
                device_id     TEXT NOT NULL,
                source_path   TEXT NOT NULL,
                imported_at   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_files_sha ON files(sha256);
            CREATE INDEX IF NOT EXISTS ix_files_taken ON files(taken_at);
            -- Fichiers du téléphone déjà traités (copiés ou reconnus comme doublons).
            CREATE TABLE IF NOT EXISTS device_items (
                device_id     TEXT NOT NULL,
                source_path   TEXT NOT NULL,
                size          INTEGER NOT NULL,
                sha256        TEXT NOT NULL,
                PRIMARY KEY (device_id, source_path, size)
            );
            CREATE TABLE IF NOT EXISTS devices (
                device_id     TEXT PRIMARY KEY,
                name          TEXT NOT NULL,
                last_seen     TEXT NOT NULL
            );
            -- Adresses déjà demandées à OpenStreetMap, par position arrondie (~100 m).
            CREATE TABLE IF NOT EXISTS addresses (
                geo_key       TEXT PRIMARY KEY,
                address       TEXT,
                fetched_at    TEXT NOT NULL
            );
            """);

        // Colonnes des lieux, ajoutées aux bibliothèques créées avant leur apparition.
        AddColumnIfMissing("files", "geo_done", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("files", "lat", "REAL");
        AddColumnIfMissing("files", "lon", "REAL");
        AddColumnIfMissing("files", "geo_key", "TEXT");
        AddColumnIfMissing("files", "city", "TEXT");
        AddColumnIfMissing("files", "region", "TEXT");
        AddColumnIfMissing("files", "country_code", "TEXT");
        AddColumnIfMissing("files", "country", "TEXT");
        AddColumnIfMissing("files", "address", "TEXT");
        Execute("CREATE INDEX IF NOT EXISTS ix_files_geo_key ON files(geo_key)");
        // Commune réelle donnée par OpenStreetMap ; version 1 = adresse demandée avant qu'on la garde.
        AddColumnIfMissing("addresses", "city", "TEXT");
        AddColumnIfMissing("addresses", "region", "TEXT");
        AddColumnIfMissing("addresses", "country_code", "TEXT");
        AddColumnIfMissing("addresses", "country", "TEXT");
        AddColumnIfMissing("addresses", "version", "INTEGER NOT NULL DEFAULT 1");
        CreateFaceTables();
    }

    /// <summary>
    /// Version du calcul des lieux : les fichiers traités par une version plus ancienne (base des villes
    /// de plus de 1 000 habitants) sont recalculés automatiquement.
    /// </summary>
    public const int GeoVersion = 2;

    /// <summary>Version des adresses en ligne : celles d'une version plus ancienne sont redemandées.</summary>
    public const int AddressVersion = 2;

    void AddColumnIfMissing(string table, string column, string definition)
    {
        using var check = Command($"SELECT 1 FROM pragma_table_info('{table}') WHERE name = $c", ("$c", column));
        if (check.ExecuteScalar() is null) Execute($"ALTER TABLE {table} ADD COLUMN {column} {definition}");
    }

    // ---- Lieux ----

    /// <summary>Fichiers dont la position n'a pas encore été lue.</summary>
    public List<(long Id, string RelativePath)> FilesWithoutGeo(int limit)
    {
        using var cmd = Command("SELECT id, rel_path FROM files WHERE geo_done < $v LIMIT $l", ("$v", GeoVersion), ("$l", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    /// <summary>Clé de regroupement des positions proches (~100 m) pour les adresses en ligne.</summary>
    public static string GeoKey(GeoPoint p) =>
        FormattableString.Invariant($"{Math.Round(p.Latitude, 3):0.000},{Math.Round(p.Longitude, 3):0.000}");

    public void SetGeo(long id, GeoPoint? point, Place? place)
    {
        // La ville hors ligne d'abord, puis la commune d'OpenStreetMap si elle est déjà connue pour cette position.
        using var cmd = Command("""
            UPDATE files SET geo_done = $v, lat = $lat, lon = $lon, geo_key = $k,
                city = $city, region = $region, country_code = $cc, country = $country,
                address = (SELECT address FROM addresses WHERE geo_key = $k)
            WHERE id = $id;
            UPDATE files SET city = a.city, region = a.region, country_code = a.country_code, country = a.country
            FROM addresses a
            WHERE files.id = $id AND a.geo_key = files.geo_key AND a.city IS NOT NULL AND a.country_code IS NOT NULL;
            """,
            ("$id", id), ("$v", GeoVersion),
            ("$lat", (object?)point?.Latitude ?? DBNull.Value), ("$lon", (object?)point?.Longitude ?? DBNull.Value),
            ("$k", point is { } p ? GeoKey(p) : DBNull.Value),
            ("$city", (object?)place?.City ?? DBNull.Value), ("$region", (object?)place?.Region ?? DBNull.Value),
            ("$cc", (object?)place?.CountryCode ?? DBNull.Value), ("$country", (object?)place?.Country ?? DBNull.Value));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Positions dont l'adresse n'a encore jamais été demandée, avec un point représentatif.</summary>
    public List<(string GeoKey, GeoPoint Point)> GeoKeysWithoutAddress(int limit)
    {
        using var cmd = Command("""
            SELECT geo_key, AVG(lat), AVG(lon) FROM files
            WHERE geo_key IS NOT NULL AND geo_key NOT IN (SELECT geo_key FROM addresses WHERE version >= $v)
            GROUP BY geo_key LIMIT $l
            """, ("$v", AddressVersion), ("$l", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<(string, GeoPoint)>();
        while (r.Read()) list.Add((r.GetString(0), new GeoPoint(r.GetDouble(1), r.GetDouble(2))));
        return list;
    }

    public int CountGeoKeysWithoutAddress()
    {
        using var cmd = Command("""
            SELECT COUNT(DISTINCT geo_key) FROM files
            WHERE geo_key IS NOT NULL AND geo_key NOT IN (SELECT geo_key FROM addresses WHERE version >= $v)
            """, ("$v", AddressVersion));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Enregistre la réponse d'OpenStreetMap (null s'il ne connaît rien à cet endroit) pour toutes les photos
    /// de cette position : l'adresse, et la commune réelle, qui remplace la ville estimée hors ligne.
    /// </summary>
    public void SetAddress(string geoKey, OsmAddress? osm)
    {
        var hasCity = osm?.City is not null && osm.CountryCode is not null;
        using var tx = _db.BeginTransaction();
        using (var cmd = Command("""
                   INSERT OR REPLACE INTO addresses (geo_key, address, fetched_at, city, region, country_code, country, version)
                   VALUES ($k, $a, $t, $city, $region, $cc, $country, $v)
                   """,
                   ("$k", geoKey), ("$a", osm?.Text), ("$t", DateTime.Now.ToString("O")),
                   ("$city", hasCity ? osm!.City : null), ("$region", hasCity ? osm!.Region ?? "" : null),
                   ("$cc", hasCity ? osm!.CountryCode : null), ("$country", hasCity ? osm!.Country ?? osm.CountryCode : null),
                   ("$v", AddressVersion)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        using (var cmd = Command(hasCity
                       ? "UPDATE files SET address = $a, city = $city, region = $region, country_code = $cc, country = $country WHERE geo_key = $k"
                       : "UPDATE files SET address = $a WHERE geo_key = $k",
                   ("$k", geoKey), ("$a", osm?.Text), ("$city", osm?.City), ("$region", osm?.Region ?? ""),
                   ("$cc", osm?.CountryCode), ("$country", osm?.Country ?? osm?.CountryCode)))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public bool IsKnownDeviceItem(string deviceId, string sourcePath, long size)
    {
        using var cmd = Command("SELECT 1 FROM device_items WHERE device_id=$d AND source_path=$p AND size=$s",
            ("$d", deviceId), ("$p", sourcePath), ("$s", size));
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Chemin relatif d'un fichier de même contenu déjà présent dans la bibliothèque.</summary>
    public string? FindByHash(string sha256)
    {
        using var cmd = Command("SELECT rel_path FROM files WHERE sha256=$h", ("$h", sha256));
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rel = reader.GetString(0);
            // On ne se fie à l'index que si le fichier est encore réellement sur le disque.
            if (File.Exists(Path.Combine(LibraryRoot, rel))) return rel;
        }
        return null;
    }

    public void AddFile(LibraryFile f)
    {
        using var tx = _db.BeginTransaction();
        using (var cmd = Command("""
            INSERT INTO files (sha256, size, rel_path, taken_at, date_source, kind, origin, device_id, source_path, imported_at)
            VALUES ($h, $s, $r, $t, $ds, $k, $o, $d, $p, $i)
            """,
            ("$h", f.Sha256), ("$s", f.Size), ("$r", f.RelativePath), ("$t", f.TakenAt.ToString("O")),
            ("$ds", f.DateSource.ToString()), ("$k", f.Kind.ToString()), ("$o", f.Origin.ToString()),
            ("$d", f.DeviceId), ("$p", f.SourcePath), ("$i", DateTime.Now.ToString("O"))))
        {
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
        AddDeviceItem(f.DeviceId, f.SourcePath, f.Size, f.Sha256, tx);
        tx.Commit();
    }

    public void AddDeviceItem(string deviceId, string sourcePath, long size, string sha256, SqliteTransaction? tx = null)
    {
        using var cmd = Command("INSERT OR REPLACE INTO device_items VALUES ($d, $p, $s, $h)",
            ("$d", deviceId), ("$p", sourcePath), ("$s", size), ("$h", sha256));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public void TouchDevice(string deviceId, string name)
    {
        using var cmd = Command("""
            INSERT INTO devices VALUES ($d, $n, $t)
            ON CONFLICT(device_id) DO UPDATE SET last_seen = excluded.last_seen
            """, ("$d", deviceId), ("$n", name), ("$t", DateTime.Now.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    public int FileCount()
    {
        using var cmd = Command("SELECT COUNT(*) FROM files");
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _db.Dispose();
        // Libère le fichier de base pour que le disque externe puisse être éjecté.
        SqliteConnection.ClearAllPools();
    }
}
