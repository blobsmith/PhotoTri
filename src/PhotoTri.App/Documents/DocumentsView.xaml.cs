using System.Windows;
using System.Windows.Controls;
using PhotoTri.Core.Documents;
using PhotoTri.Core.Library;
using Wpf.Ui.Controls;

namespace PhotoTri.App.Documents;

/// <summary>Une catégorie de la liste de gauche (null : tous les documents).</summary>
sealed record CategoryItem(DocumentKind? Kind, string Label, SymbolRegular Icon, int Count);

/// <summary>
/// Page « Documents » : pièces d'identité, permis, factures et feuilles reconnus en lisant le texte des photos,
/// avec la correction des erreurs de classement.
/// </summary>
public partial class DocumentsView : UserControl
{
    static readonly (DocumentKind Kind, SymbolRegular Icon)[] Kinds =
    {
        (DocumentKind.IdentityCard, SymbolRegular.ContactCard24),
        (DocumentKind.Passport, SymbolRegular.Globe24),
        (DocumentKind.DrivingLicense, SymbolRegular.VehicleCar24),
        (DocumentKind.HealthCard, SymbolRegular.HeartPulse24),
        (DocumentKind.Receipt, SymbolRegular.Receipt24),
        (DocumentKind.Document, SymbolRegular.DocumentText24),
    };

    LibraryCatalog _catalog = LibraryCatalog.Empty;
    string? _libraryRoot;
    DocumentKind? _selected;
    bool _reloading;

    public DocumentsView()
    {
        InitializeComponent();
        Browser.AddListAction("Ce n'est pas un document", SymbolRegular.DocumentDismiss24,
            "Retirer les photos choisies des documents", entries => Correct(entries, null));
        Browser.AddListMenuAction("Classer comme…", SymbolRegular.Tag24, "Changer la catégorie des photos choisies",
            Kinds.Select(k => (DocumentClassifier.Label(k.Kind), (Action<IReadOnlyList<CatalogEntry>>)(entries => Correct(entries, k.Kind)))));
        // À la première ouverture de la page, on part du document le plus récent.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible || _shown) return;
            _shown = true;
            Dispatcher.BeginInvoke(Browser.ScrollToTop, System.Windows.Threading.DispatcherPriority.Loaded);
        };
    }

    bool _shown;

    /// <summary>Exécute une modification de la base (fournie par la fenêtre principale), puis recharge les pages.</summary>
    public Func<Action<LibraryIndex>, Task>? Modify { get; set; }

    /// <summary>Suppression demandée depuis la vue Liste.</summary>
    public event Action<IReadOnlyList<CatalogEntry>>? DeleteRequested
    {
        add => Browser.DeleteRequested += value;
        remove => Browser.DeleteRequested -= value;
    }

    public void SetStatus(string text) => StatusText.Text = text;

    public void Load(LibraryCatalog catalog, string? libraryRoot)
    {
        _catalog = catalog;
        _libraryRoot = libraryRoot;
        var documents = catalog.Entries.Where(e => e.Document is not null).ToList();

        var items = new List<CategoryItem> { new(null, "Tous les documents", SymbolRegular.DocumentMultiple24, documents.Count) };
        items.AddRange(Kinds.Select(k => new CategoryItem(k.Kind, Plural(k.Kind), k.Icon, documents.Count(d => d.Document == k.Kind))));

        _reloading = true;
        Categories.ItemsSource = items;
        Categories.SelectedItem = items.FirstOrDefault(i => i.Kind == _selected) ?? items[0];
        _reloading = false;
        Show();
    }

    static string Plural(DocumentKind kind) => kind switch
    {
        DocumentKind.IdentityCard => "Pièces d'identité",
        DocumentKind.Passport => "Passeports",
        DocumentKind.DrivingLicense => "Permis de conduire",
        DocumentKind.HealthCard => "Cartes Vitale",
        DocumentKind.Receipt => "Factures et tickets",
        _ => "Autres documents",
    };

    void Categories_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reloading || Categories.SelectedItem is not CategoryItem item) return;
        _selected = item.Kind;
        Show();
        Browser.ScrollToTop();
    }

    void Show()
    {
        var item = Categories.SelectedItem as CategoryItem;
        var entries = _catalog.Entries.Where(e => e.Document is not null && (_selected is null || e.Document == _selected)).ToList();
        TitleText.Text = item?.Label ?? "Tous les documents";
        Browser.Show(entries, _libraryRoot);
    }

    async void Correct(IReadOnlyList<CatalogEntry> entries, DocumentKind? kind)
    {
        if (entries.Count == 0 || Modify is null) return;
        var ids = entries.Select(e => e.Id).ToList();
        await Modify(index => index.SetDocumentKindByUser(ids, kind));
    }
}
