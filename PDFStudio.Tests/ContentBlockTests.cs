using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 5.8: achar os blocos (parágrafos e imagens) da página.</summary>
public class ContentBlockTests
{
    private static readonly AnnotationColor Black = new(0, 0, 0);
    private static readonly AnnotationColor Blue = new(20, 60, 200);

    /// <summary>Página de exemplo (os rótulos "topo" e "1" dela são ignorados), com título, parágrafo, duas colunas e uma imagem.</summary>
    internal static string Sample(string dir, int rotation = 0)
    {
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1, prefix: "");
        if (rotation != 0) PdfOperations.Reorganize(input, [new(0, rotation)], input);
        PdfPageContent.AddText(input, 0, 60, 120, "Relatório anual", new PageTextStyle("Arial", 20, Blue, Bold: true), input);
        PdfPageContent.AddText(input, 0, 60, 170, "Primeira linha do texto\nsegunda linha do texto\nterceira linha",
            new PageTextStyle("Times New Roman", 11, Black), input);
        PdfPageContent.AddText(input, 0, 60, 260, "Coluna esquerda", new PageTextStyle("Arial", 10, Black), input);
        PdfPageContent.AddText(input, 0, 300, 260, "Coluna direita", new PageTextStyle("Arial", 10, Black), input);
        string png = TestImages.Png(Path.Combine(dir, "foto.png"), 40, 20);
        PdfPageContent.AddImage(input, 0, 60, 320, 160, 80, png, input);
        return input;
    }

    private static List<ContentBlock> TextBlocks(string path) =>
        PdfContentBlocks.Detect(path, 0).Where(b => b.Kind == BlockKind.Text && b.Text is not ("topo" or "1")).ToList();

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void FindsTitle_Paragraph_Columns_AndImage_OnEveryRotation(int rotation)
    {
        string input = Sample(TestPdfs.TempDir(), rotation);

        var text = TextBlocks(input);
        Assert.Equal(["Relatório anual", "Primeira linha do texto\nsegunda linha do texto\nterceira linha", "Coluna esquerda", "Coluna direita"],
            text.Select(b => b.Text));

        var title = text[0];
        Assert.Equal("Arial", title.FontFamily);
        Assert.True(title.Bold);
        Assert.Equal(20, title.FontSize, 0.5);
        Assert.Equal(Blue, title.Color);
        Assert.InRange(title.X, 58, 62);
        Assert.InRange(title.Lines[0].Baseline, 120 + 15, 120 + 22); // o texto foi posto com o topo em y=120

        var paragraph = text[1];
        Assert.Equal(3, paragraph.Lines.Count);
        Assert.Equal("Times New Roman", paragraph.FontFamily);
        Assert.False(paragraph.Bold);
        Assert.InRange(paragraph.Lines[1].Baseline - paragraph.Lines[0].Baseline, 11, 15);

        var image = Assert.Single(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        Assert.InRange(image.X, 59, 61);
        Assert.InRange(image.Y, 319, 321);
        Assert.InRange(image.Width, 159, 161);
        Assert.InRange(image.Height, 79, 81);
    }

    [Theory]
    [InlineData("ABCDEF+Calibri-Bold", "Calibri", true, false)]
    [InlineData("TimesNewRomanPSMT", "Times New Roman", false, false)]
    [InlineData("TimesNewRomanPS-BoldItalicMT", "Times New Roman", true, true)]
    [InlineData("Arial,Italic", "Arial", false, true)]
    [InlineData("QWERTY+SegoeUI-Semibold", "Segoe UI", true, false)]
    [InlineData("Helvetica", "Helvetica", false, false)]
    [InlineData("Helveticaעבה", "Helvetica", true, false)]
    [InlineData("ABCDEF+Helvetica עבה נטוי", "Helvetica", true, true)]
    [InlineData("Arial-Negrito", "Arial", true, false)]
    [InlineData("Arial Fett Kursiv", "Arial", true, true)]
    [InlineData("Calibriרגיל", "Calibri", false, false)]
    [InlineData("Helvetica-Gras", "Helvetica", true, false)]
    [InlineData("Helvetica‭ ‬", "Helvetica", false, false)]
    [InlineData("ABCDEF+Helvetica‭עבה‬", "Helvetica", true, false)]
    [InlineData("Helvetica​-Bold", "Helvetica", true, false)]
    public void ParseFontName(string name, string family, bool bold, bool italic)
    {
        Assert.Equal((family, bold, italic), PdfContentBlocks.ParseFontName(name));
    }

    [Fact]
    public void EmptyOrUnreadable_GivesNoBlocks()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1, prefix: "");
        Assert.DoesNotContain(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        Assert.Empty(PdfContentBlocks.Detect(input, 5));
        string junk = Path.Combine(dir, "lixo.pdf");
        File.WriteAllText(junk, "não é PDF");
        Assert.Empty(PdfContentBlocks.Detect(junk, 0));
    }
}
