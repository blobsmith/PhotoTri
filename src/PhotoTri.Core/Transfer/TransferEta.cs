namespace PhotoTri.Core.Transfer;

/// <summary>
/// Estime la vitesse et le temps restant à partir des dernières secondes du transfert.
/// En USB, chaque fichier coûte un temps fixe en plus de ses octets : sans cela, une fin de transfert
/// faite de milliers de petites photos WhatsApp serait très sous-estimée. On compte donc chaque fichier
/// comme <see cref="PerFileCost"/> octets supplémentaires.
/// </summary>
public sealed class TransferEta(TimeSpan? window = null)
{
    public const long PerFileCost = 2L * 1024 * 1024;

    static readonly TimeSpan MinimumSpan = TimeSpan.FromSeconds(3);

    readonly TimeSpan _window = window ?? TimeSpan.FromSeconds(30);
    readonly Queue<(TimeSpan At, int Files, long Bytes)> _samples = new();

    public void Add(TimeSpan at, int filesDone, long bytesDone)
    {
        _samples.Enqueue((at, filesDone, bytesDone));
        // On garde toujours un échantillon plus ancien que la fenêtre pour couvrir toute sa durée.
        while (_samples.Count > 2 && at - _samples.ElementAt(1).At >= _window)
            _samples.Dequeue();
    }

    /// <summary>Débit récent en octets par seconde, ou null au tout début du transfert.</summary>
    public double? BytesPerSecond => Rate(s => s.Bytes);

    public TimeSpan? Remaining(int filesTotal, long bytesTotal)
    {
        if (_samples.Count == 0) return null;
        var rate = Rate(Work);
        if (rate is null or <= 0) return null;
        var last = _samples.Last();
        var remainingWork = Work((default, filesTotal, bytesTotal)) - Work(last);
        return TimeSpan.FromSeconds(Math.Max(0, remainingWork) / rate.Value);
    }

    static double Work((TimeSpan At, int Files, long Bytes) s) => s.Bytes + (double)s.Files * PerFileCost;

    double? Rate(Func<(TimeSpan At, int Files, long Bytes), double> measure)
    {
        if (_samples.Count < 2) return null;
        var first = _samples.Peek();
        var last = _samples.Last();
        var span = last.At - first.At;
        if (span < MinimumSpan) return null;
        return (measure(last) - measure(first)) / span.TotalSeconds;
    }

    public static string FormatRemaining(TimeSpan t) => t.TotalMinutes switch
    {
        < 1 => "moins d'une minute restante",
        < 60 => $"environ {Math.Ceiling(t.TotalMinutes):0} min restantes",
        _ => $"environ {(int)t.TotalHours} h {t.Minutes:00} restantes",
    };

    public static string FormatSpeed(double bytesPerSecond) =>
        $"{bytesPerSecond / (1024 * 1024):0.0} Mo/s";
}
