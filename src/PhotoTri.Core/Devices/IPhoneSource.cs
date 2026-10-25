namespace PhotoTri.Core.Devices;

/// <summary>Un fichier vu sur le téléphone. <see cref="Path"/> est relatif à la racine du téléphone.</summary>
public sealed record PhoneItem(string Path, long Size, DateTime? Modified)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>Un téléphone branché (en USB/MTP en production, un dossier dans les tests).</summary>
public interface IPhoneSource : IDisposable
{
    /// <summary>Identifiant stable du téléphone (numéro de série).</summary>
    string DeviceId { get; }

    string Name { get; }

    /// <summary>Photos et vidéos présentes dans les dossiers connus du téléphone.</summary>
    IEnumerable<PhoneItem> ListMedia(CancellationToken ct);

    void CopyTo(PhoneItem item, Stream destination);

    void Delete(PhoneItem item);
}
