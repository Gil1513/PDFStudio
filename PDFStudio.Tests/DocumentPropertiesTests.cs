using PDFStudio.Core;

namespace PDFStudio.Tests;

public class DocumentPropertiesTests
{
    [Fact]
    public void Read_ReturnsBasicInformation()
    {
        string dir = TestPdfs.TempDir();
        string pdf = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 4);

        var props = DocumentPropertiesService.Read(pdf);

        Assert.Equal(4, props.PageCount);
        Assert.Equal("Página - teste", props.Metadata.Title);
        Assert.Equal(new FileInfo(pdf).Length, props.FileSize);
        Assert.StartsWith("PDF-1.", props.PdfVersion);
        Assert.False(props.IsEncrypted);
        Assert.Empty(props.Signatures);

        // A4 = 21,0 x 29,7 cm
        Assert.Equal(21.0, DocumentPropertiesService.Convert(props.PageWidthPt, PageSizeUnit.Centimeters), 1);
        Assert.Equal(29.7, DocumentPropertiesService.Convert(props.PageHeightPt, PageSizeUnit.Centimeters), 1);
        Assert.Equal(210, DocumentPropertiesService.Convert(props.PageWidthPt, PageSizeUnit.Millimeters), 0);
    }

    [Fact]
    public void Save_UpdatesEditableFields_AndKeepsPages()
    {
        string dir = TestPdfs.TempDir();
        string pdf = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);

        DocumentPropertiesService.Save(pdf,
            new DocumentMetadata("Contrato de Trabalho", "José Ação", "Assunto ç", "pdf, teste"), pdf);

        var props = DocumentPropertiesService.Read(pdf);
        Assert.Equal("Contrato de Trabalho", props.Metadata.Title);
        Assert.Equal("José Ação", props.Metadata.Author);
        Assert.Equal("Assunto ç", props.Metadata.Subject);
        Assert.Equal("pdf, teste", props.Metadata.Keywords);
        Assert.Equal(["Página 1", "Página 2", "Página 3"], TestPdfs.Labels(pdf));
        Assert.False(File.Exists(pdf + ".tmp"));
    }

    [Fact]
    public void Save_ToAnotherFile_KeepsOriginal()
    {
        string dir = TestPdfs.TempDir();
        string pdf = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string copy = Path.Combine(dir, "copia.pdf");

        DocumentPropertiesService.Save(pdf, new DocumentMetadata("Novo", "", "", ""), copy);

        Assert.Equal("Página - teste", DocumentPropertiesService.Read(pdf).Metadata.Title);
        Assert.Equal("Novo", DocumentPropertiesService.Read(copy).Metadata.Title);
    }

    [Fact]
    public async Task Read_ListsExistingSignature_AndDetectsTampering()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string signed = Path.Combine(dir, "assinado.pdf");
        await PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = input, OutputPath = signed, PfxPath = SigningTests.CreatePfx(dir), PfxPassword = "senha123",
            Visible = true, PageNumber = 2, Reason = "Aprovação", Location = "São Paulo", ContactInfo = "a@b.com",
        });

        Assert.False(DocumentPropertiesService.HasSignatures(input));
        Assert.True(DocumentPropertiesService.HasSignatures(signed));

        var sig = Assert.Single(DocumentPropertiesService.Read(signed).Signatures);
        Assert.Equal("Fulano de Tal", sig.Signer);
        Assert.Equal("Aprovação", sig.Reason);
        Assert.Equal("São Paulo", sig.Location);
        Assert.Equal(2, sig.PageNumber);
        Assert.True(sig.IsIntact);
        Assert.True(sig.CoversWholeFile);
        Assert.NotNull(sig.SignedAt);
        Assert.InRange(sig.SignedAt!.Value, DateTime.Now.AddMinutes(-5), DateTime.Now.AddMinutes(5));

        // Muda um byte coberto pela assinatura.
        byte[] bytes = File.ReadAllBytes(signed);
        bytes[7] = bytes[7] == (byte)'9' ? (byte)'8' : (byte)(bytes[7] + 1);
        File.WriteAllBytes(signed, bytes);
        Assert.False(Assert.Single(DocumentPropertiesService.Read(signed).Signatures).IsIntact);
    }

    [Theory]
    [InlineData("D:20261110135002-03'00'", 2026, 11, 10, 16, 50, 2)] // UTC
    [InlineData("D:20261110135002Z", 2026, 11, 10, 13, 50, 2)]
    public void ParsePdfDate_HandlesTimeZones(string input, int y, int mo, int d, int h, int mi, int s)
    {
        var parsed = DocumentPropertiesService.ParsePdfDate(input);
        Assert.NotNull(parsed);
        Assert.Equal(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc), parsed!.Value.ToUniversalTime());
    }

    [Fact]
    public void FormatSize_UsesReadableUnits()
    {
        Assert.Equal("500 bytes", DocumentPropertiesService.FormatSize(500));
        Assert.Equal("145,8 KB", DocumentPropertiesService.FormatSize(149299).Replace('.', ','));
    }
}
