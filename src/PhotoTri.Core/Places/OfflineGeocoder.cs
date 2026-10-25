using System.Globalization;
using System.IO.Compression;

namespace PhotoTri.Core.Places;

public readonly record struct GeoPoint(double Latitude, double Longitude);

/// <summary>Ville la plus proche d'un point GPS.</summary>
public sealed record Place(string City, string Region, string CountryCode, string Country, double DistanceKm)
{
    /// <summary>Clé de regroupement des photos d'un même lieu.</summary>
    public string Key => $"{CountryCode}|{Region}|{City}";
}

/// <summary>
/// Trouve la ville la plus proche sans Internet, grâce à la base GeoNames des villes de plus de
/// 500 habitants (CC BY 4.0, geonames.org) intégrée à l'application (Data\cities.tsv.gz).
/// C'est une estimation : la commune dont le centre est le plus proche, pas forcément celle où la photo
/// a été prise. Les adresses en ligne (<see cref="AddressResolver"/>) donnent ensuite la commune exacte.
/// </summary>
public sealed class OfflineGeocoder
{
    /// <summary>Au-delà, le point est en mer ou dans une zone vide : pas de ville.</summary>
    public const double MaxDistanceKm = 80;

    /// <summary>Rayon de la plus grande ville du monde (~38 millions d'habitants) : au-delà, inutile de chercher.</summary>
    const double MaxInfluenceKm = 50;

    static readonly Lazy<OfflineGeocoder> Shared = new(() => Load(OpenEmbedded()));

    public static OfflineGeocoder Instance => Shared.Value;

    readonly float[] _lat;
    readonly float[] _lon;
    readonly string[] _name;
    readonly string[] _region;
    readonly string[] _countryCode;
    readonly string[] _country;
    readonly int[] _population;
    // Index par case de 1° × 1° : on ne compare qu'aux villes des cases voisines.
    readonly Dictionary<int, List<int>> _cells = new();

    OfflineGeocoder(List<string[]> rows)
    {
        var n = rows.Count;
        _lat = new float[n];
        _lon = new float[n];
        _name = new string[n];
        _region = new string[n];
        _countryCode = new string[n];
        _country = new string[n];
        _population = new int[n];
        for (var i = 0; i < n; i++)
        {
            var r = rows[i];
            _lat[i] = float.Parse(r[0], CultureInfo.InvariantCulture);
            _lon[i] = float.Parse(r[1], CultureInfo.InvariantCulture);
            _name[i] = r[2];
            _region[i] = string.Intern(r[3]);
            _countryCode[i] = string.Intern(r[4]);
            _country[i] = string.Intern(r[5]);
            _population[i] = r.Length > 6 && int.TryParse(r[6], out var pop) ? pop : 0;
            var cell = Cell(_lat[i], _lon[i]);
            if (!_cells.TryGetValue(cell, out var list)) _cells[cell] = list = new List<int>();
            list.Add(i);
        }
    }

    /// <summary>Lit une base au format « lat \t lon \t ville \t région \t code pays \t pays \t population ».</summary>
    public static OfflineGeocoder Load(Stream gzipTsv)
    {
        using var reader = new StreamReader(new GZipStream(gzipTsv, CompressionMode.Decompress));
        var rows = new List<string[]>(170_000);
        while (reader.ReadLine() is { } line)
        {
            var cols = line.Split('\t');
            if (cols.Length >= 6) rows.Add(cols);
        }
        return new OfflineGeocoder(rows);
    }

    static Stream OpenEmbedded() =>
        typeof(OfflineGeocoder).Assembly.GetManifestResourceStream("PhotoTri.Core.Data.cities.tsv.gz")
        ?? throw new InvalidOperationException("Base des villes absente de l'application.");

    public Place? Find(GeoPoint point)
    {
        var best = -1;
        var bestKm = double.MaxValue;
        var candidates = new List<(int Index, double Km)>();
        var latCell = (int)Math.Floor(point.Latitude);
        var lonCell = (int)Math.Floor(point.Longitude);
        // Les cases rétrécissent vers les pôles : on élargit la recherche en longitude.
        var lonRange = Math.Min(180, (int)Math.Ceiling(1 / Math.Max(0.1, Math.Cos(point.Latitude * Math.PI / 180))));
        for (var dLat = -1; dLat <= 1; dLat++)
        {
            for (var dLon = -lonRange; dLon <= lonRange; dLon++)
            {
                var lon = ((lonCell + dLon + 180) % 360 + 360) % 360 - 180;
                if (!_cells.TryGetValue(CellKey(latCell + dLat, lon), out var list)) continue;
                foreach (var i in list)
                {
                    var km = DistanceKm(point.Latitude, point.Longitude, _lat[i], _lon[i]);
                    if (km <= MaxInfluenceKm) candidates.Add((i, km));
                    if (km < bestKm)
                    {
                        bestKm = km;
                        best = i;
                    }
                }
            }
        }
        if (best < 0 || bestKm > MaxDistanceKm) return null;

        // Dans une grande ville, le point le plus proche est souvent un quartier (« Paris 16 Passy »,
        // « The Rocks » à Sydney) : on préfère la ville la plus peuplée dont on est dans le rayon.
        var chosen = best;
        var chosenKm = bestKm;
        foreach (var (i, km) in candidates)
        {
            if (km <= InfluenceRadiusKm(_population[i]) && _population[i] > _population[chosen])
            {
                chosen = i;
                chosenKm = km;
            }
        }
        return new Place(_name[chosen], _region[chosen], _countryCode[chosen], _country[chosen], chosenKm);
    }

    /// <summary>Rayon approximatif d'une ville selon sa population : ~1 km pour 15 000 habitants, ~12 km pour Paris.</summary>
    static double InfluenceRadiusKm(int population) => 0.008 * Math.Sqrt(population);

    static int Cell(double lat, double lon) => CellKey((int)Math.Floor(lat), (int)Math.Floor(lon));

    static int CellKey(int lat, int lon) => (lat + 90) * 1000 + (lon + 180);

    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}
