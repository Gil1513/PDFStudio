using System.Security.Cryptography.X509Certificates;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Criar o próprio certificado e a tela "Propriedades da assinatura" (validade, certificado, resumo).</summary>
public class SignatureReviewTests
{
    private const string Password = "segredo123";

    private static readonly CertificateIdentity Ana = new("Ana Souza, Teste", "ana@exemplo.com", "Empresa Exemplo Ltda",
        "Financeiro", "São Paulo", "SP", "br");

    [Fact]
    public void Problem_ChecksTheFields()
    {
        Assert.Null(CertificateMaker.Problem(Ana, Password, 3));
        Assert.NotNull(CertificateMaker.Problem(Ana with { Name = " " }, Password, 3));
        Assert.NotNull(CertificateMaker.Problem(Ana with { Email = "sem-arroba" }, Password, 3));
        Assert.NotNull(CertificateMaker.Problem(Ana with { Country = "Brasil" }, Password, 3));
        Assert.NotNull(CertificateMaker.Problem(Ana, "123", 3));
        Assert.NotNull(CertificateMaker.Problem(Ana, Password, 0));
        Assert.NotNull(CertificateMaker.Problem(Ana, Password, 11));
        Assert.Null(CertificateMaker.Problem(new CertificateIdentity("Só o nome", Country: null), Password, 1));
    }

    [Fact]
    public void CreatePfx_HasTheTypedData()
    {
        string dir = TestPdfs.TempDir();
        string pfx = Path.Combine(dir, "meu.pfx");
        var summary = CertificateMaker.CreatePfx(Ana, Password, 3, pfx);
        Assert.Equal("Ana Souza, Teste", summary.Name);
        Assert.True(summary.HasPrivateKey);
        Assert.True(summary.IsValidNow);
        Assert.InRange((summary.NotAfter - DateTime.Now).TotalDays, 365 * 3 - 3, 365 * 3 + 3);

        using var cert = PdfSigning.LoadCertificate(pfx, Password);
        var info = SignerCertificate.From(cert);
        Assert.Equal("Ana Souza, Teste", info.Name); // a vírgula do nome não quebra o certificado
        Assert.Equal("ana@exemplo.com", info.Email);
        Assert.Equal("Empresa Exemplo Ltda", info.Organization);
        Assert.Equal("Financeiro", info.OrganizationalUnit);
        Assert.Equal("São Paulo", info.City);
        Assert.Equal("SP", info.State);
        Assert.Equal("BR", info.Country);
        Assert.True(info.IsSelfSigned);
        Assert.False(info.IsTrustedBySystem);
        Assert.Equal("RSA 2048 bits", info.KeyDescription);
        Assert.Contains("Assinatura digital", info.Usages);
        Assert.Contains("Não repúdio", info.Usages);
        Assert.Contains("Assinatura de documentos", info.Usages);

        Assert.Throws<InvalidOperationException>(() => PdfSigning.InspectCertificate(pfx, "errada"));
        Assert.Throws<ArgumentException>(() => CertificateMaker.CreatePfx(Ana with { Name = "" }, Password, 3, pfx));
    }

    [Fact]
    public async Task SignedWithOwnCertificate_IsUnknownUntilTrusted()
    {
        string dir = TestPdfs.TempDir();
        string pfx = Path.Combine(dir, "meu.pfx");
        CertificateMaker.CreatePfx(Ana, Password, 2, pfx);
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string output = Path.Combine(dir, "assinado.pdf");
        await PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = input, OutputPath = output, PfxPath = pfx, PfxPassword = Password,
            Reason = "Aprovação", Location = "São Paulo",
        });

        var sig = Assert.Single(DocumentPropertiesService.Read(output).Signatures);
        Assert.Equal("Ana Souza, Teste", sig.Signer);
        Assert.Equal("Aprovação", sig.Reason);
        Assert.Equal("São Paulo", sig.Location);
        Assert.True(sig.IsIntact);
        Assert.True(sig.CoversWholeFile);
        Assert.NotNull(sig.Certificate);
        Assert.Equal("ana@exemplo.com", sig.Certificate!.Email);
        Assert.Null(sig.TimestampAt);
        Assert.Null(sig.CertificationLevel);

        var unknown = SignatureReview.Evaluate(sig, trustedByUser: false);
        Assert.Equal(SignatureStatus.Unknown, unknown.Status);
        Assert.Contains("DESCONHECIDA", unknown.Headline);
        Assert.Contains(unknown.Summary, l => l.Contains("não foi alterado"));
        Assert.Contains(unknown.Summary, l => l.Contains("autoassinado"));
        Assert.Contains(unknown.Summary, l => l.Contains("relógio do computador"));

        var trusted = SignatureReview.Evaluate(sig, trustedByUser: true);
        Assert.Equal(SignatureStatus.Valid, trusted.Status);
        Assert.Contains(trusted.Summary, l => l.Contains("marcou este certificado como confiável"));
    }

    [Fact]
    public async Task AlteredDocument_IsInvalid()
    {
        string dir = TestPdfs.TempDir();
        string pfx = Path.Combine(dir, "meu.pfx");
        CertificateMaker.CreatePfx(Ana, Password, 1, pfx);
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string output = Path.Combine(dir, "assinado.pdf");
        await PdfSigning.SignAsync(new SignatureRequest { InputPath = input, OutputPath = output, PfxPath = pfx, PfxPassword = Password });

        // Troca um byte do conteúdo assinado (no começo do arquivo, fora da assinatura).
        byte[] bytes = File.ReadAllBytes(output);
        int at = Array.IndexOf(bytes, (byte)'P', 20);
        bytes[at] = (byte)'Q';
        File.WriteAllBytes(output, bytes);

        var sig = Assert.Single(DocumentPropertiesService.Read(output).Signatures);
        var report = SignatureReview.Evaluate(sig, trustedByUser: true);
        Assert.Equal(SignatureStatus.Invalid, report.Status);
        Assert.Contains("INVÁLIDA", report.Headline);
    }

    [Fact]
    public void Evaluate_ExpiredCertificateAndCertification()
    {
        var cert = new SignerCertificate("X", null, null, null, null, null, null, "CN=X", "X", "CN=X", "01",
            new DateTime(2020, 1, 1), new DateTime(2021, 1, 1), "AA", "BB", true, false, "RSA 2048 bits", "sha256RSA", []);
        var sig = new SignatureDetails("Assinatura1", 1, "X", "X", null, null, null, new DateTime(2022, 5, 1),
            true, true, cert, new DateTime(2022, 5, 1, 10, 0, 0), 1);
        var report = SignatureReview.Evaluate(sig, trustedByUser: true);
        Assert.Equal(SignatureStatus.Invalid, report.Status);
        Assert.Contains(report.Summary, l => l.Contains("fora do prazo"));
        Assert.Contains(report.Summary, l => l.Contains("não permite nenhuma alteração"));
        Assert.Contains(report.Summary, l => l.Contains("carimbo de tempo"));

        var noCert = SignatureReview.Evaluate(sig with { Certificate = null, CertificationLevel = null }, trustedByUser: false);
        Assert.Equal(SignatureStatus.Unknown, noCert.Status);
    }
}
