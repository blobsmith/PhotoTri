using PhotoTri.Core.Documents;
using PhotoTri.Core.Media;

namespace PhotoTri.Core.Library;

// Documents reconnus sur les photos (pièces d'identité, permis, factures, feuilles…).
public sealed partial class LibraryIndex
{
    /// <summary>Version de la reconnaissance des documents : les photos lues par une version plus ancienne sont relues.</summary>
    public const int DocumentVersion = 2; // 2 : place occupée par le texte dans la photo

    /// <summary>Texte lu gardé pour la recherche : de quoi retrouver un document, sans alourdir la base.</summary>
    const int MaxDocumentText = 2000;

    void CreateDocumentColumns()
    {
        AddColumnIfMissing("files", "doc_done", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("files", "doc_kind", "TEXT");
        AddColumnIfMissing("files", "doc_text", "TEXT");
        // Correction de l'utilisateur : un type de document, ou « None » pour « ce n'est pas un document ».
        AddColumnIfMissing("files", "doc_user", "TEXT");
    }

    public List<(long Id, string RelativePath, MediaOrigin Origin)> PhotosWithoutDocumentCheck(int limit)
    {
        using var cmd = Command(
            "SELECT id, rel_path, origin FROM files WHERE kind = 'Photo' AND doc_done < $v ORDER BY taken_at DESC LIMIT $l",
            ("$v", DocumentVersion), ("$l", limit));
        using var r = cmd.ExecuteReader();
        var list = new List<(long, string, MediaOrigin)>();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1), Enum.Parse<MediaOrigin>(r.GetString(2))));
        return list;
    }

    public int CountPhotosWithoutDocumentCheck()
    {
        using var cmd = Command("SELECT COUNT(*) FROM files WHERE kind = 'Photo' AND doc_done < $v", ("$v", DocumentVersion));
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void SaveDocument(long fileId, DocumentKind? kind, string? text)
    {
        if (text is { Length: > MaxDocumentText }) text = text[..MaxDocumentText];
        using var cmd = Command("UPDATE files SET doc_done = $v, doc_kind = $k, doc_text = $t WHERE id = $id",
            ("$id", fileId), ("$v", DocumentVersion), ("$k", kind?.ToString()), ("$t", kind is null ? null : text));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Correction de l'utilisateur, prioritaire sur la reconnaissance : un type, ou null pour « pas un document ».</summary>
    public void SetDocumentKindByUser(IEnumerable<long> fileIds, DocumentKind? kind)
    {
        using var tx = _db.BeginTransaction();
        foreach (var id in fileIds)
            Run(tx, "UPDATE files SET doc_user = $k WHERE id = $id", ("$id", id), ("$k", kind?.ToString() ?? "None"));
        tx.Commit();
    }
}
