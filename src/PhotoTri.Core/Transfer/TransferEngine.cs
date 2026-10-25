using PhotoTri.Core.Devices;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;

namespace PhotoTri.Core.Transfer;

public sealed record PlannedItem(PhoneItem Item, MediaKind Kind, MediaOrigin Origin);

public sealed record TransferPlan(IReadOnlyList<PlannedItem> Items, int AlreadyImported)
{
    public long TotalBytes => Items.Sum(i => i.Item.Size);
}

/// <summary>Avancement de l'analyse : fichiers parcourus sur le téléphone, dont nouveaux, et dossier en cours.</summary>
public sealed record PlanProgress(int Scanned, int New, string Folder);

public sealed record TransferProgress(int Done, int Total, string CurrentFile, long BytesDone, long BytesTotal);

public sealed record TransferError(string File, string Message);

public sealed class TransferReport
{
    public int Copied { get; set; }

    /// <summary>Fichiers dont le contenu était déjà dans la bibliothèque (venant d'un autre dossier ou téléphone).</summary>
    public int Duplicates { get; set; }

    public int DeletedFromPhone { get; set; }

    public List<TransferError> Errors { get; } = new();

    /// <summary>Raison de l'arrêt avant la fin (disque débranché, téléphone qui ne répond plus…), sinon null.</summary>
    public string? StoppedReason { get; set; }

    public bool Cancelled { get; set; }
}

public sealed class TransferException(string message) : Exception(message);

/// <summary>
/// Copie les nouveaux fichiers du téléphone dans la bibliothèque, vérifie chaque copie, puis,
/// si demandé, supprime du téléphone uniquement les fichiers dont la copie est vérifiée.
/// </summary>
public sealed class TransferEngine(LibraryIndex index)
{
    /// <summary>Marge d'espace libre gardée sur le disque de destination.</summary>
    public const long FreeSpaceMargin = 200L * 1024 * 1024;

    const int MaxConsecutiveErrors = 5;

    string Root => index.LibraryRoot;

    string IncomingDir => Path.Combine(Root, LibraryLayout.DataFolderName, "incoming");

    public TransferPlan Plan(IPhoneSource phone, MediaFilter filter,
        IProgress<PlanProgress>? progress = null, CancellationToken ct = default)
    {
        var items = new List<PlannedItem>();
        var known = 0;
        var scanned = 0;
        var lastReport = Environment.TickCount64;
        foreach (var item in phone.ListMedia(ct))
        {
            scanned++;
            // La lecture via USB est lente : on signale l'avancement environ 4 fois par seconde.
            if (progress is not null && Environment.TickCount64 - lastReport >= 250)
            {
                lastReport = Environment.TickCount64;
                progress.Report(new(scanned, items.Count, Path.GetDirectoryName(item.Path) ?? ""));
            }

            var kind = MediaClassifier.KindOf(item.Path);
            var origin = MediaClassifier.OriginOf(item.Path);
            if (kind is null || !filter.Accepts(kind.Value, origin)) continue;
            if (index.IsKnownDeviceItem(phone.DeviceId, item.Path, item.Size))
            {
                known++;
                continue;
            }
            items.Add(new PlannedItem(item, kind.Value, origin));
        }
        progress?.Report(new(scanned, items.Count, ""));
        return new TransferPlan(items, known);
    }

