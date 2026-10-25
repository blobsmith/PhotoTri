using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace PhotoTri.Core.Faces;

/// <summary>Visage trouvé : cadre et 5 repères (œil gauche et droit à l'image, nez, coins de la bouche), en pixels.</summary>
public sealed record DetectedFace(float X, float Y, float Width, float Height, float Score, (float X, float Y)[] Landmarks);

/// <summary>
/// Détection (YuNet) et reconnaissance (SFace) des visages, entièrement sur le PC.
/// L'empreinte d'un visage est un vecteur de 128 nombres de longueur 1 : plus le produit scalaire
/// de deux empreintes est proche de 1, plus les visages se ressemblent.
/// </summary>
public sealed class FaceEngine : IDisposable
{
    const int DetectorSize = 640;
    const int EmbedderSize = 112;
    const float MinScore = 0.85f;
    const float NmsIoU = 0.3f;

    // Position des repères dans un visage aligné de 112 × 112 pixels (modèle de référence ArcFace, utilisé par SFace).
    static readonly (float X, float Y)[] Reference =
    {
        (38.2946f, 51.6963f), (73.5318f, 51.5014f), (56.0252f, 71.7366f), (41.5493f, 92.3655f), (70.7299f, 92.2041f),
    };

    readonly InferenceSession _detector;
    readonly InferenceSession _embedder;

    public FaceEngine(string? modelsDir = null)
    {
        modelsDir ??= Path.Combine(AppContext.BaseDirectory, "Models");
        var options = new SessionOptions
        {
            // Analyse en arrière-plan : on laisse des cœurs libres pour que l'application reste fluide.
            IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 4),
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        // Modèles lus par .NET puis passés en mémoire : ONNX Runtime n'ouvre pas les chemins de plus de 260 caractères.
        _detector = new InferenceSession(File.ReadAllBytes(Path.Combine(modelsDir, "face_detection_yunet_2023mar.onnx")), options);
        _embedder = new InferenceSession(File.ReadAllBytes(Path.Combine(modelsDir, "face_recognition_sface_2021dec.onnx")), options);
    }

    public List<DetectedFace> Detect(RgbImage image)
    {
        // Image réduite dans un carré de 640, collée en haut à gauche ; le reste est noir.
        var scale = (float)DetectorSize / Math.Max(image.Width, image.Height);
        var input = new DenseTensor<float>(new[] { 1, 3, DetectorSize, DetectorSize });
        var w = (int)(image.Width * scale);
        var h = (int)(image.Height * scale);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                for (var c = 0; c < 3; c++) // YuNet attend du BGR de 0 à 255
                    input[0, c, y, x] = image.Sample(x / scale, y / scale, c);

        using var results = _detector.Run(new[] { NamedOnnxValue.CreateFromTensor("input", input) });
        var outputs = results.ToDictionary(r => r.Name, r => r.AsTensor<float>());

        var faces = new List<DetectedFace>();
        foreach (var stride in new[] { 8, 16, 32 })
        {
            var cls = outputs[$"cls_{stride}"];
            var obj = outputs[$"obj_{stride}"];
            var box = outputs[$"bbox_{stride}"];
            var kps = outputs[$"kps_{stride}"];
            var cols = DetectorSize / stride;
            var count = cls.Dimensions[1];
            for (var i = 0; i < count; i++)
            {
                var score = MathF.Sqrt(Math.Clamp(cls[0, i, 0], 0, 1) * Math.Clamp(obj[0, i, 0], 0, 1));
                if (score < MinScore) continue;
                var col = i % cols;
                var row = i / cols;
                var cx = (col + box[0, i, 0]) * stride;
                var cy = (row + box[0, i, 1]) * stride;
                var bw = MathF.Exp(box[0, i, 2]) * stride;
                var bh = MathF.Exp(box[0, i, 3]) * stride;
                var landmarks = new (float, float)[5];
                for (var k = 0; k < 5; k++)
                    landmarks[k] = ((kps[0, i, 2 * k] + col) * stride / scale, (kps[0, i, 2 * k + 1] + row) * stride / scale);
                faces.Add(new DetectedFace((cx - bw / 2) / scale, (cy - bh / 2) / scale, bw / scale, bh / scale, score, landmarks));
            }
        }
        return NonMaximumSuppression(faces);
    }

    static List<DetectedFace> NonMaximumSuppression(List<DetectedFace> faces)
    {
        var kept = new List<DetectedFace>();
        foreach (var face in faces.OrderByDescending(f => f.Score))
            if (kept.All(k => IoU(k, face) < NmsIoU)) kept.Add(face);
        return kept;
    }

    static float IoU(DetectedFace a, DetectedFace b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.Width, b.X + b.Width);
        var y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        return inter / (a.Width * a.Height + b.Width * b.Height - inter);
    }

    /// <summary>Empreinte du visage, après l'avoir redressé et recadré comme SFace l'attend.</summary>
    public float[] Embed(RgbImage image, DetectedFace face)
    {
        // Transformation (rotation + échelle + translation) qui amène les repères du visage sur la référence.
        var (a, b, tx, ty) = SimilarityTransform(face.Landmarks, Reference);
        // On parcourt l'image alignée et on va chercher chaque pixel dans la photo (transformation inverse).
        var det = a * a + b * b;
        var input = new DenseTensor<float>(new[] { 1, 3, EmbedderSize, EmbedderSize });
        for (var y = 0; y < EmbedderSize; y++)
        {
            for (var x = 0; x < EmbedderSize; x++)
            {
                var dx = x - tx;
                var dy = y - ty;
                var sx = (a * dx + b * dy) / det;
                var sy = (-b * dx + a * dy) / det;
                // SFace attend du RGB de 0 à 255.
                input[0, 0, y, x] = image.Sample(sx, sy, 2);
                input[0, 1, y, x] = image.Sample(sx, sy, 1);
                input[0, 2, y, x] = image.Sample(sx, sy, 0);
            }
        }
        using var results = _embedder.Run(new[] { NamedOnnxValue.CreateFromTensor("data", input) });
        return Normalize(results.First().AsTensor<float>().ToArray());
    }

    /// <summary>
    /// Similitude (x' = a·x − b·y + tx ; y' = b·x + a·y + ty) qui envoie au mieux, au sens des moindres
    /// carrés, les points <paramref name="from"/> sur <paramref name="to"/>.
    /// </summary>
    public static (float A, float B, float Tx, float Ty) SimilarityTransform((float X, float Y)[] from, (float X, float Y)[] to)
    {
        var n = from.Length;
        float mx = 0, my = 0, ux = 0, uy = 0;
        for (var i = 0; i < n; i++)
        {
            mx += from[i].X; my += from[i].Y;
            ux += to[i].X; uy += to[i].Y;
        }
        mx /= n; my /= n; ux /= n; uy /= n;
        float sxx = 0, sab = 0, sba = 0;
        for (var i = 0; i < n; i++)
        {
            var px = from[i].X - mx;
            var py = from[i].Y - my;
            var qx = to[i].X - ux;
            var qy = to[i].Y - uy;
            sxx += px * px + py * py;
            sab += px * qx + py * qy;
            sba += px * qy - py * qx;
        }
        var a = sab / sxx;
        var b = sba / sxx;
        return (a, b, ux - (a * mx - b * my), uy - (b * mx + a * my));
    }

    public static float[] Normalize(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        return norm == 0 ? v : v.Select(x => x / norm).ToArray();
    }

    public static float Similarity(float[] a, float[] b)
    {
        float sum = 0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return sum;
    }

    public void Dispose()
    {
        _detector.Dispose();
        _embedder.Dispose();
    }
}
