using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 5.6: número de página, cabeçalho e rodapé.</summary>
public class HeaderFooterTests
{
    private static readonly AnnotationColor Black = new(0, 0, 0);

    private static List<TextWord> Words(string path, int page) => PdfTextLayout.Words(path, page);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void PageNumber_GoesAtTheBottomCenter_UprightOnEveryRotation(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        if (rotation != 0) PdfOperations.Reorganize(input, [new(0, rotation), new(1, rotation), new(2, rotation)], input);
        var (w, h) = TestPdfs.VisualSizes(input)[1];

        Assert.Equal(3, PdfHeaderFooter.Add(input, new(FooterCenter: "Folha {n} de {total}", Color: Black, FontSize: 12), null, input));

        var words = Words(input, 1);
        var de = Assert.Single(words, x => x.Text == "de" && x.Y > h / 2);
        var footer = words.Where(x => x.Line == de.Line && Math.Abs(x.Y - de.Y) < 3).OrderBy(x => x.X)
            .SkipWhile(x => x.Text != "Folha").ToList(); // (a página de exemplo tem letras soltas giradas)
        Assert.Equal(["Folha", "2", "de", "3"], footer.Select(x => x.Text));
        double left = footer.Min(x => x.X), right = footer.Max(x => x.Right);
        Assert.InRange((left + right) / 2, w / 2 - 3, w / 2 + 3);
        Assert.InRange(de.Y + de.Height, h - 28 - 4, h - 28 + 4); // encostado na margem de baixo
        Assert.Equal(rotation, TestPdfs.Rotations(input)[1]);
    }

    [Fact]
    public void Header_LeftCenterRight_AndDate()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        var (w, _) = TestPdfs.VisualSizes(input)[0];

        PdfHeaderFooter.Add(input, new(HeaderLeft: "Esq", HeaderCenter: "Meio", HeaderRight: "{data}", Color: Black, MarginPt: 20),
            null, input, today: new DateTime(2026, 11, 19));

        var words = Words(input, 0);
        var esq = Assert.Single(words, x => x.Text == "Esq");
        var meio = Assert.Single(words, x => x.Text == "Meio");
        var data = Assert.Single(words, x => x.Text == "19/11/2026");
        Assert.InRange(esq.X, 19, 22);
        Assert.InRange(esq.Y, 18, 26);
        Assert.InRange(meio.X + meio.Width / 2, w / 2 - 3, w / 2 + 3);
        Assert.InRange(data.Right, w - 22, w - 19);
    }

    [Fact]
    public void StartNumber_Ranges_AndReplacing()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 4);

        PdfHeaderFooter.Add(input, new(FooterRight: "fl. {n}", Color: Black), "2-4", input);
        Assert.Equal(3, PdfHeaderFooter.Count(input));
        Assert.DoesNotContain(Words(input, 0), x => x.Text == "fl.");

        // Colocar de novo troca o anterior (não acumula), e o número inicial muda a contagem.
        PdfHeaderFooter.Add(input, new(FooterRight: "fl. {n}", Color: Black, StartNumber: 10), "2-4", input);
        var page2 = Words(input, 1);
        var fl = Assert.Single(page2, x => x.Text == "fl.");
        Assert.Equal("11", page2.Where(x => x.Line == fl.Line && x.X > fl.X).OrderBy(x => x.X).First().Text);
        Assert.Single(Words(input, 3), x => x.Text == "fl.");
    }

    [Fact]
    public void Remove_KeepsTheWatermarkAndTheOriginalText()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        PdfWatermark.Add(input, new WatermarkOptions("SIGILO", "Arial", Black, Angle: 0), null, input);
        PdfHeaderFooter.Add(input, new(HeaderCenter: "Cabeça", FooterCenter: "{n}", Color: Black), null, input);

        Assert.Equal(2, PdfHeaderFooter.Remove(input, null, input));

        Assert.Equal(0, PdfHeaderFooter.Count(input));
        Assert.Equal(2, PdfWatermark.Count(input));
        var words = Words(input, 0);
        Assert.DoesNotContain(words, x => x.Text == "Cabeça");
        Assert.Contains(words, x => x.Text == "SIGILO");
        Assert.Contains(words, x => x.Text == "Página");
    }

    [Fact]
    public void Expand_And_InvalidOptions()
    {
        Assert.Equal("Página 3 de 9 — 01/02/2026", PdfHeaderFooter.Expand(" Página {N} de {TOTAL} — {data} ", 3, 9, "01/02/2026"));
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Throws<ArgumentException>(() => PdfHeaderFooter.Add(input, new(HeaderLeft: "  "), null, input));
        Assert.Throws<FormatException>(() => PdfHeaderFooter.Add(input, new(FooterCenter: "{n}"), "3", input));
    }
}
