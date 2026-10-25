using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoTri.Core.Faces;
using PhotoTri.Core.Library;

namespace PhotoTri.App.People;

/// <summary>
/// Onglet « Personnes » : personnes nommées avec leurs photos, et groupes de visages qui se ressemblent
/// à nommer en une fois. Les corrections (« ce n'est pas elle ») servent aux attributions suivantes.
/// </summary>
public partial class PeopleView : UserControl
{
    /// <summary>Au-delà, le groupe affiche seulement ses premiers visages (les autres seront nommés avec eux).</summary>
    const int MaxFacesShown = 120;

    PeopleCatalog _people = PeopleCatalog.Empty;
    LibraryCatalog _catalog = LibraryCatalog.Empty;
    string? _libraryRoot;
    PeopleListItem? _selected;
    bool _reloading;

    public PeopleView()
    {
        InitializeComponent();
    }

    /// <summary>Exécute une modification de la base (fournie par la fenêtre principale), puis recharge les onglets.</summary>
    public Func<Action<LibraryIndex>, Task>? Modify { get; set; }

    public void SetStatus(string text) => StatusText.Text = text;

    public void Load(PeopleCatalog people, LibraryCatalog catalog, string? libraryRoot)
    {
        _people = people;
        _catalog = catalog;
        _libraryRoot = libraryRoot;

        var items = new List<PeopleListItem>();
        if (people.Persons.Count > 0)
        {
            items.Add(new HeaderItem($"Personnes ({people.Persons.Count:N0})"));
            items.AddRange(people.Persons.Select(p => new PersonItem(p)));
        }
        if (people.Groups.Count > 0)
        {
            items.Add(new HeaderItem($"À nommer ({people.Groups.Count:N0})"));
            items.AddRange(people.Groups.Select(g => new GroupItem(g)));
        }

        _reloading = true;
        PeopleList.ItemsSource = items;
        // On reste sur la même personne (ou le même groupe) après un rechargement.
        PeopleListItem? again = _selected switch
        {
            PersonItem p => items.OfType<PersonItem>().FirstOrDefault(i => i.Person.Id == p.Person.Id),
            GroupItem g => items.OfType<GroupItem>().FirstOrDefault(i => i.Group.Key == g.Group.Key),
            _ => null,
        };
        PeopleList.SelectedItem = again ?? items.FirstOrDefault(i => i is not HeaderItem);
        _reloading = false;
        Show(PeopleList.SelectedItem as PeopleListItem);
    }

