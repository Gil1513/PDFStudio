using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>
/// Texto encostado embaixo de outro: a última linha do texto de cima começa dentro da borda de cima dele. Mover (ou
/// mover junto com outros) o texto de baixo não pode levar essa linha junto (antes dava "não dá para mover").
/// </summary>
public class TouchingBlocksMoveTests
{
    private static readonly AnnotationColor Black = new(0, 0, 0);

    private static List<ContentBlock> Texts(string path) =>
        PdfContentBlocks.Detect(path, 0).Where(b => b.Kind == BlockKind.Text && b.Text is not ("topo" or "1")).ToList();

    private static string Touching(string dir)
    {
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1, prefix: "");
        PdfPageContent.AddText(input, 0, 60, 100, "Linha um de cima\nLinha dois de cima", new PageTextStyle("Arial", 12, Black), input);
        PdfPageContent.AddText(input, 0, 60, 300, "Mais longe", new PageTextStyle("Arial", 12, Black), input);
        var top = Texts(input).Single(b => b.Text.StartsWith("Linha um"));
        // Letra maior logo abaixo, com a borda de cima passando da linha de base da última linha de cima.
        double y = top.Lines[^1].Baseline - 2;
        PdfPageContent.AddText(input, 0, 60, y, "Data 01/02/2003", new PageTextStyle("Arial", 16, Black), input);
        return input;
    }

    [Fact]
    public void MovingTheLowerText_LeavesTheUpperLine()
    {
        string input = Touching(TestPdfs.TempDir());
        var before = Texts(input);
        var date = before.Single(b => b.Text == "Data 01/02/2003");
        var upper = before.Single(b => b.Text.StartsWith("Linha um"));
        Assert.True(date.Y < upper.Lines[^1].Baseline); // o caso: encostados

        PdfTextEditor.Move(input, 0, [date], 0, 30, input);
        var now = Texts(input);
        Assert.Contains(now, b => b.Text == "Data 01/02/2003" && Math.Abs(b.Y - (date.Y + 30)) < 1);
        Assert.Contains(now, b => b.Text == upper.Text && Math.Abs(b.Y - upper.Y) < 1);
    }

    [Fact]
    public void MovingSeveralTogether_WithTheLowerText()
    {
        string input = Touching(TestPdfs.TempDir());
        var before = Texts(input);
        var chosen = before.Where(b => b.Text is "Data 01/02/2003" or "Mais longe").ToList();
        var upper = before.Single(b => b.Text.StartsWith("Linha um"));

        PdfTextEditor.Move(input, 0, chosen, 20, 40, input);
        var now = Texts(input);
        foreach (var b in chosen)
            Assert.Contains(now, n => n.Text == b.Text && Math.Abs(n.X - (b.X + 20)) < 1 && Math.Abs(n.Y - (b.Y + 40)) < 1);
        Assert.Contains(now, b => b.Text == upper.Text && Math.Abs(b.Y - upper.Y) < 1);
    }
}
