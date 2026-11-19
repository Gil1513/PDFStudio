using PigDocument = UglyToad.PdfPig.PdfDocument;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 5.5: marca d'água de texto.</summary>
public class WatermarkTests
{
    private static readonly WatermarkOptions Horizontal =
        new("CONFIDENCIAL", "Arial", new AnnotationColor(200, 0, 0), Opacity: 0.4, Angle: 0);

    /// <summary>Letras da página (PdfPig), na ordem em que estão no conteúdo.</summary>
    private static string Letters(string path, int pageIndex)
    {
        using var doc = PigDocument.Open(path);
        return string.Concat(doc.GetPage(pageIndex + 1).Letters.Select(l => l.Value));
    }

    private static int[] Marked(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return Enumerable.Range(0, doc.PageCount)
            .Where(i => PdfPageContent.CountArtifacts(doc.Pages[i], PdfPageContent.ArtifactKind.Watermark) > 0).ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Horizontal_IsCenteredAndUpright_OnEveryRotation(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        if (rotation != 0) PdfOperations.Reorganize(input, [new(0, rotation)], input);
        var (w, h) = TestPdfs.VisualSizes(input)[0];
        string output = Path.Combine(dir, "saida.pdf");

        Assert.Equal(1, PdfWatermark.Add(input, Horizontal, null, output));

        var word = Assert.Single(PdfTextLayout.Words(output, 0), x => x.Text == "CONFIDENCIAL"); // palavra inteira = na horizontal
        Assert.InRange(word.X + word.Width / 2, w / 2 - 3, w / 2 + 3);
        Assert.InRange(word.Y + word.Height / 2, h / 2 - 6, h / 2 + 6);
        Assert.InRange(word.Width, w * 0.7, w * 0.81); // tamanho automático: ~80% da largura
        Assert.Equal(rotation, TestPdfs.Rotations(output)[0]);
        Assert.Empty(PdfAnnotations.List(output));
        Assert.Contains("Página 1", Letters(output, 0)); // o conteúdo original continua lá
    }

    [Fact]
    public void Diagonal_FitsInsideThePage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        var (w, h) = TestPdfs.VisualSizes(input)[0];

        PdfWatermark.Add(input, Horizontal with { Angle = null }, null, input);

        using var doc = PigDocument.Open(input);
        var letters = doc.GetPage(1).Letters.Where(l => "CONFIDENCIAL".Contains(l.Value)).ToList();
        Assert.True(letters.Count >= 12);
        Assert.All(letters, l => Assert.InRange(l.GlyphRectangle.Left, 0, w));
        // Na diagonal: a primeira letra fica embaixo à esquerda da última.
        var first = letters.First(l => l.Value == "C");
        var last = letters.Last(l => l.Value == "L");
        Assert.True(last.StartBaseLine.X > first.StartBaseLine.X + w * 0.3);
        Assert.True(last.StartBaseLine.Y > first.StartBaseLine.Y + h * 0.3); // y do PDF cresce para cima
    }

    [Fact]
    public void OnlyTheChosenPages_GetTheWatermark()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 5);

        Assert.Equal(3, PdfWatermark.Add(input, Horizontal, "1, 3-4", input));

        Assert.Equal([0, 2, 3], Marked(input));
        Assert.Equal(3, PdfWatermark.Count(input));
        Assert.DoesNotContain("CONFIDENCIAL", Letters(input, 1));
        Assert.Contains("CONFIDENCIAL", Letters(input, 2));
    }

    [Fact]
    public void Behind_IsDrawnBeforeTheOriginalContent_Front_After()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string behind = Path.Combine(dir, "atras.pdf"), front = Path.Combine(dir, "frente.pdf");

        PdfWatermark.Add(input, Horizontal with { Layer = WatermarkLayer.Behind }, null, behind);
        PdfWatermark.Add(input, Horizontal, null, front);

        string a = Letters(behind, 0), b = Letters(front, 0);
        Assert.True(a.IndexOf("CONFIDENCIAL") < a.IndexOf("Página"));
        Assert.True(b.IndexOf("CONFIDENCIAL") > b.IndexOf("Página"));
    }

    [Fact]
    public void AddingAgain_ReplacesTheOldWatermark()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);

        PdfWatermark.Add(input, Horizontal, null, input);
        PdfWatermark.Add(input, Horizontal with { Text = "RASCUNHO" }, null, input);

        Assert.DoesNotContain("CONFIDENCIAL", Letters(input, 0));
        Assert.Contains("RASCUNHO", Letters(input, 0));
        Assert.Equal(2, PdfWatermark.Count(input));
    }

    [Fact]
    public void Remove_TakesOnlyTheWatermark_AndKeepsTheRest()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        PdfPageContent.AddText(input, 0, 100, 300, "Texto meu", new PageTextStyle("Arial", 14, new AnnotationColor(0, 0, 0)), input);
        PdfWatermark.Add(input, Horizontal with { Layer = WatermarkLayer.Behind }, null, input);
        PdfWatermark.Add(input, Horizontal with { Text = "CÓPIA" }, "3", input, replaceExisting: false);

        Assert.Equal(3, PdfWatermark.Remove(input, null, input));

        Assert.Equal(0, PdfWatermark.Count(input));
        string text = Letters(input, 0);
        Assert.DoesNotContain("CONFIDENCIAL", text);
        Assert.Contains("Página 1", text);
        Assert.Contains("Texto meu", text);
        Assert.DoesNotContain("CÓPIA", Letters(input, 2));
        Assert.Equal(0, PdfWatermark.Remove(input, null, input)); // nada mais a remover
    }

    [Fact]
    public void FixedSize_AndInvalidOptions()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);

        PdfWatermark.Add(input, Horizontal with { FontSize = 30 }, null, input);
        var word = Assert.Single(PdfTextLayout.Words(input, 0), x => x.Text == "CONFIDENCIAL");
        Assert.InRange(word.Height, 25, 36);

        Assert.Throws<ArgumentException>(() => PdfWatermark.Add(input, Horizontal with { Text = "  " }, null, input));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfWatermark.Add(input, Horizontal with { Opacity = 0 }, null, input));
        Assert.Throws<FormatException>(() => PdfWatermark.Add(input, Horizontal, "9", input));
    }
}
