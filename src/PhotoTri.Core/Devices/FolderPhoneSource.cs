using PhotoTri.Core.Media;

namespace PhotoTri.Core.Devices;

/// <summary>Un dossier qui imite le stockage d'un téléphone (tests et essais sans téléphone).</summary>
public sealed class FolderPhoneSource(string root, string deviceId, string name) : IPhoneSource
{
    public string DeviceId { get; } = deviceId;

    public string Name { get; } = name;

    public IEnumerable<PhoneItem> ListMedia(CancellationToken ct)
    {
        foreach (var scanRoot in MediaClassifier.ScanRoots)
        {
            var dir = Path.Combine(root, scanRoot);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, file);
                if (MediaClassifier.KindOf(file) is null || MediaClassifier.IsExcluded(relative)) continue;
                var info = new FileInfo(file);
                yield return new PhoneItem(relative, info.Length, info.LastWriteTime);
            }
        }
    }

    public void CopyTo(PhoneItem item, Stream destination)
    {
        using var source = File.OpenRead(Path.Combine(root, item.Path));
        source.CopyTo(destination);
    }

    public void Delete(PhoneItem item) => File.Delete(Path.Combine(root, item.Path));

    public void Dispose() { }
}
