using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Mover e redimensionar comentários já feitos.</summary>
public class AnnotationMoveTests
{
    private static readonly AnnotationStyle Red = new(new AnnotationColor(255, 0, 0), LineWidth: 3);

    private static (double, double, double, double) Box(AnnotationInfo a) =>
        (Math.Round(a.X, 2), Math.Round(a.Y, 2), Math.Round(a.Width, 2), Math.Round(a.Height, 2));

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void Rectangle_MovesAndResizes_KeepingColorAndData(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        if (rotation != 0) PdfOperations.Reorganize(input, [new(0, rotation)], input);
        PdfAnnotations.AddShape(input, 0, AnnotationKind.Rectangle, 100, 150, 200, 80, Red, "Ana", input, "Revisar");
        var before = Assert.Single(PdfAnnotations.List(input));

        PdfAnnotations.MoveResize(input, 0, before.Index, 40, 60, 120, 50, input);

        var after = Assert.Single(PdfAnnotations.List(input));
        Assert.Equal((40, 60, 120, 50), Box(after));
        Assert.Equal((before.Contents, before.Author, before.Color, before.Id), (after.Contents, after.Author, after.Color, after.Id));
        Assert.Equal(["Página 1"], TestPdfs.Labels(input)); // a página não muda
    }

    [Fact]
    public void Line_EndpointsFollowTheBox()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        var size = TestPdfs.VisualSizes(input)[0];
        PdfAnnotations.AddLine(input, 0, 100, 100, 300, 200, arrow: true, Red, "", input);
        var before = Assert.Single(PdfAnnotations.List(input));

        // Mesma caixa, 50 pontos para a direita e 20 para baixo.
        PdfAnnotations.MoveResize(input, 0, 0, before.X + 50, before.Y + 20, before.Width, before.Height, input);

        Assert.Equal([150, size.Height - 120, 350, size.Height - 220], TestPdfs.AnnotationArray(input, 0, "/L").Select(v => Math.Round(v, 2)));
        Assert.Equal("/OpenArrow", TestPdfs.AnnotationNames(input, 0, "/LE")[1]); // continua seta
    }

    [Fact]
    public void Ink_StrokesStretchWithTheBox()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfAnnotations.AddInk(input, 0, [[(100, 100), (200, 200)]], Red, "", input);
        var before = Assert.Single(PdfAnnotations.List(input));

        PdfAnnotations.MoveResize(input, 0, 0, before.X, before.Y, before.Width * 2, before.Height, input);

        var after = Assert.Single(PdfAnnotations.List(input));
        Assert.True(after.Width > before.Width * 1.9, $"{after.Width} x {before.Width}");
        Assert.Equal([4], TestPdfs.InkStrokeLengths(input)); // os mesmos 2 pontos
    }

    [Fact]
    public void TextBox_IsRedrawn_AndGrowsWhenNarrower()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfAnnotations.AddTextBox(input, 0, 50, 60, 300, 20, "Um texto que vai precisar de mais linhas", 12, Red with { LineWidth = 0 }, "", input);
        var before = Assert.Single(PdfAnnotations.List(input));

        PdfAnnotations.MoveResize(input, 0, 0, 80, 90, 80, before.Height, input);

        var after = Assert.Single(PdfAnnotations.List(input));
        Assert.Equal((80, 90, 80), (Math.Round(after.X, 2), Math.Round(after.Y, 2), Math.Round(after.Width, 2)));
        Assert.True(after.Height > before.Height); // o texto não cabia: a caixa cresceu
        Assert.Equal(before.Contents, after.Contents);
    }

    [Fact]
    public void Note_OnlyMoves_AndStaysInsideThePage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        var size = TestPdfs.VisualSizes(input)[0];
        PdfAnnotations.AddNote(input, 0, 10, 10, "olá", new AnnotationColor(255, 220, 0), "", input);

        PdfAnnotations.MoveResize(input, 0, 0, 5000, -40, 300, 300, input);

        var after = Assert.Single(PdfAnnotations.List(input));
        double s = PdfAnnotations.NoteIconSize;
        Assert.Equal((Math.Round(size.Width - s, 2), 0, s, s), Box(after));
    }

    [Fact]
    public void Highlight_CannotBeMoved()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfAnnotations.AddTextMarkup(input, 0, AnnotationKind.Highlight, [(50, 50, 100, 20)], new AnnotationStyle(new AnnotationColor(255, 255, 0)), "", input);

        Assert.False(PdfAnnotations.CanMove(AnnotationKind.Highlight));
        Assert.False(PdfAnnotations.CanResize(AnnotationKind.Note));
        Assert.Throws<InvalidOperationException>(() => PdfAnnotations.MoveResize(input, 0, 0, 10, 10, 50, 50, input));
    }
}
