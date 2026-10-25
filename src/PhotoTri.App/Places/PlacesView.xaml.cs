using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using PhotoTri.Core.Library;

namespace PhotoTri.App.Places;

/// <summary>Onglet « Lieux » : pays et villes avec leur nombre de photos, carte, photos du lieu choisi.</summary>
public partial class PlacesView : UserControl
{
    const string MapHost = "phototri.carte";

    /// <summary>Ce qui est sélectionné dans la liste.</summary>
    abstract record Selection
    {
        public sealed record All : Selection;
        public sealed record Country(string Code, string Name) : Selection;
        public sealed record City(string Key, string Label) : Selection;
        public sealed record Unknown : Selection;
    }

    sealed record MapPlace(string Key, string Label, double Lat, double Lon, int Count);

    LibraryCatalog _catalog = LibraryCatalog.Empty;
    string? _libraryRoot;
    List<MapPlace> _mapPlaces = new();
    Selection _selection = new Selection.All();
    bool _mapReady;
    bool _mapStarted;
    bool _rebuildingTree;

    /// <summary>L'utilisateur a coché ou décoché la recherche des adresses en ligne.</summary>
    public event Action<bool>? OnlineAddressesChanged;

    public PlacesView()
    {
        InitializeComponent();
        // La carte n'est chargée qu'à la première ouverture de l'onglet.
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible && !_mapStarted) await StartMapAsync();
        };
    }

    public bool OnlineAddressesEnabled
    {
        get => OnlineAddresses.IsChecked == true;
        set => OnlineAddresses.IsChecked = value;
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public void Load(LibraryCatalog catalog, string? libraryRoot)
    {
        _catalog = catalog;
        _libraryRoot = libraryRoot;
        _mapPlaces = catalog.Entries
            .Where(e => e.Place is not null && e.Location is not null)
            .GroupBy(e => e.Place!.Key)
            .Select(g => new MapPlace(g.Key, $"{g.First().Place!.City}, {g.First().Place!.Country}",
                g.Average(e => e.Location!.Value.Latitude), g.Average(e => e.Location!.Value.Longitude), g.Count()))
            .ToList();
        BuildTree();
        PushPlacesToMap();
        ShowSelection(moveMap: false);
    }

    // ---- Liste des lieux ----

    void BuildTree()
    {
        _rebuildingTree = true;
        PlaceTree.Items.Clear();
        var located = _catalog.Entries.Where(e => e.Place is not null).ToList();
        PlaceTree.Items.Add(Item($"Tous les lieux  ({located.Count:N0})", new Selection.All(), bold: true));

        foreach (var country in located.GroupBy(e => e.Place!.CountryCode).OrderByDescending(g => g.Count()))
        {
            var countryName = country.First().Place!.Country;
            var countryItem = Item($"{countryName}  ({country.Count():N0})", new Selection.Country(country.Key, countryName), bold: true);
            foreach (var city in country.GroupBy(e => e.Place!.Key).OrderByDescending(g => g.Count()).ThenBy(g => g.First().Place!.City))
            {
                var place = city.First().Place!;
                var item = Item($"{place.City}  ({city.Count():N0})", new Selection.City(city.Key, $"{place.City}, {place.Country}"), bold: false);
                item.ToolTip = place.Region;
                countryItem.Items.Add(item);
            }
            PlaceTree.Items.Add(countryItem);
        }

        var unknown = _catalog.Entries.Count - located.Count;
        if (unknown > 0)
        {
            var item = Item($"Lieu inconnu  ({unknown:N0})", new Selection.Unknown(), bold: false);
            item.ToolTip = "Photos sans position GPS : captures d'écran, photos reçues par WhatsApp, GPS désactivé…";
            PlaceTree.Items.Add(item);
        }

        // On resélectionne le même lieu après un rechargement.
        var toSelect = FindItem(_selection) ?? (TreeViewItem)PlaceTree.Items[0];
        if (toSelect.Parent is TreeViewItem parent) parent.IsExpanded = true;
        toSelect.IsSelected = true;
        _rebuildingTree = false;
    }

    static TreeViewItem Item(string header, Selection tag, bool bold) => new()
    {
        Header = header,
        Tag = tag,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };

    TreeViewItem? FindItem(Selection selection)
    {
        foreach (TreeViewItem item in PlaceTree.Items)
        {
            if (Equals(item.Tag, selection)) return item;
            foreach (TreeViewItem child in item.Items)
                if (Equals(child.Tag, selection)) return child;
        }
        return null;
    }

    void PlaceTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_rebuildingTree || e.NewValue is not TreeViewItem { Tag: Selection selection }) return;
        _selection = selection;
        ShowSelection(moveMap: true);
    }

    void ShowSelection(bool moveMap)
    {
        IEnumerable<CatalogEntry> entries = _catalog.Entries;
        string title;
        List<string> keys;
        switch (_selection)
        {
            case Selection.Country c:
                entries = entries.Where(e => e.Place?.CountryCode == c.Code);
                title = c.Name;
                keys = _mapPlaces.Where(p => p.Key.StartsWith(c.Code + "|")).Select(p => p.Key).ToList();
                break;
            case Selection.City c:
                entries = entries.Where(e => e.Place?.Key == c.Key);
                title = c.Label;
                keys = new() { c.Key };
                break;
            case Selection.Unknown:
                entries = entries.Where(e => e.Place is null);
                title = "Lieu inconnu";
                keys = new();
                break;
            default:
                entries = entries.Where(e => e.Place is not null);
                title = "Tous les lieux";
                keys = new();
                break;
        }
        var list = entries.ToList();
        TitleText.Text = $"{title} · {list.Count:N0} photo(s) et vidéo(s)";
        Timeline.Show(list, _libraryRoot);
        if (moveMap)
        {
            Timeline.ScrollToTop();
            FocusMap(keys);
        }
    }

    void OnlineAddresses_Click(object sender, RoutedEventArgs e) => OnlineAddressesChanged?.Invoke(OnlineAddressesEnabled);

    // ---- Carte ----

    async Task StartMapAsync()
    {
        _mapStarted = true;
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoTri");
            var pageDir = Directory.CreateDirectory(Path.Combine(dataDir, "Carte")).FullName;
            await File.WriteAllTextAsync(Path.Combine(pageDir, "carte.html"), MapPage.Html);

            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(dataDir, "WebView2"));
            await Map.EnsureCoreWebView2Async(env);
            // Page servie depuis une adresse https locale : les fonds de carte OpenStreetMap exigent une origine valide.
            Map.CoreWebView2.SetVirtualHostNameToFolderMapping(MapHost, pageDir, CoreWebView2HostResourceAccessKind.Deny);
            Map.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Map.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Map.CoreWebView2.WebMessageReceived += (_, args) => SelectPlace(args.TryGetWebMessageAsString());
            Map.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _mapReady = true;
                PushPlacesToMap();
                ShowSelection(moveMap: true);
            };
            Map.CoreWebView2.Navigate($"https://{MapHost}/carte.html");
        }
        catch (Exception ex)
        {
            Map.Visibility = Visibility.Collapsed;
            MapError.Visibility = Visibility.Visible;
            MapError.Text = "La carte n'a pas pu s'afficher (composant Microsoft Edge WebView2 manquant ?). " +
                            "Les lieux restent disponibles dans la liste.\n\n" + ex.Message;
        }
    }

    void PushPlacesToMap()
    {
        if (!_mapReady) return;
        var json = JsonSerializer.Serialize(_mapPlaces.Select(p => new { key = p.Key, label = p.Label, lat = p.Lat, lon = p.Lon, count = p.Count }));
        _ = Map.CoreWebView2.ExecuteScriptAsync($"setPlaces({json})");
    }

    void FocusMap(List<string> keys)
    {
        if (!_mapReady) return;
        _ = Map.CoreWebView2.ExecuteScriptAsync($"focusPlaces({JsonSerializer.Serialize(keys)})");
    }

    /// <summary>Clic sur un lieu de la carte : on le sélectionne dans la liste.</summary>
    void SelectPlace(string key)
    {
        foreach (TreeViewItem country in PlaceTree.Items)
        {
            foreach (TreeViewItem city in country.Items)
            {
                if (city.Tag is Selection.City c && c.Key == key)
                {
                    country.IsExpanded = true;
                    city.IsSelected = true;
                    city.BringIntoView();
                    return;
                }
            }
        }
    }
}
