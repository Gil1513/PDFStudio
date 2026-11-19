using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 5.9: trocar um texto que já está escrito na página.</summary>
public class TextEditTests
{
    private static ContentBlock BlockStarting(string path, string start) =>
        Assert.Single(PdfContentBlocks.Detect(path, 0), b => b.Kind == BlockKind.Text && b.Text.StartsWith(start));

    private static string AllText(string path) => string.Join(" ", PdfTextLayout.Words(path, 0).Select(w => w.Text));

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Paragraph_IsReplacedInPlace_WithTheSameStyle_OnEveryRotation(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir, rotation);
        var old = BlockStarting(input, "Primeira");
        string output = Path.Combine(dir, "saida.pdf");

        var result = PdfTextEditor.Replace(input, 0, old, "Texto novo aqui\nsegunda linha nova", output);

        Assert.False(result.Covered); // o texto antigo saiu de verdade
        Assert.Equal("Times New Roman", result.FontUsed);
        string text = AllText(output);
        Assert.DoesNotContain("Primeira", text);
        Assert.DoesNotContain("terceira", text);
        var now = BlockStarting(output, "Texto novo");
        Assert.Equal("Texto novo aqui\nsegunda linha nova", now.Text);
        Assert.Equal(old.Lines[0].Baseline, now.Lines[0].Baseline, 1.0);
        Assert.Equal(old.X, now.X, 1.0);
        Assert.Equal(old.Lines[1].Baseline - old.Lines[0].Baseline, now.Lines[1].Baseline - now.Lines[0].Baseline, 0.5);
        Assert.Equal(old.FontSize, now.FontSize, 0.5);
        Assert.Equal("Times New Roman", now.FontFamily);
        // Os outros blocos continuam lá.
        Assert.Contains("Relatório", text);
        Assert.Contains("Coluna esquerda", text);
        Assert.Contains("Coluna direita", text);
        Assert.Equal(rotation, TestPdfs.Rotations(output)[0]);
    }

    [Fact]
    public void BoldColoredTitle_KeepsColorAndBold()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        var title = BlockStarting(input, "Relatório");

        PdfTextEditor.Replace(input, 0, title, "Relatório 2026", input);

        var now = BlockStarting(input, "Relatório 2026");
        Assert.True(now.Bold);
        Assert.Equal(title.Color, now.Color);
        Assert.Equal(title.Lines[0].Baseline, now.Lines[0].Baseline, 1.0);
        Assert.Contains("Primeira linha do texto", AllText(input)); // o parágrafo de baixo não foi tocado
    }

    [Fact]
    public void TextFromAnotherProgram_IsRemoved_AndEmptyTextOnlyDeletes()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1); // rótulo "Página 1" escrito pelo PDFsharp
        var label = BlockStarting(input, "Página");

        var result = PdfTextEditor.Replace(input, 0, label, "", input);

        Assert.False(result.Covered);
        Assert.DoesNotContain("Página", AllText(input));
        Assert.Contains("topo", AllText(input));
    }

    [Fact]
    public void TextInsideAForm_IsCoveredWithWhite()
    {
        string dir = TestPdfs.TempDir();
        string input = Path.Combine(dir, "form.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var form = new XForm(doc, XUnit.FromPoint(300), XUnit.FromPoint(40));
            using (var fg = XGraphics.FromForm(form))
                fg.DrawString("Dentro do formulario", new XFont("Arial", 14), XBrushes.Black, new XPoint(0, 20));
            gfx.DrawImage(form, 100, 100);
            doc.Save(input);
        }
        var block = BlockStarting(input, "Dentro");

        var result = PdfTextEditor.Replace(input, 0, block, "Por cima", input);

        Assert.True(result.Covered);
        // O texto antigo continua por baixo do branco; as letras, na ordem em que foram escritas, mostram o novo por cima.
        using (var pig = UglyToad.PdfPig.PdfDocument.Open(input))
            Assert.EndsWith("Porcima", string.Concat(pig.GetPage(1).Letters.Select(l => l.Value)).Replace(" ", ""));
        using var check = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        Assert.NotNull(check.Pages[0].Resources.Elements.GetDictionary("/XObject")); // o formulário original continua
    }

    [Theory]
    [InlineData("Arial", "Arial")]
    [InlineData("Helvetica", "Arial")]
    [InlineData("Times", "Times New Roman")]
    [InlineData("CourierStd", "Courier New")]
    [InlineData("Fonte Que Nao Existe", "Arial")]
    public void ChooseFont_UsesInstalledOrSimilar(string original, string expected)
    {
        Assert.Equal(expected, PdfTextEditor.ChooseFont(original));
    }

    [Fact]
    public void ImageBlock_IsRejected()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        var image = Assert.Single(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        Assert.Throws<ArgumentException>(() => PdfTextEditor.Replace(input, 0, image, "x", input));
    }
}
