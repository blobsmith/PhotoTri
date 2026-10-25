using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PhotoTri.App.Photos;

/// <summary>Copier un média dans le presse-papiers, ou le partager avec le panneau de partage de Windows.</summary>
static class MediaSharing
{
    /// <summary>
    /// Copie le fichier (à coller dans l'Explorateur, un e-mail, WhatsApp Web…) et, pour une photo,
    /// l'image elle-même (à coller dans Word, Paint…).
    /// </summary>
    public static void Copy(string path, BitmapSource? image)
    {
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        if (image is not null) data.SetImage(image);
        System.Windows.Clipboard.SetDataObject(data, copy: true);
    }

    /// <summary>Ouvre le panneau de partage de Windows (Mail, WhatsApp, Bluetooth, Partage de proximité…).</summary>
    public static void Share(Window owner, string path, string title)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var iid = DataTransferManagerIid;
        var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(interop.GetForWindow(hwnd, ref iid));

        // Le gestionnaire est propre à la fenêtre : on remplace la demande précédente.
        if (Handlers.Remove(hwnd, out var previous)) manager.DataRequested -= previous;
        Windows.Foundation.TypedEventHandler<DataTransferManager, DataRequestedEventArgs> handler = async (_, args) =>
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                args.Request.Data.Properties.Title = title;
                args.Request.Data.Properties.Description = Path.GetFileName(path);
                var file = await StorageFile.GetFileFromPathAsync(path);
                args.Request.Data.SetStorageItems(new IStorageItem[] { file });
            }
            catch (Exception ex)
            {
                args.Request.FailWithDisplayText("Le fichier ne peut pas être partagé : " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        };
        manager.DataRequested += handler;
        Handlers[hwnd] = handler;
        interop.ShowShareUIForWindow(hwnd);
    }

    static readonly Dictionary<IntPtr, Windows.Foundation.TypedEventHandler<DataTransferManager, DataRequestedEventArgs>> Handlers = new();

    // Identifiant de l'interface WinRT IDataTransferManager.
    static readonly Guid DataTransferManagerIid = new(0xa5caee9b, 0x8708, 0x49d1, 0x8d, 0x36, 0x67, 0xd2, 0x5a, 0x8d, 0xa0, 0x0c);

    // Pour une application de bureau, le panneau de partage s'ouvre par cette interface, avec la fenêtre propriétaire.
    [ComImport]
    [Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow(IntPtr appWindow, ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }
}
