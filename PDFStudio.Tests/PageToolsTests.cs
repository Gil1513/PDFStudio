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

    // ---------------- Extrair ----------------

    [Fact]
    public void ExtractPages_CopiesPagesInTypedOrder_AndKeepsOriginal()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 6);
        string output = Path.Combine(dir, "out.pdf");

        int count = PdfOperations.ExtractPages(input, "5, 2-3, 3", output);

        Assert.Equal(3, count);
        Assert.Equal(["Página 5", "Página 2", "Página 3"], TestPdfs.Labels(output));
        Assert.Equal(6, PdfOperations.GetPageCount(input));
    }

    [Fact]
    public void ExtractPagesSeparately_CreatesOneFilePerPage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 12);
        string folder = Path.Combine(dir, "saida");

        var files = PdfOperations.ExtractPagesSeparately(input, "2, 10-11", folder);

        Assert.Equal(["doc_pagina_02.pdf", "doc_pagina_10.pdf", "doc_pagina_11.pdf"], files.Select(Path.GetFileName));
        Assert.Equal(["Página 10"], TestPdfs.Labels(files[1]));
    }

    // ---------------- Inserir ----------------

    [Theory]
    [InlineData(0, new[] { "", "", "Página 1", "Página 2", "Página 3" })]
    [InlineData(2, new[] { "Página 1", "Página 2", "", "", "Página 3" })]
    [InlineData(3, new[] { "Página 1", "Página 2", "Página 3", "", "" })]
    public void InsertBlankPages_PutsPagesAtPosition(int position, string[] expected)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        string output = Path.Combine(dir, "out.pdf");

        int total = PdfOperations.InsertBlankPages(input, position, 2, output);

        Assert.Equal(5, total);
        Assert.Equal(expected, TestPdfs.Labels(output));
    }

    [Fact]
    public void InsertBlankPages_CopiesNeighborSize_OrUsesChosenSize()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2, landscapeEvery: 2); // pág. 2 deitada
        string same = Path.Combine(dir, "same.pdf"), a5 = Path.Combine(dir, "a5.pdf");

        PdfOperations.InsertBlankPages(input, 2, 1, same);
        PdfOperations.InsertBlankPages(input, 0, 1, a5, PdfSharp.PageSize.A5, landscape: true);

        var sizes = TestPdfs.VisualSizes(same);
        Assert.Equal(sizes[1], sizes[2]);           // igual à vizinha (paisagem)
        Assert.True(sizes[2].Width > sizes[2].Height);
        var first = TestPdfs.VisualSizes(a5)[0];
        Assert.Equal(595, first.Width, 0);          // A5 deitada: 21 x 14,8 cm
        Assert.Equal(420, first.Height, 0);
    }

    [Fact]
    public void InsertPagesFromPdf_InsertsChosenPagesOfOtherFile()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string other = TestPdfs.Create(Path.Combine(dir, "outro.pdf"), 4, prefix: "Outro");

        PdfOperations.InsertPagesFromPdf(input, other, "4, 1", 1, input);

        Assert.Equal(["Página 1", "Outro 4", "Outro 1", "Página 2"], TestPdfs.Labels(input));
    }

    [Fact]
    public void InsertPagesFromPdf_AllPagesWhenNoRange_AndRejectsBadPosition()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string output = Path.Combine(dir, "out.pdf");

        Assert.Equal(4, PdfOperations.InsertPagesFromPdf(input, input, null, 2, output));
        Assert.Equal(["Página 1", "Página 2", "Página 1", "Página 2"], TestPdfs.Labels(output));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfOperations.InsertPagesFromPdf(input, input, null, 3, output));
    }
}
