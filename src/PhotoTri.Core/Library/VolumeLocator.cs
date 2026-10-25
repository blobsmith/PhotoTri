using System.Runtime.InteropServices;
using System.Text;

namespace PhotoTri.Core.Library;

/// <summary>
/// Emplacement de la bibliothèque mémorisé par identifiant de volume (\\?\Volume{GUID}\) plutôt que
/// par lettre de lecteur : un disque externe est retrouvé même si Windows lui donne une autre lettre.
/// </summary>
public sealed record LibraryLocation(string VolumeId, string RelativePath, string LastKnownPath)
{
    public static LibraryLocation FromPath(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);
        var mountPoint = VolumeLocator.MountPointOf(fullPath);
        var volumeId = VolumeLocator.VolumeIdOf(mountPoint) ?? "";
        var relative = Path.GetRelativePath(mountPoint, fullPath);
        return new(volumeId, relative == "." ? "" : relative, fullPath);
    }

    /// <summary>Chemin actuel de la bibliothèque, ou null si le disque n'est pas branché.</summary>
    public string? Resolve()
    {
        if (VolumeId.Length == 0)
            return System.IO.Directory.Exists(LastKnownPath) ? LastKnownPath : null;
        var mountPoint = VolumeLocator.FindMountPoint(VolumeId);
        return mountPoint is null ? null : Path.Combine(mountPoint, RelativePath);
    }
}

public static class VolumeLocator
{
    public static string MountPointOf(string path)
    {
        var sb = new StringBuilder(1024);
        return GetVolumePathName(path, sb, (uint)sb.Capacity) ? sb.ToString() : Path.GetPathRoot(path)!;
    }

    public static string? VolumeIdOf(string mountPoint)
    {
        if (!mountPoint.EndsWith('\\')) mountPoint += '\\';
        var sb = new StringBuilder(64);
        return GetVolumeNameForVolumeMountPoint(mountPoint, sb, (uint)sb.Capacity) ? sb.ToString() : null;
    }

    public static string? FindMountPoint(string volumeId)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            if (string.Equals(VolumeIdOf(drive.RootDirectory.FullName), volumeId, StringComparison.OrdinalIgnoreCase))
                return drive.RootDirectory.FullName;
        }
        return null;
    }

    /// <summary>Description lisible du disque : « Disque externe MONDISQUE (E:) » ou « Disque local (C:) ».</summary>
    public static string Describe(string path)
    {
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        var letter = drive.Name.TrimEnd('\\');
        var isSystem = string.Equals(drive.Name, Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase);
        var kind = drive.DriveType == DriveType.Removable || !isSystem ? "Disque externe" : "Disque local";
        var label = drive.IsReady && drive.VolumeLabel.Length > 0 ? " " + drive.VolumeLabel : "";
        return $"{kind}{label} ({letter})";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumePathName(string lpszFileName, StringBuilder lpszVolumePathName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPoint(string lpszVolumeMountPoint, StringBuilder lpszVolumeName, uint cchBufferLength);
}
