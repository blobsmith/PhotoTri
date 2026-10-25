// Diagnostic en lecture seule : détecte les téléphones et analyse leurs fichiers, sans rien copier ni supprimer.
using System.Diagnostics;
using PhotoTri.Core;
using PhotoTri.Core.Devices;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using PhotoTri.Core.Transfer;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var settings = AppSettings.Load();
var library = settings.Library?.Resolve() ?? AppSettings.DefaultLibraryPath;
Console.WriteLine($"Bibliothèque : {library}");

// « rotations » : angle de rotation lu dans chaque vidéo de la bibliothèque.
if (args.Length > 0 && args[0] == "rotations")
{
    foreach (var group in Directory.EnumerateFiles(library, "*.mp4", SearchOption.AllDirectories)
                 .GroupBy(VideoRotation.Read).OrderBy(g => g.Key))
        Console.WriteLine($"{group.Key,4}° : {group.Count(),3} vidéo(s), ex. {Path.GetFileName(group.First())}");
    return;
}

// « tags <fichier> » : toutes les métadonnées lues par MetadataExtractor ; « gpsstats » : présence du GPS.
if (args.Length > 1 && args[0] == "tags")
{
    foreach (var dir in MetadataExtractor.ImageMetadataReader.ReadMetadata(args[1]))
        foreach (var tag in dir.Tags)
            Console.WriteLine($"[{dir.Name}] {tag.Name} = {tag.Description}");
    return;
}
if (args.Length > 0 && args[0] == "gpsstats")
{
    foreach (var ext in new[] { "*.jpg", "*.mp4" })
    {
        var files = Directory.EnumerateFiles(library, ext, SearchOption.AllDirectories).ToList();
        var withGps = files.Count(f => MetadataExtractor.ImageMetadataReader.ReadMetadata(f)
            .OfType<MetadataExtractor.Formats.Exif.GpsDirectory>().Any(g => g.GetGeoLocation() is { IsZero: false }));
        Console.WriteLine($"{ext} : {files.Count} fichiers, {withGps} avec GPS lisible par MetadataExtractor");
    }
    return;
}

// « faces [n] » : visages trouvés dans les n premières photos de l'appareil photo (lecture seule).
if (args.Length > 0 && args[0] == "faces")
{
    using var engine = new PhotoTri.Core.Faces.FaceEngine();
    var photos = Directory.EnumerateFiles(library, "*.jpg", SearchOption.AllDirectories)
        .Where(f => Path.GetFileName(f).StartsWith("20")).OrderBy(f => f).Take(args.Length > 1 ? int.Parse(args[1]) : 40).ToList();
    var total = Stopwatch.StartNew();
    var all = new List<(string File, float[] Embedding)>();
    foreach (var file in photos)
    {
        var t = Stopwatch.StartNew();
        var image = PhotoTri.Core.Faces.RgbImage.Load(file, 1600);
        var load = t.ElapsedMilliseconds;
        var faces = engine.Detect(image);
        var detect = t.ElapsedMilliseconds - load;
        foreach (var f in faces) all.Add((Path.GetFileName(file), engine.Embed(image, f)));
        if (faces.Count > 0)
            Console.WriteLine($"{Path.GetFileName(file)} {image.Width}x{image.Height} : {faces.Count} visage(s) [{string.Join(", ", faces.Select(f => $"{f.Width:0}px {f.Score:0.00}"))}]  lecture {load} ms, détection {detect} ms, total {t.ElapsedMilliseconds} ms");
    }
    Console.WriteLine($"{photos.Count} photos, {all.Count} visages en {total.Elapsed.TotalSeconds:0.0} s");
    // Paires les plus ressemblantes entre photos différentes.
    var pairs = new List<(float Sim, string A, string B)>();
    for (var i = 0; i < all.Count; i++)
        for (var j = i + 1; j < all.Count; j++)
            if (all[i].File != all[j].File)
                pairs.Add((PhotoTri.Core.Faces.FaceEngine.Similarity(all[i].Embedding, all[j].Embedding), all[i].File, all[j].File));
    foreach (var p in pairs.OrderByDescending(p => p.Sim).Take(10)) Console.WriteLine($"  {p.Sim:0.000}  {p.A} ~ {p.B}");
    if (pairs.Count > 0) Console.WriteLine($"  similarité médiane entre visages : {pairs.Select(p => p.Sim).OrderBy(s => s).ElementAt(pairs.Count / 2):0.000}");
    return;
}

