using PhotoTri.Core.Devices;
using PhotoTri.Core.Documents;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using PhotoTri.Core.Transfer;
using PhotoTri.Core.Viewer;

namespace PhotoTri.Tests;

public sealed class DocumentIndexTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "phototri-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    LibraryIndex LibraryWithPhotos(int count)
    {
        var phone = Path.Combine(_work, "phone", "DCIM", "Camera");
        Directory.CreateDirectory(phone);
        for (var i = 0; i < count; i++) File.WriteAllText(Path.Combine(phone, $"IMG_2024010{i + 1}_100000.jpg"), $"photo {i}");
        var index = new LibraryIndex(Directory.CreateDirectory(Path.Combine(_work, "library")).FullName);
        var source = new FolderPhoneSource(Path.Combine(_work, "phone"), "p1", "Test");
        var engine = new TransferEngine(index);
        engine.Execute(source, engine.Plan(source, new MediaFilter()), deleteFromPhone: false);
        return index;
    }

    [Fact]
    public void Documents_are_stored_searchable_and_correctable()
    {
        using var index = LibraryWithPhotos(3);
        var photos = index.PhotosWithoutDocumentCheck(10).OrderBy(p => p.RelativePath).ToList();
        Assert.Equal(3, index.CountPhotosWithoutDocumentCheck());
        index.SaveDocument(photos[0].Id, DocumentKind.DrivingLicense, "PERMIS DE CONDUIRE\nDUPONT");
        index.SaveDocument(photos[1].Id, DocumentKind.Receipt, "BOULANGERIE\nTOTAL TTC 4,20");
        index.SaveDocument(photos[2].Id, null, "STOP");
        Assert.Equal(0, index.CountPhotosWithoutDocumentCheck());

        var db = LibraryCatalog.DatabasePath(index.LibraryRoot);
        var entries = LibraryCatalog.Load(db).Entries.OrderBy(e => e.RelativePath).ToList();
        Assert.Equal(DocumentKind.DrivingLicense, entries[0].Document);
        Assert.Null(entries[2].Document);
        Assert.Null(entries[2].DocumentText); // le texte d'une photo ordinaire n'est pas gardé

        // Recherche dans le texte lu et dans le type de document.
        Assert.True(LibrarySearch.Matcher("boulangerie")(entries[1]));
        Assert.True(LibrarySearch.Matcher("permis")(entries[0]));
        Assert.Equal(2, entries.Count(new ViewerFilter(Documents: true).Accepts));
        Assert.Single(entries.Where(new ViewerFilter(Documents: false).Accepts));

        // L'utilisateur corrige : le ticket n'est pas un document, la photo ordinaire est une carte Vitale.
        index.SetDocumentKindByUser(new[] { entries[1].Id }, null);
        index.SetDocumentKindByUser(new[] { entries[2].Id }, DocumentKind.HealthCard);
        entries = LibraryCatalog.Load(db).Entries.OrderBy(e => e.RelativePath).ToList();
        Assert.Null(entries[1].Document);
        Assert.Equal(DocumentKind.HealthCard, entries[2].Document);
    }

    [Fact]
    public void Ordinary_screenshots_are_not_documents_but_screenshots_of_official_papers_are()
    {
        var page = Enumerable.Repeat("Une ligne de texte bien remplie dans une application de messagerie", 8).ToArray();
        Assert.Equal(DocumentKind.Document, DocumentIndexer.Classify(page, MediaOrigin.Camera));
        Assert.Null(DocumentIndexer.Classify(page, MediaOrigin.Screenshot));
        Assert.Equal(DocumentKind.DrivingLicense, DocumentIndexer.Classify(new[] { "PERMIS DE CONDUIRE" }, MediaOrigin.Screenshot));
    }
}
