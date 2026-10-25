using System.Windows;
using System.Windows.Controls;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using PhotoTri.Core.Viewer;

namespace PhotoTri.App.Photos;

/// <summary>Onglet « Photos » : chronologie par mois et par jour, avec filtres et arbre des dates.</summary>
public partial class PhotosView : UserControl
{
    sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    LibraryCatalog _catalog = LibraryCatalog.Empty;
    string? _libraryRoot;
    List<CatalogEntry> _visible = new();
    bool _populatingFilters;

    public PhotosView()
    {
        InitializeComponent();
        _populatingFilters = true;
        KindFilter.ItemsSource = new[]
        {
            new Choice<MediaKind?>("Photos et vidéos", null),
            new Choice<MediaKind?>("Photos", MediaKind.Photo),
            new Choice<MediaKind?>("Vidéos", MediaKind.Video),
        };
        KindFilter.SelectedIndex = 0;
        OriginFilter.ItemsSource = new[] { new Choice<MediaOrigin?>("Toutes les origines", null) }
            .Concat(Enum.GetValues<MediaOrigin>().Select(o => new Choice<MediaOrigin?>(MediaClassifier.Label(o), o)))
            .ToList();
        OriginFilter.SelectedIndex = 0;
        _populatingFilters = false;
    }

    /// <summary>
    /// Affiche le catalogue. <paramref name="libraryRoot"/> est null quand le disque n'est pas branché :
    /// seules les miniatures gardées sur le PC sont alors visibles.
    /// </summary>
    public void Load(LibraryCatalog catalog, string? libraryRoot)
    {
        _catalog = catalog;
        _libraryRoot = libraryRoot;
        OfflineBanner.Visibility = libraryRoot is null && catalog.Entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _populatingFilters = true;
        var selectedDevice = (DeviceFilter.SelectedItem as Choice<string?>)?.Value;
        var devices = new[] { new Choice<string?>("Tous les téléphones", null) }
            .Concat(catalog.Devices.OrderBy(d => d.Value).Select(d => new Choice<string?>(d.Value, d.Key)))
            .ToList();
        DeviceFilter.ItemsSource = devices;
        DeviceFilter.SelectedItem = devices.FirstOrDefault(d => d.Value == selectedDevice) ?? devices[0];
        _populatingFilters = false;

        ApplyFilter();
    }

    ViewerFilter CurrentFilter => new(
        (KindFilter.SelectedItem as Choice<MediaKind?>)?.Value,
        (OriginFilter.SelectedItem as Choice<MediaOrigin?>)?.Value,
        (DeviceFilter.SelectedItem as Choice<string?>)?.Value);

    void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_populatingFilters) ApplyFilter();
    }

    void ApplyFilter()
    {
        var filter = CurrentFilter;
        _visible = _catalog.Entries.Where(filter.Accepts).ToList();
        var photos = _visible.Count(e => e.Kind == MediaKind.Photo);
        CountText.Text = $"{photos:N0} photo(s), {_visible.Count - photos:N0} vidéo(s)";
        BuildTree();
        Timeline.Show(_visible, _libraryRoot);
    }

    // ---- Arbre des années et des mois ----

    void BuildTree()
    {
        DateTree.Items.Clear();
        foreach (var year in _visible.GroupBy(e => e.TakenAt.Year))
        {
            var yearItem = new TreeViewItem
            {
                Header = $"{year.Key}  ({year.Count():N0})",
                Tag = (year.Key, 0),
                FontWeight = FontWeights.SemiBold,
            };
            foreach (var month in year.GroupBy(e => e.TakenAt.Month))
            {
                yearItem.Items.Add(new TreeViewItem
                {
                    Header = $"{Core.Viewer.Timeline.MonthTitle(year.Key, month.Key).Split(' ')[0]}  ({month.Count():N0})",
                    Tag = (year.Key, month.Key),
                    FontWeight = FontWeights.Normal,
                });
            }
            DateTree.Items.Add(yearItem);
        }
        if (DateTree.Items.Count > 0) ((TreeViewItem)DateTree.Items[0]).IsExpanded = true;
    }

    void DateTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: (int year, int month) }) Timeline.ScrollToMonth(year, month);
    }
}
