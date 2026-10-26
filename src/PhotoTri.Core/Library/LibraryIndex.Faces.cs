using Microsoft.Data.Sqlite;
using PhotoTri.Core.Faces;

namespace PhotoTri.Core.Library;

/// <summary>Un visage trouvé dans une photo, prêt à être enregistré (cadre en fraction de l'image, de 0 à 1).</summary>
public sealed record FaceToSave(int Index, float X, float Y, float Width, float Height, float Score, float[] Embedding);

// Visages et personnes.
public sealed partial class LibraryIndex
{
    /// <summary>Version de l'analyse des visages : les photos analysées par une version plus ancienne sont refaites.</summary>
    public const int FaceVersion = 1;

    void CreateFaceTables()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS persons (
                id            INTEGER PRIMARY KEY,
                name          TEXT NOT NULL UNIQUE COLLATE NOCASE
            );
            CREATE TABLE IF NOT EXISTS faces (
                id            INTEGER PRIMARY KEY,
                file_id       INTEGER NOT NULL,
                idx           INTEGER NOT NULL,
                x             REAL NOT NULL,
                y             REAL NOT NULL,
                w             REAL NOT NULL,
                h             REAL NOT NULL,
                score         REAL NOT NULL,
                embedding     BLOB NOT NULL,
                person_id     INTEGER,
                assigned_by   TEXT,              -- 'user' ou 'auto'
                rejected      TEXT NOT NULL DEFAULT '', -- personnes refusées par l'utilisateur : ",3,7,"
                ignored       INTEGER NOT NULL DEFAULT 0,
                UNIQUE (file_id, idx)
            );
            CREATE INDEX IF NOT EXISTS ix_faces_file ON faces(file_id);
            CREATE INDEX IF NOT EXISTS ix_faces_person ON faces(person_id);
            """);
        AddColumnIfMissing("files", "faces_done", "INTEGER NOT NULL DEFAULT 0");
    }

    /// <summary>Photos pas encore analysées : identifiant, chemin relatif, empreinte du fichier.</summary>
    public List<(long Id, string RelativePath, string Sha256)> PhotosWithoutFaces(int limit)
    {
        using var cmd = Command(
            "SELECT id, rel_path, sha256 FROM files WHERE kind = 'Photo' AND faces_done < $v ORDER BY taken_at DESC LIMIT $l",
            ("$v", FaceVersion), ("$l", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, string)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    public int CountPhotosWithoutFaces()
    {
        using var cmd = Command("SELECT COUNT(*) FROM files WHERE kind = 'Photo' AND faces_done < $v", ("$v", FaceVersion));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>Remplace les visages d'une photo et la marque analysée.</summary>
    public void SaveFaces(long fileId, IReadOnlyList<FaceToSave> faces)
    {
        using var tx = _db.BeginTransaction();
        Run(tx, "DELETE FROM faces WHERE file_id = $f", ("$f", fileId));
        foreach (var face in faces)
        {
            Run(tx, """
                INSERT INTO faces (file_id, idx, x, y, w, h, score, embedding)
                VALUES ($f, $i, $x, $y, $w, $h, $s, $e)
                """,
                ("$f", fileId), ("$i", face.Index), ("$x", face.X), ("$y", face.Y), ("$w", face.Width), ("$h", face.Height),
                ("$s", face.Score), ("$e", ToBlob(face.Embedding)));
        }
        Run(tx, "UPDATE files SET faces_done = $v WHERE id = $f", ("$f", fileId), ("$v", FaceVersion));
        tx.Commit();
    }

    /// <summary>Cadre de chaque visage (en fraction de l'image), avec sa photo, pour recréer les vignettes.</summary>
    public List<(string RelativePath, string Sha256, int Index, float X, float Y, float Width, float Height)> FaceBoxes()
    {
        using var cmd = Command("""
            SELECT fi.rel_path, fi.sha256, f.idx, f.x, f.y, f.w, f.h
            FROM faces f JOIN files fi ON fi.id = f.file_id
            ORDER BY fi.id, f.idx
            """);
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, int, float, float, float, float)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetFloat(3), r.GetFloat(4), r.GetFloat(5), r.GetFloat(6)));
        return list;
    }

    public List<FaceVector> LoadFaceVectors()
    {
        using var cmd = Command("SELECT id, embedding, person_id, assigned_by, rejected, ignored FROM faces ORDER BY id");
        using var r = cmd.ExecuteReader();
        var list = new List<FaceVector>();
        while (r.Read())
        {
            list.Add(new FaceVector(
                r.GetInt64(0),
                FromBlob((byte[])r[1]),
                r.IsDBNull(2) ? null : r.GetInt64(2),
                !r.IsDBNull(3) && r.GetString(3) == "user",
                ParseIds(r.GetString(4)),
                r.GetInt64(5) != 0));
        }
        return list;
    }

    /// <summary>Recalcule les attributions automatiques (après une analyse ou une correction de l'utilisateur).</summary>
    public int ApplyAutoAssignments()
    {
        var changes = FaceClustering.AutoAssign(LoadFaceVectors());
        using var tx = _db.BeginTransaction();
        foreach (var (faceId, personId) in changes)
        {
            Run(tx, "UPDATE faces SET person_id = $p, assigned_by = CASE WHEN $p IS NULL THEN NULL ELSE 'auto' END WHERE id = $id",
                ("$id", faceId), ("$p", personId));
        }
        tx.Commit();
        return changes.Count;
    }

    /// <summary>
    /// L'utilisateur nomme ces visages. Si une personne porte déjà ce nom, ils lui sont ajoutés.
    /// Renvoie l'identifiant de la personne.
    /// </summary>
    public long NameFaces(IEnumerable<long> faceIds, string name)
    {
        name = name.Trim();
        using var tx = _db.BeginTransaction();
        var personId = GetOrCreatePerson(tx, name);
        foreach (var id in faceIds)
        {
            Run(tx, "UPDATE faces SET person_id = $p, assigned_by = 'user', ignored = 0 WHERE id = $id", ("$id", id), ("$p", personId));
        }
        tx.Commit();
        ApplyAutoAssignments();
        return personId;
    }

    /// <summary>Renomme une personne ; si le nouveau nom est celui d'une autre personne, les deux sont fusionnées.</summary>
    public long RenamePerson(long personId, string newName)
    {
        newName = newName.Trim();
        using var tx = _db.BeginTransaction();
        using var find = Command("SELECT id FROM persons WHERE name = $n AND id <> $id", ("$n", newName), ("$id", personId));
        find.Transaction = tx;
        var target = find.ExecuteScalar() is long other ? other : (long?)null;
        if (target is null)
        {
            Run(tx, "UPDATE persons SET name = $n WHERE id = $id", ("$n", newName), ("$id", personId));
        }
        else
        {
            Run(tx, "UPDATE faces SET person_id = $t WHERE person_id = $id", ("$t", target), ("$id", personId));
            Run(tx, "UPDATE faces SET rejected = REPLACE(rejected, $old, $new)",
                ("$old", $",{personId},"), ("$new", $",{target},"));
            Run(tx, "DELETE FROM persons WHERE id = $id", ("$id", personId));
        }
        tx.Commit();
        ApplyAutoAssignments();
        return target ?? personId;
    }

    /// <summary>« Ce n'est pas cette personne » : le visage lui est retiré et ne lui sera plus attribué.</summary>
    public void RejectFace(long faceId)
    {
        Execute(faceId, """
            UPDATE faces SET rejected = CASE WHEN rejected = '' THEN ',' ELSE rejected END || person_id || ',',
                             person_id = NULL, assigned_by = NULL
            WHERE id = $id AND person_id IS NOT NULL
            """);
        DeletePersonsWithoutUserFaces();
        ApplyAutoAssignments();
    }

    /// <summary>Visages d'inconnus à ne plus proposer (passants, personnes sur des affiches…).</summary>
    public void IgnoreFaces(IEnumerable<long> faceIds)
    {
        using var tx = _db.BeginTransaction();
        foreach (var id in faceIds)
            Run(tx, "UPDATE faces SET ignored = 1, person_id = NULL, assigned_by = NULL WHERE id = $id", ("$id", id));
        tx.Commit();
    }

    long GetOrCreatePerson(SqliteTransaction tx, string name)
    {
        using var find = Command("SELECT id FROM persons WHERE name = $n", ("$n", name));
        find.Transaction = tx;
        if (find.ExecuteScalar() is long id) return id;
        using var insert = Command("INSERT INTO persons (name) VALUES ($n) RETURNING id", ("$n", name));
        insert.Transaction = tx;
        return (long)insert.ExecuteScalar()!;
    }

    // Une personne dont il ne reste aucun visage donné par l'utilisateur n'a plus de portrait de référence.
    void DeletePersonsWithoutUserFaces()
    {
        Execute("""
            UPDATE faces SET person_id = NULL, assigned_by = NULL
            WHERE person_id IN (SELECT id FROM persons WHERE id NOT IN (SELECT person_id FROM faces WHERE assigned_by = 'user' AND person_id IS NOT NULL));
            DELETE FROM persons WHERE id NOT IN (SELECT person_id FROM faces WHERE assigned_by = 'user' AND person_id IS NOT NULL);
            """);
    }

    void Run(SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    void Execute(long id, string sql)
    {
        using var cmd = Command(sql, ("$id", id));
        cmd.ExecuteNonQuery();
    }

    static IReadOnlySet<long> ParseIds(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToHashSet();

    public static byte[] ToBlob(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBlob(byte[] bytes)
    {
        var values = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length);
        return values;
    }
}
