using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 4: geometria tela ↔ PDF, cópia de trabalho e comentários.</summary>
public class AnnotationTests
{
    private static readonly AnnotationStyle Red = new(new AnnotationColor(255, 0, 0), LineWidth: 3);

    // ---------------- Geometria ----------------

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Geometry_RoundTrips_AndKeepsCornersInsideThePage(int rotation)
    {
        var g = new PageGeometry(10, 20, 610, 820, rotation); // 600 x 800, origem deslocada (recorte)
        bool turned = rotation is 90 or 270;
        Assert.Equal(turned ? 800 : 600, g.ViewWidth);
        Assert.Equal(turned ? 600 : 800, g.ViewHeight);

        var p = g.ViewToPdf(50, 70);
        var back = g.PdfToView(p.X, p.Y);
        Assert.Equal(50, back.X, 6);
        Assert.Equal(70, back.Y, 6);

        // Os quatro cantos da tela caem nos cantos da área visível do PDF.
        foreach (var (vx, vy) in new[] { (0.0, 0.0), (g.ViewWidth, 0.0), (0.0, g.ViewHeight), (g.ViewWidth, g.ViewHeight) })
        {
            var c = g.ViewToPdf(vx, vy);
            Assert.True(c.X is 10 or 610, $"x={c.X}");
            Assert.True(c.Y is 20 or 820, $"y={c.Y}");
        }
    }

    [Fact]
    public void Geometry_TopLeftOfScreen_MatchesPdfCornerForEachRotation()
    {
        // Página 600 x 800 sem deslocamento. O canto superior esquerdo da tela corresponde a:
        Assert.Equal((0, 800), Pt(new PageGeometry(0, 0, 600, 800, 0).ViewToPdf(0, 0)));     // topo-esquerda
        Assert.Equal((0, 0), Pt(new PageGeometry(0, 0, 600, 800, 90).ViewToPdf(0, 0)));      // girada 90°: base-esquerda
        Assert.Equal((600, 0), Pt(new PageGeometry(0, 0, 600, 800, 180).ViewToPdf(0, 0)));   // de cabeça para baixo
        Assert.Equal((600, 800), Pt(new PageGeometry(0, 0, 600, 800, 270).ViewToPdf(0, 0))); // girada 270°
    }

    private static (double, double) Pt(PdfSharp.Drawing.XPoint p) => (Math.Round(p.X, 6), Math.Round(p.Y, 6));

    // ---------------- Cópia de trabalho ----------------

    [Fact]
    public void WorkingCopy_KeepsOriginalUntilSave_AndUndoRedoWork()
    {
        string dir = TestPdfs.TempDir();
        string original = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        using var work = new WorkingCopy(original, Path.Combine(dir, "work"));
        Assert.False(work.IsDirty);
        Assert.False(work.CanUndo);

        work.Apply((i, o) => PdfOperations.DeletePages(i, "1", o));
        work.Apply((i, o) => PdfOperations.DeletePages(i, "1", o));
        Assert.True(work.IsDirty);
        Assert.Equal(["Página 3"], TestPdfs.Labels(work.CurrentPath));
        Assert.Equal(3, PdfOperations.GetPageCount(original)); // original intacto

        work.Undo();
        Assert.Equal(["Página 2", "Página 3"], TestPdfs.Labels(work.CurrentPath));
        Assert.True(work.CanRedo);
        work.Redo();
        Assert.Equal(["Página 3"], TestPdfs.Labels(work.CurrentPath));

        work.Save();
        Assert.False(work.IsDirty);
        Assert.Equal(["Página 3"], TestPdfs.Labels(original));

        work.Undo();
        Assert.True(work.IsDirty); // voltou para uma versão diferente da salva
    }

