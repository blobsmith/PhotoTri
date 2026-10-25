using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoTri.App.Thumbnails;
using PhotoTri.Core.Library;
using PhotoTri.Core.Viewer;

namespace PhotoTri.App.Photos;

/// <summary>
/// Chronologie de vignettes (titres de mois et de jours, rangées de miniatures), utilisée par les
/// onglets Photos et Lieux. Seules les lignes visibles sont créées : fluide même avec des milliers de photos.
/// </summary>
public partial class TimelineView : UserControl
{
    const double TileWidth = 156; // vignette de 150 + marges

    readonly Dictionary<long, PhotoTile> _tiles = new();
    IReadOnlyList<CatalogEntry> _entries = Array.Empty<CatalogEntry>();
    string? _libraryRoot;
    List<object> _rows = new();
    int _columns;

    public TimelineView()
    {
        InitializeComponent();
    }

    /// <summary>Texte affiché quand il n'y a rien à montrer.</summary>
    public string EmptyMessage
    {
        get => EmptyText.Text;
        set => EmptyText.Text = value;
    }

    /// <summary>Affiche ces entrées, triées de la plus récente à la plus ancienne.</summary>
    public void Show(IReadOnlyList<CatalogEntry> entries, string? libraryRoot)
    {
        if (libraryRoot != _libraryRoot) _tiles.Clear();
        _libraryRoot = libraryRoot;
        _entries = entries;
        EmptyPanel.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildRows(force: true);
    }

    public void ScrollToMonth(int year, int month)
    {
        var index = _rows.FindIndex(r => r is MonthHeaderRow h && h.Year == year && (month == 0 || h.Month == month));
        if (index >= 0) ScrollToRow(index);
    }

    public void ScrollToTop()
    {
        if (_rows.Count > 0) ScrollToRow(0);
    }

    void Rows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) BuildRows(force: false);
    }

    void BuildRows(bool force)
    {
        // On retire la largeur de la barre de défilement.
        var columns = Math.Max(1, (int)((Rows.ActualWidth - SystemParameters.VerticalScrollBarWidth - 8) / TileWidth));
        if (!force && columns == _columns) return;
        _columns = columns;

        // On reste sur la même photo après un rechargement (lieux trouvés en arrière-plan, nouvelle largeur…).
        var anchor = FirstVisibleEntry();
        _rows = Timeline.Build(_entries, columns)
            .Select(row => row is ThumbnailRow t ? new TileRow(t.Items.Select(TileFor).ToList()) : (object)row)
            .ToList();
        Rows.ItemsSource = _rows;
        if (anchor is not null) ScrollToEntry(anchor);
    }

    PhotoTile TileFor(CatalogEntry entry)
    {
        // Une vignette est recréée si l'entrée a changé (lieu ou adresse trouvés depuis).
        if (!_tiles.TryGetValue(entry.Id, out var tile) || tile.Entry != entry)
            _tiles[entry.Id] = tile = new PhotoTile(entry, _libraryRoot, ThumbnailService.Shared);
        return tile;
    }

    ScrollViewer? RowsScroller => FindChild<ScrollViewer>(Rows);

    CatalogEntry? FirstVisibleEntry()
    {
        if (RowsScroller is not { } scroller || _rows.Count == 0) return null;
        for (var i = (int)scroller.VerticalOffset; i < _rows.Count; i++)
            if (_rows[i] is TileRow row) return row.Tiles[0].Entry;
        return null;
    }

    void ScrollToEntry(CatalogEntry entry)
    {
        var index = _rows.FindIndex(r => r is TileRow t && t.Tiles.Any(x => x.Entry.Id == entry.Id));
        if (index < 0) return;
        // On garde visibles les titres du jour et du mois juste au-dessus de la rangée.
        while (index > 0 && _rows[index - 1] is not TileRow) index--;
        ScrollToRow(index);
    }

    void ScrollToRow(int index)
    {
        Rows.UpdateLayout();
        RowsScroller?.ScrollToVerticalOffset(index);
    }

    // Le défilement se fait par ligne : deux lignes par cran de molette, plus confortable que trois.
    void Rows_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (RowsScroller is not { } scroller) return;
        scroller.ScrollToVerticalOffset(scroller.VerticalOffset - Math.Sign(e.Delta) * 2);
        e.Handled = true;
    }

    void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not PhotoTile tile) return;
        var tiles = _entries.Select(TileFor).ToList();
        var viewer = new ViewerWindow(tiles, tiles.FindIndex(t => t.Entry.Id == tile.Entry.Id)) { Owner = Window.GetWindow(this) };
        viewer.ShowDialog();
        // Revenir sur la dernière photo regardée dans la visionneuse.
        if (viewer.CurrentTile is { } last) ScrollToEntry(last.Entry);
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
