using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace PhotoTri.Core.Documents;

/// <summary>
/// Lit le texte présent sur une photo avec la reconnaissance de texte intégrée à Windows :
/// gratuite, hors ligne, rien n'est envoyé sur Internet.
/// </summary>
public sealed class PhotoTextReader
{
    const uint MaxSide = 2000;

    readonly OcrEngine _engine;

    PhotoTextReader(OcrEngine engine) => _engine = engine;

    /// <summary>Langue de lecture (le français si Windows le propose).</summary>
    public string Language => _engine.RecognizerLanguage.LanguageTag;

    /// <summary>Null si Windows n'a aucune langue de reconnaissance de texte installée.</summary>
    public static PhotoTextReader? TryCreate()
    {
        var engine = OcrEngine.TryCreateFromLanguage(new Language("fr-FR")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        return engine is null ? null : new PhotoTextReader(engine);
    }

    /// <summary>Lignes de texte lues sur la photo, redressée selon son orientation EXIF.</summary>
    public async Task<IReadOnlyList<string>> ReadAsync(string path) => (await ReadWithLayoutAsync(path)).Lines;

    /// <summary>
    /// Lignes de texte, et part de la photo couverte par la zone de texte (de 0 à 1) :
    /// une feuille photographiée remplit l'image, un panneau ou une affiche n'en occupe qu'un coin.
    /// </summary>
    public async Task<TextReading> ReadWithLayoutAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        // Une photo réduite à 2 000 pixels suffit pour lire un document, et se lit bien plus vite.
        var maxSide = Math.Min(MaxSide, OcrEngine.MaxImageDimension);
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * scale),
            ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var result = await _engine.RecognizeAsync(bitmap);
        var lines = result.Lines.Select(l => l.Text).ToList();

        // Rectangle qui englobe tous les mots lus, rapporté à la surface de l'image.
        var words = result.Lines.SelectMany(l => l.Words).Select(w => w.BoundingRect).ToList();
        double coverage = 0;
        if (words.Count > 0 && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
        {
            var left = words.Min(r => r.X);
            var top = words.Min(r => r.Y);
            var right = words.Max(r => r.X + r.Width);
            var bottom = words.Max(r => r.Y + r.Height);
            coverage = (right - left) * (bottom - top) / ((double)bitmap.PixelWidth * bitmap.PixelHeight);
        }
        return new TextReading(lines, Math.Clamp(coverage, 0, 1));
    }
}

/// <summary>Texte lu sur une photo et part de l'image occupée par ce texte.</summary>
public sealed record TextReading(IReadOnlyList<string> Lines, double Coverage);
