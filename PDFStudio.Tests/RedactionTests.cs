using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 6.2: redigir (apagar de verdade o que está na área e pôr tarja).</summary>
public class RedactionTests
{
    private static List<TextWord> Words(string path, int page = 0) => PdfTextLayout.Words(path, page);

    private static string AllLetters(string path, int page = 0)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Concat(pig.GetPage(page + 1).Letters.Select(l => l.Value));
    }

    /// <summary>Uma linha "Nome: Maria CPF: 123.456.789-00 fim" e devolve a área que cobre só o CPF.</summary>
    private static (string Path, RedactionArea Cpf, TextWord First) Sample(string dir, PdfFontEncoding encoding, int rotation = 0)
    {
        string path = Path.Combine(dir, $"doc-{encoding}-{rotation}.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            if (rotation == 0)
            {
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 14, XFontStyleEx.Regular, new XPdfFontOptions(encoding));
                gfx.DrawString("Nome: Maria CPF: 123.456.789-00 fim", font, XBrushes.Black, 60, 100);
                gfx.DrawString("Outra linha que fica", font, XBrushes.Black, 60, 140);
            }
            page.Rotate = rotation;
            doc.Save(path);
        }
        if (rotation != 0)
        {
            // Página girada: o texto é escrito "de pé" como a página aparece na tela.
            var style = new PageTextStyle("Arial", 14, new AnnotationColor(0, 0, 0));
            PdfPageContent.AddText(path, 0, 60, 90, "Nome: Maria CPF: 123.456.789-00 fim", style, path);
            PdfPageContent.AddText(path, 0, 60, 130, "Outra linha que fica", style, path);
        }
        var words = Words(path);
        var cpf = words.Single(w => w.Text.StartsWith("123"));
        return (path, new RedactionArea(0, cpf.X - 1, cpf.Y - 1, cpf.Width + 2, cpf.Height + 2), words[0]);
    }

    [Theory]
    [InlineData(PdfFontEncoding.WinAnsi, 0)]
    [InlineData(PdfFontEncoding.Unicode, 0)]
    [InlineData(PdfFontEncoding.WinAnsi, 90)]
    [InlineData(PdfFontEncoding.Unicode, 180)]
    public void LettersInTheArea_LeaveTheFile_AndTheRestStaysInPlace(PdfFontEncoding encoding, int rotation)
    {
        string dir = TestPdfs.TempDir();
        var (input, area, _) = Sample(dir, encoding, rotation);
        var before = Words(input);
        string output = Path.Combine(dir, "redigido.pdf");

        var result = PdfRedaction.Apply(input, [area], output);

        Assert.Equal("123.456.789-00".Length, result.Letters);
        Assert.Equal(0, result.CoveredOnly);
        string letters = AllLetters(output);
        Assert.DoesNotContain("123", letters);
        Assert.DoesNotContain("789", letters);
        var after = Words(output);
        // As palavras de fora continuam no mesmo lugar (a linha não "andou").
        foreach (string text in new[] { "Nome:", "Maria", "CPF:", "fim", "Outra" })
        {
            var a = before.First(w => w.Text == text);
            var b = after.First(w => w.Text == text);
            Assert.Equal(a.X, b.X, 0.5);
            Assert.Equal(a.Y, b.Y, 0.5);
        }
        Assert.Equal(rotation, TestPdfs.Rotations(output)[0]);
    }

    [Fact]
    public void ImageInsideTheArea_IsRemoved_AndAnnotationsToo()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        var image = Assert.Single(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        PdfAnnotations.AddShape(input, 0, AnnotationKind.Rectangle, image.X + 2, image.Y + 2, 10, 10,
            new AnnotationStyle(new AnnotationColor(255, 0, 0), 1), "Teste", input);
        string output = Path.Combine(dir, "sem-imagem.pdf");

        var result = PdfRedaction.Apply(input, [new RedactionArea(0, image.X - 2, image.Y - 2, image.Width + 4, image.Height + 4)], output);

        Assert.Equal(1, result.Images);
        Assert.Equal(1, result.Annotations);
        Assert.DoesNotContain(PdfContentBlocks.Detect(output, 0), b => b.Kind == BlockKind.Image);
        Assert.Empty(TestPdfs.AnnotationSubtypes(output));
    }

    [Fact]
    public void TextInsideAForm_IsOnlyCovered_AndReported()
    {
        string dir = TestPdfs.TempDir();
        string input = Path.Combine(dir, "form.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            var form = new XForm(doc, XUnit.FromPoint(300), XUnit.FromPoint(40));
            using (var fg = XGraphics.FromForm(form))
                fg.DrawString("Segredo", new XFont("Arial", 14), XBrushes.Black, new XPoint(0, 20));
            gfx.DrawImage(form, 100, 100);
            doc.Save(input);
        }
        var word = Words(input).Single();

        var result = PdfRedaction.Apply(input, [new RedactionArea(0, word.X - 2, word.Y - 2, word.Width + 4, word.Height + 4)], input);

        Assert.True(result.CoveredOnly > 0);
        Assert.Equal(0, result.Letters);
    }

    [Fact]
    public void BlackBox_IsDrawnOverTheArea()
    {
        string dir = TestPdfs.TempDir();
        var (input, area, _) = Sample(dir, PdfFontEncoding.WinAnsi);
        PdfRedaction.Apply(input, [area], input);

        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        string content = string.Concat(Enumerable.Range(0, doc.Pages[0].Contents.Elements.Count)
            .Select(i => System.Text.Encoding.Latin1.GetString(doc.Pages[0].Contents.Elements.GetDictionary(i)!.Stream.UnfilteredValue)));
        Assert.Contains(" re", content); // retângulo
        Assert.Contains("f", content);   // preenchido
    }

    [Fact]
    public void NoArea_IsRejected()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Throws<ArgumentException>(() => PdfRedaction.Apply(input, [], input));
    }
}
