using System.Text;
using PhotoTri.Core.Devices;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;
using PhotoTri.Core.Transfer;

namespace PhotoTri.Tests;

public sealed class TransferTests : IDisposable
{
    readonly string _work = Path.Combine(Path.GetTempPath(), "phototri-tests", Guid.NewGuid().ToString("N"));
    readonly string _phone;
    readonly string _library;

    public TransferTests()
    {
        _phone = Directory.CreateDirectory(Path.Combine(_work, "phone")).FullName;
        _library = Directory.CreateDirectory(Path.Combine(_work, "library")).FullName;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_work, recursive: true);
    }

    string PhoneFile(string relative, byte[] content, DateTime? modified = null)
    {
        var path = Path.Combine(_phone, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        if (modified is { } m) File.SetLastWriteTime(path, m);
        return path;
    }

    static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    (TransferPlan Plan, TransferReport Report) Run(IPhoneSource phone, bool delete, MediaFilter? filter = null)
    {
        using var index = new LibraryIndex(_library);
        var engine = new TransferEngine(index);
        var plan = engine.Plan(phone, filter ?? new MediaFilter());
        return (plan, engine.Execute(phone, plan, delete));
    }

    [Fact]
    public void Sorts_files_by_date_taken()
    {
        // L'EXIF prime sur le nom du fichier.
        PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", TestImages.JpegWithExifDate("2023:07:14 18:30:00"));
        PhoneFile(@"Android\media\com.whatsapp\WhatsApp\Media\WhatsApp Images\IMG-20250102-WA0001.jpg", Bytes("whatsapp"));
        PhoneFile(@"Pictures\Screenshots\Screenshot_20261024-143012.png", Bytes("screenshot"));
        PhoneFile(@"Pictures\Snapseed\retouche.jpg", Bytes("no date"), modified: new DateTime(2022, 11, 5, 9, 0, 0));

        var (plan, report) = Run(new FolderPhoneSource(_phone, "phone-1", "Test"), delete: false);

        Assert.Equal(4, plan.Items.Count);
        Assert.Equal(4, report.Copied);
        Assert.Empty(report.Errors);
        Assert.True(File.Exists(Path.Combine(_library, "2023", "2023-07 Juillet", "IMG_20240315_101500.jpg")));
        Assert.True(File.Exists(Path.Combine(_library, "2025", "2025-01 Janvier", "IMG-20250102-WA0001.jpg")));
        Assert.True(File.Exists(Path.Combine(_library, "2026", "2026-10 Octobre", "Screenshot_20261024-143012.png")));
        Assert.True(File.Exists(Path.Combine(_library, "2022", "2022-11 Novembre", "retouche.jpg")));
        Assert.Equal(new DateTime(2023, 7, 14, 18, 30, 0),
            File.GetLastWriteTime(Path.Combine(_library, "2023", "2023-07 Juillet", "IMG_20240315_101500.jpg")));
    }

    [Fact]
    public void Keeps_files_on_phone_when_asked()
    {
        var file = PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", Bytes("a"));
        Run(new FolderPhoneSource(_phone, "phone-1", "Test"), delete: false);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Deletes_verified_copies_from_phone()
    {
        var a = PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", Bytes("a"));
        var b = PhoneFile(@"DCIM\Camera\VID_20240316_101500.mp4", Bytes("b"));

        var (_, report) = Run(new FolderPhoneSource(_phone, "phone-1", "Test"), delete: true);

        Assert.Equal(2, report.DeletedFromPhone);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(File.Exists(Path.Combine(_library, "2024", "2024-03 Mars", "VID_20240316_101500.mp4")));
    }

    [Fact]
    public void Skips_files_already_transferred()
    {
        PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", Bytes("a"));
        var phone = new FolderPhoneSource(_phone, "phone-1", "Test");
        Run(phone, delete: false);

        PhoneFile(@"DCIM\Camera\IMG_20240316_101500.jpg", Bytes("b"));
        var (plan, report) = Run(phone, delete: false);

        Assert.Single(plan.Items);
        Assert.Equal(1, plan.AlreadyImported);
        Assert.Equal(1, report.Copied);
    }

    [Fact]
    public void Same_content_from_another_phone_is_not_copied_twice()
    {
        PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", Bytes("same"));
        Run(new FolderPhoneSource(_phone, "phone-1", "Paul"), delete: false);

        var (_, report) = Run(new FolderPhoneSource(_phone, "phone-2", "Marie"), delete: false);

        Assert.Equal(0, report.Copied);
        Assert.Equal(1, report.Duplicates);
        Assert.Single(Directory.GetFiles(Path.Combine(_library, "2024", "2024-03 Mars")));
    }

    [Fact]
    public void Never_overwrites_a_different_file_with_the_same_name()
    {
        PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", Bytes("first"));
        PhoneFile(@"Pictures\Edited\IMG_20240315_101500.jpg", Bytes("second"));

        var (_, report) = Run(new FolderPhoneSource(_phone, "phone-1", "Test"), delete: false);

        Assert.Equal(2, report.Copied);
        var folder = Path.Combine(_library, "2024", "2024-03 Mars");
        Assert.Equal("first", File.ReadAllText(Path.Combine(folder, "IMG_20240315_101500.jpg")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(folder, "IMG_20240315_101500 (2).jpg")));
    }

    [Fact]
    public void Respects_file_type_filter()
    {
        PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", Bytes("photo"));
        PhoneFile(@"DCIM\Screenshots\Screenshot_20240315-101500.png", Bytes("screen"));
        PhoneFile(@"DCIM\Camera\VID_20240315_101500.mp4", Bytes("video"));

        var (plan, _) = Run(new FolderPhoneSource(_phone, "phone-1", "Test"), delete: false,
            new MediaFilter(Videos: false, Screenshots: false));

        Assert.Equal("IMG_20240315_101500.jpg", Assert.Single(plan.Items).Item.Name);
    }

    [Fact]
    public void Reports_analysis_progress()
    {
        PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", Bytes("a"));
        PhoneFile(@"DCIM\Camera\IMG_20240316_101500.jpg", Bytes("b"));
        var reports = new List<PlanProgress>();
        using var index = new LibraryIndex(_library);

        new TransferEngine(index).Plan(new FolderPhoneSource(_phone, "phone-1", "Test"), new MediaFilter(),
            new SynchronousProgress<PlanProgress>(reports.Add));

        Assert.Equal(new PlanProgress(2, 2, ""), reports[^1]);
    }

    sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public void Does_not_delete_from_phone_when_copy_fails()
    {
        var good = PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", Bytes("good"));
        var bad = PhoneFile(@"DCIM\Camera\IMG_20240316_101500.jpg", Bytes("bad"));
        var phone = new FlakyPhone(new FolderPhoneSource(_phone, "phone-1", "Test"), failOn: "IMG_20240316_101500.jpg");

        var (_, report) = Run(phone, delete: true);

        Assert.Equal(1, report.Copied);
        Assert.Single(report.Errors);
        Assert.False(File.Exists(good));
        Assert.True(File.Exists(bad));
        Assert.Empty(Directory.GetFiles(Path.Combine(_library, LibraryLayout.DataFolderName, "incoming")));
    }

    [Fact]
    public void Refuses_transfer_when_destination_is_missing()
    {
        PhoneFile(@"DCIM\Camera\IMG_20240315_101500.jpg", Bytes("a"));
        var phone = new FolderPhoneSource(_phone, "phone-1", "Test");
        using var index = new LibraryIndex(_library);
        var engine = new TransferEngine(index);
        var plan = engine.Plan(phone, new MediaFilter());
        index.Dispose();
        Directory.Delete(_library, recursive: true);

        Assert.Throws<TransferException>(() => engine.Execute(phone, plan, deleteFromPhone: true));
        Assert.True(File.Exists(Path.Combine(_phone, @"DCIM\Camera\IMG_20240315_101500.jpg")));
    }

    /// <summary>Téléphone dont la lecture d'un fichier échoue en cours de copie.</summary>
    sealed class FlakyPhone(IPhoneSource inner, string failOn) : IPhoneSource
    {
        public string DeviceId => inner.DeviceId;
        public string Name => inner.Name;
        public IEnumerable<PhoneItem> ListMedia(CancellationToken ct) => inner.ListMedia(ct);
        public void Delete(PhoneItem item) => inner.Delete(item);
        public void Dispose() => inner.Dispose();

        public void CopyTo(PhoneItem item, Stream destination)
        {
            if (item.Name == failOn)
            {
                destination.Write(Bytes("ba"));
                throw new IOException("Connexion USB interrompue");
            }
            inner.CopyTo(item, destination);
        }
    }
}
