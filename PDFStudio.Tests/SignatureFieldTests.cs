using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 6.4: campos de assinatura vazios.</summary>
public class SignatureFieldTests
{
    [Fact]
    public void Add_CreatesAnEmptyField_WhereItWasDrawn()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);

        PdfSignatureFields.Add(input, 0, 100, 200, 180, 50, "Cliente", input);

        var field = Assert.Single(PdfSignatureFields.List(input));
        Assert.Equal(("Cliente", 0, false), (field.Name, field.Page, field.Signed));
        Assert.Equal(100, field.X, 1);
        Assert.Equal(200, field.Y, 1);
        Assert.Equal(180, field.Width, 1);
        Assert.Equal(50, field.Height, 1);
        Assert.Equal(["/Widget"], TestPdfs.AnnotationSubtypes(input));
        Assert.Contains("Assinar aqui", TestPdfs.Appearance(input, 0).Content);
        Assert.False(PdfSigning.HasSignatures(input)); // vazio não conta como assinado

        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var fields = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!;
        Assert.Equal("/Sig", fields.Elements.GetDictionary(0)!.Elements.GetName("/FT"));
    }

    [Fact]
    public void RotatedPage_KeepsTheScreenPosition_AndTurnsTheDrawing()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfAnnotations.Edit(input, input, doc => doc.Pages[0].Rotate = 90);

        PdfSignatureFields.Add(input, 0, 50, 60, 200, 40, "A", input);

        var field = Assert.Single(PdfSignatureFields.List(input));
        Assert.Equal((50.0, 60.0, 200.0, 40.0), (Math.Round(field.X), Math.Round(field.Y), Math.Round(field.Width), Math.Round(field.Height)));
        Assert.NotNull(TestPdfs.Appearance(input, 0).Matrix);
    }

    [Fact]
    public void Names_AreUniqueAndSuggested()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Equal("Assinatura1", PdfSignatureFields.SuggestName(input));
        PdfSignatureFields.Add(input, 0, 10, 10, 100, 30, "Assinatura1", input);
        Assert.Equal("Assinatura2", PdfSignatureFields.SuggestName(input));

        Assert.Throws<ArgumentException>(() => PdfSignatureFields.Add(input, 0, 10, 100, 100, 30, "assinatura1", input));
        Assert.Throws<ArgumentException>(() => PdfSignatureFields.Add(input, 0, 10, 100, 100, 30, "a.b", input));
        Assert.Throws<ArgumentException>(() => PdfSignatureFields.Add(input, 0, 10, 100, 20, 10, "Pequeno", input));
    }

    [Fact]
    public void Remove_TakesOutTheField()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfSignatureFields.Add(input, 0, 10, 10, 100, 30, "A", input);
        PdfSignatureFields.Add(input, 0, 10, 100, 100, 30, "B", input);

        PdfSignatureFields.Remove(input, "A", input);

        Assert.Equal(["B"], PdfSignatureFields.List(input).Select(f => f.Name));
        Assert.Throws<ArgumentException>(() => PdfSignatureFields.Remove(input, "A", input));
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!.Elements.Count);
    }

    [Fact]
    public async Task Sign_InsideTheField_UsesItsPlaceAndSize()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        PdfSignatureFields.Add(input, 2, 300, 600, 220, 60, "Cliente", input);
        PdfSignatureFields.Add(input, 0, 50, 50, 150, 40, "Testemunha", input);
        string output = Path.Combine(dir, "assinado.pdf");

        await PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = input, OutputPath = output, PfxPath = SigningTests.CreatePfx(dir), PfxPassword = "senha123",
            Visible = true, FieldName = "Cliente",
        });

        SigningTests.VerifySignature(output);
        var (page, r) = SigningTests.FindSignatureWidget(output);
        Assert.Equal(2, page);
        Assert.Equal(220, Math.Abs(r[2] - r[0]), 1);
        Assert.Equal(60, Math.Abs(r[3] - r[1]), 1);
        Assert.Equal(300, Math.Min(r[0], r[2]), 1);

        var fields = PdfSignatureFields.List(output);
        Assert.Equal(2, fields.Count);
        Assert.Contains(fields, f => f.Name == "Testemunha" && !f.Signed); // o outro continua vazio
        Assert.Contains(fields, f => f.Signed && f.Page == 2);
    }

    [Fact]
    public async Task Sign_InAMissingField_Fails()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = input, OutputPath = Path.Combine(dir, "x.pdf"), PfxPath = SigningTests.CreatePfx(dir), PfxPassword = "senha123",
            Visible = true, FieldName = "Nenhum",
        }));
    }
}
