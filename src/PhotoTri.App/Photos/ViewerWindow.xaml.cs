using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using PhotoTri.App.Thumbnails;
using PhotoTri.Core.Media;

namespace PhotoTri.App.Photos;

/// <summary>Visionneuse plein écran : photos (dans le bon sens) et vidéos, avec ← / → pour naviguer.</summary>
public partial class ViewerWindow : Window
{
    readonly IReadOnlyList<PhotoTile> _tiles;
    readonly DispatcherTimer _videoClock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    int _index;
    int _loadVersion;
    bool _seeking;
    bool _playing;

    internal ViewerWindow(IReadOnlyList<PhotoTile> tiles, int index)
    {
        InitializeComponent();
        _chromeTimer.Tick += (_, _) => HideChrome();
        Loaded += (_, _) => ShowChrome();
        _tiles = tiles;
        _index = Math.Max(0, index);
        _videoClock.Tick += (_, _) => UpdateVideoPosition();
        Loaded += (_, _) => ShowAt(_index);
        Closed += (_, _) =>
        {
            _videoClock.Stop();
            Video.Close();
        };
    }

    internal PhotoTile? CurrentTile => _index < _tiles.Count ? _tiles[_index] : null;

    async void ShowAt(int index)
    {
        if (_tiles.Count == 0) return;
        _index = Math.Clamp(index, 0, _tiles.Count - 1);
        var tile = _tiles[_index];
        var version = ++_loadVersion;

        TitleText.Text = TitleFor(tile);
        var place = tile.Entry.Address ?? (tile.Entry.Place is { } p ? $"{p.City}, {p.Country}" : null);
        var people = tile.Entry.People is { Count: > 0 } names ? "avec " + string.Join(", ", names) : null;
        DetailText.Text = string.Join(" · ", new[]
        {
            tile.FileName, people, place, MediaClassifier.Label(tile.Entry.Origin), $"{_index + 1:N0} / {_tiles.Count:N0}",
        }.Where(s => s is not null));
        PreviousButton.Visibility = _index > 0 ? Visibility.Visible : Visibility.Hidden;
        NextButton.Visibility = _index < _tiles.Count - 1 ? Visibility.Visible : Visibility.Hidden;

        StopVideo();
        Message.Text = "";
        // La miniature s'affiche tout de suite, en attendant l'image complète.
        Picture.Source = tile.Thumbnail;
        // Fondu à l'arrivée de chaque photo.
        Picture.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(220)));
        Picture.Visibility = Visibility.Visible;

        if (tile.FullPath is null || !File.Exists(tile.FullPath))
        {
            Message.Text = "Le fichier n'est pas accessible. Le disque de la bibliothèque est-il branché ?";
            return;
        }

        if (tile.IsVideo)
        {
            // Le lecteur de Windows ignore l'angle enregistré par le téléphone : on redresse nous-mêmes.
            var rotation = await Task.Run(() => VideoRotation.Read(tile.FullPath));
            if (version != _loadVersion) return;
            Video.LayoutTransform = rotation == 0 ? Transform.Identity : new RotateTransform(rotation);
            Picture.Visibility = Visibility.Collapsed;
            Video.Visibility = Visibility.Visible;
            Video.Source = new Uri(tile.FullPath);
            Video.Play();
            _playing = true;
            ShowPlaying(true);
            VideoControls.Visibility = Visibility.Visible;
            _videoClock.Start();
            return;
        }

        var maxSize = (int)Math.Max(SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight) * 2;
        var image = await Task.Run(() => LoadFullImage(tile.FullPath, maxSize));
        if (version != _loadVersion) return; // l'utilisateur est déjà passé à une autre photo
        if (image is not null)
            Picture.Source = image;
        else if (Picture.Source is null)
            Message.Text = "Cette photo ne peut pas être affichée. Pour les photos HEIC, installez « Extensions d'image HEIF » depuis le Microsoft Store.";
    }

    static string TitleFor(PhotoTile tile) =>
        PhotoTri.Core.Viewer.Timeline.DayTitle(DateOnly.FromDateTime(tile.Entry.TakenAt)) + $" à {tile.Entry.TakenAt:HH:mm}";

    /// <summary>Décode la photo à la taille de l'écran et la redresse selon l'orientation EXIF.</summary>
    static BitmapSource? LoadFullImage(string path, int maxSize)
    {
        try
        {
            var rotation = 0;
            using (var stream = File.OpenRead(path))
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                rotation = OrientationToRotation(frame.Metadata as BitmapMetadata);
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            BitmapSource result = bitmap;
            if (bitmap.PixelWidth > maxSize || bitmap.PixelHeight > maxSize)
            {
                var scale = (double)maxSize / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
                result = new TransformedBitmap(result, new ScaleTransform(scale, scale));
            }
            if (rotation != 0) result = new TransformedBitmap(result, new RotateTransform(rotation));
            result.Freeze();
            return result;
        }
        catch (Exception)
        {
            // Format non pris en charge par Windows (HEIC sans extension…) : on tente la miniature de Windows.
            return LargeShellThumbnail(path);
        }
    }

    static int OrientationToRotation(BitmapMetadata? metadata)
    {
        try
        {
            return metadata?.GetQuery("System.Photo.Orientation") switch
            {
                (ushort)3 => 180,
                (ushort)6 => 90,
                (ushort)8 => 270,
                _ => 0,
            };
        }
        catch (Exception)
        {
            return 0; // format sans métadonnées d'orientation
        }
    }

    static BitmapSource? LargeShellThumbnail(string path)
    {
        BitmapSource? result = null;
        var thread = new Thread(() => result = ShellThumbnail.Get(path, 1600));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    // ---- Vidéo ----

    void StopVideo()
    {
        _videoClock.Stop();
        Video.Stop();
        Video.Source = null;
        Video.Visibility = Visibility.Collapsed;
        VideoControls.Visibility = Visibility.Collapsed;
    }

    void Video_MediaOpened(object sender, RoutedEventArgs e)
    {
        Position.Maximum = Video.NaturalDuration.HasTimeSpan ? Video.NaturalDuration.TimeSpan.TotalSeconds : 0;
        UpdateVideoPosition();
    }

    void Video_MediaEnded(object sender, RoutedEventArgs e)
    {
        Video.Pause();
        Video.Position = TimeSpan.Zero;
        _playing = false;
        ShowPlaying(false);
        UpdateVideoPosition();
    }

    void Video_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        StopVideo();
        Picture.Visibility = Visibility.Visible;
        Message.Text = "Cette vidéo ne peut pas être lue ici. Utilisez « Ouvrir avec l'application par défaut ». " +
                       "Pour les vidéos HEVC, installez « Extensions vidéo HEVC » depuis le Microsoft Store.";
    }

    void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (Video.Source is null) return;
        if (_playing) Video.Pause(); else Video.Play();
        _playing = !_playing;
        ShowPlaying(_playing);
    }

    void UpdateVideoPosition()
    {
        if (!_seeking) Position.Value = Video.Position.TotalSeconds;
        var total = Video.NaturalDuration.HasTimeSpan ? Video.NaturalDuration.TimeSpan : TimeSpan.Zero;
        TimeText.Text = $"{Format(Video.Position)} / {Format(total)}";
    }

    static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    void Position_DragStarted(object sender, DragStartedEventArgs e) => _seeking = true;

    void Position_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _seeking = false;
        Video.Position = TimeSpan.FromSeconds(Position.Value);
    }

    void Position_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_seeking) Video.Position = TimeSpan.FromSeconds(Position.Value);
    }

    void ShowPlaying(bool playing)
    {
        PlayPauseButton.Content = playing ? "Pause" : "Lecture";
        PlayPauseButton.Icon = new SymbolIcon(playing ? SymbolRegular.Pause24 : SymbolRegular.Play24);
    }

    // ---- Barres en surimpression : visibles quand la souris bouge, cachées après un moment d'inactivité ----

    readonly DispatcherTimer _chromeTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    bool _chromeVisible = true;

    void Window_MouseMove(object sender, MouseEventArgs e) => ShowChrome();

    void ShowChrome()
    {
        _chromeTimer.Stop();
        _chromeTimer.Start();
        Cursor = null;
        if (_chromeVisible) return;
        _chromeVisible = true;
        foreach (var element in ChromeElements) element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    void HideChrome()
    {
        _chromeTimer.Stop();
        // On garde les barres tant que la souris est dessus.
        if (ChromeElements.Any(e => e.IsMouseOver)) return;
        _chromeVisible = false;
        Cursor = Cursors.None;
        foreach (var element in ChromeElements) element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)));
    }

    IEnumerable<UIElement> ChromeElements => new UIElement[] { TopBar, PreviousButton, NextButton, VideoControls };

    // ---- Navigation et actions ----

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: ShowAt(_index - 1); break;
            case Key.Right: ShowAt(_index + 1); break;
            case Key.Home: ShowAt(0); break;
            case Key.End: ShowAt(_tiles.Count - 1); break;
            case Key.Space: TogglePlay(); break;
            case Key.Escape: Close(); break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control: CopyCurrent(); break;
            default: return;
        }
        e.Handled = true;
    }

    void Previous_Click(object sender, RoutedEventArgs e) => ShowAt(_index - 1);

    void Next_Click(object sender, RoutedEventArgs e) => ShowAt(_index + 1);

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    void OpenDefault_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTile?.FullPath is { } path && File.Exists(path))
        {
            Video.Pause();
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    void Copy_Click(object sender, RoutedEventArgs e) => CopyCurrent();

    void CopyCurrent()
    {
        if (CurrentTile?.FullPath is not { } path || !File.Exists(path)) return;
        try
        {
            // Pour une photo, l'image affichée est aussi copiée (collage dans Word, Paint…).
            MediaSharing.Copy(path, CurrentTile.IsVideo ? null : Picture.Source as BitmapSource);
            CopyButton.Content = "Copié";
            CopyButton.Icon = new SymbolIcon(SymbolRegular.Checkmark24);
            var restore = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            restore.Tick += (_, _) =>
            {
                restore.Stop();
                CopyButton.Content = "Copier";
                CopyButton.Icon = new SymbolIcon(SymbolRegular.Copy24);
            };
            restore.Start();
        }
        catch (Exception ex)
        {
            // Presse-papiers occupé par une autre application.
            MessageBox.Show(this, "La copie n'a pas pu se faire : " + ex.Message, "PhotoTri", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Share_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTile?.FullPath is not { } path || !File.Exists(path)) return;
        Video.Pause();
        _playing = false;
        ShowPlaying(false);
        try
        {
            MediaSharing.Share(this, path, TitleText.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Le partage n'est pas disponible : " + ex.Message, "PhotoTri", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ShowInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentTile?.FullPath is { } path && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
