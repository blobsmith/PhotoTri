using System.IO;
using System.Runtime.InteropServices;

namespace PhotoTri.App;

/// <summary>Mise à la corbeille de Windows (les fichiers restent récupérables), comme depuis l'Explorateur.</summary>
static class RecycleBin
{
    /// <summary>Renvoie vrai si le fichier n'est plus à sa place après l'opération.</summary>
    public static bool Send(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // liste terminée par deux caractères nuls
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }

    /// <summary>Les disques amovibles (clés USB, cartes SD) n'ont pas de corbeille : la suppression y est définitive.</summary>
    public static bool HasRecycleBin(string path) =>
        new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Fixed;

    const uint FO_DELETE = 3;
    const ushort FOF_SILENT = 0x0004;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);
}
