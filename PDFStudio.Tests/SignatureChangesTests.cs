using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Comentar um PDF assinado sem invalidar a assinatura, e perceber alterações proibidas depois dela.</summary>
public class SignatureChangesTests
{
    private static readonly AnnotationStyle Red = new(new AnnotationColor(1, 0, 0));

    private static async Task<string> SignedPdf(string dir, string? pfx = null)
    {
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string signed = Path.Combine(dir, "assinado.pdf");
        await PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = input, OutputPath = signed, PfxPath = pfx ?? SigningTests.CreatePfx(dir), PfxPassword = "senha123",
            Visible = true, PageNumber = 1, Reason = "Aprovação",
        });
        return signed;
    }

    [Fact]
    public async Task CommentOnSignedPdf_KeepsTheSignatureValid()
    {
        string dir = TestPdfs.TempDir();
        string signed = await SignedPdf(dir);
        byte[] original = File.ReadAllBytes(signed);
        string commented = Path.Combine(dir, "comentado.pdf");

        PdfAnnotations.AddShape(signed, 0, AnnotationKind.Rectangle, 100, 150, 200, 80, Red, "Ana", commented, "Revisar");

        // Os bytes assinados ficam exatamente como estavam; o comentário vem depois.
        byte[] after = File.ReadAllBytes(commented);
        Assert.True(after.Length > original.Length);
        Assert.True(after.AsSpan(0, original.Length).SequenceEqual(original));
        Assert.Contains("/Square", TestPdfs.AnnotationSubtypes(commented));

        var sig = Assert.Single(DocumentPropertiesService.Read(commented).Signatures);
        Assert.True(sig.IsIntact);
        Assert.False(sig.CoversWholeFile);
        Assert.Equal(SignatureChanges.Comments, sig.ChangesAfter);

        var report = SignatureReview.Evaluate(sig, trustedByUser: true);
        Assert.Equal(SignatureStatus.ValidWithChanges, report.Status);
        Assert.Contains("VÁLIDA", report.Headline);
        Assert.Contains("comentários", report.Headline);
    }

    [Fact]
    public async Task SeveralCommentsAndDeletingOne_StillOnlyComments()
    {
        string dir = TestPdfs.TempDir();
        string file = await SignedPdf(dir);
        PdfAnnotations.AddShape(file, 0, AnnotationKind.Oval, 50, 50, 100, 60, Red, "Ana", file);
        PdfAnnotations.AddShape(file, 1, AnnotationKind.Rectangle, 60, 60, 100, 60, Red, "Ana", file);
        int square = PdfAnnotations.List(file).FindIndex(a => a.PageIndex == 1);
        PdfAnnotations.Delete(file, 1, PdfAnnotations.List(file)[square].Index, file);

        var sig = Assert.Single(DocumentPropertiesService.Read(file).Signatures);
        Assert.True(sig.IsIntact);
        Assert.Equal(SignatureChanges.Comments, sig.ChangesAfter);
        Assert.Equal(SignatureStatus.ValidWithChanges, SignatureReview.Evaluate(sig, trustedByUser: true).Status);

        // O PDFsharp também consegue abrir o arquivo com as várias gravações no fim.
        using var doc = PdfReader.Open(file, PdfDocumentOpenMode.Import);
        Assert.Equal(2, doc.PageCount);
    }

    [Fact]
    public async Task ChangingPageTextAfterSigning_IsInvalid()
    {
        string dir = TestPdfs.TempDir();
        string signed = await SignedPdf(dir);
        byte[] original = File.ReadAllBytes(signed);
        string attacked = Path.Combine(dir, "trocado.pdf");

        // "Ataque": uma gravação no fim do arquivo que troca o desenho da página 2, sem mexer nos bytes assinados.
        using (var doc = PdfReader.Open(new MemoryStream(original), PdfDocumentOpenMode.Import))
        {
            var before = PdfIncrementalUpdate.Snapshot(doc);
            var content = doc.Pages[1].Contents.Elements.GetDictionary(0)!;
            content.Elements.Remove("/Filter");
            content.Stream.Value = System.Text.Encoding.ASCII.GetBytes("BT /F1 24 Tf 100 700 Td (Valor: R$ 1.000.000) Tj ET");
            PdfIncrementalUpdate.Write(original, doc, before, attacked);
        }
        Assert.True(File.ReadAllBytes(attacked).AsSpan(0, original.Length).SequenceEqual(original));

        var sig = Assert.Single(DocumentPropertiesService.Read(attacked).Signatures);
        Assert.True(sig.IsIntact); // a parte assinada continua íntegra…
        Assert.True(sig.ChangesAfter.HasFlag(SignatureChanges.NotAllowed)); // …mas o que se vê mudou
        var report = SignatureReview.Evaluate(sig, trustedByUser: true);
        Assert.Equal(SignatureStatus.Invalid, report.Status);
        Assert.Contains(report.Summary, l => l.Contains("não são permitidas"));
    }

    [Fact]
    public async Task FileWhoseTableIsAStream_AlsoGetsCommentsAtTheEnd()
    {
        string dir = TestPdfs.TempDir();
        string signed = await SignedPdf(dir);
        byte[] original = File.ReadAllBytes(signed);
        string file = Path.Combine(dir, "fluxo.pdf");
        // Primeira gravação com a tabela em formato de fluxo (como fazem o Word e outros programas).
        using (var doc = PdfReader.Open(new MemoryStream(original), PdfDocumentOpenMode.Import))
        {
            var before = PdfIncrementalUpdate.Snapshot(doc);
            doc.Info.Elements["/Subject"] = new PdfString("teste");
            PdfIncrementalUpdate.Write(original, doc, before, file, xrefStream: true);
        }
        byte[] streamed = File.ReadAllBytes(file);
        Assert.Contains("/Type /XRef", System.Text.Encoding.Latin1.GetString(streamed));

        PdfAnnotations.AddShape(file, 1, AnnotationKind.Rectangle, 60, 60, 100, 60, Red, "Ana", file);

        Assert.True(File.ReadAllBytes(file).AsSpan().StartsWith(streamed));
        var sig = Assert.Single(DocumentPropertiesService.Read(file).Signatures);
        Assert.True(sig.IsIntact);
        Assert.Equal(SignatureChanges.Comments, sig.ChangesAfter);
    }

    [Fact]
    public async Task SigningAgain_KeepsTheFirstSignatureValid()
    {
        string dir = TestPdfs.TempDir();
        string first = await SignedPdf(dir);
        byte[] original = File.ReadAllBytes(first);
        string pfx2 = SigningTests.CreatePfx(Directory.CreateDirectory(Path.Combine(dir, "b")).FullName, "Beltrano Teste");
        string second = Path.Combine(dir, "duas.pdf");

        await PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = first, OutputPath = second, PfxPath = pfx2, PfxPassword = "senha123",
            Visible = true, PageNumber = 2, Reason = "Testemunha", Location = "São Paulo",
        });

        Assert.True(File.ReadAllBytes(second).AsSpan().StartsWith(original));
        var sigs = DocumentPropertiesService.Read(second).Signatures;
        Assert.Equal(2, sigs.Count);
        var a = sigs.Single(s => s.Signer == "Fulano de Tal");
        var b = sigs.Single(s => s.Signer == "Beltrano Teste");
        Assert.True(a.IsIntact);
        Assert.Equal(SignatureChanges.Signatures, a.ChangesAfter);
        Assert.Contains("outra assinatura", SignatureReview.Evaluate(a, trustedByUser: true).Headline);
        Assert.Equal(SignatureStatus.ValidWithChanges, SignatureReview.Evaluate(a, trustedByUser: true).Status);
        Assert.True(b.IsIntact);
        Assert.True(b.CoversWholeFile);
        Assert.Equal(2, b.PageNumber);
        Assert.Equal("Testemunha", b.Reason);
        Assert.Equal("São Paulo", b.Location);
        Assert.Equal(SignatureStatus.Valid, SignatureReview.Evaluate(b, trustedByUser: true).Status);

        // Comentar depois das duas: as duas continuam valendo.
        PdfAnnotations.AddShape(second, 0, AnnotationKind.Oval, 50, 50, 100, 60, Red, "Ana", second);
        sigs = DocumentPropertiesService.Read(second).Signatures;
        Assert.All(sigs, s => Assert.Equal(SignatureStatus.ValidWithChanges, SignatureReview.Evaluate(s, trustedByUser: true).Status));
    }

    [Fact]
    public async Task EmptyFieldAddedAfterSigning_ThenSigned_KeepsEverythingValid()
    {
        string dir = TestPdfs.TempDir();
        string file = await SignedPdf(dir);
        byte[] original = File.ReadAllBytes(file);

        PdfSignatureFields.Add(file, 1, 100, 100, 200, 60, "Testemunha", file);
        var empty = Assert.Single(DocumentPropertiesService.Read(file).Signatures);
        Assert.Equal(SignatureChanges.Signatures, empty.ChangesAfter); // campo vazio novo é permitido

        string pfx2 = SigningTests.CreatePfx(Directory.CreateDirectory(Path.Combine(dir, "b")).FullName, "Beltrano Teste");
        await PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = file, OutputPath = file, PfxPath = pfx2, PfxPassword = "senha123", Visible = true, FieldName = "Testemunha",
        });

        Assert.True(File.ReadAllBytes(file).AsSpan().StartsWith(original));
        var sigs = DocumentPropertiesService.Read(file).Signatures;
        Assert.Equal(2, sigs.Count);
        var b = sigs.Single(s => s.FieldName == "Testemunha");
        Assert.Equal("Beltrano Teste", b.Signer);
        Assert.Equal(2, b.PageNumber);
        Assert.Equal(SignatureStatus.Valid, SignatureReview.Evaluate(b, trustedByUser: true).Status);
        Assert.Equal(SignatureStatus.ValidWithChanges, SignatureReview.Evaluate(sigs.Single(s => s != b), trustedByUser: true).Status);
        Assert.DoesNotContain(PdfSignatureFields.List(file), f => !f.Signed);
    }

    [Fact]
    public async Task UnsignedPdf_IsStillRewrittenAsBefore()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        byte[] original = File.ReadAllBytes(input);
        string output = Path.Combine(dir, "saida.pdf");
        PdfAnnotations.AddShape(input, 0, AnnotationKind.Rectangle, 10, 10, 50, 50, Red, "Ana", output);
        Assert.False(File.ReadAllBytes(output).AsSpan().StartsWith(original)); // sem assinatura: arquivo regravado (menor e limpo)
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CertificateMadeForWebsites_IsInvalidForSigning()
    {
        string dir = TestPdfs.TempDir();
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Site Teste, C=BR", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // só servidor web
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(30));
        string pfx = Path.Combine(dir, "site.pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, "senha123"));

        var sig = Assert.Single(DocumentPropertiesService.Read(await SignedPdf(dir, pfx)).Signatures);
        Assert.NotNull(sig.Certificate!.UsageProblem);
        Assert.Equal(TimestampCheck.None, sig.TimestampState);
        var report = SignatureReview.Evaluate(sig, trustedByUser: true);
        Assert.Equal(SignatureStatus.Invalid, report.Status);
        Assert.Contains(report.Summary, l => l.Contains("outra finalidade"));
    }

    private static SignerCertificate Cert(DateTime from, DateTime to, RevocationCheck revocation = RevocationCheck.NotApplicable) =>
        new("X", null, null, null, null, null, null, "CN=X", "X", "CN=X", "01", from, to, "AA", "BB", true, false,
            "RSA 2048 bits", "sha256RSA", [], revocation);

    private static SignatureDetails Sig(SignerCertificate cert, DateTime signedAt) =>
        new("Assinatura1", 1, "X", "X", null, null, null, signedAt, true, true, cert);

    [Fact]
    public void Evaluate_ExpiredNowWithoutTrustedTime_IsUnknown_AndWithTimestampIsValid()
    {
        var cert = Cert(new DateTime(2020, 1, 1), new DateTime(2021, 1, 1));
        var sig = Sig(cert, new DateTime(2020, 6, 1));

        var report = SignatureReview.Evaluate(sig, trustedByUser: true);
        Assert.Equal(SignatureStatus.Unknown, report.Status);
        Assert.Contains(report.Summary, l => l.Contains("venceu"));

        var stamped = sig with { TimestampAt = new DateTime(2020, 6, 1), TimestampState = TimestampCheck.Trusted };
        Assert.Equal(SignatureStatus.Valid, SignatureReview.Evaluate(stamped, trustedByUser: true).Status);

        // Carimbo de um servidor desconhecido não prova a data.
        var unknownServer = stamped with { TimestampState = TimestampCheck.UnknownServer };
        var r2 = SignatureReview.Evaluate(unknownServer, trustedByUser: true);
        Assert.Equal(SignatureStatus.Unknown, r2.Status);
        Assert.Contains(r2.Summary, l => l.Contains("não reconhece"));
    }

    [Fact]
    public void Evaluate_RevokedOrBadLayout_IsInvalid()
    {
        var now = DateTime.Now;
        var revoked = Sig(Cert(now.AddDays(-10), now.AddDays(10), RevocationCheck.Revoked), now.AddDays(-1));
        Assert.Equal(SignatureStatus.Invalid, SignatureReview.Evaluate(revoked, trustedByUser: true).Status);

        var good = Sig(Cert(now.AddDays(-10), now.AddDays(10), RevocationCheck.Good), now.AddDays(-1));
        Assert.Equal(SignatureStatus.Valid, SignatureReview.Evaluate(good, trustedByUser: true).Status);

        var layout = good with { BadLayout = true };
        Assert.Equal(SignatureStatus.Invalid, SignatureReview.Evaluate(layout, trustedByUser: true).Status);

        var later = good with { CoversWholeFile = false, ChangesAfter = SignatureChanges.Signatures | SignatureChanges.Comments };
        var r = SignatureReview.Evaluate(later, trustedByUser: true);
        Assert.Equal(SignatureStatus.ValidWithChanges, r.Status);
        Assert.Contains("comentários e outra assinatura", r.Headline);
    }
}
