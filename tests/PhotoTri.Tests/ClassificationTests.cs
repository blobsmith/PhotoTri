using PhotoTri.Core.Library;
using PhotoTri.Core.Media;

namespace PhotoTri.Tests;

public class ClassificationTests
{
    [Theory]
    [InlineData("IMG_20261024_143012.jpg", 2026, 10, 24, 14, 30, 12)]
    [InlineData("VID_20250101_000501.mp4", 2025, 1, 1, 0, 5, 1)]
    [InlineData("PXL_20240229_235959123.jpg", 2024, 2, 29, 23, 59, 59)]
    [InlineData("Screenshot_20230518-081500_Chrome.png", 2023, 5, 18, 8, 15, 0)]
    [InlineData("Screenshot_2022-12-31-23-59-58.png", 2022, 12, 31, 23, 59, 58)]
    [InlineData("IMG-20210704-WA0012.jpg", 2021, 7, 4, 12, 0, 0)]
    public void Reads_date_from_file_name(string name, int y, int mo, int d, int h, int mi, int s)
    {
        Assert.True(DateResolver.TryFromFileName(name, out var date));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), date);
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("IMG_20261399_143012.jpg")]
    [InlineData("12345678901234.jpg")]
    public void Ignores_names_without_valid_date(string name)
    {
        Assert.False(DateResolver.TryFromFileName(name, out _));
    }

    [Fact]
    public void Month_folder_uses_french_month_names()
    {
        Assert.Equal(Path.Combine("2026", "2026-10 Octobre"), LibraryLayout.MonthFolder(new DateTime(2026, 10, 24)));
        Assert.Equal(Path.Combine("2025", "2025-02 Février"), LibraryLayout.MonthFolder(new DateTime(2025, 2, 1)));
        Assert.Equal(Path.Combine("2024", "2024-08 Août"), LibraryLayout.MonthFolder(new DateTime(2024, 8, 15)));
    }

    [Theory]
    [InlineData(@"\Stockage interne\DCIM\Camera\IMG_1.jpg", MediaOrigin.Camera)]
    [InlineData(@"\Stockage interne\DCIM\Screenshots\Screenshot_1.png", MediaOrigin.Screenshot)]
    [InlineData(@"\Stockage interne\Pictures\Screenshots\Screenshot_1.png", MediaOrigin.Screenshot)]
    [InlineData(@"\Stockage interne\Android\media\com.whatsapp\WhatsApp\Media\WhatsApp Images\IMG-1.jpg", MediaOrigin.WhatsApp)]
    [InlineData(@"\Stockage interne\Pictures\Messenger\received_1.jpeg", MediaOrigin.OtherApp)]
    [InlineData(@"\Stockage interne\Pictures\Snapseed\IMG_1.jpg", MediaOrigin.Other)]
    public void Recognises_origin_from_path(string path, MediaOrigin expected)
    {
        Assert.Equal(expected, MediaClassifier.OriginOf(path));
    }

    [Theory]
    [InlineData(@"\Stockage interne\DCIM\Camera\.trashed-1730000000-IMG_1.jpg")]
    [InlineData(@"\Stockage interne\DCIM\.thumbnails\123.jpg")]
    [InlineData(@"\Stockage interne\Android\media\com.whatsapp\WhatsApp\Media\WhatsApp Images\Sent\IMG-1.jpg")]
    [InlineData(@"\Stockage interne\Android\media\com.whatsapp\WhatsApp\Media\.Statuses\1.jpg")]
    [InlineData(@"\Stockage interne\Android\media\com.whatsapp\WhatsApp\Media\WhatsApp Animated Gifs\VID-1.mp4")]
    [InlineData(@"\Stockage interne\Pictures\PokemonGO\IMG_1.jpg")]
    [InlineData(@"\Stockage interne\DCIM\Game media\Pokémon GO\Screen_1.mp4")]
    public void Excludes_trash_thumbnails_and_sent_copies(string path)
    {
        Assert.True(MediaClassifier.IsExcluded(path));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(-90, 270)]
    [InlineData(180.0000001, 180)]
    [InlineData(-180, 180)]
    [InlineData(450, 90)]
    public void Video_rotation_is_rounded_to_a_quarter_turn(double degrees, int expected)
    {
        Assert.Equal(expected, VideoRotation.Normalize(degrees));
    }

    [Fact]
    public void Video_rotation_of_unreadable_file_is_zero()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "pas une vidéo");
            Assert.Equal(0, VideoRotation.Read(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Filter_respects_checkboxes()
    {
        var noScreens = new MediaFilter(Screenshots: false);
        Assert.False(noScreens.Accepts(MediaKind.Photo, MediaOrigin.Screenshot));
        Assert.True(noScreens.Accepts(MediaKind.Photo, MediaOrigin.Camera));

        var noVideos = new MediaFilter(Videos: false);
        Assert.False(noVideos.Accepts(MediaKind.Video, MediaOrigin.Camera));
        Assert.True(noVideos.Accepts(MediaKind.Photo, MediaOrigin.WhatsApp));
    }
}
