using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 3: excluir, extrair, inserir, recortar e redimensionar páginas.</summary>
public class PageToolsTests
{
    // ---------------- Excluir ----------------

    [Fact]
    public void DeletePages_RemovesTheChosenPages()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 6);
        string output = Path.Combine(dir, "out.pdf");

        int left = PdfOperations.DeletePages(input, "2, 4-5", output);

        Assert.Equal(3, left);
        Assert.Equal(["Página 1", "Página 3", "Página 6"], TestPdfs.Labels(output));
    }

    [Fact]
    public void DeletePages_IgnoresRepeatedPages()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        string output = Path.Combine(dir, "out.pdf");

        PdfOperations.DeletePages(input, "1, 1-2, 2", output);

        Assert.Equal(["Página 3"], TestPdfs.Labels(output));
    }

    [Fact]
    public void DeletePages_CanOverwriteInputAndKeepsRotation()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        PdfOperations.Reorganize(input, [new(0), new(1, 90), new(2)], input);

        PdfOperations.DeletePages(input, "1", input);

        Assert.Equal(["Página 2", "Página 3"], TestPdfs.Labels(input));
        Assert.Equal([90, 0], TestPdfs.Rotations(input));
    }

    [Fact]
    public void DeletePages_RefusesToDeleteEverything()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);

        Assert.Throws<InvalidOperationException>(() => PdfOperations.DeletePages(input, "1-3", Path.Combine(dir, "x.pdf")));
        Assert.Throws<FormatException>(() => PdfOperations.DeletePages(input, "7", Path.Combine(dir, "x.pdf")));
    }
}
