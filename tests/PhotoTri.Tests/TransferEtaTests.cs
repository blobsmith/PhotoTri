using PhotoTri.Core.Transfer;

namespace PhotoTri.Tests;

public class TransferEtaTests
{
    const long MB = 1024 * 1024;

    static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void No_estimate_at_the_very_start()
    {
        var eta = new TransferEta();
        eta.Add(S(0), 0, 0);
        eta.Add(S(1), 1, 50 * MB);
        Assert.Null(eta.BytesPerSecond);
        Assert.Null(eta.Remaining(100, 1000 * MB));
    }

    [Fact]
    public void Measures_recent_speed()
    {
        var eta = new TransferEta();
        for (var s = 0; s <= 10; s++) eta.Add(S(s), s, s * 80 * MB);
        Assert.Equal(80 * MB, eta.BytesPerSecond!.Value, precision: 0);
    }

    [Fact]
    public void Counts_a_fixed_cost_for_each_remaining_file()
    {
        // 1 fichier de 8 Mo par seconde, soit 10 Mo de « travail » par seconde avec le coût fixe de 2 Mo.
        var eta = new TransferEta();
        for (var s = 0; s <= 10; s++) eta.Add(S(s), s, s * 8 * MB);

        // Reste : 100 petits fichiers de 0 octet = 200 Mo de travail = 20 s.
        var remaining = eta.Remaining(filesTotal: 110, bytesTotal: 80 * MB)!.Value;
        Assert.Equal(20, remaining.TotalSeconds, precision: 3);
    }

    [Fact]
    public void Forgets_samples_older_than_the_window()
    {
        var eta = new TransferEta(S(10));
        for (var s = 0; s <= 20; s++) eta.Add(S(s), s, s * 100 * MB);   // rapide au début
        for (var s = 21; s <= 40; s++) eta.Add(S(s), s, 2000 * MB + (s - 20) * 10 * MB); // puis lent
        Assert.Equal(10 * MB, eta.BytesPerSecond!.Value, precision: 0);
    }

    [Theory]
    [InlineData(30, "moins d'une minute restante")]
    [InlineData(61, "environ 2 min restantes")]
    [InlineData(3 * 3600 + 5 * 60, "environ 3 h 05 restantes")]
    public void Formats_remaining_time(int seconds, string expected)
    {
        Assert.Equal(expected, TransferEta.FormatRemaining(S(seconds)));
    }
}
