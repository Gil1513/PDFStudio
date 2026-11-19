using System.IO.Compression;
using System.Xml.Linq;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.8: PDF → Word (.docx).</summary>
public class PdfToWordTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>Abre o .docx e devolve o document.xml (falha se algum XML do pacote estiver malformado).</summary>
    private static XDocument Document(string docx, out ZipArchive zip)
    {
        zip = ZipFile.OpenRead(docx);
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".rels")))
            using (var s = entry.Open()) XDocument.Load(s); // todos os XML precisam abrir sem erro
        using var stream = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(stream);
    }

    private static List<string> Paragraphs(XDocument doc) =>
        doc.Descendants(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value)))
           .Where(t => t.Length > 0).ToList();

    [Fact]
    public void Text_KeepsOrder_Font_Size_Bold_AndColor()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        string output = Path.Combine(dir, "doc.docx");

        var result = PdfToWord.Convert(input, output);

        var doc = Document(output, out var zip);
        using (zip)
        {
            var texts = Paragraphs(doc).Where(t => t is not ("topo" or "1")).ToList();
            Assert.Equal(["Relatório anual", "Primeira linha do texto segunda linha do texto terceira linha", "Coluna esquerda", "Coluna direita"], texts);
            Assert.Equal(1, result.Pages);

            var title = doc.Descendants(W + "r").First(r => r.Value == "Relatório anual").Element(W + "rPr")!;
            Assert.Equal("Arial", (string?)title.Element(W + "rFonts")?.Attribute(W + "ascii"));
            Assert.NotNull(title.Element(W + "b"));
            Assert.Equal("40", (string?)title.Element(W + "sz")?.Attribute(W + "val")); // 20 pt = 40 meios-pontos
            Assert.Equal("143CC8", (string?)title.Element(W + "color")?.Attribute(W + "val")); // o azul do exemplo (20, 60, 200)

            var body = doc.Descendants(W + "r").First(r => r.Value.StartsWith("Primeira")).Element(W + "rPr")!;
            Assert.Equal("Times New Roman", (string?)body.Element(W + "rFonts")?.Attribute(W + "ascii"));
            Assert.Null(body.Element(W + "b"));
        }
    }

    [Fact]
    public void Image_GoesIn_WithTheSameSize()
    {
        string dir = TestPdfs.TempDir();
        string output = Path.Combine(dir, "doc.docx");

        var result = PdfToWord.Convert(ContentBlockTests.Sample(dir), output);

        Assert.Equal(1, result.Images);
        var doc = Document(output, out var zip);
        using (zip)
        {
            var media = Assert.Single(zip.Entries, e => e.FullName.StartsWith("word/media/"));
            using (var s = media.Open()) { var head = new byte[4]; s.ReadExactly(head); Assert.Equal([137, 80, 78, 71], head); } // é um PNG
            var extent = doc.Descendants().Single(e => e.Name.LocalName == "extent");
            Assert.InRange((long)extent.Attribute("cx")!, 160 * 12700 - 1000, 160 * 12700 + 1000); // 160 x 80 pontos, como no PDF
            Assert.InRange((long)extent.Attribute("cy")!, 80 * 12700 - 1000, 80 * 12700 + 1000);

            // O pacote liga a imagem ao texto: o r:embed aponta para uma relação que existe.
            using var rels = zip.GetEntry("word/_rels/document.xml.rels")!.Open();
            string embed = doc.Descendants().Single(e => e.Name.LocalName == "blip").Attributes().First(a => a.Name.LocalName == "embed").Value;
            Assert.Contains(XDocument.Load(rels).Root!.Elements(), r => (string?)r.Attribute("Id") == embed && ((string)r.Attribute("Target")!).EndsWith(media.Name));
        }
    }

    [Fact]
    public void EveryPage_StartsOnANewPage_AndPageSizeIsKept()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "tres.pdf"), 3);
        string output = Path.Combine(dir, "tres.docx");

        var result = PdfToWord.Convert(input, output);

        Assert.Equal(3, result.Pages);
        var doc = Document(output, out var zip);
        using (zip)
        {
            Assert.Equal(2, doc.Descendants(W + "br").Count(b => (string?)b.Attribute(W + "type") == "page"));
            var size = doc.Descendants(W + "pgSz").Single();
            using var pig = UglyToad.PdfPig.PdfDocument.Open(input);
            Assert.Equal(pig.GetPage(1).Width * 20, (double)size.Attribute(W + "w")!, 2.0);
            Assert.Equal(pig.GetPage(1).Height * 20, (double)size.Attribute(W + "h")!, 2.0);
        }
    }

    [Fact]
    public void ScannedPage_BecomesAPicture()
    {
        string dir = TestPdfs.TempDir();
        string png = TestImages.Png(Path.Combine(dir, "folha.png"), 300, 400);
        string input = Path.Combine(dir, "escaneado.pdf");
        ImagesToPdf.Create([png], ImagePageSize.A4, ImageMargin.None, input);
        string output = Path.Combine(dir, "escaneado.docx");

        var result = PdfToWord.Convert(input, output);

        Assert.Equal((1, 0, 1), (result.Pages, result.Paragraphs, result.Images));
        var doc = Document(output, out var zip);
        using (zip)
        {
            // A página inteira não cabe dentro das margens: a imagem é reduzida, mas sem distorcer.
            var extent = doc.Descendants().Single(e => e.Name.LocalName == "extent");
            double cx = (long)extent.Attribute("cx")!, cy = (long)extent.Attribute("cy")!;
            Assert.Equal(300.0 / 400, cx / cy, 2);
        }
    }

    [Theory]
    [InlineData(new[] { "uma pala-", "vra quebrada" }, "uma palavra quebrada")]
    [InlineData(new[] { "Rio-", "Grande" }, "Rio- Grande")]           // maiúscula: o hífen é de verdade
    [InlineData(new[] { "  primeira ", "", "segunda" }, "primeira segunda")]
    public void Lines_AreJoined_IntoOneParagraph(string[] lines, string expected) =>
        Assert.Equal(expected, PdfToWord.JoinLines(lines));

    [Fact]
    public void SpecialCharacters_DoNotBreakTheFile()
    {
        Assert.Equal("a &amp; b &lt;c&gt; &quot;d&quot;", PdfToWord.Escape("a & b <c> \"d\""));
        Assert.Equal("ab", PdfToWord.Escape("a\u0001b"));   // caractere de controle que o XML não aceita
        Assert.Equal("😀", PdfToWord.Escape("😀"));          // emoji (par de surrogates) fica
    }
}
