using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhotoTri.App.People;
using PhotoTri.Core.Faces;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using PhotoTri.Core.Viewer;
using Wpf.Ui.Controls;

namespace PhotoTri.App.Photos;

/// <summary>Raccourci rond en tête de la chronologie : une personne, un lieu ou les visages à nommer.</summary>
sealed record Shortcut(string Label, string ToolTip, ImageSource? Image, SymbolRegular Symbol, long? PersonId = null, string? PlaceKey = null)
{
    public bool ShowSymbol => Image is null;
}

/// <summary>Page « Photos » : recherche, raccourcis, filtres et chronologie par jour.</summary>
public partial class PhotosView : UserControl
{
    const int MaxPeopleShortcuts = 6;
    const int MaxPlaceShortcuts = 4;

    sealed record Choice<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    LibraryCatalog _catalog = LibraryCatalog.Empty;
    string? _libraryRoot;
    List<CatalogEntry> _visible = new();
    bool _populating;

    /// <summary>Bouton « Importer » : aller au transfert.</summary>
    public event Action? ImportRequested;

    /// <summary>Raccourci vers une personne (null : les visages à nommer).</summary>
    public event Action<long?>? PersonRequested;

    /// <summary>Raccourci vers un lieu (clé de la ville).</summary>
    public event Action<string>? PlaceRequested;

    public PhotosView()
    {
        InitializeComponent();
        _populating = true;
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
        _populating = false;
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            ApplyFilter();
        };
    }

    /// <summary>
    /// Affiche le catalogue. <paramref name="libraryRoot"/> est null quand le disque n'est pas branché :
    /// seules les miniatures gardées sur le PC sont alors visibles.
    /// </summary>
    public void Load(LibraryCatalog catalog, PeopleCatalog people, string? libraryRoot)
    {
        _catalog = catalog;
        _libraryRoot = libraryRoot;
        OfflineBanner.IsOpen = libraryRoot is null && catalog.Entries.Count > 0;

        _populating = true;
        var selectedDevice = (DeviceFilter.SelectedItem as Choice<string?>)?.Value;
        var devices = new[] { new Choice<string?>("Tous les téléphones", null) }
            .Concat(catalog.Devices.OrderBy(d => d.Value).Select(d => new Choice<string?>(d.Value, d.Key)))
            .ToList();
        DeviceFilter.ItemsSource = devices;
        DeviceFilter.SelectedItem = devices.FirstOrDefault(d => d.Value == selectedDevice) ?? devices[0];
        _populating = false;

        BuildShortcuts(people);
        ApplyFilter();
    }

    void BuildShortcuts(PeopleCatalog people)
    {
        var shortcuts = people.Persons.Take(MaxPeopleShortcuts)
            .Select(p => new Shortcut(p.Name, $"Les photos de {p.Name}", FaceImages.Load(p.Faces[0].CropPath), SymbolRegular.Person24, PersonId: p.Id))
            .ToList();
        if (people.Groups.Count > 0)
            shortcuts.Add(new Shortcut("À nommer", $"{people.Groups.Count} groupe(s) de visages à nommer", null, SymbolRegular.PersonQuestionMark24));
        shortcuts.AddRange(_catalog.Entries.Where(e => e.Place is not null)
            .GroupBy(e => e.Place!.Key).OrderByDescending(g => g.Count()).Take(MaxPlaceShortcuts)
            .Select(g => new Shortcut(g.First().Place!.City, $"{g.First().Place!.City}, {g.First().Place!.Country} · {g.Count()} photo(s)",
                null, SymbolRegular.Location24, PlaceKey: g.Key)));
        Shortcuts.ItemsSource = shortcuts;
        Shortcuts.Visibility = shortcuts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    ViewerFilter CurrentFilter => new(
        (KindFilter.SelectedItem as Choice<MediaKind?>)?.Value,
        (OriginFilter.SelectedItem as Choice<MediaOrigin?>)?.Value,
        (DeviceFilter.SelectedItem as Choice<string?>)?.Value);

    void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_populating) ApplyFilter();
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        // On attend une courte pause dans la frappe avant de chercher.
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    void ApplyFilter()
    {
        var filter = CurrentFilter;
        var search = LibrarySearch.Matcher(SearchBox.Text ?? "");
        _visible = _catalog.Entries.Where(e => filter.Accepts(e) && search(e)).ToList();
        var photos = _visible.Count(e => e.Kind == MediaKind.Photo);
        CountText.Text = $"{photos:N0} photo(s) · {_visible.Count - photos:N0} vidéo(s)";
        BuildMonthJump();
        Timeline.EmptyMessage = _catalog.Entries.Count == 0
            ? "Aucune photo pour l'instant. Cliquez sur « Importer » pour copier les photos de votre téléphone."
            : "Aucune photo ne correspond à votre recherche.";
        Timeline.Show(_visible, _libraryRoot);
    }

    void BuildMonthJump()
    {
        _populating = true;
        MonthJump.ItemsSource = new[] { new Choice<(int, int)>("Aller à un mois…", (0, 0)) }
            .Concat(_visible.GroupBy(e => (e.TakenAt.Year, e.TakenAt.Month))
                .Select(g => new Choice<(int, int)>($"{Core.Viewer.Timeline.MonthTitle(g.Key.Year, g.Key.Month)}  ({g.Count()})", g.Key)))
            .ToList();
        MonthJump.SelectedIndex = 0;
        _populating = false;
    }

    void MonthJump_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating || MonthJump.SelectedItem is not Choice<(int Year, int Month)> { Value.Year: > 0 } choice) return;
        Timeline.ScrollToMonth(choice.Value.Year, choice.Value.Month);
    }

    void Import_Click(object sender, RoutedEventArgs e) => ImportRequested?.Invoke();

    void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not Shortcut s) return;
        if (s.PlaceKey is { } key) PlaceRequested?.Invoke(key);
        else PersonRequested?.Invoke(s.PersonId);
    }
}
