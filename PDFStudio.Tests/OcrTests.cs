using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.7: OCR (páginas sem texto e a camada de texto invisível).</summary>
public class OcrTests
{
    /// <summary>PDF "escaneado": só imagens, nenhum texto.</summary>
    private static string Scanned(string dir, int pages = 2)
    {
        string png = TestImages.Png(Path.Combine(dir, "folha.png"), 300, 400);
        string pdf = Path.Combine(dir, "escaneado.pdf");
        ImagesToPdf.Create(Enumerable.Repeat(png, pages).ToList(), ImagePageSize.A4, ImageMargin.None, pdf);
        return pdf;
    }

    [Fact]
    public void PagesWithoutText_FindsTheScannedOnes()
    {
        string dir = TestPdfs.TempDir();
        Assert.Equal([0, 1], PdfOcr.PagesWithoutText(Scanned(dir)));
        Assert.Empty(PdfOcr.PagesWithoutText(ContentBlockTests.Sample(TestPdfs.TempDir()))); // página com texto de verdade
    }

    [Fact]
    public void TextLayer_CanBeFound_WhereTheWordIs()
    {
        string dir = TestPdfs.TempDir();
        string input = Scanned(dir);
        string output = Path.Combine(dir, "pesquisavel.pdf");
        var words = new Dictionary<int, IReadOnlyList<OcrWord>>
        {
            [1] = [new("Ação", 100, 200, 80, 20), new("rápida", 190, 200, 110, 20), new("  ", 0, 0, 5, 5)],
        };

        PdfOcr.AddTextLayer(input, words, output);

        Assert.Empty(PdfTextLayout.Words(output, 0)); // a página 1 continua sem texto
        var found = PdfTextLayout.Words(output, 1);
        Assert.Equal(["Ação", "rápida"], found.Select(w => w.Text));
        var acao = found[0];
        Assert.Equal(100, acao.X, 1.0);       // começa onde a palavra está na imagem
        Assert.Equal(180, acao.Right, 1.0);   // e termina onde ela termina (largura ajustada)
        Assert.InRange(acao.Y, 195, 205);     // na mesma altura
        Assert.Equal("Ação rápida", PdfTextLayout.TextOf(found)); // copiar devolve o texto com espaço

        // Invisível de verdade: todo bloco de texto está no modo 3 ("não pinta"). Defeito encontrado: uma cor
        // transparente não basta, o PDFsharp gravava o texto em preto por cima da imagem.
        using var pig = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.All(pig.GetPage(2).Letters, l => Assert.Equal(3, (int)l.RenderingMode)); // 3 = invisível
    }

    [Fact]
    public void TextLayer_OnRotatedPage_FollowsWhatTheUserSees()
    {
        string dir = TestPdfs.TempDir();
        string input = Scanned(dir, 1);
        PdfAnnotations.Edit(input, input, doc => doc.Pages[0].Rotate = 90); // página deitada na tela
        string output = Path.Combine(dir, "girada.pdf");

        PdfOcr.AddTextLayer(input, new Dictionary<int, IReadOnlyList<OcrWord>> { [0] = [new("Titulo", 50, 60, 120, 24)] }, output);

        var word = Assert.Single(PdfTextLayout.Words(output, 0));
        Assert.Equal("Titulo", word.Text);
        Assert.Equal(50, word.X, 1.5);
        Assert.Equal(170, word.Right, 1.5);
    }

    [Fact]
    public void WrongPage_IsRejected()
    {
        string dir = TestPdfs.TempDir();
        string input = Scanned(dir, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOcr.AddTextLayer(input, new Dictionary<int, IReadOnlyList<OcrWord>> { [3] = [new("x", 1, 1, 5, 5)] }, input));
    }
}
