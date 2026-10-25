using MediaDevices;
using PhotoTri.Core.Media;

namespace PhotoTri.Core.Devices;

/// <summary>Téléphone Android branché en USB, en mode « Transfert de fichiers » (MTP).</summary>
public sealed class MtpPhoneSource : IPhoneSource
{
    readonly MediaDevice _device;

    MtpPhoneSource(MediaDevice device)
    {
        _device = device;
        DeviceId = string.IsNullOrWhiteSpace(device.SerialNumber) ? device.DeviceId : device.SerialNumber;
        Name = FirstNonEmpty(device.FriendlyName, device.Model, device.Description, "Téléphone");
    }

    public string DeviceId { get; }

    public string Name { get; }

    /// <summary>
    /// Téléphones branchés et déverrouillés. Un téléphone verrouillé, ou en mode « Recharge uniquement »,
    /// n'expose aucun stockage : il est ignoré.
    /// </summary>
    public static IReadOnlyList<MtpPhoneSource> Detect()
    {
        var found = new List<MtpPhoneSource>();
        foreach (var device in MediaDeviceManager.Instance.GetDevices() ?? [])
        {
            try
            {
                device.Connect(MediaDeviceAccess.Default, MediaDeviceShare.Default, enableCache: false);
                if (device.GetRootDirectory().EnumerateDirectories().Any())
                {
                    found.Add(new MtpPhoneSource(device));
                    continue;
                }
            }
            catch (Exception)
            {
                // Appareil inaccessible (verrouillé, en cours de déconnexion…).
            }
            device.Dispose();
        }
        return found;
    }

    public IEnumerable<PhoneItem> ListMedia(CancellationToken ct)
    {
        // Chaque stockage (mémoire interne, carte SD) est un dossier à la racine du téléphone.
        foreach (var storage in _device.GetRootDirectory().EnumerateDirectories())
        {
            foreach (var root in MediaClassifier.ScanRoots)
            {
                ct.ThrowIfCancellationRequested();
                var path = storage.FullName + "\\" + root.Replace('/', '\\');
                if (!_device.DirectoryExists(path)) continue;

                foreach (var file in _device.GetDirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (MediaClassifier.KindOf(file.Name) is null || MediaClassifier.IsExcluded(file.FullName)) continue;
                    yield return new PhoneItem(file.FullName, (long)file.Length, file.LastWriteTime);
                }
            }
        }
    }

    public void CopyTo(PhoneItem item, Stream destination) => _device.DownloadFile(item.Path, destination);

    public void Delete(PhoneItem item) => _device.DeleteFile(item.Path);

    public bool FileExists(string path) => _device.FileExists(path);

    public void Dispose()
    {
        if (_device.IsConnected) _device.Disconnect();
        _device.Dispose();
    }

    static string FirstNonEmpty(params string?[] values) => values.First(v => !string.IsNullOrWhiteSpace(v))!;
}
