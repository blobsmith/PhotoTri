using PhotoTri.Core.Documents;

namespace PhotoTri.Tests;

public class DocumentTests
{
    static DocumentKind? Classify(params string[] lines) => DocumentClassifier.Classify(lines);

    [Fact]
    public void Recognises_identity_cards_and_passports_from_their_machine_readable_zone()
    {
        Assert.Equal(DocumentKind.IdentityCard, Classify("RÉPUBLIQUE FRANÇAISE", "IDFRADUPONT<<<<<<<<<<<<<<<<<<<<<<<<<<<"));
        Assert.Equal(DocumentKind.Passport, Classify("P<FRADUPONT<<JEAN<<<<<<<<<<<<<<<<<<<<<<<<<<"));
    }

    [Theory]
    [InlineData("CARTE NATIONALE D'IDENTITÉ", DocumentKind.IdentityCard)]
    [InlineData("CARTE NATIONALE D’IDENTITÉ / IDENTITY CARD", DocumentKind.IdentityCard)]
    [InlineData("PASSEPORT / PASSPORT", DocumentKind.Passport)]
    [InlineData("PERMIS DE CONDUIRE", DocumentKind.DrivingLicense)]
    [InlineData("Führerschein", DocumentKind.DrivingLicense)]
    [InlineData("carte Vitale", DocumentKind.HealthCard)]
    [InlineData("Titre de séjour", DocumentKind.IdentityCard)]
    public void Recognises_official_documents_by_their_title(string title, DocumentKind expected)
    {
        Assert.Equal(expected, Classify("RÉPUBLIQUE FRANÇAISE", title, "Nom : DUPONT"));
    }

    [Fact]
    public void Recognises_receipts_and_invoices()
    {
        Assert.Equal(DocumentKind.Receipt, Classify("SUPER U", "Pain 1,20", "Lait 0,95", "TOTAL TTC 2,15", "Carte bancaire"));
        Assert.Equal(DocumentKind.Receipt, Classify("FACTURE N° 2026-104", "Montant HT 100,00", "TVA 20 % 20,00"));
    }

    [Fact]
    public void A_page_full_of_text_is_a_document()
    {
        var page = Enumerable.Repeat("Ceci est une ligne de texte d'un courrier administratif reçu par la poste", 6).ToArray();
        Assert.Equal(DocumentKind.Document, Classify(page));
    }

    [Fact]
    public void A_sign_full_of_text_in_a_corner_of_the_photo_is_not_a_document()
    {
        var sign = Enumerable.Repeat("Ce panneau explique l'histoire de cet avion exposé devant le musée de l'air", 6).ToArray();
        Assert.Null(DocumentClassifier.Classify(sign, coverage: 0.05));
        Assert.Equal(DocumentKind.Document, DocumentClassifier.Classify(sign, coverage: 0.6));
        // Une pièce officielle reste reconnue, même photographiée de loin.
        Assert.Equal(DocumentKind.DrivingLicense, DocumentClassifier.Classify(new[] { "PERMIS DE CONDUIRE" }, coverage: 0.02));
    }

    [Theory]
    [InlineData("STOP")]
    [InlineData("Boulangerie", "Ouvert 7j/7")]
    [InlineData("Total 12,50")] // un seul mot de facture : pas assez
    public void A_little_text_in_a_photo_is_not_a_document(params string[] lines)
    {
        Assert.Null(Classify(lines));
    }

    [Fact]
    public void Nothing_read_means_no_document()
    {
        Assert.Null(Classify());
    }
}
