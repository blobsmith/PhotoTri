using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoTri.App.Thumbnails;

/// <summary>
/// Fournit les miniatures en arrière-plan. Elles sont gardées sur le PC
/// (%LocalAppData%\PhotoTri\Thumbnails, une par empreinte de fichier) pour rester visibles quand le
/// disque externe est débranché. Les demandes les plus récentes passent en premier : ce sont
/// celles des miniatures actuellement à l'écran.
/// </summary>
sealed class ThumbnailService : IDisposable
{
    public const int CacheSize = 320;
    const int DisplaySize = 240;
    const int MemoryCapacity = 500;
    const int WorkerCount = 3;

    /// <summary>Service unique, partagé par les onglets Photos et Lieux (cache mémoire commun).</summary>
    public static ThumbnailService Shared { get; } = new();

    sealed record PendingRequest(string Sha256, string? SourcePath, Action<BitmapSource?> Done);

    readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoTri", "Thumbnails");
    readonly ConcurrentStack<PendingRequest> _pending = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly CancellationTokenSource _stop = new();
    readonly object _memoryLock = new();
    readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> _memory = new();
    readonly LinkedList<(string Key, BitmapSource Image)> _lru = new();

    public ThumbnailService()
    {
        // Les fournisseurs de miniatures de Windows exigent des threads STA.
        for (var i = 0; i < WorkerCount; i++)
        {
            var thread = new Thread(Work) { IsBackground = true, Name = "Miniatures " + i };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
    }

    /// <summary>Miniature déjà en mémoire, sinon null (et il faut appeler <see cref="Request"/>).</summary>
    public BitmapSource? TryGetCached(string sha256)
    {
        lock (_memoryLock)
        {
            if (!_memory.TryGetValue(sha256, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    /// <summary>
    /// Demande une miniature. <paramref name="done"/> est appelé depuis un thread d'arrière-plan,
    /// avec null si aucune miniature n'est disponible. <paramref name="sourcePath"/> est null si
    /// le fichier n'est pas accessible (disque débranché) : seul le cache est alors utilisé.
    /// </summary>
    public void Request(string sha256, string? sourcePath, Action<BitmapSource?> done)
    {
        _pending.Push(new PendingRequest(sha256, sourcePath, done));
        _signal.Release();
    }

    void Work()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _signal.Wait(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (!_pending.TryPop(out var request)) continue;

            BitmapSource? image = null;
            try
            {
                image = TryGetCached(request.Sha256) ?? Load(request);
            }
            catch (Exception)
            {
                // Fichier illisible : la vignette affichera un emplacement vide.
            }
            request.Done(image);
        }
    }

    BitmapSource? Load(PendingRequest request)
    {
        var cacheFile = Path.Combine(_cacheDir, request.Sha256[..2], request.Sha256 + ".jpg");
        BitmapSource? image = null;
        if (File.Exists(cacheFile))
        {
            image = Decode(cacheFile);
        }
        else if (request.SourcePath is not null && File.Exists(request.SourcePath))
        {
            var shell = ShellThumbnail.Get(request.SourcePath, CacheSize);
            if (shell is not null)
            {
                Save(shell, cacheFile);
                image = Decode(cacheFile);
            }
        }
        if (image is not null) Remember(request.Sha256, image);
        return image;
    }

    static BitmapSource Decode(string file)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelHeight = DisplaySize;
        bitmap.UriSource = new Uri(file);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    static void Save(BitmapSource image, string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
        encoder.Frames.Add(BitmapFrame.Create(image));
        var temp = file + ".tmp";
        using (var stream = File.Create(temp)) encoder.Save(stream);
        File.Move(temp, file, overwrite: true);
    }

    void Remember(string key, BitmapSource image)
    {
        lock (_memoryLock)
        {
            if (_memory.ContainsKey(key)) return;
            _memory[key] = _lru.AddFirst((key, image));
            while (_memory.Count > MemoryCapacity)
            {
                _memory.Remove(_lru.Last!.Value.Key);
                _lru.RemoveLast();
            }
        }
    }

    public void Dispose() => _stop.Cancel();
}