// « onnx <modèle> » : entrées et sorties d'un modèle.
if (args.Length > 1 && args[0] == "onnx")
{
    using var session = new Microsoft.ML.OnnxRuntime.InferenceSession(args[1]);
    foreach (var (name, meta) in session.InputMetadata) Console.WriteLine($"entrée {name} [{string.Join(",", meta.Dimensions)}] {meta.ElementType.Name}");
    foreach (var (name, meta) in session.OutputMetadata) Console.WriteLine($"sortie {name} [{string.Join(",", meta.Dimensions)}]");
    return;
}

// « places » : villes trouvées pour les fichiers de la bibliothèque (lecture seule).
if (args.Length > 0 && args[0] == "places")
{
    var timer = Stopwatch.StartNew();
    var found = Directory.EnumerateFiles(library, "*.*", SearchOption.AllDirectories)
        .Where(f => !f.Contains(LibraryLayout.DataFolderName) && MediaClassifier.KindOf(f) is not null)
        .Select(f => (File: f, Point: PhotoTri.Core.Places.GpsReader.Read(f)))
        .ToList();
    var places = found.Where(x => x.Point is not null)
        .Select(x => PhotoTri.Core.Places.OfflineGeocoder.Instance.Find(x.Point!.Value))
        .ToList();
    Console.WriteLine($"{found.Count} fichiers, {places.Count} avec GPS, {places.Count(p => p is null)} hors de toute ville ({timer.Elapsed.TotalSeconds:0.0} s)");
    foreach (var g in places.Where(p => p is not null).GroupBy(p => $"{p!.City} ({p.Region}, {p.Country})").OrderByDescending(g => g.Count()).Take(25))
        Console.WriteLine($"  {g.Count(),4}  {g.Key}  — à {g.Average(p => p!.DistanceKm):0.0} km en moyenne");
    return;
}

var sw = Stopwatch.StartNew();
var phones = MtpPhoneSource.Detect();

// « exists <chemin>… » : vérifie seulement la présence de fichiers sur le téléphone.
if (args.Length > 1 && args[0] == "exists")
{
    foreach (var phone in phones)
    {
        foreach (var path in args.Skip(1))
            Console.WriteLine($"{(phone.FileExists(path) ? "PRÉSENT" : "ABSENT ")}  {path}");
        phone.Dispose();
    }
    return;
}
Console.WriteLine($"Téléphones détectés : {phones.Count} ({sw.Elapsed.TotalSeconds:0.0} s)");

foreach (var phone in phones)
{
    Console.WriteLine($"\n== {phone.Name} (id {phone.DeviceId})");
    sw.Restart();
    using var index = new LibraryIndex(library);
    var plan = new TransferEngine(index).Plan(phone, settings.Filter);
    Console.WriteLine($"Analyse : {sw.Elapsed.TotalSeconds:0.0} s");
    Console.WriteLine($"Nouveaux fichiers : {plan.Items.Count} ({TransferEngine.FormatSize(plan.TotalBytes)}), déjà transférés : {plan.AlreadyImported}");

    foreach (var g in plan.Items.GroupBy(i => (i.Origin, i.Kind)).OrderBy(g => g.Key.Origin))
        Console.WriteLine($"  {MediaClassifier.Label(g.Key.Origin),-18} {g.Key.Kind,-6} {g.Count(),6}  {TransferEngine.FormatSize(g.Sum(i => i.Item.Size)),10}");

    Console.WriteLine("Dossiers :");
    foreach (var g in plan.Items.GroupBy(i => Path.GetDirectoryName(i.Item.Path)).OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {g.Count(),6}  {g.Key}");

    var dated = plan.Items.Select(i => DateResolver.TryFromFileName(i.Item.Name, out var d) ? d : (DateTime?)null).ToList();
    Console.WriteLine($"Date lisible dans le nom : {dated.Count(d => d is not null)} / {plan.Items.Count}");
    var withDates = dated.Where(d => d is not null).Select(d => d!.Value).ToList();
    if (withDates.Count > 0)
        Console.WriteLine($"Période (d'après les noms) : {withDates.Min():dd/MM/yyyy} → {withDates.Max():dd/MM/yyyy}");
    Console.WriteLine("Exemples :");
    foreach (var item in plan.Items.Take(5))
        Console.WriteLine($"  {item.Item.Path}  ({TransferEngine.FormatSize(item.Item.Size)}, modifié {item.Item.Modified:dd/MM/yyyy HH:mm})");
    phone.Dispose();
}
