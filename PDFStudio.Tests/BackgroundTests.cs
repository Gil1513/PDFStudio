using System.Text;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 5.7: fundo da página (cor ou imagem).</summary>
public class BackgroundTests
{
    private static readonly AnnotationColor Yellow = new(255, 240, 150);

    /// <summary>Os trechos do conteúdo da página, já descompactados, na ordem em que são desenhados.</summary>
    private static List<string> Streams(string path, int pageIndex)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var contents = doc.Pages[pageIndex].Contents.Elements;
        return Enumerable.Range(0, contents.Count)
            .Select(i => Encoding.Latin1.GetString(contents.GetDictionary(i)!.Stream.UnfilteredValue)).ToList();
    }

    private static int BackgroundIndex(string path, int page) =>
        Streams(path, page).FindIndex(s => s.StartsWith("/Artifact <</PDFStudio /Background "));

    [Fact]
    public void Color_IsDrawnFirst_BehindTheOriginalContent()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);

        Assert.Equal(2, PdfBackground.Add(input, new(Yellow), null, input));

        var streams = Streams(input, 0);
        Assert.Equal(0, BackgroundIndex(input, 0));
        Assert.Contains("1 0.941 0.588 rg", streams[0]); // amarelo
        Assert.Contains(" re", streams[0]);               // retângulo da página inteira
        Assert.Contains("Página 1", string.Concat(PdfTextLayout.Words(input, 0).Select(w => w.Text + " ")));
        Assert.Equal(2, PdfBackground.Count(input));
    }

    [Fact]
    public void Image_UsesAnXObject_AndTheVeilMakesItLighter()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string png = TestImages.Png(Path.Combine(dir, "fundo.png"), 40, 20);

        PdfBackground.Add(input, new(ImagePath: png, Opacity: 0.3), null, input);

        string bg = Streams(input, 0)[BackgroundIndex(input, 0)];
        Assert.Contains(" Do", bg);  // desenha a imagem
        Assert.Contains("1 1 1 rg", bg); // véu branco
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var xobjects = doc.Pages[0].Resources.Elements.GetDictionary("/XObject");
        Assert.NotNull(xobjects);
    }

    [Theory]
    [InlineData(BackgroundFit.Stretch, 0, 0, 600, 800)]
    [InlineData(BackgroundFit.Cover, -500, 0, 1600, 800)]  // imagem 2:1 numa página em pé: cobre a altura
    [InlineData(BackgroundFit.Fit, 0, 250, 600, 300)]      // cabe na largura, centralizada
    public void Place_KeepsProportionWhenAsked(BackgroundFit fit, double x, double y, double w, double h)
    {
        var r = PdfBackground.Place(200, 100, 600, 800, fit);
        Assert.Equal((x, y, w, h), (r.X, r.Y, r.Width, r.Height));
    }

    [Fact]
    public void Replacing_Removing_AndLivingWithOtherArtifacts()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        PdfBackground.Add(input, new(Yellow), null, input);
        PdfWatermark.Add(input, new WatermarkOptions("SIGILO", "Arial", new AnnotationColor(0, 0, 0), Angle: 0), null, input);
        PdfBackground.Add(input, new(new AnnotationColor(200, 230, 255)), "2", input); // troca só na página 2

        Assert.Single(Streams(input, 1), s => s.StartsWith("/Artifact <</PDFStudio /Background "));
        Assert.Contains("0.784 0.902 1 rg", Streams(input, 1)[BackgroundIndex(input, 1)]);
        Assert.True(BackgroundIndex(input, 0) < Streams(input, 0).FindIndex(s => s.Contains("/Watermark")));

        Assert.Equal(3, PdfBackground.Count(input));
        Assert.Equal(3, PdfBackground.Remove(input, null, input));
        Assert.Equal(0, PdfBackground.Count(input));
        Assert.Equal(3, PdfWatermark.Count(input));
        Assert.Contains(PdfTextLayout.Words(input, 2), w => w.Text == "SIGILO");
    }

    [Fact]
    public void ManyAddsAndRemoves_KeepEveryArtifactRemovable_AndQBalanced()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        var black = new AnnotationColor(0, 0, 0);
        var behind = new WatermarkOptions("ATRAS", "Arial", black, Angle: 0, Layer: WatermarkLayer.Behind);
        PdfWatermark.Add(input, behind, null, input);              // primeiro trecho da página
        PdfBackground.Add(input, new(Yellow), null, input);         // agora o fundo é o primeiro
        PdfHeaderFooter.Add(input, new(FooterCenter: "{n}", Color: black), null, input); // último trecho
        PdfPageContent.AddText(input, 0, 50, 300, "Depois", new PageTextStyle("Arial", 12, black), input);

        Assert.Equal(1, PdfWatermark.Count(input));
        Assert.Equal(1, PdfBackground.Count(input));
        Assert.Equal(1, PdfHeaderFooter.Count(input));
        PdfWatermark.Remove(input, null, input);
        PdfBackground.Remove(input, null, input);
        PdfHeaderFooter.Remove(input, null, input);

        var all = string.Join(" ", Streams(input, 0));
        Assert.DoesNotContain("/PDFStudio", all);
        var tokens = all.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries); // separa em qualquer espaço
        Assert.Equal(tokens.Count(t => t == "q"), tokens.Count(t => t == "Q"));
        var words = PdfTextLayout.Words(input, 0).Select(w => w.Text).ToList();
        Assert.Contains("Depois", words);
        Assert.Contains("Página", words);
    }

    [Fact]
    public void InvalidOptions()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfBackground.Add(input, new(Yellow, Opacity: 0), null, input));
        Assert.Throws<FileNotFoundException>(() => PdfBackground.Add(input, new(ImagePath: Path.Combine(dir, "nao.png")), null, input));
        Assert.Throws<FormatException>(() => PdfBackground.Add(input, new(Yellow), "0", input));
    }
}
