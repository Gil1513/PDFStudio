using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Editar tudo: levar textos, imagens e desenhos para outra página.</summary>
public class MoveToPageTests
{
    private static List<ContentBlock> Texts(string path, int page) =>
        PdfContentBlocks.Detect(path, page).Where(b => b.Kind == BlockKind.Text).ToList();

    private static string TwoPages(string dir, int rotation = 0)
    {
        string input = ContentBlockTests.Sample(dir, rotation);
        PdfOperations.InsertBlankPages(input, 1, 1, input); // página 2 em branco, do mesmo tamanho
        return input;
    }

    [Fact]
    public void Paragraph_GoesToTheOtherPage_WithTheSameLook()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var old = Assert.Single(Texts(input, 0), b => b.Text.StartsWith("Primeira"));
        string output = Path.Combine(dir, "saida.pdf");

        PdfTextEditor.MoveToPage(input, 0, [old], 1, 10, -50, output);

        Assert.DoesNotContain(Texts(output, 0), b => b.Text.Contains("Primeira"));
        var now = Assert.Single(Texts(output, 1), b => b.Text.StartsWith("Primeira"));
        Assert.Equal(old.Text, now.Text);
        Assert.Equal(old.X + 10, now.X, 1.0);
        Assert.Equal(old.Y - 50, now.Y, 1.0);
        Assert.Equal(old.FontName, now.FontName);
        Assert.Equal(old.FontSize, now.FontSize, 0.1);
        // O resto da página 1 continua lá.
        Assert.Contains(Texts(output, 0), b => b.Text.StartsWith("Relatório"));
        Assert.Contains(PdfContentBlocks.Detect(output, 0), b => b.Kind == BlockKind.Image);
    }

    [Fact]
    public void MovedToOtherPage_ThenMovedAgainAndBack()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var old = Assert.Single(Texts(input, 0), b => b.Text == "Coluna esquerda");

        PdfTextEditor.MoveToPage(input, 0, [old], 1, 0, 0, input);
        var there = Assert.Single(Texts(input, 1), b => b.Text == "Coluna esquerda");
        PdfTextEditor.Move(input, 1, [there], 30, 40, input); // mexe dentro da página nova
        there = Assert.Single(Texts(input, 1), b => b.Text == "Coluna esquerda");
        Assert.Equal(old.X + 30, there.X, 1.0);

        PdfTextEditor.MoveToPage(input, 1, [there], 0, -30, -40, input); // e volta
        Assert.Empty(Texts(input, 1));
        var back = Assert.Single(Texts(input, 0), b => b.Text == "Coluna esquerda");
        Assert.Equal(old.X, back.X, 1.0);
        Assert.Equal(old.Y, back.Y, 1.0);
    }

    [Fact]
    public void ImageAndText_GoTogether()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var image = Assert.Single(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        var text = Assert.Single(Texts(input, 0), b => b.Text.StartsWith("Relatório"));
        string output = Path.Combine(dir, "saida.pdf");

        PdfTextEditor.MoveToPage(input, 0, [image, text], 1, 0, 20, output);

        Assert.DoesNotContain(PdfContentBlocks.Detect(output, 0), b => b.Kind == BlockKind.Image);
        var movedImage = Assert.Single(PdfContentBlocks.Detect(output, 1), b => b.Kind == BlockKind.Image);
        var movedText = Assert.Single(Texts(output, 1), b => b.Text.StartsWith("Relatório"));
        Assert.Equal(image.Y + 20, movedImage.Y, 1.0);
        Assert.Equal(image.Width, movedImage.Width, 0.5);
        Assert.Equal(image.Y - text.Y, movedImage.Y - movedText.Y, 1.0);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    public void RotatedPages_SameRotation_Works(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir, rotation);
        PdfOperations.CopyPages(input, "1", 1, 1, input); // página 2 = cópia (girada igual)
        Assert.Equal([rotation, rotation], TestPdfs.Rotations(input));
        var old = Assert.Single(Texts(input, 0), b => b.Text.StartsWith("Primeira"));
        string output = Path.Combine(dir, "saida.pdf");

        PdfTextEditor.MoveToPage(input, 0, [old], 1, 5, 15, output);

        Assert.DoesNotContain(Texts(output, 0), b => b.Text.StartsWith("Primeira"));
        // Na página 2 (cópia) a palavra aparece duas vezes: a da cópia e a que chegou, deslocada.
        var first = PdfTextLayout.Words(input, 0).First(w => w.Text.StartsWith("Primeira"));
        var there = PdfTextLayout.Words(output, 1).Where(w => w.Text.StartsWith("Primeira")).ToList();
        Assert.Equal(2, there.Count);
        Assert.Contains(there, w => Math.Abs(w.X - (first.X + 5)) < 1 && Math.Abs(w.Y - (first.Y + 15)) < 1);
    }

    [Fact]
    public void DifferentRotation_RefusesAndChangesNothing()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        PdfOperations.RotatePage(input, 1, 90, input);
        var old = Assert.Single(Texts(input, 0), b => b.Text.StartsWith("Primeira"));
        string output = Path.Combine(dir, "saida.pdf");

        Assert.Throws<InvalidOperationException>(() => PdfTextEditor.MoveToPage(input, 0, [old], 1, 0, 0, output));
        Assert.False(File.Exists(output));
    }
}
