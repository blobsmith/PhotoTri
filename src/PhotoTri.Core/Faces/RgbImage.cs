using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoTri.Core.Faces;

/// <summary>Image en mémoire, 3 octets par pixel dans l'ordre bleu, vert, rouge, ligne par ligne.</summary>
public sealed class RgbImage(int width, int height, byte[] bgr)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Bgr { get; } = bgr;

    /// <summary>
    /// Charge une photo redressée selon son orientation EXIF, réduite pour que son plus grand côté
    /// ne dépasse pas <paramref name="maxSide"/> (le décodeur JPEG réduit très vite pendant la lecture).
    /// </summary>
    public static RgbImage Load(string path, int maxSide)
    {
        int rotation;
        int width, height;
        using (var stream = File.OpenRead(path))
        {
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            rotation = Orientation(frame.Metadata as BitmapMetadata);
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (Math.Max(width, height) > maxSide)
        {
            if (width >= height) bitmap.DecodePixelWidth = maxSide;
            else bitmap.DecodePixelHeight = maxSide;
        }
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();

        BitmapSource source = bitmap;
        if (rotation != 0) source = new TransformedBitmap(source, new RotateTransform(rotation));
        source = new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var stride = source.PixelWidth * 3;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return new RgbImage(source.PixelWidth, source.PixelHeight, pixels);
    }

    static int Orientation(BitmapMetadata? metadata)
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
            return 0; // format sans orientation (PNG…)
        }
    }

    /// <summary>Valeur d'un canal (0 = bleu, 1 = vert, 2 = rouge) en un point quelconque, par interpolation bilinéaire.</summary>
    public float Sample(float x, float y, int channel)
    {
        if (x < 0 || y < 0 || x > Width - 1 || y > Height - 1) return 0;
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = Math.Min(x0 + 1, Width - 1);
        var y1 = Math.Min(y0 + 1, Height - 1);
        var fx = x - x0;
        var fy = y - y0;
        float At(int px, int py) => Bgr[(py * Width + px) * 3 + channel];
        return (At(x0, y0) * (1 - fx) + At(x1, y0) * fx) * (1 - fy)
               + (At(x0, y1) * (1 - fx) + At(x1, y1) * fx) * fy;
    }

    /// <summary>Recadre un carré centré sur (cx, cy) et l'enregistre en JPEG de <paramref name="size"/> pixels.</summary>
    public void SaveSquareCrop(string file, float cx, float cy, float side, int size)
    {
        var pixels = new byte[size * size * 3];
        var scale = side / size;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var sx = cx - side / 2 + x * scale;
                var sy = cy - side / 2 + y * scale;
                for (var c = 0; c < 3; c++)
                    pixels[(y * size + x) * 3 + c] = (byte)Math.Clamp(Sample(sx, sy, c), 0, 255);
            }
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr24, null, pixels, size * 3);
        var encoder = new JpegBitmapEncoder { QualityLevel = 88 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        using (var stream = File.Create(temp)) encoder.Save(stream);
        File.Move(temp, file, overwrite: true);
    }
}
