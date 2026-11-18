using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 5: escrever no conteúdo da página (não é comentário).</summary>
public class PageContentTests
{
    private static readonly PageTextStyle Arial = new("Arial", 20, new AnnotationColor(200, 0, 0));

    private static TextWord Find(string path, string text) =>
        Assert.Single(PdfTextLayout.Words(path, 0), w => w.Text == text);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void AddText_StaysWhereClicked_AndUpright_OnEveryRotation(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        if (rotation != 0) PdfOperations.Reorganize(input, [new(0, rotation)], input);
        string output = Path.Combine(dir, "saida.pdf");

        PdfPageContent.AddText(input, 0, 100, 150, "Contrato assinado", Arial, output);

        var word = Find(output, "Contrato"); // palavra inteira = texto na horizontal, como aparece na tela
        Assert.InRange(word.X, 98, 103);
        Assert.InRange(word.Y, 147, 156);
        Assert.InRange(word.Height, 17, 24);
        var next = Find(output, "assinado");
        Assert.Equal(word.Line, next.Line);
        Assert.True(next.X > word.Right);
        Assert.Equal(rotation, TestPdfs.Rotations(output)[0]); // a rotação da página continua a mesma
        Assert.Empty(PdfAnnotations.List(output));          // não virou comentário
    }

    [Fact]
    public void AddText_OnCroppedPage_UsesTheVisiblePosition()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfOperations.CropPages(input, null, new PageMargins(40, 0, 0, 30), input);

        PdfPageContent.AddText(input, 0, 50, 200, "Recorte", Arial, input);

        var word = Find(input, "Recorte");
        Assert.InRange(word.X, 48, 53);
        Assert.InRange(word.Y, 197, 206);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(270)]
    public void AddText_OnLandscapePage_KeepsSizeAndPosition(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2, landscapeEvery: 2);
        if (rotation != 0) PdfOperations.Reorganize(input, [new(0, 0), new(1, rotation)], input);
        var before = TestPdfs.VisualSizes(input)[1];

        PdfPageContent.AddText(input, 1, rotation == 0 ? 600 : 300, 100, "Deitada", Arial, input);

        Assert.Equal(before, TestPdfs.VisualSizes(input)[1]);
        var word = Assert.Single(PdfTextLayout.Words(input, 1), w => w.Text == "Deitada");
        Assert.InRange(word.X, rotation == 0 ? 598 : 298, rotation == 0 ? 603 : 303);
        Assert.InRange(word.Y, 97, 106);
    }

    [Fact]
    public void AddText_EachTypedLineBreak_IsANewLine()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        var style = Arial with { Bold = true, Italic = true };

        PdfPageContent.AddText(input, 0, 80, 300, "Primeira\r\nSegunda", style, input);

        var first = Find(input, "Primeira");
        var second = Find(input, "Segunda");
        Assert.NotEqual(first.Line, second.Line);
        Assert.InRange(second.Y - first.Y, 18, 30);
        Assert.InRange(second.X, 78, 83);
        var (width, height) = PdfPageContent.MeasureText("Primeira\nSegunda", style);
        Assert.True(width > 50 && height > 40);
    }

    [Fact]
    public void AddText_RejectsEmptyTextAndUnknownFont()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string output = Path.Combine(dir, "saida.pdf");

        Assert.Throws<ArgumentException>(() => PdfPageContent.AddText(input, 0, 10, 10, "  ", Arial, output));
        Assert.Throws<InvalidOperationException>(() =>
            PdfPageContent.AddText(input, 0, 10, 10, "Oi", Arial with { FontFamily = "Fonte Que Nao Existe 123" }, output));
        Assert.True(PdfPageContent.CanUseFont("Arial"));
        Assert.False(PdfPageContent.CanUseFont("Fonte Que Nao Existe 123"));
    }

    [Fact]
    public void SystemFonts_ListsWindowsFonts_AndTheyCanBeEmbedded()
    {
        var installed = SystemFonts.Installed();
        Assert.Contains("Arial", installed);
        Assert.DoesNotContain(installed, n => n.StartsWith('@'));

        Assert.DoesNotContain(installed, n => n.Length >= 31);
        // Fontes que o PDFsharp sozinho não conhece (pedidas ao Windows), inclusive de coleções .ttc.
        foreach (var name in new[] { "Times New Roman", "Calibri", "Cambria", "Segoe UI" }.Where(installed.Contains))
            Assert.True(PdfPageContent.CanUseFont(name), name);
    }
}
