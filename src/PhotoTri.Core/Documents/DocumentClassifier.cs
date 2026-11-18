using System.Text.RegularExpressions;
using PhotoTri.Core.Viewer;

namespace PhotoTri.Core.Documents;

public enum DocumentKind
{
    IdentityCard,
    Passport,
    DrivingLicense,
    HealthCard,
    Receipt,
    Document,
}

/// <summary>Reconnaît un document (pièce d'identité, permis, facture, feuille…) d'après le texte lu sur la photo.</summary>
public static partial class DocumentClassifier
{
    /// <summary>En dessous, une photo avec un peu de texte (panneau, tee-shirt, affiche) n'est pas un document.</summary>
    const int DocumentMinWords = 40;

    /// <summary>
    /// Part minimale de la photo couverte par le texte pour un document « ordinaire » : un panneau couvert
    /// de texte au fond d'un paysage n'est pas un document. Les pièces officielles n'ont pas ce seuil.
    /// </summary>
    public const double DocumentMinCoverage = 0.15;

    static readonly string[] PassportWords = { "passeport", "passport", "reisepass", "passaporto", "pasaporte" };
    static readonly string[] DrivingWords = { "permis de conduire", "driving licence", "driving license", "fuhrerschein", "patente di guida", "permiso de conduccion" };
    static readonly string[] IdentityWords =
    {
        "carte nationale d'identite", "carte d'identite", "identity card", "personalausweis", "carta d'identita",
        "documento nacional de identidad", "carte de sejour", "titre de sejour", "identitatskarte",
    };
    static readonly string[] HealthWords = { "carte vitale", "assurance maladie", "carte europeenne d'assurance maladie" };
    static readonly string[] ReceiptWords =
    {
        "facture", "total ttc", "total ht", "tva", "ticket", "montant", "net a payer", "a payer", "sous-total", "sous total",
        "carte bancaire", "especes", "rendu", "merci de votre visite", "quantite", "prix unitaire", "reglement",
    };

    // Montant : « 12,50 », « 1 234.00 ».
    [GeneratedRegex(@"\d+[,.]\d{2}\b")]
    private static partial Regex Amount();

    /// <param name="coverage">Part de la photo couverte par le texte (1 si inconnue).</param>
    public static DocumentKind? Classify(IReadOnlyList<string> lines, double coverage = 1)
    {
        if (lines.Count == 0) return null;
        var text = " " + LibrarySearch.Normalize(string.Join(' ', lines)).Replace('’', '\'').Replace('`', '\'') + " ";

        // Bande lisible par machine des passeports et cartes d'identité : « P<FRA… », « IDFRA…<<<<<<… ».
        var mrz = lines.Select(l => l.Replace(" ", "")).FirstOrDefault(l => l.Count(c => c == '<') >= 8);
        if (mrz is not null) return mrz.StartsWith("P<", StringComparison.OrdinalIgnoreCase) ? DocumentKind.Passport : DocumentKind.IdentityCard;

        if (PassportWords.Any(text.Contains)) return DocumentKind.Passport;
        if (DrivingWords.Any(text.Contains)) return DocumentKind.DrivingLicense;
        if (IdentityWords.Any(text.Contains)) return DocumentKind.IdentityCard;
        if (HealthWords.Any(text.Contains)) return DocumentKind.HealthCard;
        if (ReceiptWords.Count(text.Contains) >= 2 && Amount().IsMatch(text)) return DocumentKind.Receipt;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));
        return words >= DocumentMinWords && coverage >= DocumentMinCoverage ? DocumentKind.Document : null;
    }

    public static string Label(DocumentKind kind) => kind switch
    {
        DocumentKind.IdentityCard => "Pièce d'identité",
        DocumentKind.Passport => "Passeport",
        DocumentKind.DrivingLicense => "Permis de conduire",
        DocumentKind.HealthCard => "Carte Vitale",
        DocumentKind.Receipt => "Facture ou ticket",
        _ => "Document",
    };

    /// <summary>Pièces officielles à manipuler avec précaution (identité, permis, santé).</summary>
    public static bool IsSensitive(DocumentKind kind) =>
        kind is DocumentKind.IdentityCard or DocumentKind.Passport or DocumentKind.DrivingLicense or DocumentKind.HealthCard;
}