    [Fact]
    public void WorkingCopy_NewEditDropsRedo_AndFailedEditChangesNothing()
    {
        string dir = TestPdfs.TempDir();
        string original = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        using var work = new WorkingCopy(original, Path.Combine(dir, "work"));

        work.Apply((i, o) => PdfOperations.DeletePages(i, "1", o));
        work.Undo();
        work.Apply((i, o) => PdfOperations.DeletePages(i, "3", o));
        Assert.False(work.CanRedo);
        Assert.Equal(["Página 1", "Página 2"], TestPdfs.Labels(work.CurrentPath));

        string before = work.CurrentPath;
        Assert.Throws<FormatException>(() => work.Apply((i, o) => PdfOperations.DeletePages(i, "99", o)));
        Assert.Equal(before, work.CurrentPath);
    }

    [Fact]
    public void WorkingCopy_SaveAs_ChangesOriginal_AndDisposeDeletesTempFiles()
    {
        string dir = TestPdfs.TempDir();
        string original = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string copy = Path.Combine(dir, "copia.pdf");
        var work = new WorkingCopy(original, Path.Combine(dir, "work"));
        work.Apply((i, o) => PdfOperations.DeletePages(i, "2", o));

        work.SaveAs(copy);

        Assert.Equal(copy, work.OriginalPath);
        Assert.Equal(1, PdfOperations.GetPageCount(copy));
        Assert.Equal(2, PdfOperations.GetPageCount(original));
        work.Dispose();
        Assert.Empty(Directory.GetDirectories(Path.Combine(dir, "work")));
    }

    // ---------------- Comentários: retângulo ----------------

    [Fact]
    public void AddShape_Rectangle_IsListedWithScreenPositionAndData()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string output = Path.Combine(dir, "out.pdf");

        PdfAnnotations.AddShape(input, 1, AnnotationKind.Rectangle, 100, 150, 200, 80, Red, "Ana Souza", output, "Revisar este trecho");

        var info = Assert.Single(PdfAnnotations.List(output));
        Assert.Equal(1, info.PageIndex);
        Assert.Equal(AnnotationKind.Rectangle, info.Kind);
        Assert.Equal((100, 150, 200, 80), (Math.Round(info.X, 3), Math.Round(info.Y, 3), Math.Round(info.Width, 3), Math.Round(info.Height, 3)));
        Assert.Equal("Revisar este trecho", info.Contents);
        Assert.Equal("Ana Souza", info.Author);
        Assert.Equal(new AnnotationColor(255, 0, 0), info.Color);
        Assert.False(info.Hidden);
        Assert.NotNull(info.Modified);
        Assert.Equal(["Página 1", "Página 2"], TestPdfs.Labels(output)); // conteúdo intacto
    }

    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void AddShape_OnRotatedPage_KeepsScreenPosition(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfOperations.Reorganize(input, [new(0, rotation)], input);

        PdfAnnotations.AddShape(input, 0, AnnotationKind.Oval, 30, 40, 300, 100, Red, "", input);

        var info = Assert.Single(PdfAnnotations.List(input));
        Assert.Equal(AnnotationKind.Oval, info.Kind);
        Assert.Equal((30, 40, 300, 100), (Math.Round(info.X, 3), Math.Round(info.Y, 3), Math.Round(info.Width, 3), Math.Round(info.Height, 3)));
    }

    [Fact]
    public void Delete_RemovesOnlyTheChosenComment()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfAnnotations.AddShape(input, 0, AnnotationKind.Rectangle, 10, 10, 50, 50, Red, "", input, "primeiro");
        PdfAnnotations.AddShape(input, 0, AnnotationKind.Rectangle, 100, 100, 50, 50, Red, "", input, "segundo");
        TestPdfs.AddLink(input, 300, 300, 400, 350); // links não são comentários

        var before = PdfAnnotations.List(input);
        Assert.Equal(["primeiro", "segundo"], before.Select(a => a.Contents));

        PdfAnnotations.Delete(input, 0, before[0].Index, input);

        Assert.Equal(["segundo"], PdfAnnotations.List(input).Select(a => a.Contents));
        Assert.Equal(["/Square", "/Link"], TestPdfs.AnnotationSubtypes(input)); // o link continua lá
    }
}
