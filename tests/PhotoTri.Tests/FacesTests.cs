using PhotoTri.Core.Devices;
using PhotoTri.Core.Faces;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using PhotoTri.Core.Transfer;

namespace PhotoTri.Tests;

public sealed class FacesTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "phototri-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_work)) Directory.Delete(_work, recursive: true);
    }

    // Empreintes de test : trois « personnes » A, B, C très différentes, et des variations autour de chacune.
    static readonly Random Rng = new(42);
    static readonly float[] A = Unit(0), B = Unit(1), C = Unit(2);

    static float[] Unit(int axis)
    {
        var v = new float[128];
        v[axis] = 1;
        return v;
    }

    static float[] Near(float[] person, float noise = 0.25f) =>
        FaceEngine.Normalize(person.Select(x => x + (float)(Rng.NextDouble() - 0.5) * noise).ToArray());

    static FaceVector Face(long id, float[] e, long? person = null, bool byUser = false, params long[] rejected) =>
        new(id, e, person, byUser, rejected.ToHashSet());

    [Fact]
    public void Similar_faces_end_up_in_the_same_group()
    {
        var faces = new[]
        {
            Face(1, Near(A)), Face(2, Near(B)), Face(3, Near(A)), Face(4, Near(A)), Face(5, Near(B)), Face(6, Near(C)),
        };
        var groups = FaceClustering.Group(faces);
        Assert.Equal(new long[] { 1, 3, 4 }, groups[0].Select(f => f.Id));
        Assert.Equal(new long[] { 2, 5 }, groups[1].Select(f => f.Id));
        Assert.Equal(new long[] { 6 }, groups[2].Select(f => f.Id));
    }

    [Fact]
    public void Named_and_ignored_faces_are_not_grouped()
    {
        var faces = new[] { Face(1, Near(A), person: 9, byUser: true), Face(2, Near(A)) with { Ignored = true }, Face(3, Near(A)) };
        Assert.Equal(new long[] { 3 }, Assert.Single(FaceClustering.Group(faces)).Select(f => f.Id));
    }

    [Fact]
    public void Faces_resembling_a_named_person_are_assigned_automatically()
    {
        var faces = new[]
        {
            Face(1, Near(A), person: 10, byUser: true),
            Face(2, Near(B), person: 20, byUser: true),
            Face(3, Near(A)),
            Face(4, Near(C)),
            Face(5, Near(A), person: 20), // attribuée automatiquement à tort : corrigée
        };
        var changes = FaceClustering.AutoAssign(faces).ToDictionary(c => c.FaceId, c => c.PersonId);
        Assert.Equal(10, changes[3]);
        Assert.False(changes.ContainsKey(4)); // ne ressemble à personne
        Assert.Equal(10, changes[5]);
        Assert.False(changes.ContainsKey(1)); // les choix de l'utilisateur ne changent jamais
    }

    [Fact]
    public void A_rejected_person_is_never_assigned_again()
    {
        var faces = new[] { Face(1, Near(A), person: 10, byUser: true), Face(2, Near(A), null, false, 10) };
        Assert.Empty(FaceClustering.AutoAssign(faces));
    }

    [Fact]
    public void Similarity_transform_maps_landmarks_exactly_when_possible()
    {
        // Points tournés de 30°, agrandis 2 fois et décalés : la transformation retrouvée doit les ramener exactement.
        (float X, float Y)[] reference = { (38, 51), (73, 51), (56, 71), (41, 92), (70, 92) };
        var angle = MathF.PI / 6;
        var moved = reference.Select(p => (
            X: 2 * (p.X * MathF.Cos(angle) - p.Y * MathF.Sin(angle)) + 300,
            Y: 2 * (p.X * MathF.Sin(angle) + p.Y * MathF.Cos(angle)) + 120)).ToArray();

        var (a, b, tx, ty) = FaceEngine.SimilarityTransform(moved, reference);
        for (var i = 0; i < reference.Length; i++)
        {
            Assert.Equal(reference[i].X, a * moved[i].X - b * moved[i].Y + tx, precision: 2);
            Assert.Equal(reference[i].Y, b * moved[i].X + a * moved[i].Y + ty, precision: 2);
        }
    }

    [Fact]
    public void Models_load_and_find_no_face_in_a_plain_image()
    {
        using var engine = new FaceEngine();
        var image = new RgbImage(800, 600, Enumerable.Repeat((byte)128, 800 * 600 * 3).ToArray());
        Assert.Empty(engine.Detect(image));
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

    // Enregistre un visage par photo, avec les empreintes données, et renvoie les identifiants des visages.
    static List<long> SaveOneFacePerPhoto(LibraryIndex index, params float[][] embeddings)
    {
        var photos = index.PhotosWithoutFaces(100).OrderBy(p => p.RelativePath).ToList();
        for (var i = 0; i < photos.Count; i++)
            index.SaveFaces(photos[i].Id, i < embeddings.Length ? new[] { new FaceToSave(0, .1f, .1f, .2f, .2f, .9f, embeddings[i]) } : Array.Empty<FaceToSave>());
        return index.LoadFaceVectors().Select(f => f.Id).ToList();
    }

    [Fact]
    public void Naming_a_group_tags_the_photos_and_recognises_new_faces()
    {
        using var index = LibraryWithPhotos(4);
        var ids = SaveOneFacePerPhoto(index, Near(A), Near(A), Near(B), Near(A));
        Assert.Equal(0, index.CountPhotosWithoutFaces());

        var db = LibraryCatalog.DatabasePath(index.LibraryRoot);
        var group = PeopleCatalog.Load(db).Groups[0];
        Assert.Equal(3, group.Faces.Count);

        // L'utilisateur nomme deux des trois visages : le troisième est reconnu automatiquement.
        index.NameFaces(group.Faces.Take(2).Select(f => f.Id), "Léa");
        var people = PeopleCatalog.Load(db);
        var lea = Assert.Single(people.Persons);
        Assert.Equal("Léa", lea.Name);
        Assert.Equal(3, lea.Faces.Count);
        Assert.Empty(people.Groups); // le visage de B est seul : pas de groupe à proposer
        Assert.Equal(1, people.LoneFaces);

        var catalog = LibraryCatalog.Load(db);
        Assert.Equal(3, catalog.Entries.Count(e => e.People?.Contains("Léa") == true));
    }

    [Fact]
    public void Rejecting_a_face_removes_it_for_good()
    {
        using var index = LibraryWithPhotos(3);
        var ids = SaveOneFacePerPhoto(index, Near(A), Near(A), Near(A));
        index.NameFaces(ids.Take(2), "Léa");
        Assert.Equal(3, PeopleCatalog.Load(LibraryCatalog.DatabasePath(index.LibraryRoot)).Persons[0].Faces.Count);

        index.RejectFace(ids[2]);
        index.ApplyAutoAssignments();
        Assert.Equal(2, PeopleCatalog.Load(LibraryCatalog.DatabasePath(index.LibraryRoot)).Persons[0].Faces.Count);
    }

    [Fact]
    public void Renaming_to_an_existing_name_merges_the_two_people()
    {
        using var index = LibraryWithPhotos(2);
        var ids = SaveOneFacePerPhoto(index, Near(A), Near(B));
        index.NameFaces(new[] { ids[0] }, "Léa");
        var other = index.NameFaces(new[] { ids[1] }, "Lea (petite)");

        index.RenamePerson(other, "léa"); // casse différente : même personne

        var person = Assert.Single(PeopleCatalog.Load(LibraryCatalog.DatabasePath(index.LibraryRoot)).Persons);
        Assert.Equal("Léa", person.Name);
        Assert.Equal(2, person.Faces.Count);
    }

    [Fact]
    public void Ignored_faces_are_no_longer_proposed()
    {
        using var index = LibraryWithPhotos(2);
        var ids = SaveOneFacePerPhoto(index, Near(C), Near(C));
        var db = LibraryCatalog.DatabasePath(index.LibraryRoot);
        Assert.Single(PeopleCatalog.Load(db).Groups);

        index.IgnoreFaces(ids);
        Assert.Empty(PeopleCatalog.Load(db).Groups);
    }
}
