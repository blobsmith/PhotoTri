using System.Text;

namespace PhotoTri.Tests;

static class TestImages
{
    /// <summary>JPEG minimal contenant seulement un bloc EXIF avec DateTimeOriginal (« yyyy:MM:dd HH:mm:ss »).</summary>
    public static byte[] JpegWithExifDate(string dateTimeOriginal)
    {
        var tiff = new MemoryStream();
        var w = new BinaryWriter(tiff);
        // En-tête TIFF little-endian, IFD0 à l'offset 8.
        w.Write("II"u8);
        w.Write((ushort)42);
        w.Write(8u);
        // IFD0 : une entrée, le pointeur vers la sous-IFD EXIF (offset 26).
        w.Write((ushort)1);
        w.Write((ushort)0x8769); w.Write((ushort)4); w.Write(1u); w.Write(26u);
        w.Write(0u);
        // Sous-IFD EXIF : DateTimeOriginal, chaîne de 20 octets à l'offset 44.
        w.Write((ushort)1);
        w.Write((ushort)0x9003); w.Write((ushort)2); w.Write(20u); w.Write(44u);
        w.Write(0u);
        w.Write(Encoding.ASCII.GetBytes(dateTimeOriginal.PadRight(19)[..19] + "\0"));

        return Jpeg(tiff.ToArray());
    }

    /// <summary>JPEG minimal contenant seulement une position GPS EXIF.</summary>
    public static byte[] JpegWithGps(double latitude, double longitude)
    {
        var tiff = new MemoryStream();
        var w = new BinaryWriter(tiff);
        w.Write("II"u8);
        w.Write((ushort)42);
        w.Write(8u);
        // IFD0 : pointeur vers l'IFD GPS (offset 26).
        w.Write((ushort)1);
        w.Write((ushort)0x8825); w.Write((ushort)4); w.Write(1u); w.Write(26u);
        w.Write(0u);
        // IFD GPS : références N/S et E/O, puis latitude (offset 80) et longitude (offset 104) en degrés/minutes/secondes.
        w.Write((ushort)4);
        w.Write((ushort)1); w.Write((ushort)2); w.Write(2u); w.Write(new[] { (byte)(latitude >= 0 ? 'N' : 'S'), (byte)0, (byte)0, (byte)0 });
        w.Write((ushort)2); w.Write((ushort)5); w.Write(3u); w.Write(80u);
        w.Write((ushort)3); w.Write((ushort)2); w.Write(2u); w.Write(new[] { (byte)(longitude >= 0 ? 'E' : 'W'), (byte)0, (byte)0, (byte)0 });
        w.Write((ushort)4); w.Write((ushort)5); w.Write(3u); w.Write(104u);
        w.Write(0u);
        WriteDms(w, Math.Abs(latitude));
        WriteDms(w, Math.Abs(longitude));
        return Jpeg(tiff.ToArray());
    }

    static void WriteDms(BinaryWriter w, double value)
    {
        var degrees = (uint)value;
        var minutes = (uint)((value - degrees) * 60);
        var seconds = (value - degrees - minutes / 60.0) * 3600;
        w.Write(degrees); w.Write(1u);
        w.Write(minutes); w.Write(1u);
        w.Write((uint)Math.Round(seconds * 10000)); w.Write(10000u);
    }

    static byte[] Jpeg(byte[] tiff)
    {
        var exif = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8, 0xFF, 0xE1]);
        var length = exif.Length + 2;
        jpeg.Write([(byte)(length >> 8), (byte)length]);
        jpeg.Write(exif);
        jpeg.Write([0xFF, 0xD9]);
        return jpeg.ToArray();
    }
}
