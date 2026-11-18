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

    // ---------------- Recortar ----------------

    [Fact]
    public void CropPages_SetsCropBoxOnlyOnChosenPages()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        string output = Path.Combine(dir, "out.pdf");
        var original = TestPdfs.VisualSizes(input)[0];

        PdfOperations.CropPages(input, "2", new PageMargins(Top: 100, Right: 20, Bottom: 50, Left: 10), output);

        var boxes = TestPdfs.CropBoxes(output);
        Assert.Null(boxes[0]);
        Assert.Equal((10, 50, original.Width - 20, original.Height - 100), boxes[1]);
        Assert.Null(boxes[2]);
        Assert.Equal(["Página 1", "Página 2", "Página 3"], TestPdfs.Labels(output)); // o conteúdo continua lá
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void CropPages_MarginsFollowWhatTheUserSees_OnRotatedPages(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfOperations.Reorganize(input, [new(0, rotation)], input);
        var before = TestPdfs.VisualSizes(input)[0];

        // Corta só o topo visível: a página fica 100 pt mais baixa na tela e a largura não muda.
        PdfOperations.CropPages(input, null, new PageMargins(100, 0, 0, 0), input);

        var after = TestPdfs.VisualSizes(input)[0];
        Assert.Equal(before.Width, after.Width, 3);
        Assert.Equal(before.Height - 100, after.Height, 3);
    }

    [Fact]
    public void CropPages_AddsToExistingCrop_AndRemoveCropRestores()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        var original = TestPdfs.VisualSizes(input)[0];

        PdfOperations.CropPages(input, null, new PageMargins(0, 0, 0, 30), input);
        PdfOperations.CropPages(input, null, new PageMargins(0, 0, 0, 30), input);
        Assert.Equal(original.Width - 60, TestPdfs.VisualSizes(input)[0].Width, 3);

        PdfOperations.RemoveCrop(input, null, input);
        Assert.Null(TestPdfs.CropBoxes(input)[0]);
        Assert.Equal(original, TestPdfs.VisualSizes(input)[0]);
    }

    [Fact]
    public void CropPages_RejectsMarginsThatLeaveNothing()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string output = Path.Combine(dir, "out.pdf");

        Assert.Throws<InvalidOperationException>(() => PdfOperations.CropPages(input, null, new PageMargins(410, 0, 410, 0), output)); // A4 tem 842 pt: sobrariam 22 pt
        Assert.Throws<ArgumentException>(() => PdfOperations.CropPages(input, null, new PageMargins(-1, 0, 0, 0), output));
    }

    // ---------------- Redimensionar ----------------

    private static readonly (double W, double H) A5 = (419.53, 595.28);

    [Fact]
    public void ResizePages_ScalesContentToFitNewSize()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string output = Path.Combine(dir, "out.pdf");

        var a4 = TestPdfs.VisualSizes(input)[0];

        PdfOperations.ResizePages(input, "1", A5.W, A5.H, PageOrientationChoice.Auto, scaleContent: true, output);

        var sizes = TestPdfs.VisualSizes(output);
        Assert.Equal(A5.W, sizes[0].Width, 1);
        Assert.Equal(A5.H, sizes[0].Height, 1);
        Assert.Equal(a4, sizes[1]);                              // página 2 não foi escolhida: continua A4
        Assert.Equal(["Página 1", "Página 2"], TestPdfs.Labels(output)); // conteúdo preservado

        var m = TestPdfs.FirstMatrix(output, 0)!;
        Assert.Equal(Math.Min(A5.W / a4.Width, A5.H / a4.Height), m[0], 4); // reduzido para caber inteiro
        Assert.Equal(m[0], m[3], 6);                             // mesma escala nos dois eixos (sem deformar)
    }

    [Fact]
    public void ResizePages_CenterMode_KeepsContentSize()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        var a4 = TestPdfs.VisualSizes(input)[0];

        PdfOperations.ResizePages(input, null, 841.89, 1190.55, PageOrientationChoice.Auto, scaleContent: false, input); // A3

        var m = TestPdfs.FirstMatrix(input, 0)!;
        Assert.Equal(1, m[0], 6);
        Assert.Equal((841.89 - a4.Width) / 2, m[4], 3); // centralizado na horizontal
        Assert.Equal(1190.55, TestPdfs.VisualSizes(input)[0].Height, 1);
    }

    [Theory]
    [InlineData(PageOrientationChoice.Auto, true)]       // a página deitada continua deitada
    [InlineData(PageOrientationChoice.Portrait, false)]
    public void ResizePages_RespectsOrientation_OnRotatedPages(PageOrientationChoice orientation, bool expectLandscape)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfOperations.Reorganize(input, [new(0, 90)], input); // aparece deitada

        PdfOperations.ResizePages(input, null, A5.W, A5.H, orientation, scaleContent: true, input);

        var size = TestPdfs.VisualSizes(input)[0];
        Assert.Equal(expectLandscape, size.Width > size.Height);
        Assert.Equal(A5.H, Math.Max(size.Width, size.Height), 1);
        Assert.Equal([90], TestPdfs.Rotations(input));
    }

    [Fact]
    public void ResizePages_MovesLinksWithTheContent()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        TestPdfs.AddLink(input, 100, 200, 300, 250);

        PdfOperations.ResizePages(input, null, A5.W, A5.H, PageOrientationChoice.Auto, scaleContent: true, input);

        var m = TestPdfs.FirstMatrix(input, 0)!;
        var rect = TestPdfs.FirstAnnotationRect(input);
        Assert.Equal(100 * m[0] + m[4], rect[0], 3);
        Assert.Equal(250 * m[3] + m[5], rect[3], 3);
    }

    [Fact]
    public void ResizePages_RejectsAbsurdSizes()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOperations.ResizePages(input, null, 10, 500, PageOrientationChoice.Auto, true, Path.Combine(dir, "x.pdf")));
    }
}
