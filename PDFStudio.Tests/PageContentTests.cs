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

    // ---------------- 5.2 Imagem ----------------

    /// <summary>Retângulos das imagens como a página aparece: canto sup. esq. e sup. dir. da imagem (tela, pontos).</summary>
    private static List<(double LeftX, double TopY, double RightX, double RightY, double W, double H)> Images(string path, int page = 0)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        var p = pig.GetPage(page + 1);
        return p.GetImages().Select(i => i.BoundingBox).Select(b =>
            (b.TopLeft.X, p.Height - b.TopLeft.Y, b.TopRight.X, p.Height - b.TopRight.Y, b.Width, b.Height)).ToList();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void AddImage_FillsTheScreenRectangle_UprightOnEveryRotation(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        if (rotation != 0) PdfOperations.Reorganize(input, [new(0, rotation)], input);
        string png = TestImages.Png(Path.Combine(dir, "foto.png"), 40, 20);

        PdfPageContent.AddImage(input, 0, 100, 150, 200, 100, png, input);

        var img = Assert.Single(Images(input));
        // O canto de cima à esquerda da imagem está onde foi pedido e o de cima à direita, 200 pt à direita:
        // ou seja, a imagem está de pé na página como ela aparece.
        Assert.Equal((100, 150, 300, 150), (Math.Round(img.LeftX), Math.Round(img.TopY), Math.Round(img.RightX), Math.Round(img.RightY)));
        Assert.Equal((200, 100), (Math.Round(img.W), Math.Round(img.H)));
        Assert.Equal(rotation, TestPdfs.Rotations(input)[0]);
        Assert.Empty(PdfAnnotations.List(input));
    }

    [Fact]
    public void AddImage_OnCroppedPage_UsesTheVisiblePosition()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfOperations.CropPages(input, null, new PageMargins(40, 0, 0, 30), input);
        string png = TestImages.Png(Path.Combine(dir, "foto.png"), 10, 10);

        PdfPageContent.AddImage(input, 0, 50, 60, 80, 80, png, input);

        var img = Assert.Single(Images(input));
        Assert.Equal((50, 60), (Math.Round(img.LeftX), Math.Round(img.TopY)));
    }

    [Fact]
    public void ImageSize_UsesTheResolutionOfTheFile()
    {
        string dir = TestPdfs.TempDir();
        // 300 x 150 pixels a 150 ppp = 2 x 1 polegadas = 144 x 72 pontos.
        var (w, h) = PdfPageContent.ImageSize(TestImages.Png(Path.Combine(dir, "a.png"), 300, 150, dpi: 150));
        Assert.Equal((144, 72), (Math.Round(w), Math.Round(h)));
    }

    [Fact]
    public void AddImage_RejectsMissingOrUnsupportedFiles()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string gif = Path.Combine(dir, "x.gif");
        File.WriteAllText(gif, "GIF89a");
        string broken = Path.Combine(dir, "x.png");
        File.WriteAllText(broken, "isto não é uma imagem");

        Assert.Throws<FileNotFoundException>(() => PdfPageContent.AddImage(input, 0, 0, 0, 10, 10, Path.Combine(dir, "nao.png"), input));
        Assert.Throws<InvalidOperationException>(() => PdfPageContent.AddImage(input, 0, 0, 0, 10, 10, gif, input));
        Assert.Throws<InvalidOperationException>(() => PdfPageContent.AddImage(input, 0, 0, 0, 10, 10, broken, input));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPageContent.AddImage(input, 0, 0, 0, 0, 10, TestImages.Png(Path.Combine(dir, "ok.png"), 4, 4), input));
    }

    [Fact]
    public void Jpeg_WithoutJfifHeader_GetsOne_SoPdfSharpCanReadIt()
    {
        byte[] bare = [0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x04, 0x01, 0x02, 0xFF, 0xD9];
        var fixedUp = PdfPageContent.WithJfifHeader(bare);
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xE0], fixedUp.Take(4));
        Assert.Equal("JFIF", System.Text.Encoding.ASCII.GetString(fixedUp, 6, 4));
        Assert.Equal(bare.Skip(2), fixedUp.Skip(20)); // o resto da foto continua igual

        byte[] withJfif = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x02, 0xFF, 0xD9];
        Assert.Same(withJfif, PdfPageContent.WithJfifHeader(withJfif));

        // Foto real do Windows sem cabeçalho JFIF/Exif (o leitor do PDFsharp sozinho recusa).
        string wallpaper = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
        if (!File.Exists(wallpaper)) return;
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfPageContent.AddImage(input, 0, 10, 10, 160, 100, wallpaper, input);
        Assert.Single(Images(input));
    }
}
