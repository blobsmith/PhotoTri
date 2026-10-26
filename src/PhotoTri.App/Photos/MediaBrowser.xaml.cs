using System.Windows;
using System.Windows.Controls;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using Wpf.Ui.Controls;

namespace PhotoTri.App.Photos;

/// <summary>
/// Photos et vidéos affichées en vignettes ou en liste, avec le choix de l'affichage juste au-dessus.
/// Utilisé par les pages Personnes et Lieux ; la page Photos a sa propre barre (recherche et filtres).
/// </summary>
public partial class MediaBrowser : UserControl
{
    IReadOnlyList<CatalogEntry> _entries = Array.Empty<CatalogEntry>();
    string? _libraryRoot;
    bool _listMode;

    public MediaBrowser()
    {
        InitializeComponent();
    }

    /// <summary>Suppression demandée depuis la vue Liste (traitée par la fenêtre principale).</summary>
    public event Action<IReadOnlyList<CatalogEntry>>? DeleteRequested
    {
        add => MediaList.DeleteRequested += value;
        remove => MediaList.DeleteRequested -= value;
    }

    public string EmptyMessage
    {
        get => Timeline.EmptyMessage;
        set => Timeline.EmptyMessage = value;
    }

    public void Show(IReadOnlyList<CatalogEntry> entries, string? libraryRoot)
    {
        _entries = entries;
        _libraryRoot = libraryRoot;
        var photos = entries.Count(e => e.Kind == MediaKind.Photo);
        CountText.Text = $"{photos:N0} photo(s) · {entries.Count - photos:N0} vidéo(s)";
        Timeline.Show(entries, libraryRoot);
        if (_listMode) MediaList.Show(entries, libraryRoot);
    }

    public void ScrollToTop() => Timeline.ScrollToTop();

    void GridMode_Click(object sender, RoutedEventArgs e) => SetListMode(false);

    void ListMode_Click(object sender, RoutedEventArgs e) => SetListMode(true);

    void SetListMode(bool list)
    {
        _listMode = list;
        Timeline.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        MediaList.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        GridModeButton.Appearance = list ? ControlAppearance.Transparent : ControlAppearance.Secondary;
        ListModeButton.Appearance = list ? ControlAppearance.Secondary : ControlAppearance.Transparent;
        if (list) MediaList.Show(_entries, _libraryRoot);
    }
}
