using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using PhotoTri.Core;
using PhotoTri.Core.Devices;
using PhotoTri.Core.Faces;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using PhotoTri.Core.Places;
using PhotoTri.Core.Transfer;

namespace PhotoTri.App;

public partial class MainWindow : Window
{
    const int WM_DEVICECHANGE = 0x0219;

    readonly AppSettings _settings = AppSettings.Load();
    readonly DispatcherTimer _deviceChangeDebounce = new() { Interval = TimeSpan.FromSeconds(1.5) };
    List<MtpPhoneSource> _devices = new();
    TransferPlan? _plan;
    CancellationTokenSource? _cts;
    bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        _settings.Library ??= LibraryLocation.FromPath(Directory.CreateDirectory(AppSettings.DefaultLibraryPath).FullName);

        IncludePhotos.IsChecked = _settings.Filter.Photos;
        IncludeVideos.IsChecked = _settings.Filter.Videos;
        IncludeScreenshots.IsChecked = _settings.Filter.Screenshots;
        IncludeApps.IsChecked = _settings.Filter.Apps;
        (_settings.DeleteFromPhone ? DeleteFromPhone : KeepOnPhone).IsChecked = true;
        PlacesTab.OnlineAddressesEnabled = _settings.OnlineAddresses;
        PlacesTab.OnlineAddressesChanged += enabled =>
        {
            _settings.OnlineAddresses = enabled;
            _settings.Save();
            _ = RunPlacesAsync();
        };

        _deviceChangeDebounce.Tick += async (_, _) =>
        {
            _deviceChangeDebounce.Stop();
            if (_busy) return;
            UpdateLibraryStatus();
            await ReloadPhotosAsync();
            StartBackgroundWork();
            await RefreshDevicesAsync();
        };

