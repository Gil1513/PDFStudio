using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Editar tudo: copiar textos, imagens e desenhos e colar em outra página (ou na mesma).</summary>
public class CopyToPageTests
{
    private static List<ContentBlock> Texts(string path, int page) =>
        PdfContentBlocks.Detect(path, page).Where(b => b.Kind == BlockKind.Text && b.Text is not ("topo" or "1")).ToList();

    private static string TwoPages(string dir)
    {
        string input = ContentBlockTests.Sample(dir);
        PdfOperations.InsertBlankPages(input, 1, 1, input);
        return input;
    }

    [Fact]
    public void AllTexts_CopiedToOtherPage_KeepTheLook_AndTheOriginalStays()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var all = Texts(input, 0);
        string output = Path.Combine(dir, "saida.pdf");

        PdfTextEditor.CopyToPage(input, 0, all, 1, 0, 0, output);

        Assert.Equal(all.Select(b => b.Text), Texts(output, 0).Select(b => b.Text)); // o original ficou
        var copies = Texts(output, 1);
        Assert.Equal(all.Count, copies.Count);
        foreach (var (old, now) in all.Zip(copies))
        {
            Assert.Equal(old.Text, now.Text);
            Assert.Equal(old.X, now.X, 1.0);
            Assert.Equal(old.Y, now.Y, 1.0);
            Assert.Equal(old.FontName, now.FontName);
            Assert.Equal(old.FontSize, now.FontSize, 0.1);
            Assert.Equal(old.Color, now.Color);
            Assert.Equal(old.Bold, now.Bold);
        }
    }

    [Fact]
    public void ImageAndText_CopiedTogether()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var image = Assert.Single(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        var text = Assert.Single(Texts(input, 0), b => b.Text.StartsWith("Relatório"));

        PdfTextEditor.CopyToPage(input, 0, [image, text], 1, 10, 30, input);

        Assert.Single(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        var copy = Assert.Single(PdfContentBlocks.Detect(input, 1), b => b.Kind == BlockKind.Image);
        Assert.Equal(image.X + 10, copy.X, 1.0);
        Assert.Equal(image.Y + 30, copy.Y, 1.0);
        Assert.Single(Texts(input, 1), b => b.Text.StartsWith("Relatório"));
    }

    [Fact]
    public void PastedOnTheSamePage_GivesTwo()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var old = Assert.Single(Texts(input, 0), b => b.Text == "Coluna esquerda");

        PdfTextEditor.CopyToPage(input, 0, [old], 0, 0, 200, input);

        var both = Texts(input, 0).Where(b => b.Text == "Coluna esquerda").ToList();
        Assert.Equal(2, both.Count);
        Assert.Contains(both, b => Math.Abs(b.Y - (old.Y + 200)) < 1);
    }

    [Fact]
    public void CopyOfACopy_AndMovingTheCopy_Works()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var old = Assert.Single(Texts(input, 0), b => b.Text == "Coluna direita");

        PdfTextEditor.CopyToPage(input, 0, [old], 1, 0, 0, input);
        var copy = Assert.Single(Texts(input, 1), b => b.Text == "Coluna direita");
        PdfTextEditor.CopyToPage(input, 1, [copy], 1, 0, 100, input);
        Assert.Equal(2, Texts(input, 1).Count(b => b.Text == "Coluna direita"));
        PdfTextEditor.Move(input, 1, [copy], -100, 0, input);
        Assert.Contains(Texts(input, 1), b => b.Text == "Coluna direita" && Math.Abs(b.X - (old.X - 100)) < 1);
        Assert.Single(Texts(input, 0), b => b.Text == "Coluna direita");
    }

    [Fact]
    public void PastedRightBelow_StaysSeparate_AndDeletingItKeepsTheOriginal()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var old = Assert.Single(Texts(input, 0), b => b.Text == "Coluna esquerda");

        // Logo abaixo (como a próxima linha de um parágrafo) e outra por cima, quase no mesmo lugar.
        PdfTextEditor.CopyToPage(input, 0, [old], 0, 0, 13, input);
        PdfTextEditor.CopyToPage(input, 0, [old], 0, 3, 2, input);
        var all = Texts(input, 0).Where(b => b.Text.Contains("Coluna esquerda")).ToList();
        Assert.Equal(3, all.Count);
        Assert.All(all, b => Assert.Equal("Coluna esquerda", b.Text));

        var below = Assert.Single(all, b => Math.Abs(b.Y - (old.Y + 13)) < 1);
        var result = PdfTextEditor.Delete(input, 0, [below], input);
        Assert.False(result.Covered);
        var left = Texts(input, 0).Where(b => b.Text == "Coluna esquerda").ToList();
        Assert.Equal(2, left.Count);
        Assert.Contains(left, b => Math.Abs(b.Y - old.Y) < 1 && Math.Abs(b.X - old.X) < 1);
    }

    [Fact]
    public void CopyOfACopy_CanBeDeletedAndMoved()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var old = Assert.Single(Texts(input, 0), b => b.Text == "Coluna direita");
        PdfTextEditor.CopyToPage(input, 0, [old], 1, 0, 0, input);
        var first = Assert.Single(Texts(input, 1), b => b.Text == "Coluna direita");
        PdfTextEditor.CopyToPage(input, 1, [first], 1, 0, 100, input);
        var second = Assert.Single(Texts(input, 1), b => b.Text == "Coluna direita" && b.Y > first.Y + 50);

        PdfTextEditor.Move(input, 1, [second], 0, 50, input);
        second = Assert.Single(Texts(input, 1), b => b.Text == "Coluna direita" && b.Y > first.Y + 50);
        Assert.Equal(first.Y + 150, second.Y, 1.0);
        Assert.False(PdfTextEditor.Delete(input, 1, [second], input).Covered);
        Assert.Single(Texts(input, 1), b => b.Text == "Coluna direita");
        Assert.Single(Texts(input, 0), b => b.Text == "Coluna direita");
    }

    [Fact]
    public void OriginalGone_RefusesAndChangesNothing()
    {
        string dir = TestPdfs.TempDir();
        string input = TwoPages(dir);
        var old = Assert.Single(Texts(input, 0), b => b.Text == "Coluna esquerda");
        PdfTextEditor.Delete(input, 0, [old], input);
        string output = Path.Combine(dir, "saida.pdf");

        Assert.Throws<InvalidOperationException>(() => PdfTextEditor.CopyToPage(input, 0, [old], 1, 0, 0, output));
        Assert.False(File.Exists(output));
    }
}
