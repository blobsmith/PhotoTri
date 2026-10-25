using Microsoft.Data.Sqlite;
using PhotoTri.Core.Library;

namespace PhotoTri.Core.Faces;

/// <summary>Un visage affichable : sa photo et sa vignette.</summary>
public sealed record FaceInfo(long Id, long FileId, string Sha256, int Index, bool ByUser)
{
    public string CropPath => FaceIndexer.CropPath(Sha256, Index);
}

public sealed record Person(long Id, string Name, IReadOnlyList<FaceInfo> Faces);

/// <summary>Visages d'une même personne pas encore nommée.</summary>
public sealed record FaceGroup(IReadOnlyList<FaceInfo> Faces)
{
    /// <summary>Identifiant stable tant que le groupe ne change pas : celui de son premier visage.</summary>
    public long Key => Faces[0].Id;
}

/// <summary>Personnes nommées et groupes de visages à nommer, lus en lecture seule depuis la base.</summary>
public sealed class PeopleCatalog
{
    /// <summary>Un groupe doit avoir au moins ce nombre de visages pour être proposé.</summary>
    public const int MinGroupSize = 2;

    public IReadOnlyList<Person> Persons { get; }
    public IReadOnlyList<FaceGroup> Groups { get; }

    /// <summary>Visages seuls, qui ne ressemblent à aucun autre.</summary>
    public int LoneFaces { get; }

    PeopleCatalog(IReadOnlyList<Person> persons, IReadOnlyList<FaceGroup> groups, int loneFaces)
    {
        Persons = persons;
        Groups = groups;
        LoneFaces = loneFaces;
    }

    public static PeopleCatalog Empty { get; } = new(Array.Empty<Person>(), Array.Empty<FaceGroup>(), 0);

    public static PeopleCatalog Load(string databasePath)
    {
        if (!File.Exists(databasePath)) return Empty;
        using var db = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        db.Open();
        using (var check = db.CreateCommand())
        {
            check.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'faces'";
            if (check.ExecuteScalar() is null) return Empty;
        }

        var infos = new Dictionary<long, FaceInfo>();
        var vectors = new List<FaceVector>();
        var personOf = new Dictionary<long, long>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT f.id, f.file_id, fi.sha256, f.idx, f.assigned_by, f.person_id, f.embedding, f.ignored
                FROM faces f JOIN files fi ON fi.id = f.file_id
                ORDER BY fi.taken_at DESC, f.idx
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var byUser = !r.IsDBNull(4) && r.GetString(4) == "user";
                long? person = r.IsDBNull(5) ? null : r.GetInt64(5);
                infos[id] = new FaceInfo(id, r.GetInt64(1), r.GetString(2), r.GetInt32(3), byUser);
                if (person is { } p) personOf[id] = p;
                vectors.Add(new FaceVector(id, LibraryIndex.FromBlob((byte[])r[6]), person, byUser,
                    new HashSet<long>(), r.GetInt64(7) != 0));
            }
        }

        var names = new Dictionary<long, string>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name FROM persons";
            using var r = cmd.ExecuteReader();
            while (r.Read()) names[r.GetInt64(0)] = r.GetString(1);
        }

        var persons = personOf
            .Where(kv => names.ContainsKey(kv.Value))
            .GroupBy(kv => kv.Value)
            .Select(g => new Person(g.Key, names[g.Key], g.Select(kv => infos[kv.Key]).OrderByDescending(f => f.ByUser).ToList()))
            .OrderByDescending(p => p.Faces.Count).ThenBy(p => p.Name)
            .ToList();

        var groups = FaceClustering.Group(vectors);
        return new PeopleCatalog(
            persons,
            groups.Where(g => g.Count >= MinGroupSize).Select(g => new FaceGroup(g.Select(v => infos[v.Id]).ToList())).ToList(),
            groups.Count(g => g.Count < MinGroupSize));
    }
}
