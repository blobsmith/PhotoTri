using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PhotoTri.Core.Library;

namespace PhotoTri.Core.Places;

/// <summary>
/// Réponse d'OpenStreetMap : l'adresse lisible, et la commune réelle (OpenStreetMap connaît les limites
/// des communes, alors que la base hors ligne ne donne que la commune dont le centre est le plus proche).
/// </summary>
public sealed record OsmAddress(string? Text, string? City, string? Region, string? CountryCode, string? Country);

/// <summary>
/// Adresses précises (rue, quartier, lieu-dit) demandées à OpenStreetMap (Nominatim), facultatif.
/// Règles du service respectées : une requête par seconde au plus, application identifiée, et
/// une seule demande par position arrondie (~100 m), mémorisée dans la base.
/// </summary>
public sealed class AddressResolver : IDisposable
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.1);

    readonly HttpClient _http;
    DateTime _lastRequest = DateTime.MinValue;

    public AddressResolver(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PhotoTri", "1.0"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(application-photo-personnelle)"));
        _http.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("fr"));
    }

    /// <summary>
    /// Traite toutes les positions sans adresse. <paramref name="progress"/> reçoit (faites, total).
    /// S'arrête sans erreur si le service refuse ou n'est pas joignable (pas d'Internet).
    /// </summary>
    public async Task<string?> RunAsync(LibraryIndex index, IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var total = index.CountGeoKeysWithoutAddress();
        var done = 0;
        while (!ct.IsCancellationRequested)
        {
            var batch = index.GeoKeysWithoutAddress(50);
            if (batch.Count == 0) return null;
            foreach (var (key, point) in batch)
            {
                OsmAddress? address;
                try
                {
                    address = await LookupAsync(point, ct);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
                {
                    return "OpenStreetMap limite les demandes : la recherche des adresses reprendra plus tard.";
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    return "OpenStreetMap n'est pas joignable (pas de connexion Internet ?).";
                }
                index.SetAddress(key, address);
                done++;
                progress?.Report((done, total));
            }
        }
        return null;
    }

    public async Task<OsmAddress?> LookupAsync(GeoPoint point, CancellationToken ct)
    {
        var wait = _lastRequest + Interval - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
        _lastRequest = DateTime.UtcNow;

        var url = string.Create(CultureInfo.InvariantCulture,
            $"reverse?format=jsonv2&zoom=18&addressdetails=1&lat={point.Latitude:0.######}&lon={point.Longitude:0.######}");
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return Parse(json.RootElement);
    }

    /// <summary>Adresse lisible et commune, ou null si OpenStreetMap ne connaît rien à cet endroit (en mer…).</summary>
    public static OsmAddress? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("address", out var a)) return null;
        var city = Get(a, "city", "town", "village", "municipality");
        var code = Get(a, "country_code")?.ToUpperInvariant();
        return new OsmAddress(FormatAddress(root), city, Get(a, "state", "region", "county"), code, Get(a, "country"));
    }

    static string? Get(JsonElement address, params string[] names) =>
        names.Select(n => address.TryGetProperty(n, out var v) ? v.GetString() : null)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>« 12 Rue de la Paix, Montmartre, Paris » à partir de la réponse de Nominatim.</summary>
    public static string? FormatAddress(JsonElement root)
    {
        if (!root.TryGetProperty("address", out var a)) return null;
        var street = Get(a, "road", "pedestrian", "footway", "path", "square");
        var number = Get(a, "house_number");
        var parts = new List<string?>
        {
            Get(a, "tourism", "amenity", "leisure", "natural", "building"),
            street is null ? null : number is null ? street : $"{number} {street}",
            Get(a, "neighbourhood", "suburb", "quarter", "hamlet", "isolated_dwelling"),
            Get(a, "village", "town", "city", "municipality"),
        };
        var text = string.Join(", ", parts.Where(p => p is not null).Distinct());
        return text.Length > 0 ? text : null;
    }

    public void Dispose() => _http.Dispose();
}
