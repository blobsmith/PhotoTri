namespace PhotoTri.Core.Faces;

/// <summary>Un visage tel que le regroupement le voit.</summary>
public sealed record FaceVector(
    long Id,
    float[] Embedding,
    long? PersonId,
    bool AssignedByUser,
    IReadOnlySet<long> RejectedPersons,
    bool Ignored = false);

/// <summary>
/// Attribution automatique des visages aux personnes nommées, et regroupement des visages inconnus
/// qui se ressemblent pour que l'utilisateur les nomme en une fois.
/// </summary>
public static class FaceClustering
{
    /// <summary>Ressemblance minimale avec une personne nommée pour lui attribuer un visage automatiquement.</summary>
    public const float AssignThreshold = 0.42f;

    /// <summary>Ressemblance minimale avec un groupe de visages inconnus pour s'y ajouter.</summary>
    public const float GroupThreshold = 0.40f;

    /// <summary>
    /// Attributions automatiques à faire ou à défaire : visage → personne (null = plus de personne).
    /// Les visages nommés par l'utilisateur ne changent jamais ; un visage n'est jamais rendu à une personne
    /// dont l'utilisateur a dit « ce n'est pas elle ».
    /// </summary>
    public static List<(long FaceId, long? PersonId)> AutoAssign(IReadOnlyList<FaceVector> faces)
    {
        // Portrait type de chaque personne : la moyenne des visages que l'utilisateur lui a donnés.
        var centroids = faces.Where(f => f.AssignedByUser && f.PersonId is not null)
            .GroupBy(f => f.PersonId!.Value)
            .ToDictionary(g => g.Key, g => Centroid(g.Select(f => f.Embedding)));

        var changes = new List<(long, long?)>();
        foreach (var face in faces.Where(f => !f.AssignedByUser && !f.Ignored))
        {
            long? best = null;
            var bestSim = AssignThreshold;
            foreach (var (person, centroid) in centroids)
            {
                if (face.RejectedPersons.Contains(person)) continue;
                var sim = FaceEngine.Similarity(face.Embedding, centroid);
                if (sim >= bestSim)
                {
                    bestSim = sim;
                    best = person;
                }
            }
            if (best != face.PersonId) changes.Add((face.Id, best));
        }
        return changes;
    }

    /// <summary>
    /// Groupes de visages qui se ressemblent, parmi ceux qui n'ont pas de personne, du plus grand au plus petit.
    /// Chaque visage rejoint le groupe dont le portrait moyen lui ressemble le plus, ou en crée un nouveau.
    /// </summary>
    public static List<List<FaceVector>> Group(IReadOnlyList<FaceVector> faces)
    {
        var groups = new List<(List<FaceVector> Members, float[] Sum, float[] Centroid)>();
        foreach (var face in faces.Where(f => f.PersonId is null && !f.Ignored))
        {
            var bestIndex = -1;
            var bestSim = GroupThreshold;
            for (var i = 0; i < groups.Count; i++)
            {
                var sim = FaceEngine.Similarity(face.Embedding, groups[i].Centroid);
                if (sim >= bestSim)
                {
                    bestSim = sim;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0)
            {
                groups.Add((new List<FaceVector> { face }, (float[])face.Embedding.Clone(), face.Embedding));
            }
            else
            {
                var (members, sum, _) = groups[bestIndex];
                members.Add(face);
                for (var k = 0; k < sum.Length; k++) sum[k] += face.Embedding[k];
                groups[bestIndex] = (members, sum, FaceEngine.Normalize(sum));
            }
        }
        return groups.Select(g => g.Members).OrderByDescending(m => m.Count).ThenBy(m => m[0].Id).ToList();
    }

    public static float[] Centroid(IEnumerable<float[]> embeddings)
    {
        float[]? sum = null;
        foreach (var e in embeddings)
        {
            sum ??= new float[e.Length];
            for (var k = 0; k < e.Length; k++) sum[k] += e[k];
        }
        return FaceEngine.Normalize(sum ?? Array.Empty<float>());
    }
}
