using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using PhotoTri.App.Thumbnails;
using PhotoTri.Core.Library;
using PhotoTri.Core.Media;

namespace PhotoTri.App.Photos;

/// <summary>Une vignette de la chronologie. La miniature est chargée au premier affichage.</summary>
sealed class PhotoTile(CatalogEntry entry, string? libraryRoot, ThumbnailService thumbnails) : INotifyPropertyChanged
{
    BitmapSource? _thumbnail;
    bool _requested;

    public CatalogEntry Entry { get; } = entry;

    /// <summary>Chemin complet du fichier, ou null si la bibliothèque n'est pas accessible.</summary>
    public string? FullPath { get; } = libraryRoot is null ? null : Path.Combine(libraryRoot, entry.RelativePath);

    public bool IsVideo => Entry.Kind == MediaKind.Video;

    public string FileName => Path.GetFileName(Entry.RelativePath);

    // Colonnes de la vue Liste.
    public string DateText => Entry.TakenAt.ToString("dd/MM/yyyy HH:mm");
    public string PlaceText => Entry.Place is { } p ? $"{p.City}, {p.Country}" : "";
    public string PeopleText => Entry.People is { Count: > 0 } names ? string.Join(", ", names) : "";
    public string KindText => (IsVideo ? "Vidéo" : "Photo") + " · " + MediaClassifier.Label(Entry.Origin);
    public string SizeText => Core.Transfer.TransferEngine.FormatSize(Entry.Size);

    public string ToolTip => string.Join("\n", new[]
    {
        FileName,
        Entry.TakenAt.ToString("dd/MM/yyyy HH:mm"),
        Entry.Place is { } p ? $"{p.City}, {p.Country}" : null,
        MediaClassifier.Label(Entry.Origin),
    }.Where(s => s is not null));

    public BitmapSource? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_requested)
            {
                _thumbnail = thumbnails.TryGetCached(Entry.Sha256);
                if (_thumbnail is null)
                {
                    _requested = true;
                    thumbnails.Request(Entry.Sha256, FullPath, image =>
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            _thumbnail = image;
                            PropertyChanged?.Invoke(this, new(nameof(Thumbnail)));
                        }));
                }
            }
            return _thumbnail;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Rangée de vignettes affichée par la chronologie.</summary>
sealed record TileRow(IReadOnlyList<PhotoTile> Tiles);