    void PeopleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_reloading) return;
        if (PeopleList.SelectedItem is HeaderItem)
        {
            // Les titres de section ne se sélectionnent pas.
            PeopleList.SelectedItem = e.RemovedItems.Count > 0 ? e.RemovedItems[0] : null;
            return;
        }
        Show(PeopleList.SelectedItem as PeopleListItem);
    }

    void Show(PeopleListItem? item)
    {
        _selected = item;
        GroupPanel.Visibility = item is GroupItem ? Visibility.Visible : Visibility.Collapsed;
        PersonPanel.Visibility = item is PersonItem ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _catalog.Entries.Count == 0
            ? "Aucune photo pour l'instant."
            : "Aucun visage à nommer pour l'instant. Les visages des photos sont recherchés en arrière-plan : " +
              "les groupes de visages qui se ressemblent apparaîtront ici.";

        switch (item)
        {
            case GroupItem g:
                ShowGroup(g.Group);
                break;
            case PersonItem p:
                ShowPerson(p.Person);
                break;
        }
    }

    // ---- Groupe à nommer ----

    void ShowGroup(FaceGroup group)
    {
        var shown = Math.Min(group.Faces.Count, MaxFacesShown);
        GroupSubtitle.Text = group.Faces.Count > shown
            ? $"{group.Faces.Count:N0} visages qui se ressemblent (les {shown} premiers sont affichés)."
            : $"{group.Faces.Count:N0} visages qui se ressemblent.";
        GroupFaces.ItemsSource = group.Faces.Take(shown).Select(f => new FaceTile(f, PhotoLabel(f))).ToList();
        NameBox.ItemsSource = _people.Persons.Select(p => p.Name).OrderBy(n => n).ToList();
        NameBox.Text = "";
        NameBox.Focus();
    }

    async void NameGroup_Click(object sender, RoutedEventArgs e) => await NameGroupAsync();

    async void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await NameGroupAsync();
    }

    async Task NameGroupAsync()
    {
        if (_selected is not GroupItem g || Modify is null) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }
        var faceIds = g.Group.Faces.Select(f => f.Id).ToList();
        // Après le nommage, on affiche la personne et ses photos.
        _selected = null;
        await Modify(index =>
        {
            var personId = index.NameFaces(faceIds, name);
            Dispatcher.Invoke(() => _selected = new PersonItem(new Person(personId, name, Array.Empty<FaceInfo>())));
        });
    }

    async void IgnoreGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not GroupItem g || Modify is null) return;
        var faceIds = g.Group.Faces.Select(f => f.Id).ToList();
        await Modify(index => index.IgnoreFaces(faceIds));
    }

    // ---- Personne nommée ----

    void ShowPerson(Person person)
    {
        PersonTitle.Text = person.Name;
        RenameBox.Visibility = Visibility.Collapsed;
        RenameButton.Content = "Renommer…";
        FacesExpander.Header = $"Visages reconnus ({person.Faces.Count:N0})";
        FacesHint.Text = $"Survolez un visage et cliquez sur ✕ s'il ne s'agit pas de {person.Name} : il ne lui sera plus attribué.";
        PersonFaces.ItemsSource = person.Faces.Take(MaxFacesShown)
            .Select(f => new FaceTile(f, PhotoLabel(f) + (f.ByUser ? "" : "\n(reconnu automatiquement)")))
            .ToList();

        var fileIds = person.Faces.Select(f => f.FileId).ToHashSet();
        Timeline.Show(_catalog.Entries.Where(e => fileIds.Contains(e.Id)).ToList(), _libraryRoot);
    }

    async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not PersonItem p) return;
        if (RenameBox.Visibility != Visibility.Visible)
        {
            RenameBox.Text = p.Person.Name;
            RenameBox.Visibility = Visibility.Visible;
            RenameButton.Content = "OK";
            RenameBox.Focus();
            RenameBox.SelectAll();
            return;
        }
        await RenameAsync();
    }

    async void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await RenameAsync();
        else if (e.Key == Key.Escape) Show(_selected);
    }

    async Task RenameAsync()
    {
        if (_selected is not PersonItem p || Modify is null) return;
        var name = RenameBox.Text.Trim();
        if (name.Length == 0 || name == p.Person.Name)
        {
            Show(_selected);
            return;
        }
        var other = _people.Persons.FirstOrDefault(x => x.Id != p.Person.Id && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (other is not null && MessageBox.Show(Window.GetWindow(this),
                $"« {other.Name} » existe déjà. Fusionner « {p.Person.Name} » avec « {other.Name} » ?",
                "Fusionner deux personnes", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        await Modify(index =>
        {
            var id = index.RenamePerson(p.Person.Id, name);
            Dispatcher.Invoke(() => _selected = new PersonItem(new Person(id, name, Array.Empty<FaceInfo>())));
        });
    }

    // ✕ sur un visage : retiré d'un groupe à nommer, ou « ce n'est pas cette personne ».
    async void RemoveFace_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not FaceTile tile || Modify is null) return;
        var faceId = tile.Face.Id;
        if (_selected is PersonItem)
            await Modify(index => index.RejectFace(faceId));
        else if (_selected is GroupItem)
            await Modify(index => index.IgnoreFaces(new[] { faceId }));
    }

    string? PhotoLabel(FaceInfo face)
    {
        var entry = _catalog.Entries.FirstOrDefault(e => e.Id == face.FileId);
        return entry is null ? null : $"{entry.TakenAt:dd/MM/yyyy}" + (entry.Place is { } p ? $" · {p.City}" : "");
    }
}
