using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoTri.App.Thumbnails;
using PhotoTri.Core.Library;
using PhotoTri.Core.Transfer;
using MessageBox = System.Windows.MessageBox;

namespace PhotoTri.App.Photos;

/// <summary>
/// Vue Liste : une ligne par photo ou vidéo, triable par colonne, avec sélection multiple
/// (Ctrl, Maj, Ctrl+A) pour copier, partager ou supprimer plusieurs fichiers d'un coup.
/// </summary>
public partial class MediaListView : UserControl
{
    readonly Dictionary<long, PhotoTile> _tiles = new();
    List<PhotoTile> _rows = new();
    string? _libraryRoot;
    string _sortColumn = "Date";
    bool _sortDescending = true;

    /// <summary>L'utilisateur demande la suppression de ces fichiers (confirmation et suppression : fenêtre principale).</summary>
    public event Action<IReadOnlyList<CatalogEntry>>? DeleteRequested;

    public MediaListView()
    {
        InitializeComponent();
        UpdateSelection();
    }

    public void Show(IReadOnlyList<CatalogEntry> entries, string? libraryRoot)
    {
        if (libraryRoot != _libraryRoot) _tiles.Clear();
        _libraryRoot = libraryRoot;
        var selected = SelectedTiles.Select(t => t.Entry.Id).ToHashSet();
        _rows = entries.Select(TileFor).ToList();
        ApplySort();
        // On garde la sélection après un rechargement ; sinon on repart du haut de la liste.
        foreach (var row in _rows.Where(r => selected.Contains(r.Entry.Id))) List.SelectedItems.Add(row);
        if (List.SelectedItems.Count == 0 && List.Items.Count > 0) List.ScrollIntoView(List.Items[0]);
        UpdateSelection();
    }

    PhotoTile TileFor(CatalogEntry entry)
    {
        if (!_tiles.TryGetValue(entry.Id, out var tile) || tile.Entry != entry)
            _tiles[entry.Id] = tile = new PhotoTile(entry, _libraryRoot, ThumbnailService.Shared);
        return tile;
    }

    List<PhotoTile> SelectedTiles => List.SelectedItems.Cast<PhotoTile>().ToList();

    /// <summary>Fichiers choisis encore présents sur le disque (le disque externe peut être débranché).</summary>
    List<string> SelectedPaths() =>
        SelectedTiles.Select(t => t.FullPath).Where(p => p is not null && File.Exists(p)).Select(p => p!).ToList();

    // ---- Colonnes : elles se partagent la largeur disponible ----

    // Part de chaque colonne (après la miniature) et largeur minimale.
    static readonly (double Share, double Min)[] ColumnSizes =
        { (0.22, 120), (0.15, 110), (0.20, 110), (0.16, 90), (0.17, 110), (0.10, 64) };

    void List_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var available = List.ActualWidth - Columns.Columns[0].Width - SystemParameters.VerticalScrollBarWidth - 12;
        for (var i = 0; i < ColumnSizes.Length; i++)
            Columns.Columns[i + 1].Width = Math.Max(ColumnSizes[i].Min, available * ColumnSizes[i].Share);
    }

    // ---- Tri ----

    void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column.Header: string column }) return;
        // Deuxième clic sur la même colonne : ordre inverse.
        _sortDescending = column == _sortColumn ? !_sortDescending : column is "Date" or "Taille";
        _sortColumn = column;
        var selected = SelectedTiles;
        ApplySort();
        foreach (var row in selected) List.SelectedItems.Add(row);
    }

    void ApplySort()
    {
        Func<PhotoTile, IComparable> key = _sortColumn switch
        {
            "Nom" => t => t.FileName,
            "Lieu" => t => t.PlaceText,
            "Personnes" => t => t.PeopleText,
            "Type" => t => t.KindText,
            "Taille" => t => t.Entry.Size,
            _ => t => t.Entry.TakenAt,
        };
        var sorted = _sortDescending ? _rows.OrderByDescending(key) : _rows.OrderBy(key);
        List.ItemsSource = sorted.ThenByDescending(t => t.Entry.TakenAt).ToList();
    }

    // ---- Sélection ----

    void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    void UpdateSelection()
    {
        var selected = SelectedTiles;
        var any = selected.Count > 0;
        CopyButton.IsEnabled = ShareButton.IsEnabled = DeleteButton.IsEnabled = any;
        SelectionText.Text = any
            ? $"{selected.Count:N0} élément(s) sélectionné(s) · {TransferEngine.FormatSize(selected.Sum(t => t.Entry.Size))}"
            : $"{_rows.Count:N0} élément(s) · Ctrl ou Maj pour en choisir plusieurs";
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) => List.SelectAll();

    void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RequestDelete();
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopySelection();
        else if (e.Key == Key.Enter) OpenViewer();
        else return;
        e.Handled = true;
    }

    // ---- Actions ----

    void Copy_Click(object sender, RoutedEventArgs e) => CopySelection();

    void CopySelection()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0 && !WarnUnavailable()) return;
        try
        {
            MediaSharing.Copy(paths);
            SelectionText.Text = $"{paths.Count:N0} fichier(s) copié(s) : collez-les dans un dossier, un e-mail ou un document.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "La copie n'a pas pu se faire : " + ex.Message, "PhotoTri",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Share_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0 && !WarnUnavailable()) return;
        try
        {
            MediaSharing.Share(Window.GetWindow(this)!, paths, paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} photos et vidéos");
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Le partage n'est pas disponible : " + ex.Message, "PhotoTri",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Delete_Click(object sender, RoutedEventArgs e) => RequestDelete();

    void RequestDelete()
    {
        var selected = SelectedTiles;
        if (selected.Count > 0) DeleteRequested?.Invoke(selected.Select(t => t.Entry).ToList());
    }

    bool WarnUnavailable()
    {
        MessageBox.Show(Window.GetWindow(this), "Les fichiers choisis ne sont pas accessibles. Le disque de la bibliothèque est-il branché ?",
            "PhotoTri", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenViewer();

    void OpenViewer()
    {
        if (List.SelectedItem is not PhotoTile tile || List.ItemsSource is not List<PhotoTile> rows) return;
        var viewer = new ViewerWindow(rows, rows.IndexOf(tile)) { Owner = Window.GetWindow(this) };
        viewer.ShowDialog();
        if (viewer.CurrentTile is { } last)
        {
            List.SelectedItem = last;
            List.ScrollIntoView(last);
        }
    }
}