    public TransferReport Execute(IPhoneSource phone, TransferPlan plan, bool deleteFromPhone,
        IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var report = new TransferReport();
        EnsureDestinationAvailable();
        var free = new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace;
        if (plan.TotalBytes + FreeSpaceMargin > free)
            throw new TransferException(
                $"Espace insuffisant sur le disque de destination : {FormatSize(plan.TotalBytes)} nécessaires, " +
                $"{FormatSize(free)} disponibles.");

        CleanupIncoming();
        Directory.CreateDirectory(IncomingDir);
        index.TouchDevice(phone.DeviceId, phone.Name);

        long bytesDone = 0;
        var consecutiveErrors = 0;
        for (var i = 0; i < plan.Items.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                report.Cancelled = true;
                break;
            }
            var planned = plan.Items[i];
            progress?.Report(new(i, plan.Items.Count, planned.Item.Name, bytesDone, plan.TotalBytes));

            // Pendant la copie d'une grosse vidéo, on signale aussi l'avancement à l'intérieur du fichier.
            var lastReport = Environment.TickCount64;
            var fileIndex = i;
            var fileStart = bytesDone;
            void OnWritten(long written)
            {
                if (progress is null || Environment.TickCount64 - lastReport < 250) return;
                lastReport = Environment.TickCount64;
                progress.Report(new(fileIndex, plan.Items.Count, planned.Item.Name, fileStart + written, plan.TotalBytes));
            }

            try
            {
                var outcome = TransferOne(phone, planned, OnWritten);
                consecutiveErrors = 0;
                if (outcome == Outcome.Copied) report.Copied++; else report.Duplicates++;

                if (deleteFromPhone)
                {
                    try
                    {
                        phone.Delete(planned.Item);
                        report.DeletedFromPhone++;
                    }
                    catch (Exception ex)
                    {
                        report.Errors.Add(new(planned.Item.Path, "Copié, mais pas supprimé du téléphone : " + ex.Message));
                    }
                }
            }
            catch (Exception ex) when (!DestinationAvailable())
            {
                report.Errors.Add(new(planned.Item.Path, ex.Message));
                report.StoppedReason = "Le disque de destination n'est plus accessible (débranché ?). " +
                                       "Les fichiers restants n'ont pas été copiés ni supprimés du téléphone.";
                break;
            }
            catch (Exception ex)
            {
                report.Errors.Add(new(planned.Item.Path, ex.Message));
                if (++consecutiveErrors >= MaxConsecutiveErrors)
                {
                    report.StoppedReason = "Le téléphone ne répond plus (débranché ou verrouillé ?). " +
                                           "Les fichiers restants n'ont pas été copiés ni supprimés du téléphone.";
                    break;
                }
            }
            bytesDone += planned.Item.Size;
        }

        progress?.Report(new(plan.Items.Count, plan.Items.Count, "", plan.TotalBytes, plan.TotalBytes));
        if (DestinationAvailable()) CleanupIncoming();
        return report;
    }

    enum Outcome { Copied, Duplicate }

    Outcome TransferOne(IPhoneSource phone, PlannedItem planned, Action<long> onWritten)
    {
        var item = planned.Item;
        var temp = Path.Combine(IncomingDir, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            string hash;
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var hashing = new HashingStream(file, onWritten))
            {
                phone.CopyTo(item, hashing);
                file.Flush(flushToDisk: true);
                if (item.Size > 0 && hashing.BytesWritten != item.Size)
                    throw new TransferException(
                        $"Copie incomplète ({hashing.BytesWritten} octets reçus sur {item.Size}).");
                hash = hashing.HashHex();
            }

            if (index.FindByHash(hash) is not null)
            {
                index.AddDeviceItem(phone.DeviceId, item.Path, item.Size, hash);
                return Outcome.Duplicate;
            }

            var date = DateResolver.Resolve(temp, item.Name, item.Modified);
            var folder = Path.Combine(Root, LibraryLayout.MonthFolder(date.Value));
            Directory.CreateDirectory(folder);
            var destination = LibraryLayout.UniquePath(folder, item.Name);
            File.Move(temp, destination);

            // Vérification : on relit le fichier tel qu'il est écrit sur le disque.
            if (HashingStream.HashFile(destination) != hash)
            {
                File.Delete(destination);
                throw new TransferException("La copie sur le disque ne correspond pas au fichier du téléphone.");
            }
            File.SetCreationTime(destination, date.Value);
            File.SetLastWriteTime(destination, date.Value);

            index.AddFile(new LibraryFile(hash, item.Size, Path.GetRelativePath(Root, destination), date.Value,
                date.Source, planned.Kind, planned.Origin, phone.DeviceId, item.Path));
            return Outcome.Copied;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    bool DestinationAvailable() => Directory.Exists(Root);

    void EnsureDestinationAvailable()
    {
        if (!DestinationAvailable())
            throw new TransferException($"La bibliothèque est introuvable : {Root}. Le disque est-il branché ?");
    }

    /// <summary>Supprime les copies interrompues d'un transfert précédent.</summary>
    void CleanupIncoming()
    {
        if (!Directory.Exists(IncomingDir)) return;
        foreach (var part in Directory.EnumerateFiles(IncomingDir, "*.part"))
            File.Delete(part);
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} Mo",
        _ => $"{bytes / 1024.0:0} Ko",
    };
}