        Loaded += async (_, _) =>
        {
            UpdateLibraryStatus();
            await ReloadPhotosAsync();
            StartBackgroundWork();
            await RefreshDevicesAsync();
        };
        PeopleTab.Modify = ModifyPeopleAsync;
        Closed += (_, _) =>
        {
            _placesCts?.Cancel();
            _facesCts?.Cancel();
            DisposeDevices();
        };
    }

    /// <summary>Analyses en arrière-plan : lieux et visages des nouveaux fichiers.</summary>
    void StartBackgroundWork()
    {
        _ = RunPlacesAsync();
        _ = RunFacesAsync();
    }

    // ---- Visages (en arrière-plan) ----

    CancellationTokenSource? _facesCts;

    async Task RunFacesAsync()
    {
        _facesCts?.Cancel();
        var cts = _facesCts = new CancellationTokenSource();
        var root = _settings.Library!.Resolve();
        if (root is null)
        {
            PeopleTab.SetStatus("Disque de la bibliothèque non branché.");
            return;
        }
        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                PeopleTab.SetStatus($"Recherche des visages : {p.Done:N0} / {p.Total:N0} photo(s)…");
                // Les groupes à nommer apparaissent au fur et à mesure.
                if (p.Done % 60 == 0) _ = ReloadPhotosAsync();
            });
            var analyzed = await Task.Run(() =>
            {
                using var index = new LibraryIndex(root);
                return FaceIndexer.Run(index, progress, cts.Token);
            });
            if (cts.IsCancellationRequested) return;
            PeopleTab.SetStatus(analyzed > 0 ? $"Visages à jour ({analyzed:N0} nouvelle(s) photo(s) analysée(s))." : "");
            if (analyzed > 0) await ReloadPhotosAsync();
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            PeopleTab.SetStatus("Recherche des visages interrompue : " + ex.Message);
        }
    }

    /// <summary>Modification demandée depuis l'onglet Personnes (nommer, renommer, corriger), puis rechargement.</summary>
    async Task ModifyPeopleAsync(Action<LibraryIndex> change)
    {
        var root = _settings.Library!.Resolve();
        if (root is null)
        {
            MessageBox.Show(this, "Le disque de la bibliothèque n'est pas branché.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            await Task.Run(() =>
            {
                using var index = new LibraryIndex(root);
                change(index);
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "La modification n'a pas pu être enregistrée : " + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await ReloadPhotosAsync();
    }

    // ---- Lieux (en arrière-plan) ----

    CancellationTokenSource? _placesCts;

    /// <summary>
    /// Lit la position GPS des nouveaux fichiers et leur attribue une ville, puis, si l'option est
    /// cochée, demande les adresses précises à OpenStreetMap. Relancé après chaque transfert.
    /// </summary>
    async Task RunPlacesAsync()
    {
        _placesCts?.Cancel();
        var cts = _placesCts = new CancellationTokenSource();
        var root = _settings.Library!.Resolve();
        if (root is null)
        {
            PlacesTab.SetStatus("Disque de la bibliothèque non branché.");
            return;
        }
        try
        {
            var gpsProgress = new Progress<int>(n => PlacesTab.SetStatus($"Lecture des positions GPS… {n:N0} fichier(s)"));
            var indexed = await Task.Run(() =>
            {
                using var index = new LibraryIndex(root);
                return LocationIndexer.Run(index, gpsProgress, cts.Token);
            });
            if (cts.IsCancellationRequested) return;
            if (indexed > 0) await ReloadPhotosAsync();

            if (!_settings.OnlineAddresses)
            {
                PlacesTab.SetStatus("");
                return;
            }
            var addressProgress = new Progress<(int Done, int Total)>(p =>
            {
                PlacesTab.SetStatus($"Adresses précises : {p.Done:N0} / {p.Total:N0} position(s), une par seconde…");
                if (p.Done % 30 == 0) _ = ReloadPhotosAsync();
            });
            var message = await Task.Run(async () =>
            {
                using var index = new LibraryIndex(root);
                using var resolver = new AddressResolver();
                return await resolver.RunAsync(index, addressProgress, cts.Token);
            });
            if (cts.IsCancellationRequested) return;
            PlacesTab.SetStatus(message ?? "Adresses précises à jour.");
            await ReloadPhotosAsync();
        }
        catch (Exception ex) when (!cts.IsCancellationRequested)
        {
            PlacesTab.SetStatus("Recherche des lieux interrompue : " + ex.Message);
        }
    }

    // Branchement ou débranchement d'un téléphone ou d'un disque : on actualise automatiquement.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool _) =>
        {
            if (msg == WM_DEVICECHANGE)
            {
                _deviceChangeDebounce.Stop();
                _deviceChangeDebounce.Start();
            }
            return IntPtr.Zero;
        });
    }

    MediaFilter CurrentFilter => new(
        IncludePhotos.IsChecked == true,
        IncludeVideos.IsChecked == true,
        IncludeScreenshots.IsChecked == true,
        IncludeApps.IsChecked == true);

    MtpPhoneSource? SelectedDevice => DeviceList.SelectedItem as MtpPhoneSource;

    // ---- Téléphones ----

    async Task RefreshDevicesAsync()
    {
        DeviceHint.Text = "Recherche des téléphones…";
        var selectedId = SelectedDevice?.DeviceId;
        DisposeDevices();
        try
        {
            _devices = (await Task.Run(MtpPhoneSource.Detect)).ToList();
        }
        catch (Exception ex)
        {
            _devices = new();
            AppendLog("Erreur lors de la recherche des téléphones : " + ex.Message);
        }

        DeviceList.ItemsSource = _devices;
        DeviceList.SelectedItem = _devices.FirstOrDefault(d => d.DeviceId == selectedId) ?? _devices.FirstOrDefault();
        DeviceHint.Text = _devices.Count == 0
            ? "Aucun téléphone détecté. Branchez-le en USB, déverrouillez-le et choisissez « Transfert de fichiers » dans la notification USB."
            : $"{_devices.Count} téléphone(s) détecté(s).";
        UpdateButtons();
    }

    void DisposeDevices()
    {
        DeviceList.ItemsSource = null;
        foreach (var d in _devices) d.Dispose();
        _devices = new();
        _plan = null;
    }

    async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    void DeviceList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _plan = null;
        UpdateButtons();
    }

    // ---- Photos ----

    /// <summary>
    /// Recharge l'onglet Photos. Disque branché : lecture de la base de la bibliothèque, dont on garde
    /// une copie sur le PC. Disque débranché : lecture de cette copie.
    /// </summary>
    async Task ReloadPhotosAsync()
    {
        var location = _settings.Library!;
        var root = location.Resolve();
        LibraryCatalog catalog;
        PeopleCatalog people;
        try
        {
            (catalog, people) = await Task.Run(() =>
            {
                var db = root is null ? LibraryCatalog.SnapshotPath(location) : LibraryCatalog.DatabasePath(root);
                if (root is not null) LibraryCatalog.SaveSnapshot(root, location);
                return (LibraryCatalog.Load(db), PeopleCatalog.Load(db));
            });
        }
        catch (Exception ex)
        {
            AppendLog("Impossible de lire la bibliothèque : " + ex.Message);
            (catalog, people) = (LibraryCatalog.Empty, PeopleCatalog.Empty);
        }
        PhotosTab.Load(catalog, root);
        PlacesTab.Load(catalog, root);
        PeopleTab.Load(people, catalog, root);
        if (catalog.Entries.Count == 0 && !_shownTransferTab)
        {
            // Premier lancement : rien à montrer, on propose directement le transfert.
            _shownTransferTab = true;
            Tabs.SelectedItem = TransferTab;
        }
    }

    bool _shownTransferTab;

    // ---- Destination ----

    void UpdateLibraryStatus()
    {
        var location = _settings.Library!;
        var path = location.Resolve();
        if (path is null)
        {
            LibraryPath.Text = location.LastKnownPath;
            LibraryStatus.Text = "⚠ Disque non branché : branchez le disque de la bibliothèque pour pouvoir transférer.";
        }
        else
        {
            LibraryPath.Text = path;
            Directory.CreateDirectory(path);
            var free = new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;
            LibraryStatus.Text = $"{VolumeLocator.Describe(path)} — {TransferEngine.FormatSize(free)} libres";
        }
        UpdateButtons();
    }

    async void ChangeLibrary_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choisir le dossier de la bibliothèque (sur ce PC ou sur un disque externe)",
            InitialDirectory = _settings.Library?.Resolve() ?? AppSettings.DefaultLibraryPath,
        };
        if (dialog.ShowDialog(this) != true) return;

        _settings.Library = LibraryLocation.FromPath(dialog.FolderName);
        _settings.Save();
        _plan = null;
        UpdateLibraryStatus();
        AppendLog($"Bibliothèque : {dialog.FolderName}");
        await ReloadPhotosAsync();
        StartBackgroundWork();
    }

    // ---- Options ----

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        _settings.Filter = CurrentFilter;
        _settings.DeleteFromPhone = DeleteFromPhone.IsChecked == true;
        _settings.Save();
        _plan = null;
    }

    // ---- Analyse et transfert ----

    async void Analyze_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    async Task<bool> AnalyzeAsync()
    {
        if (SelectedDevice is not { } phone || OpenLibraryPath() is not { } root) return false;
        var filter = CurrentFilter;
        SetBusy(true, "Analyse du téléphone…");
        var token = _cts!.Token;
        var progress = new Progress<PlanProgress>(p =>
        {
            var folder = p.Folder.Length > 0 ? $" — {Path.GetFileName(p.Folder)}" : "";
            ProgressText.Text = $"Analyse du téléphone… {p.Scanned:N0} fichier(s) parcouru(s), {p.New:N0} nouveau(x){folder}";
        });
        try
        {
            _plan = await Task.Run(() =>
            {
                using var index = new LibraryIndex(root);
                return new TransferEngine(index).Plan(phone, filter, progress, token);
            });
            AppendLog($"{phone.Name} : {_plan.Items.Count} nouveau(x) fichier(s) ({TransferEngine.FormatSize(_plan.TotalBytes)}), " +
                      $"{_plan.AlreadyImported} déjà transféré(s).");
            ProgressText.Text = _plan.Items.Count == 0
                ? "Rien de nouveau à transférer."
                : $"{_plan.Items.Count} fichier(s) prêt(s) à être transféré(s).";
            return true;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Analyse annulée.");
            return false;
        }
        catch (Exception ex)
        {
            AppendLog("Erreur pendant l'analyse : " + ex.Message);
            ProgressText.Text = "L'analyse a échoué. Le téléphone est-il toujours branché et déverrouillé ?";
            return false;
        }
        finally
        {
            SetBusy(false);
        }
    }

    async void Transfer_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null && !await AnalyzeAsync()) return;
        if (SelectedDevice is not { } phone || OpenLibraryPath() is not { } root || _plan is not { } plan) return;
        if (plan.Items.Count == 0)
        {
            MessageBox.Show(this, "Il n'y a aucun nouveau fichier à transférer.", Title);
            return;
        }

        var delete = DeleteFromPhone.IsChecked == true;
        if (delete && MessageBox.Show(this,
                $"{plan.Items.Count} fichier(s) vont être copiés puis supprimés du téléphone « {phone.Name} ».\n\n" +
                "Seuls les fichiers dont la copie a été vérifiée seront supprimés. Continuer ?",
                Title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        SetBusy(true, "Transfert en cours…");
        var token = _cts!.Token;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var eta = new TransferEta();
        var progress = new Progress<TransferProgress>(p =>
        {
            Progress.Maximum = Math.Max(1, p.BytesTotal);
            Progress.Value = p.BytesDone;
            if (p.Done >= p.Total)
            {
                ProgressText.Text = "Terminé.";
                return;
            }
            eta.Add(clock.Elapsed, p.Done, p.BytesDone);
            var text = $"{p.Done + 1:N0} / {p.Total:N0} — {p.CurrentFile}";
            if (eta.BytesPerSecond is { } speed) text += $" — {TransferEta.FormatSpeed(speed)}";
            text += eta.Remaining(p.Total, p.BytesTotal) is { } left
                ? $" — {TransferEta.FormatRemaining(left)}"
                : " — calcul du temps restant…";
            ProgressText.Text = text;
        });
        // Le PC ne doit pas se mettre en veille au milieu du transfert.
        using var keepAwake = new KeepAwake();
        try
        {
            var report = await Task.Run(() =>
            {
                using var index = new LibraryIndex(root);
                return new TransferEngine(index).Execute(phone, plan, delete, progress, token);
            });
            ShowReport(report, delete);
        }
        catch (Exception ex)
        {
            AppendLog("Transfert impossible : " + ex.Message);
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _plan = null;
            SetBusy(false);
            UpdateLibraryStatus();
        }
        await ReloadPhotosAsync();
        StartBackgroundWork();
    }

    void ShowReport(TransferReport report, bool delete)
    {
        var lines = new List<string>
        {
            $"{report.Copied} fichier(s) copié(s) et vérifié(s).",
        };
        if (report.Duplicates > 0)
            lines.Add($"{report.Duplicates} fichier(s) déjà présent(s) dans la bibliothèque (non recopiés).");
        if (delete)
            lines.Add($"{report.DeletedFromPhone} fichier(s) supprimé(s) du téléphone.");
        if (report.Cancelled)
            lines.Add("Transfert annulé : les fichiers restants n'ont été ni copiés ni supprimés.");
        if (report.StoppedReason is not null)
            lines.Add(report.StoppedReason);
        if (report.Errors.Count > 0)
            lines.Add($"{report.Errors.Count} erreur(s), voir le journal.");

        foreach (var error in report.Errors) AppendLog($"Erreur — {error.File} : {error.Message}");
        foreach (var line in lines) AppendLog(line);

        var hasProblems = report.Errors.Count > 0 || report.StoppedReason is not null;
        MessageBox.Show(this, string.Join("\n", lines), Title, MessageBoxButton.OK,
            hasProblems ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        ProgressText.Text = "Annulation… (le fichier en cours se termine)";
    }

    string? OpenLibraryPath()
    {
        var path = _settings.Library?.Resolve();
        if (path is null)
        {
            UpdateLibraryStatus();
            MessageBox.Show(this, "Le disque de la bibliothèque n'est pas branché.\n\n" + _settings.Library?.LastKnownPath,
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return path;
    }

    // ---- Interface ----

    void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        if (busy)
        {
            _cts = new CancellationTokenSource();
            Progress.Value = 0;
            Progress.IsIndeterminate = message?.StartsWith("Analyse") == true;
        }
        else
        {
            Progress.IsIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }
        if (message is not null) ProgressText.Text = message;
        CancelButton.IsEnabled = busy;
        DeviceList.IsEnabled = !busy;
        UpdateButtons();
    }

    void UpdateButtons()
    {
        var ready = !_busy && SelectedDevice is not null && _settings.Library?.Resolve() is not null;
        AnalyzeButton.IsEnabled = ready;
        TransferButton.IsEnabled = ready;
    }

    void AppendLog(string line)
    {
        Log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        Log.ScrollToEnd();
    }
}
