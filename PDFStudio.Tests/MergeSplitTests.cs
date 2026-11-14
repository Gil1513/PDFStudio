using PDFStudio.Core;

namespace PDFStudio.Tests;

public class MergeSplitTests
{
    [Fact]
    public void Merge_KeepsAllPagesInOrder()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 3, "A");
        string b = TestPdfs.Create(Path.Combine(dir, "b.pdf"), 2, "B");
        string output = Path.Combine(dir, "juntado.pdf");

        int pages = PdfOperations.Merge([b, a], output);

        Assert.Equal(5, pages);
        Assert.Equal(["B 1", "B 2", "A 1", "A 2", "A 3"], TestPdfs.Labels(output));
    }

    [Fact]
    public void Merge_CanOverwriteOneOfTheInputs()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 2, "A");
        string b = TestPdfs.Create(Path.Combine(dir, "b.pdf"), 1, "B");

        PdfOperations.Merge([a, b], a);

        Assert.Equal(["A 1", "A 2", "B 1"], TestPdfs.Labels(a));
        Assert.False(File.Exists(a + ".tmp"));
    }

    [Fact]
    public void SplitEveryPage_CreatesOneFilePerPage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 12);
        string outDir = Path.Combine(dir, "saida");

        var files = PdfOperations.SplitEveryPage(input, outDir);

        Assert.Equal(12, files.Count);
        Assert.Equal("doc_pagina_01.pdf", Path.GetFileName(files[0]));
        Assert.Equal("doc_pagina_12.pdf", Path.GetFileName(files[11]));
        for (int i = 0; i < 12; i++)
            Assert.Equal([$"Página {i + 1}"], TestPdfs.Labels(files[i]));
    }

    [Fact]
    public void SplitByRanges_CreatesOneFilePerRange()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 10);

        var files = PdfOperations.SplitByRanges(input, "1-3, 5, 8-", Path.Combine(dir, "saida"));

        Assert.Equal(3, files.Count);
        Assert.Equal(["Página 1", "Página 2", "Página 3"], TestPdfs.Labels(files[0]));
        Assert.Equal(["Página 5"], TestPdfs.Labels(files[1]));
        Assert.Equal(["Página 8", "Página 9", "Página 10"], TestPdfs.Labels(files[2]));
        Assert.Equal("doc_paginas_1-3.pdf", Path.GetFileName(files[0]));
    }

    [Fact]
    public void Split_DoesNotOverwriteExistingFiles()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string outDir = Path.Combine(dir, "saida");

        PdfOperations.SplitEveryPage(input, outDir);
        var second = PdfOperations.SplitEveryPage(input, outDir);

        Assert.Equal("doc_pagina_1 (2).pdf", Path.GetFileName(second[0]));
    }

    [Theory]
    [InlineData("", "Informe")]
    [InlineData("0-2", "fora")]
    [InlineData("4-2", "início é maior")]
    [InlineData("1-20", "fora")]
    [InlineData("abc", "não é um intervalo")]
    public void PageRanges_RejectsInvalidInput(string text, string expectedMessage)
    {
        var ex = Assert.Throws<FormatException>(() => PageRanges.Parse(text, 10));
        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void PageRanges_AcceptsOpenEnds()
    {
        var r = PageRanges.Parse("-2; 9-", 10);
        Assert.Equal([0, 1], r[0]);
        Assert.Equal([8, 9], r[1]);
    }
}
