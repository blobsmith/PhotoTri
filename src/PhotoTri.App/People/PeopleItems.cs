using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoTri.Core.Faces;

namespace PhotoTri.App.People;

/// <summary>Lignes de la liste de gauche de l'onglet Personnes.</summary>
abstract record PeopleListItem;

sealed record HeaderItem(string Text) : PeopleListItem;

sealed record PersonItem(Person Person) : PeopleListItem
{
    public string Name => Person.Name;
    public string Count => $"{Person.Faces.Count:N0} photo(s)";
    public ImageSource? Image => FaceImages.Load(Person.Faces[0].CropPath);
}

sealed record GroupItem(FaceGroup Group) : PeopleListItem
{
    public string Count => $"{Group.Faces.Count:N0} visages qui se ressemblent";
    public ImageSource? Image => FaceImages.Load(Group.Faces[0].CropPath);
}

/// <summary>Vignette d'un visage dans les panneaux de droite.</summary>
sealed record FaceTile(FaceInfo Face, string? ToolTip)
{
    public ImageSource? Image => FaceImages.Load(Face.CropPath);
}

/// <summary>Vignettes des visages (petits fichiers JPEG gardés sur le PC), gardées en mémoire une fois lues.</summary>
static class FaceImages
{
    static readonly Dictionary<string, ImageSource?> Cache = new();

    public static ImageSource? Load(string path)
    {
        if (Cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? image = null;
        if (File.Exists(path))
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                image = bitmap;
            }
            catch (Exception)
            {
                // Vignette abîmée : emplacement vide.
            }
        }
        // Une vignette manquante sera recherchée à nouveau au prochain affichage.
        if (image is not null) Cache[path] = image;
        return image;
    }
}
